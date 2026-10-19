using System;
using System.Collections.Generic;
using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

/// <summary>The LIVE quote-integrity guards: torn-NBBO (crossed / absurdly wide) and feed staleness
/// (freshest quote too old). Values drawn from the real 2026-07-13 SPY Webull incident where a
/// logged-off feed returned a torn long-call quote (bid 10.36 / ask 20.36) and a $2-off --limit.</summary>
public class QuoteSanityTests
{
	private static readonly OpenerQuoteGuardConfig Cfg = new(); // defaults: 120s, 50% of mid, $1.00 abs

	private static OptionContractQuote Q(string sym, decimal? bid, decimal? ask, DateTimeOffset? t = null) =>
		new(sym, LastPrice: null, Bid: bid, Ask: ask, Change: null, PercentChange: null, Volume: null, OpenInterest: null, ImpliedVolatility: null, QuoteTime: t);

	// ---- torn-NBBO ------------------------------------------------------------------------------------

	[Fact]
	public void TornDetectsTheRealJul13TornLongLeg()
	{
		// The actual poisoned quote: $10 wide, ask ~2x bid.
		Assert.NotNull(QuoteSanity.TornReason(Q("SPY260814C00750000", 10.36m, 20.36m), Cfg.MaxSpreadPctOfMid, Cfg.MinAbsSpreadDollars));
	}

	[Fact]
	public void TornIgnoresTightHealthyQuote()
	{
		// The clean 2026-07-14 quote: $0.07 wide.
		Assert.Null(QuoteSanity.TornReason(Q("SPY260821P00751000", 12.47m, 12.54m), Cfg.MaxSpreadPctOfMid, Cfg.MinAbsSpreadDollars));
	}

	[Fact]
	public void TornIgnoresCheapWideOption()
	{
		// A genuinely cheap OTM strike: bid 0.10 / ask 0.20 is 67% of mid but only $0.10 absolute — NOT torn
		// (the abs-spread floor spares it). This is the false-positive the AND-of-two-thresholds prevents.
		Assert.Null(QuoteSanity.TornReason(Q("SPY260720C00770000", 0.10m, 0.20m), Cfg.MaxSpreadPctOfMid, Cfg.MinAbsSpreadDollars));
	}

	[Fact]
	public void TornFlagsCrossedQuoteRegardlessOfWidth()
	{
		Assert.NotNull(QuoteSanity.TornReason(Q("X", 5.00m, 4.90m), Cfg.MaxSpreadPctOfMid, Cfg.MinAbsSpreadDollars)); // bid > ask
	}

	[Fact]
	public void TornIgnoresOneSidedQuote()
	{
		// Absent/one-sided quote is the liquidity gate's job, not integrity.
		Assert.Null(QuoteSanity.TornReason(Q("X", null, 5.0m), Cfg.MaxSpreadPctOfMid, Cfg.MinAbsSpreadDollars));
		Assert.Null(QuoteSanity.TornReason(Q("X", 0m, 0m), Cfg.MaxSpreadPctOfMid, Cfg.MinAbsSpreadDollars));
	}

	[Fact]
	public void TornLegsReturnsOnlyTheOffendingLeg()
	{
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			["SPY260720C00750000"] = Q("SPY260720C00750000", 5.24m, 5.38m),   // clean short
			["SPY260814C00750000"] = Q("SPY260814C00750000", 10.36m, 20.36m), // torn long
		};
		var legs = new List<ProposalLeg> { new("sell", "SPY260720C00750000", 1), new("buy", "SPY260814C00750000", 1) };
		var issues = QuoteSanity.TornLegs(legs, book, Cfg);
		Assert.Single(issues);
		Assert.Equal("SPY260814C00750000", issues[0].Symbol);
		Assert.Equal(QuoteIssueKind.TornNbbo, issues[0].Kind);
	}

	[Fact]
	public void DisabledGuardReturnsNoTornLegs()
	{
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase) { ["X"] = Q("X", 10m, 20m) };
		var legs = new List<ProposalLeg> { new("buy", "X", 1) };
		Assert.Empty(QuoteSanity.TornLegs(legs, book, new OpenerQuoteGuardConfig { Enabled = false }));
	}

	// ---- feed staleness -------------------------------------------------------------------------------

	private static readonly DateTimeOffset Now = new(2026, 7, 13, 13, 45, 0, TimeSpan.Zero);

	[Fact]
	public void FeedStaleWhenFreshestQuoteIsMinutesOld()
	{
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			["A"] = Q("A", 1m, 1.1m, Now.AddMinutes(-15)),
			["B"] = Q("B", 2m, 2.1m, Now.AddMinutes(-20)),
		};
		Assert.True(QuoteSanity.IsFeedStale(book, Now, 120, out var age));
		Assert.True(age >= 900);
	}

	[Fact]
	public void FeedFreshWhenAnyQuoteIsRecent()
	{
		// One quiet strike (illiquid, stamped 15m ago) must NOT trip the guard because a near-ATM strike
		// is fresh — the freshest-across-book design.
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase)
		{
			["quiet"] = Q("quiet", 1m, 1.1m, Now.AddMinutes(-15)),
			["atm"] = Q("atm", 2m, 2.1m, Now.AddSeconds(-3)),
		};
		Assert.False(QuoteSanity.IsFeedStale(book, Now, 120, out var age));
		Assert.True(age < 120);
	}

	[Fact]
	public void StalenessUnverifiableWhenNoQuoteCarriesTimestamp()
	{
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase) { ["A"] = Q("A", 1m, 1.1m) };
		Assert.False(QuoteSanity.IsFeedStale(book, Now, 120, out var age));
		Assert.Null(age); // null age = "can't tell", not "fresh"
	}

	[Fact]
	public void StalenessDisabledWhenMaxAgeNonPositive()
	{
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase) { ["A"] = Q("A", 1m, 1.1m, Now.AddHours(-3)) };
		Assert.False(QuoteSanity.IsFeedStale(book, Now, 0, out _));
	}

	// ---- frozen book (put-call parity vs live spot) --------------------------------------------------

	private static readonly DateTime ParityNow = new(2026, 10, 19, 9, 36, 0);
	private const double R = 0.043;

	private static Dictionary<string, OptionContractQuote> Book(params (string Sym, decimal Bid, decimal Ask)[] quotes)
	{
		var book = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, bid, ask) in quotes) book[sym] = Q(sym, bid, ask);
		return book;
	}

	[Fact]
	public void ParityFlagsTheRealOct19FrozenSchwabBook()
	{
		// Schwab's SPY 10-27 chain at 09:36 ET, fresh-stamped and "realtime" but frozen: puts ~$3 rich, calls ~$1.4 cheap vs the live book.
		var book = Book(
			("SPY261027C00765000", 6.00m, 6.06m), ("SPY261027P00765000", 7.43m, 7.52m),
			("SPY261027C00766000", 5.43m, 5.50m), ("SPY261027P00766000", 7.60m, 8.23m),
			("SPY261027C00767000", 4.90m, 4.96m), ("SPY261027P00767000", 8.14m, 8.70m),
			("SPY261027C00768000", 4.39m, 4.45m), ("SPY261027P00768000", 8.48m, 9.21m),
			("SPY261027C00769000", 3.91m, 3.98m), ("SPY261027P00769000", 9.16m, 9.75m));
		var gaps = QuoteSanity.ParityGaps("SPY", 767.46m, book, ParityNow, dividends: null, R);
		var gap = Assert.Single(gaps);
		Assert.Equal(new DateTime(2026, 10, 27), gap.Expiry);
		Assert.InRange(gap.ImpliedSpot, 762.5m, 763.5m);
		Assert.True(gap.Deviation < -0.005m);
	}

	[Fact]
	public void ParityPassesTheSameMinutesLiveWebullBook()
	{
		var book = Book(("SPY261027C00767000", 6.29m, 6.33m), ("SPY261027P00767000", 5.06m, 5.11m));
		Assert.Empty(QuoteSanity.ParityGaps("SPY", 767.40m, book, ParityNow, dividends: null, R));
	}

	[Fact]
	public void ParityMediansNearTheMoneyStrikesSoOneTornStrikeCannotTrip()
	{
		var book = Book(
			("SPY261027C00766000", 6.86m, 6.90m), ("SPY261027P00766000", 4.66m, 4.70m),
			("SPY261027C00767000", 6.29m, 6.33m), ("SPY261027P00767000", 9.06m, 9.11m),   // torn put: $4 rich
			("SPY261027C00768000", 5.75m, 5.79m), ("SPY261027P00768000", 5.53m, 5.57m));
		Assert.Empty(QuoteSanity.ParityGaps("SPY", 767.40m, book, ParityNow, dividends: null, R));
	}

	[Fact]
	public void ParityPricesAKnownDividendInsteadOfFlaggingIt()
	{
		// A healthy 12-31 book straddling a $3.00 ex-dividend: parity sits ~39 bp under spot by construction.
		var expiry = new DateTime(2026, 12, 31);
		var dividends = new[] { new DividendEvent(new DateTime(2026, 12, 18), 3.00m) };
		const decimal spot = 767m, strike = 767m, put = 20m;
		var close = expiry + OptionMath.MarketClose;
		var discount = (decimal)Math.Exp(-R * (close - ParityNow).TotalDays / 365.0);
		var call = put + OptionMath.DividendAdjustedSpot(spot, dividends, ParityNow, close, R) - strike * discount;
		var book = Book(("SPY261231C00767000", call - 0.02m, call + 0.02m), ("SPY261231P00767000", put - 0.02m, put + 0.02m));

		Assert.Empty(QuoteSanity.ParityGaps("SPY", spot, book, ParityNow, dividends, R));
		Assert.Single(QuoteSanity.ParityGaps("SPY", spot, book, ParityNow, dividends: null, R));
	}

	[Fact]
	public void ParitySkipsExpiriesPastTheSingleDividendHorizon()
	{
		var book = Book(("SPY270319C00767000", 30m, 30.1m), ("SPY270319P00767000", 60m, 60.1m));   // wildly off, but 151 days out
		Assert.Empty(QuoteSanity.ParityGaps("SPY", 767m, book, ParityNow, dividends: null, R));
	}

	[Fact]
	public void TouchesExpiryMatchesAnyLegOnAFlaggedExpiry()
	{
		var frozen = new HashSet<DateTime> { new(2026, 10, 27) };
		var diagonal = new[] { new ProposalLeg("sell", "SPY261027P00767000", 1), new ProposalLeg("buy", "SPY261113P00769000", 1) };
		var other = new[] { new ProposalLeg("sell", "SPY261026P00767000", 1), new ProposalLeg("buy", "SPY261113P00769000", 1) };
		Assert.True(QuoteSanity.TouchesExpiry(diagonal, frozen));
		Assert.False(QuoteSanity.TouchesExpiry(other, frozen));
		Assert.False(QuoteSanity.TouchesExpiry(diagonal, new HashSet<DateTime>()));
	}
}
