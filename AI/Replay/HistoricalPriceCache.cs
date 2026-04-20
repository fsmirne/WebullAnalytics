using System.Globalization;
using System.Text;

namespace WebullAnalytics.AI.Replay;

/// <summary>
/// Disk-cached daily closes from Yahoo. On first read for a ticker the cache fetches
/// the full historical series via YahooOptionsClient.FetchHistoricalClosesAsync and writes to
/// data/history/&lt;ticker&gt;.csv. Subsequent reads hit the disk cache.
/// </summary>
internal sealed class HistoricalPriceCache
{
	private readonly string _cacheDir;
	private readonly Dictionary<string, Dictionary<DateTime, decimal>> _memory = new(StringComparer.OrdinalIgnoreCase);

	public HistoricalPriceCache(string? cacheDir = null)
	{
		_cacheDir = cacheDir ?? Program.ResolvePath("data/history");
		Directory.CreateDirectory(_cacheDir);
	}

	public async Task<decimal?> GetCloseAsync(string ticker, DateTime date, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, cancellation);
		return map.TryGetValue(date.Date, out var close) ? close : null;
	}

	private async Task<Dictionary<DateTime, decimal>> LoadOrFetchAsync(string ticker, CancellationToken cancellation)
	{
		if (_memory.TryGetValue(ticker, out var cached)) return cached;

		var path = Path.Combine(_cacheDir, $"{ticker.ToUpperInvariant()}.csv");
		Dictionary<DateTime, decimal> map;
		if (File.Exists(path))
		{
			map = ParseCsv(await File.ReadAllTextAsync(path, cancellation));
		}
		else
		{
			var from = DateTime.UtcNow.AddYears(-2);
			var to = DateTime.UtcNow;
			map = await YahooOptionsClient.FetchHistoricalClosesAsync(ticker, from, to, cancellation);
			if (map.Count > 0)
				await File.WriteAllTextAsync(path, SerializeCsv(map), cancellation);
		}

		_memory[ticker] = map;
		return map;
	}

	/// <summary>Parses either the two-column native format ("date,close") or Yahoo's seven-column
	/// historical export ("Date,Open,High,Low,Close,Adj Close,Volume"). Skips the header row
	/// regardless of format. When ≥5 columns are present, column index 4 (Close) is used; otherwise
	/// column index 1.</summary>
	private static Dictionary<DateTime, decimal> ParseCsv(string content)
	{
		var map = new Dictionary<DateTime, decimal>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Split(',');
			if (parts.Length < 2) continue;
			if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			var closeIdx = parts.Length >= 5 ? 4 : 1;
			if (!decimal.TryParse(parts[closeIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			map[d] = close;
		}
		return map;
	}

	private static string SerializeCsv(Dictionary<DateTime, decimal> map)
	{
		var sb = new StringBuilder("date,close\n");
		foreach (var kv in map.OrderBy(k => k.Key))
			sb.Append(kv.Key.ToString("yyyy-MM-dd")).Append(',').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
		return sb.ToString();
	}
}
