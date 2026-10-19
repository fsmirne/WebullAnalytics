using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

/// <summary>A held leg marks at mid when two-sided, else at the side closing it would trade. The 2026-10-19 IC-mirror study
/// found the old one-sided conventions (plain average = ask/2, or the live side = the ask) valuing unbid long wings above $0,
/// which fired take-profits on profit the close could not realize.</summary>
public class HeldLegMarkTests
{
	[Theory]
	[InlineData(1.00, 1.10, true, 1.05)]   // two-sided: mid, either direction
	[InlineData(1.00, 1.10, false, 1.05)]
	[InlineData(0.00, 0.40, true, 0.00)]   // unbid long wing: worth its $0 bid to sell
	[InlineData(0.00, 0.40, false, 0.40)]  // unbid short: costs its ask to buy back
	[InlineData(0.30, 0.00, false, 0.30)]  // short with no offer: the bid is the only price
	public void MarksTwoSidedAtMidAndOneSidedAtTheClosingSide(double bid, double ask, bool isLong, double expected) =>
		Assert.Equal((decimal)expected, OptionMath.HeldLegMark((decimal)bid, (decimal)ask, isLong));

	[Fact]
	public void TakeProfitDoesNotFireOnAnUnbidLongLeg()
	{
		// Long call bought at $0.10 now quotes 0 / 0.40. The plain average marked it $0.20 (+100%, past a 25% target); nobody
		// bids it, so closing realizes $0 — a loss, not a take-profit.
		var position = new OpenPosition(
			Key: "GME_LONGCALL_30.00", Ticker: "GME", StrategyKind: "LongCall",
			Legs: new[] { new PositionLeg("GME260501C00030000", Side.Buy, 30.00m, new DateTime(2026, 5, 1), "C", 1) },
			InitialNetDebit: 0.10m, AdjustedNetDebit: 0.10m, Quantity: 1);
		var ctx = new EvaluationContext(
			Now: new DateTime(2026, 4, 20, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.00m },
			Quotes: new Dictionary<string, OptionContractQuote> { ["GME260501C00030000"] = new("GME260501C00030000", null, 0m, 0.40m, null, null, 0, 1000, 0.80m) },
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
		var rule = new TakeProfitRule(new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.25m });
		Assert.Null(rule.Evaluate(position, ctx));
	}
}
