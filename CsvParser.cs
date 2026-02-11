using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;

namespace WebullAnalytics;

/// <summary>
/// Parses Webull CSV export files into Trade objects.
/// Handles both single-leg options/stocks and multi-leg strategy orders.
/// </summary>
public static class CsvParser
{
    private const decimal OptionMultiplier = 100m;
    private const decimal StockMultiplier = 1m;

    /// <summary>
    /// Parses a CSV file and returns all filled trades.
    /// Strategy orders are split into a parent trade plus individual leg trades.
    /// </summary>
    /// <param name="path">Path to the CSV file</param>
    /// <param name="seqStart">Starting sequence number for trades</param>
    /// <returns>List of trades and the next available sequence number</returns>
    public static (List<Trade> trades, int nextSeq) ParseCsv(string path, int seqStart)
    {
        var rows = ReadCsvRows(path);
        if (rows == null)
            return (new List<Trade>(), seqStart);

        var isOptions = Path.GetFileName(path).Contains("Options", StringComparison.OrdinalIgnoreCase);

        if (!isOptions)
            return ParseStockTrades(rows, seqStart);

        return ParseOptionTrades(rows, seqStart);
    }

    /// <summary>
    /// Reads all rows from a CSV file into dictionaries.
    /// </summary>
    private static List<Dictionary<string, string>>? ReadCsvRows(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord;

        // Validate required columns exist
        if (headers == null || !headers.Contains("Side") || !headers.Contains("Filled"))
            return null;

        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = headers.ToDictionary(h => h, h => csv.GetField(h) ?? "");
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Parses stock trades from CSV rows.
    /// </summary>
    private static (List<Trade>, int) ParseStockTrades(List<Dictionary<string, string>> rows, int seqStart)
    {
        var trades = new List<Trade>();
        var seq = seqStart;

        foreach (var row in rows)
        {
            var core = ExtractCoreFields(row);
            if (core == null)
                continue;

            var symbol = row.GetValueOrDefault("Symbol", "").Trim();
            if (string.IsNullOrEmpty(symbol))
                continue;

            trades.Add(BuildStockTrade(seq++, core.Value, symbol));
        }

        return (trades, seq);
    }

    /// <summary>
    /// Parses option trades from CSV rows, including multi-leg strategies.
    /// Strategy rows have a Name but no Symbol; leg rows have a Symbol but no Name.
    /// </summary>
    private static (List<Trade>, int) ParseOptionTrades(List<Dictionary<string, string>> rows, int seqStart)
    {
        // Identify strategy parent rows by their placed time
        var parentTimes = rows.Where(r => !string.IsNullOrEmpty(r.GetValueOrDefault("Name", "").Trim()) && string.IsNullOrEmpty(r.GetValueOrDefault("Symbol", "").Trim()) && !string.IsNullOrEmpty(r.GetValueOrDefault("Placed Time", "").Trim())).Select(r => r.GetValueOrDefault("Placed Time", "").Trim()).ToHashSet();

        // Build metadata for each strategy (root symbol, expiration, legs)
        var strategyMeta = BuildStrategyMeta(rows, parentTimes);

        var trades = new List<Trade>();
        var seq = seqStart;
        var strategySeqByPlacedTime = new Dictionary<string, int>();

        foreach (var row in rows)
        {
            var core = ExtractCoreFields(row);
            if (core == null)
                continue;

            var name = row.GetValueOrDefault("Name", "").Trim();
            var symbol = row.GetValueOrDefault("Symbol", "").Trim();
            var placed = row.GetValueOrDefault("Placed Time", "").Trim();

            // Strategy parent row (has name, no symbol)
            if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(symbol))
            {
                var meta = strategyMeta.GetValueOrDefault(placed, new StrategyMeta());
                trades.Add(BuildStrategyTrade(seq, core.Value, name, meta));
                strategySeqByPlacedTime[placed] = seq;
                seq++;
                continue;
            }

            // Strategy leg row (placed time matches a parent, no name)
            if (parentTimes.Contains(placed) && string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(symbol))
            {
                if (strategySeqByPlacedTime.TryGetValue(placed, out var parentSeq))
                {
                    trades.Add(BuildOptionTrade(seq++, core.Value, symbol, parentSeq));
                }
                continue;
            }

            // Standalone option trade
            if (!string.IsNullOrEmpty(symbol))
            {
                trades.Add(BuildOptionTrade(seq++, core.Value, symbol, parentStrategySeq: null));
            }
        }

        return (trades, seq);
    }

    /// <summary>
    /// Extracts and validates core trade fields (side, qty, price, timestamp).
    /// Returns null for non-filled or invalid rows.
    /// </summary>
    private static (string side, decimal qty, decimal price, DateTime timestamp)? ExtractCoreFields(Dictionary<string, string> row)
    {
        // Skip non-filled orders
        var status = row.GetValueOrDefault("Status", "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(status) && status != "filled")
            return null;

        // Normalize side to "Buy" or "Sell"
        var sideRaw = row.GetValueOrDefault("Side", "").Trim();
        if (string.IsNullOrEmpty(sideRaw))
            return null;

        var side = char.ToUpper(sideRaw[0]) + sideRaw[1..].ToLower();
        if (side is not (Sides.Buy or Sides.Sell))
            return null;

        var qty = ParsingHelpers.ParseDecimal(row.GetValueOrDefault("Filled", ""));
        if (qty is null or <= 0)
            return null;

        var price = ParsingHelpers.ParseDecimal(row.GetValueOrDefault("Avg Price") ?? row.GetValueOrDefault("Price"));
        if (price == null)
            return null;

        var timestamp = ParsingHelpers.ParseTime(row.GetValueOrDefault("Filled Time") ?? row.GetValueOrDefault("Placed Time") ?? "");
        if (timestamp == null)
            return null;

        return (side, qty.Value, price.Value, timestamp.Value);
    }

    /// <summary>
    /// Builds metadata for strategy orders by parsing their leg symbols.
    /// </summary>
    private static Dictionary<string, StrategyMeta> BuildStrategyMeta(List<Dictionary<string, string>> rows, HashSet<string> parentTimes)
    {
        return rows
            .Where(r => parentTimes.Contains(r.GetValueOrDefault("Placed Time", "").Trim()) && !string.IsNullOrEmpty(r.GetValueOrDefault("Symbol", "").Trim()))
            .Select(r => (placed: r.GetValueOrDefault("Placed Time", "").Trim(), parsed: ParsingHelpers.ParseOptionSymbol(r.GetValueOrDefault("Symbol", "").Trim())))
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

    private static Trade BuildStrategyTrade(int seq, (string side, decimal qty, decimal price, DateTime timestamp) core, string name, StrategyMeta meta)
    {
        var optionKind = ParsingHelpers.StrategyKindFromName(name);

        var instrument = (meta.Root, meta.ExpDate) switch
        {
            (not null, not null) => $"{meta.Root} {Formatters.FormatOptionDate(meta.ExpDate.Value)}",
            (not null, null) => meta.Root,
            (null, not null) => $"{name} {Formatters.FormatOptionDate(meta.ExpDate.Value)}",
            _ => name
        };

        return new Trade(Seq: seq, Timestamp: core.timestamp, Instrument: instrument, MatchKey: BuildStrategyKey(name, optionKind, meta), Asset: Assets.OptionStrategy, OptionKind: optionKind, Side: core.side, Qty: core.qty, Price: core.price, Multiplier: OptionMultiplier, Expiry: meta.ExpDate);
    }

    private static Trade BuildOptionTrade(int seq, (string side, decimal qty, decimal price, DateTime timestamp) core, string symbol, int? parentStrategySeq)
    {
        var parsed = ParsingHelpers.ParseOptionSymbol(symbol);

        var (instrument, optionKind, expiry) = parsed != null ? (Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike), parsed.CallPut == "C" ? "Call" : "Put", (DateTime?)parsed.ExpiryDate) : (symbol, "Option", null);

        return new Trade(Seq: seq, Timestamp: core.timestamp, Instrument: instrument, MatchKey: $"option:{symbol}", Asset: Assets.Option, OptionKind: optionKind, Side: core.side, Qty: core.qty, Price: core.price, Multiplier: OptionMultiplier, Expiry: expiry, ParentStrategySeq: parentStrategySeq);
    }

    private static Trade BuildStockTrade(int seq, (string side, decimal qty, decimal price, DateTime timestamp) core, string symbol)
    {
        return new Trade(Seq: seq, Timestamp: core.timestamp, Instrument: symbol, MatchKey: $"stock:{symbol}", Asset: Assets.Stock, OptionKind: "", Side: core.side, Qty: core.qty, Price: core.price, Multiplier: StockMultiplier, Expiry: null);
    }

    /// <summary>
    /// Parses a fee CSV file and returns a dictionary mapping (Timestamp, Side, Qty) to the fee amount.
    /// The fee file is expected to have columns: Symbol, Time, Side, Quantity, Avg Price, Amount, Fees.
    /// </summary>
    public static Dictionary<(DateTime timestamp, string side, decimal qty), decimal> ParseFeeCsv(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HeaderValidated = null, MissingFieldFound = null };

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord;

        if (headers == null || !headers.Contains("Fees"))
            return new();

        var result = new Dictionary<(DateTime, string, decimal), decimal>();

        while (csv.Read())
        {
            var timeStr = csv.GetField("Time")?.Trim() ?? "";
            var sideRaw = csv.GetField("Side")?.Trim() ?? "";
            var qtyStr = csv.GetField("Quantity")?.Trim() ?? "";
            var feeStr = csv.GetField("Fees")?.Trim() ?? "";

            var time = ParsingHelpers.ParseTime(timeStr);
            if (time == null) continue;

            if (string.IsNullOrEmpty(sideRaw)) continue;
            var side = char.ToUpper(sideRaw[0]) + sideRaw[1..].ToLower();
            if (side is not (Sides.Buy or Sides.Sell)) continue;

            var qty = ParsingHelpers.ParseDecimal(qtyStr);
            var fee = ParsingHelpers.ParseDecimal(feeStr);
            if (qty == null || fee == null || fee.Value <= 0) continue;

            var key = (time.Value, side, qty.Value);
            if (result.ContainsKey(key))
                result[key] += fee.Value;
            else
                result[key] = fee.Value;
        }

        return result;
    }

    private class StrategyMeta
    {
        public string? Root { get; set; }
        public DateTime? ExpDate { get; set; }
        public List<(string CallPut, decimal Strike)> Legs { get; set; } = new();
    }
}
