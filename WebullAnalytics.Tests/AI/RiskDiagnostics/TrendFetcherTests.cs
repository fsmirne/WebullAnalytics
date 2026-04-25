using Xunit;
using WebullAnalytics.AI.RiskDiagnostics;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class TrendFetcherTests
{
    // Yahoo-style chart payload with 25 bars. Spot = 24.72, prev close = 25.08 → intraday ≈ −1.43%.
    private const string CannedJson = """
    {"chart":{"result":[{"meta":{"regularMarketPrice":24.72,"regularMarketTime":1777044900,"chartPreviousClose":25.08},
    "timestamp":[1774550400,1774636800,1774723200,1774809600,1774896000,
                 1775155200,1775241600,1775328000,1775414400,1775500800,
                 1775760000,1775846400,1775932800,1776019200,1776105600,
                 1776364800,1776451200,1776537600,1776624000,1776710400,
                 1776969600,1777056000,1777142400,1777228800,1777044900],
    "indicators":{"quote":[{"open":[25.40,25.30,25.20,25.15,25.10,25.05,25.00,25.05,25.10,25.15,
                                   25.20,25.25,25.30,25.28,25.26,25.24,25.22,25.20,25.18,25.16,
                                   25.14,25.12,25.10,25.08,25.08],
                           "high":[25.60,25.50,25.40,25.35,25.30,25.25,25.20,25.25,25.30,25.35,
                                   25.40,25.45,25.50,25.48,25.46,25.44,25.42,25.40,25.38,25.36,
                                   25.34,25.32,25.30,25.28,25.20],
                           "low":[25.20,25.10,25.00,24.95,24.90,24.85,24.80,24.85,24.90,24.95,
                                  25.00,25.05,25.10,25.08,25.06,25.04,25.02,25.00,24.98,24.96,
                                  24.94,24.92,24.90,24.88,24.60],
                           "close":[25.50,25.40,25.30,25.25,25.20,25.15,25.10,25.15,25.20,25.25,
                                    25.30,25.35,25.40,25.38,25.36,25.34,25.32,25.30,25.28,25.26,
                                    25.24,25.22,25.20,25.18,24.72]}]}}]}}
    """;

    [Fact]
    public void ParsesSnapshotFromCannedJson()
    {
        var snap = TrendFetcher.ParseSnapshot(CannedJson, new DateTime(2026, 4, 24));
        Assert.NotNull(snap);

        // Intraday: spot 24.72 vs yesterday's close (second-to-last bar) = 25.18 → ≈ −1.83%
        Assert.NotNull(snap!.ChangePctIntraday);
        Assert.InRange(snap.ChangePctIntraday!.Value, -1.85m, -1.80m);

        // 5-day return: spot 24.72 vs closes[19] = 25.26 → ≈ −2.14%
        Assert.InRange(snap.ChangePct5Day, -2.2m, -2.0m);

        // 20-day return: spot 24.72 vs closes[4] = 25.20 → ≈ −1.9%
        Assert.InRange(snap.ChangePct20Day, -2.0m, -1.8m);

        Assert.True(snap.Spot20DayAtrPct > 0m);
    }

    [Fact]
    public void ReturnsNullOnInsufficientBars()
    {
        const string shortJson = """
        {"chart":{"result":[{"meta":{"regularMarketPrice":24.72,"chartPreviousClose":25.08},
        "timestamp":[1,2,3],
        "indicators":{"quote":[{"open":[1,2,3],"high":[1,2,3],"low":[1,2,3],"close":[1,2,3]}]}}]}}
        """;
        Assert.Null(TrendFetcher.ParseSnapshot(shortJson, DateTime.Today));
    }

    [Fact]
    public void ReturnsNullOnMalformedJson()
    {
        Assert.Null(TrendFetcher.ParseSnapshot("not json", DateTime.Today));
        Assert.Null(TrendFetcher.ParseSnapshot("{}", DateTime.Today));
    }
}
