using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// Analyzes open positions to calculate break-even prices, max profit/loss,
/// and at-expiration P&L across a price ladder.
/// When implied volatility is provided, uses Black-Scholes pricing for
/// calendar/diagonal spreads to account for the long leg's remaining time value.
/// </summary>
public static class BreakEvenAnalyzer
{
	private const double RiskFreeRate = 0.043; // ~4.3% annual, reasonable default

	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, decimal? impliedVolatility = null)
	{
		var groups = GroupPositions(positionRows);
		var results = new List<BreakEvenResult>();
		foreach (var group in groups)
		{
			var result = AnalyzeGroup(group, impliedVolatility);
			if (result != null) results.Add(result);
		}
		return results;
	}

	/// <summary>
	/// Groups position rows: each non-leg row starts a new group; subsequent IsStrategyLeg rows belong to it.
	/// </summary>
	private static List<List<PositionRow>> GroupPositions(List<PositionRow> rows)
	{
		var groups = new List<List<PositionRow>>();
		foreach (var row in rows)
		{
			if (!row.IsStrategyLeg)
				groups.Add([row]);
			else if (groups.Count > 0)
				groups[^1].Add(row);
		}
		return groups;
	}

	private static BreakEvenResult? AnalyzeGroup(List<PositionRow> group, decimal? iv)
	{
		var parent = group[0];

		if (parent.Asset == Asset.Stock)
			return AnalyzeStock(parent);

		if (parent.Asset == Asset.Option)
			return AnalyzeSingleOption(parent, iv);

		if (parent.Asset == Asset.OptionStrategy && group.Count > 1)
			return AnalyzeStrategy(parent, group.Skip(1).ToList(), iv);

		return null;
	}

	private static BreakEvenResult AnalyzeStock(PositionRow row)
	{
		var avgPrice = row.AvgPrice;
		var isLong = row.Side == Side.Buy;
		var title = $"{row.Instrument} {(isLong ? "Long" : "Short")}";
		var details = $"{row.Qty}x @ ${avgPrice.ToString("N2", CultureInfo.InvariantCulture)}";

		var step = GetPriceStep(avgPrice);
		var notablePrices = new List<decimal> { avgPrice };
		var ladder = BuildPriceLadder(notablePrices, step, s => isLong ? (s - avgPrice) * row.Qty : (avgPrice - s) * row.Qty);

		Func<decimal, decimal> pnlFunc = s => isLong ? (s - avgPrice) * row.Qty : (avgPrice - s) * row.Qty;
		var chartData = BuildChartData(notablePrices, step, pnlFunc);

		return new BreakEvenResult(Title: title, Details: details, Qty: row.Qty, BreakEvens: [avgPrice], MaxProfit: isLong ? null : avgPrice * row.Qty, MaxLoss: isLong ? avgPrice * row.Qty : null, DaysToExpiry: null, PriceLadder: ladder, Note: null, ChartData: chartData);
	}

	private static BreakEvenResult? AnalyzeSingleOption(PositionRow row, decimal? iv)
	{
		var parsed = ParseOption(row);
		if (parsed == null) return null;

		var premium = GetPremium(row);
		var isLong = row.Side == Side.Buy;
		var isCall = parsed.CallPut == "C";
		var strike = parsed.Strike;
		var qty = row.Qty;

		var title = $"{parsed.Root} {(isLong ? "Long" : "Short")} {(isCall ? "Call" : "Put")} ${Formatters.FormatQty(strike)}";
		var details = BuildDetailsString(row);

		decimal breakEven;
		decimal? maxProfit, maxLoss;

		if (isCall)
		{
			breakEven = strike + premium;
			maxProfit = isLong ? null : premium * qty * 100;
			maxLoss = isLong ? premium * qty * 100 : null;
		}
		else
		{
			breakEven = strike - premium;
			maxProfit = isLong ? (strike - premium) * qty * 100 : premium * qty * 100;
			maxLoss = isLong ? premium * qty * 100 : (strike - premium) * qty * 100;
		}

		var notablePrices = new List<decimal> { strike, breakEven };
		var step = GetPriceStep(strike);
		Func<decimal, decimal> pnlFunc = s => OptionPnLAtExpiration(s, strike, parsed.CallPut, row.Side, qty, premium);
		var ladder = BuildPriceLadder(notablePrices, step, pnlFunc);
		var chartData = BuildChartData(notablePrices, step, pnlFunc);

		var dte = row.Expiry.HasValue ? (int)(row.Expiry.Value.Date - DateTime.Today).TotalDays : (int?)null;

		EarlyExerciseBoundary? earlyExercise = null;
		if (isLong && iv.HasValue && dte.HasValue && dte.Value > 0)
		{
			var timeYears = dte.Value / 365.0;
			earlyExercise = BjerksundStensland.ComputeExerciseBoundary(strike, timeYears, RiskFreeRate, (double)iv.Value, parsed.CallPut);
		}

		return new BreakEvenResult(title, details, qty, [breakEven], maxProfit, maxLoss, dte, ladder, null, ChartData: chartData, EarlyExercise: earlyExercise);
	}

	private static BreakEvenResult? AnalyzeStrategy(PositionRow parent, List<PositionRow> legs, decimal? iv)
	{
		var parsedLegs = legs.Select(l => (row: l, parsed: ParseOption(l))).Where(x => x.parsed != null).Select(x => (x.row, parsed: x.parsed!)).ToList();
		if (parsedLegs.Count < 2) return null;

		var root = parsedLegs[0].parsed.Root;
		var callPut = parsedLegs[0].parsed.CallPut;
		var callPutDisplay = callPut == "C" ? "Call" : "Put";
		var strategyKind = parent.OptionKind;
		var qty = parent.Qty;
		var netPremium = GetPremium(parent);

		var strikes = parsedLegs.Select(l => l.parsed.Strike).Distinct().OrderBy(x => x).ToList();
		var expiries = parsedLegs.Select(l => l.parsed.ExpiryDate).Distinct().OrderBy(x => x).ToList();

		// Build title
		string title;
		if (strategyKind is "IronCondor" or "IronButterfly")
		{
			var putStrikes = parsedLegs.Where(l => l.parsed.CallPut == "P").Select(l => l.parsed.Strike).Distinct().OrderBy(s => s);
			var callStrikes = parsedLegs.Where(l => l.parsed.CallPut == "C").Select(l => l.parsed.Strike).Distinct().OrderBy(s => s);
			var allStrikes = putStrikes.Concat(callStrikes).Select(s => $"${Formatters.FormatQty(s)}");
			title = $"{root} {strategyKind} {string.Join("/", allStrikes)}";
		}
		else if (strategyKind is "Condor" or "Butterfly")
		{
			var allStrikes = strikes.Select(s => $"${Formatters.FormatQty(s)}");
			title = $"{root} {strategyKind} {callPutDisplay} {string.Join("/", allStrikes)}";
		}
		else if (strikes.Count > 1)
			title = $"{root} {strategyKind} {callPutDisplay} ${Formatters.FormatQty(strikes[0])}/${Formatters.FormatQty(strikes[^1])}";
		else
			title = $"{root} {strategyKind} {callPutDisplay} ${Formatters.FormatQty(strikes[0])}";

		var details = BuildDetailsString(parent);

		// Build leg descriptions
		var legDescriptions = parsedLegs.Select(l =>
		{
			var longShort = l.row.Side == Side.Buy ? "Long" : "Short";
			var cpDisplay = l.parsed.CallPut == "C" ? "Call" : "Put";
			var legPremium = GetPremium(l.row);
			var desc = $"{longShort} {cpDisplay} ${Formatters.FormatQty(l.parsed.Strike)} @ ${Formatters.FormatPrice(legPremium, Asset.Option)}, Exp {Formatters.FormatOptionDate(l.parsed.ExpiryDate)}";
			if (l.row.Side == Side.Buy && iv.HasValue)
			{
				var legDte = (l.parsed.ExpiryDate.Date - DateTime.Today).TotalDays;
				if (legDte > 0)
				{
					var boundary = BjerksundStensland.ComputeExerciseBoundary(l.parsed.Strike, legDte / 365.0, RiskFreeRate, (double)iv.Value, l.parsed.CallPut);
					if (boundary != null)
						desc += $" | Exercise below ${boundary.BoundaryNear.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}";
				}
			}
			return desc;
		}).ToList();

		var isTimeSpread = expiries.Count > 1;
		var nearestExpiry = expiries[0];
		var dte = (int)(nearestExpiry.Date - DateTime.Today).TotalDays;

		var breakEvens = new List<decimal>();
		decimal? maxProfit = null;
		decimal? maxLoss = null;
		string? note = null;

		if (strategyKind == "Vertical" && strikes.Count == 2)
		{
			var lowK = strikes[0];
			var highK = strikes[1];
			var width = highK - lowK;

			breakEvens.Add(callPut == "C" ? lowK + netPremium : highK - netPremium);

			if (parent.Side == Side.Buy) // debit spread
			{
				maxProfit = (width - netPremium) * qty * 100;
				maxLoss = netPremium * qty * 100;
			}
			else // credit spread
			{
				maxProfit = netPremium * qty * 100;
				maxLoss = (width - netPremium) * qty * 100;
			}
		}
		else if (isTimeSpread && !iv.HasValue)
		{
			// Cannot compute accurate P&L without implied volatility
			note = "Break-even analysis requires implied volatility. Use --iv to specify (e.g., --iv 50 for 50%).";
			return new BreakEvenResult(title, details, qty, [], null, null, dte, [], note, legDescriptions);
		}
		else if (isTimeSpread)
		{
			// Black-Scholes pricing: evaluate at the short leg's expiry
			note = $"P&L estimated using {(iv!.Value * 100):G}% IV at short leg expiry. Actual results will vary with volatility.";

			if (parent.Side == Side.Buy)
				maxLoss = netPremium * qty * 100;
		}

		// Build price ladder
		var notablePrices = new List<decimal>(strikes);
		notablePrices.AddRange(breakEvens);
		var step = GetPriceStep(strikes.Average());

		Func<decimal, decimal> pnlFunc;
		if (isTimeSpread)
			pnlFunc = s => StrategyPnLWithBs(s, parsedLegs, qty, nearestExpiry, iv!.Value);
		else
			pnlFunc = s => StrategyPnLAtExpiration(s, parsedLegs, qty);

		var ladder = BuildPriceLadder(notablePrices, step, pnlFunc);

		// Find break-evens numerically if not already set analytically
		if (breakEvens.Count == 0)
			breakEvens = FindBreakEvensNumerically(ladder);

		// Insert numerically-found break-even prices into the ladder
		foreach (var be in breakEvens)
		{
			if (!ladder.Any(p => Math.Abs(p.UnderlyingPrice - be) < 0.005m))
				ladder.Add(new PricePnL(be, Math.Round(pnlFunc(be), 2)));
		}
		ladder.Sort((a, b) => a.UnderlyingPrice.CompareTo(b.UnderlyingPrice));

		// Estimate max profit/loss from ladder
		if (!maxProfit.HasValue)
		{
			var maxPnL = ladder.Max(p => p.PnL);
			if (maxPnL > 0) maxProfit = maxPnL;
		}

		if (!maxLoss.HasValue)
		{
			var minPnL = ladder.Min(p => p.PnL);
			if (minPnL < 0)
				maxLoss = Math.Abs(minPnL);
		}

		var chartData = BuildChartData(notablePrices, step, pnlFunc);

		return new BreakEvenResult(title, details, qty, breakEvens, maxProfit, maxLoss, dte, ladder, note, legDescriptions, chartData);
	}

	/// <summary>
	/// Computes total P&L at expiration for all legs (intrinsic value only).
	/// </summary>
	private static decimal StrategyPnLAtExpiration(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed)> legs, int qty)
	{
		return legs.Sum(l => OptionPnLAtExpiration(underlyingPrice, l.parsed.Strike, l.parsed.CallPut, l.row.Side, qty, GetPremium(l.row)));
	}

	/// <summary>
	/// Computes total P&L at the evaluation date using Black-Scholes for legs with remaining time,
	/// and intrinsic value for legs expiring at or before the evaluation date.
	/// </summary>
	private static decimal StrategyPnLWithBs(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed)> legs, int qty, DateTime evaluationDate, decimal iv)
	{
		return legs.Sum(l => LegPnLWithBs(underlyingPrice, l.parsed, l.row.Side, qty, GetPremium(l.row), evaluationDate, iv));
	}

	/// <summary>
	/// Computes P&L for a single leg. Uses Black-Scholes if the leg has time remaining
	/// past the evaluation date; otherwise uses intrinsic value (at-expiration).
	/// </summary>
	private static decimal LegPnLWithBs(decimal underlyingPrice, OptionParsed parsed, Side side, int qty, decimal premium, DateTime evaluationDate, decimal iv)
	{
		decimal legValue;
		if (parsed.ExpiryDate > evaluationDate)
		{
			var timeYears = (parsed.ExpiryDate - evaluationDate).TotalDays / 365.0;
			legValue = BlackScholes(underlyingPrice, parsed.Strike, timeYears, RiskFreeRate, iv, parsed.CallPut);
		}
		else
		{
			legValue = parsed.CallPut == "C" ? Math.Max(0, underlyingPrice - parsed.Strike) : Math.Max(0, parsed.Strike - underlyingPrice);
		}

		var pnlPerContract = side == Side.Buy ? legValue - premium : premium - legValue;
		return pnlPerContract * qty * 100;
	}

	// --- Black-Scholes implementation ---

	/// <summary>
	/// Computes the Black-Scholes theoretical price for a European option.
	/// </summary>
	/// <param name="spot">Current underlying price</param>
	/// <param name="strike">Option strike price</param>
	/// <param name="timeYears">Time to expiration in years</param>
	/// <param name="riskFreeRate">Annual risk-free interest rate (e.g., 0.043 for 4.3%)</param>
	/// <param name="iv">Annual implied volatility as a decimal fraction (e.g., 0.50 for 50%)</param>
	/// <param name="callPut">"C" for call, "P" for put</param>
	private static decimal BlackScholes(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv, string callPut)
	{
		if (timeYears <= 0)
		{
			var intrinsic = callPut == "C" ? Math.Max(0, spot - strike) : Math.Max(0, strike - spot);
			return intrinsic;
		}

		double s = (double)spot;
		double k = (double)strike;
		double sigma = (double)iv;
		double t = timeYears;
		double r = riskFreeRate;

		double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2.0) * t) / (sigma * Math.Sqrt(t));
		double d2 = d1 - sigma * Math.Sqrt(t);

		double price;
		if (callPut == "C")
			price = s * NormalCdf(d1) - k * Math.Exp(-r * t) * NormalCdf(d2);
		else
			price = k * Math.Exp(-r * t) * NormalCdf(-d2) - s * NormalCdf(-d1);

		return (decimal)Math.Max(0, price);
	}

	/// <summary>
	/// Cumulative distribution function of the standard normal distribution.
	/// Uses the Abramowitz &amp; Stegun approximation (accuracy ~1.5e-7).
	/// </summary>
	private static double NormalCdf(double x)
	{
		const double a1 = 0.254829592;
		const double a2 = -0.284496736;
		const double a3 = 1.421413741;
		const double a4 = -1.453152027;
		const double a5 = 1.061405429;
		const double p = 0.3275911;

		int sign = x < 0 ? -1 : 1;
		x = Math.Abs(x) / Math.Sqrt(2.0);

		double t = 1.0 / (1.0 + p * x);
		double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

		return 0.5 * (1.0 + sign * y);
	}

	// --- Helpers ---

	private static OptionParsed? ParseOption(PositionRow row)
	{
		if (row.MatchKey == null || !MatchKeys.TryGetOptionSymbol(row.MatchKey, out var symbol))
			return null;
		return ParsingHelpers.ParseOptionSymbol(symbol);
	}

	private static decimal GetPremium(PositionRow row) => row.AdjustedAvgPrice ?? row.AvgPrice;

	private static string BuildDetailsString(PositionRow row)
	{
		var premium = GetPremium(row);
		var premiumStr = Formatters.FormatPrice(premium, row.Asset);
		var hasAdjustment = row.AdjustedAvgPrice.HasValue && row.AdjustedAvgPrice.Value != (row.InitialAvgPrice ?? row.AvgPrice);
		var adjSuffix = hasAdjustment ? " adj" : "";
		var expiryStr = row.Expiry.HasValue ? Formatters.FormatOptionDate(row.Expiry.Value) : "N/A";
		return $"{row.Qty}x @ ${premiumStr}{adjSuffix}, Exp {expiryStr}";
	}

	/// <summary>
	/// Calculates P&L for a single option leg at a given underlying price (at expiration, intrinsic only).
	/// </summary>
	private static decimal OptionPnLAtExpiration(decimal underlyingPrice, decimal strike, string callPut, Side side, int qty, decimal premium)
	{
		var intrinsic = callPut == "C" ? Math.Max(0, underlyingPrice - strike) : Math.Max(0, strike - underlyingPrice);
		var pnlPerContract = side == Side.Buy ? intrinsic - premium : premium - intrinsic;
		return pnlPerContract * qty * 100;
	}

	private static decimal GetPriceStep(decimal referencePrice)
	{
		return referencePrice switch
		{
			< 10m => 0.50m,
			< 25m => 1m,
			< 100m => 2.50m,
			< 250m => 5m,
			_ => 10m
		};
	}

	/// <summary>
	/// Generates a price ladder of ~10 price points centered around notable prices (strikes, break-evens).
	/// </summary>
	private static List<PricePnL> BuildPriceLadder(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt)
	{
		var min = notablePrices.Min() - 2 * step;
		var max = notablePrices.Max() + 2 * step;
		if (min < 0) min = 0;

		// Extend range if fewer than ~8 stepped points
		while ((max - min) / step + 1 < 8)
		{
			min = Math.Max(0, min - step);
			max += step;
		}

		var prices = new SortedSet<decimal>();

		for (var p = min; p <= max + step / 2; p += step)
			prices.Add(Math.Round(p, 2));

		foreach (var p in notablePrices.Where(np => np >= 0))
			prices.Add(Math.Round(p, 2));

		return prices.Select(p => new PricePnL(p, Math.Round(pnlAt(p), 2))).ToList();
	}

	/// <summary>
	/// Generates ~100 evenly-spaced data points for smooth chart rendering.
	/// Uses the same price range as the discrete ladder but with finer granularity.
	/// </summary>
	private static List<PricePnL> BuildChartData(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt)
	{
		var min = notablePrices.Min() - 2 * step;
		var max = notablePrices.Max() + 2 * step;
		if (min < 0) min = 0;

		while ((max - min) / step + 1 < 8)
		{
			min = Math.Max(0, min - step);
			max += step;
		}

		const int pointCount = 100;
		var chartStep = (max - min) / (pointCount - 1);
		var points = new List<PricePnL>(pointCount);
		for (int i = 0; i < pointCount; i++)
		{
			var price = Math.Round(min + chartStep * i, 4);
			points.Add(new PricePnL(price, Math.Round(pnlAt(price), 2)));
		}

		return points;
	}

	/// <summary>
	/// Finds all prices where P&L crosses zero using linear interpolation.
	/// </summary>
	private static List<decimal> FindBreakEvensNumerically(List<PricePnL> ladder)
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
				var fraction = Math.Abs(curr.PnL) / (Math.Abs(curr.PnL) + Math.Abs(next.PnL));
				results.Add(Math.Round(curr.UnderlyingPrice + fraction * (next.UnderlyingPrice - curr.UnderlyingPrice), 2));
			}
		}

		if (ladder.Count > 0 && ladder[^1].PnL == 0 && (results.Count == 0 || results[^1] != ladder[^1].UnderlyingPrice))
			results.Add(ladder[^1].UnderlyingPrice);

		return results;
	}
}
