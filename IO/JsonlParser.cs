using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebullAnalytics.Utils;

namespace WebullAnalytics.IO;

/// <summary>
/// Parses Webull JSONL order export files into Trade objects and fee data.
/// Each line is a JSON object with an orderList array for one ticker.
/// Orders sharing the same transactTime are treated as legs of a single strategy.
/// </summary>
public static partial class JsonlParser
{
	private const decimal OptionMultiplier = Trade.OptionMultiplier;

	// Matches "SPXW $6845.00" or "GME $24.00" → root + strike
	[GeneratedRegex(@"^(.+?)\s+\$(.+)$")]
	private static partial Regex SymbolRegex();

	// Matches "13 Feb 26 Call 100" → day, month, year, callPut, multiplier
	[GeneratedRegex(@"^(\d{1,2})\s+(\w{3})\s+(\d{2,4})\s+(Call|Put)\s+(\d+)$")]
	private static partial Regex SubSymbolRegex();


	/// <summary>
	/// Parses a JSONL orders file and returns trades and a fee lookup dictionary.
	/// The fee lookup uses the same key structure as CsvParser.ParseFeeCsv for compatibility with PositionTracker.LookupFee.
	/// </summary>
	public static (List<Trade> trades, Dictionary<(DateTime timestamp, Side side, int qty), decimal> fees) ParseOrdersJsonl(string path)
	{
		var allOrders = new List<ParsedOrder>();
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			using var doc = JsonDocument.Parse(line);
			if (!doc.RootElement.TryGetProperty("orderList", out var orderList)) continue;
			foreach (var elem in orderList.EnumerateArray())
			{
				var order = ParseOrder(elem);
				if (order != null) allOrders.Add(order);
			}
		}

		allOrders.Sort((a, b) => a.TransactTime.CompareTo(b.TransactTime));

		var groups = allOrders.GroupBy(o => o.TransactTime).OrderBy(g => g.Key).ToList();

		var trades = new List<Trade>();
		var fees = new Dictionary<(DateTime, Side, int), decimal>();
		var seq = 0;

		foreach (var group in groups)
		{
			foreach (var combo in PartitionIntoCombos(group.ToList()))
			{
				if (combo.Count >= 2)
					BuildStrategyTrades(combo, trades, fees, ref seq);
				else
					BuildStandaloneTrade(combo[0], trades, fees, ref seq);
			}
		}

		return (trades, fees);
	}

	private static ParsedOrder? ParseOrder(JsonElement elem)
	{
		var symbol = elem.GetProperty("symbol").GetString()!;
		var subSymbol = elem.GetProperty("subSymbol").GetString()!;
		var filledTimeStr = elem.GetProperty("filledTime").GetString()!;
		var action = elem.GetProperty("action").GetString()!;
		var quantityStr = elem.GetProperty("quantity").GetString()!;
		var filledPriceStr = elem.GetProperty("filledPrice").GetString()!;
		var feeStr = elem.GetProperty("fee").GetString()!;
		var commissionStr = elem.GetProperty("commission").GetString()!;
		var transactTime = elem.GetProperty("transactTime").GetInt64();

		var symMatch = SymbolRegex().Match(symbol);
		if (!symMatch.Success) return null;
		var root = symMatch.Groups[1].Value;
		if (!decimal.TryParse(symMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) return null;

		var subMatch = SubSymbolRegex().Match(subSymbol);
		if (!subMatch.Success) return null;
		var dateStr = $"{subMatch.Groups[1].Value} {subMatch.Groups[2].Value} {subMatch.Groups[3].Value}";
		var dateFormat = subMatch.Groups[3].Value.Length == 4 ? "d MMM yyyy" : "d MMM yy";
		if (!DateTime.TryParseExact(dateStr, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate)) return null;
		var callPut = subMatch.Groups[4].Value == "Call" ? "C" : "P";

		// Build OCC symbol: ROOT + YYMMDD + C/P + 8-digit strike (strike * 1000)
		var strikeInt = (long)(strike * 1000m);
		var occSymbol = $"{root}{expiryDate:yyMMdd}{callPut}{strikeInt:D8}";

		if (!ParsingHelpers.TryParseWebullDateTime(filledTimeStr, out var filledTime))
			return null;

		var side = action.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell;
		var qty = (int)decimal.Parse(quantityStr, CultureInfo.InvariantCulture);
		var price = decimal.Parse(filledPriceStr, CultureInfo.InvariantCulture);
		var fee = decimal.Parse(feeStr, CultureInfo.InvariantCulture);
		var commission = decimal.Parse(commissionStr, CultureInfo.InvariantCulture);

		return new ParsedOrder { Root = root, Strike = strike, ExpiryDate = expiryDate, CallPut = callPut, OccSymbol = occSymbol, FilledTime = filledTime, TransactTime = transactTime, Side = side, Qty = qty, Price = price, TotalFee = fee + commission, };
	}

	/// <summary>Splits a same-transactTime order group into the combo tickets it plausibly came from.
	/// Webull's web export carries no combo id — transactTime (second resolution) is the only grouping
	/// signal — so two combos filling in the same second collide (observed live 2026-06-11: two separate
	/// diagonal closes merged into a phantom 4-leg DoubleDiagonal in the report). Webull itself rejects a
	/// 4-leg multi-expiry ticket (the reason StructureOrderSplit places doubles/diagonal-verticals as two
	/// orders), so any such group is provably at least two combos. The partition mirrors how those orders
	/// are placed: mixed calls+puts split by side (the double-calendar/diagonal shape, recursing per side);
	/// a single side splits by expiry when that yields buy/sell pairs (the near+far vertical shape);
	/// otherwise the k-th buy pairs with the k-th sell by strike rank (two same-side diagonals in one
	/// second — strike-adjacent pairing is the best available signal). Groups Webull could have filled as
	/// one ticket (fewer than 4 legs, or a single expiry, e.g. an iron condor) are never split, and a
	/// group nothing matches is kept whole rather than invent pairings.</summary>
	private static List<List<ParsedOrder>> PartitionIntoCombos(List<ParsedOrder> orders)
	{
		var result = new List<List<ParsedOrder>>();
		if (orders.Count < 4 || orders.Select(o => o.ExpiryDate).Distinct().Count() == 1)
		{
			result.Add(orders);
			return result;
		}

		var calls = orders.Where(o => o.CallPut == "C").ToList();
		var puts = orders.Where(o => o.CallPut == "P").ToList();
		if (calls.Count > 0 && puts.Count > 0)
		{
			result.AddRange(PartitionIntoCombos(calls));
			result.AddRange(PartitionIntoCombos(puts));
			return result;
		}

		// Same-strike cross-expiry pairs first: per-leg ROLL tickets (buy back one expiry, sell the
		// other at the SAME strike) that fill in the same second. Live example 2026-03-20: a 5-lot SPXW
		// put vertical rolled 20Mar→23Mar as two roll combos (6525 short roll + 6520 long roll) — the
		// by-expiry reading would mis-pair those as two verticals. Checked before the by-expiry branch
		// because when both apply (calendar-vertical shapes), the decompositions are economically
		// equivalent, while for rolls only the strike pairing is right.
		var byStrike = orders.GroupBy(o => o.Strike).Select(g => g.ToList()).ToList();
		if (byStrike.All(g => g.Count == 2 && g[0].ExpiryDate != g[1].ExpiryDate && g[0].Side != g[1].Side))
		{
			result.AddRange(byStrike);
			return result;
		}

		var byExpiry = orders.GroupBy(o => o.ExpiryDate).Select(g => g.ToList()).ToList();
		if (byExpiry.All(g => g.Count == 2 && g[0].Side != g[1].Side))
		{
			result.AddRange(byExpiry);
			return result;
		}

		var buys = orders.Where(o => o.Side == Side.Buy).OrderBy(o => o.Strike).ToList();
		var sells = orders.Where(o => o.Side == Side.Sell).OrderBy(o => o.Strike).ToList();
		if (buys.Count == sells.Count && buys.Count > 0)
		{
			for (var i = 0; i < buys.Count; i++)
				result.Add(new List<ParsedOrder> { buys[i], sells[i] });
			return result;
		}

		result.Add(orders);
		return result;
	}

	private static void BuildStandaloneTrade(ParsedOrder order, List<Trade> trades, Dictionary<(DateTime, Side, int), decimal> fees, ref int seq)
	{
		var instrument = Formatters.FormatOptionDisplay(order.Root, order.ExpiryDate, order.Strike);
		var optionKind = ParsingHelpers.CallPutDisplayName(order.CallPut);

		trades.Add(new Trade(Seq: seq++, Timestamp: order.FilledTime, Instrument: instrument, MatchKey: MatchKeys.Option(order.OccSymbol), Asset: Asset.Option, OptionKind: optionKind, Side: order.Side, Qty: order.Qty, Price: RoundPrice(order.Price), Multiplier: OptionMultiplier, Expiry: order.ExpiryDate, Fee: order.TotalFee));
		AddFee(fees, order.FilledTime, order.Side, order.Qty, order.TotalFee);
	}

	private static void BuildStrategyTrades(List<ParsedOrder> orders, List<Trade> trades, Dictionary<(DateTime, Side, int), decimal> fees, ref int seq)
	{
		var first = orders[0];
		var qty = first.Qty;

		var strategyKind = DetectStrategyKind(orders);

		// Compute parent price from raw (unrounded) leg prices to preserve sub-penny precision
		var netCash = orders.Sum(o => o.Side == Side.Sell ? o.Price * o.Qty : -o.Price * o.Qty);
		var parentSide = netCash >= 0 ? Side.Sell : Side.Buy;
		var parentPrice = Math.Abs(netCash) / qty;

		// Use max expiry for the strategy's date — deterministic and matches CSV behavior for calendars
		var expDate = orders.Max(o => o.ExpiryDate);
		var legsKey = string.Join(",", orders.Select(o => (o.CallPut, o.Strike)).OrderBy(l => l.CallPut).ThenBy(l => l.Strike).Select(l => $"{l.CallPut}{Formatters.FormatQty(l.Strike)}"));
		var matchKey = $"strategy:{strategyKind}:{first.Root}:{expDate:yyyy-MM-dd}:{legsKey}";
		var instrument = $"{first.Root} {Formatters.FormatOptionDate(expDate)}";

		var parentSeq = seq++;
		trades.Add(new Trade(Seq: parentSeq, Timestamp: first.FilledTime, Instrument: instrument, MatchKey: matchKey, Asset: Asset.OptionStrategy, OptionKind: strategyKind, Side: parentSide, Qty: qty, Price: parentPrice, Multiplier: OptionMultiplier, Expiry: expDate));

		foreach (var leg in orders)
		{
			var legInstrument = Formatters.FormatOptionDisplay(leg.Root, leg.ExpiryDate, leg.Strike);
			var legOptionKind = ParsingHelpers.CallPutDisplayName(leg.CallPut);

			trades.Add(new Trade(Seq: seq++, Timestamp: leg.FilledTime, Instrument: legInstrument, MatchKey: MatchKeys.Option(leg.OccSymbol), Asset: Asset.Option, OptionKind: legOptionKind, Side: leg.Side, Qty: leg.Qty, Price: RoundPrice(leg.Price), Multiplier: OptionMultiplier, Expiry: leg.ExpiryDate, ParentStrategySeq: parentSeq, Fee: leg.TotalFee));
			AddFee(fees, leg.FilledTime, leg.Side, leg.Qty, leg.TotalFee);
		}
	}

	private static string DetectStrategyKind(List<ParsedOrder> legs) =>
		ParsingHelpers.ClassifyStrategyKind(
			legs.Count,
			legs.Select(l => l.ExpiryDate).Distinct().Count(),
			legs.Select(l => l.Strike).Distinct().Count(),
			legs.Select(l => l.CallPut).Distinct().Count());

	private static decimal RoundPrice(decimal price) => Math.Round(price, 3, MidpointRounding.AwayFromZero);

	/// <summary>
	/// Adds a fee to the lookup dictionary, summing when keys collide.
	/// This matches the grouping behavior of CsvParser.ParseFeeCsv.
	/// </summary>
	private static void AddFee(Dictionary<(DateTime, Side, int), decimal> fees, DateTime timestamp, Side side, int qty, decimal fee)
	{
		var key = (timestamp, side, qty);
		if (fees.TryGetValue(key, out var existing))
			fees[key] = existing + fee;
		else
			fees[key] = fee;
	}

	private sealed class ParsedOrder
	{
		public required string Root { get; init; }
		public decimal Strike { get; init; }
		public DateTime ExpiryDate { get; init; }
		public required string CallPut { get; init; }
		public required string OccSymbol { get; init; }
		public DateTime FilledTime { get; init; }
		public long TransactTime { get; init; }
		public Side Side { get; init; }
		public int Qty { get; init; }
		public decimal Price { get; init; }
		public decimal TotalFee { get; init; }
	}
}
