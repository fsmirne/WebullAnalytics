namespace WebullAnalytics;

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

		for (int di = 0; di < dates.Count; di++)
		{
			var evalDate = dates[di];
			for (int pi = 0; pi < priceRows.Count; pi++)
			{
				var price = priceRows[pi];
				var totalPnL = legs.Sum(l => OptionMath.LegPnLWithBs(price, l.parsed, l.symbol, l.row.Side, qty, OptionMath.GetPremium(l.row), evalDate, opts));

				var value = parentSide == Side.Buy ? netPremium + totalPnL / (qty * 100m) : netPremium - totalPnL / (qty * 100m);
				values[pi, di] = Math.Round(value, 4);
				var displayValue = Math.Round(value, 2);
				pnls[pi, di] = parentSide == Side.Buy ? Math.Round((displayValue - netPremium) * qty * 100m, 2) : Math.Round((netPremium - displayValue) * qty * 100m, 2);
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
			}
		}

		return new TimeDecayGrid(dates, priceRows, values, pnls, strikes);
	}

	/// <summary>
	/// Generates ~7 evenly-spaced date columns from today to expiration.
	/// The last two columns are expiration day at market open and market close.
	/// </summary>
	private static List<DateTime> BuildDateColumns(DateTime expiry, int maxColumns)
	{
		var today = EvaluationDate.Today;
		var totalDays = (int)(expiry.Date - today).TotalDays;
		if (totalDays <= 0) return [today + OptionMath.MarketOpen, expiry.Date + OptionMath.MarketClose];

		var expiryOpen = expiry.Date + OptionMath.MarketOpen;
		var expiryClose = expiry.Date + OptionMath.MarketClose;

		var interiorSlots = Math.Max(1, maxColumns - 2);
		var dates = new List<DateTime>();

		if (totalDays <= interiorSlots)
		{
			for (int d = 0; d < totalDays; d++)
				dates.Add(today.AddDays(d) + OptionMath.MarketOpen);
		}
		else
		{
			for (int i = 0; i < interiorSlots; i++)
			{
				var dayOffset = (int)Math.Round((double)i * (totalDays - 1) / (interiorSlots - 1));
				var date = today.AddDays(dayOffset);
				if (date.Date >= expiry.Date) break;
				dates.Add(date + OptionMath.MarketOpen);
			}
		}

		dates.RemoveAll(d => d.Date >= expiry.Date);
		dates.Add(expiryOpen);
		dates.Add(expiryClose);
		return dates;
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
