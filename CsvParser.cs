using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;

namespace WebullAnalytics;

public static class CsvParser
{
    private const decimal OptionMultiplier = 100m;
    private const decimal StockMultiplier = 1m;

    public static (List<Trade> trades, int nextSeq) ParseCsv(string path, int seqStart)
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

        if (headers == null || !headers.Contains("Side") || !headers.Contains("Filled"))
            return (new List<Trade>(), seqStart);

        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>();
            foreach (var header in headers)
            {
                row[header] = csv.GetField(header) ?? "";
            }
            rows.Add(row);
        }

        var isOptions = Path.GetFileName(path).Contains("Options", StringComparison.OrdinalIgnoreCase);
        var parentTimes = new HashSet<string>();

        if (isOptions)
        {
            foreach (var row in rows)
            {
                var name = row.GetValueOrDefault("Name", "").Trim();
                var symbol = row.GetValueOrDefault("Symbol", "").Trim();

                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(symbol))
                {
                    var placed = row.GetValueOrDefault("Placed Time", "").Trim();
                    if (!string.IsNullOrEmpty(placed))
                        parentTimes.Add(placed);
                }
            }
        }

        var strategyMeta = isOptions ? BuildStrategyMeta(rows, parentTimes) : new Dictionary<string, StrategyMeta>();

        var trades = new List<Trade>();
        var seq = seqStart;

        foreach (var row in rows)
        {
            var core = ExtractCoreFields(row);
            if (core == null)
                continue;

            var (side, qty, price, timestamp) = core.Value;

            if (isOptions)
            {
                var name = row.GetValueOrDefault("Name", "").Trim();
                var symbol = row.GetValueOrDefault("Symbol", "").Trim();
                var placed = row.GetValueOrDefault("Placed Time", "").Trim();

                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(symbol))
                {
                    var meta = strategyMeta.GetValueOrDefault(placed, new StrategyMeta());
                    trades.Add(BuildStrategyTrade(seq, timestamp, name, side, qty, price, meta));
                    seq++;
                    continue;
                }

                if (parentTimes.Contains(placed) && string.IsNullOrEmpty(name))
                    continue;

                if (!string.IsNullOrEmpty(symbol))
                {
                    trades.Add(BuildOptionTrade(seq, timestamp, symbol, side, qty, price));
                    seq++;
                }

                continue;
            }

            var stockSymbol = row.GetValueOrDefault("Symbol", "").Trim();
            if (string.IsNullOrEmpty(stockSymbol))
                continue;

            trades.Add(BuildStockTrade(seq, timestamp, stockSymbol, side, qty, price));
            seq++;
        }

        return (trades, seq);
    }

    private static (string side, decimal qty, decimal price, DateTime timestamp)? ExtractCoreFields(Dictionary<string, string> row)
    {
        var status = row.GetValueOrDefault("Status", "").Trim().ToLower();
        if (!string.IsNullOrEmpty(status) && status != "filled")
            return null;

        var side = row.GetValueOrDefault("Side", "").Trim();
        side = char.ToUpper(side[0]) + side[1..].ToLower();

        if (side != "Buy" && side != "Sell")
            return null;

        var qty = ParsingHelpers.ParseDecimal(row.GetValueOrDefault("Filled", ""));
        if (qty == null || qty <= 0)
            return null;

        var price = ParsingHelpers.ParseDecimal(row.GetValueOrDefault("Avg Price") ?? row.GetValueOrDefault("Price"));
        if (price == null)
            return null;

        var timeVal = row.GetValueOrDefault("Filled Time") ?? row.GetValueOrDefault("Placed Time") ?? "";
        var timestamp = ParsingHelpers.ParseTime(timeVal);
        if (timestamp == null)
            return null;

        return (side, qty.Value, price.Value, timestamp.Value);
    }

    private static Dictionary<string, StrategyMeta> BuildStrategyMeta(List<Dictionary<string, string>> rows, HashSet<string> parentTimes)
    {
        var meta = new Dictionary<string, StrategyMeta>();

        foreach (var row in rows)
        {
            var placed = row.GetValueOrDefault("Placed Time", "").Trim();
            var symbol = row.GetValueOrDefault("Symbol", "").Trim();

            if (!parentTimes.Contains(placed) || string.IsNullOrEmpty(symbol))
                continue;

            var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
            if (parsed == null)
                continue;

            if (!meta.ContainsKey(placed))
                meta[placed] = new StrategyMeta();

            var entry = meta[placed];
            entry.Root ??= parsed.Root;
            entry.ExpDate ??= parsed.ExpiryDate;
            entry.Legs.Add((parsed.CallPut, parsed.Strike));
        }

        return meta;
    }

    private static string BuildStrategyKey(string name, string optionKind, string? root, DateTime? expDate, List<(string, decimal)>? legs)
    {
        if (root == null || expDate == null)
            return $"strategy:{name}";

        var key = $"strategy:{optionKind}:{root}:{expDate:yyyy-MM-dd}";

        if (legs != null && legs.Count > 0)
        {
            var legsKey = string.Join(",",
                legs.OrderBy(v => v.Item1).ThenBy(v => v.Item2)
                    .Select(v => $"{v.Item1}{Formatters.FormatQty(v.Item2)}"));
            key = $"{key}:{legsKey}";
        }

        return key;
    }

    private static Trade BuildStrategyTrade(int seq, DateTime timestamp, string name, string side, decimal qty, decimal price, StrategyMeta meta)
    {
        var optionKind = ParsingHelpers.StrategyKindFromName(name);
        var root = meta.Root;
        var expDate = meta.ExpDate;
        var legs = meta.Legs.Count > 0 ? meta.Legs : null;

        var instrument = name;
        if (root != null && expDate != null)
            instrument = $"{root} {Formatters.FormatOptionDate(expDate.Value)}";
        else if (root != null)
            instrument = root;
        else if (expDate != null)
            instrument = $"{name} {Formatters.FormatOptionDate(expDate.Value)}";

        var matchKey = BuildStrategyKey(name, optionKind, root, expDate, legs);

        return new Trade(
            seq, timestamp, instrument, matchKey, "Option Strategy", optionKind,
            side, qty, price, OptionMultiplier, expDate
        );
    }

    private static Trade BuildOptionTrade(int seq, DateTime timestamp, string symbol, string side, decimal qty, decimal price)
    {
        var parsed = ParsingHelpers.ParseOptionSymbol(symbol);

        string instrument;
        string optionKind;
        DateTime? expiry;

        if (parsed != null)
        {
            optionKind = parsed.CallPut == "C" ? "Call" : "Put";
            instrument = Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike);
            expiry = parsed.ExpiryDate;
        }
        else
        {
            optionKind = "Option";
            instrument = symbol;
            expiry = null;
        }

        return new Trade(
            seq, timestamp, instrument, $"option:{symbol}", "Option", optionKind,
            side, qty, price, OptionMultiplier, expiry
        );
    }

    private static Trade BuildStockTrade(int seq, DateTime timestamp, string symbol, string side, decimal qty, decimal price)
    {
        return new Trade(
            seq, timestamp, symbol, $"stock:{symbol}", "Stock", "",
            side, qty, price, StockMultiplier, null
        );
    }

    private class StrategyMeta
    {
        public string? Root { get; set; }
        public DateTime? ExpDate { get; set; }
        public List<(string, decimal)> Legs { get; set; } = new();
    }
}
