using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI;
using WebullAnalytics.Api;

namespace WebullAnalytics.Options;

internal sealed class OptionsSeedChainSettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[System.ComponentModel.Description("Underlying root to enumerate (e.g. SPY). Lists its historical option contracts from massive's reference endpoint and seeds data/options-discovery/<TICKER>.jsonl so `wa options backfill` can pull the bars. Bootstraps an underlying with no on-disk chain.")]
	public string Ticker { get; set; } = "";

	[CommandOption("--since <DATE>")]
	[System.ComponentModel.Description("Earliest expiration date to include (YYYY-MM-DD). Default: 2 years before today.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[System.ComponentModel.Description("Latest expiration date to include (YYYY-MM-DD). Default: today + 45 days (covers near-term still-live expiries).")]
	public string? Until { get; set; }

	[CommandOption("--min-strike <N>")]
	[System.ComponentModel.Description("Lowest strike to include. Omit to include all strikes.")]
	public decimal? MinStrike { get; set; }

	[CommandOption("--max-strike <N>")]
	[System.ComponentModel.Description("Highest strike to include. Omit to include all strikes.")]
	public decimal? MaxStrike { get; set; }

	[CommandOption("--underlying <SYM>")]
	[System.ComponentModel.Description("Underlying_ticker to query at massive when it differs from the OCC root. SPXW options are filed under SPX (per reference_massive_api). Default: SPXW→SPX, else the ticker itself.")]
	public string? Underlying { get; set; }

	[CommandOption("--atm-band <N>")]
	[System.ComponentModel.Description("ATM-tracking mode: keep only strikes within ±N of where the underlying actually traded during each expiry's relevant window (the days it serves as a 0-3 DTE short or 14-30 DTE long leg). Tracks a trending underlying so we seed the contracts the strategy can actually pick, not the full static box. Reads data/history/<TICKER>.csv. Recommended for dense chains (e.g. SPY).")]
	public decimal? AtmBand { get; set; }

	[CommandOption("--count")]
	[System.ComponentModel.Description("Report the contract count + strike/expiry distribution only; do not write the discovery catalog. Use to size a backfill before committing to it.")]
	public bool CountOnly { get; set; }
}

/// <summary>`wa options chain &lt;ticker&gt;` — enumerates an underlying's historical option contracts from
/// massive's reference endpoint and seeds the discovery catalog so the existing backfill can pull the bars.
/// Needed for underlyings the bot never traded (no Webull registry, no discovery, no on-disk chain) where the
/// usual discover→backfill loop can't bootstrap because it has no bars to read a chain from.</summary>
internal sealed class OptionsSeedChainCommand : AsyncCommand<OptionsSeedChainSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, OptionsSeedChainSettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		var until = settings.Until != null
			? DateOnly.ParseExact(settings.Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: DateOnly.FromDateTime(DateTime.Today).AddDays(45);
		var since = settings.Since != null
			? DateOnly.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: DateOnly.FromDateTime(DateTime.Today).AddYears(-2);

		var apiConfigPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(apiConfigPath)) { AnsiConsole.MarkupLine($"  [red]api-config.json not found[/] at {Markup.Escape(apiConfigPath)}"); return 1; }
		ApiConfig? apiConfig;
		try { apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(apiConfigPath)); }
		catch (Exception ex) { AnsiConsole.MarkupLine($"  [red]failed to parse api-config.json[/]: {Markup.Escape(ex.Message)}"); return 1; }
		if (apiConfig == null || string.IsNullOrWhiteSpace(apiConfig.MassiveApiKey)) { AnsiConsole.MarkupLine("  [red]MassiveApiKey not set in api-config.json[/]"); return 1; }
		MassivePolygonClient.MaxRequestsPerWindow = apiConfig.MassiveMaxRequestsPerMinute;

		AnsiConsole.MarkupLine($"[bold]Enumerating {Markup.Escape(ticker)} contracts[/] expiring {since:yyyy-MM-dd} → {until:yyyy-MM-dd}"
			+ (settings.MinStrike.HasValue || settings.MaxStrike.HasValue ? $", strikes {settings.MinStrike?.ToString() ?? "·"}–{settings.MaxStrike?.ToString() ?? "·"}" : "")
			+ " via massive reference endpoint…");

		// massive files some roots under a different underlying_ticker (SPXW → SPX). Use the override,
		// else the known SPXW mapping, else the ticker itself. The returned OCCs keep their real root.
		var underlying = settings.Underlying ?? (string.Equals(ticker, "SPXW", StringComparison.OrdinalIgnoreCase) ? "SPX" : ticker);
		if (!string.Equals(underlying, ticker, StringComparison.OrdinalIgnoreCase))
			AnsiConsole.MarkupLine($"  querying underlying_ticker={underlying} (root {ticker})");

		// Query both expired and still-live contracts (Polygon returns only one side per call).
		var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var expired in new[] { true, false })
		{
			var batch = await MassivePolygonClient.FetchOptionContractsAsync(
				apiConfig.MassiveApiKey, underlying, expired, since, until, settings.MinStrike, settings.MaxStrike, cancellation);
			// Querying SPX returns both SPXW (weekly, PM-settled) and SPX (monthly, AM-settled) roots —
			// keep only the requested root so we don't catalog/backfill the wrong settlement family.
			foreach (var occ in batch)
			{
				var p = ParsingHelpers.ParseOptionSymbol(occ);
				if (p != null && string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) all.Add(occ);
			}
			AnsiConsole.MarkupLine($"  expired={expired}: {batch.Count} returned");
		}

		if (all.Count == 0) { AnsiConsole.MarkupLine("  [yellow]no contracts returned[/] — check ticker / date range / tier entitlement"); return 0; }

		// ATM-tracking: keep only strikes near where the underlying actually traded during each expiry's
		// relevant entry window ([expiry-32d, expiry-1d] covers the 14-30 DTE long and 0-3 DTE short entry
		// days). This is the cold-start equivalent of what `discover` does via the opener — it tracks a
		// trending underlying so we seed the contracts the strategy can pick, not the full static box.
		if (settings.AtmBand is { } band)
		{
			var closes = LoadDailyCloses(ticker);
			if (closes.Count == 0) { AnsiConsole.MarkupLine($"  [red]--atm-band needs data/history/{ticker}.csv[/] (run `wa ai history {ticker}`)"); return 1; }
			var before = all.Count;
			all.RemoveWhere(occ =>
			{
				var p = ParsingHelpers.ParseOptionSymbol(occ);
				if (p == null) return true;
				var (lo, hi) = AtmRange(closes, p.ExpiryDate.Date.AddDays(-32), p.ExpiryDate.Date.AddDays(-1));
				if (lo == 0m) return true; // no underlying data in window — can't place it, drop
				return p.Strike < lo - band || p.Strike > hi + band;
			});
			AnsiConsole.MarkupLine($"  ATM-band ±{band}: kept {all.Count} of {before} (dropped {before - all.Count} far-from-ATM)");
		}

		// Distribution summary so the caller can pick a strike band before backfilling.
		var strikes = new List<decimal>();
		var expiries = new HashSet<DateTime>();
		foreach (var occ in all)
		{
			var p = ParsingHelpers.ParseOptionSymbol(occ);
			if (p == null) continue;
			strikes.Add(p.Strike);
			expiries.Add(p.ExpiryDate.Date);
		}
		strikes.Sort();
		AnsiConsole.MarkupLine($"  [green]{all.Count}[/] distinct contract(s); {expiries.Count} expiries; strikes {(strikes.Count > 0 ? $"{strikes[0]:N0} → {strikes[^1]:N0}" : "—")}");

		if (settings.CountOnly) { AnsiConsole.MarkupLine("  [dim]--count: catalog not written.[/]"); return 0; }

		// Union into the discovery catalog (same {"occ","ticker"} line format the backfill reads).
		var dir = Program.ResolvePath("data/options-discovery");
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, ticker + ".jsonl");
		var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (File.Exists(path))
			foreach (var line in File.ReadAllLines(path))
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				try { using var d = JsonDocument.Parse(line); if (d.RootElement.TryGetProperty("occ", out var o) && o.GetString() is { } s) existing.Add(s); } catch { }
			}
		var added = 0;
		using (var sw = new StreamWriter(path, append: true))
			foreach (var occ in all)
				if (existing.Add(occ)) { sw.WriteLine(JsonSerializer.Serialize(new { occ, ticker })); added++; }

		AnsiConsole.MarkupLine($"  wrote [green]{added}[/] new OCC(s) to {Markup.Escape(path)} ({existing.Count} total). Now run: [bold]wa options backfill {ticker}[/]");
		return 0;
	}

	/// <summary>Loads date→close from data/history/&lt;TICKER&gt;.csv (header: date,open,high,low,close,...).
	/// Returns an ordered list so AtmRange can window by date. Empty if the file is missing/unreadable.</summary>
	private static IReadOnlyList<(DateTime Date, decimal Close)> LoadDailyCloses(string ticker)
	{
		var path = Program.ResolvePath($"data/history/{ticker}.csv");
		if (!File.Exists(path)) return Array.Empty<(DateTime, decimal)>();
		var result = new List<(DateTime Date, decimal Close)>();
		foreach (var line in File.ReadLines(path))
		{
			var parts = line.Split(',');
			if (parts.Length < 5) continue;
			if (!DateTime.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) continue; // skips header
			if (!decimal.TryParse(parts[4], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var c)) continue;
			result.Add((d.Date, c));
		}
		result.Sort((a, b) => a.Date.CompareTo(b.Date));
		return result;
	}

	/// <summary>Min/max close over [from, to]. Returns (0,0) when no bar falls in the window.</summary>
	private static (decimal Lo, decimal Hi) AtmRange(IReadOnlyList<(DateTime Date, decimal Close)> closes, DateTime from, DateTime to)
	{
		decimal lo = 0m, hi = 0m;
		foreach (var (d, c) in closes)
		{
			if (d < from || d > to) continue;
			if (lo == 0m || c < lo) lo = c;
			if (c > hi) hi = c;
		}
		return (lo, hi);
	}
}
