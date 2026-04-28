using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.RiskDiagnostics;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class ProposalSinkTests
{
	[Fact]
	public void Emit_WritesDiagnosticToJsonl_WhenManagementProposalIncludesOne()
	{
		var tmp = Path.GetTempFileName();
		try
		{
			var diagnostic = new RiskDiagnostic(
				StructureLabel: "calendar",
				DirectionalBias: "neutral",
				NetDelta: 0.01m,
				NetThetaPerDay: 1.23m,
				NetVega: 4.56m,
				ShortLegDteMin: 3,
				LongLegDteMax: 38,
				DteGapDays: 35,
				LongPremiumPaid: 1.35m,
				ShortPremiumReceived: 0.20m,
				NetCashPerShare: -1.15m,
				PremiumRatio: 6.75m,
				SpotAtEvaluation: 25.09m,
				BreakevenDistancePct: null,
				ShortLegOtm: true,
				ShortLegExtrinsic: 0.20m,
				Trend: null,
				CostBasisPerShare: null,
				CurrentValuePerShare: null,
				UnrealizedPnlPerShare: null,
				Rules: Array.Empty<RiskRuleHit>());

			var proposal = new ManagementProposal(
				Rule: "DefensiveRollRule",
				Ticker: "GME",
				PositionKey: "GME_CALENDAR_25.00_20260501",
				Kind: ProposalKind.AlertOnly,
				Legs: new[]
				{
					new ProposalLeg("buy", "GME260501P00025000", 474),
					new ProposalLeg("sell", "GME260508P00024500", 474),
				},
				NetDebit: 0m,
				Rationale: "test rationale",
				Diagnostic: diagnostic);

			using (var sink = new ProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "error" }, mode: "once"))
			{
				sink.Emit(proposal, isRepeat: false);
			}

			var contents = File.ReadAllText(tmp);
			Assert.Contains("\"type\":\"management\"", contents);
			Assert.Contains("\"diagnostic\":{", contents);
			Assert.Contains("\"structureLabel\":\"calendar\"", contents);
		}
		finally
		{
			File.Delete(tmp);
		}
	}
}
