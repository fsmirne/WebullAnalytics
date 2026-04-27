using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class ShortExpiresBeforeLongRuleTests
{
	[Fact]
	public void FiresWhenShortDteLessThanLong()
	{
		var hit = new ShortExpiresBeforeLongRule().TryEvaluate(RuleTestFacts.Default(
			shortLegDteMin: 0, longLegDteMax: 7, dteGapDays: 7,
			hasShortLeg: true, hasLongLeg: true, netDeltaPostShort: 0.60m));
		Assert.NotNull(hit);
		Assert.Equal("short_expires_before_long", hit!.Id);
		Assert.Equal(0m, hit.Inputs["short_dte"]);
		Assert.Equal(7m, hit.Inputs["long_dte"]);
		Assert.Equal(7m, hit.Inputs["dte_gap"]);
		Assert.Equal(0.60m, hit.Inputs["net_delta_post_short"]);
	}

	[Fact]
	public void DoesNotFireWhenDtesEqual()
	{
		Assert.Null(new ShortExpiresBeforeLongRule().TryEvaluate(RuleTestFacts.Default(
			shortLegDteMin: 7, longLegDteMax: 7, dteGapDays: 0)));
	}

	[Fact]
	public void DoesNotFireWithoutBothLegs()
	{
		Assert.Null(new ShortExpiresBeforeLongRule().TryEvaluate(RuleTestFacts.Default(hasShortLeg: false)));
		Assert.Null(new ShortExpiresBeforeLongRule().TryEvaluate(RuleTestFacts.Default(hasLongLeg: false)));
	}
}
