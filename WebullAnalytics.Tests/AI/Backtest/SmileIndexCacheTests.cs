using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

// Guards the strictly-before semantics: the SMILE value for a date is published at EOD on that date,
// so a 09:30 backtest decision on day T must see T-1's settled value, never T's own (lookahead).
public class SmileIndexCacheTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"wa-smile-test-{Guid.NewGuid():N}.csv");

	public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

	[Fact]
	public async Task GetValueAsync_ReturnsPriorSettledValue_NeverSameDay()
	{
		File.WriteAllText(_path, "date,smile\n2026-06-05,2500\n2026-06-08,2600\n");
		var cache = new SmileIndexCache(_path, offline: true);

		// Monday 2026-06-08: its own value (2600) is EOD-published and not knowable intraday →
		// the most recent prior value is Friday's 2500 (weekend walk-back).
		Assert.Equal(2500m, await cache.GetValueAsync(new DateTime(2026, 6, 8), CancellationToken.None));
		// Tuesday sees Monday's settled value.
		Assert.Equal(2600m, await cache.GetValueAsync(new DateTime(2026, 6, 9), CancellationToken.None));
	}

	[Fact]
	public async Task GetTrailingStatsAsync_UsesOnlySessionsBeforeTheDate_AndNormalizesTheLevel()
	{
		// Ten weekday sessions 2026-06-01..06-12 at 2000, 2010, ... 2090; then 06-15 (Monday) at 9999.
		var lines = new List<string> { "date,smile" };
		var d = new DateTime(2026, 6, 1);
		for (var v = 2000; v <= 2090; d = d.AddDays(1))
		{
			if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
			lines.Add($"{d:yyyy-MM-dd},{v}");
			v += 10;
		}
		lines.Add("2026-06-15,9999");
		File.WriteAllText(_path, string.Join("\n", lines) + "\n");
		var cache = new SmileIndexCache(_path, offline: true);

		// On Monday 06-15 the latest settled value is Friday's 2090; the day's own 9999 must not appear anywhere.
		var (value, change, z) = await cache.GetTrailingStatsAsync(new DateTime(2026, 6, 15), window: 20, CancellationToken.None);
		Assert.Equal(2090m, value);
		Assert.Equal(10m, change);
		// Mean 2045, sample stdev of 2000..2090 step 10 = 30.2765 → (2090 − 2045) / 30.2765 = 1.486
		Assert.NotNull(z);
		Assert.Equal(1.486, (double)z!.Value, 3);

		// The same shape 500 points higher gives the same z: the level drops out.
		File.WriteAllText(_path, string.Join("\n", lines.Select((l, n) => n == 0 ? l : $"{l.Split(',')[0]},{int.Parse(l.Split(',')[1]) + 500}")) + "\n");
		var shifted = await new SmileIndexCache(_path, offline: true).GetTrailingStatsAsync(new DateTime(2026, 6, 15), window: 20, CancellationToken.None);
		Assert.Equal(1.486, (double)shifted.ZScore!.Value, 3);
	}

	[Fact]
	public async Task GetTrailingStatsAsync_FewerThanTenSessions_ReturnsNoZScore()
	{
		File.WriteAllText(_path, "date,smile\n2026-06-04,2500\n2026-06-05,2510\n");
		var (value, change, z) = await new SmileIndexCache(_path, offline: true).GetTrailingStatsAsync(new DateTime(2026, 6, 8), window: 20, CancellationToken.None);
		Assert.Equal(2510m, value);
		Assert.Equal(10m, change);
		Assert.Null(z);
	}
}
