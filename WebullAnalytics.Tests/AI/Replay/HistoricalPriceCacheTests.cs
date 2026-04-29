using WebullAnalytics.AI.Replay;
using Xunit;

namespace WebullAnalytics.Tests.AI.Replay;

public class HistoricalPriceCacheTests
{
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
				});

			var closes = await cache.GetRecentClosesAsync("GME", 4, new DateTime(2026, 4, 24), CancellationToken.None);

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
}
