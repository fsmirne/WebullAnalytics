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
	private static readonly TimeSpan MarketOpen = new(9, 30, 0);
	private static readonly TimeSpan MarketClose = new(16, 30, 0);

	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, decimal? ivLong = null, decimal? ivShort = null, decimal padding = 2, int maxGridColumns = 7, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol = null, IReadOnlyDictionary<string, decimal>? underlyingPrices = null)
	{
		var groups = GroupPositions(positionRows);
		var results = new List<BreakEvenResult>();
		foreach (var group in groups)
		{
			var result = AnalyzeGroup(group, ivLong, ivShort, padding, maxGridColumns, optionQuotesBySymbol, underlyingPrices);
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

	private static BreakEvenResult? AnalyzeGroup(List<PositionRow> group, decimal? ivLong, decimal? ivShort, decimal padding, int maxGridColumns, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		var parent = group[0];

		if (parent.Asset == Asset.Stock)
			return AnalyzeStock(parent);

		if (parent.Asset == Asset.Option)
			return AnalyzeSingleOption(parent, ivLong, ivShort, padding, maxGridColumns, optionQuotesBySymbol, underlyingPrices);

		if (parent.Asset == Asset.OptionStrategy && group.Count > 1)
			return AnalyzeStrategy(parent, group.Skip(1).ToList(), ivLong, ivShort, padding, maxGridColumns, optionQuotesBySymbol, underlyingPrices);

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
		Func<decimal, decimal> pnlFunc = s => isLong ? (s - avgPrice) * row.Qty : (avgPrice - s) * row.Qty;
		var ladder = BuildPriceLadder(notablePrices, step, pnlFunc, (s, pnl) => null);
		var chartData = BuildChartData(notablePrices, step, pnlFunc, (s, pnl) => null);

		return new BreakEvenResult(Title: title, Details: details, Qty: row.Qty, BreakEvens: [avgPrice], MaxProfit: isLong ? null : avgPrice * row.Qty, MaxLoss: isLong ? avgPrice * row.Qty : null, DaysToExpiry: null, PriceLadder: ladder, Note: null, ChartData: chartData);
	}

	private static BreakEvenResult? AnalyzeSingleOption(PositionRow row, decimal? ivLong, decimal? ivShort, decimal padding, int maxGridColumns, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		var parsedInfo = ParseOption(row);
		if (parsedInfo == null) return null;
		var (parsed, symbol) = parsedInfo.Value;

		var premium = GetPremium(row);
		var isLong = row.Side == Side.Buy;
		var isCall = parsed.CallPut == "C";
		var strike = parsed.Strike;
		var qty = row.Qty;

		var title = $"{parsed.Root} {(isLong ? "Long" : "Short")} {ParsingHelpers.CallPutDisplayName(parsed.CallPut)} ${Formatters.FormatQty(strike)}";
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

		var spot = LookupUnderlyingPrice(parsed.Root, underlyingPrices);
		var notablePrices = new List<decimal> { strike, breakEven };
		if (spot.HasValue) notablePrices.Add(spot.Value);
		var step = GetPriceStep(strike);
		Func<decimal, decimal> pnlFunc = s => OptionPnLAtExpiration(s, strike, parsed.CallPut, row.Side, qty, premium);
		Func<decimal, decimal, decimal?> valueAt = (s, pnl) => isLong ? (pnl / (qty * 100m)) + premium : premium - (pnl / (qty * 100m));
		var ladder = BuildPriceLadder(notablePrices, step, pnlFunc, valueAt);
		var chartData = BuildChartData(notablePrices, step, pnlFunc, valueAt);

		var dte = row.Expiry.HasValue ? (int)(row.Expiry.Value.Date - DateTime.Today).TotalDays : (int?)null;

		EarlyExerciseBoundary? earlyExercise = null;
		var iv = GetLegIv(row.Side, symbol, optionQuotesBySymbol, ivLong, ivShort);
		if (isLong && iv.HasValue && dte.HasValue && dte.Value > 0)
		{
			var timeYears = dte.Value / 365.0;
			earlyExercise = BjerksundStensland.ComputeExerciseBoundary(strike, timeYears, RiskFreeRate, (double)iv.Value, parsed.CallPut);
		}

		TimeDecayGrid? grid = null;
		if (iv.HasValue && dte.HasValue && dte.Value > 0)
		{
			var legsList = new List<(PositionRow row, OptionParsed parsed, string symbol)> { (row, parsed, symbol) };
			var gridBreakEvens = new List<decimal> { breakEven };
			if (spot.HasValue) gridBreakEvens.Add(spot.Value);
			grid = BuildTimeDecayGrid(legsList, qty, row.Side, premium, parsed.ExpiryDate, ivLong, ivShort, padding, strike, gridBreakEvens, maxGridColumns, optionQuotesBySymbol, spot);
		}

		List<string>? legsDisplay = null;
		var legIvOverride = row.Side == Side.Buy ? ivLong : ivShort;
		var yahooInfo = TryFormatYahooQuote(symbol, optionQuotesBySymbol, legIvOverride);
		if (yahooInfo != null)
			legsDisplay = [$"Market: {yahooInfo}"];

		return new BreakEvenResult(title, details, qty, [breakEven], maxProfit, maxLoss, dte, ladder, Note: null, Legs: legsDisplay, ChartData: chartData, EarlyExercise: earlyExercise, Grid: grid, UnderlyingPrice: spot);
	}

	private static BreakEvenResult? AnalyzeStrategy(PositionRow parent, List<PositionRow> legs, decimal? ivLong, decimal? ivShort, decimal padding, int maxGridColumns, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		var parsedLegs = legs.Select(l => (row: l, parsed: ParseOption(l))).Where(x => x.parsed != null).Select(x => (x.row, x.parsed!.Value.parsed, x.parsed!.Value.symbol)).ToList();
		if (parsedLegs.Count < 2) return null;

		var root = parsedLegs[0].parsed.Root;
		var callPut = parsedLegs[0].parsed.CallPut;
		var callPutDisplay = ParsingHelpers.CallPutDisplayName(callPut);
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
			var cpDisplay = ParsingHelpers.CallPutDisplayName(l.parsed.CallPut);
			var legPremium = GetPremium(l.row);
			var desc = $"{longShort} {cpDisplay} ${Formatters.FormatQty(l.parsed.Strike)} @ ${Formatters.FormatPrice(legPremium, Asset.Option)}, Exp {Formatters.FormatOptionDate(l.parsed.ExpiryDate)}";

			var legIv = GetLegIv(l.row.Side, l.symbol, optionQuotesBySymbol, ivLong, ivShort);
			var legIvOverride = l.row.Side == Side.Buy ? ivLong : ivShort;
			var yahooInfo = TryFormatYahooQuote(l.symbol, optionQuotesBySymbol, legIvOverride);
			if (yahooInfo != null)
				desc += $" | {yahooInfo}";

			if (l.row.Side == Side.Buy && legIv.HasValue)
			{
				var legDte = (l.parsed.ExpiryDate.Date - DateTime.Today).TotalDays;
				if (legDte > 0)
				{
					var boundary = BjerksundStensland.ComputeExerciseBoundary(l.parsed.Strike, legDte / 365.0, RiskFreeRate, (double)legIv.Value, l.parsed.CallPut);
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
		else if (isTimeSpread && !HasIvForRemainingTimeLegs(parsedLegs, nearestExpiry, optionQuotesBySymbol, ivLong, ivShort))
		{
			// Cannot compute accurate P&L without implied volatility
			note = "Break-even analysis requires implied volatility. Provide --iv-long/--iv-short or enable Yahoo option-chain lookup with --yahoo.";
			return new BreakEvenResult(title, details, qty, [], null, null, dte, [], note, legDescriptions, UnderlyingPrice: LookupUnderlyingPrice(root, underlyingPrices));
		}
		else if (isTimeSpread)
		{
			// Black-Scholes pricing: evaluate at the short leg's expiry
			note = "P&L estimated using Black-Scholes at short leg expiry. Actual results will vary with volatility.";

			if (parent.Side == Side.Buy)
				maxLoss = netPremium * qty * 100;
		}

		// Build price ladder
		var spot = LookupUnderlyingPrice(root, underlyingPrices);
		var notablePrices = new List<decimal>(strikes);
		notablePrices.AddRange(breakEvens);
		if (spot.HasValue) notablePrices.Add(spot.Value);
		var step = GetPriceStep(strikes.Average());

		Func<decimal, decimal> pnlFunc;
		if (isTimeSpread)
			pnlFunc = s => StrategyPnLWithBs(s, parsedLegs, qty, nearestExpiry.Date + MarketClose, ivLong, ivShort, optionQuotesBySymbol);
		else
			pnlFunc = s => StrategyPnLAtExpiration(s, parsedLegs, qty);

		Func<decimal, decimal, decimal?> valueAt = (s, pnl) => parent.Side == Side.Buy ? (pnl / (qty * 100m)) + netPremium : netPremium - (pnl / (qty * 100m));
		var ladder = BuildPriceLadder(notablePrices, step, pnlFunc, valueAt);

		// Find break-evens numerically if not already set analytically
		if (breakEvens.Count == 0)
			breakEvens = FindBreakEvensNumerically(ladder);

		// Insert numerically-found break-even prices into the ladder
		foreach (var be in breakEvens)
		{
			if (!ladder.Any(p => Math.Abs(p.UnderlyingPrice - be) < 0.005m))
				ladder.Add(new PricePnL(be, 0m, valueAt(be, 0m)));
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

		var chartData = BuildChartData(notablePrices, step, pnlFunc, valueAt);

		TimeDecayGrid? grid = null;
		if (dte > 0 && HasIvForRemainingTimeLegs(parsedLegs, nearestExpiry, optionQuotesBySymbol, ivLong, ivShort))
		{
			var gridNotable = new List<decimal>(breakEvens);
			if (spot.HasValue) gridNotable.Add(spot.Value);
			grid = BuildTimeDecayGrid(parsedLegs, qty, parent.Side, netPremium, nearestExpiry, ivLong, ivShort, padding, strikes.Average(), gridNotable, maxGridColumns, optionQuotesBySymbol, spot);
		}

		return new BreakEvenResult(title, details, qty, breakEvens, maxProfit, maxLoss, dte, ladder, note, legDescriptions, chartData, Grid: grid, UnderlyingPrice: spot);
	}

	/// <summary>
	/// Computes total P&L at expiration for all legs (intrinsic value only).
	/// </summary>
	private static decimal StrategyPnLAtExpiration(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty)
	{
		return legs.Sum(l => OptionPnLAtExpiration(underlyingPrice, l.parsed.Strike, l.parsed.CallPut, l.row.Side, qty, GetPremium(l.row)));
	}

	/// <summary>
	/// Computes total P&L at the evaluation date using Black-Scholes for legs with remaining time,
	/// and intrinsic value for legs expiring at or before the evaluation date.
	/// </summary>
	private static decimal StrategyPnLWithBs(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty, DateTime evaluationDate, decimal? ivLong, decimal? ivShort, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol)
	{
		return legs.Sum(l => LegPnLWithBs(underlyingPrice, l.parsed, l.symbol, l.row.Side, qty, GetPremium(l.row), evaluationDate, ivLong, ivShort, optionQuotesBySymbol));
	}

	/// <summary>
	/// Computes P&L for a single leg. Uses Black-Scholes if the leg has time remaining
	/// past the evaluation date; otherwise uses intrinsic value (at-expiration).
	/// Option expiration is defined as market close (4:30 PM) on the expiry date.
	/// </summary>
	private static decimal LegPnLWithBs(decimal underlyingPrice, OptionParsed parsed, string symbol, Side side, int qty, decimal premium, DateTime evaluationDate, decimal? ivLong, decimal? ivShort, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol)
	{
		decimal legValue;
		var expirationTime = parsed.ExpiryDate.Date + MarketClose;
		var iv = GetLegIv(side, symbol, optionQuotesBySymbol, ivLong, ivShort);

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
			return Intrinsic(spot, strike, callPut);

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

	// --- Time-Decay Grid ---

	/// <summary>
	/// Builds a 2D grid of option values across dates and underlying prices.
	/// </summary>
	private static TimeDecayGrid BuildTimeDecayGrid(List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty, Side parentSide, decimal netPremium, DateTime latestExpiry, decimal? ivLong, decimal? ivShort, decimal padding, decimal centerPrice, List<decimal> breakEvens, int maxColumns, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, decimal? underlyingPrice)
	{
		var dates = BuildDateColumns(latestExpiry, maxColumns);
		var strikes = legs.Select(l => l.parsed.Strike).Distinct().ToList();
		var priceRows = BuildPriceRows(centerPrice, padding, breakEvens, strikes);

		var values = new decimal[priceRows.Count, dates.Count];
		var pnls = new decimal[priceRows.Count, dates.Count];

		for (int di = 0; di < dates.Count; di++)
		{
			var evalDate = dates[di];
			for (int pi = 0; pi < priceRows.Count; pi++)
			{
				var price = priceRows[pi];
				var totalPnL = legs.Sum(l => LegPnLWithBs(price, l.parsed, l.symbol, l.row.Side, qty, GetPremium(l.row), evalDate, ivLong, ivShort, optionQuotesBySymbol));

				pnls[pi, di] = Math.Round(totalPnL, 2);
				values[pi, di] = parentSide == Side.Buy ? Math.Round(netPremium + totalPnL / (qty * 100m), 4) : Math.Round(netPremium - totalPnL / (qty * 100m), 4);
			}
		}

		// Anchor the first column (today) to market bid/ask mid-prices when available.
		// This corrects for IV discrepancies between Yahoo and the broker by computing the
		// offset between BS theoretical and market mid at the current underlying price,
		// then applying that offset to all rows in the today column.
		if (optionQuotesBySymbol != null && underlyingPrice.HasValue)
		{
			var marketMid = ComputeSpreadMarketMid(legs, optionQuotesBySymbol);
			if (marketMid.HasValue)
			{
				int closestRow = 0;
				var closestDist = decimal.MaxValue;
				for (int pi = 0; pi < priceRows.Count; pi++)
				{
					var dist = Math.Abs(priceRows[pi] - underlyingPrice.Value);
					if (dist < closestDist) { closestDist = dist; closestRow = pi; }
				}

				var bsValue = values[closestRow, 0];
				var adjustment = bsValue - marketMid.Value;
				if (adjustment != 0)
				{
					for (int pi = 0; pi < priceRows.Count; pi++)
					{
						values[pi, 0] = Math.Round(values[pi, 0] - adjustment, 4);
						pnls[pi, 0] = parentSide == Side.Buy ? Math.Round((values[pi, 0] - netPremium) * qty * 100, 2) : Math.Round((netPremium - values[pi, 0]) * qty * 100, 2);
					}
				}
			}
		}

		return new TimeDecayGrid(dates, priceRows, values, pnls, strikes);
	}

	/// <summary>
	/// Generates ~7 evenly-spaced date columns from today to expiration.
	/// If DTE &lt; 7, uses daily intervals.
	/// All non-expiration dates use market open (9:30 AM).
	/// The last two columns are expiration day at market open (9:30 AM) and market close (4:30 PM),
	/// representing BS value with remaining intraday time vs intrinsic at expiry.
	/// </summary>
	private static List<DateTime> BuildDateColumns(DateTime expiry, int maxColumns)
	{
		var today = DateTime.Today;
		var totalDays = (int)(expiry.Date - today).TotalDays;
		if (totalDays <= 0) return [today + MarketOpen, expiry.Date + MarketClose];

		var expiryOpen = expiry.Date + MarketOpen;   // start of expiration trading day — BS with ~7h remaining
		var expiryClose = expiry.Date + MarketClose;  // end of expiration trading day — intrinsic only

		// Reserve 2 slots for expiry open/close, rest are interior dates
		var interiorSlots = Math.Max(1, maxColumns - 2);
		var dates = new List<DateTime>();

		if (totalDays <= interiorSlots)
		{
			for (int d = 0; d < totalDays; d++)
				dates.Add(today.AddDays(d) + MarketOpen);
		}
		else
		{
			for (int i = 0; i < interiorSlots; i++)
			{
				var dayOffset = (int)Math.Round((double)i * (totalDays - 1) / (interiorSlots - 1));
				var date = today.AddDays(dayOffset);
				if (date.Date >= expiry.Date) break;
				dates.Add(date + MarketOpen);
			}
		}

		// Remove any date that landed on expiry day (shouldn't happen, but guard)
		dates.RemoveAll(d => d.Date >= expiry.Date);

		dates.Add(expiryOpen);
		dates.Add(expiryClose);
		return dates;
	}

	/// <summary>
	/// Generates price rows for the grid. Step size is derived from the smallest gap
	/// between strikes (or strike-to-break-even for single-strike positions), divided by
	/// the granularity parameter. Higher granularity = smaller steps = more rows.
	/// Always includes 2 padding rows beyond the outermost notable price.
	/// </summary>
	private static List<decimal> BuildPriceRows(decimal centerPrice, decimal granularity, List<decimal> breakEvens, List<decimal> strikes)
	{
		var notablePrices = breakEvens.Concat(strikes).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();

		// Find the reference gap to derive step size from.
		var distinctStrikes = strikes.Where(s => s > 0).Distinct().OrderBy(s => s).ToList();
		decimal referenceGap;
		if (distinctStrikes.Count >= 2)
		{
			referenceGap = decimal.MaxValue;
			for (int i = 1; i < distinctStrikes.Count; i++)
			{
				var gap = distinctStrikes[i] - distinctStrikes[i - 1];
				if (gap > 0 && gap < referenceGap) referenceGap = gap;
			}
		}
		else if (distinctStrikes.Count == 1 && breakEvens.Count > 0)
		{
			referenceGap = breakEvens.Where(b => b > 0).Select(b => Math.Abs(b - distinctStrikes[0])).Where(g => g > 0).DefaultIfEmpty(0).Min();
			if (referenceGap == 0) referenceGap = centerPrice * 0.01m;
		}
		else
		{
			referenceGap = centerPrice * 0.01m;
		}

		// Step = reference gap divided by granularity. Default granularity of 2 gives 2 rows per gap.
		var step = Math.Max(0.01m, referenceGap / granularity);

		// 2 padding rows beyond outermost notable prices, minimum 5 steps each side of center
		const int paddingRows = 2;
		var low = Math.Min(centerPrice - 5 * step, notablePrices[0] - paddingRows * step);
		var high = Math.Max(centerPrice + 5 * step, notablePrices[^1] + paddingRows * step);
		low = Math.Max(0.01m, low);

		var prices = new SortedSet<decimal>();
		for (var p = low; p <= high + step / 2; p += step)
			prices.Add(Math.Round(p, 2));

		foreach (var p in notablePrices)
			prices.Add(Math.Round(p, 2));

		return prices.Reverse().ToList();
	}

	/// <summary>
	/// Computes the spread's market mid-price from Yahoo bid/ask quotes.
	/// Returns null if any leg is missing bid or ask data.
	/// </summary>
	private static decimal? ComputeSpreadMarketMid(List<(PositionRow row, OptionParsed parsed, string symbol)> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		decimal total = 0;
		foreach (var leg in legs)
		{
			if (!quotes.TryGetValue(leg.symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
				return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.row.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	// --- Helpers ---

	private static (OptionParsed parsed, string symbol)? ParseOption(PositionRow row)
	{
		if (row.MatchKey == null || !MatchKeys.TryGetOptionSymbol(row.MatchKey, out var symbol))
			return null;
		var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
		return parsed == null ? null : (parsed, symbol);
	}

	private static decimal? GetLegIv(Side side, string symbol, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, decimal? ivLong, decimal? ivShort)
	{
		var cliIv = side == Side.Buy ? ivLong : ivShort;
		if (cliIv.HasValue) return cliIv.Value;
		if (optionQuotesBySymbol != null && optionQuotesBySymbol.TryGetValue(symbol, out var quote) && quote.ImpliedVolatility.HasValue && quote.ImpliedVolatility.Value > 0)
			return quote.ImpliedVolatility.Value;
		return null;
	}

	private static bool HasIvForRemainingTimeLegs(List<(PositionRow row, OptionParsed parsed, string symbol)> legs, DateTime evaluationExpiry, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, decimal? ivLong, decimal? ivShort)
	{
		if (ivLong.HasValue || ivShort.HasValue) return true;
		if (optionQuotesBySymbol == null) return false;

		// For time spreads we evaluate at the nearest expiry; only legs expiring after that retain time value.
		return legs
			.Where(l => l.parsed.ExpiryDate.Date > evaluationExpiry.Date)
			.Any(l => GetLegIv(l.row.Side, l.symbol, optionQuotesBySymbol, ivLong, ivShort).HasValue);
	}

	private static string? TryFormatYahooQuote(string symbol, IReadOnlyDictionary<string, OptionContractQuote>? optionQuotesBySymbol, decimal? ivOverride = null)
	{
		if (optionQuotesBySymbol == null) return null;
		if (!optionQuotesBySymbol.TryGetValue(symbol, out var quote)) return null;

		var parts = new List<string>();
		if (quote.LastPrice.HasValue) parts.Add($"Last ${Formatters.FormatPrice(quote.LastPrice.Value, Asset.Option)}");
		if (quote.Bid.HasValue) parts.Add($"Bid ${Formatters.FormatPrice(quote.Bid.Value, Asset.Option)}");
		if (quote.Ask.HasValue) parts.Add($"Ask ${Formatters.FormatPrice(quote.Ask.Value, Asset.Option)}");
		if (quote.Change.HasValue || quote.PercentChange.HasValue)
		{
			var chgText = quote.Change.HasValue ? quote.Change.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : "-";
			var pctText = quote.PercentChange.HasValue ? quote.PercentChange.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : "-";
			parts.Add($"Chg {chgText} ({pctText}%)");
		}
		if (quote.Volume.HasValue) parts.Add($"Vol {quote.Volume.Value.ToString("N0", CultureInfo.InvariantCulture)}");
		if (quote.OpenInterest.HasValue) parts.Add($"OI {quote.OpenInterest.Value.ToString("N0", CultureInfo.InvariantCulture)}");
		var yahooIv = quote.ImpliedVolatility;
		if (yahooIv.HasValue && ivOverride.HasValue)
			parts.Add($"IV ~{FormatIvPct(yahooIv.Value)}~ {FormatIvPct(ivOverride.Value)}");
		else if (yahooIv.HasValue)
			parts.Add($"IV {FormatIvPct(yahooIv.Value)}");
		else if (ivOverride.HasValue)
			parts.Add($"IV {FormatIvPct(ivOverride.Value)}");

		return parts.Count == 0 ? null : string.Join(" | ", parts);
	}

	private static string FormatIvPct(decimal iv) => $"{(iv * 100m).ToString("N1", CultureInfo.InvariantCulture)}%";

	private static decimal? LookupUnderlyingPrice(string root, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		if (underlyingPrices != null && underlyingPrices.TryGetValue(root, out var price))
			return Math.Round(price, 2);
		return null;
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
	/// Computes intrinsic value of an option at a given underlying price.
	/// </summary>
	private static decimal Intrinsic(decimal underlyingPrice, decimal strike, string callPut) => callPut == "C" ? Math.Max(0, underlyingPrice - strike) : Math.Max(0, strike - underlyingPrice);

	/// <summary>
	/// Calculates P&L for a single option leg at a given underlying price (at expiration, intrinsic only).
	/// </summary>
	private static decimal OptionPnLAtExpiration(decimal underlyingPrice, decimal strike, string callPut, Side side, int qty, decimal premium)
	{
		var pnlPerContract = side == Side.Buy ? Intrinsic(underlyingPrice, strike, callPut) - premium : premium - Intrinsic(underlyingPrice, strike, callPut);
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
	private static List<PricePnL> BuildPriceLadder(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt, Func<decimal, decimal, decimal?> valueAt)
	{
		ComputePriceRange(notablePrices, step, out var min, out var max);

		var prices = new SortedSet<decimal>();

		for (var p = min; p <= max + step / 2; p += step)
			prices.Add(Math.Round(p, 2));

		foreach (var p in notablePrices.Where(np => np >= 0))
			prices.Add(Math.Round(p, 2));

		return prices.Select(p =>
		{ 
			var pnl = Math.Round(pnlAt(p), 2); 
			return new PricePnL(p, pnl, valueAt(p, pnl)); 
		}).ToList();
	}

	/// <summary>
	/// Computes the price range [min, max] for a price ladder or chart.
	/// Expands the range until it spans at least 8 step-sized intervals.
	/// </summary>
	private static void ComputePriceRange(List<decimal> notablePrices, decimal step, out decimal min, out decimal max)
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

	/// <summary>
	/// Generates ~100 evenly-spaced data points for smooth chart rendering.
	/// Uses the same price range as the discrete ladder but with finer granularity.
	/// </summary>
	private static List<PricePnL> BuildChartData(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt, Func<decimal, decimal, decimal?> valueAt)
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
