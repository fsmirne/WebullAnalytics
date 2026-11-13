using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>Locks <see cref="GexMatrix.FindCallWallAt"/>: the call wall is ranked by gamma with the underlying at the probe spot, not at
/// the matrix's own spot, and honours its strike floor. Two call strikes with equal OI, one beside today's spot and one beside the
/// probe: each is the wall from its own side.</summary>
public class GexCallWallAtTests
{
	private static readonly DateTime AsOf = new(2026, 11, 13, 12, 0, 0);
	private static readonly DateTime Expiry = new(2026, 11, 13);
	private const decimal Spot = 7500m;

	private static GexMatrix Matrix()
	{
		var quotes = new Dictionary<string, OptionContractQuote>();
		// Live two-sided books: GexMatrix.Build trusts a vendor IV only on one.
		foreach (var (strike, bid, ask) in new[] { (7525m, 6.90m, 7.10m), (7625m, 0.20m, 0.25m) })
		{
			var symbol = MatchKeys.OccSymbol("SPXW", Expiry, strike, "C");
			quotes[symbol] = new(ContractSymbol: symbol, LastPrice: null, Bid: bid, Ask: ask, Change: null, PercentChange: null, Volume: null, OpenInterest: 5000, ImpliedVolatility: 0.15m, HistoricalVolatility: null);
		}
		return GexMatrix.Build(quotes, "SPXW", Spot, AsOf, Expiry, strikeRangeFraction: 0.20m, maxDteDays: 7, maxStrikes: 50);
	}

	[Fact]
	public void RanksByGammaAtTheProbeSpot()
	{
		var m = Matrix();
		Assert.Equal(7525m, m.FindCallWallAt(Expiry, atSpot: Spot, minStrike: 0m));
		Assert.Equal(7625m, m.FindCallWallAt(Expiry, atSpot: 7620m, minStrike: 0m));
	}

	[Fact]
	public void HonoursTheStrikeFloor()
	{
		var m = Matrix();
		Assert.Equal(7625m, m.FindCallWallAt(Expiry, atSpot: Spot, minStrike: 7600m));
		Assert.Null(m.FindCallWallAt(Expiry, atSpot: Spot, minStrike: 7700m));
	}
}
