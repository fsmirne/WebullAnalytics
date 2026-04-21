namespace WebullAnalytics;

/// <summary>
/// A single leg after netting across all positions on one ticker. Either an option leg
/// (with OptionParsed) or a stock leg (with Ticker set and Parsed = null).
/// </summary>
/// <param name="Ticker">Underlying ticker symbol.</param>
/// <param name="Parsed">Parsed OCC info for options; null for stock.</param>
/// <param name="Symbol">OCC symbol (for options); ticker (for stock).</param>
/// <param name="Side">Net side after signed-qty netting.</param>
/// <param name="Qty">Magnitude of net qty (always positive).</param>
/// <param name="Price">Weighted-average price (per share/contract).</param>
/// <param name="SourcePositionCount">Number of distinct source positions merged into this leg.</param>
internal record MergedLeg(
	string Ticker,
	OptionParsed? Parsed,
	string Symbol,
	Side Side,
	int Qty,
	decimal Price,
	int SourcePositionCount
)
{
	internal bool IsStock => Parsed == null;
}

/// <summary>
/// Flattens and merges legs across positions on a single ticker.
/// Groups by MatchKey, nets signed quantities, weighted-averages prices, drops zero-net legs.
/// </summary>
internal static class LegMerger
{
	/// <summary>
	/// Merges the given positions (assumed to share one ticker) into a list of net legs.
	/// Parent strategy rows (Asset == OptionStrategy) are skipped; their legs follow as
	/// IsStrategyLeg rows and are processed individually.
	/// </summary>
	internal static List<MergedLeg> Merge(IEnumerable<PositionRow> positions)
	{
		var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);

		foreach (var row in positions)
		{
			if (row.Asset == Asset.OptionStrategy) continue;
			if (row.MatchKey == null) continue;

			var signedQty = row.Side == Side.Buy ? row.Qty : -row.Qty;
			var price = row.AdjustedAvgPrice ?? row.AvgPrice;

			if (row.Asset == Asset.Stock)
			{
				if (!buckets.TryGetValue(row.MatchKey, out var b))
				{
					b = new Bucket(row.Instrument, parsed: null, symbol: row.Instrument);
					buckets[row.MatchKey] = b;
				}
				b.SignedQty += signedQty;
				b.SignedValue += signedQty * price;
				b.Sources.Add(row);
				continue;
			}

			if (row.Asset == Asset.Option)
			{
				var parsedInfo = MatchKeys.ParseOption(row.MatchKey);
				if (parsedInfo == null) continue;
				var (parsed, symbol) = parsedInfo.Value;
				if (!buckets.TryGetValue(row.MatchKey, out var b))
				{
					b = new Bucket(parsed.Root, parsed, symbol);
					buckets[row.MatchKey] = b;
				}
				b.SignedQty += signedQty;
				b.SignedValue += signedQty * price;
				b.Sources.Add(row);
			}
		}

		var result = new List<MergedLeg>();
		foreach (var b in buckets.Values)
		{
			if (b.SignedQty == 0) continue;
			var price = b.SignedValue / b.SignedQty;
			var side = b.SignedQty > 0 ? Side.Buy : Side.Sell;
			var qty = Math.Abs(b.SignedQty);
			result.Add(new MergedLeg(
				Ticker: b.Ticker,
				Parsed: b.Parsed,
				Symbol: b.Symbol,
				Side: side,
				Qty: qty,
				Price: Math.Round(price, 4, MidpointRounding.AwayFromZero),
				SourcePositionCount: b.Sources.Count
			));
		}

		// Stable order: stock first, then options by expiry then strike.
		result.Sort((a, b) =>
		{
			if (a.IsStock != b.IsStock) return a.IsStock ? -1 : 1;
			if (a.IsStock) return 0;
			var exp = a.Parsed!.ExpiryDate.CompareTo(b.Parsed!.ExpiryDate);
			if (exp != 0) return exp;
			var strike = a.Parsed.Strike.CompareTo(b.Parsed.Strike);
			if (strike != 0) return strike;
			return string.CompareOrdinal(a.Parsed.CallPut, b.Parsed.CallPut);
		});

		return result;
	}

	private sealed class Bucket
	{
		internal string Ticker { get; }
		internal OptionParsed? Parsed { get; }
		internal string Symbol { get; }
		internal int SignedQty { get; set; }
		internal decimal SignedValue { get; set; }
		internal List<PositionRow> Sources { get; } = [];

		internal Bucket(string ticker, OptionParsed? parsed, string symbol)
		{
			Ticker = ticker;
			Parsed = parsed;
			Symbol = symbol;
		}
	}
}
