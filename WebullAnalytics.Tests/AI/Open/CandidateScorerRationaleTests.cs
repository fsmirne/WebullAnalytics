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

	[Fact]
	public void BuildRationalePlacesStatArbDetailOnIndicatorsLine()
	{
		var proposal = new OpenProposal(
			Ticker: "GME",
			StructureKind: OpenStructureKind.IronCondor,
			Legs: new[]
			{
				new ProposalLeg("buy", "GME   260501P00023000", 1),
				new ProposalLeg("sell", "GME   260501P00024000", 1),
				new ProposalLeg("sell", "GME   260501C00025000", 1),
				new ProposalLeg("buy", "GME   260501C00026000", 1)
			},
			Qty: 1,
			DebitOrCreditPerContract: 28m,
			MaxProfitPerContract: 28m,
			MaxLossPerContract: -72m,
			CapitalAtRiskPerContract: 72m,
			Breakevens: new[] { 23.72m, 25.28m },
			ProbabilityOfProfit: 0.54m,
			ExpectedValuePerContract: 7.12m,
			DaysToTarget: 2,
			RawScore: 0.049112m,
			BiasAdjustedScore: 0.022291m,
			DirectionalFit: 0,
			Rationale: "",
			Fingerprint: "fp",
			PremiumRatio: 0.28m,
			MarketNetPremiumPerShare: -0.28m,
			TheoreticalNetPremiumPerShare: -0.26m,
			StatArbAdjustmentFactor: 1.01m,
			FinalScore: 0.022291m);

		var rationale = CandidateScorer.BuildRationale(proposal, bias: 0.05m, cfg: new OpenerConfig());
		var lines = rationale.Split('\n');

		Assert.Equal(5, lines.Length);
		Assert.Equal("market net $-0.28 / theoretical net $-0.26, edge $+0.02/share → arb 1.01", lines[2]);
		Assert.Contains(" × arb 1.01 = adjusted 0.022291", lines[3]);
	}

	[Fact]
	public void StatArbFactorBoostsDebitWhenContractIsCheap()
	{
		// Long call (debit) priced below BS theoretical → market is cheap → favorable to buyer → boost.
		var factor = CandidateScorer.ComputeStatArbAdjustmentFactor(
			marketNet: 1.00m,        // paid $1.00
			theoreticalNet: 1.20m,   // BS-fair $1.20
			grossTheoretical: 1.20m,
			weight: 0.30m);

		Assert.NotNull(factor);
		Assert.True(factor.Value > 1m, $"expected boost (>1), got {factor.Value}");
	}

	[Fact]
	public void StatArbFactorCutsDebitWhenContractIsExpensive()
	{
		var factor = CandidateScorer.ComputeStatArbAdjustmentFactor(
			marketNet: 1.20m,        // paid $1.20
			theoreticalNet: 1.00m,   // BS-fair $1.00
			grossTheoretical: 1.00m,
			weight: 0.30m);

		Assert.NotNull(factor);
		Assert.True(factor.Value < 1m, $"expected cut (<1), got {factor.Value}");
	}

	[Fact]
	public void StatArbFactorCutsCreditWhenContractIsCheap()
	{
		// Short call (credit) sold cheap → received less than fair → cut.
		// marketNet = −marketShort = −1.00 (received $1.00); theoNet = −1.20 (fair credit $1.20).
		var factor = CandidateScorer.ComputeStatArbAdjustmentFactor(
			marketNet: -1.00m,
			theoreticalNet: -1.20m,
			grossTheoretical: 1.20m,
			weight: 0.30m);

		Assert.NotNull(factor);
		Assert.True(factor.Value < 1m, $"expected cut (<1), got {factor.Value}");
	}

	[Fact]
	public void StatArbFactorBoostsCreditWhenContractIsExpensive()
	{
		// Short call sold expensive → received more than fair → boost.
		var factor = CandidateScorer.ComputeStatArbAdjustmentFactor(
			marketNet: -1.20m,
			theoreticalNet: -1.00m,
			grossTheoretical: 1.00m,
			weight: 0.30m);

		Assert.NotNull(factor);
		Assert.True(factor.Value > 1m, $"expected boost (>1), got {factor.Value}");
	}

	[Fact]
	public void StatArbFactorIsNullWhenWeightIsZero()
	{
		var factor = CandidateScorer.ComputeStatArbAdjustmentFactor(
			marketNet: 1.00m, theoreticalNet: 1.20m, grossTheoretical: 1.20m, weight: 0m);
		Assert.Null(factor);
	}

	[Fact]
	public void StatArbFactorIsFlooredAt010()
	{
		// Extreme negative edge clamps to relative=−1; with weight 1.5, factor would be 1−1.5=−0.5; floor at 0.10.
		var factor = CandidateScorer.ComputeStatArbAdjustmentFactor(
			marketNet: 10m, theoreticalNet: 1m, grossTheoretical: 1m, weight: 1.5m);
		Assert.NotNull(factor);
		Assert.Equal(0.10m, factor.Value);
	}
}
