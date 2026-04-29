using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public class BuildReproductionCommandsTests
{
	private static AnalyzePositionCommand.Scenario MakeScenario(string actionSummary, bool isRoll, decimal cashImpactPerContract = 0m) =>
		new(
			Name: "test",
			ActionSummary: actionSummary,
			CashImpactPerContract: cashImpactPerContract,
			ProjectedValuePerContract: 0m,
			TotalPnLPerContract: 0m,
			MarginDeltaPerContract: 0m,
			Qty: 100,
			DaysToTarget: 7,
			Rationale: "",
			IsRoll: isRoll);

	[Fact]
	public void FourLegResetSplitsIntoCloseAndOpenCombos()
	{
		// Real-world example: diagonal being reset to a same-strike calendar. The single 4-leg combo
		// would be rejected by Webull ("Butterfly requires 0 equity legs, 3 option legs"), so the
		// command must split into close-half and open-half combos.
		var sc = MakeScenario(
			"BUY GME260424C00025000 x100 @0.07, SELL GME260501C00024500 x100 @0.71, BUY GME260522C00025000 x100 @1.19, SELL GME260501C00025000 x100 @0.50",
			isRoll: true);

		var (trades, analyze) = AnalyzePositionCommand.BuildReproductionCommands(sc, new AnalyzePositionSettings());

		Assert.NotNull(trades);
		Assert.Equal(2, trades!.Count);
		// Close half: sell 0.71 − buy 0.07 = +0.64 credit; limit is absolute.
		Assert.Equal("wa trade place --trade \"buy:GME260424C00025000:100,sell:GME260501C00024500:100\" --limit 0.64", trades[0]);
		// Open half: sell 0.50 − buy 1.19 = −0.69 debit; limit is absolute.
		Assert.Equal("wa trade place --trade \"buy:GME260522C00025000:100,sell:GME260501C00025000:100\" --limit 0.69", trades[1]);
		Assert.Equal(
			"wa analyze trade \"buy:GME260424C00025000:100@0.07,sell:GME260501C00024500:100@0.71;buy:GME260522C00025000:100@1.19,sell:GME260501C00025000:100@0.50\"",
			analyze);
	}

	[Fact]
	public void TwoLegSameStrikeCalendarRollStaysAsCombo()
	{
		// Same strike, different expiries → Webull accepts the combo reversal.
		var sc = MakeScenario(
			"BUY GME260424C00025000 x100 @0.07, SELL GME260501C00025000 x100 @0.50",
			isRoll: true,
			cashImpactPerContract: 43m);

		var (trades, _) = AnalyzePositionCommand.BuildReproductionCommands(sc, new AnalyzePositionSettings());

		Assert.NotNull(trades);
		Assert.Single(trades!);
		// Combo limit comes from CashImpactPerContract / 100.
		Assert.Equal("wa trade place --trade \"buy:GME260424C00025000:100,sell:GME260501C00025000:100\" --limit 0.43", trades[0]);
	}

	[Fact]
	public void TwoLegDiagonalRollSplitsIntoSingleLegs()
	{
		// Different strike AND different expiry → Webull rejects the combo; split per leg.
		var sc = MakeScenario(
			"BUY GME260424C00025000 x100 @0.07, SELL GME260501C00024500 x100 @0.71",
			isRoll: true);

		var (trades, analyze) = AnalyzePositionCommand.BuildReproductionCommands(sc, new AnalyzePositionSettings());

		Assert.NotNull(trades);
		Assert.Equal(2, trades!.Count);
		Assert.Equal("wa trade place --trade \"buy:GME260424C00025000:100\" --limit 0.07", trades[0]);
		Assert.Equal("wa trade place --trade \"sell:GME260501C00024500:100\" --limit 0.71", trades[1]);
		Assert.Equal("wa analyze trade \"buy:GME260424C00025000:100@0.07;sell:GME260501C00024500:100@0.71\"", analyze);
	}

	[Fact]
	public void ParseSyntheticTrades_KeepsExplicitDiagonalGroupAsSingleStrategyOrder()
	{
		var trades = AnalyzeCommon.ParseSyntheticTrades("sell:GME260501P00025000:152@0.32,buy:GME260522P00025500:152@1.255", startSeq: 1, baseTimestamp: new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc));
		var parent = Assert.Single(trades, t => t.Asset == Asset.OptionStrategy);
		var legs = trades.Where(t => t.Asset == Asset.Option).ToList();

		Assert.Equal("Diagonal", parent.OptionKind);
		Assert.Equal(152, parent.Qty);
		Assert.Equal(2, legs.Count);
		Assert.All(legs, leg => Assert.Equal(parent.Seq, leg.ParentStrategySeq));
	}

	[Fact]
	public void HoldScenarioReturnsNull()
	{
		var sc = MakeScenario("—", isRoll: false);
		var (trades, analyze) = AnalyzePositionCommand.BuildReproductionCommands(sc, new AnalyzePositionSettings());
		Assert.Null(trades);
		Assert.Null(analyze);
	}

	[Fact]
	public void GenerateScenarios_UsesMidPricesInAnalyzePositionSuggestions()
	{
		var expiry = new DateTime(2026, 5, 1);
		var shortSymbol = "GME260501C00025000";
		var longSymbol = "GME260501C00025500";
		var legs = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new(shortSymbol, LegAction.Sell, 100, 0.15m, new OptionParsed("GME", expiry, "C", 25.0m)),
			new(longSymbol, LegAction.Buy, 100, 0.65m, new OptionParsed("GME", expiry, "C", 25.5m)),
		};
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			[shortSymbol] = new(shortSymbol, null, 0.10m, 0.30m, null, null, null, null, 0.40m),
			[longSymbol] = new(longSymbol, null, 0.60m, 0.80m, null, null, null, null, 0.40m),
		};

		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			legs,
			AnalyzePositionCommand.StructureKind.Vertical,
			new AnalyzePositionSettings(),
			spot: 25.2m,
			asOf: new DateTime(2026, 4, 20),
			quotes: quotes);

		Assert.Equal(50m, scenarios[0].CashImpactPerContract);
		var closeAll = Assert.Single(scenarios, s => s.Name == "Close all");
		Assert.Equal($"BUY {shortSymbol} x100 @0.2, SELL {longSymbol} x100 @0.7", closeAll.ActionSummary);
	}
}
