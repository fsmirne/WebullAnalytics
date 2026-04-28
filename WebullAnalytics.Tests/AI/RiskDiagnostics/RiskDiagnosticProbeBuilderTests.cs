using WebullAnalytics.AI;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class RiskDiagnosticProbeBuilderTests
{
    [Fact]
    public void OpenerEnumDeltaUsesEnumerationDefaultIvInsteadOfLiveQuoteIv()
    {
        var asOf = new DateTime(2026, 4, 20);
        var expiry = new DateTime(2026, 4, 24);
        var spot = 24.95m;
        var shortSymbol = MatchKeys.OccSymbol("GME", expiry, 25.5m, "C");
        var longSymbol = MatchKeys.OccSymbol("GME", expiry, 26.5m, "C");
        var shortLeg = new DiagnosticLeg(shortSymbol, new OptionParsed("GME", expiry, "C", 25.5m), IsLong: false, Qty: 1, PricePerShare: 0.36m, CostBasisPerShare: null);
        var longLeg = new DiagnosticLeg(longSymbol, new OptionParsed("GME", expiry, "C", 26.5m), IsLong: true, Qty: 1, PricePerShare: 0.12m, CostBasisPerShare: null);
        var cfg = new OpenerConfig();
        cfg.Structures.ShortVertical.ShortDeltaMin = 0.15m;
        cfg.Structures.ShortVertical.ShortDeltaMax = 0.35m;
        cfg.IvDefaultPct = 40m;

        var defaultIvDelta = Math.Abs(OptionMath.Delta(spot, 25.5m, 4 / 365.0, OptionMath.RiskFreeRate, 0.40m, "C"));
        var liveIvDelta = Math.Abs(OptionMath.Delta(spot, 25.5m, 4 / 365.0, OptionMath.RiskFreeRate, 0.80m, "C"));
        Assert.InRange(defaultIvDelta, 0.15m, 0.35m);
        Assert.True(liveIvDelta > 0.35m);

       var probe = RiskDiagnosticProbeBuilder.Build(
            legs: new[] { shortLeg, longLeg },
            spot: spot,
            asOf: asOf,
            ivResolver: _ => 0.80m,
            quotes: null,
            opener: (
                bias: 0m,
                cfg: cfg,
                structure: nameof(OpenStructureKind.ShortCallVertical),
                qty: 1,
                rationale: "",
                creditPerContract: 24m,
                maxProfit: 24m,
                maxLoss: -76m,
                risk: 76m,
                pop: 0.5m,
                ev: 1m,
                days: 4,
                rawScore: 0.01m,
                biasScore: 0.01m,
                thetaPerDayPerContract: 1.23m));

        Assert.NotNull(probe.EnumDelta);
        Assert.Equal(Math.Round(defaultIvDelta, 12), Math.Round(probe.EnumDelta!.Value, 12));
        Assert.True(probe.EnumDeltaPass);
    }
}
