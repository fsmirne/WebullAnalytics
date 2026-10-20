using Spectre.Console;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI.Replay;

/// <summary>Maintains the on-disk intraday tape <c>data/intraday/<TICKER>/<date>.csv</c>: audits which past
/// sessions are complete, re-pulls missing/partial past days from the deep-history sources (Webull query-mini
/// paginator for SPX-family/VIX, massive for equities, Yahoo for VXN), captures today's partial session, and
/// keeps <c>sealed.json</c> in step. Shared by `wa ai history` (the nightly backfill) and `wa ai gate` (which
/// heals the sessions it is about to replay).</summary>
internal static class IntradayTapeBackfill
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	/// <summary>Tickers whose intraday tape comes from Yahoo's trailing-week 1m endpoint — forward
	/// capture only, for CBOE indices with no deep-history source anywhere (see FetchRangeAsync).</summary>
	private static bool IsYahooIntradayCapture(string ticker) => string.Equals(ticker, "VXN", StringComparison.OrdinalIgnoreCase);

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
	/// day is only sealed when it actually LooksComplete, so a short pull stays visibly partial.
	/// <para><paramref name="latestNy"/> (inclusive, NY date) narrows the window's far end — `wa ai gate` heals
	/// only the sessions it replays. Null = through yesterday.</para></summary>
	internal static async Task BackfillAsync(string inputTicker, DateTime earliest, DateTime asOf, CancellationToken cancellation, string? vendor = null, DateTime? latestNy = null)
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
		var endNyExclusive = latestNy.HasValue && latestNy.Value.Date.AddDays(1) < todayNy ? latestNy.Value.Date.AddDays(1) : todayNy;
		var audit = AuditIntraday(inputTicker, earliestNy, endNyExclusive);
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
			barsByDate = await FetchRangeAsync(apiConfig, inputTicker, isSpxFamily, earliestMissing, latestMissing, cancellation, vendor);
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

	/// <summary>Single entry point for historical-range fetches used by <see cref="BackfillAsync"/>.
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
		CancellationToken cancellation,
		string? vendor = null)
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
			// --vendor webull: skip the massive attempt and pull straight from Webull's paginator (for old
			// data past massive's ~2y horizon — no wasted 403). Default tries massive first, then falls back.
			if (string.Equals(vendor, "webull", StringComparison.OrdinalIgnoreCase))
			{
				var deep = await FetchEquityDeepFromWebullAsync(apiConfig, ticker, startNyDate, endNyDate, cancellation);
				return GroupBarsByNyDate(deep);
			}
			var bars = await FetchFromMassiveAsync(apiConfig, ticker, startNyDate, endNyDate, cancellation);
			var byDate = GroupBarsByNyDate(bars);
			// Default (auto): massive's SIP aggregates only reach ~2 years; fill any day it didn't return from
			// Webull's query-mini paginator (the same one VIX/SPX use — walks back years with the captured
			// session's signed requests). massive wins on days it served (higher-fidelity SIP); Webull fills the
			// older tail. includeExtended keeps SPY's pre/post-market. --vendor massive suppresses the fallback.
			if (!string.Equals(vendor, "massive", StringComparison.OrdinalIgnoreCase))
			{
				var gaps = new List<DateTime>();
				for (var d = startNyDate.Date; d <= endNyDate.Date; d = d.AddDays(1))
					if (MarketCalendar.IsOpen(d) && !byDate.ContainsKey(d)) gaps.Add(d);
				if (gaps.Count > 0)
				{
					var deep = await FetchEquityDeepFromWebullAsync(apiConfig, ticker, gaps[0], gaps[^1], cancellation);
					foreach (var (d, b) in GroupBarsByNyDate(deep))
						if (!byDate.ContainsKey(d)) byDate[d] = b;
				}
			}
			return byDate;
		}
	}

	/// <summary>Deep intraday history for an equity/ETF (e.g. SPY) from Webull's query-mini chart paginator —
	/// the same one the SPX/VIX path uses, which walks back years using the captured session's signed
	/// requests (massive's SIP aggregates only reach ~2y). Resolves the chart tickerId from the known-chart
	/// map, else dynamically via getQuote. includeExtended so SPY's pre/post-market minutes come through
	/// (the tape anchors on the pre-market open). Empty when the id can't be resolved.</summary>
	private static async Task<IReadOnlyList<MinuteBar>> FetchEquityDeepFromWebullAsync(
		ApiConfig apiConfig, string ticker, DateTime startNyDate, DateTime endNyDate, CancellationToken cancellation)
	{
		if (!WebullChartsClient.TryResolveKnownChartTickerId(ticker, out var tickerId))
		{
			var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { ticker }, cancellation);
			if (!resolved.TryGetValue(ticker, out tickerId) || tickerId == 0)
			{
				AnsiConsole.MarkupLine($"    [yellow]could not resolve Webull chart id for {Markup.Escape(ticker)}; skipping deep Webull fill[/]");
				return Array.Empty<MinuteBar>();
			}
		}
		var startEt = new DateTime(startNyDate.Year, startNyDate.Month, startNyDate.Day, 4, 0, 0, DateTimeKind.Unspecified);
		var endEt = new DateTime(endNyDate.Year, endNyDate.Month, endNyDate.Day, 20, 0, 0, DateTimeKind.Unspecified);
		var startUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();
		var endUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endEt, NyTz), TimeSpan.Zero).ToUnixTimeSeconds();
		AnsiConsole.MarkupLine($"    paginating {Markup.Escape(ticker)} from Webull ({startNyDate:yyyy-MM-dd} → {endNyDate:yyyy-MM-dd})");
		var bars = await WebullChartsClient.FetchPaginatedHistoricalMinuteBarsAsync(
			apiConfig, tickerId, startUnix, endUnix,
			includeExtended: true, countPerPage: 800,
			delayBetweenPages: TimeSpan.FromSeconds(1), onPageProgress: null, cancellation);
		AnsiConsole.MarkupLine($"    Webull returned {bars.Count} {Markup.Escape(ticker)} bars");
		return bars;
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
	/// same <see cref="SpxFamilyTape.Merge"/> splice every SPX-family writer uses.
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
			var merged = SpxFamilyTape.Merge(spx ?? new List<MinuteBar>(), spy ?? new List<MinuteBar>());
			if (merged.Count > 0) result[d] = merged;
		}
		return result;
	}

	internal static Dictionary<DateTime, List<MinuteBar>> GroupBarsByNyDate(IReadOnlyList<MinuteBar> bars)
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

	/// <summary>Walks every trading day in [earliestNy, endNyExclusive) (never today — live capture owns
	/// the current session) and classifies the on-disk CSV as complete, partial, or missing. As a side
	/// effect, any LooksComplete=true CSV not yet in sealed.json is added and the manifest rewritten —
	/// so sealed.json grows into an authoritative ledger of all known-good dates rather than only the
	/// subset this command happens to write via massive.com. This auto-heals gaps left by older builds
	/// (and by the live capture, which writes CSVs but never touches sealed.json) without needing a
	/// separate reconcile command.</summary>
	internal static IntradayAuditResult AuditIntraday(string inputTicker, DateTime earliestNy, DateTime endNyExclusive)
	{
		var intradayDir = Path.Combine(Program.ResolvePath("data/intraday"), inputTicker);
		var sealedPath = Path.Combine(intradayDir, "sealed.json");
		var sealedDates = LoadSealedManifest(sealedPath);

		var complete = new List<DateTime>();
		var partial = new List<DateTime>();
		var missing = new List<DateTime>();
		var sealedDirty = false;
		for (var d = earliestNy; d < endNyExclusive; d = d.AddDays(1))
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

	internal static string FormatDateList(IReadOnlyList<DateTime> dates, int maxInline)
	{
		if (dates.Count == 0) return "(none)";
		if (dates.Count <= maxInline) return string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")));
		var half = maxInline / 2;
		var head = string.Join(", ", dates.Take(half).Select(d => d.ToString("yyyy-MM-dd")));
		var tail = string.Join(", ", dates.Skip(dates.Count - half).Select(d => d.ToString("yyyy-MM-dd")));
		return $"{head}, … ({dates.Count - maxInline} more) …, {tail}";
	}

	internal sealed record IntradayAuditResult(
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
	internal static HashSet<DateTime> LoadSealedManifest(string path)
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

	internal static void SaveSealedManifest(string path, HashSet<DateTime> sealedDates)
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
	internal static async Task<int> CaptureTodayAsync(string ticker, DateTime asOf, CancellationToken cancellation)
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

		// Merge into what's on disk (the live capture's bars), the fresh pull winning on overlap: a truncated
		// page must never shrink today's file, now that `wa ai gate` triggers this capture unattended.
		var path = Path.Combine(Program.ResolvePath("data/intraday"), ticker.ToUpperInvariant(), $"{todayNy:yyyy-MM-dd}.csv");
		var existing = File.Exists(path) ? ReadIntradayCsv(path) : new List<MinuteBar>();
		WriteIntradayCsv(path, IntradayBarCache.Merge(existing, clamped));
		AnsiConsole.MarkupLine($"  [green]captured {clamped.Count} bar(s)[/] (04:00 ET → now) → data/intraday/{Markup.Escape(ticker)}/{todayNy:yyyy-MM-dd}.csv");
		AnsiConsole.MarkupLine($"  [dim]not added to sealed.json (session incomplete); run `wa ai history {Markup.Escape(ticker)}` tomorrow to finalize + seal[/]");
		return 0;
	}

	internal static ApiConfig? TryLoadApiConfig()
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
	internal static bool LooksComplete(string path)
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

		// The session's real close depends on the date: NYSE/CBOE half-days (Fri after Thanksgiving, Jul 3,
		// Christmas Eve) close at 13:00. Consult the calendar so we don't (a) demand a 16:00 tail on a half-day
		// or (b) accept a truncated regular day that happens to end near 1pm. On a known early-close day, index
		// feeds (VIX) legitimately print ~15 min past the 13:00 equity close, so we take last≥12:59 with no
		// upper bound; on a regular day we require the 15:59 tail.
		var earlyClose = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
			CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && MarketCalendar.IsEarlyClose(day);

		if (earlyClose)
			return lastRthMinute >= earlyCloseMinute && rthCount >= 200;
		return lastRthMinute >= regularCloseMinute && rthCount >= 380;
	}

	/// <summary>True when today's on-disk RTH tape for <paramref name="ticker"/> ends before the last completed
	/// minute — the session is under way (or over) and nothing has been writing the file. See <see cref="IsBehind"/>.</summary>
	internal static bool IsTodayBehind(string ticker, DateTime asOfUtc)
	{
		var today = TimeZoneInfo.ConvertTime(asOfUtc, NyTz).Date;
		var path = Path.Combine(Program.ResolvePath("data/intraday"), ticker.ToUpperInvariant(), $"{today:yyyy-MM-dd}.csv");
		return IsBehind(File.Exists(path) ? ReadIntradayCsv(path) : new List<MinuteBar>(), asOfUtc);
	}

	/// <summary>Whether <paramref name="bars"/> (one session's tape, any order) lag the last completed RTH minute of
	/// <paramref name="asOfUtc"/>'s session. Bars are start-of-bar stamped, so at wall-clock T the newest published bar
	/// is T−1min; one more minute of grace covers the feed's publish lag. False before the first RTH bar can exist,
	/// and on non-trading days. Internal for tests.</summary>
	internal static bool IsBehind(IReadOnlyList<MinuteBar> bars, DateTime asOfUtc)
	{
		var nowEt = TimeZoneInfo.ConvertTime(asOfUtc, NyTz);
		if (!MarketCalendar.IsOpen(nowEt.Date)) return false;
		var openMinute = 9 * 60 + 30;
		var closeMinute = MarketCalendar.IsEarlyClose(nowEt.Date) ? 13 * 60 : 16 * 60;
		var lastCompleted = Math.Min(nowEt.Hour * 60 + nowEt.Minute - 1, closeMinute - 1);
		if (lastCompleted < openMinute) return false;

		var lastRth = -1;
		foreach (var b in bars)
		{
			var et = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz);
			var m = et.Hour * 60 + et.Minute;
			if (m >= openMinute && m < closeMinute && m > lastRth) lastRth = m;
		}
		return lastRth < lastCompleted - 1;
	}

	internal static List<MinuteBar> ReadIntradayCsv(string path)
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

	internal static void WriteIntradayCsv(string path, IReadOnlyList<MinuteBar> bars)
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
