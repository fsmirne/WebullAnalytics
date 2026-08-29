using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// Covers the independent max-profit-based stop trigger (rules.stopLoss.pctOfMaxProfit): close once
// realized loss reaches a fraction of theoretical MAX PROFIT rather than max loss — e.g. 1.0 gives
// back exactly what a sold vertical could have made. 7695/7690 short put vertical for $1.40 credit
// (the real GEXOptionsTrading 2026-08-27 trade analyzed this session): width 5, maxLoss 3.60,
// maxProfit 1.40.
public class StopLossRuleMaxProfitTests
{
	private static OpenPosition ShortPutVertical() => new(
		Key: "SPX_VERT_7695_7690",
		Ticker: "SPX",
		StrategyKind: "ShortPutVertical",
		Legs: new[]
		{
			new PositionLeg("SPXW260827P07695000", Side.Sell, 7695.00m, new DateTime(2026, 8, 27), "P", 100),
			new PositionLeg("SPXW260827P07690000", Side.Buy, 7690.00m, new DateTime(2026, 8, 27), "P", 100),
		},
		InitialNetDebit: -1.40m,
		AdjustedNetDebit: -1.40m,
		Quantity: 100);

	private static EvaluationContext Ctx(OpenPosition position, decimal shortMid, decimal longMid)
	{
		const decimal h = 0.02m;
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260827P07695000"] = new("SPXW260827P07695000", null, shortMid - h, shortMid + h, null, null, 100, 1000, 0.20m),
			["SPXW260827P07690000"] = new("SPXW260827P07690000", null, longMid - h, longMid + h, null, null, 100, 1000, 0.20m),
		};
		return new EvaluationContext(
			Now: new DateTime(2026, 8, 27, 12, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPX"] = 7700m },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	// pctOfMaxLoss fixed at 1.0 (disabled) throughout so only the max-profit trigger under test can fire.
	private static StopLossRule Rule(decimal pctOfMaxProfit) =>
		new(new StopLossConfig { Enabled = true, PctOfMaxLoss = 1.0m, PctOfMaxProfit = pctOfMaxProfit },
			new OpenerRealizedExpectancyConfig { Enabled = true, StopLossPctOfMaxLoss = 1.0m, StopLossPctOfMaxProfit = pctOfMaxProfit });

	[Fact]
	public void Fires_WhenRealizedLossReachesMaxProfit()
	{
		// mark = 0.20 - 3.00 = -2.80; realizedLoss = -1.40 - (-2.80) = 1.40 == maxProfit.
		var p = Rule(pctOfMaxProfit: 1.0m).Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), shortMid: 3.00m, longMid: 0.20m));
		Assert.NotNull(p);
		Assert.Equal(ProposalKind.Close, p!.Kind);
		Assert.Contains("of max profit", p.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void DoesNotFire_BelowMaxProfitThreshold()
	{
		// mark = 0.20 - 2.99 = -2.79; realizedLoss = 1.39 < 1.40.
		Assert.Null(Rule(pctOfMaxProfit: 1.0m).Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), shortMid: 2.99m, longMid: 0.20m)));
	}

	[Fact]
	public void DoesNotFire_WhenKnobOff()
	{
		// Same underwater mark as the firing case, but pctOfMaxProfit = 0 (default) disables the trigger.
		Assert.Null(Rule(pctOfMaxProfit: 0m).Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), shortMid: 3.00m, longMid: 0.20m)));
	}

	[Fact]
	public void TighterThreshold_FiresBeforeMaxLossThreshold()
	{
		// maxLoss = 3.60 (width 5 - credit 1.40); a 50% max-loss stop would need realizedLoss 1.80.
		// The max-profit stop at 1.0x fires first (at 1.40) since it's the tighter threshold.
		var rule = new StopLossRule(
			new StopLossConfig { Enabled = true, PctOfMaxLoss = 0.50m, PctOfMaxProfit = 1.0m },
			new OpenerRealizedExpectancyConfig { Enabled = true, StopLossPctOfMaxLoss = 0.50m, StopLossPctOfMaxProfit = 1.0m });
		var p = rule.Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), shortMid: 3.00m, longMid: 0.20m));
		Assert.NotNull(p);
		Assert.Contains("of max profit", p!.Rationale, StringComparison.Ordinal);
	}
}
