using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class ScenarioGridTests
{
	[Fact]
	public void GridHasFivePoints()
	{
		var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		Assert.Equal(5, grid.Count);
	}

	[Fact]
	public void WeightsSumToOne()
	{
		var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		var sum = grid.Sum(p => p.Weight);
		Assert.InRange((double)sum, 0.999, 1.001);
	}

	[Fact]
	public void MiddlePointEqualsSpot()
	{
		var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		var mid = grid[2];
		Assert.Equal(100m, mid.SpotAtExpiry);
	}

	[Fact]
	public void EndpointsScaleWithSigmaRange()
	{
		var spot = 100m;
		var iv = 0.40m;
		var years = 30.0 / 365.0;
		var sigma = (decimal)((double)iv * Math.Sqrt(years));

		// Default sigmaRange = 1.0 → endpoints at spot·e^(±1σ)
		var defaultGrid = CandidateScorer.BuildScenarioGrid(spot, iv, years);
		var defaultLo = spot * (decimal)Math.Exp((double)(-sigma));
		var defaultHi = spot * (decimal)Math.Exp((double)sigma);
		Assert.InRange((double)defaultGrid[0].SpotAtExpiry, (double)(defaultLo - 0.01m), (double)(defaultLo + 0.01m));
		Assert.InRange((double)defaultGrid[4].SpotAtExpiry, (double)(defaultHi - 0.01m), (double)(defaultHi + 0.01m));

		// Explicit sigmaRange = 2.0 → endpoints at spot·e^(±2σ) (matches pre-tuning behavior)
		var wideGrid = CandidateScorer.BuildScenarioGrid(spot, iv, years, sigmaRange: 2.0m);
		var wideLo = spot * (decimal)Math.Exp((double)(-2m * sigma));
		var wideHi = spot * (decimal)Math.Exp((double)(2m * sigma));
		Assert.InRange((double)wideGrid[0].SpotAtExpiry, (double)(wideLo - 0.01m), (double)(wideLo + 0.01m));
		Assert.InRange((double)wideGrid[4].SpotAtExpiry, (double)(wideHi - 0.01m), (double)(wideHi + 0.01m));
	}

	[Fact]
	public void InnerPointsAreHalfTheEndpoint()
	{
		var spot = 100m;
		var iv = 0.40m;
		var years = 30.0 / 365.0;
		var sigma = (decimal)((double)iv * Math.Sqrt(years));
		var grid = CandidateScorer.BuildScenarioGrid(spot, iv, years, sigmaRange: 1.0m);

		// grid[1] and grid[3] are at ±0.5σ
		var innerLo = spot * (decimal)Math.Exp((double)(-0.5m * sigma));
		var innerHi = spot * (decimal)Math.Exp((double)(0.5m * sigma));
		Assert.InRange((double)grid[1].SpotAtExpiry, (double)(innerLo - 0.01m), (double)(innerLo + 0.01m));
		Assert.InRange((double)grid[3].SpotAtExpiry, (double)(innerHi - 0.01m), (double)(innerHi + 0.01m));
	}
}
