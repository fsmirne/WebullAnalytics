using WebullAnalytics;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

/// <summary>Disk/offline behaviour of the backtest's historical dividend cache. Network is always
/// injected, so these run hermetically.</summary>
public class HistoricalDividendCacheTests : IDisposable
{
	private readonly string _tmpDir;

	public HistoricalDividendCacheTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-divcache-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	private static IReadOnlyList<DividendEvent> Schedule(params (int Y, int M, int D, decimal Amt)[] divs) =>
		divs.Select(d => new DividendEvent(new DateTime(d.Y, d.M, d.D), d.Amt)).ToList();

	[Fact]
	public async Task Online_FetchesWritesAndRoundTrips()
	{
		var fetched = Schedule((2025, 6, 20, 1.76m), (2025, 3, 17, 1.79m)); // deliberately out of order
		var cache = new HistoricalDividendCache(_tmpDir, (_, _) => Task.FromResult(fetched), offline: false);

		var divs = await cache.GetAsync("SPY", CancellationToken.None);

		// Written to disk, and returned sorted oldest-first.
		Assert.True(File.Exists(Path.Combine(_tmpDir, "SPY.csv")));
		Assert.Equal(2, divs.Count);
		Assert.Equal(new DateTime(2025, 3, 17), divs[0].ExDate);
		Assert.Equal(new DateTime(2025, 6, 20), divs[1].ExDate);

		// A fresh OFFLINE cache reads the same file back identically.
		var offline = new HistoricalDividendCache(_tmpDir, (_, _) => throw new Exception("must not fetch offline"), offline: true);
		var reread = await offline.GetAsync("SPY", CancellationToken.None);
		Assert.Equal(2, reread.Count);
		Assert.Equal(1.79m, reread[0].Amount);
		Assert.Equal(1.76m, reread[1].Amount);
	}

	[Fact]
	public async Task Offline_MissingFile_ReturnsEmpty_NeverFetches()
	{
		var cache = new HistoricalDividendCache(_tmpDir, (_, _) => throw new Exception("must not fetch offline"), offline: true);
		var divs = await cache.GetAsync("SPXW", CancellationToken.None); // cash-settled index → no file
		Assert.Empty(divs);
	}

	[Fact]
	public async Task Online_TransientEmptyFetch_DoesNotClobberGoodFile()
	{
		// Seed a good file via one online fetch...
		var good = new HistoricalDividendCache(_tmpDir, (_, _) => Task.FromResult(Schedule((2025, 6, 20, 1.76m))), offline: false);
		Assert.Single(await good.GetAsync("SPY", CancellationToken.None));

		// ...then a fresh cache whose fetch returns empty (transient Yahoo failure) must keep the old data.
		var transient = new HistoricalDividendCache(_tmpDir, (_, _) => Task.FromResult<IReadOnlyList<DividendEvent>>(Array.Empty<DividendEvent>()), offline: false);
		var divs = await transient.GetAsync("SPY", CancellationToken.None);
		Assert.Single(divs);
		Assert.Equal(1.76m, divs[0].Amount);
	}

	[Fact]
	public async Task BuildScheduleMap_OmitsRootsWithNoDividends()
	{
		var cache = new HistoricalDividendCache(_tmpDir,
			(t, _) => Task.FromResult(t == "SPY" ? Schedule((2025, 6, 20, 1.76m)) : (IReadOnlyList<DividendEvent>)Array.Empty<DividendEvent>()),
			offline: false);

		var map = await cache.BuildScheduleMapAsync(new[] { "SPY", "SPXW" }, CancellationToken.None);

		Assert.True(map.ContainsKey("SPY"));
		Assert.False(map.ContainsKey("SPXW")); // index root → absent → pricer leaves it unadjusted
	}
}
