using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

public class CloseBeforeShortExpiryRuleTests
{
	private static CloseBeforeShortExpiryConfig DefaultConfig() => new()
	{
		Enabled = true,
		MinProfitPct = 30m,
		EmergencyBreakEvenBufferPct = 1.0m,
	};

	private static OpenPosition GmePutCalendar(int qty = 300, decimal initialDebit = 0.50m, decimal adjDebit = 0.50m) => new(
		Key: "GME_CALENDAR_25.00_20260501",
		Ticker: "GME",
		StrategyKind: "CALENDAR",
		Legs: new[]
		{
			new PositionLeg("GME260501P00025000", Side.Sell, 25.00m, new DateTime(2026, 5, 1), "P", qty),
			new PositionLeg("GME260515P00025000", Side.Buy,  25.00m, new DateTime(2026, 5, 15), "P", qty),
		},
		InitialNetDebit: initialDebit,
		AdjustedNetDebit: adjDebit,
		Quantity: qty);

	private static EvaluationContext CtxOnExpiry(decimal spot, decimal shortMid, decimal longMid, OpenPosition position)
	{
		// shortMid = (bid + ask) / 2. We pick a tight spread so mid ≈ given value.
		const decimal spreadHalf = 0.02m;
		return new EvaluationContext(
			Now: new DateTime(2026, 5, 1, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = spot },
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260501P00025000"] = new("GME260501P00025000", null, shortMid - spreadHalf, shortMid + spreadHalf, null, null, 100, 1000, 0.40m),
				["GME260515P00025000"] = new("GME260515P00025000", null, longMid - spreadHalf,  longMid + spreadHalf,  null, null, 100, 1000, 0.40m),
			},
			AccountCash: 0m,
			AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	[Fact]
	public void DoesNotFire_WhenShortDteGreaterThanZero()
	{
		var rule = new CloseBeforeShortExpiryRule(DefaultConfig());
		var position = GmePutCalendar();
		var ctx = new EvaluationContext(
			Now: new DateTime(2026, 4, 30, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.00m },
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260501P00025000"] = new("GME260501P00025000", null, 0.20m, 0.30m, null, null, 100, 1000, 0.40m),
				["GME260515P00025000"] = new("GME260515P00025000", null, 0.95m, 1.05m, null, null, 100, 1000, 0.40m),
			},
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());

		Assert.Null(rule.Evaluate(position, ctx));
	}

	[Fact]
	public void DoesNotFire_WhenProfitBelowThreshold()
	{
		var rule = new CloseBeforeShortExpiryRule(DefaultConfig()); // 30% threshold
		var position = GmePutCalendar(initialDebit: 0.50m);
		// mark = long - short = 0.55 - 0.20 = 0.35. profit = 0.35 - 0.50 = -0.15 (loss)
		var ctx = CtxOnExpiry(spot: 25.00m, shortMid: 0.20m, longMid: 0.55m, position: position);
		Assert.Null(rule.Evaluate(position, ctx));
	}

	[Fact]
	public void Fires_WhenProfitAboveThreshold()
	{
		var rule = new CloseBeforeShortExpiryRule(DefaultConfig()); // 30% threshold
		var position = GmePutCalendar(initialDebit: 0.50m, qty: 300);
		// mark = long - short = 0.85 - 0.10 = 0.75. profit = 0.75 - 0.50 = 0.25. pct = 50%.
		var ctx = CtxOnExpiry(spot: 25.10m, shortMid: 0.10m, longMid: 0.85m, position: position);
		var p = rule.Evaluate(position, ctx);
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Equal(2, p.Legs.Count);
		Assert.All(p.Legs, l => Assert.Equal(300, l.Qty));
		Assert.Contains("close all 300", p.Rationale);
	}

	[Fact]
	public void EmergencyClose_FiresPastLowerBreakEven_RegardlessOfProfit()
	{
		var rule = new CloseBeforeShortExpiryRule(DefaultConfig());
		// debit 0.50 → BE [24.50, 25.50]. Spot 23.80 is well past lower BE.
		var position = GmePutCalendar(initialDebit: 0.50m, adjDebit: 0.50m, qty: 300);
		// Even with the position underwater, emergency fires.
		var ctx = CtxOnExpiry(spot: 23.80m, shortMid: 1.20m, longMid: 1.30m, position: position);
		var p = rule.Evaluate(position, ctx);
		Assert.NotNull(p);
		Assert.Contains("emergency", p!.Rationale, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Disabled_DoesNotFire()
	{
		var cfg = DefaultConfig();
		cfg.Enabled = false;
		var rule = new CloseBeforeShortExpiryRule(cfg);
		var position = GmePutCalendar();
		var ctx = CtxOnExpiry(spot: 25.10m, shortMid: 0.10m, longMid: 0.85m, position: position);
		Assert.Null(rule.Evaluate(position, ctx));
	}
}
