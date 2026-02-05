using System;
using System.Collections.Generic;
using System.Linq;

namespace WebullAnalytics;

public static class PositionTracker
{
    private const string ExpireSide = "Expire";
    private static readonly TimeSpan ExpirationTime = new(23, 59, 59);

    public static List<Trade> LoadTrades(string dataDir)
    {
        var trades = new List<Trade>();
        var seq = 0;

        var csvFiles = Directory.GetFiles(dataDir, "*.csv", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        foreach (var path in csvFiles)
        {
            var (parsed, nextSeq) = CsvParser.ParseCsv(path, seq);
            trades.AddRange(parsed);
            seq = nextSeq;
        }

        return trades;
    }

    public static (List<ReportRow> rows, Dictionary<string, List<Lot>> positions, decimal running) ComputeReport(
        List<Trade> trades, DateTime asOfDate)
    {
        var allTrades = new List<Trade>(trades);
        allTrades.AddRange(BuildExpirationTrades(trades, asOfDate));
        allTrades.Sort((a, b) =>
        {
            var cmp = a.Timestamp.CompareTo(b.Timestamp);
            return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq);
        });

        var positions = new Dictionary<string, List<Lot>>();
        var running = 0m;
        var rows = new List<ReportRow>();

        foreach (var trade in allTrades)
        {
            var (updatedPositions, realized, closedQty) = ApplyTrade(positions, trade);
            positions = updatedPositions;

            if (trade.Side == ExpireSide && closedQty == 0)
                continue;

            running += realized;
            var displayQty = trade.Side == ExpireSide ? closedQty : trade.Qty;

            rows.Add(new ReportRow(
                trade.Timestamp,
                trade.Instrument,
                trade.Asset,
                string.IsNullOrEmpty(trade.OptionKind) ? "-" : trade.OptionKind,
                trade.Side,
                displayQty,
                trade.Price,
                closedQty,
                realized,
                running
            ));
        }

        return (rows, positions, running);
    }

    private static List<Trade> BuildExpirationTrades(List<Trade> trades, DateTime asOfDate)
    {
        if (trades.Count == 0)
            return new List<Trade>();

        var maxSeq = trades.Max(t => t.Seq);
        var seen = new Dictionary<string, Trade>();

        foreach (var trade in trades)
        {
            if (trade.Expiry == null || trade.Expiry.Value.Date > asOfDate.Date)
                continue;

            if (!seen.ContainsKey(trade.MatchKey))
                seen[trade.MatchKey] = trade;
        }

        var expirations = new List<Trade>();
        var seq = maxSeq + 1;

        foreach (var trade in seen.Values)
        {
            if (trade.Expiry == null)
                continue;

            var expirationTime = trade.Expiry.Value.Date + ExpirationTime;

            expirations.Add(new Trade(
                seq,
                expirationTime,
                trade.Instrument,
                trade.MatchKey,
                trade.Asset,
                trade.OptionKind,
                ExpireSide,
                0m,
                0m,
                trade.Multiplier,
                trade.Expiry
            ));
            seq++;
        }

        return expirations;
    }

    private static (Dictionary<string, List<Lot>>, decimal realized, decimal closedQty) ApplyTrade(
        Dictionary<string, List<Lot>> positions, Trade trade)
    {
        var lots = positions.GetValueOrDefault(trade.MatchKey, new List<Lot>());

        List<Lot> updatedLots;
        decimal realized;
        decimal closedQty;

        if (trade.Side == ExpireSide)
        {
            (updatedLots, realized, closedQty) = ApplyExpiration(lots, trade.Multiplier);
        }
        else
        {
            (updatedLots, realized, closedQty) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);
        }

        if (updatedLots.SequenceEqual(lots))
            return (positions, realized, closedQty);

        var updatedPositions = new Dictionary<string, List<Lot>>(positions);

        if (updatedLots.Count > 0)
            updatedPositions[trade.MatchKey] = updatedLots;
        else
            updatedPositions.Remove(trade.MatchKey);

        return (updatedPositions, realized, closedQty);
    }

    private static (List<Lot>, decimal realized, decimal closedQty) ApplyExpiration(List<Lot> lots, decimal multiplier)
    {
        if (lots.Count == 0)
            return (new List<Lot>(), 0m, 0m);

        var realized = 0m;
        var closedQty = 0m;

        foreach (var lot in lots)
        {
            if (lot.Side == "Buy")
                realized += (0m - lot.Price) * lot.Qty * multiplier;
            else
                realized += (lot.Price - 0m) * lot.Qty * multiplier;

            closedQty += lot.Qty;
        }

        return (new List<Lot>(), realized, closedQty);
    }

    private static (List<Lot>, decimal realized, decimal closedQty) ApplyToLots(
        List<Lot> lots, string tradeSide, decimal tradeQty, decimal tradePrice, decimal multiplier)
    {
        var remaining = tradeQty;
        var realized = 0m;
        var closedQty = 0m;
        var updated = new List<Lot>();

        foreach (var lot in lots)
        {
            if (remaining > 0 && lot.Side != tradeSide)
            {
                var match = Math.Min(remaining, lot.Qty);
                var pnlPerContract = tradeSide == "Buy"
                    ? lot.Price - tradePrice
                    : tradePrice - lot.Price;

                realized += pnlPerContract * match * multiplier;
                closedQty += match;
                remaining -= match;

                var leftover = lot.Qty - match;
                if (leftover > 0)
                    updated.Add(new Lot(lot.Side, leftover, lot.Price));
            }
            else
            {
                updated.Add(lot);
            }
        }

        if (remaining > 0)
            updated.Add(new Lot(tradeSide, remaining, tradePrice));

        return (updated, realized, closedQty);
    }

    public static List<PositionRow> BuildPositionRows(
        Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex)
    {
        var rows = new List<PositionRow>();

        foreach (var (matchKey, lots) in positions)
        {
            if (lots.Count == 0)
                continue;

            var totalQty = lots.Sum(l => l.Qty);
            if (totalQty <= 0)
                continue;

            var weighted = lots.Sum(l => l.Price * l.Qty);
            var avgPrice = weighted / totalQty;

            var trade = tradeIndex.GetValueOrDefault(matchKey);
            var instrument = trade?.Instrument ?? matchKey;
            var asset = trade?.Asset ?? "-";
            var optionKind = trade != null && !string.IsNullOrEmpty(trade.OptionKind) ? trade.OptionKind : "-";
            var expiry = trade?.Expiry;

            rows.Add(new PositionRow(
                instrument, asset, optionKind, lots[0].Side, totalQty, avgPrice, expiry
            ));
        }

        rows.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Asset, b.Asset, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            cmp = string.Compare(a.Instrument, b.Instrument, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            return string.Compare(a.Side, b.Side, StringComparison.Ordinal);
        });

        return rows;
    }

    public static Dictionary<string, Trade> BuildTradeIndex(IEnumerable<Trade> trades)
    {
        var index = new Dictionary<string, Trade>();

        foreach (var trade in trades)
        {
            if (!index.ContainsKey(trade.MatchKey))
                index[trade.MatchKey] = trade;
        }

        return index;
    }
}
