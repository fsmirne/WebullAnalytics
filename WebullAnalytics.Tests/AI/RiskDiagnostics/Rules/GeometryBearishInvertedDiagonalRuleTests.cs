using Xunit;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class GeometryBearishInvertedDiagonalRuleTests
{
    [Fact]
    public void FiresForInvertedDiagonalBearish()
    {
        var hit = new GeometryBearishInvertedDiagonalRule().TryEvaluate(RuleTestFacts.Default(
            structureLabel: "inverted_diagonal", directionalBias: "bearish",
            longLegStrike: 25.5m, shortLegStrike: 25m, netDelta: -0.35m));
        Assert.NotNull(hit);
        Assert.Equal("geometry_bearish_inverted_diagonal", hit!.Id);
        Assert.Equal(25.5m, hit.Inputs["long_strike"]);
        Assert.Equal(25m, hit.Inputs["short_strike"]);
    }

    [Fact]
    public void IncludesTrendAlignedWhenTrendProvided()
    {
        var trend = new TrendSnapshot(ChangePctIntraday: 1m, ChangePct5Day: -4m, ChangePct20Day: -2m, Spot20DayAtrPct: 3m, AsOf: DateTime.Today);
        var hit = new GeometryBearishInvertedDiagonalRule().TryEvaluate(RuleTestFacts.Default(
            structureLabel: "inverted_diagonal", directionalBias: "bearish", trend: trend));
        Assert.NotNull(hit);
        Assert.Equal(1m, hit!.Inputs["trend_aligned"]);
    }

    [Fact]
    public void DoesNotFireForCoveredDiagonal()
    {
        Assert.Null(new GeometryBearishInvertedDiagonalRule().TryEvaluate(RuleTestFacts.Default(structureLabel: "covered_diagonal", directionalBias: "bullish")));
    }

    [Fact]
    public void DoesNotFireWhenBiasNotBearish()
    {
        Assert.Null(new GeometryBearishInvertedDiagonalRule().TryEvaluate(RuleTestFacts.Default(structureLabel: "inverted_diagonal", directionalBias: "neutral")));
    }
}
