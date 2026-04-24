using Xunit;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class HighRealizedVolRuleTests
{
    private static TrendSnapshot Trend(decimal atrPct) =>
        new(ChangePctIntraday: null, ChangePct5Day: 0m, ChangePct20Day: 0m, Spot20DayAtrPct: atrPct, AsOf: DateTime.Today);

    [Fact]
    public void FiresWhenAtrAboveThreshold()
    {
        var hit = new HighRealizedVolRule().TryEvaluate(RuleTestFacts.Default(trend: Trend(4.5m)));
        Assert.NotNull(hit);
        Assert.Equal("high_realized_vol", hit!.Id);
        Assert.Equal(4.5m, hit.Inputs["atr_pct"]);
        Assert.Equal(4m, hit.Inputs["threshold"]);
    }

    [Fact]
    public void DoesNotFireAtThreshold()
    {
        Assert.Null(new HighRealizedVolRule().TryEvaluate(RuleTestFacts.Default(trend: Trend(4m))));
    }

    [Fact]
    public void DoesNotFireWhenTrendNull()
    {
        Assert.Null(new HighRealizedVolRule().TryEvaluate(RuleTestFacts.Default(trend: null)));
    }
}
