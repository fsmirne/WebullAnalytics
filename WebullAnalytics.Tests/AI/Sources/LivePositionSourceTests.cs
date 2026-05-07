using WebullAnalytics;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.AI.Sources;

public class LivePositionSourceTests
{
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
}
