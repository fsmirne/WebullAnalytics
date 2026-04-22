namespace WebullAnalytics.AI;

/// <summary>
/// Estimates the max profit per share a position can achieve across its remaining lifetime,
/// sampled over a grid of future dates (today → latest leg expiry) and spot prices (±25% of current spot).
/// Uses Black-Scholes with each leg's IV from the live quote snapshot. Returns null when any leg
/// is missing IV (fail-closed so TakeProfitRule doesn't trigger on incomplete data).
/// </summary>
internal static class ProfitProjector
{
	private const double RiskFreeRate = 0.036;
	private const int DateSamples = 8;        // today + 6 mid + latest expiry
	private const int PriceSamples = 21;      // 21 price rows across ±25% around spot
	private const decimal PriceRangePct = 0.25m;

	public static decimal? MaxForCurrentColumn(OpenPosition position, EvaluationContext ctx)
	{
		if (position.Legs.Count == 0) return null;
		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m) return null;

		// Need IV + parsed OCC symbol for every leg. Stock legs (CallPut null) aren't supported here.
		var legData = new (PositionLeg Leg, OptionParsed Parsed, decimal Iv)[position.Legs.Count];
		for (int i = 0; i < position.Legs.Count; i++)
		{
			var leg = position.Legs[i];
			if (leg.CallPut == null || leg.Expiry == null) return null;
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q) || !q.ImpliedVolatility.HasValue || q.ImpliedVolatility.Value <= 0m) return null;
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed == null) return null;
			legData[i] = (leg, parsed, q.ImpliedVolatility.Value);
		}

		var today = ctx.Now.Date;
		var latestExpiry = position.Legs.Max(l => l.Expiry!.Value.Date);
		if (latestExpiry <= today) return null;

		var dates = BuildDateSamples(today, latestExpiry);
		var prices = BuildPriceSamples(spot);

		var maxProfitPerShare = decimal.MinValue;
		foreach (var date in dates)
		{
			foreach (var price in prices)
			{
				var netValuePerShare = 0m;
				foreach (var (leg, parsed, iv) in legData)
				{
					var years = Math.Max(0.0, (parsed.ExpiryDate.Date - date).TotalDays / 365.0);
					var legValue = OptionMath.BlackScholes(price, parsed.Strike, years, RiskFreeRate, iv, parsed.CallPut);
					netValuePerShare += leg.Side == Side.Buy ? legValue : -legValue;
				}
				var profitPerShare = netValuePerShare - position.AdjustedNetDebit;
				if (profitPerShare > maxProfitPerShare) maxProfitPerShare = profitPerShare;
			}
		}

		return maxProfitPerShare == decimal.MinValue ? null : maxProfitPerShare;
	}

	private static List<DateTime> BuildDateSamples(DateTime today, DateTime latestExpiry)
	{
		var totalDays = (latestExpiry - today).Days;
		var dates = new List<DateTime>(DateSamples);
		for (int i = 0; i < DateSamples - 1; i++)
			dates.Add(today.AddDays(totalDays * i / (DateSamples - 1)));
		dates.Add(latestExpiry);
		return dates;
	}

	private static List<decimal> BuildPriceSamples(decimal spot)
	{
		var low = spot * (1m - PriceRangePct);
		var high = spot * (1m + PriceRangePct);
		var step = (high - low) / (PriceSamples - 1);
		var prices = new List<decimal>(PriceSamples);
		for (int i = 0; i < PriceSamples; i++)
			prices.Add(low + step * i);
		return prices;
	}
}
