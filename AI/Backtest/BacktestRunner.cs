using Spectre.Console;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Pricing;
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
/// Fills price off <see cref="BacktestQuoteSource"/> under the active <c>--pricing</c> mode: <c>mid</c> at the
/// spread midpoint, <c>bidask</c> crossing the spread (worst-case marketable fills). Marks always use mid.
/// Fees are debited per leg-contract; <c>slippagePerSharePerOrder</c> applies additively on top.
/// Phase-1 limitations (noted for follow-up work): no intraday triggering on stop-loss / take-profit,
/// and opens fill same-day instead of at next-day open.
/// </summary>
internal sealed class BacktestRunner
{

	private readonly AIConfig _config;
	private readonly SimulatedBook _book;
	private readonly BacktestPositionSource _positions;
	private readonly IBacktestQuoteSource _quotes;
	private readonly HistoricalBarCache _bars;
	private readonly HistoricalPriceCache _closeCache;
	private readonly int _topNPerStep;
	private readonly IntradayBarCache _intradayBars;
	private readonly bool _oracle;
	private readonly bool _profile;
	private readonly int? _fixedContracts;
	// Fraction of each leg's half-spread a fill gives up: 0 = mid (--pricing mid), 1 = cross the spread (--pricing bidask),
	// in between = --cross. See ComboFill.
	private readonly decimal _cross;
	private readonly int _scanStride;
	// Book each split structure as its two combo orders (live-broker representation) instead of one
	// composite position. See OpenProposalIntoBook.
	private readonly bool _splitStructures;
	private readonly IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? _dividendsByRoot;
	// Historical ^IRX closes (fractions) — pins OptionMath.RiskFreeRate per trading day so the whole
	// engine (quote-source IV back-solve + scorer BS/greeks) prices at the rate that prevailed that day.
	private readonly IReadOnlyList<(DateTime Date, double Rate)>? _rateHistory;
	// Layer-2 expiry-day regime store (campaign: gex_layers) — populated per step for tickers whose
	// short leg expires that day, read by CloseBeforeShortExpiryRule. Inert while the knob is off.
	private readonly Rules.ExpiryRegimeProvider _expiryRegimes = new();
	/// <summary>Peak marks of <see cref="Rules.TrailingStopRule"/>, shared by the rule the evaluator runs on the multi-day walks and the
	/// one the 0DTE walk runs itself. In memory: a backtest's peaks die with the run.</summary>
	private readonly Rules.TrailingStopStore _trailingStops = new();
	/// <summary>The session minute (ET) up to which today's intraday management has run (<see cref="ManageTodayThroughAsync"/>); minutes before it
	/// are already managed. <see cref="DateTime.MinValue"/> at the start of each step.</summary>
	private DateTime _managedThroughEt;
	/// <summary>Lineages a 0DTE walk rule consumed for the day without closing them (a leg-in or condor completion: the result rides to expiry),
	/// so a later stretch of the same session's walk leaves them alone.</summary>
	private readonly HashSet<long> _zeroDteConsumed = new();
	/// <summary>Lineages a management minute walk acted on today (a close, roll or leg-in). The walk runs in stretches up to each opener
	/// decision, and a roll or leg-in re-keys the position, so without this a later stretch would pick the re-keyed position up and manage
	/// it again the same day — a result that would depend on where the opener's decision minutes split the session.</summary>
	private readonly HashSet<long> _walkConsumed = new();

	private readonly int _cascadeCadenceMinutes;
	private readonly bool _cascadeRecache;
	/// <summary>Each session's cascade readings, replayed in the background ahead of the day loop (<see cref="StartCascadeReplay"/>); null when <c>opener.cascade</c> is off.</summary>
	private Dictionary<DateTime, TaskCompletionSource<IReadOnlyList<Analyze.CascadeMoment>>>? _cascadeReads;
	private Open.Cascade.CascadeOpener? _cascadeOpener;
	private Open.Cascade.CascadeSignalLedger? _cascadeLedger;
	/// <summary>Cascade trades the opener proposed that the book did not take, by setup (and neutral side), with the reason (sized to zero, refused by the book, the daily cap).</summary>
	private readonly List<(Analyze.CascadeRegime Setup, string? Side, string Reason)> _cascadeUnfilled = new();
	/// <summary>The reading the cascade opener's reader hands back: set by the day loop just before each evaluation.</summary>
	private Analyze.CascadeLiveRead? _cascadeRead;
	/// <summary>Today's cascade readings (null without any), the next one <see cref="AdvanceDayThroughAsync"/> evaluates, and today's
	/// cascade opens so far (for live's daily cap).</summary>
	private IReadOnlyList<Analyze.CascadeMoment>? _cascadeToday;
	private int _cascadeNext;
	private int _cascadeOpenedToday;
	// Proposal-replay mode (--proposals): non-null replaces the opener entirely — each day's recorded live
	// proposal opens are booked at their recorded submit prices, then managed by the normal rule engine.
	private readonly IReadOnlyDictionary<DateTime, List<ProposalReplayOpen>>? _replayOpensByDate;
	private int _noArbClampedFills;   // multi-leg fills whose summed leg prices fell outside the structure's expiry-payoff range (see NoArb)
	private int _droppedRuleActions;   // rule-proposed Close/Roll/LegIn that never filled: unfillable at the 09:30 pass AND never filled by the minute walk (see ReportUnfilledRuleActions), or an unpriceable partial-expiry survivor
	private readonly Dictionary<string, string> _unfilledRuleActions = new(StringComparer.OrdinalIgnoreCase);   // position key → why its 09:30 action couldn't fill; cleared when any later action fills it that session
	private int _blindPositionDays;    // position-days where NO leg had a quote, so rules could not evaluate the position at all
	private readonly HashSet<long> _openFallbackLineages = new();   // window-end lineages with >=1 leg marked at entry price (no NBBO) — unrealized not fully real-priced

	// Conceptual fill times within a trading day, stamped with start-of-minute labels — the
	// start-of-bar convention: a bar at 09:30:00 covers the 09:30→09:31 ET minute and contains the
	// auction-cleared open price. A fill labeled minute M prices off the quote store's row M, the book
	// struck at M+1:00 (QuoteStoreCache.BookInstant); spot, TTE and tape/VIX for that minute are read at
	// the same instant (bar M's close). Webull natively stamps end-of-bar (09:31:00) but we normalize that
	// to start-of-bar at parse time in `WebullChartsClient` so the entire codebase agrees with
	// Polygon, ToS, and TradingView. Expirations settle at bar.Close intrinsic, stamped at 16:00 ET.
	private static readonly TimeSpan MarketOpenTime = TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30);
	private static readonly TimeSpan MarketCloseTime = TimeSpan.FromHours(16);

	public BacktestRunner(AIConfig config, SimulatedBook book, BacktestPositionSource positions, IBacktestQuoteSource quotes, HistoricalBarCache bars, HistoricalPriceCache closeCache, int topNPerStep, bool oracle = false, bool profile = false, int? fixedContracts = null, string pricingMode = SuggestionPricing.Mid, decimal? cross = null, int scanStride = 1, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividendsByRoot = null, bool splitStructures = false, IReadOnlyList<ProposalReplayOpen>? replayOpens = null, IReadOnlyList<(DateTime Date, double Rate)>? rateHistory = null, IntradayBarCache? intradayBars = null, int cascadeCadenceMinutes = 10, bool cascadeRecache = false)
	{
		_rateHistory = rateHistory;
		_replayOpensByDate = replayOpens?.GroupBy(o => o.OpenEt.Date).ToDictionary(g => g.Key, g => g.OrderBy(o => o.OpenEt).ToList());
		_config = config;
		_cross = cross ?? (SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? 1m : 0m);
		_scanStride = Math.Max(1, scanStride);
		_cascadeCadenceMinutes = Math.Max(1, cascadeCadenceMinutes);
		_cascadeRecache = cascadeRecache;
		_splitStructures = splitStructures;
		_dividendsByRoot = dividendsByRoot;
		_book = book;
		_positions = positions;
		_quotes = quotes;
		_bars = bars;
		_closeCache = closeCache;
		_topNPerStep = topNPerStep;
		_oracle = oracle;
		_profile = profile;
		_fixedContracts = fixedContracts;
		// Disk-only cache for backtest: the fetcher returns empty so any missing minute file fails
		// closed (we fall back to the single 09:30 fill path). The on-disk read path serves the
		// backfilled data/intraday/<TICKER>/<date>.csv files that the Polygon-mirror import created.
		_intradayBars = intradayBars ?? new IntradayBarCache(NoopIntradayFetcher);
	}

	internal static Task<IReadOnlyList<MinuteBar>> NoopIntradayFetcher(string ticker, BarInterval interval, int count, bool includeExtended, CancellationToken cancellation)
		=> Task.FromResult<IReadOnlyList<MinuteBar>>(Array.Empty<MinuteBar>());

	/// <param name="onStep">Optional progress callback invoked after each trading day completes, with
	/// (daysDone, totalDays, day). The command wires it to an inline Spectre progress bar.</param>
	public async Task<BacktestResult> RunAsync(DateTime since, DateTime until, CancellationToken cancellation, Action<int, int, DateTime>? onStep = null)
	{
		var tickerSet = _config.TickerSet();
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(_config, expiryRegimes: _expiryRegimes, trailingStops: _trailingStops), _config);
		var openEvaluator = new OpenCandidateEvaluator(_config, _quotes, SuggestionPricing.Mid, _closeCache, backtestMode: true, dividendsByRoot: _dividendsByRoot);

		var equityCurve = new List<(DateTime Date, decimal Equity)>();
		var startingCash = _book.Cash;
		var peakEquity = startingCash;
		var maxDrawdown = 0m;
		var maxDrawdownPct = 0m;
		var steps = EnumerateTradingDays(since, until).ToList();
		// The scorer-driven opener has nothing to do when no structure is enabled (a layer of signal-driven trades only).
		var scoredOpener = _config.Opener.Structures.EnabledDteRanges().Any();
		if (_config.Opener.Enabled && _config.Opener.Cascade.Enabled && _replayOpensByDate == null) StartCascadeReplay(steps, cancellation);

		// Per-step timing diagnostic. Enabled via --profile on `wa ai backtest`; off otherwise (zero
		// overhead in normal runs). Surfaces where wall time is going across the simulator's loop —
		// useful when a recent change makes the backtest noticeably slower than before.
		var profile = _profile;
		var swTotal = profile ? System.Diagnostics.Stopwatch.StartNew() : null;
		long msStep1 = 0, msStep3 = 0, msTriggers = 0, msMtm = 0;
		var done = 0;

		foreach (var step in steps)
		{
			cancellation.ThrowIfCancellationRequested();
			// The day's risk-free rate = the prior ^IRX close (the value knowable at this open). Absent
			// series/coverage keeps the current value rather than silently reverting to a constant.
			if (_rateHistory != null && HistoricalRateCache.RateOn(_rateHistory, step) is double dayRate)
				Pricing.OptionMath.RiskFreeRate = dayRate;
			// The trading-time clock as calibrated through the prior session (a fit is only used after its last session).
			Pricing.MarketClock.PinnedDay = DateOnly.FromDateTime(step);
			_managedThroughEt = DateTime.MinValue;
			_zeroDteConsumed.Clear();
			_walkConsumed.Clear();
			var swSection = profile ? System.Diagnostics.Stopwatch.StartNew() : null;

			// Step 1: management rules. Run BEFORE settlement so CloseBeforeShortExpiryRule (and
			// any rule that checks DTE==0) can fire on the actual expiry day. Otherwise the position
			// is gone by the time rules see it and ITM shorts go straight to assignment.
			var openPositions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			if (openPositions.Count > 0)
			{
				// Layer-2: classify the expiry-day session (one probe fetch per ticker with a short dying
				// today) before any rule evaluation, so the 09:30 pass and the minute walk read one regime.
				await Rules.ExpiryRegimeHost.PopulateAsync(_expiryRegimes, _config.Rules.CloseBeforeShortExpiry.Regime, openPositions.Values, _quotes, step, cancellation);
				var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
				var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, step, _quotes, _config, cancellation);
				// A position whose legs have NO quotes today leaves every management rule blind: nothing can price a
				// close, and value-gated rules (TakeProfit, CloseBeforeShortExpiry's minProfitPct) silently decline
				// to propose. Happens when the store lacks the day — today's session before the evening NBBO pull,
				// or a historical gap. Warn per position-day so a missing exit reads as a data gap, not a rule decision.
				foreach (var pos in openPositions.Values)
				{
					if (pos.Legs.Count > 0 && pos.Legs.All(l => !quoteSnapshot.Options.ContainsKey(l.Symbol)))
					{
						_blindPositionDays++;
						Console.WriteLine($"⚠ {step:yyyy-MM-dd}: no option quotes for {pos.Ticker} {pos.Key} — management rules are blind for this position today (quote-store gap; today's session lands with the evening pull, historical gaps need a backfill).");
					}
				}
				var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);
				var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
				var results = evaluator.Evaluate(ctx);

				ApplyManagementResults(step, results, openPositions, quoteSnapshot.Options, quoteSnapshot.Underlyings, recordDrop: true);
			}

			if (profile) { msStep1 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// The rest of the session runs in time order on one clock (AdvanceDayThroughAsync): the intraday management walk (live
			// re-evaluates every rule each ~60s, so an ITM expiry-day short is force-closed and a TakeProfit fires at the minute it
			// crosses) advances only up to each opener decision, so an open is sized from the cash and checked against the positions the
			// book holds at its minute. Walking the whole session (and settling 16:00 expirations) before the opener let a 09:30 open spend
			// cash from closes later that day: cash went negative on the 09:30 fill (2026-01-29: 21 lots at −$2,634, funded by 09:43–09:48
			// TakeProfits) and every compounding size was inflated by same-day future proceeds.
			_cascadeToday = _cascadeReads != null && _cascadeReads.TryGetValue(step.Date, out var cascadePending) ? await cascadePending.Task : null;
			_cascadeNext = 0;
			_cascadeOpenedToday = 0;

			// Step 2: opener — scan intraday minute by minute. Previously fired once at 09:30 ET
			// because there was no minute data to anchor any other moment; with backfilled
			// data/intraday/<TICKER>/<date>.csv we can now re-evaluate at each minute and let the
			// existing score gate (cfg.MinScoreToOpen) decide when conditions warrant a fill. Top-N
			// per step caps the number of opens (typically 1 for SPXW); first proposal that passes
			// score + cash + qty wins the day. If no minute crosses, the day skips.
			// Proposal-replay mode (--proposals) replaces this step entirely: the day's live-recorded proposal
			// opens book at their recorded time, qty, and submit price — no scoring, sizing, or cash gate (the
			// live sim already decided those) — and the rules/triggers/settlement below manage them as usual.
			// EXCEPTION: --lots N (fixedContracts) overrides the recorded qty so a replay run can be compared
			// per-lot / at a fixed size to a normal backtest (else the live 6-11 lot size dwarfs a --lots 1 run).
			if (_replayOpensByDate != null)
			{
				if (_replayOpensByDate.TryGetValue(step.Date, out var todaysOpens))
					foreach (var o in todaysOpens)
					{
						await AdvanceDayThroughAsync(step, evaluator, tickerSet, o.OpenEt, cancellation);
						OpenLegsIntoBook(o.OpenEt, o.Ticker, o.StructureKind, o.Legs, _fixedContracts ?? o.Qty, o.Spot, o.RawScore, o.FinalScore, o.RepIv, o.ApplySlippage);
					}
			}
			else if (scoredOpener)
			{
				var openedAtMinute = await TryOpenAcrossDayAsync(step, tickerSet, evaluator, openEvaluator, cancellation);
				// Fallback: if there is no intraday data for this day (dates before the massive.com/Polygon
				// tape, which covers SPY from 2021-07-19, or any future hole), use the legacy 09:30 single-call path.
				if (!openedAtMinute.HasIntraday)
				{
					var openProposals = openedAtMinute.LegacyProposals;
					// Selection must not be dictated by affordability: consider only the top-N by score and
					// skip any we can't afford — never fall through to a cheaper, lower-ranked substitute.
					foreach (var p in openProposals.Take(_topNPerStep))
					{
						var qty = ResolveOpenQty(p);
						if (qty < 1) continue;
						OpenProposalIntoBook(step, p, qty);
					}
				}
			}

			if (profile) { msStep3 += swSection!.ElapsedMilliseconds; swSection.Restart(); }

			// The rest of the session: the cascade readings and management minutes the opener scan has not reached. Closes here happen
			// BEFORE settlement, so an expiry-day short that goes ITM intraday is closed the way live would close it rather than riding
			// to a 16:00 intrinsic settle, and a 0DTE position that stopped intraday doesn't double-count at expiration.
			await AdvanceDayThroughAsync(step, evaluator, tickerSet, step.Date.Add(MarketCloseTime), cancellation);
			ReportUnfilledRuleActions(step);

			// Settle anything still in the book that has reached expiry: either no rule decided to close it, or its short legs are OTM
			// and harmless to let expire worthless. A position opened today whose short expires today settles here too.
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
				// Percent DD against the peak IN FORCE at the time, tracked separately from the dollar max —
				// the two can peak at different points. Dividing the dollar max by the END-OF-WINDOW peak
				// (the old renderer behavior) hid early-window pain entirely: the 2022-01..2026-07 DC run
				// printed 18.4% while the actual 2022 path sat ~60% under its $50k starting peak.
				var ddPct = peakEquity > 0m ? dd / peakEquity : 0m;
				if (ddPct > maxDrawdownPct) maxDrawdownPct = ddPct;
			}

			if (profile) msMtm += swSection!.ElapsedMilliseconds;
			onStep?.Invoke(++done, steps.Count, step);
		}

		if (profile)
		{
			var total = swTotal!.ElapsedMilliseconds;
			Console.WriteLine();
			Console.WriteLine($"[profile] backtest wall time: {total:N0} ms ({steps.Count} trading days, {(double)total / Math.Max(1, steps.Count):F1} ms/day avg)");
			Console.WriteLine($"[profile]   step 1 (rules):              {msStep1:N0} ms ({(double)msStep1 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   opener (+ mgmt to its minute): {msStep3:N0} ms ({(double)msStep3 * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   rest of session + settle:    {msTriggers:N0} ms ({(double)msTriggers * 100 / Math.Max(1, total):F1}%)");
			Console.WriteLine($"[profile]   end-of-step MTM:             {msMtm:N0} ms ({(double)msMtm * 100 / Math.Max(1, total):F1}%)");
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
			LegInFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.LegIn),
			ExpireFills: _book.Fills.Count(f => f.Kind == BacktestFillKind.Expire),
			MaxDrawdown: maxDrawdown,
			MaxDrawdownPct: maxDrawdownPct,
			PeakEquity: peakEquity,
			EquityCurve: equityCurve,
			// Chronological, not append order: the day loop books carry-over management closes (Step 1.5 minute
			// walk) before the day's opens (Step 3), so a 15:30 Close lands in _fills ahead of that day's 09:31
			// Open. Every consumer that walks Fills sequentially (fill-ledger running cash, --fills-jsonl,
			// --book-cmd) expects time order; OrderBy is stable so same-timestamp fills keep append order.
			Fills: _book.Fills.OrderBy(f => f.Date).ToList(),
			EndMtmByLineage: endMtmByLineage,
			Provenance: ComputeProvenance(),
			Cleanliness: ComputeCleanliness(endMtmByLineage.Keys),
			DroppedRuleActions: _droppedRuleActions,
			NoArbClampedFills: _noArbClampedFills,
			BlindPositionDays: _blindPositionDays,
			OpenPositionsFallbackMarked: _openFallbackLineages.Count,
			LegCollisionBlockedOpens: _book.LegCollisionBlockedOpens);
	}

	/// <summary>Executes management-rule proposals (Close/Roll/LegIn) against the book at <paramref name="ts"/>,
	/// pricing fills from <paramref name="quotes"/> under the active pricing mode. Shared by the daily 09:30 rule
	/// pass and the intraday management minute-walk so both paths fill identically. Returns the keys of positions
	/// an action was actually executed for. <paramref name="recordDrop"/> is true for the daily pass: an unfillable
	/// action there is recorded, not printed, because the minute walk retries it every minute after — only one that
	/// is still unfilled when the walk ends is a real drop (see <see cref="ReportUnfilledRuleActions"/>). The minute
	/// walk passes false (it IS the retry); any action it executes clears the position's recorded drop.</summary>
	private HashSet<string> ApplyManagementResults(DateTime ts, IReadOnlyList<RuleEvaluator.EvaluationResult> results, IReadOnlyDictionary<string, OpenPosition> openPositions, IReadOnlyDictionary<string, OptionContractQuote> quotes, IReadOnlyDictionary<string, decimal> underlyings, bool recordDrop)
	{
		var acted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var r in results)
		{
			var p = r.Proposal;
			if (!openPositions.TryGetValue(p.PositionKey, out var pos)) continue;
			if (p.Kind == ProposalKind.Close)
			{
				var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quotes, p.Marketable);
				if (legFills != null) { _book.Close(ts, p.PositionKey, legFills, p.Rule, underlyings.GetValueOrDefault(pos.Ticker)); acted.Add(p.PositionKey); }
				else if (recordDrop) RecordUnfilledRuleAction(p, pos, quotes);
			}
			else if (p.Kind == ProposalKind.Roll)
			{
				var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quotes);
				if (legFills != null) { _book.Roll(ts, p.PositionKey, legFills, p.Rule, underlyings.GetValueOrDefault(pos.Ticker)); acted.Add(p.PositionKey); }
				else if (recordDrop) RecordUnfilledRuleAction(p, pos, quotes);
			}
			else if (p.Kind == ProposalKind.LegIn)
			{
				var legFills = BuildLegFillsFromQuotes(p.Legs, pos.Quantity, quotes);
				if (legFills == null && recordDrop) RecordUnfilledRuleAction(p, pos, quotes);
				// Rule emits the new structure name via convention: LongCall→LongCallVertical, LongPut→LongPutVertical.
				// Derive from the existing strategy + the fact that the new leg is opposite-side.
				if (legFills != null)
				{
					var newStructure = string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongCallVertical
						: string.Equals(pos.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongPutVertical
						// CompleteCondorRule adds the opposite-side vertical to a held short vertical → iron condor.
						: string.Equals(pos.StrategyKind, "ShortPutVertical", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.IronCondor
						: string.Equals(pos.StrategyKind, "ShortCallVertical", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.IronCondor
						: (OpenStructureKind?)null;
					if (newStructure != null) { _book.LegIn(ts, p.PositionKey, legFills, p.Rule, newStructure.Value, underlyings.GetValueOrDefault(pos.Ticker)); acted.Add(p.PositionKey); }
				}
			}
			// AlertOnly: noop in backtest.
		}
		foreach (var key in acted) _unfilledRuleActions.Remove(key);
		return acted;
	}

	/// <summary>Records why the 09:30 pass couldn't fill a rule's action. Not printed here: the minute walk retries
	/// from 09:31, and an opening-minute market that is merely too wide usually fills minutes later — printing
	/// "position left open" at 09:30 read as the position riding to expiry when it closed at 09:49.</summary>
	private void RecordUnfilledRuleAction(ManagementProposal p, OpenPosition pos, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		// Distinguish the two reasons BuildLegFillsFromQuotes returns null: a genuine store gap (no quote
		// at all) vs a leg whose market is too wide to trust — the two need different messages, and
		// printing "NO quotes exist" for a leg that has a perfectly real, just-too-wide quote is misleading.
		var wideLeg = p.Legs.FirstOrDefault(l => quotes.TryGetValue(l.Symbol, out var q) && q.Bid is > 0m && q.Ask is > 0m
			&& !CandidateScorer.PassesFillSpreadGate(new[] { l.Symbol }, quotes, _config.Opener.Liquidity.MaxFillSpreadPctOfMid, _config.Opener.Liquidity.MinAbsFillSpreadDollars));
		if (wideLeg != null)
		{
			var q = quotes[wideLeg.Symbol];
			var spread = q.Ask!.Value - q.Bid!.Value;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			_unfilledRuleActions[p.PositionKey] = $"{p.Rule} proposed {p.Kind} for {pos.Ticker} {p.PositionKey} at 09:30 but {wideLeg.Symbol}'s market ({q.Bid}/{q.Ask}, {spread / mid:P0} of mid) was too wide to trust as a fill, and no later minute filled it";
		}
		else
		{
			_unfilledRuleActions[p.PositionKey] = $"{p.Rule} proposed {p.Kind} for {pos.Ticker} {p.PositionKey} at 09:30 but NO option quotes exist for its legs, and no later minute filled it (store gap: today's quotes land with the evening pull; historical gaps need a backfill)";
		}
	}

	/// <summary>Warns for each 09:30 action the minute walk never filled either, while the position is still open —
	/// the real drop: it heads into settlement (an expiring short settles at intrinsic) or the next day unmanaged.
	/// Runs after the carry-over walk and before <see cref="SettleExpirationsAsync"/>.</summary>
	private void ReportUnfilledRuleActions(DateTime step)
	{
		foreach (var (key, reason) in _unfilledRuleActions)
		{
			if (!_book.OpenPositions.ContainsKey(key)) continue;
			_droppedRuleActions++;
			Console.WriteLine($"⚠ {step:yyyy-MM-dd}: {reason} — action dropped, position left open.");
		}
		_unfilledRuleActions.Clear();
	}

	/// <summary>Per-lineage MTM of still-open positions at the final step. Used by the renderer to split
	/// realized from unrealized P&L. Returns the same numbers <see cref="ComputeOpenMarkAsync"/> would
	/// have summed, but bucketed by lineage so we can attribute each to a lifecycle.</summary>
	private async Task<IReadOnlyDictionary<long, decimal>> ComputeOpenMarkPerLineageAsync(DateTime step, CancellationToken cancellation)
	{
		var byLineage = new Dictionary<long, decimal>();
		if (_book.OpenPositions.Count == 0) return byLineage;

		var symbols = _book.OpenPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var tickers = _book.OpenPositions.Values.Select(p => p.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var snap = await _quotes.GetQuotesAsync(step, symbols, tickers, cancellation);

		// Entry (open-fill) price per (LineageId, Symbol) — the fallback mark for a leg with no NBBO at the
		// window end. Previously a single missing near-money quote set allLegsPriced=false and zeroed the WHOLE
		// position (EndMtm dropped to 0 → unrealized booked the full debit as a loss, discarding the value of
		// the well-priced legs). Now each leg marks independently: real NBBO if present, else its entry price
		// (assume unchanged since open — approximate but bounded), and the lineage is flagged so the summary can
		// warn that its unrealized is not fully real-priced. A leg with neither NBBO nor an entry marks at 0.
		// Keyed by lineage, not PositionKey: a partial-expiry survivor is re-keyed with no fill under the new
		// key, but its lineage — and its legs' open fills — are unchanged.
		var entryPriceByLineageSymbol = _book.Fills
			.Where(f => f.Kind == BacktestFillKind.Open)
			.SelectMany(f => f.Legs.Select(l => (f.LineageId, l.Symbol, l.PricePerShare)))
			.GroupBy(x => (x.LineageId, x.Symbol))
			.ToDictionary(g => g.Key, g => g.First().PricePerShare);

		foreach (var pos in _book.OpenPositions.Values)
		{
			// The book's own key→lineage map, NOT a fills-derived join: a partial-expiry survivor's re-keyed
			// position has no fill under its new key, and dropping it here excluded the survivor from
			// EndMtmByLineage — which both zeroed its unrealized MTM and let the summary misclassify the
			// half-settled lineage as a fully realized loss.
			var lineage = _book.LineageFor(pos.Key);
			if (lineage == null) continue;
			decimal perContract = 0m;
			var usedFallback = false;
			foreach (var leg in pos.Legs)
			{
				decimal mid;
				if (snap.Options.TryGetValue(leg.Symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue)
					mid = OptionMath.HeldLegMark(q.Bid.Value, q.Ask.Value, leg.Side == Side.Buy);
				else if (entryPriceByLineageSymbol.TryGetValue((lineage.Value, leg.Symbol), out var entry)) { mid = entry; usedFallback = true; }
				else { mid = 0m; usedFallback = true; }
				perContract += leg.Side == Side.Buy ? mid : -mid;
			}
			byLineage[lineage.Value] = perContract * 100m * pos.Quantity;
			if (usedFallback) _openFallbackLineages.Add(lineage.Value);
		}
		return byLineage;
	}

	/// <summary>Execution price for a single leg: the midpoint moved <see cref="_cross"/> of the way to the crossing side
	/// (<c>--pricing mid</c> = 0 fills at the midpoint; <c>bidask</c> = 1 lifts the ask / hits the bid, worst-case marketable
	/// fills; <c>--cross</c> sets anything in between). Multi-leg orders are then held inside their no-arbitrage range by
	/// <see cref="NoArb"/> at the call sites. Slippage (<c>slippagePerSharePerOrder</c>) applies additively
	/// on top in <see cref="SimulatedBook"/>, independent of this. Marks (MTM / threshold detection) always
	/// use mid — only realized fills cross the spread.
	///
	/// <para>A one-sided book (bid or ask genuinely 0 — the far-OTM/expiry-day norm, not a data gap) has no
	/// coherent "mid": blending a live side with an absent one manufactures a price nobody could transact at
	/// in either direction. There, regardless of <c>--pricing</c>, the trade uses the one real side it
	/// actually needs — paying the real ask to buy, receiving the real bid (which can genuinely be $0) to
	/// sell — exactly as <c>bidask</c> mode would. A fully two-sided book still respects the configured
	/// mode.</para></summary>
	private decimal ExecPrice(Side side, decimal bid, decimal ask)
	{
		if (bid <= 0m || ask <= 0m) return side == Side.Buy ? ask : bid;
		return ComboFill.LegPrice((bid + ask) / 2m, side == Side.Buy ? ask : bid, _cross);
	}

	/// <summary>The price a marketable order trades at: the ask to buy, the bid to sell.</summary>
	private static decimal TouchPrice(Side side, decimal bid, decimal ask) => side == Side.Buy ? ask : bid;

	/// <summary>For management proposals (close/roll), re-price each leg under the active pricing mode.
	/// Returns null if any leg lacks a usable quote.</summary>
	/// <summary>Fills every leg from <paramref name="quotes"/> for a rule-driven close/roll/leg-in, or
	/// returns null (no fill — same signal as a missing quote, which the callers already treat as "left
	/// the position open") when any leg's market is too wide to trust as an achievable price. This is the
	/// backtest's analog of a real limit order not filling against a bad print — live needs no equivalent
	/// call, a real broker already won't fill one; see PassesFillSpreadGate's doc for the trade that
	/// motivated this and CandidateScorer.PassesExitNoiseGate's doc for why this belongs in the simulator
	/// rather than in StopLossRule/TakeProfitRule.</summary>
	/// <param name="marketable">The proposal is a marketable close (<see cref="ManagementProposal.Marketable"/> — a stop): live sends it at the side
	/// that trades now, so each leg fills at its touch (sell at the bid, buy at the ask) whatever the pricing mode.</param>
	private IReadOnlyList<BacktestLegFill>? BuildLegFillsFromQuotes(IReadOnlyList<ProposalLeg> legs, int qty, IReadOnlyDictionary<string, OptionContractQuote> quotes, bool marketable = false)
	{
		if (!CandidateScorer.PassesFillSpreadGate(legs.Select(l => l.Symbol), quotes, _config.Opener.Liquidity.MaxFillSpreadPctOfMid, _config.Opener.Liquidity.MinAbsFillSpreadDollars)) return null;
		var fills = new List<BacktestLegFill>(legs.Count);
		foreach (var l in legs)
		{
			if (!quotes.TryGetValue(l.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return null;
			var side = string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
			fills.Add(new BacktestLegFill(l.Symbol, side, qty, marketable ? TouchPrice(side, q.Bid.Value, q.Ask.Value) : ExecPrice(side, q.Bid.Value, q.Ask.Value)));
		}
		return NoArb(fills);
	}

	/// <summary>Resolves the contract quantity for an open. Normal mode uses the evaluator's
	/// cash/risk-scaled <c>Qty</c> and honors the cash-reserve and free-cash gates (returns 0 to skip).
	/// Fixed-lots mode (<c>--lots N</c>) overrides to a flat N every trade and ignores cash entirely:
	/// terminal P&L becomes the additive sum of per-trade results instead of a compounding curve, and
	/// every endorsed signal trades regardless of bankroll — so a sweep measures per-trade edge, not the
	/// position-sizing feedback loop (which otherwise compounds a small base until it dwarfs the signal,
	/// or overflows).</summary>
	private int ResolveOpenQty(OpenProposal p)
	{
		if (_fixedContracts.HasValue) return _fixedContracts.Value;
		if (p.CashReserveBlocked) return 0;
		if (p.Qty < 1) return 0;
		// Buying power a new contract needs: its margin (MarginCalculator — the collateral, e.g. a credit vertical's width or an inverted
		// diagonal's strike loss) plus the cash the book takes for it — the debit at the price it fills at, slippage and fees, exactly as
		// SimulatedBook.Open charges them (the scorer's mid debit left those out, so a cap sized to the last dollar overdrew cash on the
		// fill); a net credit received lands in cash. Existing positions hold their own margin, which the cash balance doesn't reflect
		// (a debit already left cash; collateral never did). The backtest scores one ticker, so the proposal's spot prices any naked short.
		if (BuildLegFillsFromProposal(p.Legs, 1) is not { } fillsForOne) return 0;
		var marginPerContract = MarginCalculator.Requirement(MarginLegs(p.Legs.Select(l => (l.Symbol, string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase), l.Qty)), p.Legs), p.Spot).Total / Math.Max(1, p.Qty)
			+ Math.Max(0m, _book.OpenCashOutPerContract(p.StructureKind, fillsForOne, applySlippage: true));
		if (marginPerContract <= 0m) return 0;
		var marginHeld = 0m;
		foreach (var pos in _book.OpenPositions.Values)
			marginHeld += MarginCalculator.Requirement(MarginLegs(pos.Legs.Select(l => (l.Symbol, l.Side == Side.Buy, l.Qty)), null), p.Spot).Total;
		var buyingPower = _book.Cash - marginHeld;
		var affordableQty = (int)Math.Floor(buyingPower / marginPerContract);
		var qty = Math.Min(p.Qty, affordableQty);
		return qty < 1 ? 0 : qty;
	}

	/// <summary>Option legs as <see cref="MarginCalculator"/> takes them; a short's premium comes from its proposal leg when there is one.</summary>
	private static List<MarginLeg> MarginLegs(IEnumerable<(string Symbol, bool IsLong, int Qty)> legs, IReadOnlyList<ProposalLeg>? priced) =>
		legs.Select(l => (l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.Where(x => x.Parsed != null)
			.Select(x => new MarginLeg(x.Parsed!, x.l.Qty, x.l.IsLong, priced?.FirstOrDefault(pl => pl.Symbol == x.l.Symbol)?.PricePerShare ?? 0m))
			.ToList();

	/// <summary>For opener proposals, the candidate scorer has already priced each leg from the same quote
	/// source: <c>PricePerShare</c> is the mid, <c>ExecutionPricePerShare</c> the conservative cross-the-spread
	/// fill (ask on buys, bid on sells). Under <c>--pricing bidask</c> we fill at the latter; under <c>mid</c>
	/// at the former. Candidate selection/scoring is unaffected — only the fill price changes.</summary>
	/// <summary>Books a proposal into the simulated book the way the broker would hold it. Whole mode
	/// (default) keeps all legs as ONE position — the historical simulator behavior, where management
	/// rules see the structure's combined mark and debit. Split mode (--split) mirrors live execution:
	/// the two combo orders from <see cref="StructureOrderSplit"/> become two independent positions
	/// (Webull has no composite position concept), so each half is managed against its OWN debit and
	/// take-profit/stop-loss fire per half. Friction and fees are identical across modes — OrdersForStructure
	/// already charges 2-order structures per order, and per-leg fees sum the same — so an A/B isolates the
	/// exit-policy granularity. All halves must price or nothing books (live validates the whole leg set the
	/// same way). Returns true when at least one position was opened.</summary>
	private bool OpenProposalIntoBook(DateTime when, OpenProposal p, int qty)
		=> OpenLegsIntoBook(when, p.Ticker, p.StructureKind, p.Legs, qty, p.Spot, p.RawScore, p.FinalScore, p.ImpliedVolatilityAnnual, applySlippage: true, p.Breakevens);

	private bool OpenLegsIntoBook(DateTime when, string ticker, OpenStructureKind structureKind, IReadOnlyList<ProposalLeg> legs, int qty, decimal spot, decimal? rawScore, decimal? finalScore, decimal? repIv, bool applySlippage, IReadOnlyList<decimal>? breakevens = null)
	{
		// Mirrors the live executor's held-position stance (OpenerAutoExecutor): allowAddToHeldPosition=true
		// (the default) lets an exact re-take of a held structure merge in as an add instead of being dropped.
		// Applies to the scored opener AND proposal replay — a replayed live add was recorded because live's
		// executor passed it, so dropping it here silently diverges the books (2026-07-29: live added to its
		// 07-28 diagonal; replay skipped the day entirely).
		var allowAdd = _config.AutoExecute.Opener.AllowAddToHeldPosition;
		var groups = _splitStructures ? StructureOrderSplit.Split(structureKind, legs) : null;
		if (groups == null || groups.Count == 1)
		{
			var legFills = BuildLegFillsFromProposal(legs, qty);
			return legFills != null && _book.Open(when, ticker, structureKind, legFills, qty, spot, rawScore, finalScore, repIv, applySlippage, allowAdd, breakevens);
		}

		var groupFills = new List<(OpenStructureKind Kind, IReadOnlyList<BacktestLegFill> Fills)>(groups.Count);
		foreach (var g in groups)
		{
			var fills = BuildLegFillsFromProposal(g.Legs, qty);
			if (fills == null) return false;
			groupFills.Add((HalfStructureKind(structureKind, fills), fills));
		}
		var opened = false;
		foreach (var (kind, fills) in groupFills)
			opened |= _book.Open(when, ticker, kind, fills, qty, spot, rawScore, finalScore, repIv, applySlippage, allowAdd);
		return opened;
	}

	/// <summary>Structure kind of ONE combo order of a split structure. Side-split doubles yield the
	/// single-sided analog (each half is a 2-leg cross-expiry pair). Expiry-split structures
	/// (DiagonalVertical / CalendarVertical) yield same-expiry verticals whose direction follows the
	/// half's net fill: net debit = long vertical, net credit = short vertical.</summary>
	private static OpenStructureKind HalfStructureKind(OpenStructureKind parent, IReadOnlyList<BacktestLegFill> halfFills)
	{
		if (parent == OpenStructureKind.DoubleDiagonal) return OpenStructureKind.LongDiagonal;
		if (parent == OpenStructureKind.DoubleCalendar) return OpenStructureKind.LongCalendar;
		var isCall = halfFills.Any(f => ParsingHelpers.ParseOptionSymbol(f.Symbol)?.CallPut == "C");
		var netPerShare = halfFills.Sum(f => f.Side == Side.Buy ? -f.PricePerShare : f.PricePerShare);
		return netPerShare < 0m
			? (isCall ? OpenStructureKind.LongCallVertical : OpenStructureKind.LongPutVertical)
			: (isCall ? OpenStructureKind.ShortCallVertical : OpenStructureKind.ShortPutVertical);
	}

	/// <summary>Holds a multi-leg order inside its no-arbitrage range (<see cref="ComboFill.ClampToNoArb"/>) and counts the fills
	/// that needed it, so the summary shows how much of a result rests on corrected prices.</summary>
	private IReadOnlyList<BacktestLegFill> NoArb(IReadOnlyList<BacktestLegFill> fills)
	{
		var clamped = ComboFill.ClampToNoArb(fills);
		if (!ReferenceEquals(clamped, fills)) _noArbClampedFills++;
		return clamped;
	}

	private IReadOnlyList<BacktestLegFill>? BuildLegFillsFromProposal(IReadOnlyList<ProposalLeg> legs, int qty)
	{
		var fills = new List<BacktestLegFill>(legs.Count);
		foreach (var l in legs)
		{
			if (!l.PricePerShare.HasValue) return null;
			var price = l.ExecutionPricePerShare is decimal crossing ? ComboFill.LegPrice(l.PricePerShare.Value, crossing, _cross) : l.PricePerShare.Value;
			var side = string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
			fills.Add(new BacktestLegFill(l.Symbol, side, qty, price));
		}
		return NoArb(fills);
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
	/// <summary>True when every expiring leg of <paramref name="pos"/> expires on or before
	/// <paramref name="step"/> (DTE ≤ 0) — i.e. the position carries no leg that lives past today.
	/// Only such positions are eligible for the SL/TP intraday-trigger minute-walk, whose parametric leg
	/// pricing (<see cref="BacktestQuoteSource.PriceAtSpot"/>) is consistent with the captured-bar
	/// entry pricing only for short-TTE near-intrinsic 0DTE legs. A position with any future-dated leg
	/// (calendar / diagonal long leg) is managed by the daily rule loop plus
	/// <see cref="RunManagementMinuteWalkAsync"/>, both of which price on the same captured-bar basis as entry.</summary>
	private static bool IsAllZeroDte(OpenPosition pos, DateTime step)
	{
		foreach (var leg in pos.Legs)
		{
			if (leg.Expiry is { } exp && (exp.Date - step.Date).Days > 0) return false;
		}
		return true;
	}

	/// <summary>Intraday management for multi-DTE positions — the backtest analog of the live watch tick.
	/// Live (`wa ai watch` → <see cref="LiveTick"/>) re-runs the full <see cref="RuleEvaluator"/> every ~60s,
	/// so an expiry-day short that goes ITM at 11:00 is force-closed for assignment risk and a position that
	/// crosses the TakeProfit target at 14:00 exits at 14:00; a once-daily 09:30 pass sees neither. Walks each
	/// RTH minute after the daily pass, re-prices positions through the same captured-NBBO path the daily pass
	/// and entry use (<see cref="AIPipelineHelper.FetchQuotesWithHypotheticals"/> with a minute asOf), evaluates
	/// the same rule set, and executes proposals via <see cref="ApplyManagementResults"/>. Multi-DTE positions
	/// were historically excluded from ALL intraday triggering because the old walk priced legs parametrically
	/// and manufactured phantom same-day profits on calendars/diagonals; the quotes-only pivot removed that
	/// divergence — marks here are real minute NBBO, the same basis as entry. Days with no minute tape keep the
	/// daily-pass-only behavior. A position that fires any rule is consumed for the day (same semantics as the
	/// 0DTE walk; live would keep managing a rolled position intraday — the next daily pass picks it up).
	/// <paramref name="openedToday"/> selects the population so the two walks never overlap: false = carry-over positions, walked from
	/// the minute after the daily rule pass; true = positions opened this step, walked from the minute after each fill (live's next tick).
	/// Both run before <see cref="SettleExpirationsAsync"/>, so an expiry-day short is closeable before settlement sweeps it.</summary>
	/// <summary>Today's intraday management through the minutes before <paramref name="untilEt"/>, resuming where the last call stopped:
	/// the minute walk of carry-over multi-DTE positions, the 0DTE trigger walk (intraday SL/TP/trailing/leg-in — the daily-step engine
	/// would otherwise jump a 0DTE position straight from open to expiry), then the minute walk of multi-DTE positions opened today.
	/// The day's openers call it (through <see cref="AdvanceDayThroughAsync"/>) up to each decision minute, so a position a rule closed
	/// earlier in the session is out of the book (cash, held legs) when an opener looks at it, and one a rule closes later is still in it.</summary>
	private async Task ManageTodayThroughAsync(DateTime step, RuleEvaluator evaluator, IReadOnlySet<string> tickerSet, DateTime untilEt, CancellationToken cancellation)
	{
		if (untilEt <= _managedThroughEt) return;
		await RunManagementMinuteWalkAsync(step, evaluator, tickerSet, openedToday: false, _managedThroughEt, untilEt, cancellation);
		await RunIntradayTriggersAsync(step, _managedThroughEt, untilEt, cancellation);
		await RunManagementMinuteWalkAsync(step, evaluator, tickerSet, openedToday: true, _managedThroughEt, untilEt, cancellation);
		_managedThroughEt = untilEt;
	}

	/// <summary>Advances the session to <paramref name="untilEt"/> in time order: each of today's cascade readings whose book minute is
	/// before it is evaluated (and its trade booked) after management has walked up to that minute, then management walks the rest of
	/// the way. The scored opener calls it before each minute it decides on, so its opens and the cascade's interleave on one clock.</summary>
	private async Task AdvanceDayThroughAsync(DateTime step, RuleEvaluator evaluator, IReadOnlySet<string> tickerSet, DateTime untilEt, CancellationToken cancellation)
	{
		while (_cascadeToday != null && _cascadeNext < _cascadeToday.Count)
		{
			var moment = _cascadeToday[_cascadeNext];
			var minuteEt = CascadeBookMinute(step, moment);
			if (minuteEt >= untilEt) break;
			_cascadeNext++;
			await ManageTodayThroughAsync(step, evaluator, tickerSet, minuteEt, cancellation);
			await OpenCascadeReadingAsync(step, tickerSet, moment, minuteEt, cancellation);
		}
		await ManageTodayThroughAsync(step, evaluator, tickerSet, untilEt, cancellation);
	}

	/// <summary>The book minute of a cascade reading of instant T: the quote store's row T−1:00 (<see cref="QuoteStoreCache"/>'s book
	/// instant), which the trade is priced from and stamped at.</summary>
	private static DateTime CascadeBookMinute(DateTime step, Analyze.CascadeMoment moment) => step.Date + moment.TimeEt - TimeSpan.FromMinutes(1);

	/// <param name="fromEt">First minute walked (earlier minutes were walked by a previous call).</param>
	/// <param name="untilEt">Walk stops before this minute.</param>
	private async Task RunManagementMinuteWalkAsync(DateTime step, RuleEvaluator evaluator, IReadOnlySet<string> tickerSet, bool openedToday, DateTime fromEt, DateTime untilEt, CancellationToken cancellation)
	{
		var byTicker = _book.OpenPositions.Values
			.Where(p => !IsAllZeroDte(p, step) && (p.OpenedAt.HasValue && p.OpenedAt.Value.Date == step.Date) == openedToday && !(_book.LineageFor(p.Key) is { } lineage && _walkConsumed.Contains(lineage)))
			.GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (byTicker.Count == 0) return;

		var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);

		foreach (var group in byTicker)
		{
			var ticker = group.Key;
			var pending = group.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var lineageByKey = group.ToDictionary(p => p.Key, p => _book.LineageFor(p.Key), StringComparer.OrdinalIgnoreCase);
			// 09:30 itself belongs to the daily rule pass; the walk owns everything after it.
			var walkStartEt = step.Date.Add(MarketOpenTime).AddMinutes(1);
			var walkEndEt = step.Date.Add(MarketCloseTime);
			var startUtc = MarketTime.EtToInstant(walkStartEt);
			var endUtc = MarketTime.EtToInstant(walkEndEt);
			var minuteBars = await _intradayBars.GetBarsAsync(ticker, startUtc, endUtc, BarInterval.M1, includeExtended: false, cancellation);
			var oneTicker = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker };
			// Look-ahead quote snapshots. A minute's snapshot depends only on the held set and the store, never on the walk's
			// decisions, so a batch of upcoming minutes is priced in parallel while the rules still run strictly minute by
			// minute — the snapshots were this walk's serial cost with most cores idle. Any action (or the book dropping a
			// position) changes the held set, so the batch is discarded and re-priced from the next minute.
			var lookahead = new Task<QuoteSnapshot?>[minuteBars.Count];
			var lookaheadValidFrom = int.MaxValue;
			var batch = Math.Max(1, Environment.ProcessorCount);
			// VIX only feeds WeekendCloseRule's VIX gate here — keep the tape read off the walk otherwise.
			var (vixBars, vixPriorClose) = _config.Rules.WeekendClose is { Enabled: true, MinVixChangePct: > 0m }
				? await LoadVixDayAsync(step, endUtc, cancellation)
				: (Array.Empty<MinuteBar>(), null);
			var vixCursor = 0;

			for (var mwi = 0; mwi < minuteBars.Count; mwi++)
			{
				var minuteBar = minuteBars[mwi];
				cancellation.ThrowIfCancellationRequested();
				if (MinuteEt(minuteBar) >= untilEt) break;
				if (MinuteEt(minuteBar) < fromEt) continue;
				if (pending.RemoveWhere(k => !_book.OpenPositions.ContainsKey(k)) > 0) lookaheadValidFrom = int.MaxValue;
				if (pending.Count == 0) break;
				var minuteEt = MinuteEt(minuteBar);
				var stillOpen = MinutePopulation(pending, minuteEt, openedToday);
				if (stillOpen.Count == 0) continue;

				if (mwi < lookaheadValidFrom || lookahead[mwi] == null)
				{
					var held = pending.ToList();
					// Bounded by untilEt too: the walk advances in short stretches up to each opener decision, and pricing minutes past
					// the stretch would be discarded (and waited on) at its end.
					for (var j = mwi; j < Math.Min(minuteBars.Count, mwi + batch) && MinuteEt(minuteBars[j]) < untilEt; j++)
					{
						var bar = minuteBars[j];
						lookahead[j] = Task.Run(() => MinuteSnapshotAsync(held, bar, ticker, oneTicker, endUtc, openedToday, cancellation), cancellation);
					}
					for (var j = mwi + batch; j < minuteBars.Count; j++) lookahead[j] = null!;
					lookaheadValidFrom = mwi;
				}
				var snap = (await lookahead[mwi])!;

				// Spot at the book instant (bar close = M+1:00, the moment row M's book was struck). The helper's parity
				// spot guard may have corrected the bar spot against the option book (composed-open lag) — adopt its value
				// so rule moneyness matches the quotes being priced.
				var spot = minuteBar.Close;
				if (snap.Underlyings.TryGetValue(ticker, out var snapSpot) && snapSpot > 0m) spot = snapSpot;
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spot };
				var vixNow = CausalVix(vixBars, ref vixCursor, minuteBar.Timestamp + QuoteStoreCache.BookLag, vixPriorClose);
				var ctx = new EvaluationContext(minuteEt, stillOpen, underlyings, snap.Options, cash, accountValue, technicalSignals, vixNow, VixPriorClose: vixPriorClose);
				var results = evaluator.Evaluate(ctx);

				// Fill-realism check on P&L-threshold-driven closes only (StopLossRule / TakeProfitRule):
				// don't honor a crossing unless it still holds one minute later. This belongs here, not in
				// the rule (see project_dc_sweep_tp_noise_artifact.md) — the RULE's job is "does this
				// position's economics justify an exit," which is unconditional and identical for live; the
				// SIMULATOR's job is "would a real order actually have filled at this print," which live gets
				// for free from the broker (a bad quote just doesn't fill) and backtest has to model itself.
				// A same-minute noise-ratio check (PassesExitNoiseGate) doesn't catch this failure mode — the
				// confirming minute's OWN spread can look perfectly ordinary in isolation; only comparing
				// against the ADJACENT minute exposes a print that didn't persist. A confirmed close fills at the
				// CONFIRMING minute (its quotes, stamped then) — filling at minute M after seeing M+1 agree would be
				// a look-ahead. Everything else (rolls, leg-ins, other closes) passes through immediately at M.
				var (now, atNext) = results.Count == 0 || mwi + 1 >= minuteBars.Count
					? (results, Array.Empty<ConfirmedClose>()) // nothing to filter, or no next bar to confirm against (last bar of
					                                           // the day — fire unconfirmed rather than silently suppress every end-of-day exit)
					: await ConfirmPnlClosesAsync(results, stillOpen, ticker, evaluator, minuteBars[mwi + 1], endUtc, cash, accountValue, technicalSignals, cancellation);

				if (now.Count > 0 || atNext.Count > 0)
				{
					// In-flight look-ahead reads the positions an action is about to mutate; let it finish first (its
					// results are discarded below either way).
					await Task.WhenAll(lookahead.Where(t => t != null)).ContinueWith(_ => { }, TaskScheduler.Default);
					lookaheadValidFrom = int.MaxValue;
				}
				pending.ExceptWith(ConsumeForDay(ApplyManagementResults(minuteEt, now, stillOpen, snap.Options, underlyings, recordDrop: false), lineageByKey));
				foreach (var c in atNext)
					if (pending.Contains(c.Result.Proposal.PositionKey)) // not already acted on at M (ApplyManagementResults reads the M snapshot)
						pending.ExceptWith(ConsumeForDay(ApplyManagementResults(c.MinuteEt, new[] { c.Result }, stillOpen, c.Quotes, c.Underlyings, recordDrop: false), lineageByKey));
			}
			// Look-ahead snapshots past where the walk stopped read the book; settle them before the caller (an opener, the next group) changes it.
			await Task.WhenAll(lookahead.Where(t => t != null)).ContinueWith(_ => { }, TaskScheduler.Default);
		}
	}

	/// <summary>Marks the lineages of the positions a walk acted on as consumed for the day (<see cref="_walkConsumed"/>); returns the keys.</summary>
	private HashSet<string> ConsumeForDay(HashSet<string> acted, IReadOnlyDictionary<string, long?> lineageByKey)
	{
		foreach (var key in acted)
			if (lineageByKey.GetValueOrDefault(key) is { } lineage) _walkConsumed.Add(lineage);
		return acted;
	}

	/// <summary>The day's RTH VIX minute tape (data/intraday/VIX/) plus the most recent settled close STRICTLY BEFORE
	/// <paramref name="step"/>, walked back 5 days over weekends/holidays (mirroring BacktestIVProvider.GetVixSeriesCloseAsync)
	/// — never the day's own 16:00 close, which is not knowable during the walk (using it let a 10:00 VIX gate "see" a
	/// close-of-day vol spike — lookahead). The prior close is both the missing-tape fallback and the base of the day's move.</summary>
	private async Task<(IReadOnlyList<MinuteBar> Bars, decimal? PriorClose)> LoadVixDayAsync(DateTime step, DateTimeOffset endUtc, CancellationToken cancellation)
	{
		var rthStartUtc = MarketTime.EtToInstant(step.Date.Add(MarketOpenTime));
		var bars = await _intradayBars.GetBarsAsync("VIX", rthStartUtc, endUtc, BarInterval.M1, includeExtended: false, cancellation);
		decimal? priorClose = null;
		for (var i = 1; i <= 5 && priorClose == null; i++)
		{
			var vixBar = await _bars.GetBarAsync("VIX", step.Date.AddDays(-i), cancellation);
			if (vixBar != null) priorClose = vixBar.Close;
		}
		return (bars, priorClose);
	}

	/// <summary>Causal VIX at <paramref name="minuteUtc"/>: advance <paramref name="cursor"/> to the last tape bar at-or-before
	/// the decision minute. A bar stamped exactly at this minute contributes its OPEN (the M:00 print); an earlier bar its
	/// CLOSE (completed before this minute). No bar yet → <paramref name="fallback"/> (the prior settled close).</summary>
	private static decimal? CausalVix(IReadOnlyList<MinuteBar> bars, ref int cursor, DateTimeOffset minuteUtc, decimal? fallback)
	{
		while (cursor < bars.Count && bars[cursor].Timestamp <= minuteUtc) cursor++;
		if (cursor == 0) return fallback;
		var vb = bars[cursor - 1];
		return vb.Timestamp == minuteUtc ? vb.Open : vb.Close;
	}

	private static DateTime MinuteEt(MinuteBar bar) => MarketTime.ToEt(bar.Timestamp).DateTime;

	/// <summary>The same-day (0DTE) pricing window at a minute bar: from the book instant (bar + <see cref="QuoteStoreCache.BookLag"/>) to the
	/// session close, floored at one minute so a deep-OTM 0DTE leg doesn't price exactly intrinsic before the actual close (the
	/// settlement path handles the at-close cash flow). An interval, so each root measures it on its own clock.</summary>
	private static (DateTime From, DateTime To) ZeroDteWindow(DateTimeOffset barUtc, DateTimeOffset closeUtc)
	{
		var close = MarketTime.ToEt(closeUtc).DateTime;
		var book = MarketTime.ToEt(barUtc + QuoteStoreCache.BookLag).DateTime;
		return (book < close.AddMinutes(-1) ? book : close.AddMinutes(-1), close);
	}

	/// <summary>Population at a walk minute: still open, and (for today's opens) strictly past their fill minute — live's first
	/// management look at a fresh open is the tick AFTER the fill.</summary>
	private Dictionary<string, OpenPosition> MinutePopulation(IEnumerable<string> pending, DateTime minuteEt, bool openedToday)
	{
		var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);
		foreach (var key in pending)
		{
			var pos = _book.OpenPositions[key];
			if (openedToday && pos.OpenedAt.HasValue && minuteEt <= pos.OpenedAt.Value) continue;
			stillOpen[key] = pos;
		}
		return stillOpen;
	}

	/// <summary>The walk's quote snapshot at <paramref name="bar"/>'s minute for the held set, or null when no position is
	/// in that minute's population. Spot and TTE are at the book instant (bar close, M+1:00); the TTE override only affects
	/// legs that fall back to parametric pricing (captured NBBO needs neither).</summary>
	private async Task<QuoteSnapshot?> MinuteSnapshotAsync(IReadOnlyList<string> held, MinuteBar bar, string ticker, IReadOnlySet<string> oneTicker, DateTimeOffset endUtc, bool openedToday, CancellationToken cancellation)
	{
		var minuteEt = MinuteEt(bar);
		var stillOpen = MinutePopulation(held, minuteEt, openedToday);
		if (stillOpen.Count == 0) return null;
		var overrides = new QuoteOverrides(
			Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = bar.Close },
			ZeroDteWindow: ZeroDteWindow(bar.Timestamp, endUtc));
		return await AIPipelineHelper.FetchQuotesWithHypotheticals(stillOpen, oneTicker, minuteEt, _quotes, _config, cancellation, overrides);
	}

	/// <summary>A StopLoss/TakeProfit close confirmed at the next minute: the confirming evaluation's own result, to be
	/// filled at that minute — its quotes, spot and timestamp.</summary>
	private sealed record ConfirmedClose(RuleEvaluator.EvaluationResult Result, DateTime MinuteEt, IReadOnlyDictionary<string, OptionContractQuote> Quotes, IReadOnlyDictionary<string, decimal> Underlyings);

	/// <summary>Splits <paramref name="results"/> into what fills now and what a real order would plausibly have
	/// filled one minute later: proposals other than a StopLossRule/TakeProfitRule close pass through untouched
	/// (Now); those two are re-checked by re-evaluating the SAME position against <paramref name="nextBar"/>'s real
	/// quotes and requiring the SAME rule to fire again there — and when it does, the close is the confirming
	/// evaluation's result, filled at that minute (AtNext), since the simulator only knows the print persisted once
	/// the next minute is in hand. A crossing that can't be checked (no legs to price, no next-minute quotes) fills
	/// Now, unconfirmed. Re-invoking the actual rule (rather than
	/// re-deriving its threshold math here) means this can never drift out of sync with what
	/// StopLossRule/TakeProfitRule actually do — including the two rules' own AdjustedNetDebit-vs-
	/// InitialNetDebit difference, which a hand-rolled copy of the formula would risk getting wrong.
	///
	/// <para>Calling <paramref name="evaluator"/> a second time here does touch its internal per-position
	/// dedup fingerprint one minute early, but <c>ApplyManagementResults</c> never consults
	/// <c>EvaluationResult.IsRepeat</c> (it's display-only, read by the proposal sinks) and ONLY when
	/// confirmation SUCCEEDS does the position leave <c>pending</c> — an unconfirmed position is still
	/// re-evaluated for real when the walk reaches <paramref name="nextBar"/>'s minute on its own next
	/// iteration, which simply re-derives the identical fingerprint. Benign, not worth extra machinery to
	/// avoid.</para></summary>
	private async Task<(IReadOnlyList<RuleEvaluator.EvaluationResult> Now, IReadOnlyList<ConfirmedClose> AtNext)> ConfirmPnlClosesAsync(
		IReadOnlyList<RuleEvaluator.EvaluationResult> results, IReadOnlyDictionary<string, OpenPosition> stillOpen, string ticker,
		RuleEvaluator evaluator, MinuteBar nextBar, DateTimeOffset endUtc, decimal cash, decimal accountValue,
		IReadOnlyDictionary<string, TechnicalBias> technicalSignals, CancellationToken cancellation)
	{
		List<RuleEvaluator.EvaluationResult>? confirmed = null; // allocated lazily; the common case (nothing to confirm) copies nothing
		List<ConfirmedClose>? atNext = null;
		for (var i = 0; i < results.Count; i++)
		{
			var r = results[i];
			var isPnlClose = r.Proposal.Kind == ProposalKind.Close
				&& (string.Equals(r.Proposal.Rule, "StopLossRule", StringComparison.Ordinal) || string.Equals(r.Proposal.Rule, "TakeProfitRule", StringComparison.Ordinal) || string.Equals(r.Proposal.Rule, "TrailingStopRule", StringComparison.Ordinal));
			if (!isPnlClose)
			{
				confirmed?.Add(r);
				continue;
			}

			if (confirmed == null) { confirmed = new List<RuleEvaluator.EvaluationResult>(results.Count); for (var j = 0; j < i; j++) confirmed.Add(results[j]); }

			if (!stillOpen.TryGetValue(r.Proposal.PositionKey, out var pos)) continue; // defensive; shouldn't happen
			var symbols = pos.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
			if (symbols.Count == 0) { confirmed.Add(r); continue; } // nothing priced to confirm against — don't block on it

			var nextEt = MarketTime.ToEt(nextBar.Timestamp).DateTime;
			var nextSpot = nextBar.Close;
			var nextOverrides = new QuoteOverrides(
				Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = nextSpot },
				ZeroDteWindow: ZeroDteWindow(nextBar.Timestamp, endUtc));
			var nextQuotes = (await _quotes.GetQuotesAsync(nextEt, symbols, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker }, cancellation, nextOverrides)).Options;
			if (nextQuotes.Count == 0) { confirmed.Add(r); continue; } // no data to confirm against — don't block on a store gap

			var confirmCtx = new EvaluationContext(nextEt,
				new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { [pos.Key] = pos },
				new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = nextSpot },
				nextQuotes, cash, accountValue, technicalSignals);
			var nextResults = evaluator.Evaluate(confirmCtx);
			var confirming = nextResults
				.Where(nr => nr.Proposal.Kind == ProposalKind.Close
					&& string.Equals(nr.Proposal.Rule, r.Proposal.Rule, StringComparison.Ordinal)
					&& string.Equals(nr.Proposal.PositionKey, pos.Key, StringComparison.OrdinalIgnoreCase))
				.Select(nr => (RuleEvaluator.EvaluationResult?)nr)
				.FirstOrDefault();
			if (confirming is { } c)
				(atNext ??= new()).Add(new ConfirmedClose(c, nextEt, nextQuotes, confirmCtx.UnderlyingPrices));
			// else: drop. Position stays open; the walk's own next iteration re-evaluates it for real.
		}
		return (confirmed ?? results, (IReadOnlyList<ConfirmedClose>?)atNext ?? Array.Empty<ConfirmedClose>());
	}

	/// <param name="fromEt">First minute walked (earlier minutes were walked by a previous call).</param>
	/// <param name="untilEt">Walk stops before this minute.</param>
	private async Task RunIntradayTriggersAsync(DateTime step, DateTime fromEt, DateTime untilEt, CancellationToken cancellation)
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

			// Intraday SL/TP/LegIn triggering is a 0DTE-only construct. A same-day-expiry position opens
			// and settles within the session, so the daily rule loop (which runs at the next step's open)
			// would never see it before expiry — hence the intraday minute-walk. But that walk prices each
			// leg with the parametric Black-Scholes model (BacktestQuoteSource.PriceAtSpot / GetIntradayQuotesAsync),
			// which deliberately ignores captured bars. For a multi-DTE structure (calendar / diagonal) the
			// entry debit is set from the captured-bar path while this pass re-marks the long leg parametrically;
			// the two models diverge and manufacture an instant same-day paper profit that TakeProfit harvests
			// (observed: +44.8% in 19 minutes, 99.4% win rate). Multi-DTE positions are managed by the daily
			// rule loop (Step 1) plus RunManagementMinuteWalkAsync — both price via GetQuotesAsync, the same
			// captured-bar basis as entry, so entry and management never disagree. This specialized SL/TP
			// walk stays all-0DTE-only.
			if (!IsAllZeroDte(pos, step)) continue;
			if (_book.LineageFor(pos.Key) is { } lineage && _zeroDteConsumed.Contains(lineage)) continue;

			// Prefer the minute-walk: walks minute bars chronologically and triggers at the first
			// minute the position mark crosses SL or TP. Removes the bar.Low / bar.High pessimism
			// of the 2-point sampling (which fires SL on intraday spikes that never realized as
			// actual fills, especially for 0DTE near-the-money positions). Falls back to the
			// legacy 2-point logic when no minute data exists for this date.
			var triggered = await TryMinuteWalkTriggerAsync(step, pos, fromEt, untilEt, cash, accountValue, realizedExpectancy, cancellation);
			if (triggered) continue;
			// The whole-day 2-point fallback below belongs to the session's last stretch only: an earlier stretch would evaluate minutes it has not reached.
			if (untilEt < step.Date.Add(MarketCloseTime)) continue;

			// The 2-point fallback below evaluates the WHOLE day's bar Low/High — including minutes BEFORE
			// the position opened — and stamps the close at `step` (~09:30) with a bisected threshold-spot.
			// That is non-causal for any position opened intraday: it fabricates an exit (e.g. a TakeProfit
			// timestamped 09:30 with a 09:30 spot) for a position that opened at, say, 10:00, that the causal
			// minute-walk above correctly did NOT trigger. This pass only handles same-day-opened 0DTE
			// positions (IsAllZeroDte gate), so the minute-walk is the ONLY causal path — never fall back to
			// the 2-point for a position opened today. No minute data → ride to EOD settlement rather than
			// invent a pre-open intraday trigger. The 2-point remains for genuine carry-over positions only.
			if (pos.OpenedAt is null || pos.OpenedAt.Value.Date == step.Date) continue;

			var bar = await _bars.GetBarAsync(pos.Ticker, step.Date, cancellation);
			if (bar == null) continue;
			await TryIntradayTriggerAsync(step, pos, bar.Open, bar.Low, bar.High, cash, accountValue, realizedExpectancy, cancellation);
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
	/// <summary>Combined SL mark-level threshold (mark at or below this fires): the tighter — i.e.
	/// fires-soonest as mark declines — of the independent max-loss-based and max-profit-based
	/// triggers, whichever are armed. Null when neither is armed. Both share the same shape: threshold
	/// mark = AdjustedNetDebit − pct × maxX; a HIGHER mark threshold requires less of a decline to
	/// reach, so it fires first — hence Math.Max over whichever candidates exist.</summary>
	private decimal? ComputeSlTarget(OpenPosition pos, OpenerRealizedExpectancyConfig realizedExpectancy)
	{
		if (!_config.Rules.StopLoss.Enabled) return null;

		decimal? fromMaxLoss = null;
		if (pos.MaxLossPerShare.HasValue && pos.MaxLossPerShare.Value > 0m && realizedExpectancy.StopLossPctOfMaxLoss < 1m)
			fromMaxLoss = pos.AdjustedNetDebit - realizedExpectancy.StopLossPctOfMaxLoss * pos.MaxLossPerShare.Value;

		decimal? fromMaxProfit = null;
		if (pos.MaxProfitPerShare.HasValue && pos.MaxProfitPerShare.Value > 0m && realizedExpectancy.StopLossPctOfMaxProfit > 0m)
			fromMaxProfit = pos.AdjustedNetDebit - realizedExpectancy.StopLossPctOfMaxProfit * pos.MaxProfitPerShare.Value;

		if (!fromMaxLoss.HasValue) return fromMaxProfit;
		if (!fromMaxProfit.HasValue) return fromMaxLoss;
		return Math.Max(fromMaxLoss.Value, fromMaxProfit.Value);
	}

	/// <summary>Take-profit mark-level threshold (mark at or ABOVE this fires), mirroring
	/// <see cref="ComputeSlTarget"/> on the other side of entry. Same definition as
	/// <see cref="Rules.TakeProfitRule"/>: fire once <c>(mark − AdjustedNetDebit) / |AdjustedNetDebit|</c>
	/// reaches <c>profitTargetPctOfPremium</c>, i.e. once <c>mark ≥ AdjustedNetDebit + pct × |AdjustedNetDebit|</c>.
	/// Disjoint from the SL threshold by construction — one needs the mark up, the other down.
	///
	/// <para>This exists because <c>rules.takeProfit</c> could NEVER fire for a position opened the same day it
	/// expires. All-0DTE positions are excluded from both management minute-walks by design, so their only
	/// path is <see cref="TryMinuteWalkTriggerAsync"/> — which evaluated stop-loss, leg-in and force-close but
	/// not take-profit, and early-returned outright when stop-loss was disabled. The result was a silent
	/// no-op: a 0DTE config with <c>takeProfit.enabled: true</c> produced a run byte-identical to one with it
	/// off, reporting <c>Closes (rules) 0</c>. That is a correctness bug, not a strategy finding, and it
	/// invalidated any prior 0DTE conclusion that rested on take-profit being active.</para></summary>
	private decimal? ComputeTpTarget(OpenPosition pos)
	{
		if (!_config.Rules.TakeProfit.Enabled) return null;
		var pct = _config.Rules.TakeProfit.ProfitTargetPctOfPremium;
		if (pct <= 0m) return null;
		var entryPremium = Math.Abs(pos.AdjustedNetDebit);
		if (entryPremium <= 0m) return null;
		return pos.AdjustedNetDebit + pct * entryPremium;
	}

	/// <param name="fromEt">First minute the rules are evaluated at; the minutes before it (walked by a previous call) only feed the running intraday range.</param>
	/// <param name="untilEt">Walk stops before this minute.</param>
	private async Task<bool> TryMinuteWalkTriggerAsync(DateTime step, OpenPosition pos, DateTime fromEt, DateTime untilEt, decimal cash, decimal accountValue, OpenerRealizedExpectancyConfig realizedExpectancy, CancellationToken cancellation)
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

		var startUtc = MarketTime.EtToInstant(walkStartEt);
		var endUtc = MarketTime.EtToInstant(walkEndEt);
		var minuteBars = await _intradayBars.GetBarsAsync(pos.Ticker, startUtc, endUtc, BarInterval.M1, includeExtended: false, cancellation);
		if (minuteBars.Count == 0) return false;

		// SL threshold (mark at or below this fires SL). Matches legacy logic exactly.
		decimal? slTarget = ComputeSlTarget(pos, realizedExpectancy);

		// TP threshold (mark at or above this fires TP). Target A (% of entry premium) — the same definition
		// TakeProfitRule uses, evaluated here because a same-day-opened 0DTE position settles in the step it
		// opened and the start-of-day rule loop therefore never sees it. (The removed Target B, % of max
		// PROJECTED profit, is not reinstated.)
		decimal? tpTarget = ComputeTpTarget(pos);

		// LegInShort: only meaningful on single-leg long calls/puts and only fires intraday for 0DTE
		// strategies (multi-day positions get evaluated at start-of-day in the main rule loop).
		// Instantiated outside the minute loop so we don't re-allocate per minute. Must be declared
		// BEFORE the early-return check so the carve-out can see it.
		var legInRule = _config.Rules.LegInShort.Enabled
			&& (string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) || string.Equals(pos.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase))
			? new Rules.LegInShortRule(_config.Rules.LegInShort, _config.Indicators)
			: null;

		// CompleteCondor: the 0DTE analog of LegInShort but for a held single-sided short vertical — it
		// sells the opposite-side vertical to form an iron condor once the held side has cushion. Disjoint
		// from legInRule (which only applies to single-leg longs), so at most one is non-null per position.
		var completeCondorRule = _config.Rules.CompleteCondor.Enabled
			&& pos.Legs.Count == 2
			&& (string.Equals(pos.StrategyKind, "ShortPutVertical", StringComparison.OrdinalIgnoreCase) || string.Equals(pos.StrategyKind, "ShortCallVertical", StringComparison.OrdinalIgnoreCase))
			? new Rules.CompleteCondorRule(_config.Rules.CompleteCondor)
			: null;

		// TrailingStop: the rule the evaluator runs on the multi-day walks. All-0DTE positions never reach those walks, so it is
		// evaluated here — otherwise a 0DTE config with rules.trailingStop on backtests exactly as with it off (the same silent
		// no-op take-profit once had on this path).
		// A structure the stop excludes arms nothing here, so a held-to-expiry spread with no other exit skips the walk entirely.
		var trailingRule = _config.Rules.TrailingStop.Enabled && !_config.Rules.TrailingStop.Exclude.Contains(pos.StrategyKind, StringComparer.OrdinalIgnoreCase)
			? new Rules.TrailingStopRule(_config.Rules.TrailingStop, _trailingStops)
			: null;

		// Regime indicators for LegInShort / CompleteCondor: VIX and intraday range (tracked across the
		// minute loop). Both null when not needed — keep the lookup cost off the hot path when neither
		// rule's enabled flag is set. VIX is read causally per minute from the day's real intraday tape
		// (data/intraday/VIX/, captured from Webull like the SPX tape): at decision minute M the value is
		// bar M's open (start-of-bar convention — the M:00 print) or, when M's bar is absent, the latest
		// completed bar's close. This mirrors live, where the gates see the real-time VIX quote. Where the
		// tape is missing the fallback is the most recent settled close STRICTLY BEFORE step (walked back
		// 5 days over weekends/holidays, mirroring BacktestIVProvider.GetVixSeriesCloseAsync) — never the
		// day's own 16:00 close, which is not knowable during the walk (using it let the 10:00 MaxVix gate
		// "see" a close-of-day vol spike — lookahead).
		var (vixBars, fallbackVix) = legInRule != null || completeCondorRule != null
			? await LoadVixDayAsync(step, endUtc, cancellation)
			: (Array.Empty<MinuteBar>(), null);
		var vixCursor = 0;

		// LegInShort needs the minute walk even when SL/TP are both disabled (e.g. SPXW's
		// profit/stop pcts pinned at 1.0 to defer all closes to expiration). Without this carve-out,
		// the rule would silently never fire on 0DTE strategies that disable both gates.
		// Broker forced liquidation applies only to physically-settled (assignable) roots; cash-settled
		// index options (XSP/SPXW) settle in cash with no assignment, so they're never force-closed.
		var forceCloseActive = _config.Rules.CloseBeforeShortExpiry.Enabled
			&& !OptionSettlement.CashSettledIndexRoots.Contains(pos.Ticker);
		if (!slTarget.HasValue && !tpTarget.HasValue && legInRule == null && completeCondorRule == null && trailingRule == null && !forceCloseActive) return false;

		// Track intraday range running from session start to the current minute. Used by LegInShort's
		// trend-day filter. We use the first minute bar's open as the day's open reference (close to
		// 09:30 ET when the data covers the full session).
		var dayOpenSpot = minuteBars.Count > 0 ? minuteBars[0].Open : 0m;
		decimal dayHigh = dayOpenSpot, dayLow = dayOpenSpot;

		// Walk minute bars. At each minute, re-price the position at the book instant — the bar's close
		// (M+1:00, when the store's row-M book was struck) with the remaining-session TTE from that same
		// instant. Trigger on first SL/TP crossing.
		for (var mwi = 0; mwi < minuteBars.Count; mwi++)
		{
			var minuteBar = minuteBars[mwi];
			cancellation.ThrowIfCancellationRequested();
			var minuteUtc = minuteBar.Timestamp;
			var spot = minuteBar.Close;
			var minutesToClose = Math.Max(1.0, (endUtc - (minuteUtc + QuoteStoreCache.BookLag)).TotalMinutes);

			if (MinuteEt(minuteBar) >= untilEt) break;

			// Update intraday range with this minute's H/L (complete at the book instant).
			if (minuteBar.High > dayHigh) dayHigh = minuteBar.High;
			if (minuteBar.Low < dayLow) dayLow = minuteBar.Low;
			if (MinuteEt(minuteBar) < fromEt) continue;

			var vixNow = CausalVix(vixBars, ref vixCursor, minuteUtc + QuoteStoreCache.BookLag, fallbackVix);

			// For LegInShort: pre-generate candidate strikes around spot at the long's expiry so the
			// rule has multiple strikes to pick from. Only done when the rule is applicable to this
			// position's structure; otherwise we fetch just the position's leg symbols.
			// Parity probes ride along either way: the position legs alone rarely carry a same-strike
			// call+put pair, and without one the parity spot guard below can't cross-check the bar spot
			// against the option book (composed-open lag on gap-day 09:30/09:31 bars).
			var probeExpiry = pos.Legs.FirstOrDefault(l => l.Expiry.HasValue)?.Expiry;
			IEnumerable<string> quoteSymbols = probeExpiry.HasValue
				? symbols.Concat(ParitySpotGuard.ProbeSymbols(pos.Ticker, probeExpiry.Value.Date, spot, _config.Indicators.StrikeStep))
				: symbols;
			if (legInRule != null || completeCondorRule != null)
			{
				// Side of the strikes to pre-price: LegInShort sells on the long's own side; CompleteCondor
				// sells the side OPPOSITE the held vertical. Expiry is the position's (both rules act at the
				// position's single expiry).
				var anchorLeg = pos.Legs[0];
				var candidateSide = legInRule != null
					? anchorLeg.CallPut!
					: (anchorLeg.CallPut == "P" ? "C" : "P");
				var step5 = _config.Indicators.StrikeStep > 0m ? _config.Indicators.StrikeStep : 5m;
				var candidateSymbols = new HashSet<string>(quoteSymbols, StringComparer.OrdinalIgnoreCase);
				// ±100 points (at $5 step → 40 strikes) covers the 0.05–0.95 delta range for SPXW even
				// at short DTE with elevated IV. Cheap enough at minute resolution.
				for (decimal k = Math.Floor(spot / step5) * step5 - 100m; k <= Math.Ceiling(spot / step5) * step5 + 100m; k += step5)
				{
					if (k <= 0m) continue;
					candidateSymbols.Add(MatchKeys.OccSymbol(pos.Ticker, anchorLeg.Expiry!.Value, k, candidateSide));
				}
				quoteSymbols = candidateSymbols;
			}
			// Price the position at this minute through the SAME captured-bar-preferred path the entry used
			// (GetQuotesAsync with a minute-ET asOf + spot/TTE overrides), NOT the parametric-only intraday
			// path — otherwise the SL/TP mark and the close fill diverge from the entry's captured-bar pricing,
			// producing phantom early exits (e.g. a $1-wide credit spread opened at a captured $0.12 credit but
			// marked at a Black-Scholes $0.01 → fake ~90% TakeProfit). BS stays the fallback for legs with no bar.
			var minuteEt = MarketTime.ToEt(minuteUtc).DateTime;
			var minuteOverrides = new QuoteOverrides(
				Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot },
				ZeroDteWindow: ZeroDteWindow(minuteUtc, endUtc));
			var quotes = (await _quotes.GetQuotesAsync(minuteEt, quoteSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pos.Ticker }, cancellation, minuteOverrides)).Options;
			// Parity spot guard: LegInShort's minSpotPctITM, CompleteCondor's cushion, and the assignment-risk
			// force-close all gate on spot-vs-strike moneyness, so they must see the spot the option book is
			// pricing, not a lagging bar print (the SL mark itself is quote-based and unaffected).
			if (ParitySpotGuard.Correct(pos.Ticker, spot, quotes, minuteEt) is decimal paritySpot) spot = paritySpot;
			var mark = ComputeMarkFromQuotes(pos.Legs, quotes);
			if (!mark.HasValue) continue;

			// Try LegInShort first at this minute. If it fires, execute the leg-in and stop the
			// minute walk for this position — the now-vertical settles at expire. We forgo intraday
			// SL/TP on the post-leg-in vertical (simplest pass; vertical's tighter risk profile
			// makes this a benign approximation for 0DTE).
			if (legInRule != null)
			{
				var legInMinuteEt = MarketTime.ToEt(minuteUtc).DateTime;
				var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot };
				var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
				decimal? rangePct = dayOpenSpot > 0m ? (dayHigh - dayLow) / dayOpenSpot * 100m : null;
				var minuteCtx = new EvaluationContext(legInMinuteEt, stillOpen, underlyings, quotes, cash, accountValue, emptySignals, vixNow, rangePct);
				var legInProposal = legInRule.Evaluate(pos, minuteCtx);
				if (legInProposal != null)
				{
					// Single sell-to-open leg from the proposal. Fill under the active pricing mode: bidask
					// hits the bid (we're selling), mid takes the spread midpoint.
					var shortLeg = legInProposal.Legs[0];
					if (quotes.TryGetValue(shortLeg.Symbol, out var sq) && sq.Bid is decimal sBid && sq.Ask is decimal sAsk)
					{
						// Map to OpenStructureKind. Credit-spread mode → ShortVertical (single kind covers
						// both bear-call and bull-put credit spreads); debit-spread → LongCallVertical or
						// LongPutVertical depending on original long direction.
						OpenStructureKind newStructure;
						if (_config.Rules.LegInShort.CreditSpread)
							newStructure = (string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.ShortCallVertical : OpenStructureKind.ShortPutVertical);
						else
							newStructure = string.Equals(pos.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase) ? OpenStructureKind.LongCallVertical : OpenStructureKind.LongPutVertical;
						var legInFills = new[] { new BacktestLegFill(shortLeg.Symbol, Side.Sell, pos.Quantity, ExecPrice(Side.Sell, sBid, sAsk)) };
						ConsumeForDay(pos);
						_book.LegIn(legInMinuteEt, pos.Key, legInFills, "LegInShortRule", newStructure, spot);
						return true; // mirror SL/TP semantics: a fired rule consumes the position for the day.
					}
				}
			}

			// CompleteCondor: try converting the held short vertical into an iron condor at this minute.
			// Fires before SL/TP — but its held-side-cushion gate means it only triggers when the held
			// short is comfortably OTM, i.e. nowhere near the stop. On fire we add the opposite vertical
			// and consume the position for the day (the condor rides to expiry, same simplification as the
			// leg-in path above).
			if (completeCondorRule != null)
			{
				var stillOpen = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { { pos.Key, pos } };
				var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot };
				var emptySignals = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
				decimal? rangePct = dayOpenSpot > 0m ? (dayHigh - dayLow) / dayOpenSpot * 100m : null;
				var condorCtx = new EvaluationContext(minuteEt, stillOpen, underlyings, quotes, cash, accountValue, emptySignals, vixNow, rangePct);
				var condorProposal = completeCondorRule.Evaluate(pos, condorCtx);
				if (condorProposal != null)
				{
					var sellLeg = condorProposal.Legs.First(l => string.Equals(l.Action, "sell", StringComparison.OrdinalIgnoreCase));
					var buyLeg = condorProposal.Legs.First(l => string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase));
					if (quotes.TryGetValue(sellLeg.Symbol, out var csq) && csq.Bid is decimal csBid && csq.Ask is decimal csAsk
						&& quotes.TryGetValue(buyLeg.Symbol, out var cbq) && cbq.Bid is decimal cbBid && cbq.Ask is decimal cbAsk)
					{
						var condorFills = new[]
						{
							new BacktestLegFill(sellLeg.Symbol, Side.Sell, pos.Quantity, ExecPrice(Side.Sell, csBid, csAsk)),
							new BacktestLegFill(buyLeg.Symbol, Side.Buy, pos.Quantity, ExecPrice(Side.Buy, cbBid, cbAsk))
						};
						ConsumeForDay(pos);
						_book.LegIn(minuteEt, pos.Key, NoArb(condorFills), "CompleteCondorRule", OpenStructureKind.IronCondor, spot);
						return true;
					}
				}
			}

			// Broker forced liquidation: an assignable position whose short is ITM in the final window before
			// settlement is closed by the broker at the current mark (Webull liquidates ITM SPY ~15:30 ET) to
			// avoid assignment, rather than riding to 16:00 intrinsic. Fires at the first in-window minute the
			// short is ITM. (The multi-day EOD path is handled by CloseBeforeShortExpiryRule; this is the 0DTE
			// intraday analog, scoped to its assignment-risk leg only — no profit/BE gating.)
			if (forceCloseActive && minutesToClose <= _config.Rules.CloseBeforeShortExpiry.BrokerForceCloseMinutesBeforeClose && HasAtRiskShortLeg(pos.Legs, spot, _config.Rules.CloseBeforeShortExpiry.BrokerForceCloseMoneynessBufferPct))
			{
				var fcFills = new List<BacktestLegFill>(pos.Legs.Count);
				bool fcPriced = true;
				foreach (var leg in pos.Legs)
				{
					if (leg.CallPut == null) continue;
					if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) { fcPriced = false; break; }
					var cs = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
					fcFills.Add(new BacktestLegFill(leg.Symbol, cs, pos.Quantity, ExecPrice(cs, q.Bid.Value, q.Ask.Value)));
				}
				if (fcPriced) { _book.Close(minuteEt, pos.Key, NoArb(fcFills), "CloseBeforeShortExpiryRule", spot); return true; }
			}

			bool slFires = slTarget.HasValue && mark.Value <= slTarget.Value;
			bool tpFires = tpTarget.HasValue && mark.Value >= tpTarget.Value;
			if (slFires || tpFires)
			{
				// Noise gate, same model as the opener's minEntryToNoiseRatio (CandidateScorer.PassesExitNoiseGate):
				// don't honor a threshold crossing the legs' own quote noise fully explains. This is the fast
				// 0DTE-only path that bypasses StopLossRule/TakeProfitRule entirely (see IsAllZeroDte above), so
				// it needs its own copy of the same check those rule classes now carry. A same-minute NBBO tick
				// on one leg is exactly what produced the trade that motivated this: a LongDiagonal that
				// "captured" 137% of its debit in one minute on a flat underlying (project_dc_sweep_tp_noise_artifact.md).
				if (!CandidateScorer.PassesExitNoiseGate(symbols, quotes, mark.Value - pos.AdjustedNetDebit, _config.Opener.MinEntryToNoiseRatio))
					slFires = tpFires = false;
			}
			// The trailing stop values the position at its closing side and skips a crossed quote itself, so the mid-based noise
			// gate above is not applied to it.
			var trailFires = trailingRule?.Evaluate(pos, SinglePositionContext(minuteEt, pos, spot, quotes, cash, accountValue, vixNow)) != null;
			if (!slFires && !tpFires && !trailFires) continue;

			// SL wins a tie it cannot actually have — the two thresholds sit on opposite sides of entry — but
			// checking it first keeps the precedence explicit and matches the rules' own priority order.
			var ruleName = slFires ? "StopLossRule" : trailFires ? "TrailingStopRule" : "TakeProfitRule";

			// Fill-realism check: don't honor the crossing unless it still holds one minute later, re-priced
			// from scratch (mirrors ConfirmPnlClosesAsync's role for the shared-rule path — see its doc for
			// why this belongs in the simulator rather than the rule, and why the noise gate above isn't
			// enough on its own: it only looks at the current minute's OWN spread, which can look perfectly
			// ordinary while still disagreeing wildly with the prior minute). Fire unconfirmed on the day's
			// last bar (nothing to confirm against) rather than silently suppress every end-of-day exit.
			if (mwi + 1 < minuteBars.Count)
			{
				var nextBar = minuteBars[mwi + 1];
				var nextMinuteEt = MarketTime.ToEt(nextBar.Timestamp).DateTime;
				var nextSpot = nextBar.Close;
				var nextOverrides = new QuoteOverrides(
					Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = nextSpot },
					ZeroDteWindow: ZeroDteWindow(nextBar.Timestamp, endUtc));
				var nextQuotes = (await _quotes.GetQuotesAsync(nextMinuteEt, symbols, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pos.Ticker }, cancellation, nextOverrides)).Options;
				var nextMark = ComputeMarkFromQuotes(pos.Legs, nextQuotes);
				var nextSlFires = nextMark.HasValue && slTarget.HasValue && nextMark.Value <= slTarget.Value;
				var nextTpFires = nextMark.HasValue && tpTarget.HasValue && nextMark.Value >= tpTarget.Value;
				if ((nextSlFires || nextTpFires) && !CandidateScorer.PassesExitNoiseGate(symbols, nextQuotes, nextMark!.Value - pos.AdjustedNetDebit, _config.Opener.MinEntryToNoiseRatio))
					nextSlFires = nextTpFires = false;
				var nextTrailFires = trailFires && trailingRule!.Evaluate(pos, SinglePositionContext(nextMinuteEt, pos, nextSpot, nextQuotes, cash, accountValue, vixNow)) != null;
				if (!nextSlFires && !nextTpFires && !nextTrailFires) continue; // didn't persist — position stays open, next iteration re-checks for real

				// Confirmed: fill against the CONFIRMING minute's quotes, not the fleeting one that triggered
				// the check — that's the price basis actually validated as real.
				minuteEt = nextMinuteEt;
				spot = nextSpot;
				quotes = nextQuotes;
				ruleName = nextSlFires ? "StopLossRule" : nextTrailFires ? "TrailingStopRule" : "TakeProfitRule";
			}

			// Fill-realism check on the basis actually being used (the confirming minute's quotes, when a
			// confirmation happened above) — same gate ApplyManagementResults' BuildLegFillsFromQuotes
			// applies for the shared-rule path; this fast path builds fills inline so it needs its own call.
			if (!CandidateScorer.PassesFillSpreadGate(symbols, quotes, _config.Opener.Liquidity.MaxFillSpreadPctOfMid, _config.Opener.Liquidity.MinAbsFillSpreadDollars)) continue;

			var legFills = new List<BacktestLegFill>(pos.Legs.Count);
			bool allLegsPriced = true;
			foreach (var leg in pos.Legs)
			{
				if (leg.CallPut == null) continue;
				if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) { allLegsPriced = false; break; }
				var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
				// The trailing stop is a marketable close: live sends it at the touch.
				legFills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, ruleName == "TrailingStopRule" ? TouchPrice(closeSide, q.Bid.Value, q.Ask.Value) : ExecPrice(closeSide, q.Bid.Value, q.Ask.Value)));
			}
			if (!allLegsPriced) continue;

			_book.Close(minuteEt, pos.Key, NoArb(legFills), ruleName, spot);
			return true;
		}

		return false;
	}

	/// <summary>Marks <paramref name="pos"/>'s lineage as done with the 0DTE walk for the day: a leg-in or condor completion re-keys it, and the result rides to expiry.</summary>
	private void ConsumeForDay(OpenPosition pos)
	{
		if (_book.LineageFor(pos.Key) is { } lineage) _zeroDteConsumed.Add(lineage);
	}

	/// <summary>The evaluation context of one position at one minute of the 0DTE walk, for a rule run outside the evaluator.</summary>
	private static EvaluationContext SinglePositionContext(DateTime minuteEt, OpenPosition pos, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal cash, decimal accountValue, decimal? vix) =>
		new(minuteEt, new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase) { [pos.Key] = pos }, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [pos.Ticker] = spot }, quotes, cash, accountValue, new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase), vix);

	// True when any short leg is ITM (bufferPct 0) or within bufferPct of the money — "at risk" of finishing
	// ITM (short call: spot > strike×(1−b); short put: spot < strike×(1+b)).
	private static bool HasAtRiskShortLeg(IReadOnlyList<PositionLeg> legs, decimal spot, decimal bufferPct)
	{
		var b = bufferPct;
		foreach (var leg in legs)
		{
			if (leg.Side != Side.Sell || leg.CallPut == null) continue;
			if (leg.CallPut == "C" ? spot > leg.Strike * (1m - b) : spot < leg.Strike * (1m + b)) return true;
		}
		return false;
	}

	private async Task TryIntradayTriggerAsync(
		DateTime step, OpenPosition pos, decimal barOpen, decimal barLow, decimal barHigh,
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
			step, pos.Ticker, barLow, symbols, MarketClock.BeforeClose(pos.Ticker, step, BacktestQuoteSource.IntradayHalfSessionHours), cancellation);
		var quotesHigh = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, barHigh, symbols, MarketClock.BeforeClose(pos.Ticker, step, BacktestQuoteSource.IntradayHalfSessionHours), cancellation);
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
		decimal? slTarget = ComputeSlTarget(pos, realizedExpectancy);

		// Intraday take-profit was Target-B only; removed (Target A fires at start-of-day in the main loop).

		// Does the stop threshold sit in [markLow, markHigh]? The mark is monotonic in spot for every
		// structure currently enumerated (verticals, naked longs, iron condors). If the threshold
		// lies between the two extreme marks, there's a spot in [barLow, barHigh] where mark equals
		// the threshold.
		decimal markMin = Math.Min(markLow.Value, markHigh.Value);
		decimal markMax = Math.Max(markLow.Value, markHigh.Value);
		bool slFires = slTarget.HasValue && slTarget.Value >= markMin && slTarget.Value <= markMax;

		// Also catch the case where the position was ALREADY past the threshold at bar.Open. We don't
		// have bar.Open here, but if BOTH extreme marks are past the threshold the day opened past it.
		// Trigger and close at the threshold price (conservative — gives back any deeper capture).
		if (slTarget.HasValue && markLow.Value <= slTarget.Value && markHigh.Value <= slTarget.Value) slFires = true;

		if (!slFires) return;

		string ruleName = "StopLossRule";
		decimal targetMark = slTarget!.Value;

		// Find the spot in [barLow, barHigh] where the position mark equals targetMark.
		var thresholdSpot = await FindSpotForMarkAsync(
			step, pos, symbols, barLow, markLow.Value, barHigh, markHigh.Value, targetMark, cancellation);
		var quotesAtThreshold = await _quotes.GetIntradayQuotesAsync(
			step, pos.Ticker, thresholdSpot, symbols, MarketClock.BeforeClose(pos.Ticker, step, BacktestQuoteSource.IntradayHalfSessionHours), cancellation);

		// Reverse each leg's side to close. Fill price follows the active pricing mode (mid or cross-spread).
		var legFills = new List<BacktestLegFill>(pos.Legs.Count);
		foreach (var leg in pos.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!quotesAtThreshold.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) return;
			var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
			legFills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, ExecPrice(closeSide, q.Bid.Value, q.Ask.Value)));
		}

		_book.Close(step, pos.Key, NoArb(legFills), ruleName, thresholdSpot);
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
				step, pos.Ticker, mid, symbols, MarketClock.BeforeClose(pos.Ticker, step, BacktestQuoteSource.IntradayHalfSessionHours), cancellation);
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

	// ── Cascade trades (opener.cascade) ────────────────────────────────────────────────────────────────────

	/// <summary>The instants of a session the cascade is read at: from the opener's earliest entry time (or one cadence after the open)
	/// every <c>--cadence</c> minutes through its latest entry time. Live reads every <c>opener.cascade.readSeconds</c>; a backtest reads
	/// on this coarser grid because each reading replays the whole chain — the same trade-off `analyze cascade-study` makes with its
	/// checkpoints. A setup therefore confirms at the first grid instant it is seen at, up to one cadence later than live would.</summary>
	private List<TimeSpan> CascadeReadTimes()
	{
		var cadence = TimeSpan.FromMinutes(_cascadeCadenceMinutes);
		var first = ParsingHelpers.TryParseClockTime(_config.Opener.EarliestEntryTimeEt, out var earliest) ? earliest : MarketOpenTime + cadence;
		var last = ParsingHelpers.TryParseClockTime(_config.Opener.LatestEntryTimeEt, out var latest) ? latest : MarketCloseTime - TimeSpan.FromMinutes(1);
		var times = new List<TimeSpan>();
		for (var t = first; t <= last; t += cadence) times.Add(t);
		return times;
	}

	/// <summary>Starts replaying every session's cascade readings in the background, several sessions at a time and in date order, so
	/// the day loop finds each day's readings ready (or waits for just that day). A reading depends only on stored market data, never
	/// on the book, so it can run ahead; this is what lets a cascade backtest take minutes, as `analyze cascade-study` does. Only the
	/// reading and its spot are kept — a session's chain is large, and the legs of the rare trade are priced from the quote store.
	/// Readings are kept on disk (<see cref="Analyze.CascadeReadingCache"/>), so a later run of the same build reads them back instead.</summary>
	private void StartCascadeReplay(IReadOnlyList<DateTime> steps, CancellationToken cancellation)
	{
		var times = CascadeReadTimes();
		var ticker = _config.Ticker;
		var reads = steps.ToDictionary(d => d.Date, _ => new TaskCompletionSource<IReadOnlyList<Analyze.CascadeMoment>>(TaskCreationOptions.RunContinuationsAsynchronously));
		_cascadeReads = reads;
		// The opener itself is the live one: same setups, trade construction, sizing and once-a-day ledger (in memory here, and silent).
		_cascadeLedger = new Open.Cascade.CascadeSignalLedger();
		_cascadeOpener = new Open.Cascade.CascadeOpener(_config, live: false, _cascadeLedger,
			(_, _) => Task.FromResult<(Analyze.CascadeLiveRead? Read, string? Error)>((_cascadeRead, null)), quiet: true, toEt: et => et);

		var minuteVolume = new System.Collections.Concurrent.ConcurrentDictionary<(string, DateTime, DateTime), Lazy<SortedDictionary<TimeSpan, long>?>>();
		var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellation };
		var store = new Analyze.CascadeReadingCache(ticker, times, _cascadeRecache);
		_ = Task.Run(async () =>
		{
			try
			{
				await Parallel.ForEachAsync(steps, options, async (day, ct) =>
				{
					try
					{
						var fingerprint = Analyze.CascadeReadingCache.Fingerprint(ticker, day.Date);
						var moments = store.TryLoad(day.Date, fingerprint);
						if (moments == null)
						{
							var rate = _rateHistory != null ? HistoricalRateCache.RateOn(_rateHistory, day.Date) : null;
							moments = await ReplayCascadeSessionAsync(ticker, day.Date, times, new Analyze.CascadeReplayCache(minuteVolume), rate, ct);
							store.Save(day.Date, fingerprint, moments);
						}
						reads[day.Date].TrySetResult(moments);
					}
					catch (Exception ex) { reads[day.Date].TrySetException(ex); }
				});
			}
			catch (OperationCanceledException) { }
			finally { foreach (var pending in reads.Values) pending.TrySetCanceled(); }
		}, CancellationToken.None);
	}

	/// <summary>One session's readings on the read grid; empty when the session cannot be replayed (no captured chain or tape).</summary>
	/// <param name="riskFreeRate">The session's rate — the one the day loop uses for that day; null leaves the process-wide rate.</param>
	private static async Task<IReadOnlyList<Analyze.CascadeMoment>> ReplayCascadeSessionAsync(string ticker, DateTime date, IReadOnlyList<TimeSpan> times, Analyze.CascadeReplayCache cache, double? riskFreeRate, CancellationToken ct)
	{
		var (session, _) = await Analyze.CascadeReplaySession.OpenAsync(ticker, date, cache, riskFreeRate, ct);
		var moments = new List<Analyze.CascadeMoment>();
		if (session == null) return moments;
		foreach (var t in times)
			if (await session.ReadAsync(t, ct) is { } read) moments.Add(new Analyze.CascadeMoment(t, read.Reading, read.Spot));
		return moments;
	}

	/// <summary>Runs one cascade reading through the cascade opener and books the trades it proposes. A reading is of an instant T; the
	/// trade is priced from and stamped at its book minute <paramref name="minuteEt"/> (<see cref="CascadeBookMinute"/>) — decision and
	/// fill on the same book, as for every other backtest open. <see cref="AdvanceDayThroughAsync"/> calls it in time order after managing
	/// today's positions up to that minute, so a trade a rule closed earlier in the session no longer holds legs, cash or a place in the book.</summary>
	private async Task OpenCascadeReadingAsync(DateTime step, IReadOnlySet<string> tickerSet, Analyze.CascadeMoment moment, DateTime minuteEt, CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		var ticker = _config.Ticker;
		var trades = CascadeTradesToPrice(moment);
		var quotes = trades.Count > 0 ? await CascadeLegQuotesAsync(ticker, step.Date, minuteEt, moment, trades, cancellation) : new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		_cascadeRead = new Analyze.CascadeLiveRead(moment.Reading, moment.Reading.Terrain.ExpiryCount, moment.Spot, quotes);

		var positions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
		var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
		var ctx = new EvaluationContext(step.Date + moment.TimeEt, positions, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = moment.Spot }, quotes, cash, accountValue, new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase));
		// Live's per-day cap on opens (autoExecute.opener.maxOrdersPerDay), counted across the day's cascade trades: with more setups
		// enabled than the cap allows, the later confirmations of the day are not placed, as live would not place them.
		var cap = _config.AutoExecute.Opener.MaxOrdersPerDay;
		foreach (var p in await _cascadeOpener!.EvaluateAsync(ctx, cancellation))
		{
			var (regime, side) = _cascadeOpener.SetupOf(p) ?? (moment.Reading.PrimaryRegime, null);
			if (_cascadeOpenedToday >= cap) { _cascadeUnfilled.Add((regime, side, $"daily cap of {cap} open(s) reached (autoExecute.opener.maxOrdersPerDay)")); continue; }
			var qty = ResolveOpenQty(p);
			if (qty >= 1 && OpenProposalIntoBook(minuteEt, p, qty)) { _cascadeOpener.Acted(p); _cascadeOpenedToday++; }
			else _cascadeUnfilled.Add((regime, side, qty < 1 ? $"sized to zero ({p.CashReserveDetail ?? "no contracts affordable"})" : "refused by the book (leg collision or unpriced leg)"));
		}
	}

	/// <summary>The trades a reading could open, whose legs must be priced before the opener sees it: the confirmed setup's, or on a neutral
	/// reading the setup it leans to (when that block trades leans) and, from the neutral setup's time on, its enabled sides (the opener
	/// itself decides whether the day is neutral).</summary>
	private List<CascadeSetupConfig> CascadeTradesToPrice(Analyze.CascadeMoment moment)
	{
		var cfg = _config.Opener.Cascade;
		if (moment.Reading.PrimaryRegime != Analyze.CascadeRegime.Neutral)
			return _cascadeOpener!.SetupsToTrade(moment.Reading).Select(s => cfg.For(s.Regime)).Where(t => t is { Enabled: true }).Select(t => t!).ToList();
		var trades = new List<CascadeSetupConfig>();
		if (_cascadeOpener!.LeanSetup(moment.Reading) is { } lean) trades.Add(lean.Trade);
		if (cfg.Neutral.Enabled && ParsingHelpers.TryParseClockTime(cfg.Neutral.AfterTimeEt, out var after) && moment.TimeEt >= after)
			trades.AddRange(cfg.Neutral.EnabledSides.Select(x => x.Trade));
		return trades;
	}

	/// <summary>The neutral side a proposal belongs to, from the right of its structure.</summary>
	private static string NeutralSide(OpenStructureKind kind) => kind is OpenStructureKind.ShortCallVertical or OpenStructureKind.LongCallVertical or OpenStructureKind.LongCall ? "call" : "put";

	/// <summary>What became of the run's cascade signals, one line per traded setup — first confirmations, trades opened, and why the rest
	/// were not — so a setup that never trades (a credit floor, an account too small for the option) is visible rather than a quiet zero.
	/// Empty when <c>opener.cascade</c> is off.</summary>
	public IReadOnlyList<string> CascadeSummary()
	{
		if (_cascadeLedger == null) return Array.Empty<string>();
		var times = CascadeReadTimes();
		var lines = new List<string> { $"Cascade signals — read every {_cascadeCadenceMinutes} min, {times.FirstOrDefault():hh\\:mm}–{times.LastOrDefault():hh\\:mm} ET ({times.Count} reads a session; --cadence changes it):" };
		var entries = _cascadeLedger.Entries;
		var blocks = _config.Opener.Cascade.Setups.Where(x => x.Setup.Enabled).Select(x => (Regime: Enum.Parse<Analyze.CascadeRegime>(x.Name, ignoreCase: true), Side: (string?)null, x.Setup)).ToList();
		if (_config.Opener.Cascade.Neutral.Enabled)
			blocks.AddRange(_config.Opener.Cascade.Neutral.EnabledSides.Select(x => (Analyze.CascadeRegime.Neutral, (string?)x.Side, x.Trade)));
		foreach (var (regime, side, setup) in blocks)
		{
			var mine = entries.Where(e => e.Setup == regime && e.Side == side && e.Outcome != Open.Cascade.CascadeSignalLedger.Confirmed).ToList();
			// One per session a setup confirmed on: a trade built (Signaled), a first confirmation that was final (Skipped), or a confirmation that only ever
			// waited (Waiting, one line per change of reason, so counted by day).
			var confirmed = mine.Where(e => e.Outcome is Open.Cascade.CascadeSignalLedger.Signaled or Open.Cascade.CascadeSignalLedger.Skipped or Open.Cascade.CascadeSignalLedger.Waiting).Select(e => e.Date).Distinct().Count();
			var opened = mine.Count(e => e.Outcome == Open.Cascade.CascadeSignalLedger.Placed);
			var openedDays = mine.Where(e => e.Outcome == Open.Cascade.CascadeSignalLedger.Placed).Select(e => e.Date).ToHashSet();
			// Why a session ended with no trade: its last reason (a final skip, or what it was still waiting on at the close).
			var reasons = mine.Where(e => e.Outcome is Open.Cascade.CascadeSignalLedger.Skipped or Open.Cascade.CascadeSignalLedger.Waiting && !openedDays.Contains(e.Date))
				.GroupBy(e => e.Date).Select(g => g.Last().Detail)
				.Concat(_cascadeUnfilled.Where(u => u.Setup == regime && u.Side == side).Select(u => u.Reason))
				.GroupBy(ReasonKind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()}× {g.Key}").ToList();
			var gated = _cascadeOpener!.GatedReadings.Where(g => g.Setup == regime).ToList();
			var name = side == null ? Analyze.CascadeReading.RegimeLabel(regime) : $"NEUTRAL {side.ToUpperInvariant()}";
			var what = side == null ? "first confirmation(s)" : "neutral day(s)";
			lines.Add($"  {name} → {setup.Kind}: {confirmed} {what}, {opened} opened{(reasons.Count > 0 ? "; not opened: " + string.Join(", ", reasons) : "")}{(gated.Count > 0 ? $"; {gated.Count} reading(s) held back by an entry condition ({string.Join(", ", gated.GroupBy(g => ReasonKind(g.Reason)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()}× {g.Key}"))})" : "")}");
		}
		return lines;
	}

	/// <summary>A skip reason with its numbers blanked, so "the $7,650/$7,660 call spread mid credit $0.07 is under minCredit $0.10" and the
	/// same reason at other strikes count as one kind.</summary>
	private static string ReasonKind(string reason) => System.Text.RegularExpressions.Regex.Replace(reason, @"\$?-?\d[\d,]*(\.\d+)?", "#");

	/// <summary>The quote-store book, at the trade's minute, of exactly the legs the setups' trades would use — asked for by symbol, so a
	/// strike the store never captured simply is not there and the opener reports it as unlisted.</summary>
	private async Task<IReadOnlyDictionary<string, OptionContractQuote>> CascadeLegQuotesAsync(string ticker, DateTime date, DateTime minuteEt, Analyze.CascadeMoment moment, IEnumerable<CascadeSetupConfig> setups, CancellationToken cancellation)
	{
		var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var setup in setups)
		{
			var (first, second, skip) = Open.Cascade.CascadeOpener.TradeStrikes(moment.Spot, moment.Reading.Terrain, _config.Indicators.StrikeStep, setup, moment.Reading.Inputs.Tape);
			if (skip != null) continue;
			symbols.Add(MatchKeys.OccSymbol(ticker, date, first, setup.Right));
			if (second is decimal wing) symbols.Add(MatchKeys.OccSymbol(ticker, date, wing, setup.Right));
		}
		if (symbols.Count == 0) return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var overrides = new QuoteOverrides(Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = moment.Spot }, ZeroDteWindow: ZeroDteWindow(MarketTime.EtToInstant(minuteEt), MarketTime.EtToInstant(date.Add(MarketCloseTime))));
		var snapshot = await _quotes.GetQuotesAsync(minuteEt, symbols, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker }, cancellation, overrides);
		// Only the legs asked for: the store expands a requested expiry into its near-money chain, which the trade must not pick from.
		return snapshot.Options.Where(kv => symbols.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Scans each minute of the trading day (09:30 → 16:00 ET — see <see cref="MarketOpenTime"/>) and opens the first proposal
	/// that clears the score + cash gates. Spot is taken from the per-minute close of the configured
	/// ticker's intraday CSV; 0DTE TTE shrinks linearly to 16:00. One open per day max (early-exit on
	/// first fill). Returns <c>HasIntraday=false</c> when minute data is missing for any ticker —
	/// caller falls back to the legacy once-per-day fill. The session advances (<see cref="AdvanceDayThroughAsync"/>) to each minute
	/// before its decision, so every open is sized and guarded against the book at that minute.</summary>
	private async Task<DailyOpenScanResult> TryOpenAcrossDayAsync(DateTime step, HashSet<string> tickerSet, RuleEvaluator evaluator, OpenCandidateEvaluator openEvaluator, CancellationToken cancellation)
	{
		// The book after the 09:30 rule pass (the legacy and oracle paths score against it) and the day's signals (date-only).
		var postMgmt = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
		var (postCash, postAccount) = await _positions.GetAccountStateAsync(step, cancellation);
		var postSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, _closeCache, _config.Indicators.TechnicalFilter, step, cancellation);

		// Load each ticker's minute bars for the RTH window. Convert step.Date + (09:30 ET, 16:00 ET)
		// to UTC for the cache call — the cache returns bars in UTC, but the on-disk grouping is by
		// NY date, so any ET-correct window spans the right file. Window starts at 09:30 (first RTH
		// minute under our normalized start-of-bar convention; see `MarketOpenTime`).
		var openEt = new DateTime(step.Date.Year, step.Date.Month, step.Date.Day, 9, 30, 0, DateTimeKind.Unspecified);
		var closeEt = new DateTime(step.Date.Year, step.Date.Month, step.Date.Day, 16, 0, 0, DateTimeKind.Unspecified);
		var openUtc = MarketTime.EtToInstant(openEt);
		var closeUtc = MarketTime.EtToInstant(closeEt);

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
				// Drop Informational (display-only, below-gate) proposals so log level can't change fills — see
				// the equivalent filter in EvaluateMinuteAsync and OpenerAutoExecutor.
				return new DailyOpenScanResult(HasIntraday: false, LegacyProposals: legacy.Where(p => !p.Informational).ToList());
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

		// Parallel minute evaluation. EvaluateMinuteAsync is a pure function over its inputs (no
		// shared mutable state) so minutes can be evaluated concurrently. The results are then
		// processed in chronological order for entry-decision / oracle logic.
		// Earliest-entry gate: withhold opens until a configured ET wall-clock time so the intraday tape
		// can form and blend into the bias before the directional read is committed (vs trading the stale
		// 09:30 overnight macro). Parsed once per day; null/empty = no delay.
		TimeSpan? earliestEntry = null;
		if (ParsingHelpers.TryParseClockTime(_config.Opener.EarliestEntryTimeEt, out var ee))
			earliestEntry = ee;
		// Latest-entry gate: suppress opens AFTER this ET time (the tail-of-session mirror of earliestEntry).
		// Models the `wa ai watch` cutoff so a backtest can validate it. Parsed once per day; null/empty = none.
		TimeSpan? latestEntry = null;
		if (ParsingHelpers.TryParseClockTime(_config.Opener.LatestEntryTimeEt, out var le))
			latestEntry = le;

		// The 0DTE session gate carries its own entry window and will reject every minute outside it, so fold
		// that window into the scan trim: without this a config that sets only opener.zeroDteGate.{earliest,
		// latest}EntryEt still enumerates and scores all 390 minutes and throws the out-of-window ones away.
		// Narrowing only (Math.Max / Math.Min) so neither window can ever WIDEN what the other allows.
		if (_config.Opener.ZeroDteGate.Enabled)
		{
			if (ParsingHelpers.TryParseClockTime(_config.Opener.ZeroDteGate.EarliestEntryEt, out var gee))
				earliestEntry = earliestEntry.HasValue ? TimeSpan.FromTicks(Math.Max(earliestEntry.Value.Ticks, gee.Ticks)) : gee;
			if (ParsingHelpers.TryParseClockTime(_config.Opener.ZeroDteGate.LatestEntryEt, out var gle))
				latestEntry = latestEntry.HasValue ? TimeSpan.FromTicks(Math.Min(latestEntry.Value.Ticks, gle.Ticks)) : gle;
		}

		var timestampList = allTimestamps.ToList();
		// Open-scan stride: evaluate every Nth minute instead of all 390. On days that never open, the
		// scan would otherwise re-run the (heavy, for multi-leg structures) candidate enumeration every
		// minute. A stride caps that cost ~N× at the price of N-minute entry granularity — negligible for
		// a backtest, decisive for a multi-leg full-year run. Stride 1 (default) = exhaustive, unchanged.
		// Oracle always scans every minute (it needs the full surface), so the stride is open-scan only.
		if (_scanStride > 1 && !_oracle && timestampList.Count > 0)
		{
			var strided = new List<DateTimeOffset>(timestampList.Count / _scanStride + 1);
			for (var i = 0; i < timestampList.Count; i += _scanStride) strided.Add(timestampList[i]);
			timestampList = strided;
		}

		// Entry-window trim: a non-oracle open can only fire within [earliestEntry, latestEntry], so scoring
		// minutes outside that window is pure waste — on a day that never qualifies, the scan would otherwise
		// enumerate candidates for every one of the ~390 minutes. Trimming caps a no-open/late-open day at the
		// window length: with a 09:40 cutoff and no earliest, a dead day scans ~10 minutes then stops instead
		// of the full session. The per-minute earliest/latest guards below stay as a defensive backstop.
		// Oracle needs every minute for its best-EOD benchmark, so it is never trimmed.
		if (!_oracle && (earliestEntry.HasValue || latestEntry.HasValue) && timestampList.Count > 0)
			timestampList = timestampList.Where(t =>
			{
				var tod = MarketTime.ToEt(t).TimeOfDay;
				return (!earliestEntry.HasValue || tod >= earliestEntry.Value) && (!latestEntry.HasValue || tod <= latestEntry.Value);
			}).ToList();

		var batchSize = Math.Max(1, Environment.ProcessorCount);
		var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = batchSize, CancellationToken = cancellation };

		// Oracle (a lookahead research benchmark, not a simulation): score every minute against the book after the 09:30 pass, pick the
		// (minute, proposal) pair with the best expiry P&L, then advance the session to that minute and size the open from the book there.
		if (_oracle)
		{
			var minuteResults = new (DateTime MinuteEt, IReadOnlyList<OpenProposal> Proposals)?[timestampList.Count];
			await Parallel.ForEachAsync(Enumerable.Range(0, timestampList.Count), parallelOpts, async (idx, ct) =>
				minuteResults[idx] = await EvaluateMinuteAsync(timestampList[idx], closeUtc, tickerSet, barIndexByTicker, postMgmt, postCash, postAccount, postSignals, openEvaluator, ct));

			(DateTime MinuteEt, OpenProposal Proposal, decimal Pnl)? best = null;
			foreach (var mr in minuteResults)
			{
				if (mr == null) continue;
				foreach (var p in mr.Value.Proposals)
				{
					var qty = ResolveOpenQty(p);
					if (qty < 1 || BuildLegFillsFromProposal(p.Legs, qty) == null) continue;
					var eodPnl = await ComputeOracleEodPnlAsync(p, step.Date, cancellation);
					if (eodPnl != null && (best == null || eodPnl.Value > best.Value.Pnl))
						best = (mr.Value.MinuteEt, p, eodPnl.Value);
				}
			}
			if (best is { } b)
			{
				await AdvanceDayThroughAsync(step, evaluator, tickerSet, b.MinuteEt, cancellation);
				var qty = ResolveOpenQty(b.Proposal);
				if (qty >= 1 && BuildLegFillsFromProposal(b.Proposal.Legs, qty) is { } legFills)
					_book.Open(b.MinuteEt, b.Proposal.Ticker, b.Proposal.StructureKind, legFills, qty, b.Proposal.Spot, b.Proposal.RawScore, b.Proposal.FinalScore, b.Proposal.ImpliedVolatilityAnnual);
			}
			return new DailyOpenScanResult(HasIntraday: true, LegacyProposals: Array.Empty<OpenProposal>());
		}

		// Only the first qualifying minute is needed. Adaptive batch with early exit: a fixed ProcessorCount-wide batch evaluated ~32
		// minutes just to use minute 1 — and since the chain-seeding fix every minute carries a full chain, the FIRST scanned minute
		// almost always qualifies, so those other 31 minutes (each scoring ~6k skeletons) were pure waste and dominated the runtime.
		// Start with a 1-minute probe and double the batch only when early minutes don't qualify: the common case scores 1 minute, while
		// rare late/no-open days still grow to full parallel width. Chronological "first qualifying minute" semantics preserved.
		// A batch is scored against the book as it stands at its first minute; the session then advances minute by minute as its results
		// are read (management, cascade trades). When the book changes inside the batch (a close, a roll, a cascade open), the minutes from
		// there on were scored against a stale book (cash, held legs, position count), so the scan re-scores from that minute.
		var opened = 0;
		var entryDecided = false;
		var scanBatch = 1;
		for (int batchStart = 0; batchStart < timestampList.Count && !entryDecided && opened < _topNPerStep; )
		{
			cancellation.ThrowIfCancellationRequested();
			await AdvanceDayThroughAsync(step, evaluator, tickerSet, MarketTime.ToEt(timestampList[batchStart]).DateTime, cancellation);
			var positions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
			var bookVersion = _book.Fills.Count;
			var batchCount = Math.Min(scanBatch, timestampList.Count - batchStart);
			var batchResults = new (DateTime MinuteEt, IReadOnlyList<OpenProposal> Proposals)?[batchCount];
			var first = batchStart;
			await Parallel.ForEachAsync(Enumerable.Range(0, batchCount), parallelOpts, async (idx, ct) =>
				batchResults[idx] = await EvaluateMinuteAsync(timestampList[first + idx], closeUtc, tickerSet, barIndexByTicker, positions, cash, accountValue, postSignals, openEvaluator, ct));

			// Process batch results in chronological order.
			var next = batchStart + batchCount;
			for (int i = 0; i < batchCount && !entryDecided && opened < _topNPerStep; i++)
			{
				var minuteUtc = timestampList[batchStart + i];
				await AdvanceDayThroughAsync(step, evaluator, tickerSet, MarketTime.ToEt(minuteUtc).DateTime, cancellation);
				if (_book.Fills.Count != bookVersion) { next = batchStart + i; break; }
				if (batchResults[i] is not { } mr) continue;

				var minuteTimeEt = MarketTime.ToEt(minuteUtc).TimeOfDay;
				if (earliestEntry.HasValue && minuteTimeEt < earliestEntry.Value) continue;
				if (latestEntry.HasValue && minuteTimeEt > latestEntry.Value) continue;
				// Only the top-ranked candidate(s) up to _topNPerStep are ever attempted — never a lower-ranked fallback. entryDecided
				// latches ONLY on an actual successful open, not on merely having a candidate: a top pick refused by a downstream guard
				// (e.g. HeldLegGuard — it collides with an already-held position on the opposite side) must not forfeit the rest of the
				// day. The scan keeps checking later minutes (bounded by latestEntryTimeEt) for the SAME best-scoring opportunity to
				// clear, exactly as live re-evaluates every tick.
				foreach (var p in mr.Proposals.Take(_topNPerStep - opened))
				{
					var qty = ResolveOpenQty(p);
					if (qty < 1) continue;
					if (OpenProposalIntoBook(mr.MinuteEt, p, qty))
					{
						opened++;
						entryDecided = true;
					}
				}
			}
			batchStart = next;
			scanBatch = Math.Min(scanBatch * 2, batchSize);
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
		// opener's bootstrap would have no spot to work with. The minute's legs price off the quote store's
		// row M — the book struck at M+1:00 (QuoteStoreCache.BookInstant) — and the entry fills on that same
		// book, so the strike-selection spot is the price at that same instant: the bar's close. (An earlier
		// in-bar anchor — the open, then the (Open+Close)/2 midpoint mimicking live's :30 tick — paired a
		// pre-book spot with the M+1:00 book.) On gap-day 09:30 bars the official open print lags the option
		// book (composed-open lag: constituents stagger in over the first minute); the opener still corrects
		// any residual via ParitySpotGuard once the minute's chain is in hand.
		var minuteSpots = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in tickerSet)
		{
			if (!barIndexByTicker[t].TryGetValue(minuteUtc, out var bar)) return null;
			minuteSpots[t] = bar.Close;
		}

		// Convert the bar's UTC timestamp back to ET-naive (DateTimeKind.Unspecified) so it matches
		// the simulator's ET-wall-clock convention.
		var minuteEt = MarketTime.ToEt(minuteUtc).DateTime;

		var minuteOverrides = new QuoteOverrides(Spots: minuteSpots, ZeroDteWindow: ZeroDteWindow(minuteUtc, closeUtc));
		var postQuotes = await AIPipelineHelper.FetchQuotesWithHypotheticals(postMgmt, tickerSet, minuteEt, _quotes, _config, cancellation, minuteOverrides);
		var postCtx = new EvaluationContext(minuteEt, postMgmt, postQuotes.Underlyings, postQuotes.Options, postCash, postAccount, postSignals);
		var proposals = await openEvaluator.EvaluateAsync(postCtx, cancellation, minuteOverrides);
		// Mirror OpenerAutoExecutor: Informational proposals (the structure-coverage floor's best-of-each-
		// enabled-structure, surfaced under --log-level debug for visibility) are display-only and below the
		// open gate. Live filters them before executing; the backtest must too, or log level changes which
		// trades open (a debug-only candidate that doesn't clear MinScoreToOpen would otherwise fill here).
		return (minuteEt, proposals.Where(p => !p.Informational).ToList());
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
			var mid = OptionMath.HeldLegMark(q.Bid.Value, q.Ask.Value, leg.Side == Side.Buy);
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
			var survivorKey = _book.Expire(settleTime, key, bar.Close);

			// Partial expiry left a surviving leg (a calendar/diagonal's long after the short expired). Close
			// it at THIS day's close at market value rather than carrying it overnight. The daily management
			// pass already ran at the open, so an unclosed survivor would otherwise sit until the next day —
			// bleeding a day of theta and an overnight gap as an unintended naked directional leg. Closing at
			// market (not intrinsic) preserves the remaining extrinsic, which was the point of the partial settle.
			if (survivorKey != null)
				await CloseSurvivorAtCloseAsync(survivorKey, pos.Ticker, settleTime, bar.Close, cancellation);
		}
	}

	/// <summary>Closes a partial-expiry survivor at the short-expiry day's close, priced at market under the
	/// active pricing mode (mid/bidask) with the day's close as spot. Leaves it open only if a leg can't be
	/// priced — next day's management then handles it (the prior fallback behavior).</summary>
	private async Task CloseSurvivorAtCloseAsync(string survivorKey, string ticker, DateTime settleTime, decimal spotAtClose, CancellationToken cancellation)
	{
		if (!_book.OpenPositions.TryGetValue(survivorKey, out var pos)) return;
		var symbols = pos.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (symbols.Count == 0) return;

		var overrides = new QuoteOverrides(Spots: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spotAtClose });
		var snap = await _quotes.GetQuotesAsync(settleTime, symbols, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker }, cancellation, overrides);

		var fills = new List<BacktestLegFill>(pos.Legs.Count);
		foreach (var leg in pos.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!snap.Options.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
			{
				// Can't price → leave open for next-day management. Warn like a dropped rule action: silently
				// carrying the survivor reads as "the close never fired", and at the data frontier (--until today
				// before the evening pull) there IS no next day — the lineage ends the window half-settled.
				_droppedRuleActions++;
				Console.WriteLine($"⚠ {settleTime:yyyy-MM-dd}: CloseSurvivorOnShortExpiry for {ticker} {survivorKey} dropped — no NBBO for {leg.Symbol} at the {settleTime:HH:mm} settle — surviving leg left open. (Store gap: today's quotes land with the evening pull; historical gaps need a backfill.)");
				return;
			}
			var closeSide = leg.Side == Side.Buy ? Side.Sell : Side.Buy;
			fills.Add(new BacktestLegFill(leg.Symbol, closeSide, pos.Quantity, ExecPrice(closeSide, q.Bid.Value, q.Ask.Value)));
		}
		if (fills.Count > 0)
			_book.Close(settleTime, survivorKey, NoArb(fills), "CloseSurvivorOnShortExpiry", spotAtClose);
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
					legValue = OptionMath.HeldLegMark(q.Bid.Value, q.Ask.Value, leg.Side == Side.Buy);
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

	/// <summary>Pricing-provenance diagnostic. Under the quotes-only path every market-priced fill is backed
	/// by a real NBBO quote by construction — a leg with no quote is omitted from the snapshot, so the candidate
	/// never fills — so provenance is 100% real (captured == total) with no synthetic breakdown. Bucketed by leg
	/// DTE-at-fill (0DTE vs >0DTE) for the renderer; Expire fills are excluded (they settle at real bar.Close
	/// intrinsic, not a model price).</summary>
	private PricingProvenance ComputeProvenance()
	{
		int zTot = 0, mTot = 0;
		foreach (var fill in _book.Fills)
		{
			if (fill.Kind == BacktestFillKind.Expire) continue;
			foreach (var leg in fill.Legs)
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
				if (parsed?.CallPut == null) continue;
				var dte = (parsed.ExpiryDate.Date - fill.Date.Date).Days;
				if (dte <= 0) zTot++;
				else mTot++;
			}
		}
		// captured == total (all real), no synthetic legs → all the breakdown buckets are zero.
		return new PricingProvenance(zTot, zTot, mTot, mTot, 0, 0, 0, 0, 0, 0, 0);
	}

	/// <summary>Per-trade cleanliness split. Under the quotes-only path every market-priced fill used real NBBO
	/// by construction, so every finalized lineage is "clean" (none contaminated). Kept so the renderer can
	/// print the clean-trade P&L row.</summary>
	private CleanlinessBreakdown ComputeCleanliness(IEnumerable<long> openAtEndLineages)
	{
		var stillOpen = openAtEndLineages.ToHashSet();
		int cleanN = 0, cleanW = 0, cleanL = 0;
		decimal cleanPnl = 0m, cleanGrossWin = 0m, cleanGrossLoss = 0m;
		foreach (var g in _book.Fills.GroupBy(f => f.LineageId))
		{
			// Only fully finalized trades: a lineage still open at the window end (even half-settled — short
			// leg Expired, survivor unclosed) has outstanding exit proceeds and would book a phantom loss.
			if (stillOpen.Contains(g.Key) || !g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire)) continue;
			var pnl = g.Sum(f => f.NetCashFlow - f.Fees);
			cleanN++; cleanPnl += pnl;
			if (pnl > 0m) { cleanW++; cleanGrossWin += pnl; } else { cleanL++; cleanGrossLoss += Math.Abs(pnl); }
		}
		return new CleanlinessBreakdown(cleanN, cleanPnl, cleanW, cleanL, cleanGrossWin, cleanGrossLoss, 0, 0m, 0, 0);
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

/// <summary>How much of the backtest's realized fills were priced from real captured option bars vs the
/// synthetic Black-Scholes fallback, split by leg DTE-at-fill. A high multi-DTE synthetic fraction is a
/// red flag that calendar/diagonal P&L rests on modeled prices rather than real prints.</summary>
// MultiDteSurfaceIv/VixSmile/Intrinsic break down the SYNTHETIC (non-captured) >0DTE legs by how they were
// priced: SurfaceIv = interpolated off the real same-day smile of captured neighbors (high fidelity);
// VixSmile = parametric VIX1D-anchored ATM+smile (no neighbors found); Intrinsic = no usable IV at all.
// They sum to (MultiDteTotal - MultiDteCaptured). A high VixSmile share is the signal that cross-expiry
// interpolation would add value; a high SurfaceIv share means the synthetic legs are already neighbor-anchored.
// MultiDteVixBracketed/OneSided: of the MultiDteVixSmile (parametric-fallback) legs, how many had a nearby
// captured expiry (same root+right, within the probe's day-window) on BOTH sides of the target (genuine
// total-variance interpolation) vs only ONE side (extrapolation across the gap, far less reliable). The
// remainder (MultiDteVixSmile - bracketed - oneSided) is the irreducible floor: minutes where the whole
// local term structure was untraded, which no interpolation scheme can recover.
internal readonly record struct PricingProvenance(int ZeroDteCaptured, int ZeroDteTotal, int MultiDteCaptured, int MultiDteTotal, int MultiDteSurfaceIv, int MultiDteCrossExpiry, int MultiDteVixSmile, int MultiDteIntrinsic, int MultiDteVixBracketed, int MultiDteVixOneSided, int MultiDtePhantom)
{
	public double ZeroDteCapturedPct => ZeroDteTotal == 0 ? 1.0 : (double)ZeroDteCaptured / ZeroDteTotal;
	public double MultiDteCapturedPct => MultiDteTotal == 0 ? 1.0 : (double)MultiDteCaptured / MultiDteTotal;
	public int MultiDteSynthetic => MultiDteTotal - MultiDteCaptured;
}

/// <summary>Per-trade split by pricing cleanliness: "clean" = every market-priced fill of the trade used real
/// captured bars; "contaminated" = at least one leg was synthetic (entry/exit can be mispriced). Lets the
/// summary show whether the edge lives in the real-priced trades or the synthetic artifacts.</summary>
internal readonly record struct CleanlinessBreakdown(
	int CleanCount, decimal CleanPnl, int CleanWins, int CleanLosses, decimal CleanGrossWin, decimal CleanGrossLoss,
	int ContamCount, decimal ContamPnl, int ContamWins, int ContamLosses)
{
	public decimal CleanProfitFactor => CleanGrossLoss > 0m ? CleanGrossWin / CleanGrossLoss : 0m;
	/// <summary>Profit factor for display: gross win / gross loss, but "∞" when there were no losing trades
	/// (PF is undefined, not zero) and "—" when there were no clean trades at all.</summary>
	public string CleanProfitFactorDisplay =>
		CleanGrossLoss > 0m ? (CleanGrossWin / CleanGrossLoss).ToString("F2") : (CleanGrossWin > 0m ? "∞" : "—");
	public decimal CleanExpectancy => CleanCount > 0 ? CleanPnl / CleanCount : 0m;
	public decimal CleanWinRate => CleanCount > 0 ? CleanWins * 100m / CleanCount : 0m;
	public decimal ContamWinRate => ContamCount > 0 ? ContamWins * 100m / ContamCount : 0m;
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
	decimal MaxDrawdownPct,
	decimal PeakEquity,
	IReadOnlyList<(DateTime Date, decimal Equity)> EquityCurve,
	IReadOnlyList<BacktestFill> Fills,
	IReadOnlyDictionary<long, decimal> EndMtmByLineage,
	PricingProvenance Provenance,
	CleanlinessBreakdown Cleanliness,
	int DroppedRuleActions = 0,
	int NoArbClampedFills = 0,
	int BlindPositionDays = 0,
	int OpenPositionsFallbackMarked = 0,
	int LegCollisionBlockedOpens = 0)
{
	/// <summary>A lineage is still open at the window end when the book carried its position into the final
	/// MTM pass — even if it already booked a terminal fill (a diagonal whose short leg Expired but whose
	/// surviving long couldn't be closed for lack of quotes, e.g. a --until at today's data frontier).
	/// Keying "realized" off the mere presence of a Close/Expire fill booked such half-settled lifecycles as
	/// realized losses (the long's exit proceeds are still outstanding) while excluding their MTM from
	/// unrealized — distorting P&L, PF, expectancy and win rate whenever a survivor is left open.</summary>
	private bool IsOpenAtEnd(IGrouping<long, BacktestFill> g) =>
		EndMtmByLineage.ContainsKey(g.Key) || !g.Any(f => f.Kind == BacktestFillKind.Close || f.Kind == BacktestFillKind.Expire);

	/// <summary>P&L on closed lifecycles only (lineages fully finalized via Close/Expire — none of their
	/// legs survive at the window end). Each lifecycle's P&L = sum of (NetCashFlow - Fees) across all fills
	/// sharing its LineageId.</summary>
	public decimal RealizedPnL => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => !IsOpenAtEnd(g))
		.Sum(g => g.Sum(f => f.NetCashFlow - f.Fees));

	/// <summary>Unrealized P&L = per-lineage net cash + per-lineage final MTM, summed across lifecycles still
	/// open at the window end (including half-settled ones whose remaining legs the MTM pass marked).</summary>
	public decimal UnrealizedPnL => Fills
		.GroupBy(f => f.LineageId)
		.Where(IsOpenAtEnd)
		.Sum(g => g.Sum(f => f.NetCashFlow - f.Fees) + (EndMtmByLineage.TryGetValue(g.Key, out var m) ? m : 0m));

	public decimal TotalPnL => RealizedPnL + UnrealizedPnL;

	/// <summary>True when any lifecycle is still open at the window end (its P&L includes a final mark, not only realized cash).</summary>
	public bool HasOpenAtEnd => Fills.GroupBy(f => f.LineageId).Any(IsOpenAtEnd);

	public decimal EndingEquity => StartingCash + TotalPnL;

	/// <summary>Per-lifecycle wins/losses (closed lineages only).</summary>
	/// <summary>Per-closed-lifecycle realized P&L — one entry per lineage that ended in Close/Expire.
	/// The building block for per-trade edge stats (expectancy, win/loss averages, profit factor) which,
	/// unlike compounded terminal equity, don't depend on position sizing.</summary>
	public IReadOnlyList<decimal> LifecyclePnLs() => Fills
		.GroupBy(f => f.LineageId)
		.Where(g => !IsOpenAtEnd(g))
		.Select(g => g.Sum(f => f.NetCashFlow - f.Fees))
		.ToList();

	public (int wins, int losses) LifecycleWinLoss()
	{
		var closed = LifecyclePnLs();
		return (closed.Count(p => p > 0m), closed.Count(p => p <= 0m));
	}
}
