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
	bool BypassOpenerDailyCap);

/// <summary>Mutable state that must persist across ticks (watch) but reset per run (scan).</summary>
internal sealed class LiveTickState
{
	/// <summary>Gates the one-time "staleness unverifiable on this vendor" note.</summary>
	public bool StaleUnverifiableNoted;
}

/// <summary>Outcome of one tick, for the caller's own reporting (watch's per-tick pulse, scan's completion
/// line). <see cref="Aborted"/> is true only when a spot-override hook rejected the tick.</summary>
internal readonly record struct LiveTickResult(int PositionCount, int MgmtCount, int OpenCount, decimal? Spot, bool Aborted);

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
		var config = deps.Config;
		var tickerSet = config.TickerSet();

		// One broker-state pull per tick, shared by both auto-executors. A fresh token each tick lets
		// BrokerStateService coalesce the management+opener refreshes into one Webull order-endpoint round-trip.
		var cycleToken = new object();
		var openPositions = await deps.Positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
		var (cash, accountValue) = await deps.Positions.GetAccountStateAsync(now, cancellation);
		var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, deps.Quotes, config, cancellation);
		// No-op during RTH; corrects the stale chain spot on premarket ticks (--ignore-market-hours runs).
		quoteSnapshot = await PremarketSpotOverride.ApplyAsync(quoteSnapshot, config, deps.Quotes, now, cancellation);
		if (applySpotOverrides != null)
		{
			var overridden = await applySpotOverrides(quoteSnapshot);
			if (overridden == null) return new LiveTickResult(openPositions.Count, 0, 0, null, Aborted: true);
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

		var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals, HistoricalVolByTicker: historicalVolByTicker);
		var results = deps.Evaluator.Evaluate(ctx);
		if (deps.EmitManagement && deps.MgmtSink != null)
			foreach (var r in results) deps.MgmtSink.Emit(r.Proposal, r.IsRepeat);
		if (deps.MgmtExecutor != null)
			await deps.MgmtExecutor.HandleAsync(results, ctx, cancellation, cycleToken);

		var openCount = 0;
		if (deps.OpenEvaluator != null && deps.OpenSink != null)
		{
			var openResults = await deps.OpenEvaluator.EvaluateAsync(ctx, cancellation);
			openCount = openResults.Count;
			// LIVE quote-integrity guard: warn loudly on a stale feed or torn NBBO (the 07-13 SPY case: long
			// leg bid 10.36 / ask 20.36) and withhold the affected opens from auto-execution. Proposals still
			// render so the issue stays visible amid a fast-scrolling watch.
			var nowOffset = new DateTimeOffset(now);
			var (feedStale, suspect) = LiveQuoteGuard.Inspect(quoteSnapshot.Options, nowOffset, MarketCalendar.IsRegularHours(nowOffset), deps.VendorName, config.Opener.QuoteGuard, openResults, ref state.StaleUnverifiableNoted);
			for (var i = 0; i < openResults.Count; i++) deps.OpenSink.Emit(openResults[i], rank: i + 1);
			if (deps.OpenerExecutor != null)
			{
				// A stale feed poisons every quote → hold all opens this tick; otherwise drop only the
				// torn-quote proposals and let clean lower-ranked ones proceed.
				var safeToOpen = feedStale ? new List<OpenProposal>() : openResults.Where((_, i) => !suspect.Contains(i)).ToList();
				await deps.OpenerExecutor.HandleAsync(safeToOpen, openPositions, now, cancellation, bypassDailyCap: deps.BypassOpenerDailyCap, cycleToken: cycleToken);
			}
		}

		var spot = quoteSnapshot.Underlyings.TryGetValue(config.Ticker, out var s) ? s
			: (deps.OpenEvaluator?.LastUnderlyings.TryGetValue(config.Ticker, out var hb) ?? false) ? hb
			: (decimal?)null;
		return new LiveTickResult(openPositions.Count, results.Count, openCount, spot, Aborted: false);
	}
}
