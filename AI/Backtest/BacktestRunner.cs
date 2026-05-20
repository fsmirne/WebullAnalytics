using Spectre.Console;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Daily-step simulator. Each step (end-of-day, 15:45 ET):
///   1. Settle positions whose short leg has expired on or before today (intrinsic-value cash flow).
///   2. Run <see cref="RuleEvaluator"/> over surviving positions. Each Close / Roll / AlertOnly proposal
///      applies to the simulated book at the day's mark.
///   3. Run <see cref="OpenCandidateEvaluator"/>; the top-N proposals that fit free cash get opened
///      at the day's mark.
/// All fills use the BS-priced mid from <see cref="BacktestQuoteSource"/>. Fees are debited per leg-contract.
/// Phase-1 limitations (noted for follow-up work): no intraday triggering on stop-loss / take-profit,
/// and opens fill same-day instead of at next-day open.
/// </summary>
internal sealed class BacktestRunner
{
	private readonly AIConfig _config;
	private readonly SimulatedBook _book;
	private readonly BacktestPositionSource _positions;
	private readonly BacktestQuoteSource _quotes;
	private readonly HistoricalBarCache _bars;
	private readonly HistoricalPriceCache _closeCache;
	private readonly int _topNPerStep;

	// Conceptual fill times within a trading day. Opens, closes, and rolls all price off bar.Open
	// (BacktestQuoteSource uses the day's open as spot), so they're stamped at 09:30 ET. Expirations
	// settle at the day's close (bar.Close intrinsic), so they're stamped at 16:00 ET. Exposing real
	// times on each fill makes the ledger directly verifiable against historical OHLC bars — pre-fix,
	// every fill said 15:45 ET, which matched neither the open mid nor the close intrinsic actually used.
	private static readonly TimeSpan MarketOpenTime = TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
	private static readonly TimeSpan MarketCloseTime = TimeSpan.FromHours(16);

	public BacktestRunner(AIConfig config, SimulatedBook book, BacktestPositionSource positions, BacktestQuoteSource quotes, HistoricalBarCache bars, HistoricalPriceCache closeCache, int topNPerStep)
	{
		_config = config;
		_book = book;
		_positions = positions;
		_quotes = quotes;
		_bars = bars;
		_closeCache = closeCache;
		_topNPerStep = topNPerStep;
	}

	public async Task<BacktestResult> RunAsync(DateTime since, DateTime until, CancellationToken cancellation)
	{
		var tickerSet = new HashSet<string>(_config.Tickers, StringComparer.OrdinalIgnoreCase);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(_config), _config);
		var openEvaluator = new OpenCandidateEvaluator(_config, _quotes, SuggestionPricing.Mid, _closeCache, backtestMode: true);

		var equityCurve = new List<(DateTime Date, decimal Equity)>();
		var startingCash = _book.Cash;
		var peakEquity = startingCash;
		var maxDrawdown = 0m;
		var steps = EnumerateTradingDays(since, until).ToList();

		foreach (var step in steps)
		{
			cancellation.ThrowIfCancellationRequested();

			// Step 1: management rules. Run BEFORE settlement so CloseBeforeShortExpiryRule (and
			// any rule that checks DTE==0) can fire on the actual expiry day. Otherwise the position
			// is gone by the time rules see it and ITM shorts go straight to assignment.
			var openPositions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			if (openPositions.Count > 0)
			{
				var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
				var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, step, _quotes, _config, cancellation);
				var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);
				var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
				var results = evaluator.Evaluate(ctx);

				foreach (var r in results)
				{
					var p = r.Proposal;
					if (!openPositions.TryGetValue(p.PositionKey, out var pos)) continue;
					if (p.Kind == ProposalKind.Close)
					{
						var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quoteSnapshot.Options);
						if (legFills != null) _book.Close(step, p.PositionKey, legFills, p.Rule);
					}
					else if (p.Kind == ProposalKind.Roll)
					{
						var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quoteSnapshot.Options);
						if (legFills != null) _book.Roll(step, p.PositionKey, legFills, p.Rule);
					}
					// AlertOnly: noop in backtest.
				}
			}

			// Step 2: settle anything that's still in the book and has reached expiry. Anything still
			// here at this point is either (a) the rule engine didn't decide to close it, or (b) the
			// position has only-OTM short legs and is harmless to let expire worthless.
			await SettleExpirationsAsync(step, cancellation);

			// Step 3: opener.
			var postMgmt = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			var (postCash, postAccount) = await _positions.GetAccountStateAsync(step, cancellation);
			var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, step, _quotes, _config, cancellation);
			var postSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);
			var postCtx = new EvaluationContext(step, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
			var openProposals = await openEvaluator.EvaluateAsync(postCtx, cancellation);

			var opened = 0;
			foreach (var p in openProposals)
			{
				if (opened >= _topNPerStep) break;
				if (p.CashReserveBlocked) continue;
				if (p.Qty < 1) continue;
				var requiredCash = p.CapitalAtRiskPerContract * p.Qty;
				if (requiredCash > _book.Cash) continue;

				var legFills = BuildLegFillsFromProposal(p.Legs, p.Qty);
				if (legFills == null) continue;
				if (_book.Open(step, p.Ticker, p.StructureKind, legFills, p.Qty)) opened++;
			}

			// Intraday SL/TP simulation: re-price each open position at the day's bar.High and bar.Low
			// (with mid-session TTE for 0DTE) and fire StopLossRule / TakeProfitRule against both
			// extremes. Without this, 0DTE positions never see their stops or profit targets because
			// the daily-step engine jumps straight from open to expiry. Closes here happen BEFORE
			// settlement so any 0DTE position that would have stopped intraday doesn't double-count
			// at expiration.
			await RunIntradayTriggersAsync(step, evaluator, cancellation);

			// 0DTE: a position opened this step whose short leg expires today must settle today, not
			// roll over to tomorrow and pick up the wrong bar. The second pass is a no-op when none
			// of the opens are 0DTE.
			await SettleExpirationsAsync(step, cancellation);

			// Track equity curve at end-of-step: cash + MTM of open positions.
			var mtm = await ComputeOpenMarkAsync(step, cancellation);
			var equity = _book.Cash + mtm;
			equityCurve.Add((step.Date, equity));
			if (equity > peakEquity) peakEquity = equity;
			else
			{
				var dd = peakEquity - equity;
				if (dd > maxDrawdown) maxDrawdown = dd;
			}
		}

		// Final per-lineage MTM for still-open positions so the renderer can compute unrealized P&L.
		var endMtmByLineage = await ComputeOpenMarkPerLineageAsync(steps.Count > 0 ? steps[^1] : until, cancellation);

		return new BacktestResult(
			StartingCash: startingCash,
			EndingCash: _book.Cash,
			TotalFees: _book.TotalFees,
			OpenFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Open),
			CloseFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Close),
			RollFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Roll),
			ExpireFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Expire),
			MaxDrawdown: maxDrawdown,
			PeakEquity: peakEquity,
			EquityCurve: equityCurve,
			Fills: _book.Fills,
			EndMtmByLineage: endMtmByLineage);
	}

	/// <summary>Per-lineage MTM of still-open positions at the final step. Used by the renderer to split
	/// realized from unrealized P&amp;L. Returns the same numbers <see cref="ComputeOpenMarkAsync"/> would
	/// have summed, but bucketed by lineage so we can attribute each to a lifecycle.</summary>
	private async Task<IReadOnlyDictionary<long, decimal>> ComputeOpenMarkPerLineageAsync(DateTime step, CancellationToken cancellation)
	{
		var byLineage = new Dictionary<long, decimal>();
		if (_book.OpenPositions.Count == 0) return byLineage;

		var symbols = _book.OpenPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var tickers = _book.OpenPositions.Values.Select(p => p.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var snap = await _quotes.GetQuotesAsync(step, symbols, tickers, cancellation);

		// Lineage lookup: each open position's lineage id is the LineageId of the most recent fill on that position's PositionKey.
		var lineageByKey = _book.Fills
			.GroupBy(f => f.PositionKey, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.Last().LineageId, StringComparer.OrdinalIgnoreCase);

		foreach (var pos in _book.OpenPositions.Values)
		{
			if (!lineageByKey.TryGetValue(pos.Key, out var lineage)) continue;
			decimal perContract = 0m;
			var allLegsPriced = true;
			foreach (var leg in pos.Legs)
			{
				if (!snap.Options.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
				{
					allLegsPriced = false;
					break;
				}
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				perContract += leg.Side == Side.Buy ? mid : -mid;
			}
			if (allLegsPriced) byLineage[lineage] = perContract * 100m * pos.Quantity;
		}
		return byLineage;
	}

	/// <summary>For management proposals (close/roll), re-price each leg at the current quote mid.
	/// Returns null if any leg lacks a usable quote.</summary>
	private static IReadOnlyList<BacktestLegFill>? BuildLegFillsFromQuotes(IReadOnlyList<ProposalLeg> legs, int qty, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var fills = new List<BacktestLegFill>(legs.Count);
		foreach (var l in legs)
		{
			if (!quotes.TryGetValue(l.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			var side = string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
			fills.Add(new BacktestLegFill(l.Symbol, side, qty, mid));
		}
		return fills;
	}

	/// <summary>For opener proposals, the leg's <c>PricePerShare</c> is already set by the candidate scorer
	/// using the same quote source. Trust it.</summary>
	private static IReadOnlyList<BacktestLegFill>? BuildLegFillsFromProposal(IReadOnlyList<ProposalLeg> legs, int qty)
	{
		var fills = new List<BacktestLegFill>(legs.Count);
		foreach (var l in legs)
		{
			if (!l.PricePerShare.HasValue) return null;
			var side = string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
			fills.Add(new BacktestLegFill(l.Symbol, side, qty, l.PricePerShare.Value));
		}
		return fills;
	}

	/// <summary>Intraday SL/TP simulation. For each ticker with open positions, re-prices each leg
	/// at bar.Low and bar.High using mid-session TTE for 0DTE (3.25h) and the standard <c>dte/365</c>
	/// for longer positions. Runs the existing StopLossRule and TakeProfitRule (only — roll rules
	/// are excluded as they're EOD-style) against both extremes. On whipsaw days when SL would have
	/// fired at one extreme AND TP at the other, the conservative convention is to honour the SL
	/// first (the rule engine's priority order does this naturally because StopLossRule has Priority=1
	/// and TakeProfitRule Priority=2). Without minute-bar history we can't know which extreme came
	/// first; assuming SL wins is the bear-case approximation.</summary>
	private async Task RunIntradayTriggersAsync(DateTime step, RuleEvaluator evaluator, CancellationToken cancellation)
	{
		if (_book.OpenPositions.Count == 0) return;

		// Take a snapshot — the book gets mutated as we close inside the loop.
		var byTicker = _book.OpenPositions.Values
			.GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);

		foreach (var grp in byTicker)
		{
			var ticker = grp.Key;
			var bar = await _bars.GetBarAsync(ticker, step.Date, cancellation);
			if (bar == null) continue;

			// Two extremes per ticker. Evaluate Low first (StopLoss bear case), then High (TakeProfit
			// bull case) — positions closed at Low don't get a second look at High.
			await EvaluateIntradayExtremeAsync(step, ticker, bar.Low, evaluator, cash, accountValue, cancellation);
			await EvaluateIntradayExtremeAsync(step, ticker, bar.High, evaluator, cash, accountValue, cancellation);
		}
	}

	private async Task EvaluateIntradayExtremeAsync(
		DateTime step, string ticker, decimal spot, RuleEvaluator evaluator,
		decimal cash, decimal accountValue, CancellationToken cancellation)
	{
		var stillOpen = _book.OpenPositions.Values
			.Where(p => string.Equals(p.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
			.ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);
		if (stillOpen.Count == 0) return;

		var symbols = stillOpen.Values
			.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var quotes = await _quotes.GetIntradayQuotesAsync(
			step, ticker, spot, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
		if (quotes.Count == 0) return;

		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spot };
		var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
		var ctx = new EvaluationContext(step, stillOpen, underlyings, quotes, cash, accountValue, emptySignals);

		var results = evaluator.Evaluate(ctx);
		foreach (var r in results)
		{
			var p = r.Proposal;
			if (p.Kind != ProposalKind.Close) continue;
			// Only SL/TP can legitimately fire intraday. Roll rules read multi-day state (DTE buckets,
			// short-strike proximity over a window) and belong in the EOD management pass.
			if (p.Rule != "StopLossRule" && p.Rule != "TakeProfitRule") continue;
			if (!_book.OpenPositions.TryGetValue(p.PositionKey, out var pos)) continue;
			var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quotes);
			if (legFills != null) _book.Close(step, p.PositionKey, legFills, p.Rule);
		}
	}

	private async Task SettleExpirationsAsync(DateTime step, CancellationToken cancellation)
	{
		var expired = _book.OpenPositions.Values
			.Where(p => p.Legs.Any(l => l.Expiry.HasValue && l.Expiry.Value.Date <= step.Date))
			.Select(p => p.Key)
			.ToList();
		// Stamp expirations at 16:00 ET — the bar.Close is what determines intrinsic, so the fill
		// time should match the price source for ledger verification.
		var settleTime = step.Date.Add(MarketCloseTime);
		foreach (var key in expired)
		{
			if (!_book.OpenPositions.TryGetValue(key, out var pos)) continue;
			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar == null) continue;
			_book.Expire(settleTime, key, bar.Close);
		}
	}

	private async Task<decimal> ComputeOpenMarkAsync(DateTime step, CancellationToken cancellation)
	{
		if (_book.OpenPositions.Count == 0) return 0m;
		var symbols = _book.OpenPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var tickers = _book.OpenPositions.Values.Select(p => p.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var snap = await _quotes.GetQuotesAsync(step, symbols, tickers, cancellation);

		decimal total = 0m;
		foreach (var pos in _book.OpenPositions.Values)
		{
			decimal perContract = 0m;
			foreach (var leg in pos.Legs)
			{
				// Prefer the BS-priced mid from the quote source. If a leg can't be priced (e.g. a sparse
				// edge case in the bar cache), fall back to intrinsic value rather than zeroing out the
				// whole position — that would falsely report a big drawdown on a single sparse day.
				decimal legValue;
				if (snap.Options.TryGetValue(leg.Symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue)
				{
					legValue = (q.Bid.Value + q.Ask.Value) / 2m;
				}
				else if (snap.Underlyings.TryGetValue(pos.Ticker, out var spot) && leg.CallPut != null)
				{
					legValue = leg.CallPut == "C" ? Math.Max(0m, spot - leg.Strike) : Math.Max(0m, leg.Strike - spot);
				}
				else
				{
					// No spot, no BS — best we can do is treat the leg at zero. Note that this is the same
					// behavior the old code had for the WHOLE position; here we localize it to one leg so the
					// rest of the position still contributes.
					legValue = 0m;
				}
				perContract += leg.Side == Side.Buy ? legValue : -legValue;
			}
			total += perContract * 100m * pos.Quantity;
		}
		return total;
	}

	private static IEnumerable<DateTime> EnumerateTradingDays(DateTime since, DateTime until)
	{
		var d = since.Date;
		while (d <= until.Date)
		{
			// Skip weekends and NYSE holidays. Pricing the chain on a closed-market day yields zero MTM
			// for every leg (no bar) which would mis-report drawdown as if positions had imploded.
			if (MarketCalendar.IsOpen(d))
				yield return d.Add(MarketOpenTime);
			d = d.AddDays(1);
		}
	}
}

internal sealed record BacktestResult(
	decimal StartingCash,
	decimal EndingCash,
	decimal TotalFees,
	int OpenFills,
	int CloseFills,
	int RollFills,
	int ExpireFills,
	decimal MaxDrawdown,
	decimal PeakEquity,
	IReadOnlyList<(DateTime Date, decimal Equity)> EquityCurve,
	IReadOnlyList<BacktestFill> Fills,
	IReadOnlyDictionary<long, decimal> EndMtmByLineage)
{
	/// <summary>P&amp;L on closed lifecycles only (lineages that ended in Close or Expire). Each lifecycle's
	/// P&amp;L = sum of (NetCashFlow - Fees) across all fills sharing its LineageId.</summary>
	public decimal RealizedPnL => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire))
		.Sum(g => g.Sum(f => f.NetCashFlow - f.Fees));

	/// <summary>Unrealized P&amp;L = per-lineage net cash + per-lineage final MTM, summed across still-open lifecycles.</summary>
	public decimal UnrealizedPnL => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => !g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire))
		.Sum(g => g.Sum(f => f.NetCashFlow - f.Fees) + (EndMtmByLineage.TryGetValue(g.Key, out var m) ? m : 0m));

	public decimal TotalPnL => RealizedPnL + UnrealizedPnL;

	public decimal EndingEquity => StartingCash + TotalPnL;

	/// <summary>Per-lifecycle wins/losses (closed lineages only).</summary>
	public (int wins, int losses) LifecycleWinLoss()
	{
		var closed = Fills
			.GroupBy(f => f.LineageId)
			.Where(g => g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire))
			.Select(g => g.Sum(f => f.NetCashFlow - f.Fees))
			.ToList();
		return (closed.Count(p => p > 0m), closed.Count(p => p <= 0m));
	}
}
