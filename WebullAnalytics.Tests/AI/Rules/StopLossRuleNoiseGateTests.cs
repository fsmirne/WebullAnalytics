using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// StopLossRule's noise gate (minEntryToNoiseRatio, mirroring the opener's same-named knob via
// CandidateScorer.PassesExitNoiseGate), covering both the max-loss and max-profit realized-loss
// triggers. Same fixture shape as StopLossRuleMaxProfitTests but with a controllable leg half-spread.
public class StopLossRuleNoiseGateTests
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

	private static EvaluationContext Ctx(OpenPosition position, decimal shortMid, decimal longMid, decimal halfSpread)
	{
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260827P07695000"] = new("SPXW260827P07695000", null, shortMid - halfSpread, shortMid + halfSpread, null, null, 100, 1000, 0.20m),
			["SPXW260827P07690000"] = new("SPXW260827P07690000", null, longMid - halfSpread, longMid + halfSpread, null, null, 100, 1000, 0.20m),
		};
		return new EvaluationContext(
			Now: new DateTime(2026, 8, 27, 12, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPX"] = 7700m },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	// maxLoss = width(5) - credit(1.40) = 3.60. mark = 1.40 - 3.20 = -1.80; realizedLoss = -1.40-(-1.80)
	// = 0.40, which clears a 10% (0.36) max-loss threshold. Mids kept well above the widest half-spread
	// tested (1.00) so bid = mid - halfSpread never goes negative and silently drops a leg from the noise
	// calc (see the analogous fix in TakeProfitRuleNoiseGateTests).
	private const decimal ShortMid = 3.20m, LongMid = 1.40m;

	private static StopLossRule Rule(decimal minEntryToNoiseRatio) =>
		new(new StopLossConfig { Enabled = true, PctOfMaxLoss = 0.10m, PctOfMaxProfit = 0m },
			new OpenerRealizedExpectancyConfig { Enabled = true, StopLossPctOfMaxLoss = 0.10m, StopLossPctOfMaxProfit = 0m },
			minEntryToNoiseRatio);

	[Fact]
	public void MaxLossTrigger_TightSpread_StillFires_WithNoiseGateArmed()
	{
		var p = Rule(minEntryToNoiseRatio: 0.5m).Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), ShortMid, LongMid, halfSpread: 0.02m));
		Assert.NotNull(p);
		Assert.Contains("of max loss", p!.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void MaxLossTrigger_WideSpread_SameCrossing_NoLongerFires()
	{
		// half-spread 1.00/leg -> RSS noise = sqrt(1²+1²) ≈ 1.414; 0.5×1.414 ≈ 0.707 > the 0.40 realized
		// loss — the crossing is smaller than the spread noise that could fully explain it.
		Assert.Null(Rule(minEntryToNoiseRatio: 0.5m).Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), ShortMid, LongMid, halfSpread: 1.00m)));
	}

	[Fact]
	public void MaxLossTrigger_WideSpread_RatioDisabled_StillFires_PreservingPriorBehavior()
	{
		var p = Rule(minEntryToNoiseRatio: 0m).Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), ShortMid, LongMid, halfSpread: 1.00m));
		Assert.NotNull(p);
	}

	[Fact]
	public void MaxProfitTrigger_WideSpread_SameGateApplies()
	{
		// maxProfit = credit = 1.40; pctOfMaxProfit 0.5 -> threshold 0.70. mark = 2.20 - 4.60 = -2.40;
		// realizedLoss = -1.40-(-2.40) = 1.00, clearing the 0.70 threshold. Mids kept above the widest
		// half-spread tested (2.00) so both bids stay positive.
		var rule = new StopLossRule(
			new StopLossConfig { Enabled = true, PctOfMaxLoss = 1.0m, PctOfMaxProfit = 0.5m },
			new OpenerRealizedExpectancyConfig { Enabled = true, StopLossPctOfMaxLoss = 1.0m, StopLossPctOfMaxProfit = 0.5m },
			minEntryToNoiseRatio: 0.5m);
		Assert.NotNull(rule.Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), shortMid: 4.60m, longMid: 2.20m, halfSpread: 0.02m)));
		// Same 1.00 realized-loss crossing, but a half-spread of 2.00/leg -> noise = sqrt(2²+2²) ≈ 2.828,
		// 0.5×2.828 ≈ 1.414 > 1.00 — noise, not a genuine max-profit giveback.
		Assert.Null(rule.Evaluate(ShortPutVertical(), Ctx(ShortPutVertical(), shortMid: 4.60m, longMid: 2.20m, halfSpread: 2.00m)));
	}
}
