using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal sealed class AIWatchSettings : AILiveTickerSubcommandSettings
{
	[CommandOption("--tick <SECONDS>")]
	[Description("Override watch.tickIntervalSeconds, managementIntervalSeconds and openerIntervalSeconds.")]
	public int? Tick { get; set; }

	[CommandOption("--duration <DURATION>")]
	[Description("Stop after duration (e.g., 6h, 90m). Default: until market close.")]
	public string? Duration { get; set; }

	[CommandOption("--ignore-market-hours")]
	[Description("Run regardless of clock (for testing).")]
	public bool IgnoreMarketHours { get; set; }

	[CommandOption("--account <ALIAS>")]
	[Description("Account alias or ID from api-config.json. Mirrors `wa trade place --account`: overrides defaultAccount for this run. Affects both the live-position read and any auto-executed orders.")]
	public string? Account { get; set; }

	[CommandOption("--submit")]
	[Description("Override autoExecute.{management,opener}.submit=true for this run. Mirrors `wa trade place --submit`: keep config safe at dry-run, flip live from the CLI when ready.")]
	public bool Submit { get; set; }

	[CommandOption("--tif <VALUE>")]
	[Description("Override autoExecute.{management,opener}.timeInForce for this run. Mirrors `wa trade place --tif`: DAY (in-session only) or GTC (queues across sessions, accepted off-hours). Default: whatever config says, which itself defaults to DAY.")]
	public string? Tif { get; set; }

	[CommandOption("--start <TIME>")]
	[Description("Wait until this time (ET, HH:mm or HH:mm:ss) before the first tick. Overrides config startTime. Example: --start 09:30:30.")]
	public string? Start { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Tick.HasValue && (Tick.Value < 1 || Tick.Value > 3600))
			return ValidationResult.Error($"--tick: must be in [1, 3600], got {Tick.Value}");
		if (Duration != null && !TryParseDuration(Duration, out _))
			return ValidationResult.Error($"--duration: must be like '6h' or '90m', got '{Duration}'");
		if (Tif != null && !string.Equals(Tif, "day", StringComparison.OrdinalIgnoreCase) && !string.Equals(Tif, "gtc", StringComparison.OrdinalIgnoreCase))
			return ValidationResult.Error($"--tif: must be 'day' or 'gtc', got '{Tif}'");
		if (Start != null && !TimeOnly.TryParse(Start, CultureInfo.InvariantCulture, out _))
			return ValidationResult.Error($"--start: must be HH:mm or HH:mm:ss, got '{Start}'");

		return ValidationResult.Success();
	}

	internal static bool TryParseDuration(string s, out TimeSpan span)
	{
		span = default;
		if (string.IsNullOrWhiteSpace(s)) return false;
		var suffix = s[^1];
		var numPart = s[..^1];
		if (!int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
		span = suffix switch
		{
			'h' or 'H' => TimeSpan.FromHours(n),
			'm' or 'M' => TimeSpan.FromMinutes(n),
			's' or 'S' => TimeSpan.FromSeconds(n),
			_ => TimeSpan.Zero
		};
		return span != TimeSpan.Zero;
	}
}

internal sealed class AIWatchCommand : AsyncCommand<AIWatchSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AIWatchSettings settings, CancellationToken cancellation)
		=> await AITextOutput.RunAsync(settings, "AIWatch", async () =>
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;
		if (settings.Submit) { config.AutoExecute.Management.Submit = true; config.AutoExecute.Opener.Submit = true; AIContext.WarnIfSubmitInert(config); }
		if (settings.Tif != null) { config.AutoExecute.Management.TimeInForce = settings.Tif.ToUpperInvariant(); config.AutoExecute.Opener.TimeInForce = settings.Tif.ToUpperInvariant(); }
		if (settings.Start != null) config.Watch.StartTime = settings.Start;


		// --tick overrides every interval; otherwise each falls back to tickIntervalSeconds. The loop runs at the management cadence and starts the opener when it is due.
		var mgmtSeconds = settings.Tick ?? config.Watch.ManagementIntervalSeconds ?? config.Watch.TickIntervalSeconds;
		var openerInterval = TimeSpan.FromSeconds(settings.Tick ?? config.Watch.OpenerIntervalSeconds ?? config.Watch.TickIntervalSeconds);

		// Optional scheduled first-tick: wait until config.Watch.StartTime (ET) before entering the loop.
		// stopAt is computed AFTER the wait so --duration is relative to the start time, not launch time.
		// Past-time policy: explicit --start in the past is a user error; a config-only startTime in the
		// past means the user simply launched late, so we skip the wait and start immediately.
		if (!string.IsNullOrWhiteSpace(config.Watch.StartTime))
		{
			var target = ComputeStartTime(config.Watch.StartTime);
			var wait = target - DateTime.Now;
			if (wait <= TimeSpan.Zero)
			{
				if (settings.Start != null)
				{
					Console.Error.WriteLine($"Error: --start '{settings.Start}' is in the past (target {target:HH:mm:ss}). Aborting.");
					return 4;
				}
				AnsiConsole.MarkupLine($"[dim]watch.startTime {target:HH:mm:ss} already passed; starting immediately.[/]");
			}
			else
			{
				AnsiConsole.MarkupLine($"[dim]Waiting until {target:HH:mm:ss} ({wait.TotalMinutes:F1} min) before first tick...[/]");
				try { await Task.Delay(wait, cancellation); } catch (OperationCanceledException) { return 0; }
			}
		}

		var stopAt = ComputeStopTime(settings);

		AIContext.ConfigureRawQuoteDump(settings.Dump);
		var vendor = LiveQuoteSource.ResolveVendor(settings.Vendor);
		var positions = AIContext.BuildLivePositionSource(config, settings.Account);
		var quotes = AIContext.BuildLiveQuoteSource(vendor);
		var expiryRegimes = new Rules.ExpiryRegimeProvider();

		// Auto-executors: turn proposals into real (or dry-run) order submissions. Both off by default;
		// both honor enabled/submit gates independently. Shared with `wa ai scan` via AIContext.BuildAutoExecutors.
		var (autoExecutor, openerExecutor, repricer, brokerStops) = AIContext.BuildAutoExecutors(config, settings.Account);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config, settings.Pricing, expiryRegimes, Rules.TrailingStopStore.Default(), brokerStops != null ? brokerStops.IsProtected : null), config);

		// Apply the live 13-week T-bill risk-free rate once (mirrors `wa ai scan`): the opener's IV back-solve
		// and BS pricing read OptionMath.RiskFreeRate, so watch must set it too or it prices against a stale
		// default and diverges from scan/backtest. Fetched once per session — the rate barely moves intraday.
		YahooOptionsClient.ApplyToOptionMath(await Backtest.HistoricalRateCache.FetchLiveOrLatestAsync(cancellation));

		var priceCache = new Replay.HistoricalPriceCache();

		var sink = new ProposalSink(config.LogLevel, config.Ticker, config.Strategy, mode: "watch", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
		OpenProposalSink? openSink = null;
		OpenCandidateEvaluator? openEvaluator = null;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			openSink = new OpenProposalSink(config.LogLevel, config.Ticker, config.Strategy, mode: "watch", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			openEvaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache, enableChainSnapshot: true);
		}

		AnsiConsole.MarkupLine($"[bold]ai watch[/] ticker={config.Ticker} manage={mgmtSeconds}s opener={openerInterval.TotalSeconds:0}s stopAt={stopAt:HH:mm:ss} source={LiveQuoteSource.VendorName(vendor)}");

		// Mirror `wa ai scan`'s debug banner so watch is never silent in debug mode: when the opener
		// finds nothing the only other debug output (the per-structure breakdown) doesn't fire, leaving
		// no signal that debug is even on. This line plus the per-tick summary below close that gap.
		var debug = string.Equals(config.LogLevel, "debug", StringComparison.OrdinalIgnoreCase);
		if (debug) Console.Error.WriteLine($"[debug] wa ai watch: log-level=debug ticker={config.Ticker} opener.enabled={config.Opener.Enabled} emitOpen={settings.EmitOpenProposals} emitMgmt={settings.EmitManagementProposals} ignoreMktHours={settings.IgnoreMarketHours}");

		var failures = 0;
		var ticksRun = 0;
		var proposalsEmitted = 0;
		// Watch = "one live management tick in a loop, plus the opener on its own cadence". The opener never bypasses its daily cap here (that's a scan flag).
		// The gate serializes the two halves' broker-state pulls and order submissions (BrokerStateService / TodayOrdersFeed each hold one cycle token).
		var deps = new LiveTickDeps(positions, quotes, priceCache, evaluator, autoExecutor, openEvaluator, sink, openSink, openerExecutor,
			config, LiveQuoteSource.VendorName(vendor), settings.EmitManagementProposals, BypassOpenerDailyCap: false, Repricer: repricer, Stops: brokerStops,
			CascadeOpener: openEvaluator != null ? AIContext.BuildCascadeOpener(config, LiveQuoteSource.VendorName(vendor)) : null, ActionGate: new SemaphoreSlim(1, 1));
		var opener = new WatchOpenerRunner(deps, openEvaluator, openSink, debug);
		var openerDueAt = DateTime.MinValue;

		// Entry window (ET, opener.earliestEntryTimeEt..latestEntryTimeEt): outside it the watch loop stops SUBMITTING
		// opens — nulling the opener executor for the tick suppresses ONLY auto-execution (proposals still render,
		// logged as informational; management still runs). The backtest enforces the same window, so the two agree;
		// the earliest end was previously honored by the backtest only. Watch-only by construction: scan builds its
		// own deps and never nulls, so `wa ai scan --submit` is never time-gated.
		string? windowState = null;   // last state announced: "before" / "after" / null (inside)
		while (!cancellation.IsCancellationRequested && DateTime.Now < stopAt)
		{
			if (!settings.IgnoreMarketHours && !IsMarketOpen())
			{
				try { await Task.Delay(TimeSpan.FromSeconds(mgmtSeconds), cancellation); } catch (OperationCanceledException) { break; }
				continue;
			}

			// An opener run that hit an auth failure or too many consecutive failures ends the watch, as the same faults do for a management tick.
			if (opener.Unauthorized) return 2;
			if (opener.ConsecutiveFailures >= WatchOpenerRunner.WatchFailureLimit)
			{
				Console.Error.WriteLine($"Circuit breaker: {WatchOpenerRunner.WatchFailureLimit} consecutive opener run failures. Exiting.");
				return 3;
			}

			try
			{
				var now = DateTime.Now;
				// Layer-2: classify today's expiry-day regime once (first tick with a short dying today);
				// one extra chain probe per day, only while the knob is on.
				if (config.Rules.CloseBeforeShortExpiry.Regime.Enabled && !expiryRegimes.Has(config.Ticker, now.Date))
				{
					var regimePositions = await positions.GetOpenPositionsAsync(now, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.Ticker }, cancellation);
					await Rules.ExpiryRegimeHost.PopulateAsync(expiryRegimes, config.Rules.CloseBeforeShortExpiry.Regime, regimePositions.Values, quotes, now, cancellation);
				}
				var etTimeOfDay = MarketTime.ToEt(now).TimeOfDay;
				var outsideWindow = !config.Opener.IsWithinEntryWindow(etTimeOfDay);
				var state = !outsideWindow ? null : ParsingHelpers.TryParseClockTime(config.Opener.EarliestEntryTimeEt, out var earliest) && etTimeOfDay < earliest ? "before" : "after";
				if (state != windowState)
				{
					if (state == "before") AnsiConsole.MarkupLine($"[dim]before earliest-entry {config.Opener.EarliestEntryTimeEt} ET — opens withheld until then (managing only).[/]");
					else if (state == "after") AnsiConsole.MarkupLine($"[dim]past latest-entry {config.Opener.LatestEntryTimeEt} ET — opens no longer submitted (managing only).[/]");
					windowState = state;
				}
				var tick = await LiveTick.ManagementTickAsync(now, deps, cancellation);
				var mgmtEmitted = settings.EmitManagementProposals ? tick.MgmtCount : 0;
				proposalsEmitted += mgmtEmitted;

				// Per-tick pulse. With an opener configured it prints its own line per run, so a management tick speaks only when it emitted something; with none, every tick
				// still gets a timestamped line so a quiet watch is anchored in time (no silent blinking caret). Suppressed at debug, where the [debug] line carries it.
				if (debug) Console.Error.WriteLine($"[debug] {now:HH:mm:ss} tick {ticksRun + 1}: spot={tick.Spot} positions={tick.PositionCount} mgmtResults={tick.MgmtCount}");
				else if (openEvaluator == null || mgmtEmitted > 0)
				{
					var spotStr = tick.Spot.HasValue ? tick.Spot.Value.ToString("0.00") : "?";
					AnsiConsole.MarkupLine($"[dim]{now:HH:mm:ss}  {config.Ticker} {spotStr} — {(mgmtEmitted == 0 ? "no proposals emitted" : $"{mgmtEmitted} proposal(s) emitted")}[/]");
				}

				// The opener runs in the background off this tick's frame once it is due and the previous run has finished, so scoring never delays the next management tick.
				if (openEvaluator != null && tick.Frame != null && opener.Idle && now >= openerDueAt)
				{
					opener.Start(tick.Frame, tick.MgmtCount, outsideWindow, cancellation);
					openerDueAt = now + openerInterval;
				}

				ticksRun++;
				failures = 0;
			}
			// Only a cancellation of the loop's own token ends it. An HTTP timeout surfaces as TaskCanceledException (an OperationCanceledException)
			// with that token untouched: it is a failed tick, counted toward the circuit breaker below, not a request to stop (2026-11-16 09:52, a
			// timed-out tick ended the loop silently: "Loop exited. ticks=5 proposals=0 failures=0").
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
			catch (UnauthorizedAccessException ex)
			{
				Console.Error.WriteLine($"Auth failure: {ex.Message}. Exiting.");
				await opener.DrainAsync();
				return 2;
			}
			catch (Exception ex)
			{
				failures++;
				AnsiConsole.MarkupLine($"[red]Tick {ticksRun + 1} failed ({failures}/{WatchOpenerRunner.WatchFailureLimit}): {Markup.Escape(WatchOpenerRunner.ExceptionChain(ex))}[/]");
				if (failures >= WatchOpenerRunner.WatchFailureLimit)
				{
					Console.Error.WriteLine($"Circuit breaker: {WatchOpenerRunner.WatchFailureLimit} consecutive tick failures. Exiting.");
					await opener.DrainAsync();
					return 3;
				}
			}

			try { await Task.Delay(TimeSpan.FromSeconds(mgmtSeconds), cancellation); } catch (OperationCanceledException) { break; }
		}

		await opener.DrainAsync();
		proposalsEmitted += opener.ProposalsEmitted;
		AnsiConsole.MarkupLine($"[dim]Loop exited. ticks={ticksRun} proposals={proposalsEmitted} failures={failures}[/]");
		return 0;
	});

	// NYSE regular session, hardcoded. The watch loop is the only consumer; every other code path
	// reuses MarketCalendar.IsOpen for day-of-week / holiday handling. If we ever need to support a
	// different exchange, lift these constants — but the rest of the app assumes US equity/options too.
	private static readonly TimeSpan MarketCloseEt = new(16, 0, 0);

	private static DateTime ComputeStopTime(AIWatchSettings s)
	{
		if (s.Duration != null && AIWatchSettings.TryParseDuration(s.Duration, out var span))
			return DateTime.Now + span;
		// Default: today's market close in ET.
		var nowLocal = MarketTime.NowEt();
		var closeLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, MarketCloseEt.Hours, MarketCloseEt.Minutes, 0, DateTimeKind.Unspecified);
		return MarketTime.EtToUtc(closeLocal).ToLocalTime();
	}

	/// <summary>Parses HH:mm or HH:mm:ss as today's ET time-of-day and returns the equivalent local DateTime.</summary>
	private static DateTime ComputeStartTime(string hhmm)
	{
		var t = TimeOnly.Parse(hhmm, CultureInfo.InvariantCulture);
		var nowEt = MarketTime.NowEt();
		var targetEt = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);
		return MarketTime.EtToUtc(targetEt).ToLocalTime();
	}

	private static bool IsMarketOpen()
	{
		return MarketCalendar.IsRegularHours(DateTimeOffset.Now);
	}
}
