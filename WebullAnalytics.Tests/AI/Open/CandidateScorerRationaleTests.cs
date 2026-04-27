using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerRationaleTests
{
	[Fact]
	public void BuildRationaleSplitsTradeStatsFromScoreBreakdown()
	{
		var proposal = new OpenProposal(
			Ticker: "XYZ",
			StructureKind: OpenStructureKind.LongCalendar,
			Legs: new[]
			{
				new ProposalLeg("buy", "XYZ   260619C00025000", 1),
				new ProposalLeg("sell", "XYZ   260515C00025000", 1)
			},
			Qty: 1,
			DebitOrCreditPerContract: -74.00m,
			MaxProfitPerContract: 36.91m,
			MaxLossPerContract: -74.00m,
			CapitalAtRiskPerContract: 74.00m,
			Breakevens: new[] { 24.66m, 26.49m },
			ProbabilityOfProfit: 0.511m,
			ExpectedValuePerContract: 7.62m,
			DaysToTarget: 4,
			RawScore: 0.025756m,
			BiasAdjustedScore: 0.005466m,
			DirectionalFit: 0,
			Rationale: "",
			Fingerprint: "fp",
			PremiumRatio: 3.06m);

		var rationale = CandidateScorer.BuildRationale(proposal, bias: 0.13m, cfg: new OpenerConfig());
		var lines = rationale.Split('\n');

		Assert.Equal(2, lines.Length);
		Assert.Equal("debit $74.00, maxProfit $36.91, maxLoss $74.00, R/R 0.50, prem 3.06x, BE $24.66/26.49, POP 51.1%, EV $7.62", lines[0]);
		Assert.Contains("raw 0.025756 → tech-adjusted 0.025756", lines[1]);
		Assert.Contains("[tech +0.13, fit 0 → no tech adjustment]", lines[1]);
		Assert.Contains("adjusted 0.005466", lines[1]);
		Assert.Contains("× structure 1.30", lines[1]);
		Assert.Contains("× balance 0.16", lines[1]);
	}
}
