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

	[Fact]
	public async Task HandleAsync_SkipsInformationalProposals()
	{
		var exec = new OpenerAutoExecutor(DryRunConfig(), account: null);
		var proposals = new[]
		{
			Proposal(OpenStructureKind.LongCalendar, informational: false),
			Proposal(OpenStructureKind.DoubleCalendar, informational: true),
		};

		var count = await exec.HandleAsync(proposals, DateTime.UtcNow, CancellationToken.None);

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

		var count = await exec.HandleAsync(proposals, DateTime.UtcNow, CancellationToken.None);

		Assert.Equal(0, count);
	}
}
