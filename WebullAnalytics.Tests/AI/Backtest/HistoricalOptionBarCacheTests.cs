using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests.AI.Backtest;

public class HistoricalOptionBarCacheTests : IDisposable
{
	private readonly string _tmpDir;

	public HistoricalOptionBarCacheTests()
	{
		_tmpDir = Path.Combine(Path.GetTempPath(), "wa-opt-cache-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tmpDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tmpDir, recursive: true); } catch { }
	}

	[Fact]
	public void GetBar_ContractMissing_ReturnsNull()
	{
		var cache = new HistoricalOptionBarCache(_tmpDir);
		Assert.Null(cache.GetBar("SPXW260526C07530000", DateTimeOffset.UtcNow));
	}

	[Fact]
	public void GetBar_BarPresent_ReturnsBar()
	{
		var occ = "SPXW260526C07530000";
		var path = SeedCsv(occ, new List<OptionMinuteBar>
		{
			// 09:30 ET on 2026-05-26 = 13:30 UTC = 1779802200
			new(DateTimeOffset.FromUnixTimeSeconds(1779802200), 100m, 105m, 99m, 102m, 50, 15.5m),
		});

		var cache = new HistoricalOptionBarCache(_tmpDir);
		var bar = cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1779802200));
		Assert.NotNull(bar);
		Assert.Equal(102m, bar!.Close);
		Assert.Equal(15.5m, bar.ImpliedVolatility);
		Assert.True(File.Exists(path)); // sanity
	}

	[Fact]
	public void GetBar_MinuteOutsideWindow_ReturnsNull()
	{
		// Single bar at t=0. Ask for t=600 (10 minutes later) — beyond the 5-minute walk → null.
		var occ = "SPXW260526C07530000";
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1000), 100m, 100m, 100m, 100m, 1, null),
		});
		var cache = new HistoricalOptionBarCache(_tmpDir);
		var bar = cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1600));
		Assert.Null(bar);
	}

	[Fact]
	public void GetBar_AtOrAfterWalk_FindsNextMinute()
	{
		// Webull's first RTH bar is typically 09:31 ET, not 09:30 ET. Look up 09:30 (no bar) →
		// the walk should find 09:31's bar. Same when the first bar is 09:32, 09:33, etc.
		var occ = "SPXW260526C07530000";
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			// Bar one minute after target.
			new(DateTimeOffset.FromUnixTimeSeconds(1779802260), 50m, 50m, 50m, 50m, 100, 15m),
		});
		var cache = new HistoricalOptionBarCache(_tmpDir);
		// Target 09:30:00 UTC equivalent (one minute before the bar).
		var bar = cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1779802200));
		Assert.NotNull(bar);
		Assert.Equal(50m, bar!.Close);
	}

	[Fact]
	public void GetBar_AtOrAfterWalk_DoesNotLookBackward()
	{
		// Bar at t=1000. Look up t=1100 (100 sec later) and t=900 (100 sec earlier).
		// Forward lookup at t=1100 should still find t=1000+? No — the walk only goes forward,
		// so a bar 100 sec EARLIER than the target should be missed. This ensures we don't pick
		// up stale pre-market prints when the daily-step asks "what was the open mid?".
		var occ = "SPXW260526C07530000";
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1000), 100m, 100m, 100m, 100m, 1, null),
		});
		var cache = new HistoricalOptionBarCache(_tmpDir);
		// Looking up t=1100: walk forward from 1100 to 1100+300 (max). t=1000 is behind → miss.
		Assert.Null(cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1100)));
	}

	[Fact]
	public void GetBar_SubMinuteTimestamp_RoundsDownToMinute()
	{
		var occ = "SPXW260526C07530000";
		SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1779802200), 100m, 100m, 100m, 100m, 1, null),
		});
		var cache = new HistoricalOptionBarCache(_tmpDir);
		// 35 seconds into the minute — should still hit the bar.
		var bar = cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1779802235));
		Assert.NotNull(bar);
	}

	[Fact]
	public void GetBar_MalformedOcc_ReturnsNull()
	{
		var cache = new HistoricalOptionBarCache(_tmpDir);
		Assert.Null(cache.GetBar("NOT_AN_OCC_SYMBOL", DateTimeOffset.UtcNow));
	}

	[Fact]
	public void GetBar_SecondReadIsCached_NoDiskHit()
	{
		var occ = "SPXW260526C07530000";
		var path = SeedCsv(occ, new List<OptionMinuteBar>
		{
			new(DateTimeOffset.FromUnixTimeSeconds(1779802200), 100m, 100m, 100m, 100m, 1, null),
		});
		var cache = new HistoricalOptionBarCache(_tmpDir);
		Assert.NotNull(cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1779802200)));

		// Delete the file — cache should still serve from memory.
		File.Delete(path);
		Assert.NotNull(cache.GetBar(occ, DateTimeOffset.FromUnixTimeSeconds(1779802200)));
	}

	private string SeedCsv(string occ, List<OptionMinuteBar> bars)
	{
		var parsed = ParsingHelpers.ParseOptionSymbol(occ);
		Assert.NotNull(parsed);
		var expiryDir = parsed!.ExpiryDate.ToString("yyyy-MM-dd");
		var path = Path.Combine(_tmpDir, parsed.Root.ToUpperInvariant(), expiryDir, occ + ".csv");
		AIHistoryOptionsBackfill.WriteOptionCsv(path, bars);
		return path;
	}
}
