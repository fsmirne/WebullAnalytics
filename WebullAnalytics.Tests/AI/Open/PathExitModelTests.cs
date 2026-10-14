using WebullAnalytics.AI;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class PathExitModelTests
{
	private static readonly DateTime AsOf = new(2026, 10, 14, 9, 30, 0);
	private static readonly OptionParsed Call = ParsingHelpers.ParseOptionSymbol("SPY261113C00775000")!;
	private const decimal Spot = 775m, Iv = 0.15m;

	private static (decimal Ev, decimal Premium) LongCallEv(decimal? takeProfit, decimal? stopLoss)
	{
		var target = Call.ExpiryDate.Date + OptionMath.MarketClose;
		var years = (target - AsOf).TotalDays / 365.0;
		var premium = OptionMath.BlackScholes(Spot, Call.Strike, years, OptionMath.RiskFreeRate, Iv, "C") * 100m;
		var sigmaTotal = (double)Iv * Math.Sqrt(years);
		var forward = Spot * (decimal)Math.Exp(OptionMath.RiskFreeRate * years);
		var center = CandidateScorer.ScenarioCenter(forward, sigmaTotal, 0);
		const int steps = 24;
		var stepTimes = Enumerable.Range(1, steps).Select(k => AsOf + (target - AsOf) * ((double)k / steps)).ToList();
		decimal Terminal(decimal s) => Math.Max(0m, s - Call.Strike) * 100m - premium;
		var ev = PathExitModel.ExpectedPnl([new PathLeg(Call, Iv, true, 1)], premium, Terminal, Spot, center, sigmaTotal, stepTimes, takeProfit, stopLoss, dividends: null);
		return (ev, premium);
	}

	[Fact]
	public void FairPricedOption_HasNoEdge_WithOrWithoutExits()
	{
		// Optional stopping: under the distribution the option is priced on, no take-profit or stop can manufacture EV.
		// (Undiscounted, the fair EV is the premium's carry, premium·(e^{rT} − 1) — a few dollars.)
		foreach (var (tp, sl) in new (decimal?, decimal?)[] { (null, null), (null, 300m), (200m, null), (150m, 250m) })
		{
			var (ev, premium) = LongCallEv(tp, sl);
			Assert.InRange((double)ev, -0.02 * (double)premium, 0.02 * (double)premium);
		}
	}

	[Fact]
	public void Stop_IsNotAFreePut()
	{
		// The old terminal-only model floored every end-of-life loss at the stop, which is strictly better than reality:
		// paths that touch the stop and recover are stopped losses, not recoveries. The path EV must sit below it.
		var (pathEv, premium) = LongCallEv(takeProfit: null, stopLoss: 300m);
		var target = Call.ExpiryDate.Date + OptionMath.MarketClose;
		var years = (target - AsOf).TotalDays / 365.0;
		var grid = CandidateScorer.BuildScenarioGrid(Spot * (decimal)Math.Exp(OptionMath.RiskFreeRate * years), Iv, years);
		var flooredEv = grid.Sum(p => p.Weight * Math.Max(Math.Max(0m, p.SpotAtExpiry - Call.Strike) * 100m - premium, -300m));
		Assert.True(pathEv < flooredEv - 50m, $"path EV {pathEv:F2} should sit well below the terminal-floor EV {flooredEv:F2}");
	}
}
