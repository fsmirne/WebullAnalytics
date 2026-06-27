using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Replay;

public class IntradayBarCacheTests
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	// 2026-05-18 is a Monday. Use 14:30 UTC = 10:30 ET for the test "now".
	private static DateTimeOffset NyTime(int hour, int minute) =>
		new DateTimeOffset(new DateTime(2026, 5, 18, hour, minute, 0, DateTimeKind.Unspecified), NyTz.GetUtcOffset(new DateTime(2026, 5, 18, hour, minute, 0)));

	private static MinuteBar Bar(DateTimeOffset ts, decimal close = 100m, long volume = 100) =>
		new MinuteBar(ts.ToUniversalTime(), close, close + 0.5m, close - 0.5m, close, volume);

	[Fact]
	public async Task ColdStart_FetchesAndWritesDiskFile()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"IntradayCacheTests_{Guid.NewGuid():N}");
		try
		{
			var now = NyTime(10, 30);
			var calls = 0;
			var fetcher = new IntradayBarFetcher((_, _, _, _, _) =>
			{
				calls++;
				return Task.FromResult<IReadOnlyList<MinuteBar>>(new[]
				{
					Bar(NyTime(9, 30)),
					Bar(NyTime(9, 31)),
					Bar(NyTime(10, 29)),
				});
			});
			var cache = new IntradayBarCache(fetcher, dir, freshnessThreshold: TimeSpan.FromSeconds(70), utcNow: () => now);

			var bars = await cache.GetBarsAsync("SPX", NyTime(9, 30), NyTime(10, 30), BarInterval.M1, includeExtended: false, CancellationToken.None);

			Assert.Equal(3, bars.Count);
			Assert.Equal(1, calls);
			var filePath = Path.Combine(dir, "SPX", "2026-05-18.csv");
			Assert.True(File.Exists(filePath));
			var lines = await File.ReadAllLinesAsync(filePath, TestContext.Current.CancellationToken);
			Assert.Equal("timestamp_utc,open,high,low,close,volume", lines[0]);
			Assert.Equal(4, lines.Length); // header + 3 bars
		}
		finally
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public async Task WarmRead_WithinFreshness_DoesNotRefetch()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"IntradayCacheTests_{Guid.NewGuid():N}");
		try
		{
			var now = NyTime(10, 30);
			var calls = 0;
			var fetcher = new IntradayBarFetcher((_, _, _, _, _) =>
			{
				calls++;
				return Task.FromResult<IReadOnlyList<MinuteBar>>(new[]
				{
					Bar(NyTime(9, 30)),
					Bar(now.AddSeconds(-30)), // bar within freshness window
				});
			});
			var cache = new IntradayBarCache(fetcher, dir, freshnessThreshold: TimeSpan.FromSeconds(70), utcNow: () => now);

			// First call: cold start, fetcher runs once.
			await cache.GetBarsAsync("SPX", NyTime(9, 30), now, BarInterval.M1, false, CancellationToken.None);
			Assert.Equal(1, calls);

			// Second call same moment: in-memory hit, fresh, no fetch.
			await cache.GetBarsAsync("SPX", NyTime(9, 30), now, BarInterval.M1, false, CancellationToken.None);
			Assert.Equal(1, calls);
		}
		finally
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public async Task PastDayWithExistingFile_NeverRefetched()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"IntradayCacheTests_{Guid.NewGuid():N}");
		try
		{
			// Pre-seed yesterday's file.
			Directory.CreateDirectory(Path.Combine(dir, "SPX"));
			var yesterdayPath = Path.Combine(dir, "SPX", "2026-05-15.csv");
			await File.WriteAllTextAsync(yesterdayPath, "timestamp_utc,open,high,low,close,volume\n2026-05-15T13:30:00Z,5125.00,5125.50,5124.50,5125.25,1000\n", TestContext.Current.CancellationToken);

			var calls = 0;
			var fetcher = new IntradayBarFetcher((_, _, _, _, _) => { calls++; return Task.FromResult<IReadOnlyList<MinuteBar>>(Array.Empty<MinuteBar>()); });
			var cache = new IntradayBarCache(fetcher, dir, utcNow: () => NyTime(10, 30));

			// Both bounds within Friday's NY-local session date so the cache only enumerates one date.
			var fridayOpen = new DateTimeOffset(2026, 5, 15, 9, 30, 0, NyTz.GetUtcOffset(new DateTime(2026, 5, 15)));
			var fridayClose = new DateTimeOffset(2026, 5, 15, 16, 0, 0, NyTz.GetUtcOffset(new DateTime(2026, 5, 15)));
			var bars = await cache.GetBarsAsync("SPX", fridayOpen, fridayClose, BarInterval.M1, false, CancellationToken.None);

			Assert.Single(bars);
			Assert.Equal(0, calls);
		}
		finally
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public async Task CrossDayFetch_SplitsByNyDateAndSealsHistorical()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"IntradayCacheTests_{Guid.NewGuid():N}");
		try
		{
			var now = NyTime(10, 30); // Monday 2026-05-18 10:30 ET
			var fridayBar = Bar(new DateTimeOffset(2026, 5, 15, 19, 59, 0, TimeSpan.Zero)); // Friday 15:59 ET
			var mondayBar = Bar(NyTime(9, 31));
			var fetcher = new IntradayBarFetcher((_, _, _, _, _) =>
				Task.FromResult<IReadOnlyList<MinuteBar>>(new[] { fridayBar, mondayBar }));
			var cache = new IntradayBarCache(fetcher, dir, utcNow: () => now);

			await cache.GetBarsAsync("SPX", NyTime(9, 30), now, BarInterval.M1, false, CancellationToken.None);

			var fridayPath = Path.Combine(dir, "SPX", "2026-05-15.csv");
			var mondayPath = Path.Combine(dir, "SPX", "2026-05-18.csv");
			Assert.True(File.Exists(fridayPath));
			Assert.True(File.Exists(mondayPath));
		}
		finally
		{
			if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Merge_IncomingWinsOnConflict()
	{
		var ts = new DateTimeOffset(2026, 5, 18, 13, 30, 0, TimeSpan.Zero);
		var existing = new[] { new MinuteBar(ts, 100m, 100.5m, 99.5m, 100.25m, 50) };
		var incoming = new[] { new MinuteBar(ts, 100m, 101m, 99.5m, 100.75m, 200) };

		var merged = IntradayBarCache.Merge(existing, incoming);

		Assert.Single(merged);
		Assert.Equal(200, merged[0].Volume);
		Assert.Equal(100.75m, merged[0].Close);
	}

	[Fact]
	public void Merge_DisjointInputs_UnionedAndSorted()
	{
		var b1 = new MinuteBar(new DateTimeOffset(2026, 5, 18, 13, 31, 0, TimeSpan.Zero), 100m, 100.5m, 99.5m, 100.25m, 50);
		var b2 = new MinuteBar(new DateTimeOffset(2026, 5, 18, 13, 30, 0, TimeSpan.Zero), 100m, 100.5m, 99.5m, 100.25m, 50);
		var b3 = new MinuteBar(new DateTimeOffset(2026, 5, 18, 13, 32, 0, TimeSpan.Zero), 100m, 100.5m, 99.5m, 100.25m, 50);

		var merged = IntradayBarCache.Merge(new[] { b1 }, new[] { b2, b3 });

		Assert.Equal(3, merged.Count);
		Assert.Equal(b2.Timestamp, merged[0].Timestamp);
		Assert.Equal(b1.Timestamp, merged[1].Timestamp);
		Assert.Equal(b3.Timestamp, merged[2].Timestamp);
	}
}
