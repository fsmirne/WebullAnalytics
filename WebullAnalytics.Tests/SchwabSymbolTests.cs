using WebullAnalytics;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests;

/// <summary>Guards the Schwab → internal symbol mapping, especially the SPX/SPXW separation: requesting the
/// index underlying ($SPX) returns BOTH the AM-settled monthly SPX root and the PM weekly SPXW root, so an
/// SPXW capture must not be polluted by SPX contracts — the same class of bug seen with Webull and massive.</summary>
public class SchwabSymbolTests
{
	[Theory]
	[InlineData("SPY   260618C00750000", "SPY260618C00750000", "SPY", 750.0)]
	[InlineData("SPX   260619C06000000", "SPX260619C06000000", "SPX", 6000.0)]
	[InlineData("SPXW  260619P06000000", "SPXW260619P06000000", "SPXW", 6000.0)]
	[InlineData("XSP   260619C00600000", "XSP260619C00600000", "XSP", 600.0)]
	public void NormalizeSymbol_DepadsAndParsesToCorrectRoot(string raw, string expectedNorm, string expectedRoot, double expectedStrike)
	{
		var norm = SchwabOptionsClient.NormalizeSymbol(raw);
		Assert.Equal(expectedNorm, norm);

		var parsed = ParsingHelpers.ParseOptionSymbol(norm);
		Assert.NotNull(parsed);
		Assert.Equal(expectedRoot, parsed!.Root);
		Assert.Equal((decimal)expectedStrike, parsed.Strike);
	}

	[Fact]
	public void RootFilter_SeparatesSpxFromSpxw_OnSharedExpiry()
	{
		// Both an SPX monthly and an SPXW weekly expiring the same day, as a $SPX chain request returns them.
		var raws = new[] { "SPX   260619C06000000", "SPXW  260619C06000000", "SPX   260619P06000000", "SPXW  260619P06000000" };
		var parsed = raws
			.Select(SchwabOptionsClient.NormalizeSymbol)
			.Select(ParsingHelpers.ParseOptionSymbol)
			.ToList();
		Assert.All(parsed, p => Assert.NotNull(p));

		// The scraper keeps only contracts whose root == the requested ticker (ScraperLoop root filter).
		var spxwOnly = parsed.Where(p => string.Equals(p!.Root, "SPXW", System.StringComparison.OrdinalIgnoreCase)).ToList();
		Assert.Equal(2, spxwOnly.Count);                 // both SPXW legs kept
		Assert.All(spxwOnly, p => Assert.Equal("SPXW", p!.Root));
		Assert.DoesNotContain(spxwOnly, p => p!.Root == "SPX"); // no AM-settled SPX pollution
	}

	[Theory]
	[InlineData("SPY", "SPY")]
	[InlineData("SPXW", "$SPX")]
	[InlineData("SPX", "$SPX")]
	[InlineData("XSP", "$XSP")]
	[InlineData("AAPL", "AAPL")]
	public void ToSchwabUnderlying_MapsIndexRootsToDollarSymbols(string root, string expected) =>
		Assert.Equal(expected, SchwabOptionsClient.ToSchwabUnderlying(root));

	[Fact]
	public void ParseChain_OffHours_PrefersUnderlyingLastOverAfterHoursUnderlyingPrice()
	{
		const string json = """
		{
			"symbol": "SPY",
			"status": "SUCCESS",
			"underlyingPrice": 735.63,
			"underlying": {
				"symbol": "SPY",
				"last": 730.20,
				"mark": 735.56,
				"close": 732.44
			},
			"callExpDateMap": {},
			"putExpDateMap": {}
		}
		""";

		var (spot, _) = SchwabOptionsClient.ParseChain(json, "SPY", isRth: false);
		Assert.Equal(730.20m, spot);
		Assert.Equal(735.63m, SchwabOptionsClient.TryGetExtendedSpot("SPY"));
	}

	[Fact]
	public void ParseChain_DuringRth_KeepsUnderlyingPriceUnchanged()
	{
		const string json = """
		{
			"symbol": "SPY",
			"status": "SUCCESS",
			"underlyingPrice": 735.63,
			"underlying": {
				"symbol": "SPY",
				"last": 735.50,
				"mark": 735.63,
				"close": 732.44
			},
			"callExpDateMap": {},
			"putExpDateMap": {}
		}
		""";

		var (spot, _) = SchwabOptionsClient.ParseChain(json, "SPY", isRth: true);
		Assert.Equal(735.63m, spot);
	}

	[Fact]
	public void ParseChain_FallsBackToUnderlyingPriceWhenUnderlyingLastMissingOrZero()
	{
		const string json = """
		{
			"symbol": "$SPX",
			"status": "SUCCESS",
			"underlyingPrice": 7323.63,
			"underlying": {
				"symbol": "$SPX",
				"last": 0.0
			},
			"callExpDateMap": {},
			"putExpDateMap": {}
		}
		""";

		var (spot, _) = SchwabOptionsClient.ParseChain(json, "$SPX", isRth: false);
		Assert.Equal(7323.63m, spot);
	}
}
