using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class ShortLegLowExtrinsicRuleTests
{
	[Fact]
	public void FiresWhenShortDteZeroAndExtrinsicBelowThreshold()
	{
		var hit = new ShortLegLowExtrinsicRule().TryEvaluate(RuleTestFacts.Default(shortLegDteMin: 0, shortLegExtrinsic: 0.07m));
		Assert.NotNull(hit);
		Assert.Equal("short_leg_low_extrinsic", hit!.Id);
		Assert.Equal(0m, hit.Inputs["short_dte"]);
		Assert.Equal(0.07m, hit.Inputs["short_extrinsic"]);
		Assert.Equal(2m, hit.Inputs["threshold_dte"]);
		Assert.Equal(0.30m, hit.Inputs["threshold_extrinsic"]);
	}

	[Fact]
	public void DoesNotFireWhenDteAboveThreshold()
	{
		var hit = new ShortLegLowExtrinsicRule().TryEvaluate(RuleTestFacts.Default(shortLegDteMin: 3, shortLegExtrinsic: 0.07m));
		Assert.Null(hit);
	}

	[Fact]
	public void DoesNotFireWhenExtrinsicAboveThreshold()
	{
		var hit = new ShortLegLowExtrinsicRule().TryEvaluate(RuleTestFacts.Default(shortLegDteMin: 1, shortLegExtrinsic: 0.50m));
		Assert.Null(hit);
	}

	[Fact]
	public void DoesNotFireWhenNoShortLeg()
	{
		var hit = new ShortLegLowExtrinsicRule().TryEvaluate(RuleTestFacts.Default(shortLegDteMin: 0, shortLegExtrinsic: 0.05m, hasShortLeg: false));
		Assert.Null(hit);
	}
}
