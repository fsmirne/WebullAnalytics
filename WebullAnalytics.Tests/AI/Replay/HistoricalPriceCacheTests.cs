using WebullAnalytics.AI.Replay;
using Xunit;

namespace WebullAnalytics.Tests.AI.Replay;

public class HistoricalPriceCacheTests
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	[Fact]
	public async Task ExistingCsvIsRefreshedWhenAsOfIsNewerThanLastCachedDate()
	{
		var cacheDir = Path.Combine(Path.GetTempPath(), $"HistoricalPriceCacheTests_{Guid.NewGuid():N}");
		Directory.CreateDirectory(cacheDir);
		try
		{
			var path = Path.Combine(cacheDir, "GME.csv");
			await File.WriteAllTextAsync(path, "date,close\n2026-04-21,24.10\n2026-04-22,24.30\n");
			var calls = new List<(DateTime from, DateTime to)>();
			var cache = new HistoricalPriceCache(
				cacheDir,
				(ticker, from, to, cancellation) =>
				{
					calls.Add((from, to));
					return Task.FromResult(new Dictionary<DateTime, decimal>
					{
						[new DateTime(2026, 4, 23)] = 24.55m,
						[new DateTime(2026, 4, 24)] = 25.10m,
					});
				},
				utcNow: () => NyMidnight(2026, 4, 25));

			// asOf 04-25: cache now applies a strict-less-than filter to prevent backtest lookahead,
			// so callers asking "as of day X" get closes from days strictly before X. To assert that
			// 04-24's close is returned we must pass 04-25 as the as-of date.
			var closes = await cache.GetRecentClosesAsync("GME", 4, new DateTime(2026, 4, 25), CancellationToken.None);

			Assert.Equal(new decimal[] { 24.10m, 24.30m, 24.55m, 25.10m }, closes);
			Assert.Single(calls);
			Assert.Equal(new DateTime(2026, 4, 23), calls[0].from);
			Assert.Equal(new DateTime(2026, 4, 25), calls[0].to);

			var persisted = await File.ReadAllTextAsync(path);
			Assert.Contains("2026-04-23,24.55", persisted);
			Assert.Contains("2026-04-24,25.10", persisted);
		}
		finally
		{
			Directory.Delete(cacheDir, recursive: true);
		}
	}

	[Fact]
	public async Task TodayIsNotFetchedOrPersistedBeforeFivePmNyCutoff()
	{
		var cacheDir = Path.Combine(Path.GetTempPath(), $"HistoricalPriceCacheTests_{Guid.NewGuid():N}");
		Directory.CreateDirectory(cacheDir);
		try
		{
			var path = Path.Combine(cacheDir, "GME.csv");
			await File.WriteAllTextAsync(path, "date,close\n2026-05-04,23.84\n2026-05-05,24.23\n");
			var calls = new List<(DateTime from, DateTime to)>();
			var cache = new HistoricalPriceCache(
				cacheDir,
				(ticker, from, to, cancellation) =>
				{
					calls.Add((from, to));
					return Task.FromResult(new Dictionary<DateTime, decimal>
					{
						[new DateTime(2026, 5, 6)] = 24.30m,
					});
				},
				utcNow: () => NyDateTimeToUtc(2026, 5, 6, 9, 45));

			var close = await cache.GetCloseAsync("GME", new DateTime(2026, 5, 6), CancellationToken.None);

			Assert.Null(close);
			Assert.Empty(calls);
			var persisted = await File.ReadAllTextAsync(path);
			Assert.DoesNotContain("2026-05-06", persisted);
		}
		finally
		{
			Directory.Delete(cacheDir, recursive: true);
		}
	}

	[Fact]
	public async Task TodayIsFetchedAndPersistedAtOrAfterFivePmNyCutoff()
	{
		var cacheDir = Path.Combine(Path.GetTempPath(), $"HistoricalPriceCacheTests_{Guid.NewGuid():N}");
		Directory.CreateDirectory(cacheDir);
		try
		{
			var path = Path.Combine(cacheDir, "GME.csv");
			await File.WriteAllTextAsync(path, "date,close\n2026-05-04,23.84\n2026-05-05,24.23\n");
			var calls = new List<(DateTime from, DateTime to)>();
			var cache = new HistoricalPriceCache(
				cacheDir,
				(ticker, from, to, cancellation) =>
				{
					calls.Add((from, to));
					return Task.FromResult(new Dictionary<DateTime, decimal>
					{
						[new DateTime(2026, 5, 6)] = 25.17m,
					});
				},
				utcNow: () => NyDateTimeToUtc(2026, 5, 6, 17, 0));

			var close = await cache.GetCloseAsync("GME", new DateTime(2026, 5, 6), CancellationToken.None);

			Assert.Equal(25.17m, close);
			Assert.Single(calls);
			Assert.Equal(new DateTime(2026, 5, 6), calls[0].from);
			Assert.Equal(new DateTime(2026, 5, 7), calls[0].to);
			var persisted = await File.ReadAllTextAsync(path);
			Assert.Contains("2026-05-06,25.17", persisted);
		}
		finally
		{
			Directory.Delete(cacheDir, recursive: true);
		}
	}

	private static DateTime NyMidnight(int year, int month, int day) => NyDateTimeToUtc(year, month, day, 0, 0);

	private static DateTime NyDateTimeToUtc(int year, int month, int day, int hour, int minute)
	{
		var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
		return TimeZoneInfo.ConvertTimeToUtc(local, NyTz);
	}
}
