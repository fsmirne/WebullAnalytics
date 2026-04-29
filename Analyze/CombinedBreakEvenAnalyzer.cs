using System.Globalization;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Produces an aggregated break-even panel per ticker that has 2+ open positions.
/// Legs shared across positions are merged (signed-qty netting, weighted-average price).
/// Max profit, max loss, and break-evens are evaluated at the earliest option expiry;
/// the time-decay grid spans to the latest expiry.
/// </summary>
public static class CombinedBreakEvenAnalyzer
{
	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, AnalysisOptions opts, decimal padding = 2, int maxGridColumns = 7, IReadOnlyList<BreakEvenResult>? individualResults = null)
		  => Analyze(positionRows, opts, padding, terminalWidth: TerminalHelper.DetailedMinWidth, displayMode: "pnl", showLegs: false, gridTableHasBorder: false, individualResults, forcedMaxGridColumns: maxGridColumns);

	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, AnalysisOptions opts, decimal padding, int terminalWidth, string displayMode, bool showLegs, bool gridTableHasBorder = false, IReadOnlyList<BreakEvenResult>? individualResults = null)
		  => Analyze(positionRows, opts, padding, terminalWidth, displayMode, showLegs, gridTableHasBorder, individualResults, forcedMaxGridColumns: null);

	private static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, AnalysisOptions opts, decimal padding, int terminalWidth, string displayMode, bool showLegs, bool gridTableHasBorder, IReadOnlyList<BreakEvenResult>? individualResults, int? forcedMaxGridColumns)
	{
		var units = GroupUnits(positionRows);
		var byTicker = new Dictionary<string, List<List<PositionRow>>>(StringComparer.Ordinal);
		foreach (var unit in units)
		{
			var ticker = GetUnitTicker(unit);
			if (ticker == null) continue;
			if (!byTicker.TryGetValue(ticker, out var list))
			{
				list = [];
				byTicker[ticker] = list;
			}
			list.Add(unit);
		}

		// Sum individual margins per ticker so the combined panel inherits correct per-position pairing.
		var marginByTicker = new Dictionary<string, decimal>(StringComparer.Ordinal);
		if (individualResults != null)
		{
			foreach (var r in individualResults)
			{
				if (!r.Margin.HasValue) continue;
				var sp = r.Title.IndexOf(' ');
				if (sp <= 0) continue;
				var t = r.Title[..sp];
				marginByTicker[t] = (marginByTicker.TryGetValue(t, out var existing) ? existing : 0m) + r.Margin.Value;
			}
		}

		var results = new List<BreakEvenResult>();
		foreach (var ticker in byTicker.Keys.OrderBy(k => k, StringComparer.Ordinal))
		{
			var unitsForTicker = byTicker[ticker];
			if (unitsForTicker.Count < 2) continue;

			var allRows = unitsForTicker.SelectMany(u => u).ToList();
			var merged = LegMerger.Merge(allRows);
			if (merged.Count == 0) continue; // fully offsetting portfolio

			var tickerMargin = marginByTicker.TryGetValue(ticker, out var m) ? m : (decimal?)null;
			var result = BuildResult(ticker, unitsForTicker.Count, merged, opts, padding, terminalWidth, displayMode, showLegs, gridTableHasBorder, tickerMargin, forcedMaxGridColumns);
			if (result != null) results.Add(result);
		}
		return results;
	}

	/// <summary>
	/// Derives the ticker for a unit. Strategy parent rows are constructed without a
	/// MatchKey, so we scan every row in the unit and take the first parseable MatchKey.
	/// </summary>
	private static string? GetUnitTicker(List<PositionRow> unit)
	{
		foreach (var row in unit)
		{
			if (row.MatchKey == null) continue;
			var ticker = MatchKeys.GetTicker(row.MatchKey);
			if (ticker != null) return ticker;
		}
		return null;
	}

	/// <summary>
	/// Groups rows into units the same way <see cref="BreakEvenAnalyzer"/> does:
	/// each non-leg row starts a new unit; subsequent IsStrategyLeg rows attach to it.
	/// </summary>
	private static List<List<PositionRow>> GroupUnits(List<PositionRow> rows)
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

	private static BreakEvenResult? BuildResult(string ticker, int unitCount, List<MergedLeg> merged, AnalysisOptions opts, decimal padding, int terminalWidth, string displayMode, bool showLegs, bool gridTableHasBorder, decimal? margin, int? forcedMaxGridColumns)
	{
		var optionLegs = merged.Where(l => !l.IsStock).ToList();
		var stockLeg = merged.FirstOrDefault(l => l.IsStock);

		// Normalizing qty keeps the grid "value" column on the same per-pair scale as the adj basis
		// displayed in the details line: balanced / partly balanced portfolios quote per matched pair;
		// single-sided portfolios quote per weighted contract.
		var longQty = optionLegs.Where(l => l.Side == Side.Buy).Sum(l => l.Qty);
		var shortQty = optionLegs.Where(l => l.Side == Side.Sell).Sum(l => l.Qty);
		var pairQty = Math.Min(longQty, shortQty);
		var totalQty = optionLegs.Sum(l => l.Qty);
		var normalizingQty = pairQty > 0 ? pairQty : totalQty;

       // Per-pair net premium: Σ signed × (legQty / normalizingQty) × legPrice.
		// The grid cell uses (displayValue − netPremium) × normalizingQty × 100 to derive pnl,
		// so colors stay consistent with the rounded display value.
		decimal netPremium = 0m;
		if (normalizingQty > 0)
		{
			foreach (var leg in optionLegs)
			{
				var signed = leg.Side == Side.Buy ? 1 : -1;
				var weight = (decimal)leg.Qty / normalizingQty;
				netPremium += signed * weight * leg.Price;
			}
		}

		var title = BuildTitle(ticker, merged);

		DateTime? nearestExpiry = optionLegs.Count > 0 ? optionLegs.Min(l => l.Parsed!.ExpiryDate) : null;
		var hasMixedExpiries = optionLegs.Count > 0 && optionLegs.Select(l => l.Parsed!.ExpiryDate.Date).Distinct().Count() > 1;

        // Net adj basis across all merged option legs: Σ signed × qty × price × 100.
		// Used for per-share/per-spread quoting; sign is absorbed into the absolute value.
		decimal totalAdjDollars = 0m;
		foreach (var leg in optionLegs)
		{
			var signed = leg.Side == Side.Buy ? 1 : -1;
			totalAdjDollars += signed * leg.Qty * leg.Price * 100m;
		}

		string details;
		if (optionLegs.Count > 0)
		{
			string qtyPart;
			if (pairQty > 0)
			{
				var perShare = Math.Abs(totalAdjDollars) / (pairQty * 100m);
				qtyPart = $"{pairQty}x @ ${Formatters.FormatPrice(perShare, Asset.Option)} adj";
			}
			else
			{
				var perShare = totalQty > 0 ? Math.Abs(totalAdjDollars) / (totalQty * 100m) : 0m;
				qtyPart = $"@ ${Formatters.FormatPrice(perShare, Asset.Option)} adj";
			}
			details = $"{qtyPart}, Exp {Formatters.FormatOptionDate(nearestExpiry!.Value)}";
		}
		else
		{
			details = $"{unitCount} positions";
		}

		int? daysToExpiry = nearestExpiry.HasValue ? (int)(nearestExpiry.Value.Date - EvaluationDate.Today).TotalDays : null;

		var legDescriptions = BuildLegDescriptions(merged, opts);

		decimal StockDollarPnL(decimal s)
		{
			if (stockLeg == null) return 0m;
			var signed = stockLeg.Side == Side.Buy ? 1 : -1;
			return signed * stockLeg.Qty * (s - stockLeg.Price);
		}

		var spot = LookupUnderlyingPrice(ticker, opts);
		var strikes = optionLegs.Select(l => l.Parsed!.Strike).Distinct().OrderBy(x => x).ToList();
		var notablePrices = new List<decimal>(strikes);
		if (spot.HasValue) notablePrices.Add(spot.Value);
		notablePrices.AddRange(LookupExtraNotablePrices(ticker, opts));
		if (notablePrices.Count == 0 && stockLeg != null) notablePrices.Add(stockLeg.Price);
		if (notablePrices.Count == 0) return null;

		var centerPrice = strikes.Count > 0 ? strikes.Average() : (spot ?? stockLeg!.Price);
		var step = OptionMath.GetPriceStep(centerPrice);

		string? note = null;
		bool canComputeBsAtNearest = nearestExpiry == null || HasIvForUnexpiredLegs(optionLegs, nearestExpiry.Value, opts);
		if (optionLegs.Count > 0 && !canComputeBsAtNearest && hasMixedExpiries)
		{
			note = "Break-even analysis requires implied volatility. Enable option-chain lookup with --api yahoo (or --api webull), or use the interactive IV override after the report renders.";
			return new BreakEvenResult(
				title, details, Qty: 1, BreakEvens: [], MaxProfit: null, MaxLoss: null,
				DaysToExpiry: daysToExpiry, PriceLadder: [], Note: note,
				Legs: legDescriptions,
				UnderlyingPrice: spot,
				OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(ticker, opts)
			);
		}

		DateTime pnlEvalDate = nearestExpiry.HasValue ? nearestExpiry.Value.Date + OptionMath.MarketClose : EvaluationDate.Today;
		Func<decimal, decimal> pnlFunc = s =>
			OptionMath.StrategyPnLWithBsMixed(s, merged, pnlEvalDate, opts) + StockDollarPnL(s);
		Func<decimal, decimal, decimal?> valueAt = (s, pnl) => null;

		var ladder = OptionMath.BuildPriceLadder(notablePrices, step, pnlFunc, valueAt);

		var breakEvens = OptionMath.FindBreakEvensNumerically(ladder, pnlFunc);
		foreach (var be in breakEvens)
		{
			if (!ladder.Any(p => Math.Abs(p.UnderlyingPrice - be) < 0.005m))
				ladder.Add(new PricePnL(be, 0m, null));
		}
		ladder.Sort((a, b) => a.UnderlyingPrice.CompareTo(b.UnderlyingPrice));

		decimal? maxProfit = ladder.Max(p => p.PnL);
		decimal? maxLoss = Math.Abs(ladder.Min(p => p.PnL));

		ladder.Reverse();
		var chartData = OptionMath.BuildChartData(notablePrices, step, pnlFunc, valueAt);

		TimeDecayGrid? grid = null;
		if (optionLegs.Count > 0 && nearestExpiry.HasValue && canComputeBsAtNearest)
		{
			var gridNotable = new List<decimal>(breakEvens);
			if (spot.HasValue) gridNotable.Add(spot.Value);
			gridNotable.AddRange(LookupExtraNotablePrices(ticker, opts));
			var build = (int maxCols) => TimeDecayGridBuilder.Build(merged, netPremium, normalizingQty, nearestExpiry.Value, opts, padding, centerPrice, gridNotable, maxCols, spot);
			if (forcedMaxGridColumns.HasValue)
			{
				grid = build(forcedMaxGridColumns.Value);
			}
			else
			{
				var maxLegWidth = optionLegs
					.Select(l => l.Price.ToString("N2", CultureInfo.InvariantCulture).Length)
					.DefaultIfEmpty(0)
					.Max();
				var initialMax = TableBuilder.ComputeMaxGridColumns(terminalWidth, displayMode, showLegs, maxLegCount: optionLegs.Count, maxLegValueWidth: maxLegWidth, gridTableOuterBorders: gridTableHasBorder ? 2 : 0);
				grid = BuildFittedGrid(build, initialMax, terminalWidth, displayMode, showLegs, gridTableHasBorder ? 2 : 0);
			}

			if (stockLeg != null)
			{
				for (int pi = 0; pi < grid.PriceRows.Count; pi++)
				{
					var cellPrice = grid.PriceRows[pi];
					var stockPnL = StockDollarPnL(cellPrice);
					for (int di = 0; di < grid.DateColumns.Count; di++)
						grid.PnLs[pi, di] = Math.Round(grid.PnLs[pi, di] + stockPnL, 2);
				}
			}
		}
		else if (hasMixedExpiries && nearestExpiry.HasValue)
		{
			note = "P&L estimated using Black-Scholes at earliest-expiry date. Actual results will vary with volatility.";
		}

		return new BreakEvenResult(
			Title: title,
			Details: details,
			Qty: 1,
			BreakEvens: breakEvens,
			MaxProfit: maxProfit,
			MaxLoss: maxLoss,
			DaysToExpiry: daysToExpiry,
			PriceLadder: ladder,
			Note: note,
			Legs: legDescriptions,
			ChartData: chartData,
			EarlyExercise: null,
			Grid: grid,
			UnderlyingPrice: spot,
			OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(ticker, opts),
			Margin: margin
		);
	}

	private static TimeDecayGrid BuildFittedGrid(Func<int, TimeDecayGrid> buildGrid, int initialMaxColumns, int terminalWidth, string displayMode, bool showLegs, int gridTableOuterBorders)
	{
		var maxColumns = Math.Max(3, initialMaxColumns);
		var grid = buildGrid(maxColumns);
		var required = TableBuilder.ComputeTimeDecayGridRequiredWidth(grid, displayMode, showLegs, gridTableOuterBorders);

		while (required > terminalWidth && maxColumns > 3)
		{
			maxColumns--;
			grid = buildGrid(maxColumns);
			required = TableBuilder.ComputeTimeDecayGridRequiredWidth(grid, displayMode, showLegs, gridTableOuterBorders);
		}

		for (int i = 0; i < 5; i++)
		{
			var tryMax = maxColumns + 1;
			var expanded = buildGrid(tryMax);
			if (expanded.DateColumns.Count <= grid.DateColumns.Count)
				break;

			var expandedRequired = TableBuilder.ComputeTimeDecayGridRequiredWidth(expanded, displayMode, showLegs, gridTableOuterBorders);
			if (expandedRequired > terminalWidth)
				break;

			grid = expanded;
			maxColumns = tryMax;
		}

		return grid;
	}

	private static string BuildTitle(string ticker, List<MergedLeg> merged)
	{
		var symbols = new List<string>();
		foreach (var leg in merged)
		{
			if (leg.IsStock) symbols.Add(ticker);
			else symbols.Add(leg.Symbol);
		}
      return $"{ticker} Combined — [{string.Join(", ", symbols)}]";
	}

	private static List<string> BuildLegDescriptions(List<MergedLeg> merged, AnalysisOptions opts)
	{
		var legs = new List<string>();
		foreach (var leg in merged)
		{
			if (leg.IsStock)
			{
				var sideWord = leg.Side == Side.Buy ? "Long" : "Short";
              var line = $"Stock — {sideWord} {leg.Qty} sh @ ${leg.Price.ToString("N2", CultureInfo.InvariantCulture)}";
				legs.Add(line);
				continue;
			}

			var longShort = leg.Side == Side.Buy ? "Long" : "Short";
			var cpDisplay = ParsingHelpers.CallPutDisplayName(leg.Parsed!.CallPut);
         var desc = $"{longShort} {leg.Qty}× {cpDisplay} ${Formatters.FormatQty(leg.Parsed.Strike)} @ ${Formatters.FormatPrice(leg.Price, Asset.Option)}, Exp {Formatters.FormatOptionDate(leg.Parsed.ExpiryDate)}";

			var yahooInfo = TryFormatYahooQuote(leg.Symbol, opts);
			if (yahooInfo != null) desc += $" | {yahooInfo}";

			if (leg.SourcePositionCount > 1)
				desc += $" (merged from {leg.SourcePositionCount} positions)";

			legs.Add(desc);
		}
		return legs;
	}

	private static bool HasIvForUnexpiredLegs(List<MergedLeg> optionLegs, DateTime evaluationExpiry, AnalysisOptions opts)
	{
		if (opts.OptionQuotes == null && opts.IvOverrides == null) return false;

		var unexpired = optionLegs.Where(l => l.Parsed!.ExpiryDate.Date > evaluationExpiry.Date).ToList();
		if (unexpired.Count == 0) return true;

		if (opts.IvOverrides != null && unexpired.All(l => opts.IvOverrides.ContainsKey(l.Symbol))) return true;
		if (opts.OptionQuotes == null) return false;
		return unexpired.All(l => OptionMath.GetLegIv(l.Side, l.Symbol, opts).HasValue);
	}

	private static decimal? LookupUnderlyingPrice(string ticker, AnalysisOptions opts)
	{
		if (opts.UnderlyingPriceOverrides != null && opts.UnderlyingPriceOverrides.TryGetValue(ticker, out var overridePrice))
			return Math.Round(overridePrice, 2);
		if (opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(ticker, out var price))
			return Math.Round(price, 2);
		return null;
	}

	private static decimal? LookupOriginalUnderlyingPrice(string ticker, AnalysisOptions opts)
	{
		if (opts.UnderlyingPriceOverrides == null || !opts.UnderlyingPriceOverrides.ContainsKey(ticker)) return null;
		if (opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(ticker, out var price))
			return Math.Round(price, 2);
		return null;
	}

	private static List<decimal> LookupExtraNotablePrices(string ticker, AnalysisOptions opts)
	{
		if (opts.ExtraNotablePrices != null && opts.ExtraNotablePrices.TryGetValue(ticker, out var prices))
			return prices;
		return [];
	}

	private static string? TryFormatYahooQuote(string symbol, AnalysisOptions opts)
	{
		if (opts.OptionQuotes == null) return null;
		if (!opts.OptionQuotes.TryGetValue(symbol, out var quote)) return null;

		var parts = new List<string>();
		if (quote.LastPrice.HasValue) parts.Add($"Last ${Formatters.FormatPrice(quote.LastPrice.Value, Asset.Option)}");
		if (quote.Bid.HasValue) parts.Add($"Bid ${Formatters.FormatPrice(quote.Bid.Value, Asset.Option)}");
		if (quote.Ask.HasValue) parts.Add($"Ask ${Formatters.FormatPrice(quote.Ask.Value, Asset.Option)}");
		if (quote.ImpliedVolatility.HasValue)
			parts.Add($"IV {(quote.ImpliedVolatility.Value * 100m).ToString("N1", CultureInfo.InvariantCulture)}%");
		if (quote.HistoricalVolatility.HasValue)
			parts.Add($"HV {(quote.HistoricalVolatility.Value * 100m).ToString("N1", CultureInfo.InvariantCulture)}%");
		return parts.Count == 0 ? null : string.Join(" | ", parts);
	}
}
