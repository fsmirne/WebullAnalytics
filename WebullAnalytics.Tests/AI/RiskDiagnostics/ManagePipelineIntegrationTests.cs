using System.Text.Json;
using Spectre.Console;
using Xunit;
using WebullAnalytics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class ManagePipelineIntegrationTests
{
    [Fact]
    public void BuildAndLogDiagnostic_WritesJsonlWithDiagnosticBlock()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"analyze-position-{Guid.NewGuid():N}.jsonl");
        try
        {
            var longLeg = new AnalyzePositionCommand.PositionSnapshot(
                Symbol: "GME260501C00024500", Action: LegAction.Buy, Qty: 100,
                CostBasis: 0.976m,
                Parsed: new OptionParsed("GME", new DateTime(2026, 5, 1), "C", 24.50m));
            var shortLeg = new AnalyzePositionCommand.PositionSnapshot(
                Symbol: "GME260424C00025000", Action: LegAction.Sell, Qty: 100,
                CostBasis: 0.256m,
                Parsed: new OptionParsed("GME", new DateTime(2026, 4, 24), "C", 25.00m));

            AnalyzePositionCommand.BuildAndLogDiagnostic(
                logPath: tmp,
                ticker: "GME",
                positionKey: "GME-DIAG-CALLS",
                legs: new[] { longLeg, shortLeg },
                spot: 24.72m,
                asOf: new DateTime(2026, 4, 24),
                ivResolver: _ => 0.40m,
                legPriceResolver: sym => sym.EndsWith("C00024500") ? 0.71m : 0.07m,
                trend: null,
                console: AnsiConsole.Console);

            var lines = File.ReadAllLines(tmp);
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal("analyze_position", root.GetProperty("type").GetString());
            Assert.Equal("GME", root.GetProperty("ticker").GetString());

            var diag = root.GetProperty("diagnostic");
            Assert.Equal("covered_diagonal", diag.GetProperty("structureLabel").GetString());
            Assert.Equal("bullish", diag.GetProperty("directionalBias").GetString());
            Assert.True(diag.GetProperty("rules").GetArrayLength() > 0);

            var ruleIds = new HashSet<string>();
            foreach (var rule in diag.GetProperty("rules").EnumerateArray())
                ruleIds.Add(rule.GetProperty("id").GetString()!);
            Assert.Contains("directional_exposure", ruleIds);
            Assert.Contains("short_expires_before_long", ruleIds);
            Assert.Contains("geometry_bullish_covered_diagonal", ruleIds);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
