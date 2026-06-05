using WebullAnalytics;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Utils;

/// <summary>Pins the --spot reprice semantics for the open-positions market-value summary:
/// without an override the raw mid is used; with an override the position is repriced at the
/// hypothetical spot via the grid's own dividend-aware path (LegContractValueWithBs), shifting the
/// observed mid by the model's value change from the real market spot to the override.</summary>
// ComputeOpenPositionsMarketValue short-circuits when EvaluationDate.Today is in the past. Pin it to
// today (and join the EvaluationDate collection so a parallel test can't leave a stale past override).
[Collection("EvaluationDate")]
public class RepriceAtSpotTests : IDisposable
{
	public RepriceAtSpotTests() => EvaluationDate.Set(DateTime.Today);
	public void Dispose() => EvaluationDate.Reset();

	private const decimal MarketSpot = 757.09m;
	private const decimal LowerSpot = 752.96m;

	private static (Dictionary<string, List<Lot>> lots, Dictionary<string, OptionContractQuote> quotes, string occ, OptionParsed parsed)
		BuildLeg(string callPut, Side side)
	{
		var expiry = DateTime.Today.AddDays(30);
		var occ = MatchKeys.OccSymbol("SPY", expiry, 750m, callPut);
		var lots = new Dictionary<string, List<Lot>> { [MatchKeys.Option(occ)] = new() { new Lot(side, 1, 5.00m) } };
		var quotes = new Dictionary<string, OptionContractQuote> { [occ] = TestQuote.Q(9.90m, 10.10m, iv: 0.20m) };
		return (lots, quotes, occ, new OptionParsed("SPY", expiry, callPut, 750m));
	}

	[Fact]
	public void NoOverride_UsesRawMid()
	{
		var (lots, quotes, _, _) = BuildLeg("C", Side.Buy);
		var opts = new AnalysisOptions(OptionQuotes: quotes, UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = MarketSpot });

		var value = TableBuilder.ComputeOpenPositionsMarketValue(lots, opts);

		Assert.NotNull(value);
		Assert.Equal(10.00m * 100m, value!.Value, 2); // (9.90 + 10.10)/2 × 100, long
	}

	[Fact]
	public void Override_RepricesLongCallDownAsSpotDrops()
	{
		var (lots, quotes, occ, parsed) = BuildLeg("C", Side.Buy);
		var prices = new Dictionary<string, decimal> { ["SPY"] = MarketSpot };
		var optsBase = new AnalysisOptions(OptionQuotes: quotes, UnderlyingPrices: prices);
		var optsOv = optsBase with { UnderlyingPriceOverrides = new Dictionary<string, decimal> { ["SPY"] = LowerSpot } };

		var baseVal = TableBuilder.ComputeOpenPositionsMarketValue(lots, optsBase)!.Value;
		var repriced = TableBuilder.ComputeOpenPositionsMarketValue(lots, optsOv)!.Value;

		// A long call loses value as spot falls — the override must move the number, not echo the mid.
		Assert.True(repriced < baseVal, $"expected reprice below {baseVal}, got {repriced}");

		// Exact: raw mid shifted by the grid's own (dividend-aware) pricing path.
		var now = EvaluationDate.Now;
		var shift = OptionMath.LegContractValueWithBs(LowerSpot, parsed, occ, Side.Buy, now, optsOv)
				  - OptionMath.LegContractValueWithBs(MarketSpot, parsed, occ, Side.Buy, now, optsOv);
		Assert.Equal((10.00m + shift) * 100m, repriced, 2);
	}

	[Fact]
	public void Override_RepricesLongPutUpAsSpotDrops()
	{
		var (lots, quotes, _, _) = BuildLeg("P", Side.Buy);
		var prices = new Dictionary<string, decimal> { ["SPY"] = MarketSpot };
		var optsBase = new AnalysisOptions(OptionQuotes: quotes, UnderlyingPrices: prices);
		var optsOv = optsBase with { UnderlyingPriceOverrides = new Dictionary<string, decimal> { ["SPY"] = LowerSpot } };

		var baseVal = TableBuilder.ComputeOpenPositionsMarketValue(lots, optsBase)!.Value;
		var repriced = TableBuilder.ComputeOpenPositionsMarketValue(lots, optsOv)!.Value;

		// A long put gains value as spot falls.
		Assert.True(repriced > baseVal, $"expected reprice above {baseVal}, got {repriced}");
	}

	[Fact]
	public void Theoretical_UsesGridDividendAwarePath()
	{
		var (lots, quotes, occ, parsed) = BuildLeg("C", Side.Buy);
		// A dividend inside the option's life makes the dividend-aware path diverge from plain BS — so this
		// asserts the theoretical total goes through LegContractValueWithBs, not a hand-rolled BlackScholes.
		var divs = new Dictionary<string, IReadOnlyList<DividendEvent>> { ["SPY"] = new[] { new DividendEvent(DateTime.Today.AddDays(15), 1.50m) } };
		var opts = new AnalysisOptions(
			OptionQuotes: quotes,
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = MarketSpot },
			Theoretical: true,
			Dividends: divs);

		var value = TableBuilder.ComputeOpenPositionsMarketValue(lots, opts);

		var expected = OptionMath.LegContractValueWithBs(MarketSpot, parsed, occ, Side.Buy, EvaluationDate.Now, opts) * 100m;
		Assert.NotNull(value);
		Assert.Equal(expected, value!.Value, 2);
	}

	[Fact]
	public void Theoretical_NoIv_FallsBackToIntrinsicInsteadOfSkipping()
	{
		// No quotes, no --iv, no calibration → GetLegIv returns null. Previously the leg was dropped from the
		// total (→ null); now it falls back to intrinsic like the grid does. ITM call: intrinsic = spot − strike.
		var (lots, _, _, _) = BuildLeg("C", Side.Buy);
		var opts = new AnalysisOptions(
			UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = MarketSpot },
			Theoretical: true);

		var value = TableBuilder.ComputeOpenPositionsMarketValue(lots, opts);

		Assert.NotNull(value);
		Assert.Equal((MarketSpot - 750m) * 100m, value!.Value, 2);
	}
}
