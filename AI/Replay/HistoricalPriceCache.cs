using System.Globalization;
using System.Text;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Replay;

/// <summary>
/// Disk-cached daily closes from Yahoo. On first read for a ticker the cache fetches
/// the full historical series via YahooOptionsClient.FetchHistoricalClosesAsync and writes to
/// data/history/<ticker>.csv. Subsequent reads hit the disk cache.
/// </summary>
internal sealed class HistoricalPriceCache
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan SettlementCutoff = TimeSpan.FromHours(17);

	private readonly string _cacheDir;
	private readonly Func<string, DateTime, DateTime, CancellationToken, Task<Dictionary<DateTime, decimal>>> _fetchHistoricalClosesAsync;
	private readonly Func<DateTime> _utcNow;
	private readonly Dictionary<string, Dictionary<DateTime, decimal>> _memory = new(StringComparer.OrdinalIgnoreCase);

	public HistoricalPriceCache(string? cacheDir = null) : this(cacheDir, YahooOptionsClient.FetchHistoricalClosesAsync)
	{
	}

	internal HistoricalPriceCache(string? cacheDir, Func<string, DateTime, DateTime, CancellationToken, Task<Dictionary<DateTime, decimal>>> fetchHistoricalClosesAsync, Func<DateTime>? utcNow = null)
	{
		_cacheDir = cacheDir ?? Program.ResolvePath("data/history");
		_fetchHistoricalClosesAsync = fetchHistoricalClosesAsync;
		_utcNow = utcNow ?? (() => DateTime.UtcNow);
		Directory.CreateDirectory(_cacheDir);
	}

	public async Task<decimal?> GetCloseAsync(string ticker, DateTime date, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, date.Date, cancellation);
		return map.TryGetValue(date.Date, out var close) ? close : null;
	}

	private async Task<Dictionary<DateTime, decimal>> LoadOrFetchAsync(string ticker, DateTime neededThrough, CancellationToken cancellation)
	{
		// Yahoo's daily chart endpoint returns the in-progress intraday last as the "close" for the
		// current trading day. Clamp the request to the most recent settled NY trading date (today if
		// it is at or past the 5pm NY cutoff, otherwise yesterday) so we never persist a stale value.
		var effectiveThrough = ClampToSettled(neededThrough);
		if (_memory.TryGetValue(ticker, out var cached) && !NeedsRefresh(cached, effectiveThrough)) return cached;

		var path = Path.Combine(_cacheDir, $"{ticker.ToUpperInvariant()}.csv");
		Dictionary<DateTime, decimal> map;
		if (File.Exists(path))
		{
			map = ParseCsv(await File.ReadAllTextAsync(path, cancellation));
			if (NeedsRefresh(map, effectiveThrough))
			{
				var refreshed = await FetchRangeAsync(ticker, map.Count > 0 ? map.Keys.Max().Date.AddDays(1) : effectiveThrough.AddYears(-2), effectiveThrough, cancellation);
				Merge(map, refreshed);
				if (refreshed.Count > 0)
					await File.WriteAllTextAsync(path, SerializeCsv(map), cancellation);
			}
		}
		else
		{
			map = await FetchRangeAsync(ticker, effectiveThrough.AddYears(-2), effectiveThrough, cancellation);
			if (map.Count > 0)
				await File.WriteAllTextAsync(path, SerializeCsv(map), cancellation);
		}

		_memory[ticker] = map;
		return map;
	}

	private DateTime ClampToSettled(DateTime neededThrough)
	{
		var settled = LatestSettledNyDate();
		return neededThrough.Date < settled ? neededThrough.Date : settled;
	}

	private DateTime LatestSettledNyDate()
	{
		var nowNy = TimeZoneInfo.ConvertTimeFromUtc(_utcNow(), NyTz);
		var settled = nowNy.TimeOfDay >= SettlementCutoff ? nowNy.Date : nowNy.Date.AddDays(-1);
		// Walk back past weekends + holidays so the settled date always lands on a session Yahoo
		// will actually have a close for.
		while (!MarketCalendar.IsOpen(settled)) settled = settled.AddDays(-1);
		return settled;
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

	/// <summary>Returns the last <paramref name="count"/> daily closes strictly before <paramref name="asOf"/>,
	/// oldest-first. Returns fewer than <paramref name="count"/> entries if the cache has less data.
	/// The strict-less-than filter prevents backtest lookahead: at a 09:30 step on day X the model
	/// must not consume day X's close (which the historical cache already knows). In live mode the
	/// cache only has settled days, so the strict filter is a no-op there.</summary>
	public async Task<IReadOnlyList<decimal>> GetRecentClosesAsync(string ticker, int count, DateTime asOf, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, asOf.Date, cancellation);
		return map
			.Where(kv => kv.Key.Date < asOf.Date)
			.OrderByDescending(kv => kv.Key)
			.Take(count)
			.OrderBy(kv => kv.Key)
			.Select(kv => kv.Value)
			.ToList();
	}

	private static bool NeedsRefresh(Dictionary<DateTime, decimal> map, DateTime neededThrough)
	{
		if (map.Count == 0) return true;
		return map.Keys.Max().Date < neededThrough.Date;
	}

	private async Task<Dictionary<DateTime, decimal>> FetchRangeAsync(string ticker, DateTime from, DateTime to, CancellationToken cancellation)
	{
		if (from.Date > to.Date) return new Dictionary<DateTime, decimal>();
		return await _fetchHistoricalClosesAsync(ticker, from.Date, to.Date.AddDays(1), cancellation);
	}

	private static void Merge(Dictionary<DateTime, decimal> target, Dictionary<DateTime, decimal> source)
	{
		foreach (var (date, close) in source)
			target[date.Date] = close;
	}
}
