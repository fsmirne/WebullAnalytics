using System;
using System.Collections.Generic;
using WebullAnalytics;
using WebullAnalytics.AI;
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
}
