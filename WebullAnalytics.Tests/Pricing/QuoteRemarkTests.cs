using WebullAnalytics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

public class QuoteRemarkTests
{
	private static readonly DateTime Expiry = DateTime.Today.AddDays(45);
	private const decimal Sigma = 0.25m;
	private static readonly IReadOnlyDictionary<string, DateTime> NoDividends = new Dictionary<string, DateTime>();

	private static OptionTime Time(string root) => MarketClock.Between(root, OptionMath.ObservationInstant(), Expiry.Date + OptionMath.MarketClose);

	/// <summary>A two-sided quote whose mid is the model price at <paramref name="spot"/> and <see cref="Sigma"/>, 10 cents wide.</summary>
	private static (string Symbol, OptionContractQuote Quote) PricedAt(string root, decimal spot, decimal strike, string callPut)
	{
		var sym = MatchKeys.OccSymbol(root, Expiry, strike, callPut);
		var mid = OptionMath.BlackScholes(spot, strike, Time(root), OptionMath.RiskFreeRate, Sigma, callPut);
		return (sym, new OptionContractQuote(sym, mid, mid - 0.05m, mid + 0.05m, null, null, null, null, ImpliedVolatility: 0.40m));
	}

	[Theory]
	[InlineData("C", 700, 705)]
	[InlineData("C", 731, 704.74)]
	[InlineData("P", 700, 690)]
	public void MovesTheMidToTheModelPriceAtTheNewSpot_HoldingTheVolSolvedAtTheBook(string callPut, decimal strike, decimal toSpot)
	{
		var (sym, q) = PricedAt("SPY", 697m, strike, callPut);
		var remarked = QuoteRemark.StickyStrike(new Dictionary<string, OptionContractQuote> { [sym] = q }, "SPY", 697m, toSpot, out var unpriced)[sym];

		var expectedMid = OptionMath.BlackScholes(toSpot, strike, Time("SPY"), OptionMath.RiskFreeRate, Sigma, callPut);
		Assert.Equal(0, unpriced);
		Assert.Equal(expectedMid, (remarked.Bid!.Value + remarked.Ask!.Value) / 2m, 3);
		Assert.Equal(0.10m, remarked.Ask!.Value - remarked.Bid!.Value, 3);       // spread width kept
		Assert.Equal(Sigma, remarked.ImpliedVolatility!.Value, 3);               // the vol solved from the mid at the book, not the quote's stale 0.40
		Assert.Equal(0.40m, remarked.VendorImpliedVolatility);
	}

	[Fact]
	public void SameSpot_LeavesPricesAsQuoted()
	{
		var (sym, q) = PricedAt("SPY", 697m, 700m, "C");
		var remarked = QuoteRemark.StickyStrike(new Dictionary<string, OptionContractQuote> { [sym] = q }, "SPY", 697m, 697m, out _)[sym];
		Assert.Equal(q.Bid!.Value, remarked.Bid!.Value, 3);
		Assert.Equal(q.Ask!.Value, remarked.Ask!.Value, 3);
	}

	[Fact]
	public void OtherRootsPassThrough_SpxSiblingIsMoved()
	{
		var (qqq, qq) = PricedAt("QQQ", 600m, 600m, "C");
		var (spxw, sq) = PricedAt("SPXW", 7000m, 7000m, "C");
		var quotes = new Dictionary<string, OptionContractQuote> { [qqq] = qq, [spxw] = sq };

		var remarked = QuoteRemark.StickyStrike(quotes, "SPX", 7000m, 7070m, out _);

		Assert.Same(qq, remarked[qqq]);
		Assert.True(remarked[spxw].Bid > sq.Bid);
	}

	[Fact]
	public void OneSidedQuote_HoldsItsOwnIv_AndANoIvQuoteIsCountedUnpriced()
	{
		var oneSidedSym = MatchKeys.OccSymbol("SPY", Expiry, 700m, "C");
		var oneSided = new OptionContractQuote(oneSidedSym, null, null, 20m, null, null, null, null, ImpliedVolatility: Sigma);
		var noIvSym = MatchKeys.OccSymbol("SPY", Expiry, 710m, "C");
		var noIv = new OptionContractQuote(noIvSym, null, null, 15m, null, null, null, null, null);

		var remarked = QuoteRemark.StickyStrike(new Dictionary<string, OptionContractQuote> { [oneSidedSym] = oneSided, [noIvSym] = noIv }, "SPY", 697m, 705m, out var unpriced);

		var t = Time("SPY");
		var shift = OptionMath.BlackScholes(705m, 700m, t, OptionMath.RiskFreeRate, Sigma, "C") - OptionMath.BlackScholes(697m, 700m, t, OptionMath.RiskFreeRate, Sigma, "C");
		Assert.Equal(20m + shift, remarked[oneSidedSym].Ask!.Value, 3);
		Assert.Null(remarked[oneSidedSym].Bid);
		Assert.Same(noIv, remarked[noIvSym]);
		Assert.Equal(1, unpriced);
	}

	[Fact]
	public void BookSpots_OffHoursReadTheBooksParity_RthKeepsTheVendorSpot()
	{
		// Sunday night, a frozen SPY book struck at 693.26 while the vendor reports the 697.48 post-market last.
		var sunday = new DateTime(2026, 11, 22, 21, 0, 0);
		var monday = sunday.Date.AddDays(1);
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var k in new[] { 692m, 693m, 694m })
		{
			var discount = (decimal)Math.Exp(-OptionMath.RiskFreeRate * 1 / 365.0);
			var call = 5m + Math.Max(0m, 693.26m - k * discount);
			var put = call - (693.26m - k * discount);
			var c = MatchKeys.OccSymbol("SPY", monday, k, "C");
			var p = MatchKeys.OccSymbol("SPY", monday, k, "P");
			quotes[c] = new OptionContractQuote(c, null, call - 0.01m, call + 0.01m, null, null, null, null, null);
			quotes[p] = new OptionContractQuote(p, null, put - 0.01m, put + 0.01m, null, null, null, null, null);
		}
		var vendor = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["SPY"] = 697.48m };

		Assert.Equal(693.26m, AnalyzeCommon.BookSpots(quotes, vendor, sunday, NoDividends)["SPY"], 2);
		Assert.Equal(697.48m, AnalyzeCommon.BookSpots(quotes, vendor, monday.AddHours(10), NoDividends)["SPY"]);
	}

	[Fact]
	public void RemarkToSpots_ReturnsTheBoardAndTheSpotItIsNowStruckAt()
	{
		var (sym, q) = PricedAt("SPY", 693.26m, 700m, "C");
		var book = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["SPY"] = 693.26m, ["QQQ"] = 600m };
		var overrides = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["SPY"] = 704.74m };

		var (quotes, spots) = AnalyzeCommon.RemarkToSpots(new Dictionary<string, OptionContractQuote> { [sym] = q }, book, overrides);

		Assert.Equal(704.74m, spots["SPY"]);
		Assert.Equal(600m, spots["QQQ"]);
		var expectedMid = OptionMath.BlackScholes(704.74m, 700m, Time("SPY"), OptionMath.RiskFreeRate, Sigma, "C");
		Assert.Equal(expectedMid, (quotes[sym].Bid!.Value + quotes[sym].Ask!.Value) / 2m, 3);
	}
}
