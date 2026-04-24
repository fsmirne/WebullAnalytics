using System.Globalization;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Analyzes open positions to calculate break-even prices, max profit/loss,
/// and at-expiration P&L across a price ladder.
/// When implied volatility is provided, uses Black-Scholes pricing for
/// calendar/diagonal spreads to account for the long leg's remaining time value.
/// </summary>
public static class BreakEvenAnalyzer
{
	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, AnalysisOptions opts, decimal padding = 2, int maxGridColumns = 7)
	{
		var groups = GroupPositions(positionRows);
		var results = new List<BreakEvenResult>();
		foreach (var group in groups)
		{
			var result = AnalyzeGroup(group, opts, padding, maxGridColumns);
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

	private static BreakEvenResult? AnalyzeGroup(List<PositionRow> group, AnalysisOptions opts, decimal padding, int maxGridColumns)
	{
		var parent = group[0];

		if (parent.Asset == Asset.Stock)
			return AnalyzeStock(parent, opts);

		if (parent.Asset == Asset.Option)
			return AnalyzeSingleOption(parent, opts, padding, maxGridColumns);

		if (parent.Asset == Asset.OptionStrategy && group.Count > 1)
			return AnalyzeStrategy(parent, group.Skip(1).ToList(), opts, padding, maxGridColumns);

		return null;
	}

	private static BreakEvenResult AnalyzeStock(PositionRow row, AnalysisOptions opts)
	{
		var avgPrice = row.AvgPrice;
		var isLong = row.Side == Side.Buy;
		var title = $"{row.Instrument} {(isLong ? "Long" : "Short")}";
		var details = $"{row.Qty}x @ ${avgPrice.ToString("N2", CultureInfo.InvariantCulture)}";

		var step = OptionMath.GetPriceStep(avgPrice);
		var notablePrices = new List<decimal> { avgPrice };
		notablePrices.AddRange(LookupExtraNotablePrices(row.Instrument, opts));
		Func<decimal, decimal> pnlFunc = s => isLong ? (s - avgPrice) * row.Qty : (avgPrice - s) * row.Qty;
		var ladder = OptionMath.BuildPriceLadder(notablePrices, step, pnlFunc, (s, pnl) => null);
		ladder.Reverse();
		var chartData = OptionMath.BuildChartData(notablePrices, step, pnlFunc, (s, pnl) => null);

		return new BreakEvenResult(Title: title, Details: details, Qty: row.Qty, BreakEvens: [avgPrice], MaxProfit: isLong ? null : avgPrice * row.Qty, MaxLoss: isLong ? avgPrice * row.Qty : null, DaysToExpiry: null, PriceLadder: ladder, Note: null, ChartData: chartData);
	}

	private static BreakEvenResult? AnalyzeSingleOption(PositionRow row, AnalysisOptions opts, decimal padding, int maxGridColumns)
	{
		var parsedInfo = ParseOption(row);
		if (parsedInfo == null) return null;
		var (parsed, symbol) = parsedInfo.Value;

		var premium = OptionMath.GetPremium(row);
		var isLong = row.Side == Side.Buy;
		var isCall = parsed.CallPut == "C";
		var strike = parsed.Strike;
		var qty = row.Qty;

		var title = $"{parsed.Root} {(isLong ? "Long" : "Short")} {ParsingHelpers.CallPutDisplayName(parsed.CallPut)} ${Formatters.FormatQty(strike)} [{symbol}]";
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

		var spot = LookupUnderlyingPrice(parsed.Root, opts);
		var notablePrices = new List<decimal> { strike, breakEven };
		if (spot.HasValue) notablePrices.Add(spot.Value);
		notablePrices.AddRange(LookupExtraNotablePrices(parsed.Root, opts));
		var step = OptionMath.GetPriceStep(strike);
		Func<decimal, decimal> pnlFunc = s => OptionMath.OptionPnLAtExpiration(s, strike, parsed.CallPut, row.Side, qty, premium);
		Func<decimal, decimal, decimal?> valueAt = (s, pnl) => isLong ? (pnl / (qty * 100m)) + premium : premium - (pnl / (qty * 100m));
		var ladder = OptionMath.BuildPriceLadder(notablePrices, step, pnlFunc, valueAt);
		ladder.Reverse();
		var chartData = OptionMath.BuildChartData(notablePrices, step, pnlFunc, valueAt);

		var dte = row.Expiry.HasValue ? (int)(row.Expiry.Value.Date - EvaluationDate.Today).TotalDays : (int?)null;

		EarlyExerciseBoundary? earlyExercise = null;
		var iv = OptionMath.GetLegIv(row.Side, symbol, opts);
		if (isLong && iv.HasValue && dte.HasValue && dte.Value >= 0)
		{
			var timeYears = dte.Value / 365.0;
			earlyExercise = BjerksundStensland.ComputeExerciseBoundary(strike, timeYears, OptionMath.RiskFreeRate, (double)iv.Value, parsed.CallPut);
		}

		TimeDecayGrid? grid = null;
		if (iv.HasValue && dte.HasValue && dte.Value >= 0)
		{
			var legsList = new List<(PositionRow row, OptionParsed parsed, string symbol)> { (row, parsed, symbol) };
			var gridBreakEvens = new List<decimal> { breakEven };
			if (spot.HasValue) gridBreakEvens.Add(spot.Value);
			gridBreakEvens.AddRange(LookupExtraNotablePrices(parsed.Root, opts));
			grid = TimeDecayGridBuilder.Build(legsList, qty, row.Side, premium, parsed.ExpiryDate, opts, padding, strike, gridBreakEvens, maxGridColumns, spot);
		}

		List<string>? legsDisplay = null;
		var ivOverride = opts.IvOverrides != null && opts.IvOverrides.TryGetValue(symbol, out var ov) ? ov : (decimal?)null;
		var yahooInfo = TryFormatYahooQuote(symbol, opts, ivOverride);
		if (yahooInfo != null)
			legsDisplay = [$"Market: {yahooInfo}"];

		return new BreakEvenResult(title, details, qty, [breakEven], maxProfit, maxLoss, dte, ladder, Note: null, Legs: legsDisplay, ChartData: chartData, EarlyExercise: earlyExercise, Grid: grid, UnderlyingPrice: spot, OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(parsed.Root, opts));
	}

	private static BreakEvenResult? AnalyzeStrategy(PositionRow parent, List<PositionRow> legs, AnalysisOptions opts, decimal padding, int maxGridColumns)
	{
		var parsedLegs = legs.Select(l => (row: l, parsed: ParseOption(l))).Where(x => x.parsed != null).Select(x => (x.row, x.parsed!.Value.parsed, x.parsed!.Value.symbol)).ToList();
		if (parsedLegs.Count < 2) return null;

		var root = parsedLegs[0].parsed.Root;
		var callPut = parsedLegs[0].parsed.CallPut;
		var callPutDisplay = ParsingHelpers.CallPutDisplayName(callPut);
		var strategyKind = parent.OptionKind;
		var qty = parent.Qty;
		var netPremium = OptionMath.GetPremium(parent);

		var strikes = parsedLegs.Select(l => l.parsed.Strike).Distinct().OrderBy(x => x).ToList();
		var expiries = parsedLegs.Select(l => l.parsed.ExpiryDate).Distinct().OrderBy(x => x).ToList();
		var earliestExpiry = expiries[0];

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

		var legSymbols = string.Join(", ", parsedLegs.Select(l => l.symbol));
		title += $" [{legSymbols}]";

		var details = BuildDetailsString(parent, earliestExpiry);

		// Build leg descriptions
		var legDescriptions = parsedLegs.Select(l =>
		{
			var longShort = l.row.Side == Side.Buy ? "Long" : "Short";
			var cpDisplay = ParsingHelpers.CallPutDisplayName(l.parsed.CallPut);
			var legPremium = OptionMath.GetPremium(l.row);
			var desc = $"{longShort} {cpDisplay} ${Formatters.FormatQty(l.parsed.Strike)} @ ${Formatters.FormatPrice(legPremium, Asset.Option)}, Exp {Formatters.FormatOptionDate(l.parsed.ExpiryDate)}";

			var legIv = OptionMath.GetLegIv(l.row.Side, l.symbol, opts);
			var legIvOverride = opts.IvOverrides != null && opts.IvOverrides.TryGetValue(l.symbol, out var legOv) ? legOv : (decimal?)null;
			var yahooInfo = TryFormatYahooQuote(l.symbol, opts, legIvOverride);
			if (yahooInfo != null)
				desc += $" | {yahooInfo}";

			if (l.row.Side == Side.Buy && legIv.HasValue)
			{
				var legDte = (l.parsed.ExpiryDate.Date - EvaluationDate.Today).TotalDays;
				if (legDte > 0)
				{
					var boundary = BjerksundStensland.ComputeExerciseBoundary(l.parsed.Strike, legDte / 365.0, OptionMath.RiskFreeRate, (double)legIv.Value, l.parsed.CallPut);
					if (boundary != null)
						desc += $" | Exercise below ${boundary.BoundaryNear.ToString("N2", CultureInfo.InvariantCulture)}";
				}
			}
			return desc;
		}).ToList();

		var isTimeSpread = expiries.Count > 1;
		var nearestExpiry = expiries[0];
		var dte = (int)(nearestExpiry.Date - EvaluationDate.Today).TotalDays;

		var breakEvens = new List<decimal>();
		decimal? maxProfit = null;
		decimal? maxLoss = null;
		string? note = null;

		if (strategyKind == "Vertical" && strikes.Count == 2)
		{
			var lowK = strikes[0];
			var highK = strikes[1];
			breakEvens.Add(callPut == "C" ? lowK + netPremium : highK - netPremium);
		}
		else if (isTimeSpread && !HasIvForRemainingTimeLegs(parsedLegs, nearestExpiry, opts))
		{
			note = "Break-even analysis requires implied volatility. Enable option-chain lookup with --api yahoo (or --api webull), or use the interactive IV override after the report renders.";
			return new BreakEvenResult(title, details, qty, [], null, null, dte, [], note, legDescriptions, UnderlyingPrice: LookupUnderlyingPrice(root, opts), OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(root, opts));
		}
		else if (isTimeSpread)
		{
			note = "P&L estimated using Black-Scholes at short leg expiry. Actual results will vary with volatility.";
			if (parent.Side == Side.Buy)
				maxLoss = netPremium * qty * 100;
		}

		// Build price ladder
		var spot = LookupUnderlyingPrice(root, opts);
		var notablePrices = new List<decimal>(strikes);
		notablePrices.AddRange(breakEvens);
		if (spot.HasValue) notablePrices.Add(spot.Value);
		notablePrices.AddRange(LookupExtraNotablePrices(root, opts));
		var step = OptionMath.GetPriceStep(strikes.Average());

		Func<decimal, decimal> pnlFunc;
		if (isTimeSpread)
			pnlFunc = s => OptionMath.StrategyPnLWithBs(s, parsedLegs, qty, nearestExpiry.Date + OptionMath.MarketClose, opts);
		else
			pnlFunc = s => OptionMath.StrategyPnLAtExpiration(s, parsedLegs, qty);

		Func<decimal, decimal, decimal?> valueAt = (s, pnl) => parent.Side == Side.Buy ? (pnl / (qty * 100m)) + netPremium : netPremium - (pnl / (qty * 100m));
		var ladder = OptionMath.BuildPriceLadder(notablePrices, step, pnlFunc, valueAt);

		// Find break-evens numerically if not already set analytically
		if (breakEvens.Count == 0)
			breakEvens = OptionMath.FindBreakEvensNumerically(ladder, pnlFunc);

		// Insert numerically-found break-even prices into the ladder
		foreach (var be in breakEvens)
		{
			if (!ladder.Any(p => Math.Abs(p.UnderlyingPrice - be) < 0.005m))
				ladder.Add(new PricePnL(be, 0m, valueAt(be, 0m)));
		}
		ladder.Sort((a, b) => a.UnderlyingPrice.CompareTo(b.UnderlyingPrice));

		if (!maxProfit.HasValue)
			maxProfit = ladder.Max(p => p.PnL);

		if (!maxLoss.HasValue)
			maxLoss = Math.Abs(ladder.Min(p => p.PnL));

		ladder.Reverse();
		var chartData = OptionMath.BuildChartData(notablePrices, step, pnlFunc, valueAt);

		TimeDecayGrid? grid = null;
		if (dte >= 0 && HasIvForRemainingTimeLegs(parsedLegs, nearestExpiry, opts))
		{
			var gridNotable = new List<decimal>(breakEvens);
			if (spot.HasValue) gridNotable.Add(spot.Value);
			gridNotable.AddRange(LookupExtraNotablePrices(root, opts));
			grid = TimeDecayGridBuilder.Build(parsedLegs, qty, parent.Side, netPremium, nearestExpiry, opts, padding, strikes.Average(), gridNotable, maxGridColumns, spot);
		}

		return new BreakEvenResult(title, details, qty, breakEvens, maxProfit, maxLoss, dte, ladder, note, legDescriptions, chartData, Grid: grid, UnderlyingPrice: spot, OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(root, opts));
	}

	// --- Helpers ---

	private static (OptionParsed parsed, string symbol)? ParseOption(PositionRow row)
		=> row.MatchKey != null ? MatchKeys.ParseOption(row.MatchKey) : null;

	private static bool HasIvForRemainingTimeLegs(List<(PositionRow row, OptionParsed parsed, string symbol)> legs, DateTime evaluationExpiry, AnalysisOptions opts)
	{
		if (opts.IvOverrides != null && legs.Any(l => opts.IvOverrides.ContainsKey(l.symbol))) return true;
		if (opts.OptionQuotes == null) return false;

		var isTimeSpread = legs.Select(l => l.parsed.ExpiryDate.Date).Distinct().Count() > 1;
		if (isTimeSpread)
			return legs.Where(l => l.parsed.ExpiryDate.Date > evaluationExpiry.Date).Any(l => OptionMath.GetLegIv(l.row.Side, l.symbol, opts).HasValue);

		return legs.Any(l => OptionMath.GetLegIv(l.row.Side, l.symbol, opts).HasValue);
	}

	private static string? TryFormatYahooQuote(string symbol, AnalysisOptions opts, decimal? ivOverride = null)
	{
		if (opts.OptionQuotes == null) return null;
		if (!opts.OptionQuotes.TryGetValue(symbol, out var quote)) return null;

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
		var volParts = new List<string>();
		string? ivColor = null;
		var effectiveIv = ivOverride ?? yahooIv;
		if (effectiveIv.HasValue && quote.HistoricalVolatility.HasValue && quote.HistoricalVolatility.Value != 0)
		{
			var vrp = effectiveIv.Value / quote.HistoricalVolatility.Value;
			if (vrp < 0.90m) ivColor = "cheap";
			else if (vrp > 1.10m) ivColor = "rich";
		}
		if (yahooIv.HasValue && ivOverride.HasValue)
			volParts.Add($"~{FormatIvPct(yahooIv.Value)}~ {FormatIvVal(ivOverride.Value, ivColor)}");
		else if (yahooIv.HasValue)
			volParts.Add(FormatIvVal(yahooIv.Value, ivColor));
		else if (ivOverride.HasValue)
			volParts.Add(FormatIvVal(ivOverride.Value, ivColor));
		if (quote.HistoricalVolatility.HasValue)
			volParts.Add($"HV {FormatIvPct(quote.HistoricalVolatility.Value)}");
		if (quote.ImpliedVolatility5Day.HasValue)
			volParts.Add($"IV5 {FormatIvPct(quote.ImpliedVolatility5Day.Value)}");
		if (volParts.Count > 0)
			parts.Add($"IV {string.Join(" | ", volParts)}");

		return parts.Count == 0 ? null : string.Join(" | ", parts);
	}

	private static string FormatIvPct(decimal iv) => $"{(iv * 100m).ToString("N1", CultureInfo.InvariantCulture)}%";

	private static string FormatIvVal(decimal iv, string? color) => color != null ? $"{{{color}}}{FormatIvPct(iv)}{{/{color}}}" : FormatIvPct(iv);

	private static decimal? LookupUnderlyingPrice(string root, AnalysisOptions opts)
	{
		if (opts.UnderlyingPriceOverrides != null && opts.UnderlyingPriceOverrides.TryGetValue(root, out var overridePrice))
			return Math.Round(overridePrice, 2);
		if (opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(root, out var price))
			return Math.Round(price, 2);
		return null;
	}

	private static decimal? LookupOriginalUnderlyingPrice(string root, AnalysisOptions opts)
	{
		if (opts.UnderlyingPriceOverrides == null || !opts.UnderlyingPriceOverrides.ContainsKey(root)) return null;
		if (opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(root, out var price))
			return Math.Round(price, 2);
		return null;
	}

	private static List<decimal> LookupExtraNotablePrices(string ticker, AnalysisOptions opts)
	{
		if (opts.ExtraNotablePrices != null && opts.ExtraNotablePrices.TryGetValue(ticker, out var prices))
			return prices;
		return [];
	}

	private static string BuildDetailsString(PositionRow row, DateTime? expiryOverride = null)
	{
		var premium = OptionMath.GetPremium(row);
		var premiumStr = Formatters.FormatPrice(premium, row.Asset);
		var hasAdjustment = row.AdjustedAvgPrice.HasValue && row.AdjustedAvgPrice.Value != (row.InitialAvgPrice ?? row.AvgPrice);
		var adjSuffix = hasAdjustment ? " adj" : "";
		var expiry = expiryOverride ?? row.Expiry;
		var expiryStr = expiry.HasValue ? Formatters.FormatOptionDate(expiry.Value) : "N/A";
		return $"{row.Qty}x @ ${premiumStr}{adjSuffix}, Exp {expiryStr}";
	}
}
