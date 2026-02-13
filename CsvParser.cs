using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WebullAnalytics;

/// <summary>
/// Parses Webull CSV export files into Trade objects.
/// Handles both single-leg options/stocks and multi-leg strategy orders.
/// </summary>
public static partial class CsvParser
{
	private const decimal OptionMultiplier = 100m;
	private const decimal StockMultiplier = 1m;

	[GeneratedRegex(@"\s[A-Za-z]{3}$")]
	private static partial Regex TimezoneSuffixRegex();

	private static CsvConfiguration CreateLenientConfig() => new(CultureInfo.InvariantCulture)
	{
		IgnoreBlankLines = true,
		ShouldSkipRecord = args => args.Row.Parser.Record != null && args.Row.Parser.Record.All(field => string.IsNullOrWhiteSpace(field)),
		BadDataFound = null,
		MissingFieldFound = null
	};

	/// <summary>
	/// Parses a trade CSV file and returns all filled trades.
	/// Strategy orders are split into a parent trade plus individual leg trades.
	/// </summary>
	public static (List<Trade> trades, int nextSeq) ParseTradeCsv(string path, int seqStart)
	{
		var rawTrades = ParseRawTradeCsv(path);
		if (rawTrades.Count == 0)
			return (new List<Trade>(), seqStart);

		var filled = rawTrades.Where(IsFilledTrade).ToList();
		var isOptions = Path.GetFileName(path).Contains("Options", StringComparison.OrdinalIgnoreCase);

		return isOptions ? BuildOptionTrades(filled, seqStart) : BuildStockTrades(filled, seqStart);
	}

	/// <summary>
	/// Parses a trade CSV file into strongly-typed RawTrade records.
	/// </summary>
	public static List<RawTrade> ParseRawTradeCsv(string path)
	{
		var config = CreateLenientConfig();
		using var reader = new StreamReader(path);
		using var csv = new CsvReader(reader, config);
		csv.Context.RegisterClassMap<RawTradeMap>();
		return csv.GetRecords<RawTrade>().ToList();
	}

	/// <summary>
	/// Parses a fee CSV file and returns a dictionary mapping (Timestamp, Side, Qty) to the fee amount.
	/// </summary>
	public static Dictionary<(DateTime timestamp, Side side, int qty), decimal> ParseFeeCsv(string path)
	{
		var config = CreateLenientConfig();
		using var reader = new StreamReader(path);
		using var csv = new CsvReader(reader, config);
		csv.Context.RegisterClassMap<FeeMap>();
		return csv.GetRecords<Fee>().GroupBy(g => (g.DateTime, g.Side, g.Quantity)).Select(g => new { g.Key, Fees = g.Sum(x => x.Fees) }).ToDictionary(k => k.Key, v => v.Fees);
	}

	private static bool IsFilledTrade(RawTrade rt) => (string.IsNullOrEmpty(rt.Status) || rt.Status.Equals("Filled", StringComparison.OrdinalIgnoreCase)) && rt.Side is Side.Buy or Side.Sell && rt.Filled > 0 && (rt.AveragePrice ?? rt.Price) is not null;

	private static decimal GetPrice(RawTrade rt) => rt.AveragePrice ?? rt.Price ?? 0m;

	private static DateTime GetTimestamp(RawTrade rt) => rt.FilledTime ?? rt.PlacedTime;

	/// <summary>
	/// Builds stock Trade records from parsed RawTrade rows.
	/// </summary>
	private static (List<Trade>, int) BuildStockTrades(List<RawTrade> rawTrades, int seqStart)
	{
		var trades = new List<Trade>();
		var seq = seqStart;

		foreach (var rt in rawTrades)
		{
			if (string.IsNullOrEmpty(rt.Symbol))
				continue;

			trades.Add(new Trade(Seq: seq++, Timestamp: GetTimestamp(rt), Instrument: rt.Symbol, MatchKey: MatchKeys.Stock(rt.Symbol), Asset: Asset.Stock, OptionKind: "", Side: rt.Side, Qty: rt.Filled, Price: GetPrice(rt), Multiplier: StockMultiplier, Expiry: null));
		}

		return (trades, seq);
	}

	/// <summary>
	/// Builds option Trade records from parsed RawTrade rows, including multi-leg strategies.
	/// Strategy rows have a Name but no Symbol; leg rows have a Symbol but no Name.
	/// </summary>
	private static (List<Trade>, int) BuildOptionTrades(List<RawTrade> rawTrades, int seqStart)
	{
		var parentTimes = rawTrades.Where(rt => !string.IsNullOrEmpty(rt.Name) && string.IsNullOrEmpty(rt.Symbol) && rt.PlacedTime != default).Select(rt => rt.PlacedTime).ToHashSet();
		var strategyMeta = BuildStrategyMeta(rawTrades, parentTimes);

		var trades = new List<Trade>();
		var seq = seqStart;
		var strategySeqByPlacedTime = new Dictionary<DateTime, int>();

		foreach (var rt in rawTrades)
		{
			// Strategy parent row (has name, no symbol)
			if (!string.IsNullOrEmpty(rt.Name) && string.IsNullOrEmpty(rt.Symbol))
			{
				var meta = strategyMeta.GetValueOrDefault(rt.PlacedTime, new StrategyMeta());
				var optionKind = ParsingHelpers.StrategyKindFromName(rt.Name);
				var instrument = (meta.Root, meta.ExpDate) switch
				{
					(not null, not null) => $"{meta.Root} {Formatters.FormatOptionDate(meta.ExpDate.Value)}",
					(not null, null) => meta.Root,
					(null, not null) => $"{rt.Name} {Formatters.FormatOptionDate(meta.ExpDate.Value)}",
					_ => rt.Name
				};

				trades.Add(new Trade(Seq: seq, Timestamp: GetTimestamp(rt), Instrument: instrument, MatchKey: BuildStrategyKey(rt.Name, optionKind, meta), Asset: Asset.OptionStrategy, OptionKind: optionKind, Side: rt.Side, Qty: rt.Filled, Price: GetPrice(rt), Multiplier: OptionMultiplier, Expiry: meta.ExpDate));
				strategySeqByPlacedTime[rt.PlacedTime] = seq;
				seq++;
				continue;
			}

			// Strategy leg row (placed time matches a parent, no name)
			if (parentTimes.Contains(rt.PlacedTime) && string.IsNullOrEmpty(rt.Name) && !string.IsNullOrEmpty(rt.Symbol))
			{
				if (strategySeqByPlacedTime.TryGetValue(rt.PlacedTime, out var parentSeq))
					trades.Add(BuildOptionTrade(seq++, rt, parentSeq));
				continue;
			}

			// Standalone option trade
			if (!string.IsNullOrEmpty(rt.Symbol))
				trades.Add(BuildOptionTrade(seq++, rt, parentStrategySeq: null));
		}

		return (trades, seq);
	}

	/// <summary>
	/// Builds metadata for strategy orders by parsing their leg symbols.
	/// </summary>
	private static Dictionary<DateTime, StrategyMeta> BuildStrategyMeta(List<RawTrade> rawTrades, HashSet<DateTime> parentTimes)
	{
		return rawTrades
			.Where(rt => parentTimes.Contains(rt.PlacedTime) && !string.IsNullOrEmpty(rt.Symbol))
			.Select(rt => (placed: rt.PlacedTime, parsed: ParsingHelpers.ParseOptionSymbol(rt.Symbol)))
			.Where(x => x.parsed != null)
			.GroupBy(x => x.placed)
			.ToDictionary(
				g => g.Key,
				g =>
				{
					var first = g.First().parsed!;
					return new StrategyMeta
					{
						Root = first.Root,
						ExpDate = first.ExpiryDate,
						Legs = g.Select(x => (x.parsed!.CallPut, x.parsed.Strike)).ToList()
					};
				});
	}

	/// <summary>
	/// Creates a unique key for matching strategy trades.
	/// Format: strategy:{kind}:{root}:{expDate}:{sorted legs}
	/// </summary>
	private static string BuildStrategyKey(string name, string optionKind, StrategyMeta meta)
	{
		if (meta.Root == null || meta.ExpDate == null)
			return $"strategy:{name}";

		var key = $"strategy:{optionKind}:{meta.Root}:{meta.ExpDate:yyyy-MM-dd}";

		if (meta.Legs.Any())
		{
			var legsKey = string.Join(",", meta.Legs.OrderBy(l => l.CallPut).ThenBy(l => l.Strike).Select(l => $"{l.CallPut}{Formatters.FormatQty(l.Strike)}"));
			key = $"{key}:{legsKey}";
		}

		return key;
	}

	private static Trade BuildOptionTrade(int seq, RawTrade rt, int? parentStrategySeq)
	{
		var parsed = ParsingHelpers.ParseOptionSymbol(rt.Symbol);
		var (instrument, optionKind, expiry) = parsed != null ? (Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike), parsed.CallPut == "C" ? "Call" : "Put", (DateTime?)parsed.ExpiryDate) : (rt.Symbol, "Option", null);

		return new Trade(Seq: seq, Timestamp: GetTimestamp(rt), Instrument: instrument, MatchKey: MatchKeys.Option(rt.Symbol), Asset: Asset.Option, OptionKind: optionKind, Side: rt.Side, Qty: rt.Filled, Price: GetPrice(rt), Multiplier: OptionMultiplier, Expiry: expiry, ParentStrategySeq: parentStrategySeq);
	}

	private class StrategyMeta
	{
		public string? Root { get; set; }
		public DateTime? ExpDate { get; set; }
		public List<(string CallPut, decimal Strike)> Legs { get; set; } = [];
	}

	private sealed class RawTradeMap : ClassMap<RawTrade>
	{
		public RawTradeMap()
		{
			Map(m => m.Name).Name("Name");
			Map(m => m.Symbol).Name("Symbol");
			Map(m => m.Side).Name("Side");
			Map(m => m.Status).Name("Status");
			Map(m => m.Filled).Name("Filled");
			Map(m => m.Quantity).Name("Total Qty");
			Map(m => m.Price).Name("Price").TypeConverter<CustomDecimalConverter>();
			Map(m => m.AveragePrice).Name("Avg Price");
			Map(m => m.TimeInForce).Name("Time-in-Force");
			Map(m => m.PlacedTime).Name("Placed Time").TypeConverter<CustomDateTimeConverter>();
			Map(m => m.FilledTime).Name("Filled Time").TypeConverter<CustomDateTimeConverter>();
		}
	}

	private sealed class FeeMap : ClassMap<Fee>
	{
		public FeeMap()
		{
			Map(m => m.Symbol).Name("Symbol");
			Map(m => m.DateTime).Name("Time").TypeConverter<CustomDateTimeConverter>();
			Map(m => m.Side).Name("Side");
			Map(m => m.Quantity).Name("Quantity");
			Map(m => m.AveragePrice).Name("Avg Price");
			Map(m => m.Amount).Name("Amount");
			Map(m => m.Fees).Name("Fees");
		}
	}

	private class CustomDateTimeConverter : DateTimeConverter
	{
		public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				var targetType = memberMapData.Member switch
				{
					PropertyInfo pi => pi.PropertyType,
					FieldInfo fi => fi.FieldType,
					_ => null
				};

				if (targetType is not null && Nullable.GetUnderlyingType(targetType) is not null)
					return null!;

				return base.ConvertFromString(text, row, memberMapData);
			}

			text = TimezoneSuffixRegex().Replace(text.Trim(), "");

			var formats = new[]
			{
				"MM/dd/yyyy HH:mm:ss",
				"M/d/yyyy H:mm:ss",
				"MM/dd/yyyy H:mm:ss",
				"M/d/yyyy HH:mm:ss"
			};

			if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
				return dt;

			return base.ConvertFromString(text, row, memberMapData);
		}
	}

	private class CustomDecimalConverter : DecimalConverter
	{
		public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
		{
			if (ParsingHelpers.TryParseWebullDecimal(text, out var result))
				return result;

			if (string.IsNullOrWhiteSpace(text))
				return null;

			return base.ConvertFromString(text, row, memberMapData);
		}
	}
}
