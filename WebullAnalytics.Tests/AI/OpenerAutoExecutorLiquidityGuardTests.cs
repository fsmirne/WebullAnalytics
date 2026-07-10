using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class OpenerAutoExecutorLiquidityGuardTests
{
	// Mirrors the live LongDiagonal that auto-submitted despite a dead short leg: worst-leg spread 67%,
	// worst-leg relative OI ~0%. The guard must reject it; a clean-liquidity proposal must pass.
	private static OpenProposal Proposal(decimal? worstSpreadPct, decimal? relOi) => new(
		Ticker: "SPY",
		StructureKind: OpenStructureKind.LongDiagonal,
		Legs: new[]
		{
			new ProposalLeg("sell", "SPY260713C00775000", 15, PricePerShare: 0.02m),
			new ProposalLeg("buy", "SPY260724C00769000", 15, PricePerShare: 1.00m),
		},
		Qty: 15,
		DebitOrCreditPerContract: -0.98m,
		MaxProfitPerContract: 806m,
		MaxLossPerContract: -98m,
		CapitalAtRiskPerContract: 98m,
		Breakevens: new[] { 756.25m },
		ProbabilityOfProfit: 0.379m,
		ExpectedValuePerContract: 36m,
		DaysToTarget: 3,
		RawScore: 0.0025m,
		BiasAdjustedScore: 0.0025m,
		DirectionalFit: 1,
		Rationale: "",
		Fingerprint: "LongDiagonal",
		WorstLegBidAskSpreadPct: worstSpreadPct,
		MinRelativeOpenInterest: relOi);

	private static OpenerAutoExecuteConfig Config(decimal maxSpread = 0m, decimal minRelOi = 0m) => new()
	{
		Enabled = true,
		Submit = false,       // dry-run: no broker, deterministic
		MaxOrdersPerDay = 5,
		MaxWorstLegSpreadPct = maxSpread,
		MinRelativeOpenInterest = minRelOi,
	};

	private static IReadOnlyDictionary<string, OpenPosition> NoPositions() => new Dictionary<string, OpenPosition>();

	[Fact]
	public async Task WideSpread_IsBlocked()
	{
		var exec = new OpenerAutoExecutor(Config(maxSpread: 0.25m), account: null);
		var count = await exec.HandleAsync(new[] { Proposal(worstSpreadPct: 0.67m, relOi: 0.90m) }, NoPositions(), DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(0, count);
	}

	[Fact]
	public async Task SubGridRelativeOi_IsBlocked()
	{
		var exec = new OpenerAutoExecutor(Config(minRelOi: 0.25m), account: null);
		var count = await exec.HandleAsync(new[] { Proposal(worstSpreadPct: 0.05m, relOi: 0.00m) }, NoPositions(), DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(0, count);
	}

	[Fact]
	public async Task CleanLiquidity_Passes()
	{
		var exec = new OpenerAutoExecutor(Config(maxSpread: 0.25m, minRelOi: 0.25m), account: null);
		var count = await exec.HandleAsync(new[] { Proposal(worstSpreadPct: 0.05m, relOi: 0.90m) }, NoPositions(), DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(1, count);
	}

	[Fact]
	public async Task GuardDisabled_ByDefault_LetsBadTradeThrough()
	{
		// Thresholds default to 0 (off): the illiquid trade must still execute, proving prod behavior is
		// unchanged until the guard is explicitly configured.
		var exec = new OpenerAutoExecutor(Config(), account: null);
		var count = await exec.HandleAsync(new[] { Proposal(worstSpreadPct: 0.67m, relOi: 0.00m) }, NoPositions(), DateTime.UtcNow, CancellationToken.None);
		Assert.Equal(1, count);
	}
}
