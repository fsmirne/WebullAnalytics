using WebullAnalytics.Positions;
using WebullAnalytics.Report;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Positions;

/// <summary>Covers the two money-integrity mechanisms that keep `wa report` tied to the broker's book:
/// the flat-book identity (running P&L == cash walk — the 2026-07-30 $0.52 drift came from rounding
/// legCash while ApplyToLots' FIFO realized runs unrounded) and the BrokerCash override (posted cash from
/// the cash-record ledger superseding price×qty±fee reconstruction, applied to cash AND realized).</summary>
public class PositionTrackerBrokerCashTests
{
	private static readonly DateTime OpenTime = new(2026, 7, 13, 12, 12, 8);
	private static readonly DateTime CloseTime = new(2026, 7, 14, 10, 45, 56);
	private static readonly DateTime BackExpiry = new(2026, 8, 21);
	private static readonly DateTime FrontExpiry = new(2026, 7, 17);

	/// <summary>The real 2026-07-13/14 GME diagonal round trip: a CSV-precision close leg (0.2599 × 150 =
	/// 38.985 — an exact half-cent, the worst case for Round2) used to leak $0.50 between realized and cash.</summary>
	private static List<Trade> DiagonalRoundTrip(decimal? closeBrokerCash = null)
	{
		var longSymbol = MatchKeys.OccSymbol("GME", BackExpiry, 21m, "C");
		var shortSymbol = MatchKeys.OccSymbol("GME", FrontExpiry, 22.5m, "C");
		const string parentKey = "strategy:Diagonal:GME:2026-08-21:C21,C22.5";
		return
		[
			new(1, OpenTime, "GME 21 Aug 2026", parentKey, Asset.OptionStrategy, "Diagonal", Side.Buy, 150, 1.538m, Trade.OptionMultiplier, BackExpiry),
			new(2, OpenTime, Formatters.FormatOptionDisplay("GME", BackExpiry, 21m), MatchKeys.Option(longSymbol), Asset.Option, "Call", Side.Buy, 150, 1.75m, Trade.OptionMultiplier, BackExpiry, 1, Fee: 6.80m),
			new(3, OpenTime, Formatters.FormatOptionDisplay("GME", FrontExpiry, 22.5m), MatchKeys.Option(shortSymbol), Asset.Option, "Call", Side.Sell, 150, 0.212m, Trade.OptionMultiplier, FrontExpiry, 1, Fee: 7.36m),
			new(4, CloseTime, "GME 21 Aug 2026", parentKey, Asset.OptionStrategy, "Diagonal", Side.Sell, 150, 1.58m, Trade.OptionMultiplier, BackExpiry, BrokerCash: closeBrokerCash),
			new(5, CloseTime, Formatters.FormatOptionDisplay("GME", BackExpiry, 21m), MatchKeys.Option(longSymbol), Asset.Option, "Call", Side.Sell, 150, 1.84m, Trade.OptionMultiplier, BackExpiry, 4, Fee: 7.86m),
			new(6, CloseTime, Formatters.FormatOptionDisplay("GME", FrontExpiry, 22.5m), MatchKeys.Option(shortSymbol), Asset.Option, "Call", Side.Buy, 150, 0.2599m, Trade.OptionMultiplier, FrontExpiry, 4, Fee: 6.80m),
		];
	}

	[Fact]
	public void FlatBook_RunningEqualsCashWalk_DespiteSubCentLegPrices()
	{
		var (rows, positions, running) = PositionTracker.ComputeReport(DiagonalRoundTrip(), initialAmount: 50000m);

		Assert.All(positions.Values, lots => Assert.Empty(lots));
		var last = rows.Last(r => !r.IsStrategyLeg);
		Assert.Equal(last.Cash, last.Total);
		Assert.Equal(50000m + running, last.Cash);
	}

	[Fact]
	public void BrokerCash_OverridesCashAndRealizedSymmetrically()
	{
		// Posted close = broker's book: $1.00 below the computed Round2(150×1.58)×100 − 14.66 = 23685.34.
		var (rows, _, running) = PositionTracker.ComputeReport(DiagonalRoundTrip(closeBrokerCash: 23684.34m), initialAmount: 50000m);

		var last = rows.Last(r => !r.IsStrategyLeg);
		Assert.Equal(50000m - 23084.16m + 23684.34m, last.Cash);  // open computed, close posted
		Assert.Equal(last.Cash, last.Total);                      // identity survives the override
		Assert.Equal(last.Cash - 50000m, running);
	}
}
