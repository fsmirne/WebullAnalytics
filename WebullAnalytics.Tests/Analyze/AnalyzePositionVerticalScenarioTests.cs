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
        Assert.Equal(0m, ironCondor.BPDeltaPerContract);
     Assert.Contains("SELL GME260501C00026000", ironCondor.ActionSummary, StringComparison.Ordinal);
        Assert.Contains("BUY GME260501C00027000", ironCondor.ActionSummary, StringComparison.Ordinal);
        Assert.Contains(scenarios, s => s.Name.StartsWith("Reset to $25.50/$24.50 vertical", StringComparison.Ordinal));
    }
}
