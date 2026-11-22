using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// The cascade's Gravity & Target Wall factor used hard switches: a few points of spot, or a near-tie between two strikes, moved it 5–9 points
// and a setup across the trigger (2026-11-20 10:00 bull cascade 67 in the backtest vs 64 live; 10:30 bear cascade 62 vs 71). Each switch is now
// a blend, so the target-wall points are continuous in their inputs — and unchanged away from the blend bands.
public class CascadeTargetWallTests
{
	private const decimal Spot = 7100m;
	private const decimal Move = 100m;   // DailyMove 100 > 0.6% of spot

	private static CascadeTerrain Terrain(decimal? frontPutWall, decimal? otmPutWall, decimal? runnerUp = null, decimal runnerUpShare = 0m, decimal? frontCallWall = null, decimal? otmCallWall = null, decimal? callRunnerUp = null, decimal callShare = 0m) => new(
		Front: new SqueezeTerrain(Spot, Trigger: Spot + 30m, ShortGamma: true, CallWall: frontCallWall, PutWall: frontPutWall, DailyMove: Move),
		FrontExpiry: new DateTime(2026, 11, 20),
		Gravity0Dte: null, Centroid0Dte: null, NetPull0Dte: null, FlipMulti: null,
		NetGexFractionMulti: 0m, NetVexMulti: 0m, NetVexFractionMulti: 0m, ExpiryCount: 20,
		OtmCallWall0Dte: otmCallWall, OtmPutWall0Dte: otmPutWall,
		OtmCallRunnerUp0Dte: callRunnerUp, OtmCallRunnerUpShare: callShare,
		OtmPutRunnerUp0Dte: runnerUp, OtmPutRunnerUpShare: runnerUpShare);

	private static decimal BearPoints(CascadeTerrain t) => CascadeScreener.TargetWallPoints(t, bull: false, Move).Points;

	/// <summary>Bull side with the front call wall at the money, a $7,350 OTM call wall and a $7,400 runner-up holding <paramref name="share"/> of its GEX.</summary>
	private static decimal Bull(decimal share) =>
		CascadeScreener.TargetWallPoints(Terrain(null, null, frontCallWall: Spot + 2m, otmCallWall: Spot + 158m, callRunnerUp: Spot + 205m, callShare: share), bull: true, Move).Points;

	[Theory]
	[InlineData(0.10, 0.0)]
	[InlineData(0.15, 0.0)]
	[InlineData(0.50, 10.0)]
	[InlineData(1.50, 10.0)]
	[InlineData(1.75, 7.5)]    // used to be 5 the moment it passed 1.5 EM
	[InlineData(2.00, 5.0)]
	[InlineData(3.00, 5.0)]
	[InlineData(-0.20, 0.0)]   // behind spot
	public void DistancePoints(double moves, double points) =>
		Assert.Equal((decimal)points, CascadeScreener.WallDistancePoints((decimal)moves * Move, Move));

	[Fact]
	public void FrontWallBesideTheOldSwitch_NoLongerJumps()
	{
		// 11-20 10:30: the $7,100 front put wall sat ~0.15 EM under spot and the next wall out ($7,050) 0.68 EM away. A fifth of a point of spot
		// either side of 0.15 EM used to move the factor from 10 to 0; now it moves it by a fifth of a point. The blend is still steep — it spreads
		// those 10 points over 0.10 EM — so three points of spot (live vs backtest that morning) move it ~2.5 points instead of 9.
		var inside = BearPoints(Terrain(frontPutWall: Spot - 14.9m, otmPutWall: Spot - 68m));
		var outside = BearPoints(Terrain(frontPutWall: Spot - 15.1m, otmPutWall: Spot - 68m));
		Assert.InRange(inside - outside, 0m, 0.25m);
		Assert.InRange(BearPoints(Terrain(frontPutWall: Spot - 14.5m, otmPutWall: Spot - 68m)) - BearPoints(Terrain(frontPutWall: Spot - 17.5m, otmPutWall: Spot - 68m)), 2m, 3m);
	}

	[Fact]
	public void OutsideTheBlendBand_TheFrontWallOrTheNextWallScoresAsBefore()
	{
		Assert.Equal(CascadeScreener.WallDistancePoints(30m, Move), BearPoints(Terrain(frontPutWall: Spot - 30m, otmPutWall: Spot - 68m)));   // 0.30 EM: the front wall
		Assert.Equal(CascadeScreener.WallDistancePoints(68m, Move), BearPoints(Terrain(frontPutWall: Spot - 5m, otmPutWall: Spot - 68m)));    // 0.05 EM: the next wall
	}

	[Fact]
	public void NearEqualOtmWalls_AreBlended_UpToAnEvenSplitAtATie()
	{
		// 11-20 10:00: $7,350 and $7,400 call walls traded the lead on the IV source. At a tie they now count half each.
		var leader = CascadeScreener.WallDistancePoints(158m, Move);
		var runner = CascadeScreener.WallDistancePoints(205m, Move);
		Assert.Equal(leader, Bull(0.79m));                        // a clear leader scores alone, as before
		Assert.Equal((leader + runner) / 2m, Bull(1.00m));        // a tie: an even split, the same whichever of the two leads
		Assert.InRange(Bull(0.90m), Math.Min(leader, runner), Math.Max(leader, runner));
	}
}
