using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

/// <summary>Live-executor wiring for the held-leg collision guard (see HeldLegGuardTests for the shared rule
/// itself). A real broker nets a fungible option contract into ONE position — it would not let this account
/// independently carry lineage A's short leg and lineage B's OPPOSITE-side leg on the identical symbol — so
/// the opener must refuse a candidate that opposes a DIFFERENT already-held position's side on a shared
/// symbol, exactly like the backtest book does via the same HeldLegGuard. Adding to the SAME side is fine.</summary>
public class OpenerAutoExecutorHeldLegGuardTests
{
	private static OpenProposal Proposal(string shortSym, string longSym) => new(
		Ticker: "SPY",
		StructureKind: OpenStructureKind.ShortPutVertical,
		Legs: new[]
		{
			new ProposalLeg("sell", shortSym, 1, PricePerShare: 7.60m),
			new ProposalLeg("buy", longSym, 1, PricePerShare: 3.35m),
		},
		Qty: 1,
		DebitOrCreditPerContract: 425m,
		MaxProfitPerContract: 425m,
		MaxLossPerContract: -575m,
		CapitalAtRiskPerContract: 575m,
		Breakevens: new[] { 615.75m },
		ProbabilityOfProfit: 0.70m,
		ExpectedValuePerContract: 120m,
		DaysToTarget: 51,
		RawScore: 0.0015m,
		BiasAdjustedScore: 0.0015m,
		DirectionalFit: 1,
		Rationale: "",
		Fingerprint: "ShortPutVertical");

	private static OpenerAutoExecuteConfig Config() => new() { Enabled = true, Submit = false, MaxOrdersPerDay = 5 };

	// short 590P / long 580P, already open
	private static IReadOnlyDictionary<string, OpenPosition> HeldShort590Long580() =>
		new Dictionary<string, OpenPosition>
		{
			["A"] = new OpenPosition(
				Key: "A",
				Ticker: "SPY",
				StrategyKind: "ShortPutVertical",
				Legs: new[]
				{
					new PositionLeg("SPY250919P00590000", Side.Sell, 590m, null, "P", 1),
					new PositionLeg("SPY250919P00580000", Side.Buy, 580m, null, "P", 1),
				},
				InitialNetDebit: -1m,
				AdjustedNetDebit: -1m,
				Quantity: 1),
		};

	[Fact]
	public async Task CandidateOpposingHeldLeg_IsBlocked()
	{
		var held = HeldShort590Long580();
		var exec = new OpenerAutoExecutor(Config(), account: null);
		// New candidate: short 620P / long 590P — its BUY leg opposes A's SELL of the same 590P.
		var count = await exec.HandleAsync(new[] { Proposal("SPY250919P00620000", "SPY250919P00590000") }, held, DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(0, count);
	}

	[Fact]
	public async Task CandidateAddingToTheSameSide_Passes()
	{
		var held = HeldShort590Long580();
		var exec = new OpenerAutoExecutor(Config(), account: null);
		// New candidate ALSO sells 590P (same side as A) against a different long leg — just more short
		// size on that contract, not a netting conflict. Must be allowed.
		var count = await exec.HandleAsync(new[] { Proposal("SPY250919P00590000", "SPY250919P00560000") }, held, DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(1, count);
	}

	[Fact]
	public async Task CandidateWithNoOverlap_Passes()
	{
		var held = HeldShort590Long580();
		var exec = new OpenerAutoExecutor(Config(), account: null);
		var count = await exec.HandleAsync(new[] { Proposal("SPY250919P00623000", "SPY250919P00617000") }, held, DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(1, count);
	}
}
