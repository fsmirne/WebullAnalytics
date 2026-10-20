using WebullAnalytics.AI.Sources;
using Xunit;

namespace WebullAnalytics.Tests.AI.Sources;

public class SpxFamilyTapeTests
{
	private static readonly DateTimeOffset T0 = new(2026, 10, 19, 13, 30, 0, TimeSpan.Zero); // 09:30 ET

	private static MinuteBar Bar(int minute, decimal price, long volume) => new(T0.AddMinutes(minute), price, price, price, price, volume);

	[Fact]
	public void EveryEmittedBarHasZeroVolume()
	{
		// The 2026-10-19 artifact: an SPX bar whose "volume" is its price, plus SPY bars carrying real share volume.
		var spx = new[] { Bar(0, 7690m, 0), Bar(1, 7700m, 7700) };
		var spy = new[] { Bar(-1, 767m, 50_000), Bar(1, 770m, 80_000) };

		var merged = SpxFamilyTape.Merge(spx, spy);

		Assert.Equal(3, merged.Count);
		Assert.All(merged, b => Assert.Equal(0, b.Volume));
	}

	[Fact]
	public void SpxWinsOverlap_SpyScaledByMostRecentOverlapRatio()
	{
		var spx = new[] { Bar(0, 7690m, 0), Bar(1, 7700m, 0) };
		var spy = new[] { Bar(-1, 766m, 1), Bar(0, 769m, 1), Bar(1, 770m, 1) };

		var merged = SpxFamilyTape.Merge(spx, spy);

		Assert.Equal(new[] { T0.AddMinutes(-1), T0, T0.AddMinutes(1) }, merged.Select(b => b.Timestamp));
		Assert.Equal(7690m, merged[1].Close);                    // SPX wins its own minute
		Assert.Equal(Math.Round(766m * (7700m / 770m), 2), merged[0].Close); // ratio from the newest overlap (minute 1)
	}

	[Fact]
	public void NoSpx_IsEmpty() => Assert.Empty(SpxFamilyTape.Merge(Array.Empty<MinuteBar>(), new[] { Bar(0, 770m, 1) }));

	[Fact]
	public void NoOverlap_IsSpxOnly_VolumeStillZeroed()
	{
		var merged = SpxFamilyTape.Merge(new[] { Bar(0, 7700m, 7700) }, new[] { Bar(-30, 766m, 1) });
		Assert.Single(merged);
		Assert.Equal(0, merged[0].Volume);
	}
}
