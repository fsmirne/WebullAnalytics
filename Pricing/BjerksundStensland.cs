namespace WebullAnalytics.Pricing;

/// <summary>
/// Bjerksund-Stensland 2002 early exercise boundary approximation for American options.
/// Computes the optimal stock price at which exercising immediately beats holding.
/// Without dividend data (q=0), only puts benefit from early exercise.
/// </summary>
public static class BjerksundStensland
{
	private const double GoldenSection = 0.6180339887; // (√5 - 1) / 2

	/// <summary>
	/// Computes the early exercise boundary for an American option.
	/// Returns null when early exercise is never optimal (calls with q=0).
	/// </summary>
	/// <param name="strike">Option strike price</param>
	/// <param name="timeYears">Time to expiration in years</param>
	/// <param name="riskFreeRate">Annual risk-free rate (e.g., 0.043)</param>
	/// <param name="volatility">Annual implied volatility as decimal fraction (e.g., 0.50)</param>
	/// <param name="callPut">"C" for call, "P" for put</param>
	public static EarlyExerciseBoundary? ComputeExerciseBoundary(decimal strike, double timeYears, double riskFreeRate, double volatility, string callPut)
	{
		if (timeYears <= 0 || volatility <= 0) return null;

		// Calls on non-dividend-paying stocks: early exercise is never optimal
		if (callPut == "C") return null;

		// Put boundary via put-call transformation:
		// An American put with strike X on a stock S is equivalent to an American call
		// with strike S on a "stock" worth X. We compute the call boundary and invert.
		double k = (double)strike;
		double r = riskFreeRate;
		double sigma = volatility;

		double t1 = GoldenSection * timeYears;

		double boundaryFar = ComputePutBoundary(k, timeYears, r, sigma);
		double boundaryNear = ComputePutBoundary(k, t1, r, sigma);

		int transitionDays = (int)Math.Round(t1 * 365.0);

		return new EarlyExerciseBoundary(BoundaryNear: (decimal)Math.Round(boundaryNear, 2), BoundaryFar: (decimal)Math.Round(boundaryFar, 2), TransitionDays: transitionDays, IsCall: false);
	}

	/// <summary>
	/// Computes the critical stock price below which a put should be exercised early.
	/// Uses the B-S 2002 formula: S* = X / (1 + factor), where factor captures time value of waiting.
	/// </summary>
	private static double ComputePutBoundary(double strike, double tau, double r, double sigma)
	{
		if (tau <= 0) return strike; // at expiry, exercise when ITM

		double sigmaSqrt = sigma * Math.Sqrt(tau);
		double h = (r * tau - 2.0 * sigmaSqrt) * (2.0 * r / (sigma * sigma));
		double factor = (sigma * sigma) / (2.0 * r) * (1.0 - Math.Exp(h));

		// Clamp factor to avoid degenerate boundary values
		if (factor < 0) factor = 0;

		return strike / (1.0 + factor);
	}
}
