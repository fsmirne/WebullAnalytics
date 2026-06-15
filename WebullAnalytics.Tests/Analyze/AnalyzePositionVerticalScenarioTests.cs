using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public class AnalyzePositionVerticalScenarioTests
{
	[Fact]
	public void AssignmentRiskPenalty_NearMoneyShortPutDropsWhenBiasIsStronglyBullish()
	{
		var neutral = AnalyzePositionCommand.ComputeAssignmentRiskPenaltyPerContract(
			spot: 25.56m,
			shortStrike: 25.50m,
			callPut: "P",
			daysToTarget: 4,
			strikeStep: 0.50m,
			technicalBias: 0m);

		var stronglyBullish = AnalyzePositionCommand.ComputeAssignmentRiskPenaltyPerContract(
			spot: 25.56m,
			shortStrike: 25.50m,
			callPut: "P",
			daysToTarget: 4,
			strikeStep: 0.50m,
			technicalBias: 1m);

		Assert.True(neutral > stronglyBullish);
		Assert.True(neutral > 0m);
	}

	[Fact]
	public void GenerateScenarios_VerticalProducesManagementOptions()
	{
		var expiry = new DateTime(2026, 5, 1);
		var legs = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new("GME260501P00024500", LegAction.Sell, 396, 0.06m, new OptionParsed("GME", expiry, "P", 24.50m)),
			new("GME260501P00023500", LegAction.Buy, 396, 0.21m, new OptionParsed("GME", expiry, "P", 23.50m)),
		};
		var settings = new AnalyzePositionSettings
		{
			IvDefault = 40m,
			StrikeStep = 0.50m,
		};

		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			legs,
			AnalyzePositionCommand.StructureKind.Vertical,
			settings,
			spot: 25.56m,
			asOf: new DateTime(2026, 4, 27),
			quotes: null);

		Assert.NotEmpty(scenarios);
		Assert.False(scenarios[0].Name.StartsWith("Roll short to $25.50 (same exp 05-01, credit vertical)", StringComparison.Ordinal));
		Assert.Contains(scenarios, s => s.Name == "Hold to expiry");
		Assert.Contains(scenarios, s => s.Name == "Close short only");
		Assert.Contains(scenarios, s => s.Name == "Close all");
		Assert.Contains(scenarios, s => s.Name.StartsWith("Roll short to $25.50", StringComparison.Ordinal));
		var ironCondor = Assert.Single(scenarios, s => s.Name.StartsWith("Add complementary call spread", StringComparison.Ordinal));
		Assert.Equal(0m, ironCondor.MarginDeltaPerContract);
		Assert.Contains("SELL GME260501C00026000", ironCondor.ActionSummary, StringComparison.Ordinal);
		Assert.Contains("BUY GME260501C00027000", ironCondor.ActionSummary, StringComparison.Ordinal);
		Assert.Contains(scenarios, s => s.Name.StartsWith("Reset to $25.50/$24.50 vertical", StringComparison.Ordinal));
	}

	[Fact]
	public void GenerateScenarios_CalendarShowsFundablePartialRollInsteadOfUnfundableFullRoll()
	{
		var shortExpiry = new DateTime(2026, 5, 1);
		var longExpiry = new DateTime(2026, 5, 29);
		var legs = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new("GME260501C00026000", LegAction.Sell, 10, 0.35m, new OptionParsed("GME", shortExpiry, "C", 26.00m)),
			new("GME260529C00026000", LegAction.Buy, 10, 1.05m, new OptionParsed("GME", longExpiry, "C", 26.00m)),
		};
		var settings = new AnalyzePositionSettings
		{
			Cash = 400m,
			IvDefault = 40m,
			StrikeStep = 0.50m,
		};

		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			legs,
			AnalyzePositionCommand.StructureKind.Calendar,
			settings,
			spot: 25.56m,
			asOf: new DateTime(2026, 4, 27),
			quotes: null);

		Assert.DoesNotContain(scenarios, s => s.Name == "Roll short to $25.50 (same exp 05-01, inverted diagonal)");

		var partialRoll = Assert.Single(scenarios, s => s.Name.StartsWith("Roll short to $25.50 (same exp 05-01, inverted diagonal) · partial ", StringComparison.Ordinal));
		Assert.True(partialRoll.MarginDeltaPerContract * partialRoll.Qty <= settings.Cash);
		Assert.Contains("full size would need", partialRoll.Rationale, StringComparison.Ordinal);
	}

	[Fact]
	public void ClassifyAndGenerate_PutCondorIsSupported()
	{
		var expiry = new DateTime(2026, 7, 17);
		// The reported USO long put condor: long wings (80, 110), short body (90, 100).
		var legs = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new("USO260717P00110000", LegAction.Buy, 20, 1.95m, new OptionParsed("USO", expiry, "P", 110m)),
			new("USO260717P00100000", LegAction.Sell, 20, 0.60m, new OptionParsed("USO", expiry, "P", 100m)),
			new("USO260717P00090000", LegAction.Sell, 20, 0.20m, new OptionParsed("USO", expiry, "P", 90m)),
			new("USO260717P00080000", LegAction.Buy, 20, 0.11m, new OptionParsed("USO", expiry, "P", 80m)),
		};

		Assert.Equal(AnalyzePositionCommand.StructureKind.Condor, AnalyzePositionCommand.ClassifyStructure(legs));

		var settings = new AnalyzePositionSettings { IvDefault = 45m, StrikeStep = 1m };
		var scenarios = AnalyzePositionCommand.GenerateScenarios(
			legs, AnalyzePositionCommand.StructureKind.Condor, settings,
			spot: 95m, asOf: new DateTime(2026, 6, 15), quotes: null);

		Assert.NotEmpty(scenarios);
		Assert.Contains(scenarios, s => s.Name == "Close all");
		Assert.Contains(scenarios, s => s.Name.StartsWith("Hold to expiry (plateau mid", StringComparison.Ordinal));
		Assert.Contains(scenarios, s => s.Name.StartsWith("Hold to expiry (lower wing", StringComparison.Ordinal));
		Assert.Contains(scenarios, s => s.Name.StartsWith("Hold to expiry (upper wing", StringComparison.Ordinal));
	}

	[Fact]
	public void Classify_CallCondorSupported_StackedVerticalsAreNot()
	{
		var exp = new DateTime(2026, 7, 17);
		// Long call condor: buy wings, sell body -> Condor.
		var condor = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new("USO260717C00080000", LegAction.Buy, 1, 1m, new OptionParsed("USO", exp, "C", 80m)),
			new("USO260717C00090000", LegAction.Sell, 1, 1m, new OptionParsed("USO", exp, "C", 90m)),
			new("USO260717C00100000", LegAction.Sell, 1, 1m, new OptionParsed("USO", exp, "C", 100m)),
			new("USO260717C00110000", LegAction.Buy, 1, 1m, new OptionParsed("USO", exp, "C", 110m)),
		};
		Assert.Equal(AnalyzePositionCommand.StructureKind.Condor, AnalyzePositionCommand.ClassifyStructure(condor));

		// Long/short alternating by strike (not wings/body) is two stacked verticals, not a condor.
		var stacked = new List<AnalyzePositionCommand.PositionSnapshot>
		{
			new("USO260717C00080000", LegAction.Buy, 1, 1m, new OptionParsed("USO", exp, "C", 80m)),
			new("USO260717C00090000", LegAction.Sell, 1, 1m, new OptionParsed("USO", exp, "C", 90m)),
			new("USO260717C00100000", LegAction.Buy, 1, 1m, new OptionParsed("USO", exp, "C", 100m)),
			new("USO260717C00110000", LegAction.Sell, 1, 1m, new OptionParsed("USO", exp, "C", 110m)),
		};
		Assert.NotEqual(AnalyzePositionCommand.StructureKind.Condor, AnalyzePositionCommand.ClassifyStructure(stacked));
	}
}
