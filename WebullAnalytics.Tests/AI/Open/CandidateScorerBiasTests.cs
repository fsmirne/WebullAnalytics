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
        // EV 100, days 0 → clamped to 1; capitalAtRisk 200 → 100 / 1 / 200 = 0.5
		Assert.Equal(0.5m, CandidateScorer.ComputeRawScore(ev: 100m, daysToTarget: 0, capitalAtRisk: 200m));
	}

	[Fact]
	public void RawScoreZeroWhenCapitalAtRiskZero()
	{
		Assert.Equal(0m, CandidateScorer.ComputeRawScore(ev: 50m, daysToTarget: 5, capitalAtRisk: 0m));
	}

	[Fact]
	public void HighIvRelativeToHvBoostsShortPremiumStructures()
	{
		var adjusted = CandidateScorer.VolatilityAdjust(
			score: 0.010m,
			kind: OpenStructureKind.ShortPutVertical,
			ivAnnual: 0.60m,
			historicalVolAnnual: 0.30m,
			weight: 0.50m);

		Assert.Equal(0.015m, adjusted);
	}

	[Fact]
	public void HighIvRelativeToHvCutsLongPremiumStructures()
	{
		var adjusted = CandidateScorer.VolatilityAdjust(
			score: 0.010m,
			kind: OpenStructureKind.LongCall,
			ivAnnual: 0.60m,
			historicalVolAnnual: 0.30m,
			weight: 0.50m);

		Assert.Equal(0.005m, adjusted);
	}

	[Fact]
	public void BalanceFactorUsesTemperedRiskRewardAndDebitPenalty()
	{
		var factor = CandidateScorer.BalanceFactor(maxProfit: 100m, maxLoss: -50m, premiumRatio: 4m);

		Assert.Equal(0.7071067811865475244008443621m, factor, 12);
	}

	[Fact]
	public void BalanceFactorDoesNotPenalizeCreditStructureForSubOnePremiumRatio()
	{
		var factor = CandidateScorer.BalanceFactor(maxProfit: 50m, maxLoss: -100m, premiumRatio: 0.25m);

		Assert.Equal(0.7071067811865475244008443621m, factor, 12);
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
		var factor = CandidateScorer.ComputeSetupFactor(OpenStructureKind.IronCondor, 25.09m, new[] { 24.97m, 27.03m });

		Assert.NotNull(factor);
		Assert.True(factor.Value < 0.10m);
	}

	[Fact]
	public void SetupFactorRewardsCenteredNeutralTrade()
	{
		var factor = CandidateScorer.ComputeSetupFactor(OpenStructureKind.IronCondor, 26.00m, new[] { 24.97m, 27.03m });

		Assert.NotNull(factor);
		Assert.Equal(1m, factor.Value);
	}

	[Fact]
	public void DiagonalGeometryFactorRewardsDiagonalWhenFrontWeekCollectsMeaningfulRent()
	{
		var shortExp = new DateTime(2026, 5, 8);
		var longExp = new DateTime(2026, 5, 29);
		var skel = new CandidateSkeleton("GME", OpenStructureKind.LongDiagonal, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol("GME", shortExp, 24.5m, "P"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol("GME", longExp, 24m, "P"), 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[MatchKeys.OccSymbol("GME", shortExp, 24.5m, "P")] = TestQuote.Q(0.34m, 0.40m, 0.38m),
			[MatchKeys.OccSymbol("GME", longExp, 24m, "P")] = TestQuote.Q(0.50m, 0.64m, 0.41m)
		};

		var factor = CandidateScorer.ComputeDiagonalGeometryFactor(skel, quotes, "mid");

		Assert.NotNull(factor);
		Assert.True(factor.Value > 0.80m);
	}

	[Fact]
	public void DiagonalGeometryFactorCutsDiagonalWhenFrontWeekRentIsWeak()
	{
		var shortExp = new DateTime(2026, 5, 8);
		var longExp = new DateTime(2026, 5, 29);
		var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongDiagonal, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol("SPY", shortExp, 510m, "C"), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol("SPY", longExp, 505m, "C"), 1)
		}, TargetExpiry: shortExp);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[MatchKeys.OccSymbol("SPY", shortExp, 510m, "C")] = TestQuote.Q(0.10m, 0.14m, 0.25m),
			[MatchKeys.OccSymbol("SPY", longExp, 505m, "C")] = TestQuote.Q(1.35m, 1.45m, 0.27m)
		};

		var factor = CandidateScorer.ComputeDiagonalGeometryFactor(skel, quotes, "mid");

		Assert.NotNull(factor);
		Assert.True(factor.Value < 0.65m);
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
