using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Guards the historical risk-free rate series: ^IRX percentage-point closes land on disk as fractions,
// offline reads never refetch, a transient empty fetch never clobbers a good cached file, and RateOn
// serves the last close STRICTLY BEFORE the asked day (at 09:30 only the prior session's close is
// knowable — same causality rule as the OI-snapshot cache).
public class HistoricalRateCacheTests : IDisposable
{
	private readonly string _dir = Directory.CreateTempSubdirectory("wa-rate-cache-test-").FullName;

	public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

	private string CsvPath => Path.Combine(_dir, "IRX.csv");

	private static Task<IReadOnlyList<(DateTime Date, decimal Close)>> Fetch(params (DateTime Date, decimal Close)[] rows)
		=> Task.FromResult<IReadOnlyList<(DateTime, decimal)>>(rows);

	[Fact]
	public async Task OnlineFetch_ConvertsPercentPointsToFractions_AndPersists()
	{
		var cache = new HistoricalRateCache(_dir, c => Fetch((new DateTime(2026, 8, 6), 4.21m), (new DateTime(2026, 8, 7), 4.18m)));
		var rates = await cache.GetAsync(CancellationToken.None);

		Assert.Equal(2, rates.Count);
		Assert.Equal(0.0421, rates[0].Rate, precision: 6);
		Assert.Equal(0.0418, rates[1].Rate, precision: 6);
		Assert.Contains("2026-08-07,0.0418", await File.ReadAllTextAsync(CsvPath));
	}

	[Fact]
	public async Task Offline_ReadsDiskOnly_NeverFetches()
	{
		await File.WriteAllTextAsync(CsvPath, "date,rate\n2026-08-06,0.0421\n");
		var cache = new HistoricalRateCache(_dir, c => throw new InvalidOperationException("offline must not fetch"), offline: true);
		var rates = await cache.GetAsync(CancellationToken.None);

		Assert.Single(rates);
		Assert.Equal(new DateTime(2026, 8, 6), rates[0].Date);
	}

	[Fact]
	public async Task EmptyFetch_KeepsGoodCachedFile()
	{
		await File.WriteAllTextAsync(CsvPath, "date,rate\n2026-08-06,0.0421\n");
		var cache = new HistoricalRateCache(_dir, c => Fetch());
		var rates = await cache.GetAsync(CancellationToken.None);

		Assert.Single(rates);
		Assert.Contains("2026-08-06,0.0421", await File.ReadAllTextAsync(CsvPath));
	}

	[Fact]
	public void RateOn_ServesLastCloseStrictlyBeforeTheDay()
	{
		var series = new List<(DateTime Date, double Rate)>
		{
			(new DateTime(2026, 8, 5), 0.0421),
			(new DateTime(2026, 8, 6), 0.0418),
		};

		Assert.Null(HistoricalRateCache.RateOn(series, new DateTime(2026, 8, 5)));           // nothing knowable before the first close
		Assert.Equal(0.0421, HistoricalRateCache.RateOn(series, new DateTime(2026, 8, 6))); // day T sees T-1's close, not its own
		Assert.Equal(0.0418, HistoricalRateCache.RateOn(series, new DateTime(2026, 8, 7)));
		Assert.Equal(0.0418, HistoricalRateCache.RateOn(series, new DateTime(2026, 8, 10))); // weekend gap walks back to Friday's close
	}
}
