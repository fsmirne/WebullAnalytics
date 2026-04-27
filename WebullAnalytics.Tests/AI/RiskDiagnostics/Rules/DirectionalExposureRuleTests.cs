using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class DirectionalExposureRuleTests
{
	[Fact]
	public void FiresWhenAbsDeltaAboveThreshold()
	{
		var hit = new DirectionalExposureRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.35m, directionalBias: "bullish"));
		Assert.NotNull(hit);
		Assert.Equal("directional_exposure", hit!.Id);
		Assert.Equal(0.35m, hit.Inputs["net_delta"]);
		Assert.Equal(0.25m, hit.Inputs["threshold"]);
	}

	[Fact]
	public void FiresForNegativeDelta()
	{
		var hit = new DirectionalExposureRule().TryEvaluate(RuleTestFacts.Default(netDelta: -0.40m, directionalBias: "bearish"));
		Assert.NotNull(hit);
		Assert.Equal(-0.40m, hit!.Inputs["net_delta"]);
	}

	[Fact]
	public void DoesNotFireAtOrBelowThreshold()
	{
		Assert.Null(new DirectionalExposureRule().TryEvaluate(RuleTestFacts.Default(netDelta: 0.25m)));
		Assert.Null(new DirectionalExposureRule().TryEvaluate(RuleTestFacts.Default(netDelta: -0.25m)));
	}
}
