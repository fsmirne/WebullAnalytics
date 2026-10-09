using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

/// <summary>Locks the realized-EV stop to the stop that will actually be taken: a disabled rules.stopLoss must not hand the
/// scorer a loss floor (SPY DC2 / SPY 0DTE ranked candidates against a 50%/60% stop that was never placed until 2026-10-09).</summary>
public class RealizedEvPopulationTests
{
	private static AIConfig Config(bool stopEnabled)
	{
		var c = new AIConfig();
		c.Opener.RealizedEvScoring = true;
		c.Rules.StopLoss.Enabled = stopEnabled;
		c.Rules.StopLoss.PctOfMaxLoss = 0.6m;
		c.Rules.StopLoss.PctOfMaxProfit = 0.25m;
		return c;
	}

	[Fact]
	public void DisabledStop_GivesTheScorerNoStop()
	{
		var c = Config(stopEnabled: false);
		AIConfigLoader.PopulateRealizedEv(c);
		Assert.Equal(1m, c.Opener.RealizedExpectancy.StopLossPctOfMaxLoss);
		Assert.Equal(0m, c.Opener.RealizedExpectancy.StopLossPctOfMaxProfit);
	}

	[Fact]
	public void EnabledStop_IsCopiedThrough()
	{
		var c = Config(stopEnabled: true);
		AIConfigLoader.PopulateRealizedEv(c);
		Assert.Equal(0.6m, c.Opener.RealizedExpectancy.StopLossPctOfMaxLoss);
		Assert.Equal(0.25m, c.Opener.RealizedExpectancy.StopLossPctOfMaxProfit);
	}

	[Fact]
	public void DisabledStop_RealizedEvIsHoldToExpiryMinusFriction()
	{
		// The 2026-10-09 SPY 0DTE put debit spread shape: −$63 max loss, +$137 max profit. Two terminal scenarios.
		var grid = new[] { new CandidateScorer.ScenarioPoint(770m, 0.4m), new CandidateScorer.ScenarioPoint(780m, 0.6m) };
		decimal Pnl(decimal s) => s < 775m ? 137m : -63m;
		var c = Config(stopEnabled: false);
		AIConfigLoader.PopulateRealizedEv(c);
		var ev = RealizedExpectancy.RealizeEv(grid, Pnl, maxLoss: -63m, frictionPerContract: 2m, c.Opener.RealizedExpectancy);
		Assert.Equal(0.4m * 137m + 0.6m * -63m - 2m, ev);   // no 60% floor on the losing scenario
	}
}
