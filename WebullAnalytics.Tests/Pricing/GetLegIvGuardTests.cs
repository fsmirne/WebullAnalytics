using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

/// <summary>Locks the vendor-IV trust guard in <see cref="OptionMath.GetLegIv"/>: a vendor-reported IV is only
/// consumed when the quote carries a live two-sided book. The regression this prevents: an after-hours residue
/// book (ask 0.01, no bid) on an expiring contract reported IV 145%, and the --date reprice turned a ~$0.01
/// leg into a $15.74 liability — printing a P&L nearly 3x beyond the position's own max loss.</summary>
public class GetLegIvGuardTests
{
	private const string Symbol = "SPY260806P00745000";

	private static AnalysisOptions Opts(OptionContractQuote quote) =>
		new(OptionQuotes: new Dictionary<string, OptionContractQuote> { [Symbol] = quote });

	private static OptionContractQuote Quote(decimal? bid, decimal? ask, decimal? iv, decimal? hv = null) =>
		new(ContractSymbol: Symbol, LastPrice: null, Bid: bid, Ask: ask, Change: null, PercentChange: null,
			Volume: null, OpenInterest: null, ImpliedVolatility: iv, HistoricalVolatility: hv);

	[Fact]
	public void TwoSidedBook_TrustsVendorIv()
	{
		var iv = OptionMath.GetLegIv(Side.Sell, Symbol, Opts(Quote(bid: 3.25m, ask: 3.29m, iv: 0.145m, hv: 0.20m)));
		Assert.Equal(0.145m, iv);
	}

	[Theory]
	[InlineData(null, 0.01)]  // no bid at all — the after-hours dead-residue book
	[InlineData(0.0, 0.01)]   // zero bid — one-sided
	[InlineData(0.01, null)]  // no ask
	public void OneSidedBook_FallsBackToHv(double? bid, double? ask)
	{
		var quote = Quote(bid: (decimal?)bid, ask: (decimal?)ask, iv: 1.453m, hv: 0.145m);
		var iv = OptionMath.GetLegIv(Side.Sell, Symbol, Opts(quote));
		Assert.Equal(0.145m, iv);
	}

	[Fact]
	public void OneSidedBook_NoHv_ReturnsNull_SoLegPricesAtIntrinsic()
	{
		var iv = OptionMath.GetLegIv(Side.Sell, Symbol, Opts(Quote(bid: null, ask: 0.01m, iv: 1.453m)));
		Assert.Null(iv);
	}

	[Fact]
	public void CalibratedIv_WinsRegardlessOfBook()
	{
		var opts = Opts(Quote(bid: null, ask: 0.01m, iv: 1.453m)) with
		{
			CalibratedIv = new Dictionary<string, decimal> { [Symbol] = 0.18m }
		};
		Assert.Equal(0.18m, OptionMath.GetLegIv(Side.Sell, Symbol, opts));
	}
}
