using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class ScenarioGridTests
{
	[Fact]
	public void GridHasFivePoints()
	{
		var grid = CandidateScorer.BuildScenarioGrid(forward: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		Assert.Equal(5, grid.Count);
	}

	[Fact]
	public void WeightsSumToOne()
	{
		var grid = CandidateScorer.BuildScenarioGrid(forward: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		var sum = grid.Sum(p => p.Weight);
		Assert.InRange((double)sum, 0.999, 1.001);
	}

	[Fact]
	public void WeightedMeanEqualsForward()
	{
		// Neutral drift: the probability-weighted mean of the unshifted grid is exactly the forward the legs are priced on.
		var grid = CandidateScorer.BuildScenarioGrid(forward: 100.50m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		var mean = grid.Sum(p => p.Weight * p.SpotAtExpiry);
		Assert.InRange((double)mean, 100.4999, 100.5001);
	}

	[Fact]
	public void BiasShiftMovesTheMean()
	{
		var neutral = CandidateScorer.BuildScenarioGrid(forward: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		var bullish = CandidateScorer.BuildScenarioGrid(forward: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0, biasShiftSigmas: 0.5m);
		Assert.True(bullish.Sum(p => p.Weight * p.SpotAtExpiry) > neutral.Sum(p => p.Weight * p.SpotAtExpiry));
	}

	[Fact]
	public void EndpointsScaleWithSigmaRange()
	{
		var iv = 0.40m;
		var years = 30.0 / 365.0;
		var sigma = (double)iv * Math.Sqrt(years);

		// Default sigmaRange = 1.0 → endpoints at center·e^(±1σ)
		var defaultGrid = CandidateScorer.BuildScenarioGrid(100m, iv, years);
		var center = (double)defaultGrid[2].SpotAtExpiry;
		Assert.InRange((double)defaultGrid[0].SpotAtExpiry, center * Math.Exp(-sigma) - 0.01, center * Math.Exp(-sigma) + 0.01);
		Assert.InRange((double)defaultGrid[4].SpotAtExpiry, center * Math.Exp(sigma) - 0.01, center * Math.Exp(sigma) + 0.01);

		// Explicit sigmaRange = 2.0 → endpoints at center·e^(±2σ)
		var wideGrid = CandidateScorer.BuildScenarioGrid(100m, iv, years, sigmaRange: 2.0m);
		var wideCenter = (double)wideGrid[2].SpotAtExpiry;
		Assert.InRange((double)wideGrid[0].SpotAtExpiry, wideCenter * Math.Exp(-2 * sigma) - 0.01, wideCenter * Math.Exp(-2 * sigma) + 0.01);
		Assert.InRange((double)wideGrid[4].SpotAtExpiry, wideCenter * Math.Exp(2 * sigma) - 0.01, wideCenter * Math.Exp(2 * sigma) + 0.01);
	}

	[Fact]
	public void InnerPointsAreHalfTheEndpoint()
	{
		var iv = 0.40m;
		var years = 30.0 / 365.0;
		var sigma = (double)iv * Math.Sqrt(years);
		var grid = CandidateScorer.BuildScenarioGrid(100m, iv, years, sigmaRange: 1.0m);
		var center = (double)grid[2].SpotAtExpiry;

		// grid[1] and grid[3] are at ±0.5σ around the center
		Assert.InRange((double)grid[1].SpotAtExpiry, center * Math.Exp(-0.5 * sigma) - 0.01, center * Math.Exp(-0.5 * sigma) + 0.01);
		Assert.InRange((double)grid[3].SpotAtExpiry, center * Math.Exp(0.5 * sigma) - 0.01, center * Math.Exp(0.5 * sigma) + 0.01);
	}
}
