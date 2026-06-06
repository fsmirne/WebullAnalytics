using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.Scraper;

/// <summary>Configuration for the chain-snapshot scraper. Loaded from
/// <c>data/scraper-config.json</c>; CLI flags override individual fields per-run.</summary>
internal sealed class ScraperConfig
{
	[JsonPropertyName("intervalSeconds")]
	public int IntervalSeconds { get; set; } = 60;

	/// <summary>Wall-clock ET HH:mm or HH:mm:ss at which the loop fires its first tick. Earlier
	/// than RTH open (09:30) is fine — pre-market chain quotes are persisted just like RTH ones.</summary>
	[JsonPropertyName("startTime")]
	public string StartTime { get; set; } = "09:25";

	/// <summary>Wall-clock ET HH:mm or HH:mm:ss after which the loop exits. Set a few minutes past
	/// 16:00 to capture the closing print's NBBO settle.</summary>
	[JsonPropertyName("endTime")]
	public string EndTime { get; set; } = "16:05";

	/// <summary>Output root for per-day JSONL snapshots. Each day's file lives at
	/// <c>{outputPath}/<TICKER>/<date>.jsonl</c>. Relative paths are resolved against
	/// <see cref="WebullAnalytics.Program.BaseDir"/>.</summary>
	[JsonPropertyName("outputPath")]
	public string OutputPath { get; set; } = "data/chain-snapshots";

	/// <summary>If true, skip ticks when the ET clock is outside <see cref="StartTime"/>..<see cref="EndTime"/>
	/// AND outside US market hours (09:30..16:00 ET). The boundary handling is forgiving — pre-market
	/// and after-hours within the configured start/end window are persisted regardless. Default false:
	/// honor StartTime/EndTime literally and let the user decide what window to capture.</summary>
	[JsonPropertyName("marketHoursOnly")]
	public bool MarketHoursOnly { get; set; } = false;

	/// <summary>How many times to re-fetch the chain when Webull returns no contracts for the minute
	/// before giving up. A missing minute forces the backtest to interpolate, so a brief retry within
	/// the interval is preferable to persisting an empty line (or nothing). Set 0 to disable retries.</summary>
	[JsonPropertyName("emptyRetryCount")]
	public int EmptyRetryCount { get; set; } = 3;

	/// <summary>Seconds to wait between empty-chain retries. Keep small so all retries finish inside one
	/// interval (default 3 × 3s = 9s, comfortably within the 60s tick).</summary>
	[JsonPropertyName("emptyRetryDelaySeconds")]
	public int EmptyRetryDelaySeconds { get; set; } = 3;

	/// <summary>How many calendar days of expiries to capture, measured from the scrape date. 0 (default)
	/// keeps only today's expiry (0DTE) — the original behavior and smallest files. Raise it to also capture
	/// the further-dated expiries the diagonal/calendar long legs use (e.g. 45 covers the 21–45 DTE band), so
	/// `wa options reprice` can validate the far-leg synthetic pricing against real quotes. Webull's chain
	/// spans ~30k contracts across all expirations; the DTE cap bounds how much of that is persisted per minute.</summary>
	[JsonPropertyName("maxDte")]
	public int MaxDte { get; set; } = 0;

	/// <summary>When <see cref="MaxDte"/> > 0, the further-dated expiries come back from the chain/list endpoint
	/// as symbols WITHOUT bid/ask/IV (only the front 0DTE expiry is quoted there). Those contracts are then
	/// refreshed via Webull's queryBatch endpoint — but only within ±this fraction of spot, to bound the number
	/// of round-trips (the full far chain is ~thousands of strikes). 0.06 (±6%) comfortably covers the
	/// 0.15–0.55 delta strikes the diagonal/calendar legs use at 21–45 DTE. Each refreshed strike is one
	/// queryBatch slot (batched 50/call), so a wider range or larger MaxDte means more round-trips per tick —
	/// raise <see cref="IntervalSeconds"/> accordingly (e.g. 300s) when capturing the far-dated chain.</summary>
	[JsonPropertyName("farStrikeRangeFraction")]
	public decimal FarStrikeRangeFraction { get; set; } = 0.06m;
}

internal static class ScraperConfigLoader
{
	internal const string ConfigPath = "data/scraper-config.json";

	internal static ScraperConfig Load(string? overridePath = null)
	{
		var path = overridePath ?? WebullAnalytics.Program.ResolvePath(ConfigPath);
		if (!File.Exists(path)) return new ScraperConfig();
		try
		{
			var json = File.ReadAllText(path);
			var cfg = JsonSerializer.Deserialize<ScraperConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
			return cfg ?? new ScraperConfig();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Warning: failed to parse {path}: {ex.Message}. Using defaults.");
			return new ScraperConfig();
		}
	}
}
