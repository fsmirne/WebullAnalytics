using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;

namespace WebullAnalytics.Options;

/// <summary>`wa options audit <ticker>` — reports per-contract option-bar coverage completeness over the
/// on-disk history under <c>data/options/<ticker>/<expiry>/</c>. The options analog of
/// <c>wa ai history <ticker> --audit</c> (which covers the intraday tape). No network fetches; reads the
/// captured CSVs directly. Exit code 0 when coverage is clean, 2 when any gap is found.
///
/// <para>Checks are at the EXPIRY level so illiquid single strikes (which legitimately skip sessions) don't
/// create noise — a session counts as covered if ANY contract of that expiry traded it:</para>
/// <list type="bullet">
/// <item>Frontier lag — a still-live expiry whose latest captured session is behind the last closed session
/// (the bug `--source all` Webull top-ups exist to prevent).</item>
/// <item>Missing expiries — market days with no expiry folder at all (e.g. 0-DTE expiries the strategy never
/// touched). Assumes daily expirations, true for SPY/SPXW/XSP over the captured window.</item>
/// <item>Interior gaps — zero-coverage market days between two DENSE sessions (a hole in liquid coverage is a
/// real pull defect; a hole amid 2-4-contract days is just illiquidity, where no source data exists).</item>
/// <item>Incomplete expired — an expired expiry whose last bar is well before expiry.</item>
/// </list></summary>
internal sealed class OptionsAuditSettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Ticker root to audit (e.g. SPY). Reads data/options/<TICKER>/<expiry>/*.csv.")]
	public string Ticker { get; set; } = "";

	[CommandOption("--since <DATE>")]
	[Description("Only audit expiries on/after this date (YYYY-MM-DD), and only check for missing expiries from this date forward. Default: the full on-disk window (earliest captured expiry).")]
	public string? Since { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		return ValidationResult.Success();
	}
}

internal sealed class OptionsAuditCommand : AsyncCommand<OptionsAuditSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	public override Task<int> ExecuteAsync(CommandContext context, OptionsAuditSettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		DateTime? since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture)
			: null;

		var optionsRoot = Path.Combine(Program.ResolvePath("data/options"), ticker);
		AnsiConsole.MarkupLine($"[bold]Option-bar audit for {Markup.Escape(ticker)}[/]" + (since.HasValue ? $" (expiry ≥ {since.Value:yyyy-MM-dd})" : ""));
		if (!Directory.Exists(optionsRoot))
		{
			AnsiConsole.MarkupLine($"  [yellow]no option data on disk[/] at {Markup.Escape(optionsRoot)} — run `wa options backfill {Markup.Escape(ticker)}` first");
			return Task.FromResult(2);
		}

		// Each subdirectory is one expiry (yyyy-MM-dd); non-date entries (sealed.json) are skipped. Read each
		// expiry's per-session contract counts up front (the only disk I/O — the audit logic itself is pure).
		var expiryDayCounts = new SortedDictionary<DateTime, SortedDictionary<DateTime, int>>();
		var totalContracts = 0;
		foreach (var dir in Directory.EnumerateDirectories(optionsRoot))
		{
			var name = Path.GetFileName(dir);
			if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp)) continue;
			if (since.HasValue && exp.Date < since.Value.Date) continue;
			var (dayCounts, contracts) = CollectTradeDates(dir);
			totalContracts += contracts;
			expiryDayCounts[exp.Date] = dayCounts;
		}
		if (expiryDayCounts.Count == 0)
		{
			AnsiConsole.MarkupLine("  [yellow]no expiry folders in range[/] — nothing to audit");
			return Task.FromResult(2);
		}

		// The session we expect data through: today if the cash session has closed (the backfill captures
		// today's bars after the close), otherwise the previous trading day.
		var nowEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, NyTz);
		var todayEt = nowEt.Date;
		var todayClosed = MarketCalendar.IsOpen(todayEt) && nowEt.TimeOfDay >= TimeSpan.FromHours(16);
		var expectedFrontier = todayClosed ? todayEt : MarketCalendar.PreviousOpenOnOrBefore(todayEt.AddDays(-1));
		var lookback = OptionsBackfillCommand.DeriveLookbackDays(ticker);
		var windowStart = since?.Date ?? expiryDayCounts.Keys.First();

		var report = OptionsCoverageAuditor.Audit(expiryDayCounts, windowStart, todayEt, expectedFrontier, lookback);

		AnsiConsole.MarkupLine($"  window: {expiryDayCounts.Keys.First():yyyy-MM-dd} → {expiryDayCounts.Keys.Last():yyyy-MM-dd} ({expiryDayCounts.Count} expiries: {report.LiveCount} live, {report.ExpiredCount} expired, {totalContracts:N0} contracts)");
		AnsiConsole.MarkupLine($"  expected through last closed session: {expectedFrontier:yyyy-MM-dd}");

		AnsiConsole.MarkupLine(report.FrontierLag.Count == 0
			? "  [green]frontier: all live expiries current[/]"
			: $"  [red]frontier: {report.FrontierLag.Count} live expiry(ies) lag[/] (run `wa options backfill {Markup.Escape(ticker)}` with --source all)");
		foreach (var (exp, last) in report.FrontierLag)
			AnsiConsole.MarkupLine($"    [red]{exp:yyyy-MM-dd}[/]: last {last:yyyy-MM-dd} (expected {expectedFrontier:yyyy-MM-dd})");

		AnsiConsole.MarkupLine(report.MissingExpiries.Count == 0
			? "  [green]missing expiries: none[/]"
			: $"  [red]missing expiries: {report.MissingExpiries.Count}[/] (no folder): {Markup.Escape(FormatDateList(report.MissingExpiries, 30))}");

		if (report.InteriorGapTotal == 0)
			AnsiConsole.MarkupLine("  [green]interior gaps: none[/]");
		else
		{
			AnsiConsole.MarkupLine($"  [red]interior gaps: {report.InteriorGapTotal}[/] (expiry, session) across {report.InteriorGaps.Count} expiry(ies):");
			foreach (var (exp, days) in report.InteriorGaps.OrderByDescending(g => g.Days.Count).Take(25))
				AnsiConsole.MarkupLine($"    [red]{exp:yyyy-MM-dd}[/]: {days.Count} session(s): {Markup.Escape(FormatDateList(days, 12))}");
		}

		if (report.IncompleteExpired.Count > 0)
		{
			AnsiConsole.MarkupLine($"  [yellow]incomplete expired: {report.IncompleteExpired.Count}[/] (last bar well before expiry):");
			foreach (var (exp, last) in report.IncompleteExpired.OrderBy(x => x.Expiry).Take(25))
				AnsiConsole.MarkupLine($"    [yellow]{exp:yyyy-MM-dd}[/]: last {last:yyyy-MM-dd}");
		}

		AnsiConsole.MarkupLine(report.Clean ? "  [green]=> coverage clean[/]" : "  [red]=> gaps found[/]");
		return Task.FromResult(report.Clean ? 0 : 2);
	}

	/// <summary>For an expiry folder: how many distinct contracts traded on each ET session (date → contract
	/// count, sorted by date), plus the total contract count. A contract counts once per date it has any bar.
	/// Header and malformed lines are skipped.</summary>
	private static (SortedDictionary<DateTime, int> DayCounts, int Contracts) CollectTradeDates(string expiryDir)
	{
		var dayCounts = new SortedDictionary<DateTime, int>();
		var contracts = 0;
		foreach (var f in Directory.EnumerateFiles(expiryDir, "*.csv"))
		{
			contracts++;
			var seen = new HashSet<DateTime>();
			foreach (var line in File.ReadLines(f))
			{
				// Data rows start "YYYY-MM-DD..."; the header ("timestamp_utc,...") has no '-' at index 4.
				if (line.Length < 10 || line[4] != '-') continue;
				if (DateTime.TryParseExact(line.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
					seen.Add(d);
			}
			foreach (var d in seen)
				dayCounts[d] = dayCounts.TryGetValue(d, out var n) ? n + 1 : 1;
		}
		return (dayCounts, contracts);
	}

	/// <summary>Compact date list: inline up to <paramref name="maxInline"/>, else head/tail with an elision.
	/// Mirrors the intraday audit's formatter.</summary>
	private static string FormatDateList(IReadOnlyList<DateTime> dates, int maxInline)
	{
		if (dates.Count == 0) return "(none)";
		if (dates.Count <= maxInline) return string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")));
		var half = maxInline / 2;
		var head = string.Join(", ", dates.Take(half).Select(d => d.ToString("yyyy-MM-dd")));
		var tail = string.Join(", ", dates.Skip(dates.Count - half).Select(d => d.ToString("yyyy-MM-dd")));
		return $"{head}, … ({dates.Count - maxInline} more) …, {tail}";
	}
}

/// <summary>Outcome of an option-coverage audit. <see cref="Clean"/> drives the command's exit code.</summary>
internal sealed record OptionsCoverageReport(
	List<(DateTime Expiry, DateTime Last)> FrontierLag,
	List<DateTime> MissingExpiries,
	List<(DateTime Expiry, List<DateTime> Days)> InteriorGaps,
	List<(DateTime Expiry, DateTime Last)> IncompleteExpired,
	int LiveCount,
	int ExpiredCount)
{
	public int InteriorGapTotal => InteriorGaps.Sum(g => g.Days.Count);
	public bool Clean => FrontierLag.Count == 0 && MissingExpiries.Count == 0 && InteriorGaps.Count == 0 && IncompleteExpired.Count == 0;
}

/// <summary>Pure coverage analysis (no I/O), separated from <see cref="OptionsAuditCommand"/> so it can be
/// unit-tested directly: caller supplies expiry → (session → contract count); this classifies the gaps.</summary>
internal static class OptionsCoverageAuditor
{
	// An expired contract can stop trading a few sessions before expiry; allow this slack before calling an
	// expired expiry's capture "incomplete" (mirrors the backfill's ExpiredCompletionToleranceDays).
	internal const int ExpiredCompletionToleranceDays = 7;

	// An interior zero-coverage day is only flagged when both bracketing sessions carry at least this many
	// contracts. Below it the expiry is too thin (far-dated XSP, deep wings) for an empty day to mean a pull
	// defect rather than simply no trades — there's no source data to recover.
	internal const int MinNeighborDensity = 10;

	/// <param name="expiryDayCounts">Expiry date → (session date → distinct contracts that traded). An expiry
	/// present with an empty map is a folder with no usable bars.</param>
	/// <param name="windowStart">First date to check for missing expiries (inclusive).</param>
	/// <param name="todayEt">Today (ET). Expiries on/after this are "live", before are "expired".</param>
	/// <param name="expectedFrontier">Last closed session live expiries should reach, and the end of the
	/// missing-expiry scan.</param>
	/// <param name="lookback">Days-before-expiry the backfill pulls; interior gaps deeper than this are ignored
	/// (out of the strategy's horizon, and far out everything is thin).</param>
	internal static OptionsCoverageReport Audit(
		IReadOnlyDictionary<DateTime, SortedDictionary<DateTime, int>> expiryDayCounts,
		DateTime windowStart, DateTime todayEt, DateTime expectedFrontier, int lookback)
	{
		var frontierLag = new List<(DateTime, DateTime)>();
		var incompleteExpired = new List<(DateTime, DateTime)>();
		var interiorGaps = new List<(DateTime, List<DateTime>)>();
		var liveCount = 0;
		var expiredCount = 0;

		foreach (var (exp, dayCounts) in expiryDayCounts.OrderBy(kv => kv.Key))
		{
			var isLive = exp >= todayEt;
			if (isLive) liveCount++; else expiredCount++;
			if (dayCounts.Count == 0) continue; // folder with no usable bars — can't assess gaps/frontier

			var covered = dayCounts.Keys.ToList(); // sorted (SortedDictionary)
			var last = covered[^1];

			// Interior gaps: zero-coverage market days between two consecutive covered sessions, but only when
			// BOTH bracketing sessions are dense (>= MinNeighborDensity). Bounded to the backfill window
			// (expiry − lookback).
			var gapFloor = exp.AddDays(-lookback);
			var gapDays = new List<DateTime>();
			for (var i = 1; i < covered.Count; i++)
			{
				var a = covered[i - 1];
				var b = covered[i];
				if (dayCounts[a] < MinNeighborDensity || dayCounts[b] < MinNeighborDensity) continue;
				for (var d = a.AddDays(1); d < b; d = d.AddDays(1))
					if (d >= gapFloor && MarketCalendar.IsOpen(d)) gapDays.Add(d);
			}
			if (gapDays.Count > 0) interiorGaps.Add((exp, gapDays));

			if (isLive)
			{
				if (last < expectedFrontier) frontierLag.Add((exp, last));
			}
			else if (last < exp.AddDays(-ExpiredCompletionToleranceDays))
				incompleteExpired.Add((exp, last));
		}

		// Missing expiries: market days with no folder at all, from the window start through the frontier.
		var missingExpiries = new List<DateTime>();
		for (var d = windowStart; d <= expectedFrontier; d = d.AddDays(1))
			if (MarketCalendar.IsOpen(d) && !expiryDayCounts.ContainsKey(d)) missingExpiries.Add(d);

		return new OptionsCoverageReport(frontierLag, missingExpiries, interiorGaps, incompleteExpired, liveCount, expiredCount);
	}
}
