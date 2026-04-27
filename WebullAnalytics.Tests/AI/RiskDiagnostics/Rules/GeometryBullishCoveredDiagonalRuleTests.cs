using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class GeometryBullishCoveredDiagonalRuleTests
{
	[Fact]
	public void FiresForCoveredDiagonalBullish()
	{
		var hit = new GeometryBullishCoveredDiagonalRule().TryEvaluate(RuleTestFacts.Default(
			structureLabel: "covered_diagonal", directionalBias: "bullish",
			longLegStrike: 24.5m, shortLegStrike: 25m, spot: 24.72m, netDelta: 0.35m));
		Assert.NotNull(hit);
		Assert.Equal("geometry_bullish_covered_diagonal", hit!.Id);
		Assert.Equal(24.5m, hit.Inputs["long_strike"]);
		Assert.Equal(25m, hit.Inputs["short_strike"]);
		Assert.Equal(24.72m, hit.Inputs["spot"]);
		Assert.Equal(0.35m, hit.Inputs["net_delta"]);
	}

	[Fact]
	public void IncludesTrendAlignedWhenTrendProvided()
	{
		var trend = new TrendSnapshot(ChangePctIntraday: -1.4m, ChangePct5Day: -3.2m, ChangePct20Day: -1.8m, Atr14Pct: 3.6m, AsOf: DateTime.Today);
		var hit = new GeometryBullishCoveredDiagonalRule().TryEvaluate(RuleTestFacts.Default(
			structureLabel: "covered_diagonal", directionalBias: "bullish", trend: trend));
		Assert.NotNull(hit);
		Assert.Equal(0m, hit!.Inputs["trend_aligned"]);
	}

	[Fact]
	public void DoesNotFireForCalendar()
	{
		Assert.Null(new GeometryBullishCoveredDiagonalRule().TryEvaluate(RuleTestFacts.Default(structureLabel: "calendar", directionalBias: "neutral")));
	}

	[Fact]
	public void DoesNotFireForInvertedDiagonal()
	{
		Assert.Null(new GeometryBullishCoveredDiagonalRule().TryEvaluate(RuleTestFacts.Default(structureLabel: "inverted_diagonal", directionalBias: "bearish")));
	}
}
