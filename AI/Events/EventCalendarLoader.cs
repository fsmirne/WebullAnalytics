using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.AI;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Events;

/// <summary>Composes a per-tick <see cref="EventCalendar"/> from Yahoo's calendar endpoint and an
/// optional override file. Override entries take precedence over Yahoo data — useful when Yahoo lags
/// or for non-US tickers where the source is unreliable. Pure I/O wrapper; the veto math lives in
/// <see cref="EventVeto"/>.</summary>
internal static class EventCalendarLoader
{
	public static async Task<EventCalendar> LoadAsync(IReadOnlyCollection<string> tickers, OpenerEventsConfig cfg, DateTime asOf, CancellationToken cancellation)
	{
		if (!cfg.Enabled || tickers.Count == 0)
			return EventCalendar.Empty;

		var fromYahoo = await YahooCalendarClient.FetchEventsAsync(tickers, asOf, cancellation);

		var merged = new Dictionary<string, TickerEvents>(StringComparer.OrdinalIgnoreCase);
		foreach (var (k, v) in fromYahoo) merged[k] = v;

		var overrides = LoadOverrides(cfg.OverrideFilePath);
		foreach (var (k, v) in overrides) merged[k] = v;

		return new EventCalendar(merged);
	}

	private static Dictionary<string, TickerEvents> LoadOverrides(string? overridePath)
	{
		var empty = new Dictionary<string, TickerEvents>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(overridePath)) return empty;

		var resolved = Path.IsPathRooted(overridePath) ? overridePath : Program.ResolvePath(overridePath);
		if (!File.Exists(resolved))
		{
			Console.WriteLine($"Warning: events override file not found: {resolved}");
			return empty;
		}

		try
		{
			var raw = File.ReadAllText(resolved);
			return ParseOverrides(raw);
		}
		catch (IOException ex)
		{
			Console.WriteLine($"Warning: failed to read events override file: {ex.Message}");
			return empty;
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"Warning: events override file malformed: {ex.Message}");
			return empty;
		}
	}

	/// <summary>Parses the override-file JSON. Public for tests. Format:
	/// <c>{"AAPL":{"earnings":"2026-08-01","earningsTime":"AMC","exDividend":"2026-08-09","dividendAmount":0.24}}</c>.
	/// Unknown fields are ignored; missing dates become null. Returns an empty map on any structural failure.</summary>
	internal static Dictionary<string, TickerEvents> ParseOverrides(string json)
	{
		var result = new Dictionary<string, TickerEvents>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(json)) return result;

		var parsed = JsonSerializer.Deserialize<Dictionary<string, OverrideEntry>>(json, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
		});
		if (parsed == null) return result;

		foreach (var (ticker, entry) in parsed)
		{
			if (string.IsNullOrWhiteSpace(ticker)) continue;
			result[ticker.ToUpperInvariant()] = new TickerEvents(
				Ticker: ticker.ToUpperInvariant(),
				NextEarningsDate: ParseDate(entry.Earnings),
				EarningsTime: entry.EarningsTime,
				NextExDividendDate: ParseDate(entry.ExDividend),
				DividendAmount: entry.DividendAmount
			);
		}
		return result;
	}

	private static DateTime? ParseDate(string? input)
	{
		if (string.IsNullOrWhiteSpace(input)) return null;
		if (DateTime.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
			return date.Date;
		if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallback))
			return fallback.Date;
		return null;
	}

	private sealed class OverrideEntry
	{
		[JsonPropertyName("earnings")] public string? Earnings { get; set; }
		[JsonPropertyName("earningsTime")] public string? EarningsTime { get; set; }
		[JsonPropertyName("exDividend")] public string? ExDividend { get; set; }
		[JsonPropertyName("dividendAmount")] public decimal? DividendAmount { get; set; }
	}
}
