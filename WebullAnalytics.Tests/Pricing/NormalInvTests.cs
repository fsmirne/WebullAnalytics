using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Pricing;

public class NormalInvTests
{
	[Fact]
	public void NormalInv_KnownQuantiles()
	{
		// Median
		Assert.Equal(0.0, OptionMath.NormalInv(0.5), precision: 6);

		// 1 std dev: N(1) ~= 0.8413447, N(-1) ~= 0.1586553
		Assert.Equal(1.0, OptionMath.NormalInv(OptionMath.NormalCdf(1.0)), precision: 5);
		Assert.Equal(-1.0, OptionMath.NormalInv(OptionMath.NormalCdf(-1.0)), precision: 5);

		// 2 std dev: N(2) ~= 0.9772499
		Assert.Equal(2.0, OptionMath.NormalInv(OptionMath.NormalCdf(2.0)), precision: 5);
		Assert.Equal(-2.0, OptionMath.NormalInv(OptionMath.NormalCdf(-2.0)), precision: 5);

		// Tail extremes
		Assert.True(OptionMath.NormalInv(0.0001) < -3.5);
		Assert.True(OptionMath.NormalInv(0.9999) > 3.5);
	}

	[Fact]
	public void DeltaToStrike_ConsistentWithDelta()
	{
		const decimal spot = 100m;
		const double timeYears = 0.25;
		const double r = 0.04;
		const decimal iv = 0.20m;
		const decimal targetDelta = 0.20m;

		// Put strike
		var putStrike = OptionMath.DeltaToStrike(spot, timeYears, r, iv, targetDelta, "P");
		Assert.True(putStrike < spot, "OTM put strike must be below spot");
		var recoveredPutDelta = Math.Abs(OptionMath.Delta(spot, putStrike, timeYears, r, iv, "P"));
		Assert.Equal((double)targetDelta, (double)recoveredPutDelta, tolerance: 0.01);

		// Call strike
		var callStrike = OptionMath.DeltaToStrike(spot, timeYears, r, iv, targetDelta, "C");
		Assert.True(callStrike > spot, "OTM call strike must be above spot");
		var recoveredCallDelta = Math.Abs(OptionMath.Delta(spot, callStrike, timeYears, r, iv, "C"));
		Assert.Equal((double)targetDelta, (double)recoveredCallDelta, tolerance: 0.01);
	}
}
