using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers the fixed "% of entry premium, any day" take-profit (rules.takeProfit.profitTargetPctOfPremium) —
// the discretionary "grab +X% and recycle" exit. Works for both debit structures (% return on debit paid)
// and credit structures (% of the credit captured = % of max profit).
public class TakeProfitRuleDebitTargetTests
{
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
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.25m };
		var rule = new TakeProfitRule(cfg);
		var position = CallCalendar(initialDebit: 0.50m);
		// mark = 0.85 - 0.20 = 0.65; profit = 0.15 = 30% of 0.50 debit ≥ 25% target.
		var p = rule.Evaluate(position, CtxMidTrade(shortMid: 0.20m, longMid: 0.85m, position));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Contains("of net premium", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void DoesNotFire_WhenProfitBelowDebitTarget()
	{
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.35m };
		var rule = new TakeProfitRule(cfg);
		var position = CallCalendar(initialDebit: 0.50m);
		// profit = 30% of debit, below the 35% target.
		Assert.Null(rule.Evaluate(position, CtxMidTrade(shortMid: 0.20m, longMid: 0.85m, position)));
	}

	[Fact]
	public void DoesNotFire_WhenTargetOff()
	{
		// profitTargetPctOfPremium = 0 (off) → no exit path engaged.
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0m };
		var rule = new TakeProfitRule(cfg);
		var position = CallCalendar(initialDebit: 0.50m);
		Assert.Null(rule.Evaluate(position, CtxMidTrade(shortMid: 0.20m, longMid: 0.85m, position)));
	}

	// Credit structure: entry credit → AdjustedNetDebit < 0, mark < 0, and the target is measured against the
	// captured fraction of the credit (= fraction of max profit). Proves the one knob works for both types.
	private static OpenPosition ShortPutVertical(decimal credit = 1.00m) => new(
		Key: "GME_SPV_25.00",
		Ticker: "GME",
		StrategyKind: "ShortPutVertical",
		Legs: new[]
		{
			new PositionLeg("GME260515P00025000", Side.Sell, 25.00m, new DateTime(2026, 5, 15), "P", 100),
			new PositionLeg("GME260515P00024000", Side.Buy,  24.00m, new DateTime(2026, 5, 15), "P", 100),
		},
		InitialNetDebit: -credit,   // credit received → negative net "debit"
		AdjustedNetDebit: -credit,
		Quantity: 100);

	private static EvaluationContext CtxVertical(decimal sellMid, decimal buyMid, OpenPosition position)
	{
		const decimal h = 0.02m;
		return new EvaluationContext(
			Now: new DateTime(2026, 4, 20, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.50m },
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260515P00025000"] = new("GME260515P00025000", null, sellMid - h, sellMid + h, null, null, 100, 1000, 0.40m),
				["GME260515P00024000"] = new("GME260515P00024000", null, buyMid - h,  buyMid + h,  null, null, 100, 1000, 0.40m),
			},
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	[Fact]
	public void Fires_OnCreditStructure_WhenCreditCaptureReachesTarget()
	{
		// Short put vertical, credit $1.00 → AdjustedNetDebit = -1.00. Buyback now $0.50 (sell mid 0.80,
		// buy mid 0.30 → mark -0.50). profit = -0.50 - (-1.00) = 0.50 = 50% of the $1.00 credit.
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.50m };
		var rule = new TakeProfitRule(cfg);
		var position = ShortPutVertical(credit: 1.00m);
		var p = rule.Evaluate(position, CtxVertical(sellMid: 0.80m, buyMid: 0.30m, position));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Contains("of net premium", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void DoesNotFire_OnCreditStructure_WhenCaptureBelowTarget()
	{
		// Same 50%-captured position, but a 60% target → no fire.
		var cfg = new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.60m };
		var rule = new TakeProfitRule(cfg);
		var position = ShortPutVertical(credit: 1.00m);
		Assert.Null(rule.Evaluate(position, CtxVertical(sellMid: 0.80m, buyMid: 0.30m, position)));
	}
}
