using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class StrikeLadderTests
{
	private static readonly DateTime Exp = new(2026, 4, 24);

	// A deliberately NON-uniform chain: $1 spacing near the money (595–605), $5 in the wings (560–590,
	// 610–640). This is the SPX-family shape a single strikeStep can't represent.
	private static Dictionary<string, OptionContractQuote> NonUniformChain(string callPut)
	{
		var strikes = new List<decimal>();
		for (decimal k = 560m; k <= 590m; k += 5m) strikes.Add(k);   // lower wing, $5
		for (decimal k = 595m; k <= 605m; k += 1m) strikes.Add(k);   // ATM, $1
		for (decimal k = 610m; k <= 640m; k += 5m) strikes.Add(k);   // upper wing, $5
		var d = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var k in strikes)
		{
			var sym = MatchKeys.OccSymbol("SPY", Exp, k, callPut);
			d[sym] = new OptionContractQuote(sym, 1m, 0.9m, 1.1m, null, null, 100, 100, 0.2m);
		}
		return d;
	}

	[Fact]
	public void EmptyWhenNoChain()
	{
		Assert.True(StrikeLadder.Build("SPY", Exp, "C", null).IsEmpty);
		Assert.True(StrikeLadder.Build("SPY", Exp, "C", new Dictionary<string, OptionContractQuote>()).IsEmpty);
	}

	[Fact]
	public void BuildIgnoresOtherRootsExpiriesAndSides()
	{
		var chain = NonUniformChain("C");
		// Add noise: wrong side, wrong expiry, wrong root — none should leak into the C/Exp/SPY ladder.
		var put = MatchKeys.OccSymbol("SPY", Exp, 600m, "P");
		chain[put] = new OptionContractQuote(put, 1m, 0.9m, 1.1m, null, null, 1, 1, 0.2m);
		var otherExp = MatchKeys.OccSymbol("SPY", new DateTime(2026, 5, 15), 600m, "C");
		chain[otherExp] = new OptionContractQuote(otherExp, 1m, 0.9m, 1.1m, null, null, 1, 1, 0.2m);
		var ladder = StrikeLadder.Build("SPY", Exp, "C", chain);
		Assert.DoesNotContain(601.5m, ladder.Around(600m, 50)); // sanity: no fabricated strikes
		Assert.Equal(new[] { 599m, 598m, 597m }, ladder.Below(600m, 3));
	}

	[Fact]
	public void BelowAndAboveReturnNearestListedStrikes()
	{
		var ladder = StrikeLadder.Build("SPY", Exp, "C", NonUniformChain("C"));
		Assert.Equal(new[] { 599m, 598m, 597m }, ladder.Below(600m, 3));
		Assert.Equal(new[] { 601m, 602m, 603m }, ladder.Above(600m, 3));
	}

	[Fact]
	public void OffsetCountsStrikesAlongLadderAcrossVariableSpacing()
	{
		var ladder = StrikeLadder.Build("SPY", Exp, "C", NonUniformChain("C"));
		// 2 strikes up inside the $1 region: 600 → 601 → 602.
		Assert.Equal(602m, ladder.Offset(600m, 2));
		// 2 strikes up STARTING at the $1/$5 boundary: 605 → 610 → 615. A fixed $2 step would have asked
		// for 607 (not listed) and the leg would come back unpriced — the exact live failure.
		Assert.Equal(615m, ladder.Offset(605m, 2));
		// Downward and snap-to-nearest: 604.4 snaps to 604, then 3 down → 601.
		Assert.Equal(601m, ladder.Offset(604.4m, -3));
	}

	[Fact]
	public void OffsetReturnsNullWhenRunningOffTheLadder()
	{
		var ladder = StrikeLadder.Build("SPY", Exp, "C", NonUniformChain("C"));
		Assert.Null(ladder.Offset(640m, 5));   // off the top
		Assert.Null(ladder.Offset(560m, -5));  // off the bottom
	}

	[Fact]
	public void FutureExpiryWithNoQuotesPrefersOpenInterestStrikes()
	{
		// The live XSP shape: a future expiry comes back symbol-only (no bid/ask on any strike). The $1
		// near-the-money strikes are listed but dead (OI=0); only the $5 grid actually trades (OI>0). The
		// ladder must pick the $5 grid, NOT the dead $1 strikes — picking $1 is the live scored=0 failure.
		var d = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		for (decimal k = 595m; k <= 605m; k += 1m) // $1 grid, listed but dead
		{
			var sym = MatchKeys.OccSymbol("XSP", Exp, k, "C");
			d[sym] = new OptionContractQuote(sym, null, null, null, null, null, 0, 0, null);
		}
		foreach (var k in new[] { 580m, 585m, 590m, 595m, 600m, 605m, 610m, 615m, 620m }) // $5 grid, real OI
		{
			var sym = MatchKeys.OccSymbol("XSP", Exp, k, "C");
			d[sym] = new OptionContractQuote(sym, null, null, null, null, null, 0, 1200, null); // no bid/ask, OI>0
		}
		var ladder = StrikeLadder.Build("XSP", Exp, "C", d);
		// Around ATM the ladder must be the $5 grid (598/599/601 etc. are dead and excluded). Around returns
		// strikes strictly below/above spot, so 600 itself isn't listed here — 595 and 605 bracket it.
		Assert.Equal(new[] { 595m, 605m }, ladder.Around(600m, 1).Where(s => s is >= 595m and <= 605m).ToArray());
		Assert.DoesNotContain(599m, ladder.Around(600m, 10));
		// Width-1 from 600 is the next TRADEABLE strike (605), not the dead 601.
		Assert.Equal(605m, ladder.Offset(600m, 1));
	}

	[Fact]
	public void EmptyWhenNoQuotesAndNoOpenInterest()
	{
		// No liquidity signal anywhere (XSP future expiries: fully symbol-only, no bid/ask, no OI) → the
		// ladder is empty and the enumerator falls back to the uniform strikeStep grid. We deliberately do
		// NOT return these listed-but-dead strikes — selecting them is the live scored=0 failure.
		var d = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var k in new[] { 595m, 600m, 605m })
		{
			var sym = MatchKeys.OccSymbol("XSP", Exp, k, "C");
			d[sym] = new OptionContractQuote(sym, null, null, null, null, null, null, null, null);
		}
		Assert.True(StrikeLadder.Build("XSP", Exp, "C", d).IsEmpty);
	}
}
