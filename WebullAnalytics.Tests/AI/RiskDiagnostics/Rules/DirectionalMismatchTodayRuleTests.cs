using Xunit;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.RiskDiagnostics.Rules;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class DirectionalMismatchTodayRuleTests
{
    private static TrendSnapshot Trend(decimal? intraday) =>
        new(ChangePctIntraday: intraday, ChangePct5Day: 0m, ChangePct20Day: 0m, Spot20DayAtrPct: 3m, AsOf: DateTime.Today);

    [Fact]
    public void FiresWhenDeltaPositiveAndIntradayNegative()
    {
        var hit = new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.35m, trend: Trend(-1.4m)));
        Assert.NotNull(hit);
        Assert.Equal("directional_mismatch_today", hit!.Id);
        Assert.Equal(0.35m, hit.Inputs["net_delta"]);
        Assert.Equal(-1.4m, hit.Inputs["change_intraday"]);
        Assert.Equal(1m, hit.Inputs["threshold"]);
    }

    [Fact]
    public void FiresWhenDeltaNegativeAndIntradayPositive()
    {
        var hit = new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: -0.30m, trend: Trend(1.2m)));
        Assert.NotNull(hit);
    }

    [Fact]
    public void DoesNotFireWhenAligned()
    {
        Assert.Null(new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.35m, trend: Trend(1.2m))));
    }

    [Fact]
    public void DoesNotFireWhenDeltaBelowDirectionalThreshold()
    {
        Assert.Null(new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.20m, trend: Trend(-1.5m))));
    }

    [Fact]
    public void DoesNotFireWhenIntradayMoveSmall()
    {
        Assert.Null(new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.35m, trend: Trend(-0.5m))));
    }

    [Fact]
    public void DoesNotFireWhenIntradayNull()
    {
        Assert.Null(new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.35m, trend: Trend(null))));
    }

    [Fact]
    public void DoesNotFireWhenTrendNull()
    {
        Assert.Null(new DirectionalMismatchTodayRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.35m, trend: null)));
    }
}
