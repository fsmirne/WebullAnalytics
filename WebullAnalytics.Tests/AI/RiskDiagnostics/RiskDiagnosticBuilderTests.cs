using Xunit;
using WebullAnalytics;
using WebullAnalytics.AI.RiskDiagnostics;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class RiskDiagnosticBuilderTests
{
    // 2026-04-24 reproduction: long 5/1 $24.50 C @ 0.976 cost basis (current 0.71),
    //                          short 4/24 $25 C @ 0.256 cost basis (current 0.07).
    private static DiagnosticLeg LongLeg() => new(
        Symbol: "GME260501C00024500",
        Parsed: new OptionParsed("GME", new DateTime(2026, 5, 1), "C", 24.50m),
        IsLong: true, Qty: 100,
        PricePerShare: 0.71m, CostBasisPerShare: 0.976m);

    private static DiagnosticLeg ShortLeg() => new(
        Symbol: "GME260424C00025000",
        Parsed: new OptionParsed("GME", new DateTime(2026, 4, 24), "C", 25.00m),
        IsLong: false, Qty: 100,
        PricePerShare: 0.07m, CostBasisPerShare: 0.256m);

    [Fact]
    public void ProducesCoveredDiagonalBullishDiagnostic()
    {
        var asOf = new DateTime(2026, 4, 24);
        var diag = RiskDiagnosticBuilder.Build(
            legs: new[] { LongLeg(), ShortLeg() },
            spot: 24.72m,
            asOf: asOf,
            ivResolver: _ => 0.40m,
            trend: null);

        Assert.Equal("covered_diagonal", diag.StructureLabel);
        Assert.Equal("bullish", diag.DirectionalBias);
        Assert.True(diag.NetDelta > 0m, $"expected positive delta, got {diag.NetDelta}");
        Assert.Equal(0, diag.ShortLegDteMin);
        Assert.Equal(7, diag.LongLegDteMax);
        Assert.Equal(7, diag.DteGapDays);
        Assert.Equal(0.71m, diag.LongPremiumPaid);
        Assert.Equal(0.07m, diag.ShortPremiumReceived);
        Assert.Equal(-0.64m, diag.NetCashPerShare);
        Assert.NotNull(diag.PremiumRatio);
        Assert.Equal(24.72m, diag.SpotAtEvaluation);
        Assert.True(diag.ShortLegOtm);

        // P&L from cost basis: paid 0.976 long − received 0.256 short = 0.72 cost basis (signed)
        // Current value: 0.71 long − 0.07 short = 0.64
        Assert.Equal(0.976m + (-0.256m), diag.CostBasisPerShare);
        Assert.Equal(0.71m + (-0.07m), diag.CurrentValuePerShare);
        Assert.Equal(0.64m - 0.72m, diag.UnrealizedPnlPerShare);

        // Expected rule hits given this configuration
        var ids = diag.Rules.Select(r => r.Id).ToHashSet();
        Assert.Contains("directional_exposure", ids);
        Assert.Contains("short_expires_before_long", ids);
        Assert.Contains("geometry_bullish_covered_diagonal", ids);
        Assert.Contains("premium_ratio_imbalanced", ids);
    }

    [Fact]
    public void SameStrikeCalendarLabelsAsCalendarNeutral()
    {
        var asOf = new DateTime(2026, 4, 24);
        var longLeg = new DiagnosticLeg(
            "GME260522C00025000",
            new OptionParsed("GME", new DateTime(2026, 5, 22), "C", 25m),
            IsLong: true, Qty: 100, PricePerShare: 1.19m, CostBasisPerShare: 1.19m);
        var shortLeg = new DiagnosticLeg(
            "GME260501C00025000",
            new OptionParsed("GME", new DateTime(2026, 5, 1), "C", 25m),
            IsLong: false, Qty: 100, PricePerShare: 0.50m, CostBasisPerShare: 0.50m);

        var diag = RiskDiagnosticBuilder.Build(
            new[] { longLeg, shortLeg }, spot: 25m, asOf: asOf,
            ivResolver: _ => 0.40m, trend: null);

        Assert.Equal("calendar", diag.StructureLabel);
        Assert.Equal("neutral", diag.DirectionalBias);
    }

    [Fact]
    public void TrendIsPassedThroughAndTrendRulesFire()
    {
        var asOf = new DateTime(2026, 4, 24);
        var trend = new TrendSnapshot(
            ChangePctIntraday: -1.4m, ChangePct5Day: -3.2m, ChangePct20Day: -1.8m,
            Spot20DayAtrPct: 3.6m, AsOf: asOf);

        var diag = RiskDiagnosticBuilder.Build(
            new[] { LongLeg(), ShortLeg() }, spot: 24.72m, asOf: asOf,
            ivResolver: _ => 0.40m, trend: trend);

        Assert.NotNull(diag.Trend);
        Assert.Equal(-3.2m, diag.Trend!.ChangePct5Day);

        var ids = diag.Rules.Select(r => r.Id).ToHashSet();
        Assert.Contains("directional_mismatch_near_term", ids);
        Assert.Contains("directional_mismatch_today", ids);
    }

    [Fact]
    public void SingleLongCallLabelsAsBullishSingleLong()
    {
        var leg = new DiagnosticLeg(
            "GME260501C00025000",
            new OptionParsed("GME", new DateTime(2026, 5, 1), "C", 25m),
            IsLong: true, Qty: 100, PricePerShare: 0.50m, CostBasisPerShare: 0.50m);

        var diag = RiskDiagnosticBuilder.Build(
            new[] { leg }, spot: 25m, asOf: new DateTime(2026, 4, 24),
            ivResolver: _ => 0.40m, trend: null);

        Assert.Equal("single_long", diag.StructureLabel);
        Assert.Equal("bullish", diag.DirectionalBias);
    }
}
