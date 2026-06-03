using WebullAnalytics;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

public class OptionMathDividendTests
{
	private const double R = 0.043;
	private static readonly DateTime Eval = new(2026, 6, 3);

	[Fact]
	public void NoDividends_ReturnsSpotUnchanged()
	{
		Assert.Equal(754.13m, OptionMath.DividendAdjustedSpot(754.13m, null, Eval, Eval.AddDays(23), R));
		Assert.Equal(754.13m, OptionMath.DividendAdjustedSpot(754.13m, new List<DividendEvent>(), Eval, Eval.AddDays(23), R));
	}

	[Fact]
	public void ExDateInWindow_SubtractsPresentValue()
	{
		var divs = new List<DividendEvent> { new(Eval.AddDays(16), 1.75m) };
		var adj = OptionMath.DividendAdjustedSpot(754.13m, divs, Eval, Eval.AddDays(23), R);
		var expectedPv = 1.75m * (decimal)Math.Exp(-R * 16.0 / 365.0);
		Assert.Equal(754.13m - expectedPv, adj, 4);
		Assert.True(adj < 754.13m);
	}

	[Fact]
	public void ExDateAfterExpiry_Excluded()
	{
		// Short leg expires in 9 days; the ex-date 16 days out is past it → no adjustment (mirrors the
		// short leg of the SPY calendar, which expires before the dividend).
		var divs = new List<DividendEvent> { new(Eval.AddDays(16), 1.75m) };
		Assert.Equal(754.13m, OptionMath.DividendAdjustedSpot(754.13m, divs, Eval, Eval.AddDays(9), R));
	}

	[Fact]
	public void ExDateAtOrBeforeEval_Excluded()
	{
		var divs = new List<DividendEvent> { new(Eval, 1.75m), new(Eval.AddDays(-1), 1.75m) };
		Assert.Equal(754.13m, OptionMath.DividendAdjustedSpot(754.13m, divs, Eval, Eval.AddDays(23), R));
	}

	[Fact]
	public void OverSubtraction_FallsBackToSpot()
	{
		var divs = new List<DividendEvent> { new(Eval.AddDays(5), 1000m) };
		Assert.Equal(10m, OptionMath.DividendAdjustedSpot(10m, divs, Eval, Eval.AddDays(23), R));
	}

	[Fact]
	public void Calendar_LongLegCrossingExDiv_PricesBelowUndividended()
	{
		// SPY 755 call calendar: long 755C exp +23d (crosses the +16d ex-date), short 755C exp +9d (does not).
		// The dividend lowers the long leg (it trades ex) while leaving the short untouched, shrinking the net
		// theoretical — the mechanism behind the reported theoretical-vs-mid gap.
		const decimal spot = 754.13m, strike = 755m;
		var divs = new List<DividendEvent> { new(Eval.AddDays(16), 1.75m) };

		decimal LegBs(int dte, decimal iv)
		{
			var expiry = Eval.AddDays(dte);
			var t = dte / 365.0;
			var s = OptionMath.DividendAdjustedSpot(spot, divs, Eval, expiry, R);
			return OptionMath.BlackScholes(s, strike, t, R, iv, "C");
		}
		decimal LegBsNoDiv(int dte, decimal iv) => OptionMath.BlackScholes(spot, strike, dte / 365.0, R, iv, "C");

		var longWith = LegBs(23, 0.129m);
		var longWithout = LegBsNoDiv(23, 0.129m);
		var shortWith = LegBs(9, 0.1196m);
		var shortWithout = LegBsNoDiv(9, 0.1196m);

		Assert.True(longWith < longWithout, "dividend must lower the long call that trades ex-dividend");
		Assert.Equal(shortWithout, shortWith); // short expires before the ex-date → unchanged

		var netWith = longWith - shortWith;
		var netWithout = longWithout - shortWithout;
		Assert.True(netWith < netWithout, "net theoretical must shrink toward the market mid once the dividend is modeled");
	}
}
