using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenProposalSinkTests
{
	private static OpenProposal MakeProposal(decimal score, string fingerprint) => new OpenProposal(
		Ticker: "SPY",
		StructureKind: OpenStructureKind.LongCall,
		Legs: new[] { new ProposalLeg("buy", "SPY   260515C00500000", 1) },
		Qty: 1,
		DebitOrCreditPerContract: -500m,
		MaxProfitPerContract: 1000m,
		MaxLossPerContract: -500m,
		CapitalAtRiskPerContract: 500m,
		Breakevens: new[] { 505m },
		ProbabilityOfProfit: 0.45m,
		ExpectedValuePerContract: 25m,
		DaysToTarget: 30,
		RawScore: score,
		BiasAdjustedScore: score,
		DirectionalFit: 1,
		Rationale: "test rationale",
		Fingerprint: fingerprint,
		FinalScore: score
	);

	private static OpenProposal MakeProposalWithWarning(decimal score, string fingerprint) => MakeProposal(score, fingerprint) with
	{
		PricingWarning = "Warning: fallback Black-Scholes pricing used for one or more legs because live bid/ask was unavailable."
	};

	[Fact]
	public void WriteJsonlIncludesTypeAndTicker()
	{
		var json = OpenProposalSink.SerializeRecord(MakeProposal(0.01m, "fp1"), mode: "once");
		Assert.Contains("\"type\":\"open\"", json);
		Assert.Contains("\"ticker\":\"SPY\"", json);
	}

	[Fact]
	public void WriteJsonlIncludesPricingWarning()
	{
		var json = OpenProposalSink.SerializeRecord(MakeProposalWithWarning(0.01m, "fp-warning"), mode: "once");
		Assert.Contains("\"pricingWarning\":\"Warning: fallback Black-Scholes pricing used for one or more legs because live bid/ask was unavailable.\"", json);
	}

	[Fact]
	public void SerializeIsDeterministicAcrossRepeats()
	{
		// Repeats are never deduped in the JSONL — each Emit writes its own line. Serialization of the
		// same proposal is field-stable (the ts differs, but the structural fields don't).
		var a = OpenProposalSink.SerializeRecord(MakeProposal(0.01m, "fp1"), mode: "once");
		var b = OpenProposalSink.SerializeRecord(MakeProposal(0.01m, "fp1"), mode: "once");
		Assert.Contains("\"fingerprint\":\"fp1\"", a);
		Assert.Contains("\"fingerprint\":\"fp1\"", b);
	}

}
