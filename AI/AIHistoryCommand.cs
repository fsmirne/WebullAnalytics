using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

/// <summary>`wa ai history &lt;ticker&gt;` — populate the historical OHLC caches used by `wa ai backtest`.
/// Separation of concerns: the fetch step hits the network here (where transient Yahoo failures are
/// loud and re-runnable), while the backtest runs purely offline against the cache. Mirrors the
/// `wa fetch` / `wa report` split. SPX-family tickers (SPY, SPX, SPXW, XSP) also pull VIX, VIX1D, and
/// VIX9D, since the backtest reads ATM IV from the appropriate term (VIX1D for 0–1 DTE, VIX9D for
/// 2–9 DTE, VIX for longer) and the opener reads the VIX term-structure regime score.
///
/// Intraday backfill: if api-config.json has a populated <c>massiveApiKey</c>, also fills
/// <c>data/intraday/&lt;TICKER&gt;/&lt;date&gt;.csv</c> for every trading day in the lookback window
/// whose file is missing or partial. Closes the gap when the live bot was offline (holidays, outages,
/// late starts). Today's date is never touched — the live Webull capture owns it.</summary>
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

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (LookbackYears < 1) return ValidationResult.Error($"--lookback-years: must be ≥ 1, got {LookbackYears}");
		if (Audit && Program.RawArgs.Any(a => a.Equals("--lookback-years", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--lookback-years=", StringComparison.OrdinalIgnoreCase)))
			return ValidationResult.Error("--audit and --lookback-years are mutually exclusive: audit derives its window from on-disk CSVs, so a lookback override would silently be ignored.");
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

	// Tickers whose intraday is synthesized from SPY × (today_SPX_open / today_SPY_open_at_0930). SPX cash
	// index isn't on massive.com's basic tier, so the historical SPXW/SPX/XSP intraday must be derived
	// from SPY exactly the way scripts/backfill_intraday_polygon.py does it.
	private static readonly HashSet<string> SpxSynthesizedTickers = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPX", "SPXW", "XSP"
	};

	public override async Task<int> ExecuteAsync(CommandContext context, AIHistorySettings settings, CancellationToken cancellation)
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

		AnsiConsole.MarkupLine($"[bold]Fetching history for {Markup.Escape(ticker)}[/]");

		var bars = new HistoricalBarCache();

		if (!await FetchAndReportAsync(bars, ticker, earliest, asOf, cancellation))
			return 1;

		// VIX-family rides along with SPX-family fetches. VIX1D / VIX9D / VIX anchor backtest ATM IV at
		// the appropriate term (0–1 DTE, 2–9 DTE, 10+ DTE respectively); VIX9D + VIX also feed the VIX
		// term-structure signal in the opener. CBOE SMILE drives per-day smile steepness scaling in
		// BacktestIVProvider so backtest fills track the actual regime (calm vs stressed days) instead
		// of using a single anchor calibration. SMILE comes from CBOE directly (its own daily CSV);
		// VIX / VIX1D / VIX9D come from Yahoo as OHLC. VIX1D was launched 2023-04-24; pre-launch dates
		// fall back to VIX9D in the IV provider, so historical backtests beyond that point still work.
		if (VixDrivenTickers.Contains(ticker))
		{
			if (!await FetchAndReportAsync(bars, "VIX", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX1D", earliest, asOf, cancellation)) return 1;
			if (!await FetchAndReportAsync(bars, "VIX9D", earliest, asOf, cancellation)) return 1;
			if (!await FetchSmileAsync(earliest, asOf, cancellation)) return 1;
		}

		// Intraday gap-fill from massive.com. Best-effort: failures here log a warning but don't fail
		// the whole command, since the daily caches (the main thing backtests need) are already on disk.
		await BackfillIntradayAsync(ticker, bars, earliest, asOf, cancellation);

		AnsiConsole.MarkupLine("[dim]Done. Run `wa ai backtest " + Markup.Escape(ticker) + "` to use this data.[/]");
		return 0;
	}

	private static async Task<bool> FetchAndReportAsync(HistoricalBarCache bars, string ticker, DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var bar = await bars.GetBarAsync(ticker, asOf.Date.AddDays(-1), cancellation);
		if (bar == null)
		{
			AnsiConsole.MarkupLine($"  {Markup.Escape(ticker)}: [red]failed[/] (Yahoo unreachable or unknown ticker)");
			return false;
		}
		var hasCoverage = await bars.HasCoverageAsync(ticker, earliest, asOf, cancellation);
		AnsiConsole.MarkupLine(hasCoverage
			? $"  {Markup.Escape(ticker)}: [green]ok[/] (covers {earliest:yyyy-MM-dd} → {asOf:yyyy-MM-dd})"
			: $"  {Markup.Escape(ticker)}: [yellow]partial[/] (Yahoo returned a shorter window than requested)");
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

	/// <summary>Fills <c>data/intraday/&lt;TICKER&gt;/&lt;date&gt;.csv</c> for every trading day in the
	/// lookback window whose existing file is missing or partial. Pulls 1-min bars from massive.com for
	/// the source ticker (SPY when input is SPX-family; the input itself otherwise) over a single
	/// [minNeed, maxNeed] range — Polygon's 50k-bars-per-page cap covers ~4 months of 1-min in one
	/// request, paginated automatically by <see cref="MassivePolygonClient"/>. For SPX-family inputs
	/// the SPY bars are scaled by <c>today_SPX_open / today_SPY_open_at_0930</c> per session so the
	/// 09:30 minute exactly equals the daily ^SPX open — same anchor as scripts/backfill_intraday_polygon.py.
	/// Existing-and-complete files are left alone; today is never touched (live Webull owns it). The
	/// sealed manifest is rewritten after every successful CSV write so a crash mid-loop never leaves
	/// the on-disk CSV out of sync with sealed.json.</summary>
	private static async Task BackfillIntradayAsync(string inputTicker, HistoricalBarCache dailyBars, DateTime earliest, DateTime asOf, CancellationToken cancellation)
	{
		var apiConfig = TryLoadApiConfig();
		if (apiConfig == null)
		{
			AnsiConsole.MarkupLine("  intraday: [yellow]skipped[/] (api-config.json not found — run `wa sniff` to bootstrap it)");
			return;
		}
		if (string.IsNullOrWhiteSpace(apiConfig.MassiveApiKey))
		{
			AnsiConsole.MarkupLine("  intraday: [yellow]skipped[/] ([italic]massiveApiKey[/] not set in api-config.json)");
			return;
		}

		var todayNy = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		var earliestNy = TimeZoneInfo.ConvertTime(earliest, NyTz).Date;
		var audit = AuditIntraday(inputTicker, earliestNy, todayNy);
		var totalDays = audit.Complete.Count + audit.Partial.Count + audit.Missing.Count;
		var needBackfill = audit.Partial.Concat(audit.Missing).OrderBy(d => d).ToList();

		if (needBackfill.Count == 0)
		{
			AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: [green]ok[/] (all {totalDays} trading days complete)");
			return;
		}

		AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: {audit.Complete.Count}/{totalDays} complete; pulling {needBackfill.Count} from massive.com");
		AnsiConsole.MarkupLine($"    needed: {Markup.Escape(FormatDateList(needBackfill, maxInline: 12))}");

		var isSpxSynth = SpxSynthesizedTickers.Contains(inputTicker);
		var sourceTicker = isSpxSynth ? "SPY" : inputTicker;
		var minNeed = DateOnly.FromDateTime(needBackfill[0]);
		var maxNeed = DateOnly.FromDateTime(needBackfill[^1]);
		var fetched = await MassivePolygonClient.FetchMinuteAggregatesAsync(apiConfig.MassiveApiKey, sourceTicker, minNeed, maxNeed, cancellation);
		if (fetched.Count == 0)
		{
			AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: [red]failed[/] (no bars returned from massive.com)");
			return;
		}

		var byDate = new Dictionary<DateTime, List<MinuteBar>>();
		foreach (var b in fetched)
		{
			var d = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date;
			if (!byDate.TryGetValue(d, out var list)) byDate[d] = list = new List<MinuteBar>();
			list.Add(b);
		}

		var sealedDates = audit.SealedDates;
		var written = new List<DateTime>();
		var skipped = new List<(DateTime Date, string Reason)>();
		foreach (var d in needBackfill)
		{
			if (!byDate.TryGetValue(d, out var dayBars) || dayBars.Count == 0)
			{
				skipped.Add((d, "no source bars from massive.com"));
				continue;
			}

			IReadOnlyList<MinuteBar> output = dayBars;
			if (isSpxSynth)
			{
				var synth = await SynthesizeSpxAsync(d, dayBars, dailyBars, inputTicker, cancellation);
				if (synth == null)
				{
					skipped.Add((d, "no daily open for SPX synthesis"));
					continue;
				}
				output = synth;
			}

			var path = Path.Combine(audit.IntradayDir, $"{d:yyyy-MM-dd}.csv");
			WriteIntradayCsv(path, output);
			sealedDates.Add(d);
			SaveSealedManifest(audit.SealedPath, sealedDates);
			written.Add(d);
		}

		var skipNote = skipped.Count > 0 ? $", [yellow]skipped {skipped.Count}[/]" : "";
		AnsiConsole.MarkupLine($"  intraday/{Markup.Escape(inputTicker)}: [green]wrote {written.Count}[/] file(s){skipNote}");
		foreach (var (d, reason) in skipped)
			AnsiConsole.MarkupLine($"    [yellow]{d:yyyy-MM-dd}[/]: skipped ({Markup.Escape(reason)})");
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

	/// <summary>Reads the per-ticker sealed manifest at <c>data/intraday/&lt;TICKER&gt;/sealed.json</c>.
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
		var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, json);
		File.Move(tmp, path, overwrite: true);
	}

	private sealed class SealedManifest
	{
		[System.Text.Json.Serialization.JsonPropertyName("sealed")]
		public List<string> Sealed { get; set; } = new();
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

	/// <summary>"Complete" = file exists with at least 380 RTH-window bars, the 09:30 ET opening bar
	/// present, and the 15:59 ET closing bar present. The strict close requirement catches partial
	/// live captures that stopped a few minutes before 16:00 (the common case after `wa ai watch` is
	/// shut down before the bell). Early-close days will fail this check on first run and be
	/// re-pulled from massive.com, then sealed via <c>.sealed.json</c> so they don't reseed on
	/// subsequent runs.</summary>
	private static bool LooksComplete(string path)
	{
		if (!File.Exists(path)) return false;
		var bars = ReadIntradayCsv(path);

		const int rthCloseMinute = 15 * 60 + 59;  // 15:59 ET bar — opens the 15:59-16:00 final RTH minute
		int rthCount = 0;
		bool has0930 = false;
		bool has1559 = false;
		foreach (var b in bars)
		{
			var et = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz);
			var minuteOfDay = et.Hour * 60 + et.Minute;
			if (minuteOfDay < 9 * 60 + 30 || minuteOfDay >= 16 * 60) continue;
			rthCount++;
			if (et.Hour == 9 && et.Minute == 30) has0930 = true;
			if (minuteOfDay == rthCloseMinute) has1559 = true;
		}
		return rthCount >= 380 && has0930 && has1559;
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

	private static async Task<List<MinuteBar>?> SynthesizeSpxAsync(DateTime nyDate, IReadOnlyList<MinuteBar> spyBars, HistoricalBarCache dailyBars, string inputTicker, CancellationToken cancellation)
	{
		var dailyBar = await dailyBars.GetBarAsync(inputTicker, nyDate, cancellation);
		if (dailyBar == null || dailyBar.Open <= 0m) return null;

		decimal? spy0930Open = null;
		foreach (var b in spyBars)
		{
			var et = TimeZoneInfo.ConvertTime(b.Timestamp, NyTz);
			if (et.Date == nyDate && et.Hour == 9 && et.Minute == 30)
			{
				spy0930Open = b.Open;
				break;
			}
		}
		if (!spy0930Open.HasValue || spy0930Open.Value <= 0m) return null;

		var ratio = dailyBar.Open / spy0930Open.Value;
		var output = new List<MinuteBar>(spyBars.Count);
		foreach (var b in spyBars)
		{
			output.Add(new MinuteBar(
				b.Timestamp,
				Math.Round(b.Open * ratio, 4),
				Math.Round(b.High * ratio, 4),
				Math.Round(b.Low * ratio, 4),
				Math.Round(b.Close * ratio, 4),
				0));
		}
		return output;
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
