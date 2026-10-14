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
	public void DisabledStop_GivesNoStopThreshold()
	{
		var c = Config(stopEnabled: false);
		AIConfigLoader.PopulateRealizedEv(c);
		Assert.Null(RealizedExpectancy.StopLossPerContract(maxProfit: 137m, maxLoss: -63m, c.Opener.RealizedExpectancy));
	}

	[Fact]
	public void TakeProfit_IsCopiedOnlyWhenTheRuleIsEnabled()
	{
		var c = Config(stopEnabled: false);
		c.Rules.TakeProfit.Enabled = true;
		c.Rules.TakeProfit.ProfitTargetPctOfPremium = 0.20m;
		AIConfigLoader.PopulateRealizedEv(c);
		Assert.Equal(0.20m, c.Opener.RealizedExpectancy.TakeProfitPctOfPremium);

		c.Rules.TakeProfit.Enabled = false;
		AIConfigLoader.PopulateRealizedEv(c);
		Assert.Equal(0m, c.Opener.RealizedExpectancy.TakeProfitPctOfPremium);
	}
}
