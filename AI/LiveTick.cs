using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>Long-lived collaborators + per-run flags for one live evaluation tick. Built once by the caller
/// (once per process for <c>wa ai scan</c>; once before the loop for <c>wa ai watch</c>) and reused across
/// ticks. Sinks are owned by the caller so their lifecycle (single reuse vs per-run) stays caller-controlled.</summary>
internal sealed record LiveTickDeps(
	IPositionSource Positions,
	IQuoteSource Quotes,
	Replay.HistoricalPriceCache PriceCache,
	RuleEvaluator Evaluator,
	ManagementAutoExecutor? MgmtExecutor,
	OpenCandidateEvaluator? OpenEvaluator,
	ProposalSink? MgmtSink,
	OpenProposalSink? OpenSink,
	OpenerAutoExecutor? OpenerExecutor,
	AIConfig Config,
	string VendorName,
	bool EmitManagement,
	bool BypassOpenerDailyCap,
	bool OutsideEntryWindow = false,
	OrderRepricer? Repricer = null,
	Open.Cascade.CascadeOpener? CascadeOpener = null,
	BrokerStopManager? Stops = null,
	SemaphoreSlim? ActionGate = null,
	LatestCycle? Latest = null);

/// <summary>Outcome of a management-only tick: the frame it read (which the opener can score on) and what the watch pulse reports.</summary>
internal readonly record struct ManagementTickResult(LiveTickFrame? Frame, int PositionCount, int MgmtCount, decimal? Spot);

/// <summary>The broker-state cycle token of the most recent completed management tick. The opener executes against it instead of its own older frame's token, so the
/// order snapshot the management tick just pulled is reused rather than pulled again (each pull is two gated Webull reads).</summary>
internal sealed class LatestCycle
{
	private object? _token;
	public object? Token { get => Volatile.Read(ref _token); set => Volatile.Write(ref _token, value); }
}

/// <summary>Mutable state that must persist across ticks (watch) but reset per run (scan).</summary>
internal sealed class LiveTickState
{
	/// <summary>Gates the one-time "staleness unverifiable on this vendor" note.</summary>
	public bool StaleUnverifiableNoted;
}

/// <summary>Outcome of one tick, for the caller's own reporting (watch's per-tick pulse, scan's completion
/// line). <see cref="Aborted"/> is true only when a spot-override hook rejected the tick.</summary>
internal readonly record struct LiveTickResult(int PositionCount, int MgmtCount, int OpenCount, decimal? Spot, bool Aborted);

/// <summary>Everything one tick reads once and hands to both phases: the broker-state cycle token, open positions, the corrected quote snapshot and the assembled evaluation context.</summary>
internal sealed record LiveTickFrame(DateTime Now, object CycleToken, IReadOnlyDictionary<string, OpenPosition> OpenPositions, QuoteSnapshot Quotes, EvaluationContext Context, bool MarketOpen, IReadOnlyList<DividendEvent>? Dividends);

/// <summary>The single live evaluation tick shared by <c>wa ai watch</c> (run in a loop) and <c>wa ai scan</c>
/// (run once): pull positions + account, fetch quotes, correct premarket spot, compute technicals, evaluate
/// management + opener proposals, emit them, run the quote-integrity guard, and auto-execute the survivors.
/// Behavioural divergences between the two commands are expressed as flags on <see cref="LiveTickDeps"/> and
/// the optional <paramref name="applySpotOverrides"/> hook (scan's <c>--spot</c>/<c>--premarket</c>), so this
/// method reproduces each command's prior behaviour exactly.</summary>
internal static class LiveTick
{
	public static async Task<LiveTickResult> EvaluateAsync(DateTime now, LiveTickDeps deps, LiveTickState state, Func<QuoteSnapshot, Task<QuoteSnapshot?>>? applySpotOverrides, CancellationToken cancellation)
	{
		var (frame, positionCount) = await BuildFrameAsync(now, deps, applySpotOverrides, cancellation);
		if (frame == null) return new LiveTickResult(positionCount, 0, 0, null, Aborted: true);
		var mgmtCount = await ManageAsync(frame, deps, cancellation);
		var (openCount, spot) = await OpenAsync(frame, deps, state, cancellation);
		return new LiveTickResult(positionCount, mgmtCount, openCount, spot, Aborted: false);
	}

	/// <summary>The management half of a tick, for a loop that runs the opener on its own cadence: reads broker state and quotes, runs the rules and syncs the broker stops, all under
	/// <see cref="LiveTickDeps.ActionGate"/> so the broker-state pulls and order submissions of the two halves never interleave. The returned frame is what the opener scores on.</summary>
	public static async Task<ManagementTickResult> ManagementTickAsync(DateTime now, LiveTickDeps deps, CancellationToken cancellation)
	{
		using var lease = await EnterAsync(deps.ActionGate, cancellation);
		var (frame, positionCount) = await BuildFrameAsync(now, deps, null, cancellation);
		var mgmtCount = await ManageAsync(frame!, deps, cancellation);
		if (deps.Latest != null) deps.Latest.Token = frame!.CycleToken;
		return new ManagementTickResult(frame, positionCount, mgmtCount, frame!.Quotes.Underlyings.TryGetValue(deps.Config.Ticker, out var spot) ? spot : null);
	}

	/// <summary>The opener half of a tick, scored on a frame a management tick read. Only the order-submitting end runs under <see cref="LiveTickDeps.ActionGate"/>; scoring does not, so it never holds up management.</summary>
	public static Task<(int OpenCount, decimal? Spot)> OpenerTickAsync(LiveTickFrame frame, LiveTickDeps deps, LiveTickState state, CancellationToken cancellation) => OpenAsync(frame, deps, state, cancellation);

	private sealed class GateLease(SemaphoreSlim? gate) : IDisposable
	{
		public void Dispose() => gate?.Release();
	}

	private static async Task<IDisposable> EnterAsync(SemaphoreSlim? gate, CancellationToken cancellation)
	{
		if (gate != null) await gate.WaitAsync(cancellation);
		return new GateLease(gate);
	}

	/// <summary>The shared read phase of a tick: broker state, quotes (with the premarket/--spot corrections), technicals and realized vol, assembled into
	/// the <see cref="EvaluationContext"/> both the management and opener phases read. Returns a null frame when a spot-override hook rejected the tick.</summary>
	private static async Task<(LiveTickFrame? Frame, int PositionCount)> BuildFrameAsync(DateTime now, LiveTickDeps deps, Func<QuoteSnapshot, Task<QuoteSnapshot?>>? applySpotOverrides, CancellationToken cancellation)
	{
		var config = deps.Config;
		var tickerSet = config.TickerSet();

		// One broker-state pull per tick, shared by the position source and both auto-executors. A fresh token each tick lets
		// TodayOrdersFeed/BrokerStateService coalesce their pulls into one Webull order-history round-trip.
		var cycleToken = new object();
		var positionsTask = deps.Positions.GetOpenPositionsAsync(now, tickerSet, cancellation, cycleToken);
		var accountTask = deps.Positions.GetAccountStateAsync(now, cancellation);
		await Task.WhenAll(positionsTask, accountTask);
		var openPositions = await positionsTask;
		var (cash, accountValue) = await accountTask;
		var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, deps.Quotes, config, cancellation);
		// No-op during RTH; corrects the stale chain spot on premarket ticks (--ignore-market-hours runs).
		quoteSnapshot = await PremarketSpotOverride.ApplyAsync(quoteSnapshot, config, deps.Quotes, now, cancellation);
		if (applySpotOverrides != null)
		{
			var overridden = await applySpotOverrides(quoteSnapshot);
			if (overridden == null) return (null, openPositions.Count);
			quoteSnapshot = overridden;
		}
		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, deps.PriceCache, config.Indicators.TechnicalFilter, now, cancellation);

		// Underlying 20-session realized vol per ticker — the vendor-independent HV shown in the risk
		// diagnostic (never the vendor's per-contract hiv). Cheap: HistoricalPriceCache serves repeat reads.
		// Computed for BOTH watch and scan: scan previously omitted it, which silently changed the opener's
		// volatility scoring vs watch — a real inconsistency, now unified here.
		var historicalVolByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var ticker in tickerSet)
		{
			var closes = await deps.PriceCache.GetRecentClosesAsync(ticker, Math.Max(config.Opener.VolatilityLookbackDays + 1, 4), now, cancellation);
			var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
			if (hv is > 0m) historicalVolByTicker[ticker] = hv.Value;
		}
		var realizedVolByTicker = new Dictionary<string, RealizedVolModel>(StringComparer.OrdinalIgnoreCase);
		foreach (var ticker in tickerSet)
			if (await RealizedVolModel.LoadAsync(deps.PriceCache, ticker, now, config.Opener.ImpliedVolForecast, cancellation) is { } model)
				realizedVolByTicker[ticker] = model;

		var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals, HistoricalVolByTicker: historicalVolByTicker, RealizedVolByTicker: realizedVolByTicker);
		// Frozen-book guard inputs (see QuoteSanity.ParityGaps): RTH-only, and the dividend schedule only when it will be used.
		var marketOpen = MarketCalendar.IsRegularHours(new DateTimeOffset(now));
		var guardCfg = config.Opener.QuoteGuard;
		var dividends = guardCfg is { Enabled: true } && marketOpen ? await LoadDividendsAsync(config, ReportedSpot(quoteSnapshot, deps, config.Ticker) ?? 0m, now, cancellation) : null;
		return (new LiveTickFrame(now, cycleToken, openPositions, quoteSnapshot, ctx, marketOpen, dividends), openPositions.Count);
	}

	/// <summary>The management phase: evaluate the rules, emit proposals, step resting limits, auto-execute the survivors and keep each position's broker stop at its trail level. Returns the number of rule results.</summary>
	private static async Task<int> ManageAsync(LiveTickFrame frame, LiveTickDeps deps, CancellationToken cancellation)
	{
		var (now, cycleToken, _, quoteSnapshot, ctx, marketOpen, dividends) = frame;
		var config = deps.Config;
		var guardCfg = config.Opener.QuoteGuard;
		var results = deps.Evaluator.Evaluate(ctx);
		if (deps.EmitManagement && deps.MgmtSink != null)
			foreach (var r in results) deps.MgmtSink.Emit(r.Proposal, r.IsRepeat);
		// Step resting auto-execute limits (in-place replace — same order ids, so dedup and the daily cap are unaffected).
		if (deps.Repricer != null)
			await deps.Repricer.TickAsync(now, cancellation);

		if (deps.MgmtExecutor != null)
		{
			// A frozen book misprices the marks every exit rule reads, so a take-profit/stop could fire (or miss) on numbers the
			// market isn't trading: hold auto-execution of management proposals on frozen expiries. They still render above.
			var mgmtFrozen = results.Count == 0 ? new HashSet<DateTime>() : LiveQuoteGuard.FrozenExpiries(quoteSnapshot.Options, config.Ticker, ReportedSpot(quoteSnapshot, deps, config.Ticker), now, marketOpen, deps.VendorName, dividends, guardCfg, "management auto-execution");
			var mgmtResults = mgmtFrozen.Count == 0 ? results : results.Where(r => !QuoteSanity.TouchesExpiry(r.Proposal.Legs, mgmtFrozen)).ToList();
			await deps.MgmtExecutor.HandleAsync(mgmtResults, ctx, cancellation, cycleToken);
		}

		// Keep each position's broker stop at its trail level. After the management executor, so a close it just sent cancelled the stop first; the stop
		// manager only looks at positions that still exist.
		if (deps.Stops != null)
			await deps.Stops.SyncAsync(ctx, cycleToken, cancellation);

		return results.Count;
	}

	/// <summary>The opener phase: enumerate and score open candidates (plus cascade), emit them, run the live quote-integrity guard and auto-execute the survivors. Returns the open-proposal count and the ticker's spot for the caller's pulse.</summary>
	private static async Task<(int OpenCount, decimal? Spot)> OpenAsync(LiveTickFrame frame, LiveTickDeps deps, LiveTickState state, CancellationToken cancellation)
	{
		var (now, cycleToken, openPositions, quoteSnapshot, ctx, marketOpen, dividends) = frame;
		var config = deps.Config;
		var guardCfg = config.Opener.QuoteGuard;
		var nowOffset = new DateTimeOffset(now);
		var openCount = 0;
		if (deps.OpenEvaluator != null && deps.OpenSink != null)
		{
			var openResults = await deps.OpenEvaluator.EvaluateAsync(ctx, cancellation);
			// Outside the entry window watch submits nothing, so every proposal is display-only: log it as informational,
			// or consumers that read the log as "what live would have placed" (wa ai backtest --replay, wa ai verify)
			// treat a 13:04 proposal past an 11:00 cutoff as a live open (2026-10-08).
			if (deps.OutsideEntryWindow) openResults = openResults.Select(p => p with { Informational = true }).ToList();
			// Cascade trades are signal-driven and enter once, at a setup's first confirmation: outside the entry window the regime is
			// read and its panel shown, but no confirmation is recorded, so the first confirmation inside the window is the one that trades.
			var cascadeResults = deps.CascadeOpener != null ? await deps.CascadeOpener.EvaluateAsync(ctx, cancellation, entryWindowOpen: !deps.OutsideEntryWindow) : Array.Empty<OpenProposal>();
			if (cascadeResults.Count > 0) openResults = openResults.Concat(cascadeResults).ToList();
			openCount = openResults.Count;
			// LIVE quote-integrity guard: warn loudly on a stale feed or torn NBBO (the 07-13 SPY case: long
			// leg bid 10.36 / ask 20.36) and withhold the affected opens from auto-execution. Proposals still
			// render so the issue stays visible amid a fast-scrolling watch.
			// Inspect the book the opener actually priced these proposals from, not the management-side ctx book.
			// On a no-position scan ctx only spans the same-day expiry window, so a ticker whose earliest expiry
			// isn't today (GME) leaves ctx empty of the traded legs — the guard would mis-report staleness as
			// "unverifiable" and skip torn-NBBO on legs it can't see. Falls back to ctx when the opener enumerated
			// nothing (empty priced book → no proposals to guard anyway).
			IReadOnlyDictionary<string, OptionContractQuote> guardBook = deps.OpenEvaluator?.LastPricedQuotes is { Count: > 0 } priced ? priced : quoteSnapshot.Options;
			// A cascade proposal was priced from the chain its reading was scored on.
			if (cascadeResults.Count > 0) guardBook = new OverlayQuoteDictionary(guardBook, deps.CascadeOpener!.LastQuotes);
			var openFrozen = openResults.Count == 0 ? new HashSet<DateTime>() : LiveQuoteGuard.FrozenExpiries(guardBook, config.Ticker, ReportedSpot(quoteSnapshot, deps, config.Ticker), now, marketOpen, deps.VendorName, dividends, guardCfg, "opens");
			var (feedStale, suspect) = LiveQuoteGuard.Inspect(guardBook, nowOffset, marketOpen, deps.VendorName, guardCfg, openResults, openFrozen, ref state.StaleUnverifiableNoted);
			// A cascade trade is already shown in the cascade panel's trade section; it is only logged here.
			for (var i = 0; i < openResults.Count; i++) deps.OpenSink.Emit(openResults[i], rank: i + 1, console: !cascadeResults.Contains(openResults[i]));
			if (deps.OpenerExecutor != null)
			{
				// A stale feed poisons every quote → hold all opens this tick; otherwise drop only the
				// proposals with a torn leg or a leg on a frozen expiry and let clean lower-ranked ones proceed.
				var safeToOpen = feedStale ? new List<OpenProposal>() : openResults.Where((_, i) => !suspect.ContainsKey(i)).ToList();
				for (var i = 0; i < openResults.Count; i++)
					if (!openResults[i].Informational && (feedStale || suspect.ContainsKey(i)))
						deps.OpenSink.EmitAutoExecute(openResults[i], "skipped", "quote guard", feedStale ? "stale feed" : suspect[i]);
				using var lease = await EnterAsync(deps.ActionGate, cancellation);
				// Read inside the gate: no management tick can run while this holds it, so this is the freshest snapshot there will be.
				var executionToken = deps.Latest?.Token ?? cycleToken;
				await deps.OpenerExecutor.HandleAsync(safeToOpen.Where(p => !cascadeResults.Contains(p)).ToList(), openPositions, now, cancellation, bypassDailyCap: deps.BypassOpenerDailyCap, cycleToken: executionToken, log: deps.OpenSink);
				// A cascade trade goes through on its own so its outcome is known: only one the executor acted on uses up its setup's
				// signal for the day; one held back (quote guard above, or a skip inside the executor) is proposed again on a later read.
				foreach (var cascadeOpen in safeToOpen.Where(cascadeResults.Contains))
					if (await deps.OpenerExecutor.HandleAsync(new[] { cascadeOpen }, openPositions, now, cancellation, bypassDailyCap: deps.BypassOpenerDailyCap, cycleToken: executionToken, log: deps.OpenSink) > 0)
						deps.CascadeOpener!.Acted(cascadeOpen);
			}
			else
			{
				// No auto-executor: the proposals are display-only, so showing one is all that will ever happen to it.
				foreach (var cascadeOpen in cascadeResults) deps.CascadeOpener!.Acted(cascadeOpen);
			}
		}

		var spot = quoteSnapshot.Underlyings.TryGetValue(config.Ticker, out var s) ? s
			: (deps.OpenEvaluator?.LastUnderlyings.TryGetValue(config.Ticker, out var hb) ?? false) ? hb
			: deps.CascadeOpener?.LastSpot;
		return (openCount, spot);
	}

	/// <summary>The vendor-reported spot the frozen-book guard checks the book against — never a <see cref="ParitySpotGuard"/>-corrected
	/// one, which derives from the very book under test. Flat ticks carry no management-side spot, so the opener's pre-correction
	/// bootstrap spot fills in, or the cascade opener's last read when the standard opener enumerates nothing.</summary>
	private static decimal? ReportedSpot(QuoteSnapshot snapshot, LiveTickDeps deps, string ticker)
	{
		if (snapshot.ReportedUnderlyings != null && snapshot.ReportedUnderlyings.TryGetValue(ticker, out var reported)) return reported;
		if (snapshot.Underlyings.TryGetValue(ticker, out var spot)) return spot;
		if (deps.OpenEvaluator != null && deps.OpenEvaluator.LastReportedUnderlyings.TryGetValue(ticker, out var boot)) return boot;
		return deps.CascadeOpener?.LastSpot;
	}

	/// <summary>The ticker's discrete dividend schedule (same source and fallback chain the scorer prices with), so parity on an expiry
	/// past an ex-date isn't read as a frozen book.</summary>
	private static async Task<IReadOnlyList<DividendEvent>?> LoadDividendsAsync(AIConfig config, decimal spot, DateTime now, CancellationToken cancellation)
	{
		var calendar = await Events.EventCalendarLoader.LoadAsync(new[] { config.Ticker }, config.Indicators.Events, now, cancellation);
		return Pricing.DividendScheduleBuilder.BuildForTicker(calendar.Get(config.Ticker), spot, config.Indicators.Events);
	}
}
