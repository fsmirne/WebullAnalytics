using Xunit;
using WebullAnalytics.AI.RiskDiagnostics.Rules;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class VegaAdverseRuleTests
{
    [Fact]
    public void FiresWhenVegaBelowThreshold()
    {
        var hit = new VegaAdverseRule().TryEvaluate(RuleTestFacts.Default(netVega: -6m));
        Assert.NotNull(hit);
        Assert.Equal("vega_adverse", hit!.Id);
        Assert.Equal(-6m, hit.Inputs["net_vega"]);
        Assert.Equal(-5m, hit.Inputs["threshold"]);
    }

    [Fact]
    public void DoesNotFireAtThreshold()
    {
        Assert.Null(new VegaAdverseRule().TryEvaluate(RuleTestFacts.Default(netVega: -5m)));
    }

    [Fact]
    public void DoesNotFireOnPositiveVega()
    {
        Assert.Null(new VegaAdverseRule().TryEvaluate(RuleTestFacts.Default(netVega: 5m)));
    }
}
