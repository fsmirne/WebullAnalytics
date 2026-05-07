using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class PositionBreakEvenEstimatorTests
{
	private static OpenPosition GmeCallCalendar(decimal adjDebit = 0.73m, decimal strike = 26.50m)
	{
		var shortExpiry = new DateTime(2026, 5, 8);
		var longExpiry = new DateTime(2026, 5, 29);
		return new OpenPosition(
			Key: "GME_CALENDAR_26.50_20260508",
			Ticker: "GME",
			StrategyKind: "CALENDAR",
			Legs: new[]
			{
				new PositionLeg("GME260508C00026500", Side.Sell, strike, shortExpiry, "C", 400),
				new PositionLeg("GME260529C00026500", Side.Buy,  strike, longExpiry, "C", 400),
			},
			InitialNetDebit: 0.92m,
			AdjustedNetDebit: adjDebit,
			Quantity: 400);
	}

	private static EvaluationContext Ctx(decimal spot, decimal? longIv, OpenPosition position)
	{
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["GME260508C00026500"] = new("GME260508C00026500", null, 0.10m, 0.12m, null, null, 100, 1000, 0.70m),
			["GME260529C00026500"] = new("GME260529C00026500", null, 0.90m, 1.05m, null, null, 100, 1000, longIv),
		};
		return new EvaluationContext(
			Now: new DateTime(2026, 5, 6, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = spot },
			Quotes: quotes,
			AccountCash: 0m,
			AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	[Fact]
	public void TimeSpread_WithIv_ProducesBlackScholesBand_TighterThanHeuristicTriple()
	{
		var position = GmeCallCalendar(adjDebit: 0.73m, strike: 26.50m);
		var ctx = Ctx(spot: 25.17m, longIv: 0.55m, position: position);

		var (low, high, source) = PositionBreakEvenEstimator.Estimate(position, ctx);

		Assert.Equal(PositionBreakEvenEstimator.BreakEvenSource.BlackScholes, source);
		Assert.NotNull(low);
		Assert.NotNull(high);
		// Sanity: BEs should bracket the strike for an at-strike calendar.
		Assert.InRange(low!.Value, 24.00m, 26.50m);
		Assert.InRange(high!.Value, 26.50m, 29.00m);
		// Old heuristic gave 26.50 ± 0.73*3 = [24.31, 28.69]. The BS band should be tighter on at
		// least one side (calendar P&L falls off faster than a linear *3 implies once IV is real).
		var oldLow = 26.50m - 0.73m * 3m;
		var oldHigh = 26.50m + 0.73m * 3m;
		Assert.True(low.Value > oldLow || high.Value < oldHigh,
			$"expected BS band tighter than heuristic, got [{low.Value:F2}, {high.Value:F2}]");
	}

	[Fact]
	public void TimeSpread_WithoutIv_FallsBackToTightHeuristic()
	{
		var position = GmeCallCalendar(adjDebit: 0.73m);
		var ctx = Ctx(spot: 25.17m, longIv: null, position: position);

		var (low, high, source) = PositionBreakEvenEstimator.Estimate(position, ctx);

		Assert.Equal(PositionBreakEvenEstimator.BreakEvenSource.Heuristic, source);
		Assert.Equal(26.50m - 0.73m, low);
		Assert.Equal(26.50m + 0.73m, high);
	}

	[Fact]
	public void ReturnsNone_WhenDebitIsZero()
	{
		var position = GmeCallCalendar(adjDebit: 0m);
		var ctx = Ctx(spot: 25.17m, longIv: 0.55m, position: position);

		var (low, high, source) = PositionBreakEvenEstimator.Estimate(position, ctx);

		Assert.Equal(PositionBreakEvenEstimator.BreakEvenSource.None, source);
		Assert.Null(low);
		Assert.Null(high);
	}

	[Fact]
	public void ReturnsNone_WhenNoLongOrShortLeg()
	{
		var shortOnly = new OpenPosition(
			Key: "GME_NAKED",
			Ticker: "GME",
			StrategyKind: "CALENDAR",
			Legs: new[]
			{
				new PositionLeg("GME260508C00026500", Side.Sell, 26.50m, new DateTime(2026, 5, 8), "C", 100),
			},
			InitialNetDebit: 0.50m,
			AdjustedNetDebit: 0.50m,
			Quantity: 100);
		var ctx = Ctx(spot: 25m, longIv: 0.55m, position: shortOnly);

		var (low, high, source) = PositionBreakEvenEstimator.Estimate(shortOnly, ctx);

		Assert.Equal(PositionBreakEvenEstimator.BreakEvenSource.None, source);
		Assert.Null(low);
		Assert.Null(high);
	}
}
