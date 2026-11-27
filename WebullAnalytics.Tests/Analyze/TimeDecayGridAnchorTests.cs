using WebullAnalytics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

/// <summary>The market-anchored grid's per-leg basis (model − mid) is a property of the market the mids were struck in, so it must not move
/// with a --spot / --overnight override. The regression: the basis was measured at the grid row nearest the market spot, and under an
/// override the rows are built around the override, so the market spot was no row and the row's offset × delta leaked into the basis. An
/// alive long beside an expired short kept that leak in the At Exp column: a 702C/705C diagonal valued from SPY $716.89 at a $727 override
/// read $0.90/share rich there, so the grid disagreed with the panel's own break-even. Without an override the market spot is a row and the basis was always exact.</summary>
[Collection("EvaluationDate")]
public class TimeDecayGridAnchorTests : IDisposable
{
	public void Dispose() => EvaluationDate.Reset();

	private const decimal MarketSpot = 716.89m;

	[Fact]
	public void AtExpColumn_UnderAnOverride_MatchesTheGridWithoutOne()
	{
		EvaluationDate.Reset();
		var withOverride = AtExpValueAtStrike(overrideSpot: 727.00m, strikeRow: 705m);
		var without = AtExpValueAtStrike(overrideSpot: null, strikeRow: 705m);
		Assert.Equal(without, withOverride);
	}

	/// <summary>The At Exp value at <paramref name="strikeRow"/>. Without an override the market spot is a grid row (as the report adds it);
	/// with one, the rows carry the override instead and the market spot is none of them.</summary>
	private static decimal AtExpValueAtStrike(decimal? overrideSpot, decimal strikeRow)
	{
		var shortExp = DateTime.Today.AddDays(15);
		var longExp = DateTime.Today.AddDays(22);
		var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 705m, "C");
		var longSym = MatchKeys.OccSymbol("SPY", longExp, 702m, "C");
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortSym] = TestQuote.Q(23.55m, 23.95m, 0.278m),
			[longSym] = TestQuote.Q(27.23m, 28.64m, 0.278m),
		};
		var opts = new AnalysisOptions(
			OptionQuotes: quotes,
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = MarketSpot },
			UnderlyingPriceOverrides: overrideSpot is { } o ? new Dictionary<string, decimal> { ["SPY"] = o } : null);

		var legShort = new PositionRow(shortSym, Asset.Option, "Call", Side.Sell, 20, 4.53m, shortExp, IsStrategyLeg: true, MatchKey: MatchKeys.Option(shortSym));
		var legLong = new PositionRow(longSym, Asset.Option, "Call", Side.Buy, 20, 10.685m, longExp, IsStrategyLeg: true, MatchKey: MatchKeys.Option(longSym));
		var legs = new List<(PositionRow row, OptionParsed parsed, string symbol)>
		{
			(legLong, ParsingHelpers.ParseOptionSymbol(longSym)!, longSym),
			(legShort, ParsingHelpers.ParseOptionSymbol(shortSym)!, shortSym),
		};

		var grid = TimeDecayGridBuilder.Build(legs, 20, Side.Buy, 6.155m, shortExp, opts, padding: 2m, centerPrice: 703.5m, breakEvens: [], maxColumns: 8, underlyingPrice: MarketSpot, extraNotables: [overrideSpot ?? MarketSpot]);
		Assert.Equal(overrideSpot == null, grid.PriceRows.Contains(MarketSpot));
		return grid.Values[grid.PriceRows.IndexOf(strikeRow), grid.DateColumns.Count - 1];
	}
}
