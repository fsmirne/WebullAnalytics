using Xunit;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class DirectionalMismatchNearTermRuleTests
{
    private static TrendSnapshot Trend(decimal change5Day) =>
        new(ChangePctIntraday: null, ChangePct5Day: change5Day, ChangePct20Day: 0m, Atr14Pct: 3m, AsOf: DateTime.Today);

    [Fact]
    public void FiresWhenBullishAnd5DayNegative()
    {
        var hit = new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "bullish", trend: Trend(-3.5m)));
        Assert.NotNull(hit);
        Assert.Equal("directional_mismatch_near_term", hit!.Id);
        Assert.Equal(-3.5m, hit.Inputs["change_5day"]);
        Assert.Equal(3m, hit.Inputs["threshold"]);
    }

    [Fact]
    public void FiresWhenBearishAnd5DayPositive()
    {
        var hit = new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "bearish", trend: Trend(4m)));
        Assert.NotNull(hit);
    }

    [Fact]
    public void DoesNotFireWhenAligned()
    {
        Assert.Null(new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "bullish", trend: Trend(4m))));
        Assert.Null(new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "bearish", trend: Trend(-4m))));
    }

    [Fact]
    public void DoesNotFireForNeutralBias()
    {
        Assert.Null(new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "neutral", trend: Trend(-4m))));
    }

    [Fact]
    public void DoesNotFireWhenTrendNull()
    {
        Assert.Null(new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "bullish", trend: null)));
    }

    [Fact]
    public void DoesNotFireAtOrBelowThreshold()
    {
        Assert.Null(new DirectionalMismatchNearTermRule().TryEvaluate(RuleTestFacts.Default(directionalBias: "bullish", trend: Trend(-3m))));
    }
}
