using Spectre.Console;
using Spectre.Console.Rendering;
using WebullAnalytics.Api;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Trading;

public class TradeContextTests
{
	private static string Render(IRenderable renderable)
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
		console.Write(renderable);
		return output.ToString();
	}

	[Fact]
	public void BuildPositionPanel_RendersReadableSummaryWithLegs()
	{
		List<WebullOpenApiClient.AccountHolding> holdings =
		[
			new(
				PositionId: "pos-1",
				Symbol: "GME",
				InstrumentType: "OPTION",
				OptionStrategy: "calendar",
				Currency: "USD",
				CostPrice: "1.25",
				Quantity: "2",
				Cost: "250",
				LastPrice: "1.65",
				MarketValue: "330",
				UnrealizedProfitLoss: "80",
				UnrealizedProfitLossRate: "0.32",
				Proportion: "0.18",
				DayProfitLoss: "-5",
				DayRealizedProfitLoss: "0",
				Legs:
				[
					new(
						Symbol: "GME260501C00025000",
						LegId: "leg-1",
						InstrumentType: "OPTION",
						Cost: "120",
						LastPrice: "0.45",
						Proportion: "0.50",
						UnrealizedProfitLoss: "-30",
						OptionType: "CALL",
						OptionExpireDate: "2026-05-01",
						OptionExercisePrice: "25",
						OptionContractMultiplier: "100"),
					new(
						Symbol: "GME260515C00025000",
						LegId: "leg-2",
						InstrumentType: "OPTION",
						Cost: "130",
						LastPrice: "1.20",
						Proportion: "0.50",
						UnrealizedProfitLoss: "110",
						OptionType: "CALL",
						OptionExpireDate: "2026-05-15",
						OptionExercisePrice: "25",
						OptionContractMultiplier: "100")
				])
		];

		var panel = TradeContext.BuildPositionPanel(holdings[0]);
		var text = Render(panel);

		Assert.Contains("GME (OPTION 2x)", text);
		Assert.Contains("Strategy: calendar", text);
		Assert.Contains("Unrealized P/L: $80.00 (32%)", text);
		Assert.Contains("Position ID: pos-1", text);
		Assert.Contains("Legs:", text);
		Assert.Contains("GME260501C00025000 [OPTION] CALL strike=$25.00 exp=2026-05-01", text);
		Assert.Contains("weight=50%", text);
	}

	[Fact]
	public void BuildPositionPanels_ReturnsEmptyListWhenNoPositions()
	{
		List<WebullOpenApiClient.AccountHolding> holdings = [];

		var panels = TradeContext.BuildPositionPanels(holdings);

		Assert.Empty(panels);
	}
}
