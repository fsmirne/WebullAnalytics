using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

public class LegInShortRuleTests
{
	private static LegInShortConfig DefaultConfig() => new()
	{
		Enabled = true,
		MinSpotPctITM = 1.0m,
		MinLongDelta = 0.65m,
		TriggerProfitPct = 0.50m,
		MinDTE = 5,
		TargetShortDelta = 0.30m,
		ShortDeltaTolerance = 0.05m,
		MinShortCreditPerShare = 0.30m,
	};

	private static IndicatorsConfig DefaultIndicators() => new() { StrikeStep = 5m };

	// Long call at 7300, ~14 DTE. Spot drives ITM and delta; the chain we hand the rule includes a
	// few candidate short strikes so the rule can pick one in the target-delta band.
	private static OpenPosition LongSpxwCall(decimal strike = 7300m, decimal initialDebit = 30m, decimal adjDebit = 30m, int qty = 1) => new(
		Key: "SPXW_LONGCALL_7300.00_20260612",
		Ticker: "SPXW",
		StrategyKind: "LongCall",
		Legs: new[]
		{
			new PositionLeg("SPXW260612C07300000", Side.Buy, strike, new DateTime(2026, 6, 12), "C", qty),
		},
		InitialNetDebit: initialDebit,
		AdjustedNetDebit: adjDebit,
		Quantity: qty);

	// Builds a quote map: the long leg + a ladder of short candidates at common strikes. IV ~ 18%
	// keeps the BS delta computation realistic for SPXW.
	private static Dictionary<string, OptionContractQuote> BuildSpxwChain(decimal longMid, params (decimal Strike, decimal Bid, decimal Ask)[] shorts)
	{
		var map = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260612C07300000"] = new("SPXW260612C07300000", null, longMid - 0.20m, longMid + 0.20m, null, null, 100, 1000, 0.18m),
		};
		foreach (var s in shorts)
		{
			var occ = $"SPXW260612C{(int)(s.Strike * 1000):D8}";
			map[occ] = new(occ, null, s.Bid, s.Ask, null, null, 100, 1000, 0.18m);
		}
		return map;
	}

	private static EvaluationContext Ctx(decimal spot, OpenPosition position, IReadOnlyDictionary<string, OptionContractQuote> quotes, DateTime? now = null) =>
		new(
			Now: now ?? new DateTime(2026, 5, 29, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [position.Key] = position },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPXW"] = spot },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());

	[Fact]
	public void DoesNotFire_WhenDisabled()
	{
		var cfg = DefaultConfig();
		cfg.Enabled = false;
		var rule = new LegInShortRule(cfg, DefaultIndicators());
		var pos = LongSpxwCall();
		var ctx = Ctx(spot: 7400m, position: pos, quotes: BuildSpxwChain(longMid: 100m, (7450m, 50m, 51m)));
		Assert.Null(rule.Evaluate(pos, ctx));
	}

	[Fact]
	public void DoesNotFire_OnNonSingleLegStructures()
	{
		var rule = new LegInShortRule(DefaultConfig(), DefaultIndicators());
		var pos = LongSpxwCall() with { StrategyKind = "LongCallVertical" }; // already a vertical
		var ctx = Ctx(spot: 7400m, position: pos, quotes: BuildSpxwChain(longMid: 100m, (7450m, 50m, 51m)));
		Assert.Null(rule.Evaluate(pos, ctx));
	}

	[Fact]
	public void DoesNotFire_WhenNotITM()
	{
		var rule = new LegInShortRule(DefaultConfig(), DefaultIndicators());
		var pos = LongSpxwCall(strike: 7300m);
		// spot 7300 → exactly ATM, fails the > 1% ITM gate.
		var ctx = Ctx(spot: 7300m, position: pos, quotes: BuildSpxwChain(longMid: 30m, (7400m, 5m, 5.5m)));
		Assert.Null(rule.Evaluate(pos, ctx));
	}

	[Fact]
	public void DoesNotFire_WhenProfitBelowThreshold()
	{
		var rule = new LegInShortRule(DefaultConfig(), DefaultIndicators());
		var pos = LongSpxwCall(strike: 7300m, initialDebit: 30m, adjDebit: 30m);
		// spot 7400 ITM, but long mid 32 → profit only ~7% of debit, fails 50% gate.
		var ctx = Ctx(spot: 7400m, position: pos, quotes: BuildSpxwChain(longMid: 32m, (7450m, 50m, 51m)));
		Assert.Null(rule.Evaluate(pos, ctx));
	}

	[Fact]
	public void DoesNotFire_WhenNoShortInTargetDeltaBand()
	{
		var rule = new LegInShortRule(DefaultConfig(), DefaultIndicators());
		var pos = LongSpxwCall(strike: 7300m);
		// Spot 7400, long ITM and profitable. Chain offers a 7305 short (delta ~0.50, too high) but
		// no strike near 0.30 delta. Rule should bail.
		var ctx = Ctx(spot: 7400m, position: pos, quotes: BuildSpxwChain(longMid: 120m, (7305m, 95m, 96m)));
		Assert.Null(rule.Evaluate(pos, ctx));
	}

	[Fact]
	public void DoesNotFire_WhenShortCreditBelowMinimum()
	{
		var cfg = DefaultConfig();
		cfg.MinShortCreditPerShare = 50.00m; // crank up minimum so even a fat short premium misses
		var rule = new LegInShortRule(cfg, DefaultIndicators());
		var pos = LongSpxwCall(strike: 7300m);
		// 7550 short with delta near 0.30 but credit below the 50 floor.
		var ctx = Ctx(spot: 7400m, position: pos, quotes: BuildSpxwChain(longMid: 120m, (7550m, 5m, 5.2m)));
		Assert.Null(rule.Evaluate(pos, ctx));
	}

	[Fact]
	public void Fires_WhenAllGatesPass()
	{
		var rule = new LegInShortRule(DefaultConfig(), DefaultIndicators());
		var pos = LongSpxwCall(strike: 7300m, initialDebit: 30m, adjDebit: 30m);
		// Spot 7400 (1.4% ITM), long mid 60 → 100% profit. IV 18% + 14 DTE puts a 7550 strike
		// at ~0.30 delta (call). Short bid 5 / ask 5.2 → credit clears $0.30 minimum.
		var ctx = Ctx(spot: 7400m, position: pos, quotes: BuildSpxwChain(longMid: 60m, (7550m, 5m, 5.2m)));
		var proposal = rule.Evaluate(pos, ctx);
		Assert.NotNull(proposal);
		Assert.Equal(ProposalKind.LegIn, proposal!.Kind);
		Assert.Single(proposal.Legs);
		Assert.Equal("sell", proposal.Legs[0].Action);
		Assert.Equal("SPXW260612C07550000", proposal.Legs[0].Symbol);
		Assert.Equal(1, proposal.Legs[0].Qty);
		Assert.Contains("LongCallVertical", proposal.Rationale);
	}

	[Fact]
	public void Fires_InCreditSpreadMode_PicksDeeperItmShort()
	{
		// Credit-spread variant: short is BELOW long strike (deeper ITM for calls).
		// Spot 7400, long call at 7300 (1.4% ITM), profit 100%.
		// At 18% IV, 14 DTE, a 7250 call has delta ≈ 0.70 (deep ITM).
		var cfg = DefaultConfig();
		cfg.CreditSpread = true;
		cfg.TargetShortDelta = 0.70m;
		cfg.ShortDeltaTolerance = 0.05m;
		cfg.MinShortCreditPerShare = 50m; // credit-mode shorts have rich premium; raise floor accordingly
		var rule = new LegInShortRule(cfg, DefaultIndicators());

		var pos = LongSpxwCall(strike: 7300m, initialDebit: 30m, adjDebit: 30m);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260612C07300000"] = new("SPXW260612C07300000", null, 60m - 0.2m, 60m + 0.2m, null, null, 100, 1000, 0.18m),
			// 7250 call: 150 ITM, delta ≈ 0.71 at these params
			["SPXW260612C07250000"] = new("SPXW260612C07250000", null, 152m, 152.4m, null, null, 100, 1000, 0.18m),
		};
		var ctx = new EvaluationContext(
			Now: new DateTime(2026, 5, 29, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [pos.Key] = pos },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPXW"] = 7400m },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());

		var proposal = rule.Evaluate(pos, ctx);
		Assert.NotNull(proposal);
		Assert.Equal(ProposalKind.LegIn, proposal!.Kind);
		Assert.Equal("SPXW260612C07250000", proposal.Legs[0].Symbol); // deeper-ITM strike picked
		Assert.Contains("ShortVertical", proposal.Rationale);
		Assert.Contains("credit spread", proposal.Rationale);
	}

	[Fact]
	public void Fires_OnLongPut_PicksLowerStrikeShort()
	{
		var rule = new LegInShortRule(DefaultConfig(), DefaultIndicators());
		// Mirror image: long put 7400, spot 7250 → 2% ITM downside. Long put delta ~0.70.
		// Short put at 7130 strike with IV 18% / 14 DTE → put delta ~0.30.
		var pos = new OpenPosition(
			Key: "SPXW_LONGPUT_7400.00_20260612",
			Ticker: "SPXW",
			StrategyKind: "LongPut",
			Legs: new[] { new PositionLeg("SPXW260612P07400000", Side.Buy, 7400m, new DateTime(2026, 6, 12), "P", 1) },
			InitialNetDebit: 30m,
			AdjustedNetDebit: 30m,
			Quantity: 1);

		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260612P07400000"] = new("SPXW260612P07400000", null, 60m, 60.4m, null, null, 100, 1000, 0.18m),
			["SPXW260612P07130000"] = new("SPXW260612P07130000", null, 5m, 5.2m, null, null, 100, 1000, 0.18m),
		};

		var ctx = new EvaluationContext(
			Now: new DateTime(2026, 5, 29, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [pos.Key] = pos },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPXW"] = 7250m },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());

		var proposal = rule.Evaluate(pos, ctx);
		Assert.NotNull(proposal);
		Assert.Equal(ProposalKind.LegIn, proposal!.Kind);
		Assert.Equal("SPXW260612P07130000", proposal.Legs[0].Symbol);
		Assert.Contains("LongPutVertical", proposal.Rationale);
	}
}
