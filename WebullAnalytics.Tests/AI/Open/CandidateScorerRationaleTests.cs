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
			BiasAdjustedScore: 0.010411m,
			DirectionalFit: 0,
			Rationale: "",
			Fingerprint: "fp",
			PremiumRatio: 3.06m,
			GeometryFactor: 0.58m,
			RunwayFactor: 1.14m,
			ThetaPerDayPerContract: 1.50m,
			FinalScore: 0.01072333m);

		var rationale = CandidateScorer.BuildRationale(proposal, bias: 0.13m, cfg: new OpenerConfig());
		var lines = rationale.Split('\n');

		Assert.Equal(4, lines.Length);
		Assert.Equal("debit $74.00, maxProfit $36.91, maxLoss $74.00, R/R 0.50, prem 3.06x, BE $24.66/26.49, POP 51.1%, EV $7.62", lines[0]);
		Assert.Contains("raw 0.025756 → tech-adjusted 0.025756", lines[1]);
		Assert.Contains("[tech +0.13, fit 0 → no tech adjustment]", lines[1]);
		Assert.Contains("adjusted 0.010411 → final 0.010723", lines[1]);
		Assert.Equal("tech-adjusted × pop 1.09 × scale 0.65 × geom 0.58 × runway 1.14 × bal 0.40 = adjusted 0.010411", lines[2]);
		Assert.Equal("adjusted × theta factor 1.03 (+1.50/day on $74 risk) = final 0.010723", lines[3]);
	}

	[Fact]
	public void BuildRationaleLabelsRepresentativeIvAndUnderlyingHvExplicitly()
	{
		var proposal = new OpenProposal(
			Ticker: "GME",
			StructureKind: OpenStructureKind.LongCalendar,
			Legs: new[]
			{
				new ProposalLeg("buy", "GME   260529P00024500", 1),
				new ProposalLeg("sell", "GME   260501P00024500", 1)
			},
			Qty: 1,
			DebitOrCreditPerContract: -50m,
			MaxProfitPerContract: 100m,
			MaxLossPerContract: -50m,
			CapitalAtRiskPerContract: 50m,
			Breakevens: new[] { 23.33m, 25.91m },
			ProbabilityOfProfit: 0.50m,
			ExpectedValuePerContract: 10m,
			DaysToTarget: 7,
			RawScore: 0.010000m,
			BiasAdjustedScore: 0.010000m,
			DirectionalFit: 0,
			Rationale: "",
			Fingerprint: "fp",
			PremiumRatio: 3.06m,
			ImpliedVolatilityAnnual: 0.441m,
			HistoricalVolatilityAnnual: 0.346m,
			VolatilityAdjustmentFactor: 0.86m,
			FinalScore: 0.010000m);

		var rationale = CandidateScorer.BuildRationale(proposal, bias: 0.13m, cfg: new OpenerConfig());
		var lines = rationale.Split('\n');

      Assert.Equal(5, lines.Length);
		Assert.Equal("rep IV 44.1% / underlying HV 34.6% = 1.27x → vol 0.86", lines[2]);
		Assert.StartsWith("tech-adjusted × pop ", lines[3]);
		Assert.Contains(" × vol 0.86 = adjusted 0.010000", lines[3]);
		Assert.Equal("adjusted = final 0.010000", lines[4]);
	}

	[Fact]
	public void BuildRationalePlacesMaxPainTargetOnIndicatorsLine()
	{
		var proposal = new OpenProposal(
			Ticker: "GME",
			StructureKind: OpenStructureKind.LongCalendar,
			Legs: new[]
			{
				new ProposalLeg("buy", "GME   260529P00024500", 1),
				new ProposalLeg("sell", "GME   260501P00024500", 1)
			},
			Qty: 1,
			DebitOrCreditPerContract: -50m,
			MaxProfitPerContract: 100m,
			MaxLossPerContract: -50m,
			CapitalAtRiskPerContract: 50m,
			Breakevens: new[] { 23.33m, 25.91m },
			ProbabilityOfProfit: 0.50m,
			ExpectedValuePerContract: 10m,
			DaysToTarget: 7,
			RawScore: 0.010000m,
			BiasAdjustedScore: 0.010000m,
			DirectionalFit: 0,
			Rationale: "",
			Fingerprint: "fp",
			PremiumRatio: 3.06m,
			MaxPainAdjustmentFactor: 1.19m,
			TargetExpiryMaxPain: 24.50m,
			FinalScore: 0.010000m);

		var rationale = CandidateScorer.BuildRationale(proposal, bias: 0.13m, cfg: new OpenerConfig());
		var lines = rationale.Split('\n');

        Assert.Equal(5, lines.Length);
		Assert.Equal("max-pain target $24.50 → pain 1.19", lines[2]);
		Assert.DoesNotContain("max pain target", lines[3]);
		Assert.DoesNotContain("max-pain target", lines[3]);
	}
}
