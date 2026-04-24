using Xunit;
using WebullAnalytics.AI;

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
    public void EndpointsAreSpotTimesExpPlusMinus2Sigma()
    {
        var spot = 100m;
        var iv = 0.40m;
        var years = 30.0 / 365.0;
        var sigma = (decimal)((double)iv * Math.Sqrt(years));
        var grid = CandidateScorer.BuildScenarioGrid(spot, iv, years);
        var expected_lo = spot * (decimal)Math.Exp((double)(-2m * sigma));
        var expected_hi = spot * (decimal)Math.Exp((double)(+2m * sigma));
        Assert.InRange((double)grid[0].SpotAtExpiry, (double)(expected_lo - 0.01m), (double)(expected_lo + 0.01m));
        Assert.InRange((double)grid[4].SpotAtExpiry, (double)(expected_hi - 0.01m), (double)(expected_hi + 0.01m));
    }
}
