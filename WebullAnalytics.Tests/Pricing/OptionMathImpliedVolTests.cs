using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

public class OptionMathImpliedVolTests
{
	private const double R = 0.04;

	/// <summary>Round-trips: price at a known vol, then back-solve must recover it. The deep-OTM case is the
	/// regression — Newton-from-0.3 overshoots past the 5.0 bound on the first step (tiny vega) and used to
	/// return the clamped 5.0, which callers reject. The bisection fallback recovers the true ~0.63 vol.</summary>
	[Theory]
	[InlineData(110, 110, 0.45)]   // ATM — Newton converges directly
	[InlineData(110, 100, 0.50)]   // moderately OTM put
	[InlineData(110, 80, 0.63)]    // deep OTM put: the leg that failed to recalibrate
	[InlineData(110, 130, 0.55)]   // deep OTM call
	public void BackSolve_RecoversVolUsedToPrice(decimal spot, decimal strike, double knownVol)
	{
		var t = 31.0 / 365.0;
		var cp = strike <= spot ? "P" : "C";
		var price = OptionMath.BlackScholes(spot, strike, t, R, (decimal)knownVol, cp);

		var solved = OptionMath.ImpliedVol(spot, strike, t, R, price, cp);

		Assert.InRange(solved, 0.01m, 5m);
		Assert.Equal((decimal)knownVol, solved, 2);
	}

	[Fact]
	public void DeepOtm_DoesNotClampToUpperBound()
	{
		// USO P80 @ ~$110 spot, 31 DTE, mid $0.28 — the exact leg that showed plain (un-recalibrated) IV.
		var t = 31.0 / 365.0;
		var solved = OptionMath.ImpliedVol(110m, 80m, t, R, 0.28m, "P");
		Assert.True(solved < 5m, $"expected a converged vol, got the clamped bound {solved}");
		Assert.InRange(solved, 0.55m, 0.70m);
	}
}
