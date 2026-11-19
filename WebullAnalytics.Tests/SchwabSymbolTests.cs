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

	[Fact]
	public void ParseChain_Premarket_LiveOptionBook_UsesExtendedUnderlyingPrice()
	{
		// 2026-11-19 08:30 ET (premarket on a trading day). SPY closed at 730.20 yesterday (underlying.last),
		// while top-level underlyingPrice is 739.00 in premarket and the option quotes are stamped at 08:25 ET
		// today and price ~739 via put-call parity (739C mid 3.15, 739P mid 3.15).
		var nowEt = new DateTime(2026, 11, 19, 8, 30, 0);
		var quoteEpochMs = MarketTime.EtToInstant(new DateTime(2026, 11, 19, 8, 25, 0)).ToUnixTimeMilliseconds();
		var json = $$"""
		{
			"symbol": "SPY",
			"status": "SUCCESS",
			"underlyingPrice": 739.00,
			"underlying": {
				"symbol": "SPY",
				"last": 730.20,
				"mark": 739.00,
				"close": 730.20
			},
			"callExpDateMap": {
				"2026-11-19:0": {
					"739.0": [
						{ "symbol": "SPY   261119C00739000", "bid": 3.10, "ask": 3.20, "quoteTimeInLong": {{quoteEpochMs}} }
					]
				}
			},
			"putExpDateMap": {
				"2026-11-19:0": {
					"739.0": [
						{ "symbol": "SPY   261119P00739000", "bid": 3.10, "ask": 3.20, "quoteTimeInLong": {{quoteEpochMs}} }
					]
				}
			}
		}
		""";

		var (spot, _) = SchwabOptionsClient.ParseChain(json, "SPY", isRth: false, nowEtOverride: nowEt);
		Assert.Equal(739.00m, spot);
	}

	[Fact]
	public void ParseChain_Premarket_FrozenOptionBook_With0630OpraRefreshStamp_KeepsRegularClose()
	{
		// 2026-11-19 08:30 ET (premarket). USO closed at 136.11 yesterday (underlying.last) and is trading
		// at 138.60 in premarket (underlyingPrice), but its option quotes are still frozen at yesterday's
		// 136.11 level (138C mid 1.10, 138P mid 2.98 -> parity ~136.12) with OPRA's 06:30:09 ET refresh stamp.
		var nowEt = new DateTime(2026, 11, 19, 8, 30, 0);
		var opraRefreshEpochMs = MarketTime.EtToInstant(new DateTime(2026, 11, 19, 6, 30, 9)).ToUnixTimeMilliseconds();
		var json = $$"""
		{
			"symbol": "USO",
			"status": "SUCCESS",
			"underlyingPrice": 138.60,
			"underlying": {
				"symbol": "USO",
				"last": 136.11,
				"mark": 138.60,
				"close": 136.11
			},
			"callExpDateMap": {
				"2026-11-20:1": {
					"138.0": [
						{ "symbol": "USO   261120C00138000", "bid": 1.05, "ask": 1.15, "quoteTimeInLong": {{opraRefreshEpochMs}} }
					]
				}
			},
			"putExpDateMap": {
				"2026-11-20:1": {
					"138.0": [
						{ "symbol": "USO   261120P00138000", "bid": 2.91, "ask": 3.05, "quoteTimeInLong": {{opraRefreshEpochMs}} }
					]
				}
			}
		}
		""";

		var (spot, quotes) = SchwabOptionsClient.ParseChain(json, "USO", isRth: false, nowEtOverride: nowEt);
		Assert.Equal(136.11m, spot);
		var dict = quotes.ToDictionary(q => q.ContractSymbol);
		Assert.Null(WebullAnalytics.AI.QuoteSanity.FreshestLiveQuoteTimeForAnchor(dict, nowEt));
	}

	[Fact]
	public void WebullParseStrategyList_Premarket_DetectsLiveVsFrozenOptionBook()
	{
		var nowEt = new DateTime(2026, 11, 19, 8, 30, 0);

		// Live premarket SPY book: close is 730.20, top-level bid/ask is 738.98/739.02, and 739C/739P mids match ~739.00.
		const string liveSpyJson = """
		{
			"close": "730.20",
			"bidList": [{ "price": "738.98", "volume": "100" }],
			"askList": [{ "price": "739.02", "volume": "100" }],
			"expireDateList": [
				{
					"data": [
						{ "symbol": "SPY261119C00739000", "tickerId": 1, "bidList": [{ "price": "3.10" }], "askList": [{ "price": "3.20" }] },
						{ "symbol": "SPY261119P00739000", "tickerId": 2, "bidList": [{ "price": "3.10" }], "askList": [{ "price": "3.20" }] }
					]
				}
			]
		}
		""";
		var (_, spySpot) = WebullOptionsClient.ParseStrategyListResponse(liveSpyJson, new(), "SPY", nowEt);
		Assert.Equal(739.00m, spySpot);

		// Frozen premarket USO book: close is 136.11, top-level bid/ask is 138.70/138.72, but 136C/136P mids price 136.13.
		const string frozenUsoJson = """
		{
			"close": "136.11",
			"bidList": [{ "price": "138.70", "volume": "100" }],
			"askList": [{ "price": "138.72", "volume": "100" }],
			"expireDateList": [
				{
					"data": [
						{ "symbol": "USO261120C00136000", "tickerId": 3, "bidList": [{ "price": "1.92" }], "askList": [{ "price": "1.95" }] },
						{ "symbol": "USO261120P00136000", "tickerId": 4, "bidList": [{ "price": "1.74" }], "askList": [{ "price": "1.86" }] }
					]
				}
			]
		}
		""";
		var (_, usoSpot) = WebullOptionsClient.ParseStrategyListResponse(frozenUsoJson, new(), "USO", nowEt);
		Assert.Equal(136.11m, usoSpot);
	}
}
