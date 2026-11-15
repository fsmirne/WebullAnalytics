using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

// A session replayed ahead of a backtest's day loop pins its own day and rate; the day loop re-setting the process-wide values while
// the replay runs must not reach it, and the pin must not leak to the code running beside it.
[Collection("GlobalPricingState")]
public class PricingPinTests
{
	[Fact]
	public async Task APinnedRateHoldsAcrossAwaitsWhileTheGlobalRateMoves()
	{
		var global = OptionMath.RiskFreeRate;
		try
		{
			OptionMath.RiskFreeRate = 0.01;
			var inside = new TaskCompletionSource();
			var moved = new TaskCompletionSource();
			var replay = Task.Run(async () =>
			{
				using var pin = OptionMath.PinRiskFreeRate(0.05);
				inside.SetResult();
				await moved.Task;                          // the "day loop" changes the global rate meanwhile
				await Task.Yield();
				return OptionMath.RiskFreeRate;
			});
			await inside.Task;
			OptionMath.RiskFreeRate = 0.03;
			Assert.Equal(0.03, OptionMath.RiskFreeRate);    // the day loop sees its own value, not the replay's pin
			moved.SetResult();
			Assert.Equal(0.05, await replay);
			Assert.Equal(0.03, OptionMath.RiskFreeRate);
		}
		finally { OptionMath.RiskFreeRate = global; }
	}

	[Fact]
	public void DisposingAPinRestoresTheValueUnderIt()
	{
		var global = OptionMath.RiskFreeRate;
		try
		{
			OptionMath.RiskFreeRate = 0.02;
			using (OptionMath.PinRiskFreeRate(0.04))
			{
				using (OptionMath.PinRiskFreeRate(0.06)) Assert.Equal(0.06, OptionMath.RiskFreeRate);
				Assert.Equal(0.04, OptionMath.RiskFreeRate);
			}
			Assert.Equal(0.02, OptionMath.RiskFreeRate);
		}
		finally { OptionMath.RiskFreeRate = global; }
	}

	[Fact]
	public async Task APinnedDayHoldsWhileTheGlobalDayMoves()
	{
		var global = MarketClock.PinnedDay;
		try
		{
			MarketClock.PinnedDay = new DateOnly(2026, 1, 5);
			var inside = new TaskCompletionSource();
			var moved = new TaskCompletionSource();
			var replay = Task.Run(async () =>
			{
				using var pin = MarketClock.PinDay(new DateOnly(2024, 6, 3));
				inside.SetResult();
				await moved.Task;
				return MarketClock.PinnedDay;
			});
			await inside.Task;
			MarketClock.PinnedDay = new DateOnly(2026, 1, 6);
			moved.SetResult();
			Assert.Equal(new DateOnly(2024, 6, 3), await replay);
			Assert.Equal(new DateOnly(2026, 1, 6), MarketClock.PinnedDay);
		}
		finally { MarketClock.PinnedDay = global; }
	}
}

/// <summary>These tests move the process-wide rate and clock day, which every pricing test reads: they run alone.</summary>
[CollectionDefinition("GlobalPricingState", DisableParallelization = true)]
public class GlobalPricingStateCollection;
