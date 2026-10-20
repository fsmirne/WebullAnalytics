using WebullAnalytics.Data.Backfill;
using Xunit;

namespace WebullAnalytics.Tests.Data;

public class BackfillEncodingTests
{
	[Fact]
	public void EncodeQuoteKeepsOneSidedBooksAndDropsEmptyOrNegativeOnes()
	{
		var twoSided = QuoteStoreSchema.EncodeQuote("SPY", 20261021, 20261019, 34260, 670.5, 'C', 95.41, 98.74000000000001, 8, 9)!.Value;
		Assert.Equal(new QuoteRow("SPY", 20261021, 20261019, 34260, 670500, 'C', 954100, 987400, 8, 9), twoSided);

		var bidAbsent = QuoteStoreSchema.EncodeQuote("SPXW", 20261019, 20261019, 34200, 6700, 'P', double.NaN, 0.05, 0, 5)!.Value;
		Assert.Equal(0, bidAbsent.Bid);
		Assert.Equal(500, bidAbsent.Ask);

		Assert.Null(QuoteStoreSchema.EncodeQuote("SPY", 20261021, 20261019, 34260, 670, 'C', double.NaN, double.NaN, 0, 0));
		Assert.Null(QuoteStoreSchema.EncodeQuote("SPY", 20261021, 20261019, 34260, 670, 'C', -0.01, 1.0, 0, 0));
	}

	[Fact]
	public void EncodeOhlcvStoresTradedBarsOnly()
	{
		var bar = QuoteStoreSchema.EncodeOhlcv("SPY", 20261021, 20261019, 34200, 670, 'C', 95.0, 96.0, double.NaN, 95.5, 12, 3, 95.3)!.Value;
		Assert.Equal(new OhlcvRow("SPY", 20261021, 20261019, 34200, 670000, 'C', 950000, 960000, 0, 955000, 12, 3, 953000), bar);

		Assert.Null(QuoteStoreSchema.EncodeOhlcv("SPY", 20261021, 20261019, 34200, 670, 'C', 1, 1, 1, 1, 0, 0, 1));
		Assert.Null(QuoteStoreSchema.EncodeOhlcv("SPY", 20261021, 20261019, 34200, 670, 'C', 1, 1, 1, double.NaN, 5, 1, 1));
	}

	// Reference values from scripts/backfill_thetadata.py back_solve_iv (numpy) on the same inputs. numpy's exp/log and .NET's Math.*
	// can differ in the last ulp, which can flip a late halving, hence the 1e-12 relative tolerance rather than bit equality.
	[Theory]
	[InlineData(5.67, 765.26, 765.0, 6, 0.045, true, 0.1341182383429027)]
	[InlineData(13.38, 765.26, 764.0, 24, 0.045, true, 0.14770966553126846)]
	[InlineData(2.10, 765.0, 740.0, 10, 0.04, false, 0.2056839397524735)]
	[InlineData(0.05, 6700.0, 6000.0, 1, 0.045, false, 0.6705727491134694)]
	public void ImpliedVolMatchesThePythonBisection(double price, double spot, double strike, int days, double rate, bool isCall, double expected)
	{
		var iv = ImpliedVol.BackSolve(price, spot, strike, days / 365.0, rate, isCall);

		Assert.True(Math.Abs(iv - expected) / expected < 1e-12, $"iv {iv:R} vs python {expected:R}");
	}

	[Theory]
	[InlineData(1.0, 100.0, 120.0, 0)]    // expired: T = 0
	[InlineData(0.5, 100.0, 90.0, 30)]    // below intrinsic (10)
	[InlineData(double.NaN, 100.0, 90.0, 30)]
	public void ImpliedVolIsNaNWhenUnsolvable(double price, double spot, double strike, int days)
	{
		Assert.True(double.IsNaN(ImpliedVol.BackSolve(price, spot, strike, days / 365.0, 0.045, isCall: true)));
	}
}
