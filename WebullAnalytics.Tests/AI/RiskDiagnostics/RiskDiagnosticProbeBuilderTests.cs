using WebullAnalytics.AI;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class RiskDiagnosticProbeBuilderTests
{
	[Fact]
	public void TryBuildCandidateSkeletonRecognizesIronCondor()
	{
		var expiry = new DateTime(2026, 5, 8);
		var legs = new[]
		{
			new DiagnosticLeg(MatchKeys.OccSymbol("GME", expiry, 23.5m, "P"), new OptionParsed("GME", expiry, "P", 23.5m), IsLong: true, Qty: 1, PricePerShare: 0.11m, CostBasisPerShare: null),
			new DiagnosticLeg(MatchKeys.OccSymbol("GME", expiry, 24.0m, "P"), new OptionParsed("GME", expiry, "P", 24.0m), IsLong: false, Qty: 1, PricePerShare: 0.21m, CostBasisPerShare: null),
			new DiagnosticLeg(MatchKeys.OccSymbol("GME", expiry, 26.0m, "C"), new OptionParsed("GME", expiry, "C", 26.0m), IsLong: false, Qty: 1, PricePerShare: 0.42m, CostBasisPerShare: null),
			new DiagnosticLeg(MatchKeys.OccSymbol("GME", expiry, 26.5m, "C"), new OptionParsed("GME", expiry, "C", 26.5m), IsLong: true, Qty: 1, PricePerShare: 0.31m, CostBasisPerShare: null),
		};

		var skel = RiskDiagnosticProbeBuilder.TryBuildCandidateSkeleton(legs);

		Assert.NotNull(skel);
		Assert.Equal(OpenStructureKind.IronCondor, skel!.StructureKind);
		Assert.Equal(expiry, skel.TargetExpiry);
		Assert.Equal(4, skel.Legs.Count);
	}

	[Fact]
	public void TryBuildCandidateSkeletonRecognizesSingleSidedCondor()
	{
		// All-put long condor: long wings (80, 110), short body (90, 100). Maps to Condor so ScoreMultiLeg
		// can price EM/breakevens/PoP for the already-open position (the opener never builds this).
		var expiry = new DateTime(2026, 7, 17);
		var legs = new[]
		{
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 80m, "P"), new OptionParsed("USO", expiry, "P", 80m), IsLong: true, Qty: 20, PricePerShare: 0.11m, CostBasisPerShare: 0.11m),
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 90m, "P"), new OptionParsed("USO", expiry, "P", 90m), IsLong: false, Qty: 20, PricePerShare: 0.20m, CostBasisPerShare: 0.20m),
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 100m, "P"), new OptionParsed("USO", expiry, "P", 100m), IsLong: false, Qty: 20, PricePerShare: 0.60m, CostBasisPerShare: 0.60m),
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 110m, "P"), new OptionParsed("USO", expiry, "P", 110m), IsLong: true, Qty: 20, PricePerShare: 1.95m, CostBasisPerShare: 1.95m),
		};
		var skel = RiskDiagnosticProbeBuilder.TryBuildCandidateSkeleton(legs);
		Assert.NotNull(skel);
		Assert.Equal(OpenStructureKind.Condor, skel!.StructureKind);
		Assert.Equal(4, skel.Legs.Count);

		// Long/short alternating by strike (two stacked verticals) is not a condor.
		var stacked = new[]
		{
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 80m, "P"), new OptionParsed("USO", expiry, "P", 80m), IsLong: true, Qty: 1, PricePerShare: 1m, CostBasisPerShare: 1m),
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 90m, "P"), new OptionParsed("USO", expiry, "P", 90m), IsLong: false, Qty: 1, PricePerShare: 1m, CostBasisPerShare: 1m),
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 100m, "P"), new OptionParsed("USO", expiry, "P", 100m), IsLong: true, Qty: 1, PricePerShare: 1m, CostBasisPerShare: 1m),
			new DiagnosticLeg(MatchKeys.OccSymbol("USO", expiry, 110m, "P"), new OptionParsed("USO", expiry, "P", 110m), IsLong: false, Qty: 1, PricePerShare: 1m, CostBasisPerShare: 1m),
		};
		Assert.Null(RiskDiagnosticProbeBuilder.TryBuildCandidateSkeleton(stacked));
	}

	[Fact]
	public void OpenerEnumDeltaPrefersLiveQuoteIvWhenAvailable()
	{
		// The enumerator now reads each strike's live IV from the chain (commit replacing the static
		// ivDefaultPct fallback with ResolveIv). The diagnostic probe mirrors that contract: when the
		// caller's ivResolver returns a non-zero live IV, the enumerator-delta gauge uses that IV
		// rather than cfg.Indicators.IvDefaultPct. Otherwise the probe's "FAIL/PASS" label would
		// disagree with what the enumerator actually computed — the original bug this test asserts
		// the fix for.
		var asOf = new DateTime(2026, 4, 20);
		var expiry = new DateTime(2026, 4, 24);
		var spot = 24.95m;
		var shortSymbol = MatchKeys.OccSymbol("GME", expiry, 25.5m, "C");
		var longSymbol = MatchKeys.OccSymbol("GME", expiry, 26.5m, "C");
		var shortLeg = new DiagnosticLeg(shortSymbol, new OptionParsed("GME", expiry, "C", 25.5m), IsLong: false, Qty: 1, PricePerShare: 0.36m, CostBasisPerShare: null);
		var longLeg = new DiagnosticLeg(longSymbol, new OptionParsed("GME", expiry, "C", 26.5m), IsLong: true, Qty: 1, PricePerShare: 0.12m, CostBasisPerShare: null);
		var cfg = new OpenerConfig { Indicators = new IndicatorsConfig { IvDefaultPct = 40m, StrikeStep = 1.0m } };
		cfg.Structures.ShortVertical.ShortDeltaMin = 0.15m;
		cfg.Structures.ShortVertical.ShortDeltaMax = 0.35m;

		var t = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, expiry);
		var defaultIvDelta = Math.Abs(OptionMath.Delta(spot, 25.5m, t, OptionMath.RiskFreeRate, 0.40m, "C"));
		var liveIvDelta = Math.Abs(OptionMath.Delta(spot, 25.5m, t, OptionMath.RiskFreeRate, 0.80m, "C"));
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
				thetaPerDayPerContract: 1.23m,
				finalScore: 0.0103075m));

		Assert.NotNull(probe.EnumDelta);
		Assert.Equal(Math.Round(liveIvDelta, 12), Math.Round(probe.EnumDelta!.Value, 12));
		Assert.False(probe.EnumDeltaPass);
	}

	[Fact]
	public void OpenerEnumDeltaFallsBackToDefaultIvWhenNoLiveQuote()
	{
		// When the chain hasn't provided a quote for the strike (ivResolver returns 0), the probe
		// falls back to cfg.Indicators.IvDefaultPct — same fallback the enumerator's ResolveIv uses.
		var asOf = new DateTime(2026, 4, 20);
		var expiry = new DateTime(2026, 4, 24);
		var spot = 24.95m;
		var shortSymbol = MatchKeys.OccSymbol("GME", expiry, 25.5m, "C");
		var longSymbol = MatchKeys.OccSymbol("GME", expiry, 26.5m, "C");
		var shortLeg = new DiagnosticLeg(shortSymbol, new OptionParsed("GME", expiry, "C", 25.5m), IsLong: false, Qty: 1, PricePerShare: 0.36m, CostBasisPerShare: null);
		var longLeg = new DiagnosticLeg(longSymbol, new OptionParsed("GME", expiry, "C", 26.5m), IsLong: true, Qty: 1, PricePerShare: 0.12m, CostBasisPerShare: null);
		var cfg = new OpenerConfig { Indicators = new IndicatorsConfig { IvDefaultPct = 40m, StrikeStep = 1.0m } };
		cfg.Structures.ShortVertical.ShortDeltaMin = 0.15m;
		cfg.Structures.ShortVertical.ShortDeltaMax = 0.35m;

		var t = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, expiry);
		var defaultIvDelta = Math.Abs(OptionMath.Delta(spot, 25.5m, t, OptionMath.RiskFreeRate, 0.40m, "C"));

		var probe = RiskDiagnosticProbeBuilder.Build(
			legs: new[] { shortLeg, longLeg },
			spot: spot,
			asOf: asOf,
			ivResolver: _ => 0m,  // no live quote available
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
				thetaPerDayPerContract: 1.23m,
				finalScore: 0.0103075m));

		Assert.NotNull(probe.EnumDelta);
		Assert.Equal(Math.Round(defaultIvDelta, 12), Math.Round(probe.EnumDelta!.Value, 12));
		Assert.True(probe.EnumDeltaPass);
	}

	[Fact]
	public void ProbeReportsScoreUnavailableWhenTickerHasNoOpenerConfig()
	{
		// A ticker with no ai-config.<TICKER>.json can't be scored (the opener config — chiefly
		// indicators.strikeStep — has no default), so the opener-score block is unavailable. The probe
		// surfaces WHY, so the display can warn the user what to create instead of silently omitting EM.
		var expiry = new DateTime(2026, 7, 17);
		var legs = new[]
		{
			new DiagnosticLeg(MatchKeys.OccSymbol("ZZZ", expiry, 80m, "P"), new OptionParsed("ZZZ", expiry, "P", 80m), IsLong: true, Qty: 1, PricePerShare: 0.11m, CostBasisPerShare: 0.11m),
			new DiagnosticLeg(MatchKeys.OccSymbol("ZZZ", expiry, 90m, "P"), new OptionParsed("ZZZ", expiry, "P", 90m), IsLong: false, Qty: 1, PricePerShare: 0.20m, CostBasisPerShare: 0.20m),
			new DiagnosticLeg(MatchKeys.OccSymbol("ZZZ", expiry, 100m, "P"), new OptionParsed("ZZZ", expiry, "P", 100m), IsLong: false, Qty: 1, PricePerShare: 0.60m, CostBasisPerShare: 0.60m),
			new DiagnosticLeg(MatchKeys.OccSymbol("ZZZ", expiry, 110m, "P"), new OptionParsed("ZZZ", expiry, "P", 110m), IsLong: true, Qty: 1, PricePerShare: 1.95m, CostBasisPerShare: 1.95m),
		};

		var probe = RiskDiagnosticProbeBuilder.Build(legs, spot: 95m, asOf: new DateTime(2026, 6, 15), ivResolver: _ => 0.45m, quotes: null);

		Assert.NotNull(probe.ScoreUnavailableReason);
		Assert.Contains("config", probe.ScoreUnavailableReason!, StringComparison.OrdinalIgnoreCase);
	}
}
