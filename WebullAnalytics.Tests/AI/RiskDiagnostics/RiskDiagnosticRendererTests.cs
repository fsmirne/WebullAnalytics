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
	public void PremiumLineShowsMidAndTheoreticalValuesWhenAvailable()
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
			NetMidPerShare: 0.64m,
			TheoreticalValuePerShare: 0.67m,
			MarketLongPremiumPaid: 0.93m,
			MarketShortPremiumReceived: 0.26m,
			MarketNetPremiumPerShare: 0.67m,
			MarketPremiumRatio: 0.93m / 0.26m,
			TheoreticalLongPremiumPaid: 0.92m,
			TheoreticalShortPremiumReceived: 0.25m,
			TheoreticalNetPremiumPerShare: 0.67m,
			TheoreticalPremiumRatio: 0.92m / 0.25m);

		var text = Render(diagnostic);
		Assert.Contains("Premium:", text);
		Assert.Contains("market → long $0.93 / short $0.26 (3.58× ratio), net debit $0.67 | theoretical → long $0.92 / short $0.25 (3.68× ratio), net debit $0.67", text);
		Assert.DoesNotContain("long $1.00 / short $0.50", text);
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
	public void GreeksLineShowsSmallNonZeroDeltaWithoutRoundingToZero()
	{
		var diagnostic = new RiskDiagnostic(
			StructureLabel: "calendar",
			DirectionalBias: "neutral",
			NetDelta: 0.0043m,
			NetThetaPerDay: 5.14m,
			NetVega: -0.80m,
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
			Rules: Array.Empty<RiskRuleHit>());

		var text = Render(diagnostic);
		Assert.Contains("Δ +0.0043", text);
		Assert.DoesNotContain("Δ +0.00   θ +$5.14/day", text);
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
					Rationale: "debit $50.00\nraw 0.010000 → adjusted 0.020000 → final 0.020492\nrep IV 44.1% / underlying HV 34.6% = 1.27x → vol 0.86; max-pain target $24.50 → pain 1.19\ntech-adjusted × balance 0.16 = adjusted 0.020000\nadjusted × theta factor 1.02 (+1.23/day on $50 risk) = final 0.020492",
					ThetaPerDayPerContract: 1.23m,
					FinalScore: 0.020492m)));

		var text = Render(diagnostic);
		Assert.Contains("Score:", text);
		Assert.Contains("Indicators:", text);
		Assert.Contains("Factors:", text);
		Assert.Contains("Result:", text);
		Assert.DoesNotContain("Final:", text);
        Assert.Contains("max-pain target $24.50 → pain 1.19", text);
		Assert.DoesNotContain("Max-pain:", text);
		Assert.DoesNotContain("Detail:", text);
		Assert.True(text.IndexOf("Indicators:", StringComparison.Ordinal) < text.IndexOf("Factors:", StringComparison.Ordinal));
		Assert.Contains("+1.23/day on $50 risk", text);
		Assert.DoesNotContain("Theta/day:", text);
		Assert.DoesNotContain("Factors: × factors", text);
		Assert.DoesNotContain("Factors: factors:", text);
	}
}
