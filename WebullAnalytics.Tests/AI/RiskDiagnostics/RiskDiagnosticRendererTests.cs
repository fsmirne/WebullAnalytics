using Spectre.Console;
using WebullAnalytics.AI;
using WebullAnalytics.AI.RiskDiagnostics;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class RiskDiagnosticRendererTests
{
	private static string Render(RiskDiagnostic diagnostic)
	{
		var output = new StringWriter();
		var console = AnsiConsole.Create(new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.No,
			ColorSystem = ColorSystemSupport.NoColors,
			Out = new AnsiConsoleOutput(output),
			Interactive = InteractionSupport.No,
		});
		console.Profile.Width = 160;

		RiskDiagnosticRenderer.WriteConsole(console, diagnostic);
		return output.ToString();
	}

	[Fact]
	public void QuoteLinesIncludeMidValue()
	{
		var diagnostic = new RiskDiagnostic(
			StructureLabel: "calendar",
			DirectionalBias: "neutral",
			NetDelta: 0m,
			NetThetaPerDay: 0m,
			NetVega: 0m,
			ShortLegDteMin: 7,
			LongLegDteMax: 30,
			DteGapDays: 23,
			LongPremiumPaid: 1.00m,
			ShortPremiumReceived: 0.50m,
			NetCashPerShare: -0.50m,
			PremiumRatio: 2.0m,
			SpotAtEvaluation: 25.00m,
			BreakevenDistancePct: null,
			ShortLegOtm: true,
			ShortLegExtrinsic: 0.20m,
			Trend: null,
			CostBasisPerShare: null,
			CurrentValuePerShare: null,
			UnrealizedPnlPerShare: null,
			Rules: Array.Empty<RiskRuleHit>(),
			Probe: new RiskDiagnosticProbe(
				EnumDelta: null,
				EnumDeltaMin: null,
				EnumDeltaMax: null,
				EnumDeltaPass: null,
				LegQuotes: new[]
				{
					new RiskDiagnosticLegQuote("short", "GME260501C00025000", 0.10m, 0.14m, 0.400m, 0.350m, 0.380m, 100, 10),
					new RiskDiagnosticLegQuote("long", "GME260522C00025000", 0.80m, 0.90m, 0.420m, 0.360m, 0.410m, 200, 20),
				},
				OpenerScore: null));

		var text = Render(diagnostic);
		Assert.Contains("Short quote:", text);
		Assert.Contains("Long quote:", text);
		Assert.Contains("mid=0.12", text);
		Assert.Contains("mid=0.85", text);
		Assert.Contains("iv=0.400 hv=0.350 iv5=0.380", text);
		Assert.Contains("iv=0.420 hv=0.360 iv5=0.410", text);
	}

	[Fact]
	public void ShortVerticalProposalShowsMarginRequirement()
	{
		var diagnostic = new RiskDiagnostic(
			StructureLabel: "vertical",
			DirectionalBias: "bullish",
			NetDelta: 0m,
			NetThetaPerDay: 0m,
			NetVega: 0m,
			ShortLegDteMin: 7,
			LongLegDteMax: 7,
			DteGapDays: 0,
			LongPremiumPaid: 0.22m,
			ShortPremiumReceived: 0.60m,
			NetCashPerShare: 0.38m,
			PremiumRatio: 0.37m,
			SpotAtEvaluation: 500.00m,
			BreakevenDistancePct: null,
			ShortLegOtm: true,
			ShortLegExtrinsic: 0.15m,
			Trend: null,
			CostBasisPerShare: null,
			CurrentValuePerShare: null,
			UnrealizedPnlPerShare: null,
			Rules: Array.Empty<RiskRuleHit>(),
			Probe: new RiskDiagnosticProbe(
				EnumDelta: null,
				EnumDeltaMin: null,
				EnumDeltaMax: null,
				EnumDeltaPass: null,
				LegQuotes: Array.Empty<RiskDiagnosticLegQuote>(),
				OpenerScore: new RiskDiagnosticOpenerScore(
					Structure: nameof(OpenStructureKind.ShortPutVertical),
					Qty: 250,
					DebitOrCreditPerContract: 38m,
					MaxProfitPerContract: 38m,
					MaxLossPerContract: -62m,
					CapitalAtRiskPerContract: 62m,
					ProbabilityOfProfit: 0.62m,
					ExpectedValuePerContract: 5m,
					DaysToTarget: 7,
					RawScore: 0.01m,
					BiasAdjustedScore: 0.01m,
					Rationale: "credit $38.00\nraw 0.010000")));

		var text = Render(diagnostic);
		Assert.Contains("Margin:", text);
		Assert.Contains("$15,500.00 total", text);
		Assert.Contains("$62.00/contract", text);
	}

	[Fact]
	public void NonMarginProposalShowsExplicitZeroMargin()
	{
		var diagnostic = new RiskDiagnostic(
			StructureLabel: "calendar",
			DirectionalBias: "neutral",
			NetDelta: 0m,
			NetThetaPerDay: 0m,
			NetVega: 0m,
			ShortLegDteMin: 7,
			LongLegDteMax: 30,
			DteGapDays: 23,
			LongPremiumPaid: 1.00m,
			ShortPremiumReceived: 0.50m,
			NetCashPerShare: -0.50m,
			PremiumRatio: 2.0m,
			SpotAtEvaluation: 25.00m,
			BreakevenDistancePct: null,
			ShortLegOtm: true,
			ShortLegExtrinsic: 0.20m,
			Trend: null,
			CostBasisPerShare: null,
			CurrentValuePerShare: null,
			UnrealizedPnlPerShare: null,
			Rules: Array.Empty<RiskRuleHit>(),
			Probe: new RiskDiagnosticProbe(
				EnumDelta: null,
				EnumDeltaMin: null,
				EnumDeltaMax: null,
				EnumDeltaPass: null,
				LegQuotes: Array.Empty<RiskDiagnosticLegQuote>(),
				OpenerScore: new RiskDiagnosticOpenerScore(
					Structure: nameof(OpenStructureKind.LongCalendar),
					Qty: 250,
					DebitOrCreditPerContract: -50m,
					MaxProfitPerContract: 100m,
					MaxLossPerContract: -50m,
					CapitalAtRiskPerContract: 50m,
					ProbabilityOfProfit: 0.50m,
					ExpectedValuePerContract: 10m,
					DaysToTarget: 7,
					RawScore: 0.01m,
					BiasAdjustedScore: 0.01m,
					Rationale: "debit $50.00\nraw 0.010000")));

		var text = Render(diagnostic);
		Assert.Contains("Margin:", text);
		Assert.Contains("$0 total ($0/contract)", text);
	}

	[Fact]
	public void MultiLineOpenerRationaleUsesScoreAndFactorsLabels()
	{
		var diagnostic = new RiskDiagnostic(
			StructureLabel: "calendar",
			DirectionalBias: "neutral",
			NetDelta: 0m,
			NetThetaPerDay: 0m,
			NetVega: 0m,
			ShortLegDteMin: 7,
			LongLegDteMax: 30,
			DteGapDays: 23,
			LongPremiumPaid: 1.00m,
			ShortPremiumReceived: 0.50m,
			NetCashPerShare: -0.50m,
			PremiumRatio: 2.0m,
			SpotAtEvaluation: 25.00m,
			BreakevenDistancePct: null,
			ShortLegOtm: true,
			ShortLegExtrinsic: 0.20m,
			Trend: null,
			CostBasisPerShare: null,
			CurrentValuePerShare: null,
			UnrealizedPnlPerShare: null,
			Rules: Array.Empty<RiskRuleHit>(),
			Probe: new RiskDiagnosticProbe(
				EnumDelta: null,
				EnumDeltaMin: null,
				EnumDeltaMax: null,
				EnumDeltaPass: null,
				LegQuotes: Array.Empty<RiskDiagnosticLegQuote>(),
				OpenerScore: new RiskDiagnosticOpenerScore(
					Structure: nameof(OpenStructureKind.LongCalendar),
					Qty: 1,
					DebitOrCreditPerContract: -50m,
					MaxProfitPerContract: 100m,
					MaxLossPerContract: -50m,
					CapitalAtRiskPerContract: 50m,
					ProbabilityOfProfit: 0.50m,
					ExpectedValuePerContract: 10m,
					DaysToTarget: 7,
					RawScore: 0.01m,
					BiasAdjustedScore: 0.01m,
					Rationale: "debit $50.00\nraw 0.010000 → adjusted 0.020000\ntech-adjusted × balance 0.16 × theta/day +1.23/contract",
					ThetaPerDayPerContract: 1.23m)));

		var text = Render(diagnostic);
		Assert.Contains("Score:", text);
		Assert.Contains("Factors:", text);
        Assert.Contains("theta/day", text);
		Assert.DoesNotContain("Theta/day:", text);
		Assert.DoesNotContain("Factors: × factors", text);
		Assert.DoesNotContain("Factors: factors:", text);
	}
}
