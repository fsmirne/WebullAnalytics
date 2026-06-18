using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI.Rules;

public class CompleteCondorRuleTests
{
	private static CompleteCondorConfig DefaultConfig() => new()
	{
		Enabled = true,
		MinHeldSidePctOtm = 1.0m,
		ShortDeltaMin = 0.10m,
		ShortDeltaMax = 0.50m,
		MinCreditPerShare = 0.10m,
	};

	// Held bull-put credit spread on SPXW: sell 7300P / buy 7200P (width 100), ~14 DTE.
	private static OpenPosition HeldPutVertical(int qty = 1) => new(
		Key: "SPXW_SHORTPUTVERTICAL_7300.00_20260612",
		Ticker: "SPXW",
		StrategyKind: "ShortPutVertical",
		Legs: new[]
		{
			new PositionLeg("SPXW260612P07300000", Side.Sell, 7300m, new DateTime(2026, 6, 12), "P", qty),
			new PositionLeg("SPXW260612P07200000", Side.Buy, 7200m, new DateTime(2026, 6, 12), "P", qty),
		},
		InitialNetDebit: -2m, AdjustedNetDebit: -2m, Quantity: qty);

	// Held bear-call credit spread on SPXW: sell 7500C / buy 7600C (width 100).
	private static OpenPosition HeldCallVertical(int qty = 1) => new(
		Key: "SPXW_SHORTCALLVERTICAL_7500.00_20260612",
		Ticker: "SPXW",
		StrategyKind: "ShortCallVertical",
		Legs: new[]
		{
			new PositionLeg("SPXW260612C07500000", Side.Sell, 7500m, new DateTime(2026, 6, 12), "C", qty),
			new PositionLeg("SPXW260612C07600000", Side.Buy, 7600m, new DateTime(2026, 6, 12), "C", qty),
		},
		InitialNetDebit: -2m, AdjustedNetDebit: -2m, Quantity: qty);

	private static OptionContractQuote Q(string occ, decimal bid, decimal ask) =>
		new(occ, null, bid, ask, null, null, 100, 1000, 0.18m);

	private static EvaluationContext Ctx(decimal spot, OpenPosition pos, IReadOnlyDictionary<string, OptionContractQuote> quotes,
		decimal? vix = null, decimal? rangePct = null, DateTime? now = null) =>
		new(
			Now: now ?? new DateTime(2026, 5, 29, 11, 0, 0),
			OpenPositions: new Dictionary<string, OpenPosition> { [pos.Key] = pos },
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPXW"] = spot },
			Quotes: quotes,
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>(),
			Vix: vix, IntradaySpotRangePct: rangePct);

	// Held put-vertical legs + a call ladder for the opposite side: 7550 (~0.30 delta, the short) and
	// 7650 (~0.15, the long wing at short + width 100). Both are OTM calls in the wide [0.10, 0.50] band;
	// the rule picks 7550 as the short (closest to the 0.30 band midpoint) and constructs 7650 as the wing.
	private static Dictionary<string, OptionContractQuote> PutVerticalChain() => new()
	{
		["SPXW260612P07300000"] = Q("SPXW260612P07300000", 28m, 29m),
		["SPXW260612P07200000"] = Q("SPXW260612P07200000", 18m, 19m),
		["SPXW260612C07550000"] = Q("SPXW260612C07550000", 40m, 41m),
		["SPXW260612C07650000"] = Q("SPXW260612C07650000", 18m, 19m),
	};

	[Fact]
	public void DoesNotFire_WhenDisabled()
	{
		var cfg = DefaultConfig();
		cfg.Enabled = false;
		var rule = new CompleteCondorRule(cfg);
		var pos = HeldPutVertical();
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7400m, pos, PutVerticalChain())));
	}

	[Fact]
	public void DoesNotFire_OnNonShortVertical()
	{
		var rule = new CompleteCondorRule(DefaultConfig());
		var pos = HeldPutVertical() with { StrategyKind = "IronCondor" }; // already a condor
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7400m, pos, PutVerticalChain())));
	}

	[Fact]
	public void DoesNotFire_WhenHeldSideLacksCushion()
	{
		var rule = new CompleteCondorRule(DefaultConfig());
		var pos = HeldPutVertical();
		// Spot 7310: held short 7300 is only 0.14% OTM, below the 1.0% cushion gate.
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7310m, pos, PutVerticalChain())));
	}

	[Fact]
	public void DoesNotFire_WhenNoOppositeShortInDeltaBand()
	{
		var rule = new CompleteCondorRule(DefaultConfig());
		var pos = HeldPutVertical();
		// Only a very-far-OTM call (8200, delta well below 0.10) — nothing in the band.
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260612P07300000"] = Q("SPXW260612P07300000", 28m, 29m),
			["SPXW260612P07200000"] = Q("SPXW260612P07200000", 18m, 19m),
			["SPXW260612C08200000"] = Q("SPXW260612C08200000", 0.5m, 0.6m),
		};
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7400m, pos, quotes)));
	}

	[Fact]
	public void DoesNotFire_WhenCreditBelowMinimum()
	{
		var cfg = DefaultConfig();
		cfg.MinCreditPerShare = 500m; // unreachable
		var rule = new CompleteCondorRule(cfg);
		var pos = HeldPutVertical();
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7400m, pos, PutVerticalChain())));
	}

	[Fact]
	public void DoesNotFire_WhenVixAboveGate()
	{
		var cfg = DefaultConfig();
		cfg.MaxVix = 25m;
		var rule = new CompleteCondorRule(cfg);
		var pos = HeldPutVertical();
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7400m, pos, PutVerticalChain(), vix: 30m)));
	}

	[Fact]
	public void DoesNotFire_OnTrendDay()
	{
		var cfg = DefaultConfig();
		cfg.MaxIntradayRangePct = 1.0m;
		var rule = new CompleteCondorRule(cfg);
		var pos = HeldPutVertical();
		Assert.Null(rule.Evaluate(pos, Ctx(spot: 7400m, pos, PutVerticalChain(), rangePct: 1.5m)));
	}

	[Fact]
	public void Fires_OnHeldPutVertical_SellsCallVertical()
	{
		var rule = new CompleteCondorRule(DefaultConfig());
		var pos = HeldPutVertical();
		var proposal = rule.Evaluate(pos, Ctx(spot: 7400m, pos, PutVerticalChain()));
		Assert.NotNull(proposal);
		Assert.Equal(ProposalKind.LegIn, proposal!.Kind);
		Assert.Equal(2, proposal.Legs.Count);
		var sell = Assert.Single(proposal.Legs, l => l.Action == "sell");
		var buy = Assert.Single(proposal.Legs, l => l.Action == "buy");
		Assert.Equal("SPXW260612C07550000", sell.Symbol); // ~0.30-delta short call
		Assert.Equal("SPXW260612C07650000", buy.Symbol);   // long wing = short + held width (100)
		Assert.Contains("IronCondor", proposal.Rationale);
		Assert.True(proposal.NetDebit < 0m, "completing the condor must be a net credit (negative debit)");
	}

	[Fact]
	public void Fires_OnHeldCallVertical_SellsPutVertical()
	{
		var rule = new CompleteCondorRule(DefaultConfig());
		var pos = HeldCallVertical();
		// Opposite side = puts: 7250 (~0.30, short) and 7150 (long wing = short − width 100).
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			["SPXW260612C07500000"] = Q("SPXW260612C07500000", 28m, 29m),
			["SPXW260612C07600000"] = Q("SPXW260612C07600000", 18m, 19m),
			["SPXW260612P07250000"] = Q("SPXW260612P07250000", 40m, 41m),
			["SPXW260612P07150000"] = Q("SPXW260612P07150000", 18m, 19m),
		};
		var proposal = rule.Evaluate(pos, Ctx(spot: 7400m, pos, quotes));
		Assert.NotNull(proposal);
		Assert.Equal(2, proposal!.Legs.Count);
		var sell = Assert.Single(proposal.Legs, l => l.Action == "sell");
		var buy = Assert.Single(proposal.Legs, l => l.Action == "buy");
		Assert.Equal("SPXW260612P07250000", sell.Symbol);
		Assert.Equal("SPXW260612P07150000", buy.Symbol);
		Assert.Contains("IronCondor", proposal.Rationale);
	}
}
