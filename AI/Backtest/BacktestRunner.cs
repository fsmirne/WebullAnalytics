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
	private readonly bool _profile;
	private readonly bool _discover;
	private readonly HashSet<string> _discoveredOccs = new(StringComparer.OrdinalIgnoreCase);

	// Conceptual fill times within a trading day. Opens, closes, and rolls all price off bar.Open
	// (BacktestQuoteSource uses the day's open as spot), so they're stamped at 09:30 ET — the
	// start-of-bar convention: a bar at 09:30:00 covers the 09:30→09:31 ET minute and contains the
	// auction-cleared open price. Webull natively stamps end-of-bar (09:31:00) but we normalize that
	// to start-of-bar at parse time in `WebullChartsClient` so the entire codebase agrees with
	// Polygon, ToS, and TradingView. Expirations settle at bar.Close intrinsic, stamped at 16:00 ET.
	private static readonly TimeSpan MarketOpenTime = TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
	private static readonly TimeSpan MarketCloseTime = TimeSpan.FromHours(16);

	public BacktestRunner(AIConfig config, SimulatedBook book, BacktestPositionSource positions, BacktestQuoteSource quotes, HistoricalBarCache bars, HistoricalPriceCache closeCache, int topNPerStep, bool oracle = false, bool profile = false, bool discover = false)
	{
		_config = config;
		_book = book;
		_positions = positions;
		_quotes = quotes;
		_bars = bars;
		_closeCache = closeCache;
		_topNPerStep = topNPerStep;
		_oracle = oracle;
		_profile = profile;
		_discover = discover;
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

		// Per-step timing diagnostic. Enabled via --profile on `wa ai backtest`; off otherwise (zero
		// overhead in normal runs). Surfaces where wall time is going across the simulator's loop —
		// useful when a recent change makes the backtest noticeably slower than before.
		var profile = _profile;
		var swTotal = profile ? System.Diagnostics.Stopwatch.StartNew() : null;
		long msStep1 = 0, msStep2 = 0, msStep3 = 0, msTriggers = 0, msMtm = 0;

		foreach (var step in steps)
		{
			cancellation.ThrowIfCancellationRequested();
			var swSection = profile ? System.Diagnostics.Stopwatch.StartNew() : null;

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
					else if (p.Kind == ProposalKind.LegIn)
					{
						var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quoteSnapshot.Options);
						// Rule emits the new structure name via convention: LongCall→LongCallVertical, LongPut→LongPutVertical.
						// Derive from the existing strategy + the fact that the new leg is opposite-side.
						if (legFills != null)
						{
							var newStructure = string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongCallVertical
								: string.Equals(pos.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongPutVertical
								: (OpenStructureKind?)null;
							if (newStructure != null) _book.LegIn(step, p.PositionKey, legFills, p.Rule, newStructure.Value);
						}
					}
					// AlertOnly: noop in backtest.
				}
			}

			if (profile) { msStep1 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// Step 2: settle anything that's still in the book and has reached expiry. Anything still
			// here at this point is either (a) the rule engine didn't decide to close it, or (b) the
			// position has only-OTM short legs and is harmless to let expire worthless.
			await SettleExpirationsAsync(step, cancellation);

			if (profile) { msStep2 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

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
					if (_book.Open(step, p.Ticker, p.StructureKind, legFills, p.Qty))
					{
						opened++;
						RecordDiscoveredLegs(p.Legs);
					}
				}
			}

			if (profile) { msStep3 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

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

			if (profile) { msTriggers += swSection!.ElapsedMilliseconds; swSection.Restart(); }

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

			if (profile) msMtm += swSection!.ElapsedMilliseconds;
		}

		if (profile)
		{
			var total = swTotal!.ElapsedMilliseconds;
			Console.WriteLine();
			Console.WriteLine($"[profile] backtest wall time: {total:N0} ms ({steps.Count} trading days, {(double)total / Math.Max(1, steps.Count):F1} ms/day avg)");
			Console.WriteLine($"[profile]   step 1 (rules):              {msStep1:N0} ms ({(double)msStep1 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   step 2 (settle pre-open):    {msStep2:N0} ms ({(double)msStep2 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   step 3 (opener minute loop): {msStep3:N0} ms ({(double)msStep3 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   intraday triggers + settle:  {msTriggers:N0} ms ({(double)msTriggers * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   end-of-step MTM:             {msMtm:N0} ms ({(double)msMtm * 100 / Math.Max(1, total):F1}%)");
		}

		// Final per-lineage MTM for still-open positions so the renderer can compute unrealized P&L.
		var endMtmByLineage = await ComputeOpenMarkPerLineageAsync(steps.Count > 0 ? steps[^1] : until, cancellation);

		// Discovery: flush every OCC the run picked to disk so `wa ai history <ticker> --options`
		// can fetch them from massive.com next. The file persists across runs (union semantics) so
		// repeated backtests with different windows accumulate the full strategy footprint.
		if (_discover) FlushDiscoveryLog();

		return new BacktestResult(
			StartingCash: startingCash,
			EndingCash: _book.Cash,
			TotalFees: _book.TotalFees,
			OpenFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Open),
			CloseFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Close),
			RollFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Roll),
			LegInFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.LegIn),
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

			// Prefer the minute-walk: walks minute bars chronologically and triggers at the first
			// minute the position mark crosses SL or TP. Removes the bar.Low / bar.High pessimism
			// of the 2-point sampling (which fires SL on intraday spikes that never realized as
			// actual fills, especially for 0DTE near-the-money positions). Falls back to the
			// legacy 2-point logic when no minute data exists for this date.
			var triggered = await TryMinuteWalkTriggerAsync(step, pos, cash, accountValue, realizedExpectancy, cancellation);
			if (triggered) continue;

			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar == null) continue;
			await TryIntradayTriggerAsync(step, pos, bar.Low, bar.High, cash, accountValue, realizedExpectancy, cancellation);
		}
	}

	/// <summary>Walks minute bars chronologically for <paramref name="pos"/> and closes the position
	/// at the first minute its mark crosses the SL or TP threshold. Returns true when a trigger
	/// fired (caller skips the legacy fallback) and false when no minute data exists or no
	/// threshold was crossed (caller falls back to bar.Low / bar.High sampling).
	///
	/// Mechanics match <see cref="TryIntradayTriggerAsync"/> for threshold computation (SL via
	/// pos.MaxLossPerShare, TP via ProfitProjector.MaxForCurrentColumn) and conservative whipsaw
	/// (SL fires before TP). The substantive difference: instead of bracketing the day with
	/// [bar.Low, bar.High] and bisecting to find a threshold spot, we step through real minute
	/// closes from the position's open time (or 09:30 ET for carry-over positions) to 16:00 ET,
	/// price the chain at each minute's spot with a remaining-session TTE, and fire on the first
	/// real crossing. Closes at that minute's mark — no bisection needed.</summary>
	private async Task<bool> TryMinuteWalkTriggerAsync(DateTime step, OpenPosition pos, decimal cash, decimal accountValue, OpenerRealizedExpectancyConfig realizedExpectancy, CancellationToken cancellation)
	{
		var symbols = pos.Legs
			.Where(l => l.CallPut != null)
			.Select(l => l.Symbol)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (symbols.Count == 0) return false;

		// Determine when to start walking. For positions opened earlier today, start one minute
		// after the open fill so we don't trigger on the same minute we opened (mark == debit,
		// neither threshold crossed yet anyway). For carry-over positions, start at 09:30 ET.
		var openFill = _book.Fills
			.Where(f => string.Equals(f.PositionKey, pos.Key, StringComparison.OrdinalIgnoreCase) && f.Kind == BacktestFillKind.Open)
			.OrderByDescending(f => f.Date)
			.FirstOrDefault();
		DateTime walkStartEt;
		if (openFill != null && openFill.Date.Date == step.Date)
			walkStartEt = openFill.Date.AddMinutes(1);
		else
			walkStartEt = step.Date.Add(MarketOpenTime);
		var walkEndEt = step.Date.Add(MarketCloseTime);
		if (walkStartEt >= walkEndEt) return false;

		var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(walkStartEt, NyTz), TimeSpan.Zero);
		var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(walkEndEt, NyTz), TimeSpan.Zero);
		var minuteBars = await _intradayBars.GetBarsAsync(pos.Ticker, startUtc, endUtc, BarInterval.M1, includeExtended: false, cancellation);
		if (minuteBars.Count == 0) return false;

		// SL threshold (mark at or below this fires SL). Matches legacy logic exactly.
		decimal? slTarget = null;
		if (_config.Rules.StopLoss.Enabled && pos.MaxLossPerShare.HasValue && pos.MaxLossPerShare.Value > 0m
			&& realizedExpectancy.StopLossPctOfMaxLoss < 1m)
			slTarget = pos.AdjustedNetDebit - realizedExpectancy.StopLossPctOfMaxLoss * pos.MaxLossPerShare.Value;

		// TP threshold (mark at or above fires TP). Use the day's bar.High as the projector spot
		// to mirror legacy behavior — the projector iterates over future spots so the choice has
		// minimal effect for 0DTE, but staying consistent makes side-by-side comparisons cleaner.
		// Symmetric with the SL gate: skip when ProfitTargetPctOfMaxProfit ≥ 1.0 (the threshold
		// equals theoretical max profit, which no intraday mark realistically reaches — walking
		// every minute to verify that costs ~390 iterations per open position per day for no gain).
		decimal? tpTarget = null;
		if (_config.Rules.TakeProfit.Enabled && realizedExpectancy.ProfitTargetPctOfMaxProfit < 1m)
		{
			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar != null)
			{
				var quotesHighForProjector = await _quotes.GetIntradayQuotesAsync(
					step, pos.Ticker, bar.High, symbols, BacktestQuoteSource.IntradayHalfSessionTimeYears, cancellation);
				var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = bar.High };
				var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
				var projectorCtx = new EvaluationContext(step, stillOpen, underlyings, quotesHighForProjector, cash, accountValue, emptySignals);
				var maxProjected = ProfitProjector.MaxForCurrentColumn(pos, projectorCtx);
				if (maxProjected.HasValue && maxProjected.Value > 0m)
					tpTarget = pos.AdjustedNetDebit + realizedExpectancy.ProfitTargetPctOfMaxProfit * maxProjected.Value;
			}
		}

		// LegInShort: only meaningful on single-leg long calls/puts and only fires intraday for 0DTE
		// strategies (multi-day positions get evaluated at start-of-day in the main rule loop).
		// Instantiated outside the minute loop so we don't re-allocate per minute. Must be declared
		// BEFORE the early-return check so the carve-out can see it.
		var legInRule = _config.Rules.LegInShort.Enabled
			&& (string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) || string.Equals(pos.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase))
			? new Rules.LegInShortRule(_config.Rules.LegInShort, _config.Indicators)
			: null;

		// Regime indicators for LegInShort: VIX (per-day constant, fetched once) and intraday range
		// (tracked across the minute loop). Both null when not needed — keep the lookup cost off the
		// hot path when the rule's enabled flag is off.
		decimal? dayVix = null;
		if (legInRule != null)
		{
			var vixBar = await _bars.GetBarAsync("VIX", step.Date, cancellation);
			if (vixBar != null) dayVix = vixBar.Close;
		}

		// LegInShort needs the minute walk even when SL/TP are both disabled (e.g. SPXW's
		// profit/stop pcts pinned at 1.0 to defer all closes to expiration). Without this carve-out,
		// the rule would silently never fire on 0DTE strategies that disable both gates.
		if (!slTarget.HasValue && !tpTarget.HasValue && legInRule == null) return false;

		// Track intraday range running from session start to the current minute. Used by LegInShort's
		// trend-day filter. We use the first minute bar's open as the day's open reference (close to
		// 09:30 ET when the data covers the full session).
		var dayOpenSpot = minuteBars.Count > 0 ? minuteBars[0].Open : 0m;
		decimal dayHigh = dayOpenSpot, dayLow = dayOpenSpot;

		// Walk minute bars. At each minute, re-price the position at that minute's bar.Open spot
		// (start-of-minute price; consistent with ctx.Now semantics elsewhere in the simulator)
		// using a remaining-session TTE. Trigger on first SL/TP crossing.
		foreach (var minuteBar in minuteBars)
		{
			cancellation.ThrowIfCancellationRequested();
			var minuteUtc = minuteBar.Timestamp;
			var spot = minuteBar.Open;
			var minutesToClose = Math.Max(1.0, (endUtc - minuteUtc).TotalMinutes);
			var minuteZeroDteTimeYears = minutesToClose / 60.0 / 24.0 / 365.0;

			// Update intraday range with this minute's H/L.
			if (minuteBar.High > dayHigh) dayHigh = minuteBar.High;
			if (minuteBar.Low < dayLow) dayLow = minuteBar.Low;

			// For LegInShort: pre-generate candidate strikes around spot at the long's expiry so the
			// rule has multiple strikes to pick from. Only done when the rule is applicable to this
			// position's structure; otherwise we fetch just the position's leg symbols.
			IEnumerable<string> quoteSymbols = symbols;
			if (legInRule != null)
			{
				var longLeg = pos.Legs[0];
				var step5 = _config.Indicators.StrikeStep > 0m ? _config.Indicators.StrikeStep : 5m;
				var candidateSymbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
				// ±100 points (at $5 step → 40 strikes) covers the 0.05–0.95 delta range for SPXW even
				// at short DTE with elevated IV. Cheap enough at minute resolution.
				for (decimal k = Math.Floor(spot / step5) * step5 - 100m; k <= Math.Ceiling(spot / step5) * step5 + 100m; k += step5)
				{
					if (k <= 0m) continue;
					candidateSymbols.Add(MatchKeys.OccSymbol(pos.Ticker, longLeg.Expiry!.Value, k, longLeg.CallPut!));
				}
				quoteSymbols = candidateSymbols;
			}
			var quotes = await _quotes.GetIntradayQuotesAsync(step, pos.Ticker, spot, quoteSymbols, minuteZeroDteTimeYears, cancellation);
			var mark = ComputeMarkFromQuotes(pos.Legs, quotes);
			if (!mark.HasValue) continue;

			// Try LegInShort first at this minute. If it fires, execute the leg-in and stop the
			// minute walk for this position — the now-vertical settles at expire. We forgo intraday
			// SL/TP on the post-leg-in vertical (simplest pass; vertical's tighter risk profile
			// makes this a benign approximation for 0DTE).
			if (legInRule != null)
			{
				var legInMinuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);
				var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot };
				var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
				decimal? rangePct = dayOpenSpot > 0m ? (dayHigh - dayLow) / dayOpenSpot * 100m : null;
				var minuteCtx = new EvaluationContext(legInMinuteEt, stillOpen, underlyings, quotes, cash, accountValue, emptySignals, dayVix, rangePct);
				var legInProposal = legInRule.Evaluate(pos, minuteCtx);
				if (legInProposal != null)
				{
					// Single sell-to-open leg from the proposal. Price the fill at the bid for conservatism
					// (we're selling — we receive at most the bid).
					var shortLeg = legInProposal.Legs[0];
					if (quotes.TryGetValue(shortLeg.Symbol, out var sq) && sq.Bid is decimal sBid)
					{
						// Map to OpenStructureKind. Credit-spread mode → ShortVertical (single kind covers
						// both bear-call and bull-put credit spreads); debit-spread → LongCallVertical or
						// LongPutVertical depending on original long direction.
						OpenStructureKind newStructure;
						if (_config.Rules.LegInShort.CreditSpread)
							newStructure = (string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.ShortCallVertical : OpenStructureKind.ShortPutVertical);
						else
							newStructure = string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongCallVertical : OpenStructureKind.LongPutVertical;
						var legInFills = new[] { new BacktestLegFill(shortLeg.Symbol, Side.Sell, pos.Quantity, sBid) };
						_book.LegIn(legInMinuteEt, pos.Key, legInFills, "LegInShortRule", newStructure);
						return true; // mirror SL/TP semantics: a fired rule consumes the position for the day.
					}
				}
			}

			bool slFires = slTarget.HasValue && mark.Value <= slTarget.Value;
			bool tpFires = tpTarget.HasValue && mark.Value >= tpTarget.Value;
			if (!slFires && !tpFires) continue;

			// SL before TP if both crossed at the same minute — conservative whipsaw assumption.
			var ruleName = slFires ? "StopLossRule" : "TakeProfitRule";

			var legFills = new List<BacktestLegFill>(pos.Legs.Count);
			bool allLegsPriced = true;
			foreach (var leg in pos.Legs)
			{
				if (leg.CallPut == null) continue;
				if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) { allLegsPriced = false; break; }
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
				legFills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, mid));
			}
			if (!allLegsPriced) continue;

			var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);
			_book.Close(minuteEt, pos.Key, legFills, ruleName);
			return true;
		}

		return false;
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
		if (_config.Rules.TakeProfit.Enabled && realizedExpectancy.ProfitTargetPctOfMaxProfit < 1m)
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

	/// <summary>Scans each minute of the trading day (09:30 → 16:00 ET — see <see cref="MarketOpenTime"/>) and opens the first proposal
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
		// NY date, so any ET-correct window spans the right file. Window starts at 09:30 (first RTH
		// minute under our normalized start-of-bar convention; see `MarketOpenTime`).
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

		// Sequential minute loop. Per-minute work (~1.5 ms) is small enough that the .NET thread
		// pool's per-task overhead exceeds the parallel gain even on a 32-core box. The QuoteOverrides
		// refactor that removed BacktestQuoteSource's mutable state would allow a parallel pass if
		// future per-minute work grows (e.g. forward-simulating each proposal for oracle), but for
		// the current workload sequential wins on wall time.
		var opened = 0;
		foreach (var minuteUtc in allTimestamps)
		{
			if (!_oracle && opened >= _topNPerStep) break;
			cancellation.ThrowIfCancellationRequested();

			var result = await EvaluateMinuteAsync(minuteUtc, closeUtc, tickerSet, barIndexByTicker, postMgmt, postCash, postAccount, postSignals, openEvaluator, cancellation);
			if (result == null) continue;
			var (minuteEt, openProposals) = result.Value;

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
					if (eodPnl == null) continue;
					if (bestOracle == null || eodPnl.Value > bestOracle.Value.Pnl)
						bestOracle = (minuteEt, p, legFills, eodPnl.Value);
				}
				else
				{
					if (_book.Open(minuteEt, p.Ticker, p.StructureKind, legFills, p.Qty))
					{
						opened++;
						RecordDiscoveredLegs(p.Legs);
					}
				}
			}
		}

		// Oracle: open the single best proposal across all minutes.
		if (_oracle && bestOracle != null)
		{
			var b = bestOracle.Value;
			_book.Open(b.MinuteEt, b.Proposal.Ticker, b.Proposal.StructureKind, b.LegFills, b.Proposal.Qty);
			RecordDiscoveredLegs(b.Proposal.Legs);
		}

		return new DailyOpenScanResult(HasIntraday: true, LegacyProposals: Array.Empty<OpenProposal>());
	}

	/// <summary>Evaluates the opener at a single minute. Pure function over the inputs — no shared
	/// mutable state — so concurrent calls are safe. Returns null when any configured ticker has no
	/// bar at this minute (the opener would have no spot to work with).</summary>
	private async Task<(DateTime MinuteEt, IReadOnlyList<OpenProposal> Proposals)?> EvaluateMinuteAsync(
		DateTimeOffset minuteUtc,
		DateTimeOffset closeUtc,
		HashSet<string> tickerSet,
		Dictionary<string, Dictionary<DateTimeOffset, MinuteBar>> barIndexByTicker,
		IReadOnlyDictionary<string, OpenPosition> postMgmt,
		decimal postCash,
		decimal postAccount,
		IReadOnlyDictionary<string, TechnicalBias> postSignals,
		OpenCandidateEvaluator openEvaluator,
		CancellationToken cancellation)
	{
		// Build per-minute spot dict. Skip when any required ticker has no bar at this minute — the
		// opener's bootstrap would have no spot to work with. Use bar.Open (the price at the START
		// of the minute) rather than bar.Close: ctx.Now is stamped at the start of the minute and
		// the backfill's intraday SPXW curve is anchored so bar.Open @ 09:30 exactly equals
		// ^GSPC.Open from the daily history — switching to bar.Close would introduce a 1-minute
		// lookahead and break the anchor invariant.
		var minuteSpots = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in tickerSet)
		{
			if (!barIndexByTicker[t].TryGetValue(minuteUtc, out var bar)) return null;
			minuteSpots[t] = bar.Open;
		}

		// TTE for 0DTE legs: remaining minutes from this bar to 16:00 ET, in years. Floored at 1
		// minute so deep-OTM 0DTE doesn't price exactly intrinsic before the actual close — the
		// existing settlement path handles the at-close cash flow.
		var minutesToClose = Math.Max(1.0, (closeUtc - minuteUtc).TotalMinutes);
		var minuteZeroDteTimeYears = minutesToClose / 60.0 / 24.0 / 365.0;

		// Convert the bar's UTC timestamp back to ET-naive (DateTimeKind.Unspecified) so it matches
		// the simulator's ET-wall-clock convention.
		var minuteEt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(minuteUtc.UtcDateTime, NyTz), DateTimeKind.Unspecified);

		var minuteOverrides = new QuoteOverrides(Spots: minuteSpots, ZeroDteTimeYears: minuteZeroDteTimeYears);
		var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, minuteEt, _quotes, _config, cancellation, minuteOverrides);
		var postCtx = new EvaluationContext(minuteEt, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
		var proposals = await openEvaluator.EvaluateAsync(postCtx, cancellation, minuteOverrides);
		return (minuteEt, proposals);
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

	/// <summary>Adds each option leg's OCC symbol to the discovery set. Called from every <c>_book.Open</c>
	/// site (daily-step opener, intraday opener, oracle finalizer). Cheap — just a hashset add — so it's
	/// safe to invoke whether or not <c>_discover</c> is set; <see cref="FlushDiscoveryLog"/> is the
	/// guarded write path.</summary>
	private void RecordDiscoveredLegs(IEnumerable<ProposalLeg> legs)
	{
		foreach (var leg in legs)
		{
			if (string.IsNullOrWhiteSpace(leg.Symbol)) continue;
			_discoveredOccs.Add(leg.Symbol);
		}
	}

	/// <summary>Writes the union of discovered OCCs to <c>data/options-discovery/&lt;ticker&gt;.jsonl</c>.
	/// One JSON line per OCC: <c>{"occ":"SPXW260526C07530000","ticker":"SPXW"}</c>. Re-reads the existing
	/// file first so the union accumulates across multiple backtest runs — a user who runs three different
	/// date ranges over time builds up a comprehensive catalog of contracts the strategy would ever pick.
	///
	/// <para>Per-ticker file split keeps the catalog readable when the user backtests multiple tickers;
	/// the file's ticker matches the first ticker in the config (typically the only one for SPXW-focused
	/// runs). Atomic write via tmp+rename so a crash mid-write can't truncate the catalog.</para></summary>
	private void FlushDiscoveryLog()
	{
		if (_discoveredOccs.Count == 0) return;
		var ticker = _config.Tickers.FirstOrDefault();
		if (string.IsNullOrWhiteSpace(ticker)) return;

		var dir = Program.ResolvePath("data/options-discovery");
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, ticker.ToUpperInvariant() + ".jsonl");

		// Read existing entries (if any) into a set, merge with newly-discovered, write back.
		var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (File.Exists(path))
		{
			foreach (var line in File.ReadAllLines(path))
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				try
				{
					using var doc = System.Text.Json.JsonDocument.Parse(line);
					if (doc.RootElement.TryGetProperty("occ", out var el) && el.GetString() is { } occ)
						union.Add(occ);
				}
				catch (System.Text.Json.JsonException) { /* skip malformed line */ }
			}
		}
		foreach (var o in _discoveredOccs) union.Add(o);

		var sorted = union.OrderBy(o => o, StringComparer.Ordinal).ToList();
		var sb = new System.Text.StringBuilder();
		foreach (var occ in sorted)
			sb.Append("{\"occ\":\"").Append(occ).Append("\",\"ticker\":\"").Append(ticker.ToUpperInvariant()).Append("\"}\n");
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, sb.ToString());
		File.Move(tmp, path, overwrite: true);
		Console.WriteLine($"discovery: {_discoveredOccs.Count} new + {union.Count - _discoveredOccs.Count} prior = {union.Count} OCC(s) cataloged at {path}");
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
	int LegInFills,
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
