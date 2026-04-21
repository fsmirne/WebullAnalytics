using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// Produces an aggregated break-even panel per ticker that has 2+ open positions.
/// Legs shared across positions are merged (signed-qty netting, weighted-average price).
/// Max profit, max loss, and break-evens are evaluated at the earliest option expiry;
/// the time-decay grid spans to the latest expiry.
/// </summary>
public static class CombinedBreakEvenAnalyzer
{
	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, AnalysisOptions opts, decimal padding = 2, int maxGridColumns = 7)
	{
		var units = GroupUnits(positionRows);
		var byTicker = new Dictionary<string, List<List<PositionRow>>>(StringComparer.Ordinal);
		foreach (var unit in units)
		{
			var parent = unit[0];
			if (parent.MatchKey == null) continue;
			var ticker = MatchKeys.GetTicker(parent.MatchKey);
			if (ticker == null) continue;
			if (!byTicker.TryGetValue(ticker, out var list))
			{
				list = [];
				byTicker[ticker] = list;
			}
			list.Add(unit);
		}

		var results = new List<BreakEvenResult>();
		foreach (var ticker in byTicker.Keys.OrderBy(k => k, StringComparer.Ordinal))
		{
			var unitsForTicker = byTicker[ticker];
			if (unitsForTicker.Count < 2) continue;

			var allRows = unitsForTicker.SelectMany(u => u).ToList();
			var merged = LegMerger.Merge(allRows);
			if (merged.Count == 0) continue; // fully offsetting portfolio

			var result = BuildResult(ticker, unitsForTicker.Count, merged, opts, padding, maxGridColumns);
			if (result != null) results.Add(result);
		}
		return results;
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

	private static BreakEvenResult? BuildResult(string ticker, int unitCount, List<MergedLeg> merged, AnalysisOptions opts, decimal padding, int maxGridColumns)
	{
		var optionLegs = merged.Where(l => !l.IsStock).ToList();
		var stockLeg = merged.FirstOrDefault(l => l.IsStock);

		// Net premium in the synthetic qty=1 frame: Σ(signedQty × price) across option legs.
		decimal netPremium = 0m;
		foreach (var leg in optionLegs)
		{
			var signed = leg.Side == Side.Buy ? 1 : -1;
			netPremium += signed * leg.Qty * leg.Price;
		}

		var title = BuildTitle(ticker, merged);

		DateTime? nearestExpiry = optionLegs.Count > 0 ? optionLegs.Min(l => l.Parsed!.ExpiryDate) : null;
		DateTime? latestExpiry = optionLegs.Count > 0 ? optionLegs.Max(l => l.Parsed!.ExpiryDate) : null;
		var hasMixedExpiries = optionLegs.Count > 0 && optionLegs.Select(l => l.Parsed!.ExpiryDate.Date).Distinct().Count() > 1;

		var detailsParts = new List<string> { $"{unitCount} positions" };
		if (nearestExpiry.HasValue)
		{
			detailsParts.Add($"Earliest expiry {Formatters.FormatOptionDate(nearestExpiry.Value)}");
			var dte = (int)(nearestExpiry.Value.Date - EvaluationDate.Today).TotalDays;
			detailsParts.Add($"DTE {dte}");
		}
		if (hasMixedExpiries) detailsParts.Add("mixed expiries");
		var details = string.Join(" · ", detailsParts);

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
		if (optionLegs.Count > 0 && latestExpiry.HasValue && canComputeBsAtNearest)
		{
			var gridNotable = new List<decimal>(breakEvens);
			if (spot.HasValue) gridNotable.Add(spot.Value);
			gridNotable.AddRange(LookupExtraNotablePrices(ticker, opts));
			grid = TimeDecayGridBuilder.Build(merged, netPremium, latestExpiry.Value, opts, padding, centerPrice, gridNotable, maxGridColumns, spot);

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
			OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(ticker, opts)
		);
	}

	private static string BuildTitle(string ticker, List<MergedLeg> merged)
	{
		var descriptors = new List<string>();
		foreach (var leg in merged)
		{
			if (leg.IsStock)
			{
				var sideWord = leg.Side == Side.Buy ? "sh" : "sh short";
				descriptors.Add($"{leg.Qty} {sideWord} Stock");
			}
			else
			{
				var sideWord = leg.Side == Side.Buy ? "Long" : "Short";
				var kind = ParsingHelpers.CallPutDisplayName(leg.Parsed!.CallPut);
				descriptors.Add($"{sideWord} {leg.Qty}× {kind} ${Formatters.FormatQty(leg.Parsed.Strike)}");
			}
		}
		return $"{ticker} Combined — {string.Join(", ", descriptors)}";
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
		if (opts.IvOverrides != null && optionLegs.Any(l => opts.IvOverrides.ContainsKey(l.Symbol))) return true;
		if (opts.OptionQuotes == null) return false;

		var unexpired = optionLegs.Where(l => l.Parsed!.ExpiryDate.Date > evaluationExpiry.Date).ToList();
		if (unexpired.Count == 0) return true;
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
