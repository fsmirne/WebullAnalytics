using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

public class DefensiveRollRuleTests
{
	[Fact]
	public void Evaluate_DoesNotFire_WhenSpotIsStillInsideCalendarBreakEvenBand()
	{
		var rule = new DefensiveRollRule(new DefensiveRollConfig
		{
			Enabled = true,
			TriggerDTE = 3,
			SpotWithinPctOfShortStrike = 1.0m,
			StrikeStep = 0.50m,
		});

		var position = new OpenPosition(
			Key: "GME_CALENDAR_25.00_20260501",
			Ticker: "GME",
			StrategyKind: "CALENDAR",
			Legs: new[]
			{
				new PositionLeg("GME260501P00025000", Side.Sell, 25.00m, new DateTime(2026, 5, 1), "P", 474),
				new PositionLeg("GME260605P00025000", Side.Buy, 25.00m, new DateTime(2026, 6, 5), "P", 474),
			},
			InitialNetDebit: 0.55m,
			AdjustedNetDebit: 0.55m,
			Quantity: 474);

		var ctx = new EvaluationContext(
			Now: new DateTime(2026, 4, 28),
			OpenPositions: new Dictionary<string, OpenPosition>
			{
				[position.Key] = position,
			},
			UnderlyingPrices: new Dictionary<string, decimal>
			{
				["GME"] = 25.09m,
			},
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260501P00025000"] = new OptionContractQuote("GME260501P00025000", null, 0.18m, 0.22m, null, null, 100, 1000, 0.55m),
				["GME260605P00025000"] = new OptionContractQuote("GME260605P00025000", null, 1.29m, 1.39m, null, null, 100, 1000, 0.48m),
			},
			AccountCash: 0m,
			AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());

		var proposal = rule.Evaluate(position, ctx);

		Assert.Null(proposal);
	}
}
