using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerBiasTests
{
	[Fact]
	public void RawScoreInvariantToBiasWhenFitIsZero()
	{
		var raw = 0.005m;
		var withoutBias = CandidateScorer.BiasAdjust(raw, bias: 0m, fit: 0, alpha: 0.5m);
		var withBias = CandidateScorer.BiasAdjust(raw, bias: 0.8m, fit: 0, alpha: 0.5m);
		Assert.Equal(withoutBias, withBias);
		Assert.Equal(raw, withBias);
	}

	[Fact]
	public void PositiveBiasBoostsPositiveFit()
	{
		var raw = 0.010m;
		var adjusted = CandidateScorer.BiasAdjust(raw, bias: 0.4m, fit: 1, alpha: 0.5m);
        // 0.010 × (1 + 0.5 × 0.4 × 1) = 0.010 × 1.20 = 0.012
		Assert.Equal(0.012m, adjusted);
	}

	[Fact]
	public void NegativeBiasCutsPositiveFit()
	{
		var raw = 0.010m;
		var adjusted = CandidateScorer.BiasAdjust(raw, bias: -0.4m, fit: 1, alpha: 0.5m);
		// 0.010 × (1 + 0.5 × −0.4 × 1) = 0.010 × 0.80 = 0.008
		Assert.Equal(0.008m, adjusted);
	}

	[Fact]
	public void NegativeBiasBoostsNegativeFit()
	{
		var raw = 0.010m;
		var adjusted = CandidateScorer.BiasAdjust(raw, bias: -0.4m, fit: -1, alpha: 0.5m);
		Assert.Equal(0.012m, adjusted);
	}

	[Fact]
	public void RawScoreFormulaHonorsZeroDaysClamp()
	{
		// EV 100, days 0 → clamped to 1; capital 200 → 100 / 1 / 200 = 0.5.
		Assert.Equal(0.5m, CandidateScorer.ComputeRawScore(ev: 100m, daysToTarget: 0, capitalAtRisk: 200m));
	}

	[Fact]
	public void RawScoreClampsCapitalAtOne()
	{
		// 50 / 5 days / max(1, 0) = 10. Zero capital clamps to 1 to avoid divide-by-zero.
		Assert.Equal(10m, CandidateScorer.ComputeRawScore(ev: 50m, daysToTarget: 5, capitalAtRisk: 0m));
	}

	[Fact]
	public void HighIvRelativeToHvBoostsShortVegaPositions()
	{
		// netVega = -3 saturates short-vega side at vegaScaled = -1; richness saturates at +1 with IV at
		// 2× HV. Factor = 1 - 0.5 × (-1) × 1 = 1.5. Score 0.010 × 1.5 = 0.015.
		var adjusted = CandidateScorer.VolatilityAdjust(
			score: 0.010m,
			netVegaPerContract: -3m,
			ivAnnual: 0.60m,
			historicalVolAnnual: 0.30m,
			weight: 0.50m);

		Assert.Equal(0.015m, adjusted);
	}

	[Fact]
	public void HighIvRelativeToHvCutsLongVegaPositions()
	{
		// netVega = +3 saturates long-vega side at vegaScaled = +1; richness +1. Factor = 1 - 0.5 × 1 × 1 = 0.5.
		var adjusted = CandidateScorer.VolatilityAdjust(
			score: 0.010m,
			netVegaPerContract: 3m,
			ivAnnual: 0.60m,
			historicalVolAnnual: 0.30m,
			weight: 0.50m);

		Assert.Equal(0.005m, adjusted);
	}

	[Fact]
	public void LowVegaPositionGetsSmallerVolFactorSwingThanHighVega()
	{
		// Two long-vega positions in a rich-IV environment. The fatter-vega one (DC-like, +$3) gets
		// cut from 0.010 to 0.005; the thinner-vega one (DD-like, +$1) gets cut to ~0.00833.
		var rich = 0.60m;
		var hv = 0.30m;
		var weight = 0.50m;
		var heavyVega = CandidateScorer.VolatilityAdjust(0.010m, 3m, rich, hv, weight);
		var lightVega = CandidateScorer.VolatilityAdjust(0.010m, 1m, rich, hv, weight);

		Assert.True(heavyVega < lightVega, $"expected heavier-vega cut to be sharper; heavy={heavyVega} light={lightVega}");
		Assert.Equal(0.005m, heavyVega);
		Assert.Equal(0.0083333333333333333333333333m, lightVega, 12);
	}

	[Fact]
	public void BalanceFactorUsesTemperedRiskRewardAndDebitPenalty()
	{
		var factor = CandidateScorer.BalanceFactor(maxProfit: 100m, maxLoss: -50m, premiumRatio: 4m);

		// R/R 2 (linear at the default RrExponent 1.0) ÷ sqrt(premiumRatio 4) = 2 / 2 = 1.0
		Assert.Equal(1.0m, factor, 12);
	}

	[Fact]
	public void BalanceFactorDoesNotPenalizeCreditStructureForSubOnePremiumRatio()
	{
		var factor = CandidateScorer.BalanceFactor(maxProfit: 50m, maxLoss: -100m, premiumRatio: 0.25m);

		// R/R 0.5 (linear at the default RrExponent 1.0) ÷ sqrt(max(1, premiumRatio 0.25)) = 0.5 / 1 = 0.5
		// — a sub-1 premium ratio is floored to 1, so the credit structure takes no debit penalty.
		Assert.Equal(0.5m, factor, 12);
	}

	[Fact]
	public void ThetaFactorIsBoundedAndRiskNormalized()
	{
		var factor = CandidateScorer.ComputeThetaFactor(thetaPerDayPerContract: 8m, capitalAtRiskPerContract: 20m);

		Assert.Equal(1.25m, factor);
	}

	[Fact]
	public void ThetaFactorDoesNotBoostNegativeTheta()
	{
		var factor = CandidateScorer.ComputeThetaFactor(thetaPerDayPerContract: -2m, capitalAtRiskPerContract: 100m);

		Assert.Equal(1m, factor);
	}

	[Fact]
	public void ProbabilityFactorHeavilyPenalizesRouletteLikePop()
	{
		var lowPop = CandidateScorer.ComputeProbabilityFactor(0.17m);
		var healthyPop = CandidateScorer.ComputeProbabilityFactor(0.583m);

		Assert.True(lowPop < 0.02m);
		Assert.Equal(1.25m, healthyPop);
		Assert.True(healthyPop > lowPop * 50m);
	}

	[Fact]
	public void BalanceFactorCapsExtremeRiskReward()
	{
		var factor = CandidateScorer.BalanceFactor(maxProfit: 1000m, maxLoss: -10m, premiumRatio: 0.25m);

		Assert.Equal(1.25m, factor);
	}

	[Fact]
	public void CapitalScaleFactorPenalizesTinyRiskStructures()
	{
		var tinyRisk = CandidateScorer.ComputeCapitalScaleFactor(47m);
		var largerRisk = CandidateScorer.ComputeCapitalScaleFactor(300m);

		Assert.Equal(0.565444861287520m, tinyRisk, 12);
		Assert.True(largerRisk > tinyRisk);
		Assert.True(largerRisk < 1m);
	}

	[Fact]
	public void SetupFactorPenalizesNeutralTradeWhenSpotHugsBreakeven()
	{
		// Spot 25.09 sits ~12¢ above the lower BE in a ~$2 band — extreme edge case.
		// With the arithmetic-mean formula, the worst penalty is bounded but the factor still
		// drops well below a centered trade (which reaches 1.0). Anything below ~0.35 means the
		// trade is structurally on the wrong side of the band.
		var factor = CandidateScorer.ComputeSetupFactor(OpenStructureKind.IronCondor, 25.09m, new[] { 24.97m, 27.03m });

		Assert.NotNull(factor);
		Assert.True(factor.Value < 0.35m, $"expected setup factor < 0.35 for hugging-breakeven case, got {factor.Value}");
	}

	[Fact]
	public void SetupFactorDampensRatioBetweenWideCenteredAndNarrowOffCenter()
	{
		// Wide centered band (DC-shape): spot dead-center in a $2.51-wide profit zone.
		var wideCentered = CandidateScorer.ComputeSetupFactor(OpenStructureKind.DoubleCalendar, 24.595m, new[] { 23.34m, 25.85m });
		// Narrower band offset from spot (single LC at OTM call strike): zone is $2.81 wide but centered at 25.115, ~0.5 above spot.
		var narrowOffCenter = CandidateScorer.ComputeSetupFactor(OpenStructureKind.LongCalendar, 24.60m, new[] { 23.71m, 26.52m });

		Assert.NotNull(wideCentered);
		Assert.NotNull(narrowOffCenter);
		// Wide+centered should still win, but the ratio under the arithmetic-mean formula must be
		// modest (<1.8x). The previous multiplicative formula made it ~2x, which let setup
		// reverse meaningful capital-efficiency differences in raw score.
		var ratio = wideCentered.Value / narrowOffCenter.Value;
		Assert.True(ratio > 1m && ratio < 1.8m, $"expected setup ratio in (1.0, 1.8), got {ratio:F2}");
	}

	[Fact]
	public void SetupFactorRewardsCenteredNeutralTrade()
	{
		var factor = CandidateScorer.ComputeSetupFactor(OpenStructureKind.IronCondor, 26.00m, new[] { 24.97m, 27.03m });

		Assert.NotNull(factor);
		Assert.Equal(1m, factor.Value);
	}

	[Fact]
	public void AdjustmentRunwayFactorRewardsResidualLongExtrinsicAfterTargetExpiry()
	{
		var shortExp = new DateTime(2026, 5, 1);
		var longExp = new DateTime(2026, 5, 29);
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol("GME", shortExp, 25m, "P"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol("GME", longExp, 25m, "P"), 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[MatchKeys.OccSymbol("GME", shortExp, 25m, "P")] = TestQuote.Q(0.32m, 0.36m, 0.42m),
			[MatchKeys.OccSymbol("GME", longExp, 25m, "P")] = TestQuote.Q(0.95m, 1.03m, 0.41m)
		};

		var factor = CandidateScorer.ComputeAdjustmentRunwayFactor(skel, new DateTime(2026, 4, 29), spot: 25.09m, quotes);

		Assert.NotNull(factor);
		Assert.True(factor.Value > 1m);
	}

	[Fact]
	public void AdjustmentRunwayFactorIsNullWhenAllLegsExpireAtTarget()
	{
		var expiry = new DateTime(2026, 5, 1);
		var skel = new CandidateSkeleton("GME", OpenStructureKind.IronCondor, new[]
		{
			new ProposalLeg("buy", MatchKeys.OccSymbol("GME", expiry, 24m, "P"), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol("GME", expiry, 24.5m, "P"), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol("GME", expiry, 25.5m, "C"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol("GME", expiry, 26m, "C"), 1)
		}, TargetExpiry: expiry);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[MatchKeys.OccSymbol("GME", expiry, 24m, "P")] = TestQuote.Q(0.05m, 0.06m, 0.43m),
			[MatchKeys.OccSymbol("GME", expiry, 24.5m, "P")] = TestQuote.Q(0.13m, 0.14m, 0.40m),
			[MatchKeys.OccSymbol("GME", expiry, 25.5m, "C")] = TestQuote.Q(0.24m, 0.26m, 0.46m),
			[MatchKeys.OccSymbol("GME", expiry, 26m, "C")] = TestQuote.Q(0.14m, 0.16m, 0.51m)
		};

		var factor = CandidateScorer.ComputeAdjustmentRunwayFactor(skel, new DateTime(2026, 4, 29), spot: 25.09m, quotes);

		Assert.Null(factor);
	}

	[Fact]
	public void AssignmentRiskFactorCutsNearMoneyShortRisk()
	{
		var expiry = new DateTime(2026, 5, 1);
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.IronButterfly, new[]
		{
			new ProposalLeg("buy", MatchKeys.OccSymbol("SPY", expiry, 495m, "P"), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol("SPY", expiry, 500m, "P"), 1),
			new ProposalLeg("sell", MatchKeys.OccSymbol("SPY", expiry, 500m, "C"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol("SPY", expiry, 505m, "C"), 1)
		}, TargetExpiry: expiry);

		var factor = CandidateScorer.ComputeAssignmentRiskFactor(skel, spot: 500m, asOf: new DateTime(2026, 4, 28), strikeStep: 1m, technicalBias: 0m);

		Assert.NotNull(factor);
		Assert.True(factor.Value < 1m);
	}

	[Fact]
	public void CalendarMaxPainBoostsWhenPullSupportsShortSideOfSpot()
	{
		var shortExp = new DateTime(2026, 5, 1);
		var longExp = new DateTime(2026, 5, 29);
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 24.5m, "P");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 24.5m, "P");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);

		var signal = CandidateScorer.ComputeMaxPainSignal(
			skel,
			spot: 25.20m,
			maxPain: 23.00m,
			expectedMove: 2.00m,
			breakevens: new[] { 23.33m, 25.91m });

		Assert.True(signal > 0m);
	}

	[Fact]
	public void CalendarMaxPainCutsWhenPullIsOppositeShortSideOfSpot()
	{
		var shortExp = new DateTime(2026, 5, 1);
		var longExp = new DateTime(2026, 5, 29);
		var shortSym = MatchKeys.OccSymbol("GME", shortExp, 24.5m, "P");
		var longSym = MatchKeys.OccSymbol("GME", longExp, 24.5m, "P");
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongCalendar, new[]
		{
			new ProposalLeg("sell", shortSym, 1),
			new ProposalLeg("buy", longSym, 1)
		}, TargetExpiry: shortExp);

		var signal = CandidateScorer.ComputeMaxPainSignal(
			skel,
			spot: 25.20m,
			maxPain: 26.20m,
			expectedMove: 2.00m,
			breakevens: new[] { 23.33m, 25.91m });

		Assert.True(signal < 0m);
	}
}
