using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerSentimentTests
{
	[Fact]
	public void NullScoreYieldsNullFactor()
	{
		Assert.Null(CandidateScorer.ComputeSentimentFactor(sentimentScore: null, directionalFit: 1, weight: 0.15m));
	}

	[Fact]
	public void ZeroWeightYieldsNullFactor()
	{
		Assert.Null(CandidateScorer.ComputeSentimentFactor(sentimentScore: 80m, directionalFit: 1, weight: 0m));
	}

	[Fact]
	public void NeutralFitYieldsUnitFactorRegardlessOfScore()
	{
		Assert.Equal(1m, CandidateScorer.ComputeSentimentFactor(sentimentScore: 5m, directionalFit: 0, weight: 0.15m));
		Assert.Equal(1m, CandidateScorer.ComputeSentimentFactor(sentimentScore: 95m, directionalFit: 0, weight: 0.15m));
	}

	[Fact]
	public void NeutralScoreYieldsUnitFactor()
	{
		// score = 50 → bias = 0 → factor = 1 + 0.15 × 0 × ±1 = 1
		Assert.Equal(1m, CandidateScorer.ComputeSentimentFactor(sentimentScore: 50m, directionalFit: 1, weight: 0.15m));
		Assert.Equal(1m, CandidateScorer.ComputeSentimentFactor(sentimentScore: 50m, directionalFit: -1, weight: 0.15m));
	}

	[Fact]
	public void ExtremeGreedDampensBullishFit()
	{
		// score = 100 → bias = (50 - 100) / 50 = -1 → factor = 1 + 0.15 × -1 × +1 = 0.85
		var f = CandidateScorer.ComputeSentimentFactor(sentimentScore: 100m, directionalFit: 1, weight: 0.15m);
		Assert.Equal(0.85m, f);
	}

	[Fact]
	public void ExtremeGreedBoostsBearishFit()
	{
		// score = 100, fit = -1 → 1 + 0.15 × -1 × -1 = 1.15
		var f = CandidateScorer.ComputeSentimentFactor(sentimentScore: 100m, directionalFit: -1, weight: 0.15m);
		Assert.Equal(1.15m, f);
	}

	[Fact]
	public void ExtremeFearBoostsBullishFit()
	{
		// score = 0 → bias = +1 → factor = 1 + 0.15 × 1 × 1 = 1.15
		var f = CandidateScorer.ComputeSentimentFactor(sentimentScore: 0m, directionalFit: 1, weight: 0.15m);
		Assert.Equal(1.15m, f);
	}

	[Fact]
	public void ExtremeFearDampensBearishFit()
	{
		var f = CandidateScorer.ComputeSentimentFactor(sentimentScore: 0m, directionalFit: -1, weight: 0.15m);
		Assert.Equal(0.85m, f);
	}

	[Fact]
	public void FactorRespectsWeightScaling()
	{
		// score = 100, fit = +1, weight = 0.30 → 1 + 0.30 × -1 × 1 = 0.70
		var f = CandidateScorer.ComputeSentimentFactor(sentimentScore: 100m, directionalFit: 1, weight: 0.30m);
		Assert.Equal(0.70m, f);
	}

	[Fact]
	public void FactorClampedAtFloor()
	{
		// Pathological combo: weight 1.5 × bias -1 × fit +1 = -1.5 → clamped to 0.10 floor
		var f = CandidateScorer.ComputeSentimentFactor(sentimentScore: 100m, directionalFit: 1, weight: 1.5m);
		Assert.Equal(0.10m, f);
	}

	[Fact]
	public void ScoreClampedAtBounds()
	{
		// Out-of-band score 120 should clamp to 100
		var f1 = CandidateScorer.ComputeSentimentFactor(sentimentScore: 120m, directionalFit: 1, weight: 0.15m);
		var f2 = CandidateScorer.ComputeSentimentFactor(sentimentScore: 100m, directionalFit: 1, weight: 0.15m);
		Assert.Equal(f1, f2);
	}

	[Fact]
	public void SentimentAdjustReturnsScoreUnchangedWhenFactorIsNull()
	{
		Assert.Equal(0.05m, CandidateScorer.SentimentAdjust(0.05m, factor: null));
	}

	[Fact]
	public void SentimentAdjustMultipliesByFactor()
	{
		Assert.Equal(0.0425m, CandidateScorer.SentimentAdjust(0.05m, factor: 0.85m));
	}
}
