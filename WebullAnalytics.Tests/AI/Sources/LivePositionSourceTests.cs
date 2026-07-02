using WebullAnalytics;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.AI.Sources;

// Shares the EvaluationDate static override; grouped so it never runs in parallel with other
// tests that read/pin EvaluationDate (xUnit runs same-named collections serially).
[Collection("EvaluationDate")]
public class LivePositionSourceTests : IDisposable
{
	public void Dispose() => EvaluationDate.Reset();

	[Fact]
	public void BuildCostBasisLookup_ReturnsEmptyMapWhenNoTrades()
	{
		var map = LivePositionSource.BuildCostBasisLookup(trades: null, feeLookup: null);
		Assert.Empty(map);

		map = LivePositionSource.BuildCostBasisLookup(trades: new List<Trade>(), feeLookup: null);
		Assert.Empty(map);
	}

	[Fact]
	public void BuildCostBasisLookup_IndexesOpenStrategyByLegSet_AndExposesAdjustedBasisAfterRoll()
	{
		// Replicates the GME 26.50 calendar reported by the user:
		//   t0: open calendar (short 05/08 26.50C, long 06/05 26.50C) for net debit $0.92
		//   t1: roll long leg from 06/05 → 05/29 for $0.19 credit; new basis = $0.73
		// After replay, the live position has legs (05/08 short + 05/29 long) with adjusted $0.73.
		//
		// Pin the evaluation date to a day inside the trade window. The position tracker synthesizes
		// expiration trades for any leg whose expiry is before EvaluationDate.Today (PositionTracker.cs
		// ~line 140); without pinning, this test starts failing on 2026-05-08 when the short leg's
		// expiry slips into the past and gets auto-closed, collapsing the leg-set lookup.
		EvaluationDate.Set(new DateTime(2026, 4, 30));

		var ticker = "GME";
		var shortExpiry = new DateTime(2026, 5, 8);
		var origLongExpiry = new DateTime(2026, 6, 5);
		var newLongExpiry = new DateTime(2026, 5, 29);
		var strike = 26.50m;
		var shortSym = MatchKeys.OccSymbol(ticker, shortExpiry, strike, "C");
		var origLongSym = MatchKeys.OccSymbol(ticker, origLongExpiry, strike, "C");
		var newLongSym = MatchKeys.OccSymbol(ticker, newLongExpiry, strike, "C");
		var t0 = new DateTime(2026, 4, 28, 10, 0, 0, DateTimeKind.Utc);
		var t1 = t0.AddDays(2);

		var trades = new List<Trade>
		{
			// Open: calendar parent + two legs. Long $1.92, short $1.00 → debit $0.92.
			new(1, t0, $"{ticker} 05 Jun 2026", $"strategy:Calendar:{ticker}:2026-06-05:C26.5", Asset.OptionStrategy, "Calendar", Side.Buy, 400, 0.92m, Trade.OptionMultiplier, origLongExpiry),
			new(2, t0, Formatters.FormatOptionDisplay(ticker, shortExpiry, strike), MatchKeys.Option(shortSym), Asset.Option, "Call", Side.Sell, 400, 1.00m, Trade.OptionMultiplier, shortExpiry, 1),
			new(3, t0, Formatters.FormatOptionDisplay(ticker, origLongExpiry, strike), MatchKeys.Option(origLongSym), Asset.Option, "Call", Side.Buy, 400, 1.92m, Trade.OptionMultiplier, origLongExpiry, 1),

			// Roll long leg: sell 06/05 long, buy 05/29 long. Net credit $0.19/share → adjusted basis $0.92 - $0.19 = $0.73.
			new(4, t1, Formatters.FormatOptionDisplay(ticker, origLongExpiry, strike), MatchKeys.Option(origLongSym), Asset.Option, "Call", Side.Sell, 400, 1.74m, Trade.OptionMultiplier, origLongExpiry),
			new(5, t1, Formatters.FormatOptionDisplay(ticker, newLongExpiry, strike), MatchKeys.Option(newLongSym), Asset.Option, "Call", Side.Buy, 400, 1.55m, Trade.OptionMultiplier, newLongExpiry),
		};

		var map = LivePositionSource.BuildCostBasisLookup(trades, feeLookup: null);

		var legSetKey = string.Join("|", new[] { shortSym, newLongSym }.OrderBy(s => s, StringComparer.Ordinal));
		Assert.True(map.ContainsKey(legSetKey), $"expected leg-set '{legSetKey}' in map; keys: {string.Join(", ", map.Keys)}");
		var (initial, adjusted) = map[legSetKey];
		Assert.Equal(0.92m, decimal.Round(initial, 2));
		Assert.Equal(0.73m, decimal.Round(adjusted, 2));
	}

	// ── Leg-side inference (BuildPositionLegs / MapWebullStrategyToAiKind) ──────────────────────
	// Webull's holdings response has no per-leg side; sides come from structure geometry plus the
	// parent quantity's sign (negative = sold to open). These pin the mapping for every supported
	// strategy, both directions. The credit-vertical cases replicate the live 2026-07-02 XSP book
	// that the old qty<=0 skip dropped as "unclassifiable".

	private static LivePositionSource.ParsedLeg Leg(string root, DateTime expiry, decimal strike, string cp) =>
		new(MatchKeys.OccSymbol(root, expiry, strike, cp), root, cp, strike, expiry);

	private static readonly DateTime Exp = new(2026, 7, 2);
	private static readonly DateTime FarExp = new(2026, 7, 6);

	[Fact]
	public void BuildPositionLegs_CreditPutVertical_ShortsTheHigherStrike()
	{
		var legs = LivePositionSource.BuildPositionLegs("VERTICAL", new List<LivePositionSource.ParsedLeg> { Leg("XSP", Exp, 742m, "P"), Leg("XSP", Exp, 744m, "P") }, qty: 32, inverted: true)!;
		Assert.Equal(Side.Sell, legs.Single(l => l.Strike == 744m).Side);
		Assert.Equal(Side.Buy, legs.Single(l => l.Strike == 742m).Side);
		Assert.All(legs, l => Assert.Equal(32, l.Qty));
		Assert.Equal("ShortPutVertical", LivePositionSource.MapWebullStrategyToAiKind("VERTICAL", legs, inverted: true));
	}

	[Fact]
	public void BuildPositionLegs_CreditCallVertical_ShortsTheLowerStrike()
	{
		var legs = LivePositionSource.BuildPositionLegs("VERTICAL", new List<LivePositionSource.ParsedLeg> { Leg("XSP", Exp, 748m, "C"), Leg("XSP", Exp, 750m, "C") }, qty: 32, inverted: true)!;
		Assert.Equal(Side.Sell, legs.Single(l => l.Strike == 748m).Side);
		Assert.Equal(Side.Buy, legs.Single(l => l.Strike == 750m).Side);
		Assert.Equal("ShortCallVertical", LivePositionSource.MapWebullStrategyToAiKind("VERTICAL", legs, inverted: true));
	}

	[Fact]
	public void BuildPositionLegs_DebitCallVertical_LongsTheLowerStrike()
	{
		var legs = LivePositionSource.BuildPositionLegs("VERTICAL", new List<LivePositionSource.ParsedLeg> { Leg("SPY", Exp, 748m, "C"), Leg("SPY", Exp, 750m, "C") }, qty: 1, inverted: false)!;
		Assert.Equal(Side.Buy, legs.Single(l => l.Strike == 748m).Side);
		Assert.Equal(Side.Sell, legs.Single(l => l.Strike == 750m).Side);
		Assert.Equal("LongCallVertical", LivePositionSource.MapWebullStrategyToAiKind("VERTICAL", legs, inverted: false));
	}

	[Fact]
	public void BuildPositionLegs_DebitPutVertical_LongsTheHigherStrike()
	{
		// The pre-fix inference marked lower=long for puts too (debit-call default); a debit put
		// vertical is long the higher (more expensive) strike.
		var legs = LivePositionSource.BuildPositionLegs("VERTICAL", new List<LivePositionSource.ParsedLeg> { Leg("SPY", Exp, 742m, "P"), Leg("SPY", Exp, 744m, "P") }, qty: 1, inverted: false)!;
		Assert.Equal(Side.Buy, legs.Single(l => l.Strike == 744m).Side);
		Assert.Equal(Side.Sell, legs.Single(l => l.Strike == 742m).Side);
		Assert.Equal("LongPutVertical", LivePositionSource.MapWebullStrategyToAiKind("VERTICAL", legs, inverted: false));
	}

	[Fact]
	public void BuildPositionLegs_Single_DirectionFollowsQuantitySign()
	{
		var longLegs = LivePositionSource.BuildPositionLegs("SINGLE", new List<LivePositionSource.ParsedLeg> { Leg("SPY", Exp, 750m, "C") }, qty: 2, inverted: false)!;
		Assert.Equal(Side.Buy, longLegs.Single().Side);
		Assert.Equal("LongCall", LivePositionSource.MapWebullStrategyToAiKind("SINGLE", longLegs, inverted: false));

		var shortLegs = LivePositionSource.BuildPositionLegs("SINGLE", new List<LivePositionSource.ParsedLeg> { Leg("SPY", Exp, 750m, "P") }, qty: 2, inverted: true)!;
		Assert.Equal(Side.Sell, shortLegs.Single().Side);
		Assert.Equal("ShortPut", LivePositionSource.MapWebullStrategyToAiKind("SINGLE", shortLegs, inverted: true));
	}

	[Fact]
	public void BuildPositionLegs_Calendar_ShortsTheNearExpiry_ReversedWhenSold()
	{
		var parsed = () => new List<LivePositionSource.ParsedLeg> { Leg("SPY", FarExp, 750m, "C"), Leg("SPY", Exp, 750m, "C") };

		var longCal = LivePositionSource.BuildPositionLegs("CALENDAR", parsed(), qty: 1, inverted: false)!;
		Assert.Equal(Side.Sell, longCal.Single(l => l.Expiry == Exp).Side);
		Assert.Equal(Side.Buy, longCal.Single(l => l.Expiry == FarExp).Side);
		Assert.Equal("LongCalendar", LivePositionSource.MapWebullStrategyToAiKind("CALENDAR", longCal, inverted: false));

		var shortCal = LivePositionSource.BuildPositionLegs("CALENDAR", parsed(), qty: 1, inverted: true)!;
		Assert.Equal(Side.Buy, shortCal.Single(l => l.Expiry == Exp).Side);
		Assert.Equal(Side.Sell, shortCal.Single(l => l.Expiry == FarExp).Side);
		Assert.Equal("ShortCalendar", LivePositionSource.MapWebullStrategyToAiKind("CALENDAR", shortCal, inverted: true));
	}

	[Fact]
	public void BuildPositionLegs_ThreeOrMoreLegs_ReturnsNull()
	{
		var legs = LivePositionSource.BuildPositionLegs("CUSTOM", new List<LivePositionSource.ParsedLeg> { Leg("SPY", Exp, 748m, "C"), Leg("SPY", Exp, 750m, "C"), Leg("SPY", Exp, 752m, "C") }, qty: 1, inverted: false);
		Assert.Null(legs);
	}
}
