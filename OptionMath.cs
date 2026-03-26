using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// Pure math and pricing utilities for options: Black-Scholes, intrinsic value, P&L,
/// implied volatility lookup, and price ladder/chart construction.
/// </summary>
internal static class OptionMath
{
	internal static double RiskFreeRate = 0.043; // default; updated at runtime from ^IRX when available
	internal static readonly TimeSpan MarketOpen = new(9, 30, 0);
	internal static readonly TimeSpan MarketClose = new(16, 30, 0);

	// --- Black-Scholes ---

	/// <summary>
	/// Computes the Black-Scholes theoretical price for a European option.
	/// </summary>
	internal static decimal BlackScholes(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv, string callPut)
	{
		if (timeYears <= 0)
			return Intrinsic(spot, strike, callPut);

		double s = (double)spot, k = (double)strike, sigma = (double)iv, t = timeYears, r = riskFreeRate;
		double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2.0) * t) / (sigma * Math.Sqrt(t));
		double d2 = d1 - sigma * Math.Sqrt(t);

		double price = callPut == "C"
			? s * NormalCdf(d1) - k * Math.Exp(-r * t) * NormalCdf(d2)
			: k * Math.Exp(-r * t) * NormalCdf(-d2) - s * NormalCdf(-d1);

		return (decimal)Math.Max(0, price);
	}

	/// <summary>
	/// Cumulative distribution function of the standard normal distribution.
	/// Uses the Abramowitz &amp; Stegun approximation (accuracy ~1.5e-7).
	/// </summary>
	internal static double NormalCdf(double x)
	{
		const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
		int sign = x < 0 ? -1 : 1;
		x = Math.Abs(x) / Math.Sqrt(2.0);
		double t = 1.0 / (1.0 + p * x);
		double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
		return 0.5 * (1.0 + sign * y);
	}

	/// <summary>Computes intrinsic value of an option at a given underlying price.</summary>
	internal static decimal Intrinsic(decimal underlyingPrice, decimal strike, string callPut) =>
		callPut == "C" ? Math.Max(0, underlyingPrice - strike) : Math.Max(0, strike - underlyingPrice);

	// --- P&L ---

	/// <summary>Calculates P&L for a single option leg at expiration (intrinsic only).</summary>
	internal static decimal OptionPnLAtExpiration(decimal underlyingPrice, decimal strike, string callPut, Side side, int qty, decimal premium)
	{
		var pnlPerContract = side == Side.Buy ? Intrinsic(underlyingPrice, strike, callPut) - premium : premium - Intrinsic(underlyingPrice, strike, callPut);
		return pnlPerContract * qty * 100;
	}

	/// <summary>
	/// Computes P&L for a single leg. Uses Black-Scholes if the leg has time remaining
	/// past the evaluation date; otherwise uses intrinsic value.
	/// </summary>
	internal static decimal LegPnLWithBs(decimal underlyingPrice, OptionParsed parsed, string symbol, Side side, int qty, decimal premium, DateTime evaluationDate, AnalysisOptions opts)
	{
		decimal legValue;
		var expirationTime = parsed.ExpiryDate.Date + MarketClose;
		var iv = GetLegIv(side, symbol, opts);

		if (iv.HasValue && evaluationDate < expirationTime)
		{
			var timeYears = (expirationTime - evaluationDate).TotalDays / 365.0;
			legValue = BlackScholes(underlyingPrice, parsed.Strike, timeYears, RiskFreeRate, iv.Value, parsed.CallPut);
		}
		else
		{
			legValue = Intrinsic(underlyingPrice, parsed.Strike, parsed.CallPut);
		}

		var pnlPerContract = side == Side.Buy ? legValue - premium : premium - legValue;
		return pnlPerContract * qty * 100;
	}

	/// <summary>Computes total P&L at expiration for all legs (intrinsic value only).</summary>
	internal static decimal StrategyPnLAtExpiration(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty) =>
		legs.Sum(l => OptionPnLAtExpiration(underlyingPrice, l.parsed.Strike, l.parsed.CallPut, l.row.Side, qty, GetPremium(l.row)));

	/// <summary>Computes total P&L using Black-Scholes for legs with remaining time.</summary>
	internal static decimal StrategyPnLWithBs(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty, DateTime evaluationDate, AnalysisOptions opts) =>
		legs.Sum(l => LegPnLWithBs(underlyingPrice, l.parsed, l.symbol, l.row.Side, qty, GetPremium(l.row), evaluationDate, opts));

	// --- IV Lookup ---

	internal static decimal? GetLegIv(Side side, string symbol, AnalysisOptions opts)
	{
		var cliIv = side == Side.Buy ? opts.IvLong : opts.IvShort;
		if (cliIv.HasValue) return cliIv.Value;
		if (opts.OptionQuotes != null && opts.OptionQuotes.TryGetValue(symbol, out var quote) && quote.ImpliedVolatility.HasValue && quote.ImpliedVolatility.Value > 0)
			return quote.ImpliedVolatility.Value;
		return null;
	}

	// --- Shared helpers ---

	internal static decimal GetPremium(PositionRow row) => row.AdjustedAvgPrice ?? row.AvgPrice;

	// --- Price Ladder / Chart ---

	internal static decimal GetPriceStep(decimal referencePrice) => referencePrice switch
	{
		< 10m => 0.50m,
		< 25m => 1m,
		< 100m => 2.50m,
		< 250m => 5m,
		_ => 10m
	};

	/// <summary>Generates a price ladder of ~10 price points centered around notable prices.</summary>
	internal static List<PricePnL> BuildPriceLadder(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt, Func<decimal, decimal, decimal?> valueAt)
	{
		ComputePriceRange(notablePrices, step, out var min, out var max);

		const int maxExtensions = 50;
		for (int i = 0; i < maxExtensions && Math.Round(pnlAt(max), 2) > 0; i++)
			max += step;
		for (int i = 0; i < maxExtensions && min > 0 && Math.Round(pnlAt(min), 2) > 0; i++)
			min = Math.Max(0, min - step);

		var prices = new SortedSet<decimal>();
		for (var p = min; p <= max + step / 2; p += step)
			prices.Add(Math.Round(p, 2));
		prices.Add(Math.Round(max, 2));
		foreach (var p in notablePrices.Where(np => np >= 0))
			prices.Add(Math.Round(p, 2));

		return prices.Select(p =>
		{
			var pnl = Math.Round(pnlAt(p), 2);
			return new PricePnL(p, pnl, valueAt(p, pnl));
		}).ToList();
	}

	/// <summary>Computes the price range [min, max] for a price ladder or chart.</summary>
	internal static void ComputePriceRange(List<decimal> notablePrices, decimal step, out decimal min, out decimal max)
	{
		min = notablePrices.Min() - 2 * step;
		max = notablePrices.Max() + 2 * step;
		if (min < 0) min = 0;
		while ((max - min) / step + 1 < 8)
		{
			min = Math.Max(0, min - step);
			max += step;
		}
	}

	/// <summary>Generates ~100 evenly-spaced data points for smooth chart rendering.</summary>
	internal static List<PricePnL> BuildChartData(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt, Func<decimal, decimal, decimal?> valueAt)
	{
		ComputePriceRange(notablePrices, step, out var min, out var max);
		const int pointCount = 100;
		var chartStep = (max - min) / (pointCount - 1);
		var points = new List<PricePnL>(pointCount);
		for (int i = 0; i < pointCount; i++)
		{
			var price = Math.Round(min + chartStep * i, 4);
			var pnl = Math.Round(pnlAt(price), 2);
			points.Add(new PricePnL(price, pnl, valueAt(price, pnl)));
		}
		return points;
	}

	/// <summary>Finds all prices where P&L crosses zero using linear interpolation.</summary>
	internal static List<decimal> FindBreakEvensNumerically(List<PricePnL> ladder, Func<decimal, decimal>? pnlFunc = null)
	{
		var results = new List<decimal>();
		for (var i = 0; i < ladder.Count - 1; i++)
		{
			var curr = ladder[i];
			var next = ladder[i + 1];
			if (curr.PnL == 0)
			{
				results.Add(curr.UnderlyingPrice);
				continue;
			}
			if ((curr.PnL > 0 && next.PnL < 0) || (curr.PnL < 0 && next.PnL > 0))
			{
				if (pnlFunc != null)
					results.Add(BisectBreakEven(pnlFunc, curr.UnderlyingPrice, curr.PnL, next.UnderlyingPrice, next.PnL));
				else
				{
					var fraction = Math.Abs(curr.PnL) / (Math.Abs(curr.PnL) + Math.Abs(next.PnL));
					results.Add(Math.Round(curr.UnderlyingPrice + fraction * (next.UnderlyingPrice - curr.UnderlyingPrice), 2));
				}
			}
		}
		if (ladder.Count > 0 && ladder[^1].PnL == 0 && (results.Count == 0 || results[^1] != ladder[^1].UnderlyingPrice))
			results.Add(ladder[^1].UnderlyingPrice);
		return results;
	}

	/// <summary>Refines a breakeven price using bisection between two points with opposite P&L signs.</summary>
	internal static decimal BisectBreakEven(Func<decimal, decimal> pnlFunc, decimal lo, decimal loVal, decimal hi, decimal hiVal)
	{
		if (loVal > 0) { (lo, hi) = (hi, lo); (loVal, hiVal) = (hiVal, loVal); }
		for (int i = 0; i < 50; i++)
		{
			var mid = Math.Round((lo + hi) / 2, 4);
			if (mid == lo || mid == hi) break;
			var midVal = Math.Round(pnlFunc(mid), 2);
			if (midVal == 0) { lo = hi = mid; break; }
			if (midVal < 0) lo = mid;
			else hi = mid;
		}
		return Math.Round((lo + hi) / 2, 2);
	}
}
