using WebullAnalytics.AI.RiskDiagnostics.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

public class EarningsProximityRuleTests
{
	private static readonly DateTime AsOf = new(2026, 6, 3);

	[Fact]
	public void ShortCallCrossingExDiv_FiresAssignmentRisk()
	{
		// ex-div in 5 days, short leg expires in 9 days → the short call trades through the ex-date.
		var hit = new EarningsProximityRule().TryEvaluate(RuleTestFacts.Default(
			asOf: AsOf, ticker: "SPY", hasShortCallLeg: true, hasShortLeg: true,
			shortLegDteMin: 9, longLegDteMax: 23,
			nextExDividendDate: AsOf.AddDays(5), nextDividendAmount: 1.75m, shortLegExtrinsic: 0.50m));

		Assert.NotNull(hit);
		Assert.Contains("early-assignment risk", hit!.Message);
		Assert.Contains("HIGH", hit.Message); // extrinsic 0.50 < dividend 1.75
		Assert.Equal(5m, hit.Inputs["ex_div_days_out"]);
	}

	[Fact]
	public void LongOnlyCrossingExDiv_FiresInformationalNotAssignment()
	{
		// SPY call calendar reproduction: short (DTE 9) expires BEFORE the ex-date (16 days out); only the
		// long (DTE 23) trades through it. No assignment risk — informational note that explains pricing.
		var hit = new EarningsProximityRule().TryEvaluate(RuleTestFacts.Default(
			asOf: AsOf, ticker: "SPY", hasShortCallLeg: true, hasShortLeg: true,
			shortLegDteMin: 9, longLegDteMax: 23,
			nextExDividendDate: AsOf.AddDays(16), nextDividendAmount: 1.75m));

		Assert.NotNull(hit);
		Assert.Contains("long leg trades through it", hit!.Message);
		Assert.DoesNotContain("early-assignment risk", hit.Message);
	}

	[Fact]
	public void CashSettledIndexRoot_SuppressesExDivNote()
	{
		// SPXW is European cash-settled → no assignment risk and the dividend is already in the forward.
		var hit = new EarningsProximityRule().TryEvaluate(RuleTestFacts.Default(
			asOf: AsOf, ticker: "SPXW", hasShortCallLeg: true, hasShortLeg: true,
			shortLegDteMin: 9, longLegDteMax: 23,
			nextExDividendDate: AsOf.AddDays(5), nextDividendAmount: 1.75m));

		Assert.Null(hit);
	}

	[Fact]
	public void NoLegCrossesExDiv_DoesNotFire()
	{
		// ex-div is beyond every leg's expiry → nothing crosses it.
		var hit = new EarningsProximityRule().TryEvaluate(RuleTestFacts.Default(
			asOf: AsOf, ticker: "SPY", hasShortCallLeg: true, hasShortLeg: true,
			shortLegDteMin: 9, longLegDteMax: 23,
			nextExDividendDate: AsOf.AddDays(40), nextDividendAmount: 1.75m));

		Assert.Null(hit);
	}
}
