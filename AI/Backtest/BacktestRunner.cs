using Spectre.Console;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;

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
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private readonly AIConfig _config;
	private readonly SimulatedBook _book;
	private readonly BacktestPositionSource _positions;
	private readonly BacktestQuoteSource _quotes;
	private readonly HistoricalBarCache _bars;
	private readonly HistoricalPriceCache _closeCache;
	private readonly int _topNPerStep;
	private readonly IntradayBarCache _intradayBars;
	private readonly bool _oracle;

	// Conceptual fill times within a trading day. Opens, closes, and rolls all price off bar.Open
	// (BacktestQuoteSource uses the day's open as spot), so they're stamped at 09:30 ET. Expirations
	// settle at the day's close (bar.Close intrinsic), so they're stamped at 16:00 ET. Exposing real
	// times on each fill makes the ledger directly verifiable against historical OHLC bars — pre-fix,
	// every fill said 15:45 ET, which matched neither the open mid nor the close intrinsic actually used.
	private static readonly TimeSpan MarketOpenTime = TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
	private static readonly TimeSpan MarketCloseTime = TimeSpan.FromHours(16);

	public BacktestRunner(AIConfig config, SimulatedBook book, BacktestPositionSource positions, BacktestQuoteSource quotes, HistoricalBarCache bars, HistoricalPriceCache closeCache, int topNPerStep, bool oracle = false)
	{
		_config = config;
		_book = book;
		_positions = positions;
		_quotes = quotes;
		_bars = bars;
		_closeCache = closeCache;
		_topNPerStep = topNPerStep;
		_oracle = oracle;
		// Disk-only cache for backtest: the fetcher returns empty so any missing minute file fails
		// closed (we fall back to the single 09:30 fill path). The on-disk read path serves the
		// backfilled data/intraday/<TICKER>/<date>.csv files that the Polygon-mirror import created.
		_intradayBars = new IntradayBarCache(NoopIntradayFetcher);
	}

	private static Task<IReadOnlyList<MinuteBar>> NoopIntradayFetcher(string ticker, BarInterval interval, int count, bool includeExtended, CancellationToken cancellation)
		=> Task.FromResult<IReadOnlyList<MinuteBar>>(Array.Empty<MinuteBar>());

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

			// Step 3: opener — scan intraday minute by minute. Previously fired once at 09:30 ET
			// because there was no minute data to anchor any other moment; with backfilled
			// data/intraday/<TICKER>/<date>.csv we can now re-evaluate at each minute and let the
			// existing score gate (cfg.MinScoreToOpen) decide when conditions warrant a fill. Top-N
			// per step caps the number of opens (typically 1 for SPXW); first proposal that passes
			// score + cash + qty wins the day. If no minute crosses, the day skips.
			var openedAtMinute = await TryOpenAcrossDayAsync(step, tickerSet, openEvaluator, cancellation);
			// Fallback: if there is no intraday data for this day (pre-2025 dates outside the
			// Polygon backfill, or any future hole), use the legacy 09:30 single-call path.
			if (!openedAtMinute.HasIntraday)
			{
				var openProposals = openedAtMinute.LegacyProposals;
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

	/// <summary>Intraday SL/TP simulation. Per-position: re-price each leg at bar.Low and bar.High,
	/// determine whether the position's mark crosses the SL or TP threshold *anywhere* in that range,
	/// and if so close at the *threshold mark* itself (found via bisection over spot) rather than at
	/// the day's extreme.
	///
	/// This is the key behavioural change from the prior implementation, which closed at the
	/// bar.High / bar.Low BS mark whenever the rule fired. For a 0DTE put credit spread that ended
	/// the day deep OTM, bar.High would price the spread at ~$0.003/share — 99% of max profit
	/// captured — far past the 50% threshold the user actually wanted to exit at. Real intraday
	/// execution closes near 50%, not at the day's extreme. Bisecting for the threshold spot makes
	/// the fill price honest: if you set tp = 0.5, you close at $0.30/share (50% captured), not
	/// $0.003/share (99% captured).
	///
	/// Whipsaw convention: SL fires before TP. If both thresholds are crossed in [Low, High] the
	/// adverse move is assumed to have come first (bear-case path).</summary>
	private async Task RunIntradayTriggersAsync(DateTime step, RuleEvaluator evaluator, CancellationToken cancellation)
	{
		if (_book.OpenPositions.Count == 0) return;
		var realizedExpectancy = _config.Opener.RealizedExpectancy;
		if (!realizedExpectancy.Enabled) return;

		var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
		// Snapshot positions — the book mutates as we close.
		var positions = _book.OpenPositions.Values.ToList();

		foreach (var pos in positions)
		{
			cancellation.ThrowIfCancellationRequested();
			if (!_book.OpenPositions.ContainsKey(pos.Key)) continue;
			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar == null) continue;
			await TryIntradayTriggerAsync(step, pos, bar.Low, bar.High, cash, accountValue, realizedExpectancy, cancellation);
		}
	}

	private async Task TryIntradayTriggerAsync(
		DateTime step, OpenPosition pos, decimal barLow, decimal barHigh,
		decimal cash, decimal accountValue,
		OpenerRealizedExpectancyConfig realizedExpectancy, CancellationToken cancellation)
	{
		var symbols = pos.Legs
			.Where(l => l.CallPut != null)
			.Select(l => l.Symbol)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (symbols.Count == 0) return;

		// Mark at the two extremes — these bracket the day's range of possible MTM values.
		var quotesLow = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, barLow, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
		var quotesHigh = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, barHigh, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
		var markLow = ComputeMarkFromQuotes(pos.Legs, quotesLow);
		var markHigh = ComputeMarkFromQuotes(pos.Legs, quotesHigh);
		if (!markLow.HasValue || !markHigh.HasValue) return;

		// SL threshold (mark at or below this = stop-loss triggered).
		// realizedLoss = InitialNetDebit - mark; SL fires when realizedLoss ≥ slPct × maxLoss,
		// i.e. when mark ≤ InitialNetDebit - slPct × maxLoss.
		//
		// slPct ≥ 1.0 disables the stop: the threshold would equal (or fall below) the position's
		// theoretical max-loss mark, which mirrors the scorer's terminal-PnL clamp at -1.0×maxLoss
		// (no effective stop). For debit structures this matters concretely — slTarget would equal 0
		// and the BS-priced mark of a deep-OTM 0DTE leg can round to exactly 0, firing SL the same
		// morning the position opens and foreclosing intraday recovery. Letting it run hits the same
		// economic outcome via expiration if the day actually ends at the floor.
		decimal? slTarget = null;
		if (_config.Rules.StopLoss.Enabled && pos.MaxLossPerShare.HasValue && pos.MaxLossPerShare.Value > 0m
			&& realizedExpectancy.StopLossPctOfMaxLoss < 1m)
			slTarget = pos.AdjustedNetDebit - realizedExpectancy.StopLossPctOfMaxLoss * pos.MaxLossPerShare.Value;

		// TP threshold (mark at or above this = take-profit triggered).
		// pctCaptured = (mark - InitialNetDebit) / maxProjected; TP fires when pctCaptured ≥ tpPct,
		// i.e. when mark ≥ InitialNetDebit + tpPct × maxProjected. maxProjected uses the projector
		// against the bar.High quotes — for 0DTE the projector iterates over future spots so current
		// spot doesn't materially affect the result.
		decimal? tpTarget = null;
		if (_config.Rules.TakeProfit.Enabled)
		{
			var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
			var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = barHigh };
			var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
			var projectorCtx = new EvaluationContext(step, stillOpen, underlyings, quotesHigh, cash, accountValue, emptySignals);
			var maxProjected = ProfitProjector.MaxForCurrentColumn(pos, projectorCtx);
			if (maxProjected.HasValue && maxProjected.Value > 0m)
				tpTarget = pos.AdjustedNetDebit + realizedExpectancy.ProfitTargetPctOfMaxProfit * maxProjected.Value;
		}

		// Does either threshold sit in [markLow, markHigh]? The mark is monotonic in spot for every
		// structure currently enumerated (verticals, naked longs, iron condors). If the threshold
		// lies between the two extreme marks, there's a spot in [barLow, barHigh] where mark equals
		// the threshold.
		decimal markMin = Math.Min(markLow.Value, markHigh.Value);
		decimal markMax = Math.Max(markLow.Value, markHigh.Value);
		bool slFires = slTarget.HasValue && slTarget.Value >= markMin && slTarget.Value <= markMax;
		bool tpFires = tpTarget.HasValue && tpTarget.Value >= markMin && tpTarget.Value <= markMax;

		// Also catch the case where the position was ALREADY past the threshold at bar.Open. We don't
		// have bar.Open here, but if BOTH extreme marks are past the threshold the day opened past it.
		// Trigger and close at the threshold price (conservative — gives back any deeper capture).
		if (slTarget.HasValue && markLow.Value <= slTarget.Value && markHigh.Value <= slTarget.Value) slFires = true;
		if (tpTarget.HasValue && markLow.Value >= tpTarget.Value && markHigh.Value >= tpTarget.Value) tpFires = true;

		if (!slFires && !tpFires) return;

		// Conservative: SL fires before TP on whipsaw days.
		string ruleName;
		decimal targetMark;
		if (slFires)
		{
			ruleName = "StopLossRule";
			targetMark = slTarget!.Value;
		}
		else
		{
			ruleName = "TakeProfitRule";
			targetMark = tpTarget!.Value;
		}

		// Find the spot in [barLow, barHigh] where the position mark equals targetMark.
		var thresholdSpot = await FindSpotForMarkAsync(
			step, pos, symbols, barLow, markLow.Value, barHigh, markHigh.Value, targetMark, cancellation);
		var quotesAtThreshold = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, thresholdSpot, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);

		// Reverse each leg's side to close. Use quote mids — same convention as the rule engine.
		var legFills = new List<BacktestLegFill>(pos.Legs.Count);
		foreach (var leg in pos.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!quotesAtThreshold.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
			legFills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, mid));
		}

		_book.Close(step, pos.Key, legFills, ruleName);
	}

	/// <summary>Bisects spot in <c>[spotLow, spotHigh]</c> to find where the position's mark equals
	/// <paramref name="targetMark"/>. Mark monotonicity in spot is assumed (true for every structure
	/// the opener currently enumerates). Caller is responsible for verifying that <paramref name="targetMark"/>
	/// lies between <paramref name="markAtLow"/> and <paramref name="markAtHigh"/> — otherwise this
	/// returns the boundary spot closest to the target. 16 iterations gives <c>(spotHigh - spotLow) / 65536</c>
	/// spot precision — fractions of a penny for any realistic intraday range.</summary>
	private async Task<decimal> FindSpotForMarkAsync(
		DateTime step, OpenPosition pos, HashSet<string> symbols,
		decimal spotLow, decimal markAtLow, decimal spotHigh, decimal markAtHigh,
		decimal targetMark, CancellationToken cancellation)
	{
		if (spotHigh - spotLow < 0.01m) return (spotLow + spotHigh) / 2m;
		bool markIncreases = markAtHigh > markAtLow;
		decimal lo = spotLow, hi = spotHigh;

		for (int i = 0; i < 16 && hi - lo > 0.01m; i++)
		{
			cancellation.ThrowIfCancellationRequested();
			var mid = (lo + hi) / 2m;
			var quotes = await _quotes.GetIntradayQuotesAsync(
				step, pos.Ticker, mid, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
			var mark = ComputeMarkFromQuotes(pos.Legs, quotes);
			if (!mark.HasValue) break;

			if (markIncreases ? mark.Value < targetMark : mark.Value > targetMark)
				lo = mid;
			else
				hi = mid;
		}
		return (lo + hi) / 2m;
	}

	/// <summary>Result of <see cref="TryOpenAcrossDayAsync"/>. <c>HasIntraday</c> indicates whether
	/// per-minute scan ran (we found minute data for every configured ticker); when false the caller
	/// must execute the legacy 09:30 single-call path using <c>LegacyProposals</c>.</summary>
	private readonly record struct DailyOpenScanResult(bool HasIntraday, IReadOnlyList<OpenProposal> LegacyProposals);

	/// <summary>Scans each minute of the trading day (09:30 → 16:00 ET) and opens the first proposal
	/// that clears the score + cash gates. Spot is taken from the per-minute close of the configured
	/// ticker's intraday CSV; 0DTE TTE shrinks linearly to 16:00. One open per day max (early-exit on
	/// first fill). Returns <c>HasIntraday=false</c> when minute data is missing for any ticker —
	/// caller falls back to the legacy once-per-day fill.</summary>
	private async Task<DailyOpenScanResult> TryOpenAcrossDayAsync(DateTime step, HashSet<string> tickerSet, OpenCandidateEvaluator openEvaluator, CancellationToken cancellation)
	{
		// Shared per-day inputs — these depend only on the date, not the minute.
		var postMgmt = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
		var (postCash, postAccount) = await _positions.GetAccountStateAsync(step, cancellation);
		var postSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);

		// Load each ticker's minute bars for the RTH window. Convert step.Date + (09:30 ET, 16:00 ET)
		// to UTC for the cache call — the cache returns bars in UTC, but the on-disk grouping is by
		// NY date, so any ET-correct window spans the right file.
		var openEt = new DateTime(step.Date.Year, step.Date.Month, step.Date.Day, 9, 30, 0, DateTimeKind.Unspecified);
		var closeEt = new DateTime(step.Date.Year, step.Date.Month, step.Date.Day, 16, 0, 0, DateTimeKind.Unspecified);
		var openUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(openEt, NyTz), TimeSpan.Zero);
		var closeUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(closeEt, NyTz), TimeSpan.Zero);

		var barsByTicker = new Dictionary<string, IReadOnlyList<MinuteBar>>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in tickerSet)
		{
			var bars = await _intradayBars.GetBarsAsync(t, openUtc, closeUtc, BarInterval.M1, includeExtended: false, cancellation);
			if (bars.Count == 0)
			{
				// Fall back to legacy path: caller runs the once-per-day opener at 09:30.
				var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, step, _quotes, _config, cancellation);
				var postCtx = new EvaluationContext(step, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
				var legacy = await openEvaluator.EvaluateAsync(postCtx, cancellation);
				return new DailyOpenScanResult(HasIntraday: false, LegacyProposals: legacy);
			}
			barsByTicker[t] = bars;
		}

		// Index per ticker by UTC timestamp for O(1) per-minute lookup.
		var barIndexByTicker = new Dictionary<string, Dictionary<DateTimeOffset, MinuteBar>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (t, bars) in barsByTicker)
		{
			var idx = new Dictionary<DateTimeOffset, MinuteBar>(bars.Count);
			foreach (var b in bars) idx[b.Timestamp] = b;
			barIndexByTicker[t] = idx;
		}

		// Loop driver: union of timestamps across tickers, sorted. With aligned SPY/SPXW CSVs the
		// union equals either ticker's bars; the loop generalizes if other tickers get added.
		var allTimestamps = new SortedSet<DateTimeOffset>();
		foreach (var bars in barsByTicker.Values)
			foreach (var b in bars) allTimestamps.Add(b.Timestamp);

		// Oracle mode: scan every minute, forward-simulate every proposal to expiry using the day's
		// known bar.Close intrinsic, and open the single (minute, proposal) pair with the highest
		// realized P&L. By design lookahead — research / upper-bound tool, not realistic.
		(DateTime MinuteEt, OpenProposal Proposal, IReadOnlyList<BacktestLegFill> LegFills, decimal Pnl)? bestOracle = null;

		var opened = 0;
		foreach (var minuteUtc in allTimestamps)
		{
			if (!_oracle && opened >= _topNPerStep) break;
			cancellation.ThrowIfCancellationRequested();

			// Build per-minute spot dict. Skip minutes where any required ticker has no bar — the
			// opener's bootstrap would have no spot for it and produce no candidates anyway. Use
			// bar.Open (the price at the START of the minute) rather than bar.Close: ctx.Now is
			// stamped at the start of the minute and the backfill's intraday SPXW curve is anchored
			// so that bar.Open @ 09:30 exactly equals ^GSPC.Open from the daily history — switching
			// to bar.Close here would introduce a 1-minute lookahead and break the anchor invariant.
			var minuteSpots = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
			var missing = false;
			foreach (var t in tickerSet)
			{
				if (!barIndexByTicker[t].TryGetValue(minuteUtc, out var bar)) { missing = true; break; }
				minuteSpots[t] = bar.Open;
			}
			if (missing) continue;

			// TTE for 0DTE legs: remaining minutes from this bar to 16:00 ET, converted to years.
			// Floor at one minute (≈ 1.9e-6 years) so deep-OTM 0DTE doesn't price exactly intrinsic
			// before the actual close — the existing settlement path handles the at-close cash flow.
			var minutesToClose = Math.Max(1.0, (closeUtc - minuteUtc).TotalMinutes);
			var minuteZeroDteTimeYears = minutesToClose / 60.0 / 24.0 / 365.0;

			// Convert the bar's UTC timestamp back to ET-naive (DateTimeKind.Unspecified) so it matches
			// the rest of the simulator's convention — the existing engine treats step / ctx.Now as
			// ET-wall-clock without tz markers, and downstream lookups like ctx.Now.Date must resolve
			// to the trading date in ET.
			var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);

			// Stamp the evaluator's quote source with the minute's spot + TTE. The opener calls
			// _quotes.GetQuotesAsync(...) internally, which now returns minute-anchored quotes.
			_quotes.MinuteScanSpotOverrides = minuteSpots;
			_quotes.MinuteScanZeroDteTimeYears = minuteZeroDteTimeYears;
			IReadOnlyList<OpenProposal> openProposals;
			try
			{
				var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, minuteEt, _quotes, _config, cancellation);
				var postCtx = new EvaluationContext(minuteEt, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
				openProposals = await openEvaluator.EvaluateAsync(postCtx, cancellation);
			}
			finally
			{
				_quotes.MinuteScanSpotOverrides = null;
				_quotes.MinuteScanZeroDteTimeYears = null;
			}

			foreach (var p in openProposals)
			{
				if (!_oracle && opened >= _topNPerStep) break;
				if (p.CashReserveBlocked) continue;
				if (p.Qty < 1) continue;
				var requiredCash = p.CapitalAtRiskPerContract * p.Qty;
				if (requiredCash > _book.Cash) continue;

				var legFills = BuildLegFillsFromProposal(p.Legs, p.Qty);
				if (legFills == null) continue;

				if (_oracle)
				{
					var eodPnl = await ComputeOracleEodPnlAsync(p, step.Date, cancellation);
					if (eodPnl == null) continue;  // non-0DTE or missing data
					if (bestOracle == null || eodPnl.Value > bestOracle.Value.Pnl)
						bestOracle = (minuteEt, p, legFills, eodPnl.Value);
				}
				else
				{
					if (_book.Open(minuteEt, p.Ticker, p.StructureKind, legFills, p.Qty)) opened++;
				}
			}
		}

		// Oracle: open the single best proposal across all minutes.
		if (_oracle && bestOracle != null)
		{
			var b = bestOracle.Value;
			_book.Open(b.MinuteEt, b.Proposal.Ticker, b.Proposal.StructureKind, b.LegFills, b.Proposal.Qty);
		}

		return new DailyOpenScanResult(HasIntraday: true, LegacyProposals: Array.Empty<OpenProposal>());
	}

	/// <summary>Oracle forward-simulator: returns the realized P&L (in dollars) of <paramref name="p"/>
	/// if held from open to expiry, using the day's bar.Close as the at-expiry spot. Returns null when
	/// any leg expires on a date other than <paramref name="stepDate"/> (multi-day positions need a
	/// chained forward sim we don't do here — out of scope for the SPXW 0DTE strategy this is built
	/// for) or when the close-bar / leg metadata can't be resolved.</summary>
	private async Task<decimal?> ComputeOracleEodPnlAsync(OpenProposal p, DateTime stepDate, CancellationToken cancellation)
	{
		var bar = await _bars.GetBarAsync(p.Ticker, stepDate, cancellation);
		if (bar == null) return null;
		var closeSpot = bar.Close;

		decimal netOpenCashPerContract = 0m;
		decimal netExpireCashPerContract = 0m;
		foreach (var leg in p.Legs)
		{
			if (!leg.PricePerShare.HasValue) return null;
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed == null || parsed.CallPut == null) return null;
			var isBuy = string.Equals(leg.Action, "buy", StringComparison.OrdinalIgnoreCase);

			// Open cash flow per share per contract: buys are debits (negative), sells are credits (+).
			netOpenCashPerContract += (isBuy ? -1m : 1m) * leg.PricePerShare.Value;

			// Multi-day expiry: can't oracle without a chained forward sim. Skip.
			if (parsed.ExpiryDate.Date != stepDate.Date) return null;

			var intrinsic = parsed.CallPut == "C"
				? Math.Max(0m, closeSpot - parsed.Strike)
				: Math.Max(0m, parsed.Strike - closeSpot);
			// Settlement: long legs collect intrinsic (+), short legs pay (-). Matches
			// SimulatedBook.Expire's signing convention.
			netExpireCashPerContract += (isBuy ? 1m : -1m) * intrinsic;
		}

		const decimal multiplier = 100m;
		var netPnlPerContract = (netOpenCashPerContract + netExpireCashPerContract) * multiplier;
		var grossPnl = netPnlPerContract * p.Qty;
		// Fees on Open only; expirations are fee-free in the simulator. Matches SimulatedBook.
		var openFees = p.Legs.Count * p.Qty * _book.FeePerContract;
		return grossPnl - openFees;
	}

	/// <summary>Per-share mark = sum of leg mids signed by side. Returns null if any leg is missing
	/// a usable bid/ask. Matches <see cref="StopLossRule.ComputeMarkPerShare"/> and
	/// <see cref="TakeProfitRule.ComputeMarkPerContract"/> exactly (per-share, not per-contract —
	/// the two rule names are misleadingly different but the units are the same: per-share dollars).</summary>
	private static decimal? ComputeMarkFromQuotes(IReadOnlyList<PositionLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (leg.CallPut == null) continue;
			if (!quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (!q.Bid.HasValue || !q.Ask.HasValue) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
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
