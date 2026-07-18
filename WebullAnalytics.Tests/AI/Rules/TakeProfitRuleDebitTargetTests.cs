using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers the fixed "% of net debit, any day" take-profit (rules.takeProfit.profitTargetPctOfDebit) —
// the discretionary "grab +X% and recycle" exit, distinct from the % -of-max-projected-profit target.
public class TakeProfitRuleDebitTargetTests
{
	// realizedExpectancy OFF, so only the debit target can fire — isolates the new path.
	private static OpenerRealizedExpectancyConfig NoRealizedExpectancy() => new() { Enabled = false };

	private static OpenPosition CallCalendar(decimal initialDebit = 0.50m) => new(
		Key: "GME_CALENDAR_25.00",
		Ticker: "GME",
		StrategyKind: "CALENDAR",
		Legs: new[]
		{
			new PositionLeg("GME260501C00025000", Side.Sell, 25.00m, new DateTime(2026, 5, 1), "C", 100),
			new PositionLeg("GME260515C00025000", Side.Buy,  25.00m, new DateTime(2026, 5, 15), "C", 100),
		},
		InitialNetDebit: initialDebit,
		AdjustedNetDebit: initialDebit,
		Quantity: 100);

	// Mid-trade (11 days before short expiry), mark = longMid - shortMid per share.
	private static EvaluationContext CtxMidTrade(decimal shortMid, decimal longMid, OpenPosition position)
	{
		const decimal h = 0.02m;
		return new EvaluationContext(
			Now: new DateTime(2026, 4, 20, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.00m },
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260501C00025000"] = new("GME260501C00025000", null, shortMid - h, shortMid + h, null, null, 100, 1000, 0.40m),
				["GME260515C00025000"] = new("GME260515C00025000", null, longMid - h,  longMid + h,  null, null, 100, 1000, 0.40m),
			},
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	[Fact]
	public void Fires_AnyDay_WhenProfitReachesDebitTarget_EvenWithRealizedExpectancyOff()
	{
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfDebit = 0.25m };
		var rule = new TakeProfitRule(cfg, NoRealizedExpectancy());
		var position = CallCalendar(initialDebit: 0.50m);
		// mark = 0.85 - 0.20 = 0.65; profit = 0.15 = 30% of 0.50 debit ≥ 25% target.
		var p = rule.Evaluate(position, CtxMidTrade(shortMid: 0.20m, longMid: 0.85m, position));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Contains("of net debit", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void DoesNotFire_WhenProfitBelowDebitTarget()
	{
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfDebit = 0.35m };
		var rule = new TakeProfitRule(cfg, NoRealizedExpectancy());
		var position = CallCalendar(initialDebit: 0.50m);
		// profit = 30% of debit, below the 35% target.
		Assert.Null(rule.Evaluate(position, CtxMidTrade(shortMid: 0.20m, longMid: 0.85m, position)));
	}

	[Fact]
	public void DoesNotFire_WhenDebitTargetOff_AndNoMaxProjectedPath()
	{
		// profitTargetPctOfDebit = 0 (off) and realizedExpectancy off → no exit path engaged.
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfDebit = 0m };
		var rule = new TakeProfitRule(cfg, NoRealizedExpectancy());
		var position = CallCalendar(initialDebit: 0.50m);
		Assert.Null(rule.Evaluate(position, CtxMidTrade(shortMid: 0.20m, longMid: 0.85m, position)));
	}
}
