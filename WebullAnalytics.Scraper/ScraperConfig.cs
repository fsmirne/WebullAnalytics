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
	/// <c>{outputPath}/&lt;TICKER&gt;/&lt;date&gt;.jsonl</c>. Relative paths are resolved against
	/// <see cref="WebullAnalytics.Program.BaseDir"/>.</summary>
	[JsonPropertyName("outputPath")]
	public string OutputPath { get; set; } = "data/chain-snapshots";

	/// <summary>If true, skip ticks when the ET clock is outside <see cref="StartTime"/>..<see cref="EndTime"/>
	/// AND outside US market hours (09:30..16:00 ET). The boundary handling is forgiving — pre-market
	/// and after-hours within the configured start/end window are persisted regardless. Default false:
	/// honor StartTime/EndTime literally and let the user decide what window to capture.</summary>
	[JsonPropertyName("marketHoursOnly")]
	public bool MarketHoursOnly { get; set; } = false;
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
