using WebullAnalytics;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using Xunit;

namespace WebullAnalytics.Tests.AI.Events;

public class EventVetoTests
{
	private const string Ticker = "AAPL";
	private static readonly DateTime AsOf = new(2026, 6, 1);
	private static readonly DateTime TargetExpiry = new(2026, 6, 12);

	private static OpenerEventsConfig Cfg(bool enabled = true, int blackoutAfter = 0, bool rejectShortCallsThroughExDiv = true) =>
		new() { Enabled = enabled, EarningsBlackoutDaysAfter = blackoutAfter, RejectShortCallsThroughExDiv = rejectShortCallsThroughExDiv };

	private static CandidateSkeleton ShortCallVertical(DateTime expiry)
	{
		var shortSym = MatchKeys.OccSymbol(Ticker, expiry, 200m, "C");
		var longSym = MatchKeys.OccSymbol(Ticker, expiry, 205m, "C");
		return new CandidateSkeleton(
			Ticker: Ticker,
			StructureKind: OpenStructureKind.ShortCallVertical,
			Legs: new[] { new ProposalLeg("sell", shortSym, 1), new ProposalLeg("buy", longSym, 1) },
			TargetExpiry: expiry);
	}

	private static CandidateSkeleton ShortPutVertical(DateTime expiry)
	{
		var shortSym = MatchKeys.OccSymbol(Ticker, expiry, 200m, "P");
		var longSym = MatchKeys.OccSymbol(Ticker, expiry, 195m, "P");
		return new CandidateSkeleton(
			Ticker: Ticker,
			StructureKind: OpenStructureKind.ShortPutVertical,
			Legs: new[] { new ProposalLeg("sell", shortSym, 1), new ProposalLeg("buy", longSym, 1) },
			TargetExpiry: expiry);
	}

	private static CandidateSkeleton LongCall(DateTime expiry)
	{
		var sym = MatchKeys.OccSymbol(Ticker, expiry, 200m, "C");
		return new CandidateSkeleton(
			Ticker: Ticker,
			StructureKind: OpenStructureKind.LongCall,
			Legs: new[] { new ProposalLeg("buy", sym, 1) },
			TargetExpiry: expiry);
	}

	[Fact]
	public void Returns_false_when_disabled()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: AsOf.AddDays(3), null, null, null);
		Assert.False(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(enabled: false), out _));
	}

	[Fact]
	public void Returns_false_when_no_events()
	{
		Assert.False(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events: null, Cfg(), out _));
	}

	[Fact]
	public void Vetoes_short_vertical_when_earnings_within_position_life()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: AsOf.AddDays(5), null, null, null);
		Assert.True(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(), out var reason));
		Assert.Contains("earnings", reason!);
	}

	[Fact]
	public void Vetoes_short_put_vertical_when_earnings_on_expiry_day()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: TargetExpiry, null, null, null);
		Assert.True(EventVeto.ShouldVeto(ShortPutVertical(TargetExpiry), AsOf, events, Cfg(), out _));
	}

	[Fact]
	public void Does_not_veto_when_earnings_strictly_after_expiry_with_zero_blackout()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: TargetExpiry.AddDays(1), null, null, null);
		Assert.False(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(blackoutAfter: 0), out _));
	}

	[Fact]
	public void Vetoes_when_earnings_within_blackout_window_after_expiry()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: TargetExpiry.AddDays(1), null, null, null);
		Assert.True(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(blackoutAfter: 2), out _));
	}

	[Fact]
	public void Does_not_veto_long_only_structure_even_with_earnings_in_position_life()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: AsOf.AddDays(5), null, null, null);
		Assert.False(EventVeto.ShouldVeto(LongCall(TargetExpiry), AsOf, events, Cfg(), out _));
	}

	[Fact]
	public void Does_not_veto_when_earnings_is_in_past()
	{
		var events = new TickerEvents(Ticker, NextEarningsDate: AsOf.AddDays(-1), null, null, null);
		Assert.False(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(), out _));
	}

	[Fact]
	public void Vetoes_short_call_when_exdiv_before_expiry()
	{
		var events = new TickerEvents(Ticker, null, null, NextExDividendDate: AsOf.AddDays(3), null);
		Assert.True(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(), out var reason));
		Assert.Contains("ex-div", reason!);
	}

	[Fact]
	public void Does_not_veto_short_put_when_only_exdiv_is_present()
	{
		var events = new TickerEvents(Ticker, null, null, NextExDividendDate: AsOf.AddDays(3), null);
		Assert.False(EventVeto.ShouldVeto(ShortPutVertical(TargetExpiry), AsOf, events, Cfg(), out _));
	}

	[Fact]
	public void Does_not_veto_short_call_when_exdiv_after_expiry()
	{
		// The day after the Friday expiry is a Saturday, which TickerEvents normalizes BACK onto the
		// expiry — use the following Monday so the ex-div stays strictly after expiry as intended.
		var events = new TickerEvents(Ticker, null, null, NextExDividendDate: TargetExpiry.AddDays(3), null);
		Assert.False(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(), out _));
	}

	[Fact]
	public void Does_not_veto_short_call_when_exdiv_rejection_disabled()
	{
		var events = new TickerEvents(Ticker, null, null, NextExDividendDate: AsOf.AddDays(3), null);
		Assert.False(EventVeto.ShouldVeto(ShortCallVertical(TargetExpiry), AsOf, events, Cfg(rejectShortCallsThroughExDiv: false), out _));
	}
}
