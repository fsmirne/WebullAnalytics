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
