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
		Fingerprint: fingerprint
	);

	[Fact]
	public void WriteJsonlAppendsLine()
	{
		var tmp = Path.GetTempFileName();
		try
		{
			using (var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "error" }, mode: "once"))
			{
				sink.Emit(MakeProposal(0.01m, "fp1"));
				sink.Flush();
			}
			var contents = File.ReadAllLines(tmp);
			Assert.Single(contents);
			Assert.Contains("\"type\":\"open\"", contents[0]);
			Assert.Contains("\"ticker\":\"SPY\"", contents[0]);
		}
		finally { File.Delete(tmp); }
	}

	[Fact]
	public void RepeatSameFingerprintStillAppendsJsonl()
	{
		var tmp = Path.GetTempFileName();
		try
		{
			using (var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "error" }, mode: "once"))
			{
				sink.Emit(MakeProposal(0.01m, "fp1"));
				sink.Emit(MakeProposal(0.01m, "fp1"));
				sink.Flush();
			}
			var contents = File.ReadAllLines(tmp);
			Assert.Equal(2, contents.Length);
		}
		finally { File.Delete(tmp); }
	}

	[Fact]
	public void IsRepeatReturnsTrueForUnchangedScore()
	{
		var tmp = Path.GetTempFileName();
		try
		{
			using var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "error" }, mode: "once");
			Assert.False(sink.IsRepeat(MakeProposal(0.01m, "fp1")));
			sink.Emit(MakeProposal(0.01m, "fp1"));
			Assert.True(sink.IsRepeat(MakeProposal(0.01m, "fp1")));
		}
		finally { File.Delete(tmp); }
	}

	[Fact]
	public void IsRepeatReturnsFalseWhenScoreMovesByTenPercent()
	{
		var tmp = Path.GetTempFileName();
		try
		{
			using var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "error" }, mode: "once");
			sink.Emit(MakeProposal(0.01m, "fp1"));
			Assert.False(sink.IsRepeat(MakeProposal(0.0111m, "fp1"))); // +11%
			Assert.True(sink.IsRepeat(MakeProposal(0.0105m, "fp1")));  // +5% — still repeat
		}
		finally { File.Delete(tmp); }
	}
}
