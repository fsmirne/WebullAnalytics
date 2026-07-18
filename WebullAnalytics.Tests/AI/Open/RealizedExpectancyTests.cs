using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class RealizedExpectancyTests
{
	private static OpenerRealizedExpectancyConfig Cfg(
		bool enabled = true,
		decimal stopLossPct = 0.50m,
		decimal slippagePerSharePerOrder = 0m,
		int roundTrips = 2) =>
		new()
		{
			Enabled = enabled,
			StopLossPctOfMaxLoss = stopLossPct,
			SlippagePerSharePerOrder = slippagePerSharePerOrder,
			RoundTrips = roundTrips,
		};

	[Fact]
	public void RealizePnl_DoesNotCapWinners()
	{
		// Target B removed → no profit cap. maxLoss -$200, stop 50% → floor -$100. PnL $80 rides through.
		var realized = RealizedExpectancy.RealizePnl(theoreticalPnl: 80m, maxLoss: -200m, frictionPerContract: 0m, cfg: Cfg());
		Assert.Equal(80m, realized);
	}

	[Fact]
	public void RealizePnl_ClampsBelowStopLoss()
	{
		// maxLoss -$200, stop 50% → floor at -$100. PnL -$150 → realized -$100.
		var realized = RealizedExpectancy.RealizePnl(theoreticalPnl: -150m, maxLoss: -200m, frictionPerContract: 0m, cfg: Cfg());
		Assert.Equal(-100m, realized);
	}

	[Fact]
	public void RealizePnl_PassesThroughAboveFloor()
	{
		var realized = RealizedExpectancy.RealizePnl(theoreticalPnl: 25m, maxLoss: -200m, frictionPerContract: 0m, cfg: Cfg());
		Assert.Equal(25m, realized);
	}

	[Fact]
	public void RealizePnl_SubtractsFriction()
	{
		var realized = RealizedExpectancy.RealizePnl(theoreticalPnl: 25m, maxLoss: -200m, frictionPerContract: 8m, cfg: Cfg());
		Assert.Equal(17m, realized);
	}

	[Fact]
	public void RealizePnl_DisabledNoopsFlooringButStillSubtractsFriction()
	{
		// Disabled → no flooring. Friction still subtracted (caller is expected to zero it out, but the
		// function stays monotonic so passing nonzero doesn't break the contract).
		var realized = RealizedExpectancy.RealizePnl(theoreticalPnl: -300m, maxLoss: -200m, frictionPerContract: 5m, cfg: Cfg(enabled: false));
		Assert.Equal(-305m, realized);
	}

	[Fact]
	public void RealizePnl_DegenerateMaxLossPassesThrough()
	{
		var realized = RealizedExpectancy.RealizePnl(theoreticalPnl: 50m, maxLoss: 0m, frictionPerContract: 0m, cfg: Cfg());
		Assert.Equal(50m, realized);
	}

	[Fact]
	public void StopLossPerContract_AlwaysNegativeWhenEnabled()
	{
		var stop = RealizedExpectancy.StopLossPerContract(-200m, Cfg(stopLossPct: 0.50m));
		Assert.Equal(-100m, stop);
	}

	[Fact]
	public void OrdersForStructure_DoublesNeedTwoOrders()
	{
		Assert.Equal(2, RealizedExpectancy.OrdersForStructure(OpenStructureKind.DoubleCalendar));
		Assert.Equal(2, RealizedExpectancy.OrdersForStructure(OpenStructureKind.DoubleDiagonal));
	}

	[Theory]
	[InlineData(OpenStructureKind.LongCall)]
	[InlineData(OpenStructureKind.LongPut)]
	[InlineData(OpenStructureKind.LongCalendar)]
	[InlineData(OpenStructureKind.LongDiagonal)]
	[InlineData(OpenStructureKind.ShortPutVertical)]
	[InlineData(OpenStructureKind.ShortCallVertical)]
	[InlineData(OpenStructureKind.IronButterfly)]
	[InlineData(OpenStructureKind.IronCondor)]
	public void OrdersForStructure_SingleOrderForComboSupported(OpenStructureKind kind)
	{
		Assert.Equal(1, RealizedExpectancy.OrdersForStructure(kind));
	}

	[Fact]
	public void ComputeFriction_SingleOrderStructure()
	{
		// $0.02/share × 1 order × 100 × 2 trips = $4.
		var friction = RealizedExpectancy.ComputeFrictionPerContract(Cfg(slippagePerSharePerOrder: 0.02m), OpenStructureKind.LongCalendar);
		Assert.Equal(4m, friction);
	}

	[Fact]
	public void ComputeFriction_DoubleCalendarDoublesTheCost()
	{
		// $0.02/share × 2 orders × 100 × 2 trips = $8.
		var friction = RealizedExpectancy.ComputeFrictionPerContract(Cfg(slippagePerSharePerOrder: 0.02m), OpenStructureKind.DoubleCalendar);
		Assert.Equal(8m, friction);
	}

	[Fact]
	public void ComputeFriction_IronCondorStaysSingleOrder()
	{
		// Iron condor is 1 combo on Webull — should NOT scale with leg count.
		var friction = RealizedExpectancy.ComputeFrictionPerContract(Cfg(slippagePerSharePerOrder: 0.02m), OpenStructureKind.IronCondor);
		Assert.Equal(4m, friction);
	}

	[Fact]
	public void ComputeFriction_DisabledIsZero()
	{
		Assert.Equal(0m, RealizedExpectancy.ComputeFrictionPerContract(Cfg(enabled: false, slippagePerSharePerOrder: 0.02m), OpenStructureKind.LongCalendar));
	}

	[Fact]
	public void ComputeFriction_ZeroSlippageIsZero()
	{
		Assert.Equal(0m, RealizedExpectancy.ComputeFrictionPerContract(Cfg(slippagePerSharePerOrder: 0m), OpenStructureKind.LongCalendar));
	}

	[Fact]
	public void ComputeFriction_RoundTripsScales()
	{
		// Switching to a single round trip (only entry) halves the cost.
		Assert.Equal(2m, RealizedExpectancy.ComputeFrictionPerContract(Cfg(slippagePerSharePerOrder: 0.02m, roundTrips: 1), OpenStructureKind.LongCalendar));
	}

	[Fact]
	public void RealizeEv_DisabledMinusZeroFrictionEqualsTheoretical()
	{
		var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.30m, years: 30 / 365.0);
		decimal Pnl(decimal sT) => (sT - 100m) * 100m;

		decimal theoreticalEv = 0m;
		foreach (var pt in grid) theoreticalEv += pt.Weight * Pnl(pt.SpotAtExpiry);

		var realized = RealizedExpectancy.RealizeEv(grid, Pnl, maxLoss: -1000m, frictionPerContract: 0m, cfg: Cfg(enabled: false));
		Assert.Equal(theoreticalEv, realized);
	}

	[Fact]
	public void RealizeEv_ManagedStopRaisesEv()
	{
		// With losses floored at the stop and winners riding to max, the managed EV sits ABOVE theoretical.
		var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.30m, years: 30 / 365.0);
		decimal Pnl(decimal sT) => (sT - 100m) * 100m;

		decimal theoreticalEv = 0m;
		foreach (var pt in grid) theoreticalEv += pt.Weight * Pnl(pt.SpotAtExpiry);

		var flooredEv = RealizedExpectancy.RealizeEv(grid, Pnl, maxLoss: -1000m, frictionPerContract: 0m, cfg: Cfg(stopLossPct: 0.50m));
		Assert.True(flooredEv > theoreticalEv, $"managed-stop EV ({flooredEv}) should sit above theoretical ({theoreticalEv}) when downside is floored");
	}
}
