using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class PremiumRatioImbalancedRuleTests
{
	[Fact]
	public void FiresWhenDebitAndRatioAboveThreshold()
	{
		var hit = new PremiumRatioImbalancedRule().TryEvaluate(RuleTestFacts.Default(
			longPremiumPaid: 0.976m, shortPremiumReceived: 0.256m,
			netCashPerShare: -0.72m, premiumRatio: 0.976m / 0.256m));
		Assert.NotNull(hit);
		Assert.Equal("premium_ratio_imbalanced", hit!.Id);
		Assert.Equal(0.976m, hit.Inputs["long_paid"]);
		Assert.Equal(0.256m, hit.Inputs["short_received"]);
		Assert.Equal(3.0m, hit.Inputs["threshold"]);
		Assert.True(hit.Inputs["ratio"] > 3.8m && hit.Inputs["ratio"] < 3.9m);
		Assert.Equal(-0.72m, hit.Inputs["net_cash"]);
	}

	[Fact]
	public void DoesNotFireOnCreditStructure()
	{
		var hit = new PremiumRatioImbalancedRule().TryEvaluate(RuleTestFacts.Default(
			longPremiumPaid: 0.2m, shortPremiumReceived: 0.7m,
			netCashPerShare: 0.5m, premiumRatio: 0.2m / 0.7m));
		Assert.Null(hit);
	}

	[Fact]
	public void DoesNotFireWhenRatioBelowThreshold()
	{
		var hit = new PremiumRatioImbalancedRule().TryEvaluate(RuleTestFacts.Default(
			longPremiumPaid: 0.6m, shortPremiumReceived: 0.3m,
			netCashPerShare: -0.3m, premiumRatio: 2m));
		Assert.Null(hit);
	}

	[Fact]
	public void DoesNotFireWhenPremiumRatioNull()
	{
		var hit = new PremiumRatioImbalancedRule().TryEvaluate(RuleTestFacts.Default(
			longPremiumPaid: 1m, shortPremiumReceived: 0m,
			netCashPerShare: -1m, premiumRatio: null));
		Assert.Null(hit);
	}
}
