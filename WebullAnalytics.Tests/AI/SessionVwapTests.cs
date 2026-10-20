using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class SessionVwapTests
{
	private static MinuteBar Bar(decimal price, long volume) => new(DateTimeOffset.UnixEpoch, price, price, price, price, volume);

	[Fact]
	public void Empty_IsZero() => Assert.Equal(0m, new SessionVwap().Value);

	[Fact]
	public void EveryBarHasVolume_IsVolumeWeighted()
	{
		var v = new SessionVwap();
		v.Add(Bar(100m, 1));
		v.Add(Bar(110m, 3));
		Assert.Equal(107.5m, v.Value);
	}

	[Fact]
	public void NoVolume_IsTwap()
	{
		var v = new SessionVwap();
		v.Add(Bar(100m, 0));
		v.Add(Bar(110m, 0));
		Assert.Equal(105m, v.Value);
	}

	[Fact]
	public void StrayVolumeBar_DoesNotHijackTheAverage()
	{
		// The SPX cash-index artifact: zero-volume bars plus one stray bar whose "volume" is its price. The old
		// code weighted by volume once any bar had some, snapping the average to that single bar (7700).
		var v = new SessionVwap();
		v.Add(Bar(7710m, 0));
		v.Add(Bar(7712m, 0));
		v.Add(Bar(7700m, 7700));
		Assert.Equal((7710m + 7712m + 7700m) / 3m, v.Value);
	}

	[Fact]
	public void VolumeLostMidSession_FallsBackToTwapFromThenOn()
	{
		var v = new SessionVwap();
		v.Add(Bar(100m, 10));
		v.Add(Bar(110m, 30));
		Assert.Equal(107.5m, v.Value);
		v.Add(Bar(120m, 0));
		Assert.Equal(110m, v.Value);
	}
}
