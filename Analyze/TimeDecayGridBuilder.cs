using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Builds 2D grids of option values across dates and underlying prices for time-decay visualization.
/// </summary>
internal static class TimeDecayGridBuilder
{
	/// <summary>
	/// Builds a 2D grid of option values across dates and underlying prices.
	/// </summary>
	internal static TimeDecayGrid Build(List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty, Side parentSide, decimal netPremium, DateTime latestExpiry, AnalysisOptions opts, decimal padding, decimal centerPrice, List<decimal> breakEvens, int maxColumns, decimal? underlyingPrice)
	{
		var dates = BuildDateColumns(latestExpiry, maxColumns);
		var strikes = legs.Select(l => l.parsed.Strike).Distinct().ToList();
		var priceRows = BuildPriceRows(centerPrice, padding, breakEvens, strikes);

		var values = new decimal[priceRows.Count, dates.Count];
		var pnls = new decimal[priceRows.Count, dates.Count];
		var includeLegs = legs.Count > 1;
		var legValues = includeLegs ? new decimal[legs.Count, priceRows.Count, dates.Count] : null;

		for (int di = 0; di < dates.Count; di++)
		{
			var evalDate = dates[di];
			for (int pi = 0; pi < priceRows.Count; pi++)
			{
				var price = priceRows[pi];
				decimal totalPnL = 0m;
				for (int li = 0; li < legs.Count; li++)
				{
					var l = legs[li];
					totalPnL += OptionMath.LegPnLWithBs(price, l.parsed, l.symbol, l.row.Side, qty, OptionMath.GetPremium(l.row), evalDate, opts);
					if (legValues != null)
						legValues[li, pi, di] = Math.Round(OptionMath.LegContractValueWithBs(price, l.parsed, l.symbol, l.row.Side, evalDate, opts), 4);
				}

				var value = parentSide == Side.Buy ? netPremium + totalPnL / (qty * 100m) : netPremium - totalPnL / (qty * 100m);
				values[pi, di] = Math.Round(value, 4);
				var displayValue = Math.Round(values[pi, di], 2, MidpointRounding.AwayFromZero);
				pnls[pi, di] = parentSide == Side.Buy ? Math.Round((displayValue - netPremium) * qty * 100m, 2) : Math.Round((netPremium - displayValue) * qty * 100m, 2);
			}
		}

		List<string>? legLabels = null;
		if (includeLegs)
		{
			legLabels = new List<string>(legs.Count);
			foreach (var l in legs)
			{
				var sideStr = l.row.Side == Side.Buy ? "L" : "S";
				legLabels.Add($"{sideStr}{l.parsed.CallPut}{l.parsed.Strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
			}
		}

		// Anchor the first column (today) to market bid/ask mid-prices when available.
		if (!opts.Theoretical && opts.OptionQuotes != null && underlyingPrice.HasValue)
		{
			var marketMid = ComputeSpreadMarketMid(legs, opts.OptionQuotes);
			if (marketMid.HasValue)
			{
				int closestRow = 0;
				var closestDist = decimal.MaxValue;
				for (int pi = 0; pi < priceRows.Count; pi++)
				{
					var dist = Math.Abs(priceRows[pi] - underlyingPrice.Value);
					if (dist < closestDist) { closestDist = dist; closestRow = pi; }
				}

				var marketValue = parentSide == Side.Sell ? -marketMid.Value : marketMid.Value;
				var bsValue = values[closestRow, 0];
				var adjustment = bsValue - marketValue;
				if (adjustment != 0)
				{
					for (int pi = 0; pi < priceRows.Count; pi++)
					{
						values[pi, 0] = Math.Round(values[pi, 0] - adjustment, 4);
						var adjDisplayValue = Math.Round(values[pi, 0], 2);
						pnls[pi, 0] = parentSide == Side.Buy ? Math.Round((adjDisplayValue - netPremium) * qty * 100, 2) : Math.Round((netPremium - adjDisplayValue) * qty * 100, 2);
					}
				}

				// Anchor per-leg values to market mid as well.
				if (legValues != null)
				{
					for (int li = 0; li < legs.Count; li++)
					{
						var l = legs[li];
						if (!opts.OptionQuotes.TryGetValue(l.symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) continue;
						var legMid = (q.Bid.Value + q.Ask.Value) / 2m;
						var legBs = legValues[li, closestRow, 0];
						var legAdj = legBs - legMid;
						if (legAdj != 0)
						{
							for (int pi = 0; pi < priceRows.Count; pi++)
								legValues[li, pi, 0] = Math.Round(legValues[li, pi, 0] - legAdj, 4);
						}
					}
				}
			}
		}

		return new TimeDecayGrid(dates, priceRows, values, pnls, strikes, legValues, legLabels);
	}

	/// <summary>
	/// Builds a 2D grid for merged legs where each leg carries its own signed quantity
	/// (as produced by <see cref="LegMerger"/>). Unlike the uniform-qty overload, signs
	/// and magnitudes are baked per leg, so no parentSide flip is applied.
	/// Stock legs are not rendered as grid rows; callers add stock P&L to net cells
	/// after this method returns.
	/// <paramref name="normalizingQty"/> scales the per-cell "value" to a per-pair basis
	/// (Σ signed × (legQty/normalizingQty) × legValue), keeping the grid on the same scale
	/// as <paramref name="netPremium"/> (per-pair adj basis). Callers pass
	/// <c>min(longQty, shortQty)</c> when the portfolio is at all balanced, else the total option qty.
	/// Cell P&L is derived from the rounded display value so that colors stay consistent with
	/// what the user sees (matches the uniform-qty overload's behavior at rounded break-evens).
	/// </summary>
	internal static TimeDecayGrid Build(List<MergedLeg> mergedLegs, decimal netPremium, int normalizingQty, DateTime latestExpiry, AnalysisOptions opts, decimal padding, decimal centerPrice, List<decimal> breakEvens, int maxColumns, decimal? underlyingPrice)
	{
		// Only option legs participate in the grid math; stock is a linear overlay added by the caller.
		var optionLegs = mergedLegs.Where(l => !l.IsStock).ToList();

		var dates = BuildDateColumns(latestExpiry, maxColumns);
		var strikes = optionLegs.Select(l => l.Parsed!.Strike).Distinct().ToList();
		var priceRows = BuildPriceRows(centerPrice, padding, breakEvens, strikes);

		var values = new decimal[priceRows.Count, dates.Count];
		var pnls = new decimal[priceRows.Count, dates.Count];
		var includeLegs = optionLegs.Count > 1;
		var legValues = includeLegs ? new decimal[optionLegs.Count, priceRows.Count, dates.Count] : null;

		for (int di = 0; di < dates.Count; di++)
		{
			var evalDate = dates[di];
			for (int pi = 0; pi < priceRows.Count; pi++)
			{
				var price = priceRows[pi];
				decimal perPairValue = 0m;
				for (int li = 0; li < optionLegs.Count; li++)
				{
					var l = optionLegs[li];
					var legBs = OptionMath.LegContractValueWithBs(price, l.Parsed!, l.Symbol, l.Side, evalDate, opts);
					var signed = l.Side == Side.Buy ? 1 : -1;
					var weight = normalizingQty > 0 ? (decimal)l.Qty / normalizingQty : 0m;
					perPairValue += signed * weight * legBs;
					if (legValues != null)
						legValues[li, pi, di] = Math.Round(legBs, 4);
				}

				values[pi, di] = Math.Round(perPairValue, 4);
				var displayValue = Math.Round(values[pi, di], 2, MidpointRounding.AwayFromZero);
				pnls[pi, di] = Math.Round((displayValue - netPremium) * normalizingQty * 100m, 2);
			}
		}

		List<string>? legLabels = null;
		if (includeLegs)
		{
			legLabels = new List<string>(optionLegs.Count);
			foreach (var l in optionLegs)
			{
				var sideStr = l.Side == Side.Buy ? "L" : "S";
				legLabels.Add($"{sideStr}{l.Parsed!.CallPut}{l.Parsed!.Strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
			}
		}

		// Anchor the first column (today) to market bid/ask mid if all merged option legs have quotes.
		if (!opts.Theoretical && opts.OptionQuotes != null && underlyingPrice.HasValue)
		{
			var marketMid = ComputeMergedMarketMid(optionLegs, normalizingQty, opts.OptionQuotes);
			if (marketMid.HasValue)
			{
				int closestRow = 0;
				var closestDist = decimal.MaxValue;
				for (int pi = 0; pi < priceRows.Count; pi++)
				{
					var dist = Math.Abs(priceRows[pi] - underlyingPrice.Value);
					if (dist < closestDist) { closestDist = dist; closestRow = pi; }
				}

				var bsPerPairValue = values[closestRow, 0];
				var perPairAdjustment = bsPerPairValue - marketMid.Value;

				if (perPairAdjustment != 0)
				{
					for (int pi = 0; pi < priceRows.Count; pi++)
					{
						values[pi, 0] = Math.Round(values[pi, 0] - perPairAdjustment, 4);
						var adjDisplayValue = Math.Round(values[pi, 0], 2, MidpointRounding.AwayFromZero);
						pnls[pi, 0] = Math.Round((adjDisplayValue - netPremium) * normalizingQty * 100m, 2);
					}
				}

				if (legValues != null)
				{
					for (int li = 0; li < optionLegs.Count; li++)
					{
						var l = optionLegs[li];
						if (!opts.OptionQuotes.TryGetValue(l.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) continue;
						var legMid = (q.Bid.Value + q.Ask.Value) / 2m;
						var legBs = legValues[li, closestRow, 0];
						var legAdj = legBs - legMid;
						if (legAdj != 0)
						{
							for (int pi = 0; pi < priceRows.Count; pi++)
								legValues[li, pi, 0] = Math.Round(legValues[li, pi, 0] - legAdj, 4);
						}
					}
				}
			}
		}

		return new TimeDecayGrid(dates, priceRows, values, pnls, strikes, legValues, legLabels);
	}

	/// <summary>
	/// Per-pair signed market mid across merged option legs: each leg contributes
	/// sign × (legQty/normalizingQty) × mid, matching the per-pair scale of the "value" cell.
	/// </summary>
	private static decimal? ComputeMergedMarketMid(List<MergedLeg> legs, int normalizingQty, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		if (normalizingQty <= 0) return null;
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
				return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			var signed = leg.Side == Side.Buy ? 1 : -1;
			var weight = (decimal)leg.Qty / normalizingQty;
			total += signed * weight * mid;
		}
		return total;
	}

	/// <summary>
	/// Generates evenly-spaced date columns from today to expiration.
	/// The last two columns are expiration day at market open and market close.
	/// Slots are filled by priority: trading days first, then holidays, then weekends.
	/// Each tier is shown in full before drawing from the next; within a tier, selection is evenly spaced.
	/// </summary>
	private static List<DateTime> BuildDateColumns(DateTime expiry, int maxColumns)
	{
		var today = EvaluationDate.Today;
		if ((expiry.Date - today).TotalDays <= 0)
			return [today + OptionMath.MarketOpen, expiry.Date + OptionMath.MarketClose];

		// Classify every calendar day from today up to (not including) expiry by priority tier.
		var tradingDays = new List<DateTime>();
		var holidays = new List<DateTime>();
		var weekends = new List<DateTime>();
		for (var d = today; d.Date < expiry.Date; d = d.AddDays(1))
		{
			var dt = d.Date + OptionMath.MarketOpen;
			if (MarketCalendar.IsOpen(d)) tradingDays.Add(dt);
			else if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) holidays.Add(dt);
			else weekends.Add(dt);
		}

		var interiorSlots = Math.Max(1, maxColumns - 2);
		List<DateTime> selected;

		var allCount = tradingDays.Count + holidays.Count + weekends.Count;
		if (allCount <= interiorSlots)
		{
			// Every calendar day fits — show them all.
			selected = tradingDays.Concat(holidays).Concat(weekends).OrderBy(d => d).ToList();
		}
		else if (tradingDays.Count + holidays.Count <= interiorSlots)
		{
			// All trading days and holidays fit; fill remaining slots with evenly-spaced weekends.
			var remaining = interiorSlots - tradingDays.Count - holidays.Count;
			selected = tradingDays.Concat(holidays).Concat(EvenlySpaced(weekends, remaining)).OrderBy(d => d).ToList();
		}
		else if (tradingDays.Count <= interiorSlots)
		{
			// All trading days fit; fill remaining slots with evenly-spaced holidays.
			var remaining = interiorSlots - tradingDays.Count;
			selected = tradingDays.Concat(EvenlySpaced(holidays, remaining)).OrderBy(d => d).ToList();
		}
		else
		{
			// Too many trading days — select evenly-spaced trading days only.
			selected = EvenlySpaced(tradingDays, interiorSlots);
		}

		selected.Add(expiry.Date + OptionMath.MarketOpen);
		selected.Add(expiry.Date + OptionMath.MarketClose);
		return selected;
	}

	/// <summary>
	/// Selects <paramref name="count"/> evenly-spaced items from <paramref name="source"/> by index.
	/// </summary>
	private static List<DateTime> EvenlySpaced(List<DateTime> source, int count)
	{
		if (count <= 0 || source.Count == 0) return [];
		if (count >= source.Count) return [.. source];
		if (count == 1) return [source[0]];
		var result = new List<DateTime>(count);
		for (int i = 0; i < count; i++)
			result.Add(source[(int)Math.Round((double)i * (source.Count - 1) / (count - 1))]);
		return result;
	}

	/// <summary>
	/// Generates price rows for the grid, always including 2 padding rows beyond the outermost notable price.
	/// </summary>
	private static List<decimal> BuildPriceRows(decimal centerPrice, decimal granularity, List<decimal> breakEvens, List<decimal> strikes)
	{
		var notablePrices = breakEvens.Concat(strikes).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();

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

		var step = Math.Max(0.01m, referenceGap / granularity);

		const int paddingRows = 2;
		var low = Math.Min(centerPrice - 5 * step, notablePrices[0] - paddingRows * step);
		var high = Math.Max(centerPrice + 5 * step, notablePrices[^1] + paddingRows * step);
		low = Math.Max(0.01m, low);

		var prices = new SortedSet<decimal>();
		var numSteps = (int)Math.Round((high - low) / step);
		for (int i = 0; i <= numSteps; i++)
			prices.Add(Math.Round(low + i * step, 2));

		foreach (var p in notablePrices)
			prices.Add(Math.Round(p, 2));

		// Trim to exactly paddingRows beyond the outermost notable prices.
		// The evenly-spaced grid may produce more points than intended due to
		// rounding, causing asymmetric padding above vs below break-evens.
		var sortedList = prices.ToList();
		var lowestNotable = Math.Round(notablePrices[0], 2);
		var highestNotable = Math.Round(notablePrices[^1], 2);
		int firstNotableIdx = sortedList.IndexOf(lowestNotable);
		int lastNotableIdx = sortedList.IndexOf(highestNotable);
		if (firstNotableIdx >= 0 && lastNotableIdx >= 0)
		{
			int trimStart = Math.Max(0, firstNotableIdx - paddingRows);
			int trimEnd = Math.Min(sortedList.Count - 1, lastNotableIdx + paddingRows);
			sortedList = sortedList.GetRange(trimStart, trimEnd - trimStart + 1);
		}
		sortedList.Reverse();
		return sortedList;
	}

	/// <summary>
	/// Computes the spread's market mid-price from Yahoo bid/ask quotes.
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
}
