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
}
