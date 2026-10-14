using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class RealizedExpectancyTests
{
	private static OpenerRealizedExpectancyConfig Cfg(
		bool enabled = true,
		decimal stopLossPct = 0.50m,
		decimal stopLossPctOfMaxProfit = 0m,
		decimal takeProfitPct = 0m,
		decimal slippagePerSharePerOrder = 0m,
		int roundTrips = 2) =>
		new()
		{
			Enabled = enabled,
			StopLossPctOfMaxLoss = stopLossPct,
			StopLossPctOfMaxProfit = stopLossPctOfMaxProfit,
			TakeProfitPctOfPremium = takeProfitPct,
			SlippagePerSharePerOrder = slippagePerSharePerOrder,
			RoundTrips = roundTrips,
		};

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
	public void StopLoss_IsTheNearerOfTheMaxLossAndMaxProfitStops()
	{
		Assert.Equal(100m, RealizedExpectancy.StopLossPerContract(maxProfit: 400m, maxLoss: -200m, Cfg(stopLossPct: 0.50m)));
		Assert.Equal(60m, RealizedExpectancy.StopLossPerContract(maxProfit: 400m, maxLoss: -200m, Cfg(stopLossPct: 0.50m, stopLossPctOfMaxProfit: 0.15m)));
	}

	[Fact]
	public void StopLoss_DisabledStopOrScoringIsNull()
	{
		Assert.Null(RealizedExpectancy.StopLossPerContract(400m, -200m, Cfg(stopLossPct: 1m)));
		Assert.Null(RealizedExpectancy.StopLossPerContract(400m, -200m, Cfg(enabled: false)));
	}

	[Fact]
	public void TakeProfit_IsAFractionOfEntryPremiumForDebitAndCredit()
	{
		Assert.Equal(20m, RealizedExpectancy.TakeProfitPerContract(entryValuePerContract: 100m, Cfg(takeProfitPct: 0.20m)));
		Assert.Equal(20m, RealizedExpectancy.TakeProfitPerContract(entryValuePerContract: -100m, Cfg(takeProfitPct: 0.20m)));
		Assert.Null(RealizedExpectancy.TakeProfitPerContract(100m, Cfg()));
	}
}
