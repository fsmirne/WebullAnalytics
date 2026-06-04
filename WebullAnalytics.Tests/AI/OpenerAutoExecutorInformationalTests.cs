using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class OpenerAutoExecutorInformationalTests
{
	private static OpenProposal Proposal(OpenStructureKind kind, bool informational) => new(
		Ticker: "SPY",
		StructureKind: kind,
		Legs: new[]
		{
			new ProposalLeg("sell", "SPY260610C00757000", 1, PricePerShare: 5.00m),
			new ProposalLeg("buy", "SPY260619C00757000", 1, PricePerShare: 7.00m),
		},
		Qty: 1,
		DebitOrCreditPerContract: -2.00m,
		MaxProfitPerContract: 100m,
		MaxLossPerContract: -200m,
		CapitalAtRiskPerContract: 200m,
		Breakevens: new[] { 750m, 765m },
		ProbabilityOfProfit: 0.5m,
		ExpectedValuePerContract: 10m,
		DaysToTarget: 7,
		RawScore: 0.01m,
		BiasAdjustedScore: 0.01m,
		DirectionalFit: 0,
		Rationale: "",
		Fingerprint: $"{kind}",
		Informational: informational);

	private static OpenerAutoExecuteConfig DryRunConfig() => new()
	{
		Enabled = true,
		Submit = false,        // dry-run: no broker, deterministic
		MaxOrdersPerDay = 5,   // high enough that both proposals would execute if not filtered
	};

	private static IReadOnlyDictionary<string, OpenPosition> NoPositions() => new Dictionary<string, OpenPosition>();

	[Fact]
	public async Task HandleAsync_SkipsInformationalProposals()
	{
		var exec = new OpenerAutoExecutor(DryRunConfig(), account: null);
		var proposals = new[]
		{
			Proposal(OpenStructureKind.LongCalendar, informational: false),
			Proposal(OpenStructureKind.DoubleCalendar, informational: true),
		};

		var count = await exec.HandleAsync(proposals, NoPositions(), DateTime.UtcNow, CancellationToken.None);

		// Only the non-informational LongCalendar is acted on; the informational double is display-only.
		Assert.Equal(1, count);
	}

	[Fact]
	public async Task HandleAsync_AllInformational_ExecutesNothing()
	{
		var exec = new OpenerAutoExecutor(DryRunConfig(), account: null);
		var proposals = new[]
		{
			Proposal(OpenStructureKind.DoubleCalendar, informational: true),
			Proposal(OpenStructureKind.DoubleDiagonal, informational: true),
		};

		var count = await exec.HandleAsync(proposals, NoPositions(), DateTime.UtcNow, CancellationToken.None);

		Assert.Equal(0, count);
	}

	[Fact]
	public async Task HandleAsync_SkipsProposalAlreadyHeldAsPosition()
	{
		var exec = new OpenerAutoExecutor(DryRunConfig(), account: null);
		var proposals = new[] { Proposal(OpenStructureKind.LongCalendar, informational: false) };

		// A held position with the same leg set — sides match the proposal's open actions (sell near, buy
		// far), qty intentionally differs (3 held vs 1 proposed) to prove the fingerprint ignores quantity.
		var held = new OpenPosition(
			Key: "SPY_CALENDAR_757.00_20260610",
			Ticker: "SPY",
			StrategyKind: "Calendar",
			Legs: new[]
			{
				new PositionLeg("SPY260610C00757000", Side.Sell, 757m, new DateTime(2026, 6, 10), "C", 3),
				new PositionLeg("SPY260619C00757000", Side.Buy,  757m, new DateTime(2026, 6, 19), "C", 3),
			},
			InitialNetDebit: 2.00m,
			AdjustedNetDebit: 2.00m,
			Quantity: 3);
		var positions = new Dictionary<string, OpenPosition> { [held.Key] = held };

		var count = await exec.HandleAsync(proposals, positions, DateTime.UtcNow, CancellationToken.None);

		// Already held → opener must not auto-open a duplicate.
		Assert.Equal(0, count);
	}
}
