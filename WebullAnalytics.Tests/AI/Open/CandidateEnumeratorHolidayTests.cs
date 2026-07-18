using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

// Regression: a captured chain can list contracts whose expiry is a market holiday (e.g. Good Friday
// 2026-04-03), because data providers carry the holiday-Friday symbol even though it never trades.
// The enumerator must NOT propose a leg on a closed day — doing so makes the backtest fabricate a fill
// (phantom strike) and would suggest a non-tradeable contract live.
public class CandidateEnumeratorHolidayTests
{
	private static readonly DateTime GoodFriday2026 = new(2026, 4, 3);

	private static OpenerConfig Cfg() => new()
	{
		Indicators = new() { IvDefaultPct = 0.4m, StrikeStep = 1.0m },
		Structures = new OpenerStructuresConfig
		{
			LongCalendar = new OpenerCalendarLikeConfig { Enabled = true, ShortDteMin = 3, ShortDteMax = 10, LongDteMin = 21, LongDteMax = 30 },
			DoubleCalendar = new OpenerDoubleCalendarConfig { Enabled = false },
			LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
			DoubleDiagonal = new OpenerDoubleDiagonalConfig { Enabled = false },
			IronButterfly = new OpenerIronButterflyConfig { Enabled = false },
			IronCondor = new OpenerIronCondorConfig { Enabled = false },
			ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
			LongVertical = new OpenerLongVerticalConfig { Enabled = false },
			LongCallPut = new OpenerLongCallPutConfig { Enabled = false },
			DiagonalVertical = new OpenerDiagonalVerticalConfig { Enabled = false },
			CalendarVertical = new OpenerCalendarVerticalConfig { Enabled = false }
		}
	};

	// Builds quotes (strikes 14/15/16, calls + puts) for `ladderExpiries`, and reports `availableExpiries`
	// as the chain's known expiration set. Separating the two lets a test list a holiday expiry in the
	// available set while only the snapped prior-day expiry carries quotable strikes.
	private static (IReadOnlySet<DateTime> exps, Dictionary<string, OptionContractQuote> quotes) BuildChain(
		IEnumerable<DateTime> availableExpiries, IEnumerable<DateTime> ladderExpiries)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var e in ladderExpiries)
			foreach (var k in new[] { 14m, 15m, 16m })
				foreach (var cp in new[] { "C", "P" })
				{
					var sym = MatchKeys.OccSymbol("GME", e, k, cp);
					quotes[sym] = new OptionContractQuote(sym, null, 1.0m, 1.1m, null, null, 500, 500, 0.40m);
				}
		return (new HashSet<DateTime>(availableExpiries), quotes);
	}

	[Fact]
	public void SnapsHolidayExpiry_ToPriorTradingDay_NeverEnumeratesTheClosedDay()
	{
		Assert.False(MarketCalendar.IsOpen(GoodFriday2026));      // guard: 04-03 is closed
		var thursday = new DateTime(2026, 4, 2);
		Assert.True(MarketCalendar.IsOpen(thursday));             // guard: 04-02 is the real expiry

		var asOf = new DateTime(2026, 3, 27); // Friday; Good Friday 04-03 is 7 DTE (short window)
		// Available set lists ONLY the holiday-Friday short expiry (04-03) + a long (04-24) — as a data
		// provider would. Quotable strikes exist for the snapped prior day (04-02) and the long.
		var (exps, quotes) = BuildChain(
			availableExpiries: new[] { GoodFriday2026, new DateTime(2026, 4, 24) },
			ladderExpiries: new[] { thursday, new DateTime(2026, 4, 24) });

		var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, Cfg(), exps, quotes).ToList();

		// Snap (not drop): the calendar's short leg lands on 04-02, never on the closed 04-03.
		Assert.NotEmpty(skeletons);
		Assert.Contains(skeletons, s => s.Legs.Any(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)!.ExpiryDate.Date == thursday));
		foreach (var s in skeletons)
			foreach (var leg in s.Legs)
				Assert.NotEqual(GoodFriday2026, ParsingHelpers.ParseOptionSymbol(leg.Symbol)!.ExpiryDate.Date);
	}
}
