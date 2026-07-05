using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Sentiment;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

/// <summary>`wa ai history <ticker>` — populate the historical OHLC caches used by `wa ai backtest`.
/// Separation of concerns: the fetch step hits the network here (where transient Yahoo failures are
/// loud and re-runnable), while the backtest runs purely offline against the cache. Mirrors the
/// `wa fetch` / `wa report` split. SPX-family tickers (SPY, SPX, SPXW, XSP) also pull VIX, VIX1D, and
/// VIX9D, since the backtest reads ATM IV from the appropriate term (VIX1D for 0–1 DTE, VIX9D for
/// 2–9 DTE, VIX for longer) and the opener reads the VIX term-structure regime score.
///
/// Intraday backfill: if api-config.json has a populated <c>massiveApiKey</c>, also fills
/// <c>data/intraday/<TICKER>/<date>.csv</c> for every trading day in the lookback window
/// whose file is missing or partial. Closes the gap when the live bot was offline (holidays, outages,
/// late starts). Today's date is never touched — the live Webull capture owns it.
///
/// Option contracts: every run also fetches the live option chain and registers each contract's Webull
/// derivativeId. Ids are perishable — they drop off the chain at expiry, and the id is the only key back
/// to a contract's minute/IV history via the chart endpoint — so banking them here lets `wa options
/// discover`/`backfill` pull that data any time later. Run during market hours to capture same-day 0DTE
/// ids. `--partial` does NOT change this: both plain and `--partial` runs register the full chain.</summary>
internal sealed class AIHistorySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., SPY, QQQ, GME). SPX family also refreshes VIX + VIX1D + VIX9D.")]
	public string Ticker { get; set; } = "";

	[CommandOption("--lookback-years <N>")]
	[Description("How far back to ensure the cache covers when starting fresh. Default: 2.")]
	public int LookbackYears { get; set; } = 2;

	[CommandOption("--audit")]
	[Description("Report intraday CSV completeness over the full on-disk history (earliest CSV through yesterday). Audit's window is determined from on-disk CSVs, so it is mutually exclusive with --lookback-years. No network fetches. Reconciles sealed.json with any LooksComplete file not yet listed. Exit code 0 if every trading day is complete, 2 otherwise.")]
	public bool Audit { get; set; }

	[CommandOption("--import-webull-spx <file>")]
	[Description("One-time bootstrap: import a text file of SPX query-mini rows (one per line, `ts,o,c,h,l,prevClose,vol,vwap` format) sniffed from Webull's web app and merge with SPY ext-hours pulled per-day from the API. Used to load 2 years of historical SPX intraday — Webull's chart endpoint requires per-URL x-s signatures we can't forge, so deep history is captured via a browser console sniffer that records the chart's own signed requests. SPY ext-hours doesn't need signatures and is fetched here.")]
	public string? ImportWebullSpxFile { get; set; }

	[CommandOption("--import <file>")]
	[Description("One-time bootstrap: import a text file of query-mini rows (same sniffed format as --import-webull-spx) for THIS ticker as-is — no SPY ext-hours merge or scaling. Used for standalone index tapes (VIX): capture via scripts/webscraper.js on the ticker's 1-min chart, scroll back as far as Webull serves, __dumpBars(), then import here. Ongoing top-up is the normal daily run.")]
	public string? ImportFile { get; set; }

	[CommandOption("--partial")]
	[Description("Capture today's incomplete intraday tape (up to the minute it's run) from Webull's live chart endpoint and write data/intraday/<ticker>/<today>.csv. Use when the live `wa ai watch` session was missed (outage, late start) — the normal backfill skips today (it expects live capture to own it). RTH only for SPX-family; the file is NOT sealed (the session isn't done). Mutually exclusive with --audit and --import-webull-spx.")]
	public bool Partial { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (LookbackYears < 1) return ValidationResult.Error($"--lookback-years: must be ≥ 1, got {LookbackYears}");
		if (Audit && Program.RawArgs.Any(a => a.Equals("--lookback-years", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--lookback-years=", StringComparison.OrdinalIgnoreCase)))
			return ValidationResult.Error("--audit and --lookback-years are mutually exclusive: audit derives its window from on-disk CSVs, so a lookback override would silently be ignored.");
		if (Audit && !string.IsNullOrEmpty(ImportWebullSpxFile))
			return ValidationResult.Error("--audit and --import-webull-spx are mutually exclusive.");
		if (Partial && (Audit || !string.IsNullOrEmpty(ImportWebullSpxFile)))
			return ValidationResult.Error("--partial is mutually exclusive with --audit and --import-webull-spx.");
		if (!string.IsNullOrEmpty(ImportWebullSpxFile) && !File.Exists(ImportWebullSpxFile))
			return ValidationResult.Error($"--import-webull-spx: file not found at '{ImportWebullSpxFile}'");
		return ValidationResult.Success();
	}
}

internal sealed class AIHistoryCommand : AsyncCommand<AIHistorySettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private static readonly HashSet<string> VixDrivenTickers = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPY", "SPX", "SPXW", "XSP"
	};

	protected override async Task<int> ExecuteAsync(CommandContext context, AIHistorySettings settings, CancellationToken cancellation)
	{
		TerminalHelper.EnsureTerminalWidthFromConfig();
		var ticker = settings.Ticker.ToUpperInvariant();
		var asOf = DateTime.UtcNow;
		var earliest = asOf.AddYears(-settings.LookbackYears);

		if (settings.Audit)
		{
			AnsiConsole.MarkupLine($"[bold]Auditing intraday cache for {Markup.Escape(ticker)}[/]");
			return RunAudit(ticker, asOf);
		}

		if (!string.IsNullOrEmpty(settings.ImportWebullSpxFile))
		{
			AnsiConsole.MarkupLine($"[bold]Importing Webull SPX bootstrap into {Markup.Escape(ticker)}[/]");
			return await ImportSniffedSpxAsync(ticker, settings.ImportWebullSpxFile, cancellation);
		}

		if (!string.IsNullOrEmpty(settings.ImportFile))
		{
			AnsiConsole.MarkupLine($"[bold]Importing sniffed Webull rows into {Markup.Escape(ticker)}[/]");
			return ImportSniffedIndex(ticker, settings.ImportFile);
		}

		if (settings.Partial)
		{
			// --partial only changes the UNDERLYING tape (capture today's incomplete bars vs. the normal
			// backfill that skips today). Option-contract id capture is identical to a normal run — the
			// full live chain gets registered either way.
			await CaptureContractIdsAsync(ticker, cancellation);
			return await CapturePartialTodayAsync(ticker, asOf, cancellation);
		}

		AnsiConsole.MarkupLine($"[bold]Fetching history for {Markup.Escape(ticker)}[/]");

		var bars = new HistoricalBarCache();

		if (!await FetchAndReportAsync(bars, ticker, earliest, asOf, cancellation))
			return 1;

		// VIX-family rides along with SPX-family fetches. VIX1D / VIX9D / VIX anchor backtest ATM IV at
		// the appropriate term (0–1 DTE, 2–9 DTE, 10+ DTE respectively); VIX9D + VIX also feed the VIX
		// term-structure signal in the opener. CBOE SMILE drives per-day smile steepness scaling in
		// BacktestIVProvider so backtest fills track the actual regime (calm vs stressed days) instead
		// of using a single anchor calibration. The whole family comes from CBOE directly (authoritative
		// for its own indices — see CboeIndexHistoryClient for why Yahoo is not even a fallback); the bar
		// cache routes VIX / VIX1D / VIX9D there by ticker. VIX1D was launched 2023-04-24; pre-launch dates
		// fall back to VIX9D in the IV provider, so historical backtests beyond that point still work.
		if (VixDrivenTickers.Contains(ticker))
		{
			if (!await FetchAndReportAsync(bars, "VIX", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX1D", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX9D", earliest, asOf, cancellation)) return 1;
			if (!await FetchSmileAsync(earliest, asOf, cancellation)) return 1;
		}

		// Nasdaq-100 family: VXN anchors QQQ/NDX ATM IV in BacktestIVProvider (the VIX analog; no
		// shorter CBOE tenors exist). VIX + VIX9D ride along too — the vixTermStructure opener signal
		// is a market-wide regime read shared by every ticker, and a QQQ-only environment would
		// otherwise never populate them. SMILE scales the index-class smile in the parametric path.
		// Intraday VXN rides along as a Yahoo forward capture (trailing week, accumulates day by day —
		// no source anywhere serves deep 1m VXN history, so each daily run banks what exists now).
		if (ticker is "QQQ" or "NDX")
		{
			if (!await FetchAndReportAsync(bars, "VXN", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX9D", earliest, asOf, cancellation)) return 1;
			if (!await FetchSmileAsync(earliest, asOf, cancellation)) return 1;
			await BackfillIntradayAsync("VXN", earliest, asOf, cancellation);
		}

		// Intraday gap-fill from Webull. Best-effort: failures here log a warning but don't fail
		// the whole command, since the daily caches (the main thing backtests need) are already on disk.
		await BackfillIntradayAsync(ticker, earliest, asOf, cancellation);

		// Intraday VIX rides along with SPX-family runs, like the daily VIX-family closes above. The
		// backtest's minute-walk MaxVix gates (LegInShort / CompleteCondor) read data/intraday/VIX/ for
		// the causal per-minute VIX and fall back to the prior-day settled close where the tape is
		// missing. Deep history comes from the one-time `wa ai history VIX --import <sniffed>` bootstrap
		// (Webull's chart endpoint only pages ~3 RTH days back without per-URL signatures); this
		// ride-along keeps the recent edge current.
		if (VixDrivenTickers.Contains(ticker))
			await BackfillIntradayAsync("VIX", earliest, asOf, cancellation);

		// CNN Fear & Greed sentiment cache fill — ticker-agnostic daily series. Without this, the cache
		// only grows when something else calls `FearGreedClient.FetchAsync` AFTER 5pm ET (settlement
		// cutoff), and `wa ai watch` always exits before 5pm. So if the user doesn't run an `analyze`
		// command in the evening, the cache stalls.
		await RefreshSentimentCacheAsync(earliest, asOf, cancellation);

		// Scheduled-catalyst cache fill (earnings + ex-dividend) for this ticker. Previously only the
		// opener fetched events, and only for the ticker being scanned — so held tickers (e.g. SPY) never
		// populated data/event-cache, leaving `report`/`analyze position` theoretical pricing
		// un-dividend-adjusted. Riding along here keeps the cache warm for every ticker the user touches.
		await RefreshEventCalendarAsync(ticker, asOf, cancellation);

		// Full historical dividend schedule (data/dividends/<TICKER>.csv) for the backtest's dividend-aware
		// pricing. Unlike the event-cache above (which holds only the single NEXT ex-dividend), this banks
		// every ACTUAL past payment so backtested option pricing across the window sees the real ex-date and
		// amount that fell inside each leg's life — the same discrete dividend the live Black-Scholes path
		// subtracts. Non-payers / index roots (SPX/SPXW/XSP) simply write nothing → no adjustment.
		await RefreshDividendHistoryAsync(ticker, cancellation);

		// Register every currently-live contract's Webull derivativeId + tradeable-OCC/liquidity snapshot in
		// the derivative registry. Ids are perishable (gone from the chain at expiry); the opener reads this
		// registry (DerivativeIdRegistry.TradeableOccs) for its strike ladder + liquidity gating, and the
		// live chain refresh resolves the ids for Webull queryBatch OI/quote pulls. Run during market hours
		// to catch same-day 0DTE.
		await CaptureContractIdsAsync(ticker, cancellation);

		AnsiConsole.MarkupLine("[dim]Done. Run `wa ai backtest " + Markup.Escape(ticker) + "` to use this data.[/]");
		return 0;
	}

	private static async Task<bool> FetchAndReportAsync(HistoricalBarCache bars, string ticker, DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		// Probe the most recent SETTLED trading day, not literally asOf-1. After a holiday weekend,
		// asOf-1 can be a non-trading day (e.g. Memorial Day) which never has a bar — the old probe
		// then reported "Yahoo unreachable" even though Yahoo was fine and the requested calendar day
		// simply isn't a session. Mirror HistoricalBarCache.ClampToSettled: a day's bar publishes
		// ~17:00 ET, so before then the latest settled session is the prior trading day; walk back
		// over weekends/holidays either way.
		var nowNy = TimeZoneInfo.ConvertTimeFromUtc(asOf.Kind == DateTimeKind.Utc ? asOf : asOf.ToUniversalTime(), NyTz);
		var settled = nowNy.TimeOfDay >= TimeSpan.FromHours(17) ? nowNy.Date : nowNy.Date.AddDays(-1);
		while (!MarketCalendar.IsOpen(settled)) settled = settled.AddDays(-1);

		// A single session can be absent from a vendor's series even when the fetch fully succeeded — e.g.
		// ^VIX1D / ^VIX9D occasionally publish a null print for one day while neighbouring days are fine. Probing
		// only the exact settled session then misreports "Yahoo unreachable". Walk back several trading days:
		// the fetch genuinely failed (unreachable / unknown ticker) only when the whole recent window is empty.
		YahooOptionsClient.HistoricalBar? bar = null;
		for (var probe = settled; bar == null && (settled - probe).TotalDays < 10; probe = probe.AddDays(-1))
		{
			if (MarketCalendar.IsOpen(probe)) bar = await bars.GetBarAsync(ticker, probe, cancellation);
		}
		var source = CboeIndexHistoryClient.IsCboeSeries(ticker) ? "CBOE" : "Yahoo";
		if (bar == null)
		{
			AnsiConsole.MarkupLine($"  {Markup.Escape(ticker)}: [red]failed[/] ({source} unreachable or unknown ticker)");
			return false;
		}
		var hasCoverage = await bars.HasCoverageAsync(ticker, earliest, asOf, cancellation);
		AnsiConsole.MarkupLine(hasCoverage
			? $"  {Markup.Escape(ticker)}: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
			: $"  {Markup.Escape(ticker)}: [yellow]partial[/] ({source} returned a shorter window than requested)");
		return true;
	}

	private static async Task<bool> FetchSmileAsync(DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var smile = new SmileIndexCache();
		var probe = await smile.GetValueAsync(asOf.Date.AddDays(-1), cancellation);
		if (probe == null)
		{
			AnsiConsole.MarkupLine($"  SMILE: [red]failed[/] (CBOE CSV unreachable)");
			return false;
		}
		var hasCoverage = await smile.HasCoverageAsync(earliest, asOf, cancellation);
		AnsiConsole.MarkupLine(hasCoverage
			? $"  SMILE: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
			: $"  SMILE: [yellow]partial[/] (CBOE history shorter than requested)");
		return true;
	}

	/// <summary>Fills <c>data/sentiment-cache/<date>.json</c> for every trading day in
	/// [earliest, asOf-1] whose cache file is missing. Uses <see cref="FearGreedClient.FetchAsync"/>
	/// which handles the "settled" check internally (CNN settles a date's score at 17:00 ET; pre-settlement
	/// fetches don't write to disk). Throttled at 400ms per call to be polite to CNN's public endpoint.
	/// Best-effort — failures don't fail the command, since the sentiment factor is opt-in scoring.</summary>
	private static async Task RefreshSentimentCacheAsync(DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var earliestNy = TimeZoneInfo.ConvertTime(earliest, NyTz).Date;
		var todayNy = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		var cacheDir = Program.ResolvePath("data/sentiment-cache");

		var needed = new List<DateTime>();
		for (var d = earliestNy; d <= todayNy; d = d.AddDays(1))
		{
			if (!MarketCalendar.IsOpen(d)) continue;
			var path = Path.Combine(cacheDir, $"{d:yyyy-MM-dd}.json");
			if (File.Exists(path)) continue;
			needed.Add(d);
		}

		if (needed.Count == 0)
		{
			AnsiConsole.MarkupLine($"  sentiment: [green]ok[/] (cache complete through {todayNy:yyyy-MM-dd})");
			return;
		}

		AnsiConsole.MarkupLine($"  sentiment: pulling {needed.Count} missing day(s) from CNN F&G");
		var written = 0;
		var skipped = 0;
		var index = 0;
		foreach (var d in needed)
		{
			cancellation.ThrowIfCancellationRequested();
			if (index > 0) await Task.Delay(TimeSpan.FromMilliseconds(400), cancellation);
			index++;

			var snapshot = await FearGreedClient.FetchAsync(d, cancellation);
			if (snapshot == null) { skipped++; continue; }
			// FearGreedClient writes the cache file only when the date is "settled" (past 17:00 ET).
			// Today before settlement returns a snapshot but doesn't persist — that's expected; tomorrow's
			// run will pick it up.
			var path = Path.Combine(cacheDir, $"{d:yyyy-MM-dd}.json");
			if (File.Exists(path)) written++; else skipped++;

			if (index % 50 == 0)
				AnsiConsole.MarkupLine($"    progress: {index}/{needed.Count}");
		}

		if (skipped > 0)
			AnsiConsole.MarkupLine($"  sentiment: [green]wrote {written}[/], [yellow]skipped {skipped}[/] (not yet settled or CNN unreachable)");
		else
			AnsiConsole.MarkupLine($"  sentiment: [green]wrote {written}[/] day(s)");
	}

	/// <summary>Refreshes <c>data/event-cache/<TICKER>.json</c> (next earnings + ex-dividend) from
	/// Yahoo. Best-effort — a failure logs a warning and never fails the command, since the daily caches
	/// are the primary deliverable. cacheOnly:false forces a network refresh (subject to the 12h TTL).</summary>
	private static async Task RefreshEventCalendarAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		try
		{
			var calendar = await EventCalendarLoader.LoadAsync(new[] { ticker }, new OpenerEventsConfig(), asOf, cancellation, cacheOnly: false);
			var ev = calendar.Get(ticker);
			if (ev == null)
			{
				AnsiConsole.MarkupLine($"  events: [yellow]none[/] (Yahoo returned no earnings/ex-dividend for {Markup.Escape(ticker)})");
				return;
			}
			var exDiv = ev.NextExDividendDate.HasValue
				? $"ex-div {ev.NextExDividendDate.Value:yyyy-MM-dd}{(ev.DividendAmount.HasValue ? $" (${ev.DividendAmount.Value.ToString("0.##", CultureInfo.InvariantCulture)})" : "")}"
				: "no ex-div";
			var earnings = ev.NextEarningsDate.HasValue ? $"earnings {ev.NextEarningsDate.Value:yyyy-MM-dd}" : "no earnings";
			AnsiConsole.MarkupLine($"  events: [green]ok[/] ({Markup.Escape(earnings)}; {Markup.Escape(exDiv)})");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"  events: [yellow]skipped[/] ({Markup.Escape(ex.Message)})");
		}
	}

	/// <summary>Refreshes <c>data/dividends/<TICKER>.csv</c> (full historical ex-date + amount schedule)
	/// from Yahoo's crumb-free chart endpoint. Best-effort — a failure logs a warning and never fails the
	/// command. A non-payer (or an index root that isn't a Yahoo dividend ticker) yields an empty schedule,
	/// which is the correct "no adjustment" signal for the backtest.</summary>
	private static async Task RefreshDividendHistoryAsync(string ticker, CancellationToken cancellation)
	{
		try
		{
			var cache = new HistoricalDividendCache();
			var divs = await cache.GetAsync(ticker, cancellation);
			if (divs.Count == 0)
			{
				AnsiConsole.MarkupLine($"  dividends: [yellow]none[/] (no payments in Yahoo's history for {Markup.Escape(ticker)} — pricing stays q=0)");
				return;
			}
			var last = divs[^1];
			AnsiConsole.MarkupLine($"  dividends: [green]ok[/] ({divs.Count} payment(s) cached; latest ex {last.ExDate:yyyy-MM-dd} ${last.Amount.ToString("0.####", CultureInfo.InvariantCulture)})");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"  dividends: [yellow]skipped[/] ({Markup.Escape(ex.Message)})");
		}
	}

	/// <summary>Fills <c>data/intraday/<TICKER>/<date>.csv</c> for every missing trading day in
	/// the lookback window. Source-of-truth routing:
	/// <list type="bullet">
	///   <item><b>Non-SPX tickers</b> (SPY, AAPL, QQQ, …) — pulled from massive.com (SIP-consolidated
	///     NMS data via Polygon's mirror endpoint). One range query covers the whole window;
	///     <see cref="MassivePolygonClient"/> handles pagination + the basic-tier 5-req/min rate
	///     window internally. No per-day calls.</item>
	///   <item><b>SPX-family tickers</b> (SPX/SPXW) — SPX RTH bars come from Webull's <c>query-mini</c>
	///     chart endpoint (massive doesn't serve the cash index), paginated backward from "now". SPY
	///     ext-hours bars used to scale into pre/post-market come from massive too — Webull SPY is
	///     subject to the same rate-limit dropouts SPX is, and massive's SIP feed is the higher-
	///     fidelity choice anyway.</item>
	/// </list>
	/// Past PARTIAL files (a truncated live `wa ai watch` session, or an intraday `--partial` capture)
	/// are COMPLETED when the fresh pull strictly supersedes them — covers every bar timestamp the file
	/// already has and adds more. If the re-pull is missing anything the live capture had, the live file
	/// is kept untouched (the original fidelity concern). The old never-overwrite policy left every
	/// `--partial` day incomplete forever, nagging "run --audit" on every subsequent run. Today is never
	/// touched (live Webull owns the current session). The sealed manifest is rewritten after every
	/// successful CSV write so a crash mid-loop can't desync the on-disk CSV from sealed.json; a written
	/// day is only sealed when it actually LooksComplete, so a short pull stays visibly partial.</summary>
	/// <summary>Tickers whose intraday tape comes from Yahoo's trailing-week 1m endpoint — forward
	/// capture only, for CBOE indices with no deep-history source anywhere (see FetchRangeAsync).</summary>
	private static bool IsYahooIntradayCapture(string ticker) => string.Equals(ticker, "VXN", StringComparison.OrdinalIgnoreCase);

	private static async Task BackfillIntradayAsync(string inputTicker, DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null)
		{
			AnsiConsole.MarkupLine("  intraday: [yellow]skipped[/] (api-config.json not found — run `wa sniff` to bootstrap it)");
			return;
		}

		var todayNy = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		var earliestNy = TimeZoneInfo.ConvertTime(earliest, NyTz).Date;
		// Yahoo-forward-capture tapes (VXN): the 1m endpoint serves only the trailing ~7 days, so days
		// older than that are unreachable by construction — clamp the audit window to what the source
		// can serve, or every daily run reports years of forever-missing days.
		var isYahooCapture = IsYahooIntradayCapture(inputTicker);
		if (isYahooCapture && earliestNy < todayNy.AddDays(-8)) earliestNy = todayNy.AddDays(-8);
		var audit = AuditIntraday(inputTicker, earliestNy, todayNy);
		var totalDays = audit.Complete.Count + audit.Partial.Count + audit.Missing.Count;
		// Backfill MISSING days, and COMPLETE partial days (all in the past — the audit window excludes
		// today). A partial file comes from a truncated live `wa ai watch` session or an intraday
		// `--partial` capture; the write loop only replaces it when the fresh pull strictly supersedes
		// the file (see below), so live-source fidelity is never lost to a worse re-pull.
		var partialSet = new HashSet<DateTime>(audit.Partial);
		var needBackfill = audit.Missing.Concat(audit.Partial).OrderBy(d => d).ToList();

		if (needBackfill.Count == 0)
		{
			AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: [green]ok[/] (all {audit.Complete.Count} complete days; nothing to backfill)");
			return;
		}

		var isSpxFamily = WebullIntradayBars.SpxFamilyTickers.Contains(inputTicker);
		var completeNote = audit.Partial.Count > 0 ? $" (completing [yellow]{audit.Partial.Count} partial day(s)[/])" : "";
		var isVix = string.Equals(inputTicker, "VIX", StringComparison.OrdinalIgnoreCase);
		var sourceLabel = isSpxFamily ? "Webull (SPX) + massive (SPY)" : isVix ? "Webull" : isYahooCapture ? "Yahoo (trailing week)" : "massive";
		AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: {audit.Complete.Count}/{totalDays} complete; pulling {needBackfill.Count} day(s) from {sourceLabel}{completeNote}");
		AnsiConsole.MarkupLine($"    needed: {Markup.Escape(FormatDateList(needBackfill, maxInline: 12))}");

		var earliestMissing = needBackfill[0];
		var latestMissing = needBackfill[^1];

		Dictionary<DateTime, List<MinuteBar>> barsByDate;
		try
		{
			barsByDate = await FetchRangeAsync(apiConfig, inputTicker, isSpxFamily, earliestMissing, latestMissing, cancellation);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: [red]failed[/] ({Markup.Escape(ex.Message)})");
			return;
		}

		var sealedDates = audit.SealedDates;
		var written = new List<DateTime>();
		var skipped = new List<(DateTime Date, string Reason)>();
		foreach (var d in needBackfill)
		{
			if (!barsByDate.TryGetValue(d, out var bars) || bars.Count == 0)
			{
				skipped.Add((d, "no bars returned from Webull pagination"));
				continue;
			}
			var path = Path.Combine(audit.IntradayDir, $"{d:yyyy-MM-dd}.csv");
			var toWrite = (IReadOnlyList<MinuteBar>)bars;
			if (partialSet.Contains(d) && File.Exists(path))
			{
				// Merge, don't choose: union the partial live capture with the re-pull so the day becomes a
				// strict superset of both. The re-pull (authoritative SIP) wins on overlapping minutes;
				// live-only bars — thin pre/post-market minutes massive's trade-aggregates never produced, or
				// RTH bars captured during a massive dropout — are preserved. This always supersedes the
				// partial, fixing the old guard that kept a partial forever whenever the live file held any one
				// minute the re-pull lacked, even when the re-pull was more complete through the close.
				var merged = ReadIntradayCsv(path).ToDictionary(b => b.Timestamp.UtcDateTime);
				foreach (var b in bars) merged[b.Timestamp.UtcDateTime] = b;
				toWrite = merged.Values.OrderBy(b => b.Timestamp.UtcDateTime).ToList();
			}
			WriteIntradayCsv(path, toWrite);
			// Seal only what actually reads as a complete session — a short pull stays visibly partial
			// instead of being stamped complete forever.
			if (LooksComplete(path))
			{
				sealedDates.Add(d);
				SaveSealedManifest(audit.SealedPath, sealedDates);
			}
			written.Add(d);
		}

		var skipNote = skipped.Count > 0 ? $", [yellow]skipped {skipped.Count}[/]" : "";
		AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: [green]wrote {written.Count}[/] file(s){skipNote}");
		// Cap the per-day skip detail: a not-yet-bootstrapped deep history (e.g. intraday VIX before its
		// one-time --import) legitimately skips hundreds of days every run — the summary count above
		// carries the signal; ten examples carry the diagnosis.
		var skipShow = skipped.Take(10).ToList();
		foreach (var (d, reason) in skipShow)
			AnsiConsole.MarkupLine($"    [yellow]{d:yyyy-MM-dd}[/]: skipped ({Markup.Escape(reason)})");
		if (skipped.Count > skipShow.Count)
			AnsiConsole.MarkupLine($"    …and {skipped.Count - skipShow.Count} more");
	}

	/// <summary>One-time bootstrap importer for the 2-year historical pull. Reads a text file of SPX
	/// <c>query-mini</c> rows captured from Webull's web app (via the browser console sniffer), parses
	/// them, then pulls matching SPY ext-hours bars per-day from the API (which doesn't need x-s).
	/// Merges SPX + SPY scaled by per-day ratio and writes per-day CSVs in <c>data/intraday/<ticker>/</c>.
	/// Existing CSVs are NOT overwritten (same no-overwrite invariant as <see cref="BackfillIntradayAsync"/>).
	/// Throttle: 1 sec between per-day SPY pulls.</summary>
	private static async Task<int> ImportSniffedSpxAsync(string ticker, string sniffedSpxPath, CancellationToken cancellation)
	{
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null)
		{
			AnsiConsole.MarkupLine("  [red]api-config.json not found[/] — run `wa sniff` to bootstrap it");
			return 1;
		}

		var spxBars = ParseSniffedRows(sniffedSpxPath);
		if (spxBars.Count == 0)
		{
			AnsiConsole.MarkupLine($"  [red]no parseable rows in {Markup.Escape(sniffedSpxPath)}[/]");
			return 1;
		}

		var spxByDate = GroupBarsByNyDate(spxBars);
		var earliestSpxDate = spxByDate.Keys.Min();
		var latestSpxDate = spxByDate.Keys.Max();
		AnsiConsole.MarkupLine($"  loaded {spxBars.Count} SPX bars covering {spxByDate.Count} trading day(s): {earliestSpxDate:yyyy-MM-dd} → {latestSpxDate:yyyy-MM-dd}");

		var intradayDir = Path.Combine(Program.ResolvePath("data/intraday"), ticker);
		Directory.CreateDirectory(intradayDir);
		var sealedPath = Path.Combine(intradayDir, "sealed.json");
		var sealedDates = LoadSealedManifest(sealedPath);

		if (string.IsNullOrWhiteSpace(apiConfig.Massive.ApiKey))
		{
			AnsiConsole.MarkupLine("  [red]MassiveApiKey not set in api-config.json[/] — required for the SPY ext-hours half of the merge.");
			return 1;
		}

		var written = new List<DateTime>();
		var skipped = new List<(DateTime Date, string Reason)>();
		var todayNy = TimeZoneInfo.ConvertTime(DateTime.UtcNow, NyTz).Date;

		// Classify each date: "needs-new" (no CSV), "needs-repair" (CSV exists but lacks pre-market
		// = previous SPY fetch failed), or "complete" (CSV exists with pre-market). Re-running the
		// import after a flaky bulk run automatically repairs incomplete files.
		var needsWork = new List<DateTime>();
		var alreadyComplete = 0;
		foreach (var d in spxByDate.Keys.Where(d => d < todayNy).OrderBy(d => d))
		{
			var csvPath = Path.Combine(intradayDir, $"{d:yyyy-MM-dd}.csv");
			if (File.Exists(csvPath) && CsvHasPreMarketBars(csvPath)) alreadyComplete++;
			else needsWork.Add(d);
		}

		if (needsWork.Count == 0)
		{
			AnsiConsole.MarkupLine($"  [green]all {alreadyComplete} day(s) already complete[/]; nothing to import");
			return 0;
		}

		AnsiConsole.MarkupLine(alreadyComplete > 0
			? $"  {alreadyComplete} day(s) already complete; need SPY for {needsWork.Count} day(s)"
			: $"  pulling SPY ext-hours for {needsWork.Count} session(s)");

		// Single bulk SPY pull from massive.com — one range query (auto-paginated internally) covering
		// the full needed window. Massive returns ~50k bars/page so even 2 years of SPY 1-min fits
		// in ~4 pages = ~1 minute. Far faster and more reliable than per-day Webull calls.
		var spyStart = needsWork.Min();
		var spyEnd = needsWork.Max();
		AnsiConsole.MarkupLine($"    pulling SPY from massive.com ({spyStart:yyyy-MM-dd} → {spyEnd:yyyy-MM-dd})");
		IReadOnlyList<MinuteBar> spyAllBars;
		try
		{
			spyAllBars = await MassivePolygonClient.FetchMinuteAggregatesAsync(
				apiConfig.Massive.ApiKey, "SPY",
				DateOnly.FromDateTime(spyStart), DateOnly.FromDateTime(spyEnd), cancellation);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AnsiConsole.MarkupLine($"  [red]massive SPY pull failed[/]: {Markup.Escape(ex.Message)}");
			return 1;
		}
		AnsiConsole.MarkupLine($"    massive returned {spyAllBars.Count} SPY bars across the range");
		var spyByDate = GroupBarsByNyDate(spyAllBars);

		var index = 0;
		foreach (var d in needsWork)
		{
			cancellation.ThrowIfCancellationRequested();
			var csvPath = Path.Combine(intradayDir, $"{d:yyyy-MM-dd}.csv");
			index++;

			spyByDate.TryGetValue(d, out var spyOnDay);
			var spxOnDay = spxByDate[d];
			var merged = MergeSpxAndSpy(spxOnDay, spyOnDay ?? new List<MinuteBar>());
			if (merged.Count == 0)
			{
				skipped.Add((d, "no usable bars after merge"));
				continue;
			}

			WriteIntradayCsv(csvPath, merged);
			sealedDates.Add(d);
			SaveSealedManifest(sealedPath, sealedDates);
			written.Add(d);

			if (index % 50 == 0)
				AnsiConsole.MarkupLine($"    progress: {index}/{needsWork.Count} (latest: {d:yyyy-MM-dd}, {merged.Count} bars)");
		}

		AnsiConsole.MarkupLine($"  [green]wrote {written.Count}[/] file(s), [yellow]skipped {skipped.Count}[/]");
		var skipShow = skipped.Take(10).ToList();
		foreach (var (d, reason) in skipShow)
			AnsiConsole.MarkupLine($"    [yellow]{d:yyyy-MM-dd}[/]: {Markup.Escape(reason)}");
		if (skipped.Count > skipShow.Count)
			AnsiConsole.MarkupLine($"    …and {skipped.Count - skipShow.Count} more");
		return 0;
	}

	/// <summary>One-time bootstrap importer for a standalone index tape (VIX): writes the sniffed rows
	/// as-is per NY date — no SPY ext-hours merge or ratio scaling (nothing tracks the VIX overnight, and
	/// the backtest reads it RTH-only). Existing complete days are left untouched; partial days are
	/// union-merged with the sniffed rows so a prior live capture is never degraded. Purely local — the
	/// sniffed file is the entire data source, so unlike the SPX import there is no network half.</summary>
	private static int ImportSniffedIndex(string ticker, string sniffedPath)
	{
		var sniffed = ParseSniffedRows(sniffedPath);
		if (sniffed.Count == 0)
		{
			AnsiConsole.MarkupLine($"  [red]no parseable rows in {Markup.Escape(sniffedPath)}[/]");
			return 1;
		}

		var byDate = GroupBarsByNyDate(sniffed);
		AnsiConsole.MarkupLine($"  loaded {sniffed.Count} bars covering {byDate.Count} trading day(s): {byDate.Keys.Min():yyyy-MM-dd} → {byDate.Keys.Max():yyyy-MM-dd}");

		var intradayDir = Path.Combine(Program.ResolvePath("data/intraday"), ticker);
		Directory.CreateDirectory(intradayDir);
		var sealedPath = Path.Combine(intradayDir, "sealed.json");
		var sealedDates = LoadSealedManifest(sealedPath);
		var todayNy = TimeZoneInfo.ConvertTime(DateTime.UtcNow, NyTz).Date;

		var written = 0;
		var alreadyComplete = 0;
		foreach (var d in byDate.Keys.Where(d => d < todayNy).OrderBy(d => d))
		{
			var path = Path.Combine(intradayDir, $"{d:yyyy-MM-dd}.csv");
			if (File.Exists(path) && LooksComplete(path)) { alreadyComplete++; continue; }

			IReadOnlyList<MinuteBar> toWrite = byDate[d];
			if (File.Exists(path))
			{
				// Union-merge with the partial on-disk day; the sniffed rows win on overlapping minutes.
				var merged = ReadIntradayCsv(path).ToDictionary(b => b.Timestamp.UtcDateTime);
				foreach (var b in byDate[d]) merged[b.Timestamp.UtcDateTime] = b;
				toWrite = merged.Values.OrderBy(b => b.Timestamp.UtcDateTime).ToList();
			}
			WriteIntradayCsv(path, toWrite);
			if (LooksComplete(path))
			{
				sealedDates.Add(d);
				SaveSealedManifest(sealedPath, sealedDates);
			}
			written++;
		}

		AnsiConsole.MarkupLine($"  [green]wrote {written}[/] file(s); {alreadyComplete} already complete");
		return 0;
	}

	/// <summary>Parses a text file of <c>query-mini</c> response rows (one per line). Each row's
	/// 8-column format matches what Webull's chart endpoint returns: <c>ts,open,close,high,low,prevClose,volume,vwap</c>.
	/// Reuses <see cref="WebullChartsClient.ParseMiniBarRow"/> so the schema stays in one place.</summary>
	private static List<MinuteBar> ParseSniffedRows(string path)
	{
		var bars = new List<MinuteBar>();
		var seen = new HashSet<long>();
		foreach (var raw in File.ReadAllLines(path))
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith("#")) continue;
			var parsed = WebullChartsClient.ParseMiniBarRow(line);
			if (parsed == null) continue;
			var sec = parsed.Timestamp.ToUnixTimeSeconds();
			if (!seen.Add(sec)) continue;
			bars.Add(parsed);
		}
		bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return bars;
	}

	/// <summary>True if the CSV at <paramref name="path"/> contains at least one bar timestamped
	/// before 09:30 ET (NY) — used by the import to distinguish "fully merged" CSVs (SPX RTH + SPY
	/// ext-hours) from "SPX-only" CSVs left behind by a SPY fetch failure. Re-running the import
	/// repairs the latter without overwriting the former.</summary>
	private static bool CsvHasPreMarketBars(string path)
	{
		if (!File.Exists(path)) return false;
		foreach (var line in File.ReadLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			var firstComma = line.IndexOf(',');
			if (firstComma <= 0) continue;
			if (!DateTimeOffset.TryParse(line.AsSpan(0, firstComma), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
			var et = TimeZoneInfo.ConvertTime(ts, NyTz);
			if (et.Hour * 60 + et.Minute < 9 * 60 + 30) return true;
		}
		return false;
	}

	/// <summary>Single entry point for historical-range fetches used by <see cref="BackfillIntradayAsync"/>.
	/// Routes to massive.com for non-SPX-family tickers (SIP-consolidated NMS, no rate-limit issues at
	/// the basic tier for a single range query) and to a Webull-SPX + massive-SPY combo for SPX-family
	/// (massive doesn't serve the cash index). Returns one bar list per NY date covering bars
	/// timestamped within that date in NY tz; days with no usable bars are absent from the dictionary.</summary>
	private static async Task<Dictionary<DateTime, List<MinuteBar>>> FetchRangeAsync(
		ApiConfig apiConfig,
		string ticker,
		bool isSpxFamily,
		DateTime startNyDate,
		DateTime endNyDate,
		CancellationToken cancellation)
	{
		if (isSpxFamily)
		{
			var spxBars = await FetchSpxFamilyRthFromWebullAsync(apiConfig, ticker, startNyDate, endNyDate, cancellation);
			var spyBars = await FetchFromMassiveAsync(apiConfig, "SPY", startNyDate, endNyDate, cancellation);
			return MergeSpxAndSpyByDate(spxBars, spyBars);
		}
		else if (string.Equals(ticker, "VIX", StringComparison.OrdinalIgnoreCase))
		{
			// VIX is a CBOE cash index: massive's equity-aggregates entitlement excludes indices
			// (I:VIX → NOT_AUTHORIZED), so the tape comes from Webull's chart endpoint alone — same
			// paginator as SPX (the method resolves VIX's chart id from ChartKnownTickerIds), no
			// SPY merge (nothing tracks the VIX overnight, and the backtest reads it RTH-only).
			var bars = await FetchSpxFamilyRthFromWebullAsync(apiConfig, ticker, startNyDate, endNyDate, cancellation);
			return GroupBarsByNyDate(bars);
		}
		else if (IsYahooIntradayCapture(ticker))
		{
			// CBOE indices with NO deep-history source anywhere (VXN: Webull's feed died 2024-04,
			// massive's index namespace is unentitled): Yahoo's chart endpoint serves the trailing
			// ~7 days of 1m — a forward-capture-only tape that accumulates day by day. The caller
			// clamps the audit window to the same horizon.
			AnsiConsole.MarkupLine($"    pulling {Markup.Escape(ticker)} trailing week from Yahoo");
			var bars = await YahooOptionsClient.FetchIntradayMinuteBarsAsync(ticker, cancellation);
			AnsiConsole.MarkupLine($"    Yahoo returned {bars.Count} {Markup.Escape(ticker)} bars");
			return GroupBarsByNyDate(bars);
		}
		else
		{
			var bars = await FetchFromMassiveAsync(apiConfig, ticker, startNyDate, endNyDate, cancellation);
			var byDate = GroupBarsByNyDate(bars);
			return byDate;
		}
	}

	private static async Task<IReadOnlyList<MinuteBar>> FetchFromMassiveAsync(
		ApiConfig apiConfig,
		string ticker,
		DateTime startNyDate,
		DateTime endNyDate,
		CancellationToken cancellation)
	{
		if (string.IsNullOrWhiteSpace(apiConfig.Massive.ApiKey))
		{
			AnsiConsole.MarkupLine($"    [yellow]MassiveApiKey not set[/] — cannot pull {Markup.Escape(ticker)} from massive.com");
			return Array.Empty<MinuteBar>();
		}
		AnsiConsole.MarkupLine($"    pulling {Markup.Escape(ticker)} from massive.com ({startNyDate:yyyy-MM-dd} → {endNyDate:yyyy-MM-dd})");
		var bars = await MassivePolygonClient.FetchMinuteAggregatesAsync(
			apiConfig.Massive.ApiKey, ticker,
			DateOnly.FromDateTime(startNyDate), DateOnly.FromDateTime(endNyDate),
			cancellation);
		AnsiConsole.MarkupLine($"    massive returned {bars.Count} {Markup.Escape(ticker)} bars");
		return bars;
	}

	private static async Task<IReadOnlyList<MinuteBar>> FetchSpxFamilyRthFromWebullAsync(
		ApiConfig apiConfig,
		string ticker,
		DateTime startNyDate,
		DateTime endNyDate,
		CancellationToken cancellation)
	{
		// Webull's query-mini index endpoint truncates deep-history requests to 1 bar without per-URL
		// x-s signatures — pagination backward from "now" only walks reliably ~3 RTH days before the
		// API stops advancing. For deep history users run `--import-webull-spx <file>`; this
		// path is for the small recent gap (yesterday's missing CSV after a late `wa ai watch` shutdown).
		var endEt = new DateTime(endNyDate.Year, endNyDate.Month, endNyDate.Day, 20, 0, 0, DateTimeKind.Unspecified);
		var startEt = new DateTime(startNyDate.Year, startNyDate.Month, startNyDate.Day, 4, 0, 0, DateTimeKind.Unspecified);
		var endUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();
		var startUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();
		// Resolve the RTH chart id from the ticker: SPX/SPXW → 913354362 (S&P 500 cash index),
		// XSP → 925377660 (Mini-SPX index, ~SPX/10). The SPY ext-hours merge scales to this level.
		var rthTickerId = WebullChartsClient.TryResolveKnownChartTickerId(ticker, out var resolvedId) ? resolvedId : 913354362L;
		AnsiConsole.MarkupLine($"    paginating {ticker} RTH from Webull ({startNyDate:yyyy-MM-dd} → {endNyDate:yyyy-MM-dd})");
		var bars = await WebullChartsClient.FetchPaginatedHistoricalMinuteBarsAsync(
			apiConfig, rthTickerId, startUnix, endUnix,
			includeExtended: false, countPerPage: 800,
			delayBetweenPages: TimeSpan.FromSeconds(1),
			onPageProgress: null, cancellation);
		AnsiConsole.MarkupLine($"    Webull returned {bars.Count} {ticker} bars");
		return bars;
	}

	/// <summary>Groups SPX RTH bars and SPY ext-hours bars by NY date, then merges per-date with the
	/// same SPX-wins-on-overlap-plus-SPY-scaled logic used by <see cref="ImportSniffedSpxAsync"/>.
	/// Days where SPX has zero bars on that date are dropped (we'd be emitting pure SPY-scaled
	/// without an anchor for the ratio).</summary>
	private static Dictionary<DateTime, List<MinuteBar>> MergeSpxAndSpyByDate(IReadOnlyList<MinuteBar> spxBars, IReadOnlyList<MinuteBar> spyBars)
	{
		var spxByDate = GroupBarsByNyDate(spxBars);
		var spyByDate = GroupBarsByNyDate(spyBars);
		var result = new Dictionary<DateTime, List<MinuteBar>>();
		var allDates = new SortedSet<DateTime>(spxByDate.Keys);
		foreach (var d in spyByDate.Keys) allDates.Add(d);
		foreach (var d in allDates)
		{
			spxByDate.TryGetValue(d, out var spx);
			spyByDate.TryGetValue(d, out var spy);
			var merged = MergeSpxAndSpy(spx ?? new List<MinuteBar>(), spy ?? new List<MinuteBar>());
			if (merged.Count > 0) result[d] = merged;
		}
		return result;
	}

	private static Dictionary<DateTime, List<MinuteBar>> GroupBarsByNyDate(IReadOnlyList<MinuteBar> bars)
	{
		var by = new Dictionary<DateTime, List<MinuteBar>>();
		foreach (var b in bars)
		{
			var d = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date;
			if (!by.TryGetValue(d, out var list)) by[d] = list = new List<MinuteBar>();
			list.Add(b);
		}
		foreach (var list in by.Values) list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return by;
	}

	/// <summary>Combines SPX RTH bars (authoritative) with SPY ext-hours bars (proxy for SPX
	/// pre/post-market, which the cash index doesn't have). SPX wins on minute overlap; SPY is scaled
	/// by the most-recent SPX/SPY ratio observed in that day's overlap. Mirrors the live-capture path
	/// in <see cref="WebullIntradayBars"/>.</summary>
	private static List<MinuteBar> MergeSpxAndSpy(List<MinuteBar> spx, List<MinuteBar> spy)
	{
		if (spx.Count == 0 && spy.Count == 0) return new List<MinuteBar>();
		if (spy.Count == 0) return new List<MinuteBar>(spx);
		if (spx.Count == 0) return new List<MinuteBar>();

		decimal? ratio = null;
		var spyByTs = new Dictionary<long, decimal>(spy.Count);
		foreach (var b in spy) if (b.Close > 0m) spyByTs[b.Timestamp.ToUnixTimeSeconds()] = b.Close;
		for (var i = spx.Count - 1; i >= 0; i--)
		{
			if (spx[i].Close <= 0m) continue;
			if (spyByTs.TryGetValue(spx[i].Timestamp.ToUnixTimeSeconds(), out var spyClose) && spyClose > 0m)
			{
				ratio = spx[i].Close / spyClose;
				break;
			}
		}
		if (!ratio.HasValue || ratio.Value <= 0m) return new List<MinuteBar>(spx);

		var spxTimestamps = new HashSet<long>(spx.Count);
		foreach (var b in spx) spxTimestamps.Add(b.Timestamp.ToUnixTimeSeconds());

		var ratioValue = ratio.Value;
		var merged = new List<MinuteBar>(spx.Count + spy.Count);
		merged.AddRange(spx);
		foreach (var b in spy)
		{
			if (spxTimestamps.Contains(b.Timestamp.ToUnixTimeSeconds())) continue;
			merged.Add(new MinuteBar(
				b.Timestamp,
				Math.Round(b.Open * ratioValue, 2),
				Math.Round(b.High * ratioValue, 2),
				Math.Round(b.Low * ratioValue, 2),
				Math.Round(b.Close * ratioValue, 2),
				b.Volume));
		}
		merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return merged;
	}

	/// <summary>Validates every on-disk intraday CSV from the earliest filename through yesterday (NY tz).
	/// Independent of <c>--lookback-years</c>: audit's job is "what we have is correct", not "we have N
	/// years of history". For each trading day in that span, classifies the CSV as complete, partial, or
	/// missing. Returns 0 if every day is complete, 2 otherwise — distinct from the regular fetch's 0/1
	/// so scripts can distinguish "data gap" from "fetch error".</summary>
	private static int RunAudit(string ticker, DateTime asOf)
	{
		var intradayDir = Path.Combine(Program.ResolvePath("data/intraday"), ticker);
		var earliestOnDisk = FindEarliestIntradayDate(intradayDir);
		if (earliestOnDisk == null)
		{
			AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(ticker)}: [yellow]no CSVs on disk[/] (nothing to audit — run `wa ai history {Markup.Escape(ticker)}` first)");
			return 2;
		}

		var todayNy = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		var audit = AuditIntraday(ticker, earliestOnDisk.Value, todayNy);
		var totalDays = audit.Complete.Count + audit.Partial.Count + audit.Missing.Count;
		var latestNy = todayNy.AddDays(-1);
		AnsiConsole.MarkupLine($"  window: {earliestOnDisk.Value:yyyy-MM-dd} → {latestNy:yyyy-MM-dd} ({totalDays} trading days)");
		AnsiConsole.MarkupLine($"  sealed.json: {audit.SealedDates.Count} entries");
		AnsiConsole.MarkupLine($"  [green]complete: {audit.Complete.Count}[/], [yellow]partial: {audit.Partial.Count}[/], [red]missing: {audit.Missing.Count}[/]");

		if (audit.Partial.Count > 0)
			AnsiConsole.MarkupLine($"  [yellow]partial dates[/]: {Markup.Escape(FormatDateList(audit.Partial, maxInline: 30))}");
		if (audit.Missing.Count > 0)
			AnsiConsole.MarkupLine($"  [red]missing dates[/]: {Markup.Escape(FormatDateList(audit.Missing, maxInline: 30))}");

		var staleSealed = audit.Missing.Where(audit.SealedDates.Contains).ToList();
		if (staleSealed.Count > 0)
			AnsiConsole.MarkupLine($"  [yellow]stale sealed entries[/] (sealed.json claims complete but CSV missing): {Markup.Escape(FormatDateList(staleSealed, maxInline: 30))}");

		return audit.Partial.Count + audit.Missing.Count == 0 ? 0 : 2;
	}

	/// <summary>Earliest <c>YYYY-MM-DD.csv</c> filename in the ticker's intraday dir, or null if the dir
	/// is empty or missing. Used by <c>--audit</c> to anchor the validation window to actual on-disk
	/// data instead of the lookback config.</summary>
	private static DateTime? FindEarliestIntradayDate(string intradayDir)
	{
		if (!Directory.Exists(intradayDir)) return null;
		DateTime? earliest = null;
		foreach (var path in Directory.EnumerateFiles(intradayDir, "*.csv"))
		{
			var name = Path.GetFileNameWithoutExtension(path);
			if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (earliest == null || d < earliest.Value) earliest = d;
		}
		return earliest;
	}

	/// <summary>Walks every trading day in [earliestNy, todayNy) (today exclusive — live capture owns
	/// the current session) and classifies the on-disk CSV as complete, partial, or missing. As a side
	/// effect, any LooksComplete=true CSV not yet in sealed.json is added and the manifest rewritten —
	/// so sealed.json grows into an authoritative ledger of all known-good dates rather than only the
	/// subset this command happens to write via massive.com. This auto-heals gaps left by older builds
	/// (and by the live capture, which writes CSVs but never touches sealed.json) without needing a
	/// separate reconcile command.</summary>
	private static IntradayAuditResult AuditIntraday(string inputTicker, DateTime earliestNy, DateTime todayNy)
	{
		var intradayDir = Path.Combine(Program.ResolvePath("data/intraday"), inputTicker);
		var sealedPath = Path.Combine(intradayDir, "sealed.json");
		var sealedDates = LoadSealedManifest(sealedPath);

		var complete = new List<DateTime>();
		var partial = new List<DateTime>();
		var missing = new List<DateTime>();
		var sealedDirty = false;
		for (var d = earliestNy; d < todayNy; d = d.AddDays(1))
		{
			if (!MarketCalendar.IsOpen(d)) continue;
			var path = Path.Combine(intradayDir, $"{d:yyyy-MM-dd}.csv");
			if (!File.Exists(path)) { missing.Add(d); continue; }
			if (sealedDates.Contains(d)) { complete.Add(d); continue; }
			if (LooksComplete(path))
			{
				complete.Add(d);
				sealedDates.Add(d);
				sealedDirty = true;
				continue;
			}
			partial.Add(d);
		}

		if (sealedDirty)
			SaveSealedManifest(sealedPath, sealedDates);

		return new IntradayAuditResult(intradayDir, sealedPath, sealedDates, complete, partial, missing);
	}

	private static string FormatDateList(IReadOnlyList<DateTime> dates, int maxInline)
	{
		if (dates.Count == 0) return "(none)";
		if (dates.Count <= maxInline) return string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")));
		var half = maxInline / 2;
		var head = string.Join(", ", dates.Take(half).Select(d => d.ToString("yyyy-MM-dd")));
		var tail = string.Join(", ", dates.Skip(dates.Count - half).Select(d => d.ToString("yyyy-MM-dd")));
		return $"{head}, … ({dates.Count - maxInline} more) …, {tail}";
	}

	private sealed record IntradayAuditResult(
		string IntradayDir,
		string SealedPath,
		HashSet<DateTime> SealedDates,
		List<DateTime> Complete,
		List<DateTime> Partial,
		List<DateTime> Missing);

	/// <summary>Reads the per-ticker sealed manifest at <c>data/intraday/<TICKER>/sealed.json</c>.
	/// Records every date this command has successfully pulled from massive.com so subsequent runs
	/// trust those files unconditionally — needed for early-close days, which would otherwise fail the
	/// 15:59-ET completeness check on every invocation and get redundantly reseeded.</summary>
	private static HashSet<DateTime> LoadSealedManifest(string path)
	{
		var sealedDates = new HashSet<DateTime>();
		if (!File.Exists(path)) return sealedDates;
		try
		{
			var doc = JsonSerializer.Deserialize<SealedManifest>(File.ReadAllText(path));
			if (doc?.Sealed == null) return sealedDates;
			foreach (var s in doc.Sealed)
			{
				if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
					sealedDates.Add(d);
			}
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"  intraday: [yellow]warning — failed to read .sealed.json[/] ({Markup.Escape(ex.Message)}); treating all dates as unsealed");
		}
		return sealedDates;
	}

	private static void SaveSealedManifest(string path, HashSet<DateTime> sealedDates)
	{
		var sorted = sealedDates.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToList();
		var doc = new SealedManifest { Sealed = sorted };
		var json = JsonSerializer.Serialize(doc, JsonDefaults.Indented);
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, json);
		File.Move(tmp, path, overwrite: true);
	}

	private sealed class SealedManifest
	{
		[System.Text.Json.Serialization.JsonPropertyName("sealed")]
		public List<string> Sealed { get; set; } = new();
	}

	/// <summary>Captures today's incomplete intraday tape (full pre-market from 04:00 ET → the minute
	/// this runs) and writes <c>data/intraday/<ticker>/<today>.csv</c>. Uses the range-based
	/// <see cref="WebullIntradayBars.FetchHistoricalRangeAsync"/> — the same path the daily backfill
	/// uses — so it produces identical format, start-of-bar convention, and complete pre-market
	/// coverage (SPX RTH + SPY-scaled pre/post for SPX-family). The session isn't done, so we don't
	/// touch sealed.json; tomorrow's `wa ai history` re-pulls the now-complete day and seals it.</summary>
	private static async Task<int> CapturePartialTodayAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null)
		{
			AnsiConsole.MarkupLine("  [red]api-config.json not found[/] — run `wa sniff` to bootstrap it");
			return 1;
		}
		if (apiConfig.Webull.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("  [red]api-config.json has no headers[/] — run `wa sniff` to refresh");
			return 1;
		}

		var todayNy = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		AnsiConsole.MarkupLine($"[bold]Capturing partial intraday for {Markup.Escape(ticker)}[/] {todayNy:yyyy-MM-dd} (full pre-market 04:00 ET → now, not sealed)");

		// Use the range-based fetch (same path the backfill uses), not the count-capped live fetcher.
		// FetchHistoricalRangeAsync paginates [04:00 ET, 20:00 ET] so the pre-market is complete from
		// the 04:00 open regardless of when this runs. The count-based live fetcher caps at ~800 bars
		// from "now", which after a late run slides the window forward and drops early pre-market —
		// exactly the 06:11-start artifact this replaces. Bars beyond the current minute simply don't
		// exist yet, so Webull returns up to now.
		Dictionary<DateTime, List<MinuteBar>> byDate;
		try
		{
			byDate = await WebullIntradayBars.FetchHistoricalRangeAsync(
				apiConfig, ticker, todayNy, todayNy,
				delayBetweenPages: TimeSpan.FromMilliseconds(500),
				log: msg => AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(msg)}[/]"),
				cancellation);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AnsiConsole.MarkupLine($"  [red]capture failed[/]: {Markup.Escape(ex.Message)}");
			return 1;
		}

		if (!byDate.TryGetValue(todayNy, out var bars) || bars.Count == 0)
		{
			AnsiConsole.MarkupLine("  [yellow]no bars returned[/] — session may not have opened yet, or the Webull session expired (run `wa sniff`)");
			return 1;
		}

		// Clamp to the 04:00 ET pre-market open. Webull's query-mini returns overnight-session bars
		// (overnight=1) starting at 00:00 ET, but the canonical backfill files use massive's SPY which
		// has no overnight — so keeping overnight here would make this day's pre-market window wider
		// than every other day's and skew the tape signal's pre-market-open anchor. Drop < 04:00 ET so
		// the file matches the rest of the dataset.
		var premarketOpen = new TimeSpan(4, 0, 0);
		var clamped = bars.Where(b => TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).TimeOfDay >= premarketOpen).ToList();
		if (clamped.Count == 0)
		{
			AnsiConsole.MarkupLine("  [yellow]no bars at or after 04:00 ET[/] — nothing to write");
			return 1;
		}

		var path = Path.Combine(Program.ResolvePath("data/intraday"), ticker.ToUpperInvariant(), $"{todayNy:yyyy-MM-dd}.csv");
		WriteIntradayCsv(path, clamped);
		AnsiConsole.MarkupLine($"  [green]captured {clamped.Count} bar(s)[/] (04:00 ET → now) → data/intraday/{Markup.Escape(ticker)}/{todayNy:yyyy-MM-dd}.csv");
		AnsiConsole.MarkupLine($"  [dim]not added to sealed.json (session incomplete); run `wa ai history {Markup.Escape(ticker)}` tomorrow to finalize + seal[/]");
		return 0;
	}

	/// <summary>Fetches the full live option chain for <paramref name="ticker"/> solely to register every
	/// contract's Webull derivativeId. The chain only lists non-expired contracts, and once a contract
	/// expires its id — the sole key to its minute/IV history via the chart endpoint — is no longer
	/// discoverable. The DATA can be pulled any time afterward (`wa options backfill`), but only if the id
	/// was banked while the contract was live, which is why this runs on every history pass and must run
	/// during market hours to catch same-day 0DTE. Best-effort: a missing api-config or a failed fetch
	/// warns and returns without failing the command — the underlying caches are the primary deliverable.</summary>
	private static async Task CaptureContractIdsAsync(string ticker, CancellationToken cancellation)
	{
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null || apiConfig.Webull.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("  contract ids: [yellow]skipped[/] (api-config.json missing/empty — run `wa sniff`)");
			return;
		}
		try
		{
			var before = DerivativeIdRegistry.Snapshot().Count;
			var (_, _, ids) = await WebullOptionsClient.FetchChainAsync(apiConfig, ticker, cancellation);
			var added = DerivativeIdRegistry.Snapshot().Count - before;
			AnsiConsole.MarkupLine($"  contract ids: [green]{ids.Count}[/] live contract(s) on the chain ([green]+{added}[/] new in registry)");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"  contract ids: [yellow]skipped[/] ({Markup.Escape(ex.Message)})");
		}
	}

	private static ApiConfig? TryLoadApiConfig()
	{
		try
		{
			var path = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(path)) return null;
			return JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(path));
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"  intraday: [yellow]failed to load api-config.json[/] ({Markup.Escape(ex.Message)})");
			return null;
		}
	}

	/// <summary>"Complete" = file exists, has an opening bar within the first 5 minutes of RTH
	/// (09:30–09:34 ET), and ends at one of the two expected NYSE session closes:
	/// <list type="bullet">
	///   <item>Regular full session: ≥380 RTH bars with the 15:59 ET bar present (closes 16:00).</item>
	///   <item>Early-close session: ≥200 RTH bars with the last bar between 12:59 and 13:00 ET
	///     (closes 13:00 — July 3, day after Thanksgiving, Christmas Eve).</item>
	/// </list>
	/// Without the early-close branch, half-days never pass this check, never auto-seal in the audit,
	/// and stay flagged as "partial" forever — see the SPY audit on 2026-05-23 which reported
	/// 2025-07-03, 2025-11-28, 2025-12-24 as partial when they were actually complete given the close.
	///
	/// <para>The "first 5 minutes of RTH" tolerance (instead of strict 09:30) generalizes the check
	/// to feeds where the first print isn't at exactly the bell — per memory
	/// <c>webull-first-bar-of-day-is-0931</c>, Webull's option chart endpoint stamps the first bar
	/// at 09:31 ET. Underlying intraday CSVs currently always have a 09:30 print (cash index ticks
	/// continuously), so this doesn't change current behavior; it just makes the audit safe to point
	/// at option CSVs later without false-negatives.</para></summary>
	private static bool LooksComplete(string path)
	{
		if (!File.Exists(path)) return false;
		var bars = ReadIntradayCsv(path);

		const int regularCloseMinute = 15 * 60 + 59;   // 15:59 ET — last full minute before 16:00 close
		const int earlyCloseMinute = 12 * 60 + 59;     // 12:59 ET — last full minute before 13:00 close
		const int openWindowEndMinute = 9 * 60 + 34;   // 09:34 ET — last minute that still counts as "open"
		int rthCount = 0;
		bool hasOpening = false;
		int lastRthMinute = -1;
		foreach (var b in bars)
		{
			var et = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz);
			var minuteOfDay = et.Hour * 60 + et.Minute;
			if (minuteOfDay < 9 * 60 + 30 || minuteOfDay >= 16 * 60) continue;
			rthCount++;
			if (minuteOfDay <= openWindowEndMinute) hasOpening = true;
			if (minuteOfDay > lastRthMinute) lastRthMinute = minuteOfDay;
		}

		if (!hasOpening) return false;

		// Regular session: last bar at 15:59 ET (some files may have a 16:00 print stamped as the close).
		if (lastRthMinute >= regularCloseMinute && rthCount >= 380) return true;

		// Early-close session: last bar at 12:59 (closing minute of a half-day). Accept exactly 12:59 or
		// 13:00 (some feeds emit a 13:00 bar as the close); reject 13:01+ because that range overlaps with
		// truncated regular days (e.g., a normal day where `wa ai watch` was killed early afternoon).
		if (lastRthMinute >= earlyCloseMinute && lastRthMinute <= 13 * 60 && rthCount >= 200) return true;

		return false;
	}

	private static List<MinuteBar> ReadIntradayCsv(string path)
	{
		var bars = new List<MinuteBar>();
		foreach (var line in File.ReadAllLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			var parts = line.Split(',');
			if (parts.Length < 6) continue;
			if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) continue;
			if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) continue;
			long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
			bars.Add(new MinuteBar(ts, o, h, l, c, v));
		}
		return bars;
	}

	private static void WriteIntradayCsv(string path, IReadOnlyList<MinuteBar> bars)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var sb = new StringBuilder("timestamp_utc,open,high,low,close,volume\n");
		foreach (var b in bars)
		{
			sb.Append(b.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Open.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.High.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Low.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Close.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Volume.ToString(CultureInfo.InvariantCulture)).Append('\n');
		}
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, sb.ToString());
		File.Move(tmp, path, overwrite: true);
	}
}
