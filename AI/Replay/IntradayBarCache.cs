using System.Globalization;
using System.Text;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Replay;

/// <summary>Fetcher delegate decoupling the cache from the underlying transport. Production wires
/// this to <see cref="WebullChartsClient.FetchIntradayBarsAsync"/> with a tickerId resolver; tests
/// substitute an in-memory fake.</summary>
internal delegate Task<IReadOnlyList<MinuteBar>> IntradayBarFetcher(string ticker, BarInterval interval, int count, bool includeExtended, CancellationToken cancellation);

/// <summary>Disk-backed cache of intraday OHLCV bars at <c>data/intraday/&lt;TICKER&gt;/&lt;yyyy-mm-dd&gt;.csv</c>.
/// One file per NY-local session date. Today's file grows during the session; past-day files are
/// sealed (never refetched once on disk). The fetcher is called only when the in-memory snapshot for
/// the requested date is stale (current bar older than <see cref="_freshnessThreshold"/>) or absent
/// entirely. Webull's m1 endpoint only serves ~5 trading days, so historical backfill beyond that
/// window is a no-op.
///
/// Bars within a single fetch can span multiple NY dates — the cache splits the result by date and
/// opportunistically writes every covered day, so a cold-start fetch at 11am ET also seals
/// yesterday's file in one round trip.</summary>
internal sealed class IntradayBarCache
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private const int ColdStartCount = 500;
	private const int WarmGapCount = 30;

	private readonly string _cacheDir;
	private readonly IntradayBarFetcher _fetcher;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly TimeSpan _freshnessThreshold;
	private readonly Dictionary<(string Ticker, DateTime NyDate), IReadOnlyList<MinuteBar>> _memory = new();
	private readonly object _memoryLock = new();

	public IntradayBarCache(IntradayBarFetcher fetcher, string? cacheDir = null, TimeSpan? freshnessThreshold = null, Func<DateTimeOffset>? utcNow = null)
	{
		_fetcher = fetcher;
		_cacheDir = cacheDir ?? Program.ResolvePath("data/intraday");
		_freshnessThreshold = freshnessThreshold ?? TimeSpan.FromSeconds(70);
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		Directory.CreateDirectory(_cacheDir);
	}

	/// <summary>Returns bars in <c>[fromUtc, toUtc]</c> for the requested ticker, loading from disk and
	/// invoking the fetcher only when necessary. Result is oldest-first. Empty when no bars exist for
	/// the range (e.g. weekend, holiday, pre-open with cash index ticker).</summary>
	public async Task<IReadOnlyList<MinuteBar>> GetBarsAsync(string ticker, DateTimeOffset fromUtc, DateTimeOffset toUtc, BarInterval interval, bool includeExtended, CancellationToken cancellation)
	{
		if (fromUtc > toUtc) return Array.Empty<MinuteBar>();

		var result = new List<MinuteBar>();
		foreach (var nyDate in EnumerateNyDates(fromUtc, toUtc))
		{
			var bars = await LoadOrFetchAsync(ticker, nyDate, interval, includeExtended, cancellation);
			foreach (var b in bars)
			{
				if (b.Timestamp >= fromUtc && b.Timestamp <= toUtc) result.Add(b);
			}
		}
		return result;
	}

	private async Task<IReadOnlyList<MinuteBar>> LoadOrFetchAsync(string ticker, DateTime nyDate, BarInterval interval, bool includeExtended, CancellationToken cancellation)
	{
		var key = (ticker, nyDate);
		IReadOnlyList<MinuteBar>? cached;
		lock (_memoryLock) { _memory.TryGetValue(key, out cached); }

		// In-memory hit and fresh for the day: serve as-is.
		if (cached != null && IsFresh(cached, nyDate)) return cached;

		// Fall back to disk if memory missed.
		var bars = cached ?? ReadDiskFile(ticker, nyDate);

		// Decide whether to fetch:
		//   - today's date: refetch when stale (cached bars older than freshness threshold)
		//   - past date with no file: best-effort backfill within Webull's ~5-day m1 window
		//   - past date with file present: sealed, never refetched
		//   - future date: skip (data doesn't exist)
		var todayNy = TodayNyDate();
		var needFetch =
			(nyDate == todayNy && !IsFresh(bars, nyDate))
			|| (nyDate < todayNy && bars.Count == 0 && nyDate >= todayNy.AddDays(-5));

		if (needFetch)
		{
			var count = bars.Count == 0 ? ColdStartCount : WarmGapCount;
			IReadOnlyList<MinuteBar> fetched;
			try
			{
				fetched = await _fetcher(ticker, interval, count, includeExtended, cancellation);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Console.WriteLine($"Intraday cache: fetcher threw for {ticker} {nyDate:yyyy-MM-dd}: {ex.Message}");
				fetched = Array.Empty<MinuteBar>();
			}

			if (fetched.Count > 0)
			{
				// Webull returns bars that may span multiple NY dates. Group + write each date so a
				// single cold-start round trip seals yesterday's file too.
				var byDate = fetched.GroupBy(b => NyDateOf(b.Timestamp));
				foreach (var group in byDate)
				{
					var existing = group.Key == nyDate ? bars : (ReadFromMemoryOrDisk(ticker, group.Key));
					var merged = Merge(existing, group.ToList());
					WriteDiskFile(ticker, group.Key, merged);
					lock (_memoryLock) { _memory[(ticker, group.Key)] = merged; }
					if (group.Key == nyDate) bars = merged;
				}
			}
		}

		lock (_memoryLock) { _memory[key] = bars; }
		return bars;
	}

	private IReadOnlyList<MinuteBar> ReadFromMemoryOrDisk(string ticker, DateTime nyDate)
	{
		lock (_memoryLock)
		{
			if (_memory.TryGetValue((ticker, nyDate), out var m)) return m;
		}
		return ReadDiskFile(ticker, nyDate);
	}

	private bool IsFresh(IReadOnlyList<MinuteBar> bars, DateTime nyDate)
	{
		if (bars.Count == 0) return false;
		var todayNy = TodayNyDate();
		// Past days are immutable; any data is fresh.
		if (nyDate < todayNy) return true;
		// Future days: nothing can be fresh.
		if (nyDate > todayNy) return true;
		// Today: last bar must be within the freshness window.
		var lastBar = bars[^1];
		return _utcNow() - lastBar.Timestamp < _freshnessThreshold;
	}

	private DateTime TodayNyDate() => TimeZoneInfo.ConvertTime(_utcNow(), NyTz).Date;

	private static DateTime NyDateOf(DateTimeOffset ts) => TimeZoneInfo.ConvertTime(ts, NyTz).Date;

	private static IEnumerable<DateTime> EnumerateNyDates(DateTimeOffset fromUtc, DateTimeOffset toUtc)
	{
		var from = NyDateOf(fromUtc);
		var to = NyDateOf(toUtc);
		for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
	}

	internal static IReadOnlyList<MinuteBar> Merge(IReadOnlyList<MinuteBar> existing, IReadOnlyList<MinuteBar> incoming)
	{
		// Incoming wins on conflict — handles the case where the last bar of the previous fetch was
		// the in-progress minute, now finalized in the new fetch.
		var map = new SortedDictionary<long, MinuteBar>();
		foreach (var b in existing) map[b.Timestamp.ToUnixTimeSeconds()] = b;
		foreach (var b in incoming) map[b.Timestamp.ToUnixTimeSeconds()] = b;
		return map.Values.ToList();
	}

	private string FilePath(string ticker, DateTime nyDate) =>
		Path.Combine(_cacheDir, ticker.ToUpperInvariant(), nyDate.ToString("yyyy-MM-dd") + ".csv");

	private IReadOnlyList<MinuteBar> ReadDiskFile(string ticker, DateTime nyDate)
	{
		var path = FilePath(ticker, nyDate);
		if (!File.Exists(path)) return Array.Empty<MinuteBar>();
		var bars = new List<MinuteBar>();
		foreach (var line in File.ReadAllLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			var parts = line.Split(',');
			if (parts.Length < 6) continue;
			if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
			if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume);
			bars.Add(new MinuteBar(ts, open, high, low, close, volume));
		}
		bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return bars;
	}

	private void WriteDiskFile(string ticker, DateTime nyDate, IReadOnlyList<MinuteBar> bars)
	{
		var path = FilePath(ticker, nyDate);
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

		// Atomic write: temp + replace. Protects sealed historical files from a crash mid-write.
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, sb.ToString());
		File.Move(tmp, path, overwrite: true);
	}
}
