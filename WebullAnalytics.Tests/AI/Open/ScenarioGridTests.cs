using WebullAnalytics.AI;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class ScenarioGridTests
{
	[Fact]
	public void WeightsSumToOne()
	{
		var grid = CandidateScorer.BuildScenarioGrid(forward: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
		var sum = grid.Sum(p => p.Weight);
		Assert.InRange((double)sum, 0.999999, 1.000001);
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
	public void TailMassBeyondOneSigmaMatchesTheNormal()
	{
		// The former 5-point grid stopped at ±1σ, so nothing ever landed beyond it; a normal puts ~31.7% there.
		const double iv = 0.20, years = 0.25;
		var sigma = iv * Math.Sqrt(years);
		var grid = CandidateScorer.BuildScenarioGrid(forward: 100m, ivAnnual: (decimal)iv, years: years);
		var median = Math.Exp(Math.Log(100.0) - sigma * sigma / 2.0);
		var tail = grid.Where(p => Math.Abs(Math.Log((double)p.SpotAtExpiry / median)) > sigma).Sum(p => (double)p.Weight);
		// Tolerance = the mass of the bin straddling ±1σ (its node sits just inside, so it counts as "inside"): ~0.015 per side.
		Assert.InRange(tail, 0.285, 0.335);
	}

	[Theory]
	[InlineData(100.0)]
	[InlineData(110.0)]
	[InlineData(125.0)]
	public void CallPayoffExpectationMatchesBlackScholesForwardValue(double strike)
	{
		// EV of (S_T − K)+ over the grid must equal the undiscounted Black-Scholes call — including a 2.5σ-OTM strike
		// that the old ±1σ grid valued at zero.
		const double r = 0.04, iv = 0.20, years = 0.25;
		var spot = 100m;
		var forward = spot * (decimal)Math.Exp(r * years);
		var grid = CandidateScorer.BuildScenarioGrid(forward, (decimal)iv, years);
		var ev = grid.Sum(p => (double)p.Weight * Math.Max(0.0, (double)p.SpotAtExpiry - strike));
		var bsForwardValue = (double)OptionMath.BlackScholes(spot, (decimal)strike, years, r, (decimal)iv, "C") * Math.Exp(r * years);
		Assert.InRange(ev, bsForwardValue * 0.99 - 0.002, bsForwardValue * 1.01 + 0.002);
	}

	[Fact]
	public void ExpectedMoveUsesTheSameClockAsTheIv()
	{
		// 0DTE: 5.4h left on the calendar-hour clock the IV was solved on → σ√T ≈ $29 on SPX, not the full-day ~$74 the
		// old trading-day floor produced.
		var years = 5.4 / 24.0 / 365.0;
		var move = CandidateScorer.ExpectedMove(7754m, 0.151m, years);
		Assert.InRange((double)move, 28.5, 29.5);
	}
}
