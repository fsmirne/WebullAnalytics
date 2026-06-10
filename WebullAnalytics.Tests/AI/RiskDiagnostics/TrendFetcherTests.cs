using WebullAnalytics.AI.RiskDiagnostics;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

public class TrendFetcherTests
{
	// Yahoo-style chart payload: 25 consecutive trading-day bars 2026-03-23 → 2026-04-24, stamped at
	// the 09:30 ET session open (13:30 UTC) like real chart responses. Spot = 24.72.
	private const string CannedJson = """
	{"chart":{"result":[{"meta":{"regularMarketPrice":24.72,"regularMarketTime":1777037400,"chartPreviousClose":25.08},
	"timestamp":[1774272600,1774359000,1774445400,1774531800,1774618200,
				 1774877400,1774963800,1775050200,1775136600,1775223000,
				 1775482200,1775568600,1775655000,1775741400,1775827800,
				 1776087000,1776173400,1776259800,1776346200,1776432600,
				 1776691800,1776778200,1776864600,1776951000,1777037400],
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

		Assert.True(snap.Atr14Pct > 0m);

		// asOf 2026-04-24 = the last bar's own date (intraday evaluation): prior completed session is
		// the bar BEFORE it, 2026-04-23 — high 25.28, low 24.88, close 25.18.
		Assert.Equal(25.28m, snap.PriorHigh);
		Assert.Equal(24.88m, snap.PriorLow);
		Assert.Equal(25.18m, snap.PriorClose);
	}

	[Fact]
	public void PriorSessionIsSelectedByDateNotPosition_PreMarket()
	{
		// Evaluated pre-market on Monday 2026-04-27: the last bar (Friday 04-24) is already a COMPLETED
		// session and must be the prior bar. The old positional count−2 convention would land on 04-23
		// and show one-session-stale levels (the "Monday pivots on Wednesday pre-market" bug).
		var snap = TrendFetcher.ParseSnapshot(CannedJson, new DateTime(2026, 4, 27));
		Assert.NotNull(snap);
		Assert.Equal(25.20m, snap!.PriorHigh);   // 04-24 bar
		Assert.Equal(24.60m, snap.PriorLow);
		Assert.Equal(24.72m, snap.PriorClose);
		// Intraday change anchors to the same date-selected close: spot 24.72 vs 24.72 → 0%.
		Assert.Equal(0m, snap.ChangePctIntraday);
	}

	[Fact]
	public void OvernightNullCloseFallsBackToRegularMarketPrice()
	{
		// The 1 AM Yahoo payload shape: the just-finished session's bar has H/L populated but
		// quote.close (and adjclose) still null — the close exists only as meta.regularMarketPrice,
		// stamped with that session's regularMarketTime. The prior-session row must use it instead of
		// silently dropping the Levels row. regularMarketTime in CannedJson is the last bar's date.
		var overnight = CannedJson.Replace("25.18,24.72]", "25.18,null]"); // null only the final close
		var snap = TrendFetcher.ParseSnapshot(overnight, new DateTime(2026, 4, 27));
		Assert.NotNull(snap);
		Assert.Equal(25.20m, snap!.PriorHigh);
		Assert.Equal(24.60m, snap.PriorLow);
		Assert.Equal(24.72m, snap.PriorClose); // from meta.regularMarketPrice, same-session date match
	}

	[Fact]
	public void FloorPivotRowOrdersLevelsAndPlacesSpotMarker()
	{
		// The verified 2026-06-09 SPY session: H 746.90 L 722.59 C 737.05 → PP 735.51, R1 748.44,
		// R2 759.82, S1 724.13, S2 711.20. Spot 737.05 sits between PP and R1.
		var row = RiskDiagnosticRenderer.FormatFloorPivots(746.90m, 722.59m, 737.05m, spot: 737.05m, ascii: true);
		Assert.StartsWith("S2 711.20  S1 724.13  PP 735.51  <spot>  R1 748.44  R2 759.82", row);

		// Spot above every level → marker lands at the end (before the legend).
		var above = RiskDiagnosticRenderer.FormatFloorPivots(746.90m, 722.59m, 737.05m, spot: 765m, ascii: true);
		Assert.Contains("R2 759.82  <spot>", above);

		// Spot below every level → marker leads.
		var below = RiskDiagnosticRenderer.FormatFloorPivots(746.90m, 722.59m, 737.05m, spot: 700m, ascii: true);
		Assert.StartsWith("<spot>  S2 711.20", below);
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
