using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

// TakeProfitRule's noise gate (minEntryToNoiseRatio, mirroring the opener's same-named knob via
// CandidateScorer.PassesExitNoiseGate). Built after a backtest LongDiagonal "captured" 137% of its debit
// in one minute on a flat underlying — see project_dc_sweep_tp_noise_artifact.md. Same fixture shape as
// TakeProfitRuleDebitTargetTests, but with a controllable leg half-spread so the profit crossing can be
// made noise-explicable (wide spread) or not (tight spread).
public class TakeProfitRuleNoiseGateTests
{
	private static OpenPosition CallCalendar(decimal initialDebit) => new(
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

	private static EvaluationContext Ctx(decimal shortMid, decimal longMid, decimal halfSpread, OpenPosition position) => new(
		Now: new DateTime(2026, 4, 20, 11, 0, 0),
		OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
		UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.00m },
		Quotes: new Dictionary<string, OptionContractQuote>
		{
			["GME260501C00025000"] = new("GME260501C00025000", null, shortMid - halfSpread, shortMid + halfSpread, null, null, 100, 1000, 0.40m),
			["GME260515C00025000"] = new("GME260515C00025000", null, longMid - halfSpread, longMid + halfSpread, null, null, 100, 1000, 0.40m),
		},
		AccountCash: 0m, AccountValue: 0m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>());

	// mark = 1.85 - 1.20 = 0.65; profit = 0.15/share = 30% of the 0.50 debit, clears a 25% target either way.
	// Mids kept well above the widest half-spread tested (0.30) so bid = mid - halfSpread never goes
	// negative and silently drops a leg out of the noise calc (it did, the first time this was written).
	private const decimal ShortMid = 1.20m, LongMid = 1.85m, InitialDebit = 0.50m;

	[Fact]
	public void TightSpread_RealCrossing_StillFires_WithNoiseGateArmed()
	{
		var rule = new TakeProfitRule(new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.25m }, minEntryToNoiseRatio: 0.5m);
		var position = CallCalendar(InitialDebit);
		// half-spread 0.02/leg -> RSS noise ≈ 0.028; profit 0.15 clears 0.5×0.028 comfortably.
		var p = rule.Evaluate(position, Ctx(ShortMid, LongMid, halfSpread: 0.02m, position));
		Assert.NotNull(p);
	}

	[Fact]
	public void WideSpread_SameProfitCrossing_NoLongerFires()
	{
		var rule = new TakeProfitRule(new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.25m }, minEntryToNoiseRatio: 0.5m);
		var position = CallCalendar(InitialDebit);
		// half-spread 0.30/leg -> RSS noise = sqrt(0.30²+0.30²) ≈ 0.424; 0.5×0.424 ≈ 0.212 > the 0.15
		// profit — the crossing is smaller than the spread noise that could fully explain it.
		Assert.Null(rule.Evaluate(position, Ctx(ShortMid, LongMid, halfSpread: 0.30m, position)));
	}

	[Fact]
	public void WideSpread_RatioDisabled_StillFires_PreservingPriorBehavior()
	{
		// Default minEntryToNoiseRatio (0m, the ctor default) must reproduce the pre-gate behavior exactly —
		// this is what every pre-existing TakeProfitRule test relies on implicitly.
		var rule = new TakeProfitRule(new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.25m });
		var position = CallCalendar(InitialDebit);
		var p = rule.Evaluate(position, Ctx(ShortMid, LongMid, halfSpread: 0.30m, position));
		Assert.NotNull(p);
	}

	[Fact]
	public void WideSpread_ButProfitFarBeyondNoise_StillFires()
	{
		var rule = new TakeProfitRule(new TakeProfitConfig { Enabled = true, ProfitTargetPctOfPremium = 0.25m }, minEntryToNoiseRatio: 0.5m);
		var position = CallCalendar(initialDebit: 0.50m);
		// mark = 4.00 - 1.20 = 2.80; profit = 2.30/share, nowhere near explicable by ≈0.424 of noise.
		var p = rule.Evaluate(position, Ctx(shortMid: 1.20m, longMid: 4.00m, halfSpread: 0.30m, position));
		Assert.NotNull(p);
	}
}
