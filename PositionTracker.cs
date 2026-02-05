using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WebullAnalytics;

/// <summary>
/// Tracks option and stock positions, calculates realized P&L, and builds reports.
/// Uses FIFO (First-In-First-Out) accounting for matching lots.
/// </summary>
public static class PositionTracker
{
    private const string ExpireSide = "Expire";
    private static readonly TimeSpan ExpirationTime = new(23, 59, 59);

    /// <summary>
    /// Loads all trades from CSV files in the specified directory.
    /// Files are processed in alphabetical order to ensure consistent sequencing.
    /// </summary>
    public static List<Trade> LoadTrades(string dataDir)
    {
        var csvFiles = Directory.GetFiles(dataDir, "*.csv", SearchOption.AllDirectories)
            .OrderBy(f => f);

        var trades = new List<Trade>();
        var seq = 0;

        foreach (var path in csvFiles)
        {
            var (parsed, nextSeq) = CsvParser.ParseCsv(path, seq);
            trades.AddRange(parsed);
            seq = nextSeq;
        }

        return trades;
    }

    /// <summary>
    /// Computes the realized P&L report by processing all trades chronologically.
    /// Generates synthetic expiration trades for options that expired within the date range.
    /// </summary>
    /// <param name="trades">All trades loaded from CSV files</param>
    /// <param name="sinceDate">Only include trades on or after this date (DateTime.MinValue for all)</param>
    /// <returns>Report rows, final positions, and total realized P&L</returns>
    public static (List<ReportRow> rows, Dictionary<string, List<Lot>> positions, decimal running) ComputeReport(
        List<Trade> trades, DateTime sinceDate)
    {
        // Filter trades by date and add synthetic expiration trades
        var allTrades = trades
            .Where(t => t.Timestamp.Date >= sinceDate.Date)
            .Concat(BuildExpirationTrades(trades, sinceDate))
            .OrderBy(t => t.Timestamp)
            .ThenBy(t => t.Seq)
            .ToList();

        var positions = new Dictionary<string, List<Lot>>();
        var running = 0m;
        var rows = new List<ReportRow>();

        foreach (var trade in allTrades)
        {
            var (realized, closedQty, updatedPositions) = ProcessTrade(trade, positions, allTrades);
            positions = updatedPositions;

            var row = BuildReportRow(trade, realized, closedQty, ref running);
            if (row != null)
                rows.Add(row);
        }

        return (rows, positions, running);
    }

    /// <summary>
    /// Processes a single trade and returns the realized P&L and updated positions.
    /// Handles regular trades, strategy parents, and strategy legs differently.
    /// </summary>
    private static (decimal realized, decimal closedQty, Dictionary<string, List<Lot>> positions) ProcessTrade(
        Trade trade, Dictionary<string, List<Lot>> positions, List<Trade> allTrades)
    {
        var isStrategyParent = trade.Asset == "Option Strategy";

        if (!isStrategyParent)
        {
            // Regular trade (stock, option, or strategy leg) - track position normally
            var (updatedPositions, realized, closedQty) = ApplyTrade(positions, trade);
            return (realized, closedQty, updatedPositions);
        }

        // Strategy parent: calculate P&L but track position separately
        var lots = positions.GetValueOrDefault(trade.MatchKey, new List<Lot>());
        var (_, realized2, closedQty2) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);

        // For SELL strategies with no direct P&L, calculate from legs (closing a spread)
        // For BUY strategies (opening/rolling), don't count leg P&L - cost is in the spread price
        if (realized2 == 0m && closedQty2 == 0m && trade.Side == "Sell")
        {
            realized2 = allTrades
                .Where(t => t.ParentStrategySeq == trade.Seq)
                .Sum(leg =>
                {
                    var legLots = positions.GetValueOrDefault(leg.MatchKey, new List<Lot>());
                    var (_, legPnl, _) = ApplyToLots(legLots, leg.Side, leg.Qty, leg.Price, leg.Multiplier);
                    return legPnl;
                });
        }

        // Update strategy parent position
        var (updatedLots, _, _) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);
        var updatedPositions2 = new Dictionary<string, List<Lot>>(positions);

        if (updatedLots.Count > 0)
            updatedPositions2[trade.MatchKey] = updatedLots;
        else
            updatedPositions2.Remove(trade.MatchKey);

        return (realized2, closedQty2, updatedPositions2);
    }

    /// <summary>
    /// Builds a report row for the given trade. Returns null if the row should be skipped.
    /// Strategy legs are shown but don't affect running P&L.
    /// </summary>
    private static ReportRow? BuildReportRow(Trade trade, decimal realized, decimal closedQty, ref decimal running)
    {
        var optionKind = string.IsNullOrEmpty(trade.OptionKind) ? "-" : trade.OptionKind;
        var isLeg = trade.ParentStrategySeq.HasValue;

        // Strategy legs: show in report but don't affect running P&L
        if (isLeg)
        {
            return new ReportRow(
                trade.Timestamp, trade.Instrument, trade.Asset, optionKind,
                trade.Side, trade.Qty, trade.Price,
                ClosedQty: 0m, Realized: 0m, running,
                IsStrategyLeg: true
            );
        }

        // Skip expirations that didn't close any positions
        if (trade.Side == ExpireSide && closedQty == 0)
            return null;

        // Regular trade or strategy parent: update running P&L
        running += realized;
        var displayQty = trade.Side == ExpireSide ? closedQty : trade.Qty;

        return new ReportRow(
            trade.Timestamp, trade.Instrument, trade.Asset, optionKind,
            trade.Side, displayQty, trade.Price,
            closedQty, realized, running,
            IsStrategyLeg: false
        );
    }

    /// <summary>
    /// Creates synthetic expiration trades for options that expired on or before the since date.
    /// These trades close out any remaining positions at $0.
    /// </summary>
    private static List<Trade> BuildExpirationTrades(List<Trade> trades, DateTime sinceDate)
    {
        if (!trades.Any())
            return new List<Trade>();

        var maxSeq = trades.Max(t => t.Seq);

        // Get one trade per unique option (to extract metadata for expiration trade)
        var uniqueOptions = trades
            .Where(t => t.Expiry.HasValue && t.Expiry.Value.Date <= sinceDate.Date)
            .GroupBy(t => t.MatchKey)
            .Select(g => g.First())
            .ToList();

        return uniqueOptions
            .Select((trade, index) => new Trade(
                Seq: maxSeq + index + 1,
                Timestamp: trade.Expiry!.Value.Date + ExpirationTime,
                trade.Instrument,
                trade.MatchKey,
                trade.Asset,
                trade.OptionKind,
                Side: ExpireSide,
                Qty: 0m,
                Price: 0m,
                trade.Multiplier,
                trade.Expiry
            ))
            .ToList();
    }

    /// <summary>
    /// Applies a trade to the current positions using FIFO accounting.
    /// Returns updated positions, realized P&L, and quantity closed.
    /// </summary>
    private static (Dictionary<string, List<Lot>>, decimal realized, decimal closedQty) ApplyTrade(
        Dictionary<string, List<Lot>> positions, Trade trade)
    {
        var lots = positions.GetValueOrDefault(trade.MatchKey, new List<Lot>());

        var (updatedLots, realized, closedQty) = trade.Side == ExpireSide
            ? ApplyExpiration(lots, trade.Multiplier)
            : ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);

        if (updatedLots.SequenceEqual(lots))
            return (positions, realized, closedQty);

        var updatedPositions = new Dictionary<string, List<Lot>>(positions);

        if (updatedLots.Count > 0)
            updatedPositions[trade.MatchKey] = updatedLots;
        else
            updatedPositions.Remove(trade.MatchKey);

        return (updatedPositions, realized, closedQty);
    }

    /// <summary>
    /// Closes all lots at expiration (price = $0).
    /// Long positions lose their full value; short positions gain their full value.
    /// </summary>
    private static (List<Lot>, decimal realized, decimal closedQty) ApplyExpiration(List<Lot> lots, decimal multiplier)
    {
        if (!lots.Any())
            return (new List<Lot>(), 0m, 0m);

        var realized = lots.Sum(lot =>
            lot.Side == "Buy"
                ? -lot.Price * lot.Qty * multiplier  // Long position expires worthless
                : lot.Price * lot.Qty * multiplier   // Short position keeps premium
        );

        var closedQty = lots.Sum(lot => lot.Qty);

        return (new List<Lot>(), realized, closedQty);
    }

    /// <summary>
    /// Applies a buy/sell trade to existing lots using FIFO matching.
    /// Opposite-side lots are closed first, then remaining quantity opens new position.
    /// </summary>
    private static (List<Lot>, decimal realized, decimal closedQty) ApplyToLots(
        List<Lot> lots, string tradeSide, decimal tradeQty, decimal tradePrice, decimal multiplier)
    {
        var remaining = tradeQty;
        var realized = 0m;
        var closedQty = 0m;
        var updated = new List<Lot>();

        foreach (var lot in lots)
        {
            // Match against opposite-side lots
            if (remaining > 0 && lot.Side != tradeSide)
            {
                var matchQty = Math.Min(remaining, lot.Qty);

                // P&L = (exit price - entry price) * qty * multiplier
                // For buys closing shorts: lot.Price - tradePrice (we sold high, bought low)
                // For sells closing longs: tradePrice - lot.Price (we bought low, sold high)
                var pnlPerContract = tradeSide == "Buy"
                    ? lot.Price - tradePrice
                    : tradePrice - lot.Price;

                realized += pnlPerContract * matchQty * multiplier;
                closedQty += matchQty;
                remaining -= matchQty;

                // Keep leftover quantity in the lot
                var leftover = lot.Qty - matchQty;
                if (leftover > 0)
                    updated.Add(lot with { Qty = leftover });
            }
            else
            {
                updated.Add(lot);
            }
        }

        // Add remaining quantity as new lot
        if (remaining > 0)
            updated.Add(new Lot(tradeSide, remaining, tradePrice));

        return (updated, realized, closedQty);
    }

    /// <summary>
    /// Builds position rows for display, grouping options into calendar spreads where applicable.
    /// </summary>
    public static List<PositionRow> BuildPositionRows(
        Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
    {
        var allPositions = BuildRawPositionRows(positions, tradeIndex);
        var grouped = GroupIntoStrategies(allPositions);
        return BuildFinalPositionRows(grouped, allTrades);
    }

    /// <summary>
    /// Converts raw position data (lots) into position rows with calculated averages.
    /// </summary>
    private static List<(string matchKey, PositionRow row, Trade? trade)> BuildRawPositionRows(
        Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex)
    {
        var result = new List<(string matchKey, PositionRow row, Trade? trade)>();

        foreach (var (matchKey, lots) in positions)
        {
            if (!lots.Any() || lots.Sum(l => l.Qty) <= 0)
                continue;

            var trade = tradeIndex.GetValueOrDefault(matchKey);

            // Skip strategy parents - show legs only
            if (trade?.Asset == "Option Strategy")
                continue;

            var totalQty = lots.Sum(l => l.Qty);
            var avgPrice = lots.Sum(l => l.Price * l.Qty) / totalQty;

            var row = new PositionRow(
                Instrument: trade?.Instrument ?? matchKey,
                Asset: trade?.Asset ?? "-",
                OptionKind: !string.IsNullOrEmpty(trade?.OptionKind) ? trade.OptionKind : "-",
                Side: lots[0].Side,
                Qty: totalQty,
                AvgPrice: avgPrice,
                Expiry: trade?.Expiry,
                IsStrategyLeg: false
            );

            result.Add((matchKey, row, trade));
        }

        return result;
    }

    /// <summary>
    /// Groups option positions into calendar spreads by matching long/short legs
    /// with the same underlying, strike, and call/put type but different expirations.
    /// Handles partial rolls by creating separate calendars for different quantities.
    /// </summary>
    private static List<List<(string matchKey, PositionRow row, Trade? trade)>> GroupIntoStrategies(
        List<(string matchKey, PositionRow row, Trade? trade)> allPositions)
    {
        var grouped = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();
        var processed = new HashSet<string>();

        // Parse and group options by underlying/strike/type
        var optionsByKey = allPositions
            .Where(p => p.trade?.Asset == "Option")
            .Select(p => (
                pos: p,
                parsed: p.matchKey.StartsWith("option:")
                    ? ParsingHelpers.ParseOptionSymbol(p.matchKey[7..])
                    : null
            ))
            .Where(x => x.parsed != null)
            .GroupBy(x => $"{x.parsed!.Root}|{x.parsed.Strike}|{x.parsed.CallPut}")
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (key, legs) in optionsByKey)
        {
            var calendarGroups = MatchCalendarLegs(legs, processed);
            grouped.AddRange(calendarGroups);
        }

        // Add unprocessed options as standalone
        grouped.AddRange(allPositions
            .Where(p => p.trade?.Asset == "Option" && !processed.Contains(p.matchKey))
            .Select(p =>
            {
                processed.Add(p.matchKey);
                return new List<(string, PositionRow, Trade?)> { p };
            }));

        // Add non-option positions
        grouped.AddRange(allPositions
            .Where(p => p.trade?.Asset != "Option")
            .Select(p => new List<(string, PositionRow, Trade?)> { p }));

        // Sort by asset type, then instrument
        grouped.Sort((a, b) =>
        {
            var cmp = string.Compare(a[0].row.Asset, b[0].row.Asset, StringComparison.Ordinal);
            return cmp != 0 ? cmp : string.Compare(a[0].row.Instrument, b[0].row.Instrument, StringComparison.Ordinal);
        });

        return grouped;
    }

    /// <summary>
    /// Matches long and short legs into calendar spreads by quantity.
    /// Creates separate calendar groups when quantities don't match exactly (partial rolls).
    /// </summary>
    private static List<List<(string matchKey, PositionRow row, Trade? trade)>> MatchCalendarLegs(
        List<(
            (string matchKey, PositionRow row, Trade? trade) pos,
            OptionParsed? parsed
        )> legs,
        HashSet<string> processed)
    {
        var result = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();

        // Separate and sort legs: longs by expiry desc (furthest first), shorts by expiry asc (nearest first)
        var longLegs = legs.Where(l => l.pos.row.Side == "Buy").OrderByDescending(l => l.parsed!.ExpiryDate).ToList();
        var shortLegs = legs.Where(l => l.pos.row.Side == "Sell").OrderBy(l => l.parsed!.ExpiryDate).ToList();

        if (!longLegs.Any() || !shortLegs.Any())
        {
            // No calendar possible - add all as standalone
            foreach (var leg in legs.Where(l => !processed.Contains(l.pos.matchKey)))
            {
                processed.Add(leg.pos.matchKey);
                result.Add(new List<(string, PositionRow, Trade?)> { leg.pos });
            }
            return result;
        }

        // Track remaining quantities for matching
        var longRemaining = longLegs.ToDictionary(l => l.pos.matchKey, l => l.pos.row.Qty);
        var shortRemaining = shortLegs.ToDictionary(l => l.pos.matchKey, l => l.pos.row.Qty);

        // Match each short leg with available long legs
        foreach (var shortLeg in shortLegs)
        {
            var shortQty = shortRemaining[shortLeg.pos.matchKey];
            if (shortQty <= 0 || processed.Contains(shortLeg.pos.matchKey))
                continue;

            foreach (var longLeg in longLegs)
            {
                var longQty = longRemaining[longLeg.pos.matchKey];
                if (longQty <= 0)
                    continue;

                var matchedQty = Math.Min(longQty, shortQty);

                // Create calendar with matched quantities
                result.Add(new List<(string, PositionRow, Trade?)>
                {
                    (longLeg.pos.matchKey, longLeg.pos.row with { Qty = matchedQty }, longLeg.pos.trade),
                    (shortLeg.pos.matchKey, shortLeg.pos.row with { Qty = matchedQty }, shortLeg.pos.trade)
                });

                longRemaining[longLeg.pos.matchKey] -= matchedQty;
                shortRemaining[shortLeg.pos.matchKey] -= matchedQty;
                shortQty -= matchedQty;

                if (shortQty <= 0)
                {
                    processed.Add(shortLeg.pos.matchKey);
                    break;
                }
            }
        }

        // Mark fully consumed legs as processed
        foreach (var leg in longLegs.Where(l => longRemaining[l.pos.matchKey] <= 0))
            processed.Add(leg.pos.matchKey);

        // Add unmatched long leg remainders as standalone
        foreach (var longLeg in longLegs)
        {
            var remaining = longRemaining[longLeg.pos.matchKey];
            if (remaining > 0 && !processed.Contains(longLeg.pos.matchKey))
            {
                processed.Add(longLeg.pos.matchKey);
                result.Add(new List<(string, PositionRow, Trade?)>
                {
                    (longLeg.pos.matchKey, longLeg.pos.row with { Qty = remaining }, longLeg.pos.trade)
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the final position rows, creating strategy summary rows for multi-leg positions
    /// and calculating adjusted prices based on credits from rolled legs.
    /// </summary>
    private static List<PositionRow> BuildFinalPositionRows(
        List<List<(string matchKey, PositionRow row, Trade? trade)>> grouped,
        List<Trade> allTrades)
    {
        var rows = new List<PositionRow>();

        foreach (var group in grouped)
        {
            if (group.Count > 1)
            {
                var strategyRows = BuildStrategyRows(group, allTrades);
                rows.AddRange(strategyRows);
            }
            else
            {
                rows.Add(group[0].row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Builds rows for a multi-leg strategy (calendar spread).
    /// Creates a summary row plus individual leg rows with adjusted prices.
    /// </summary>
    private static List<PositionRow> BuildStrategyRows(
        List<(string matchKey, PositionRow row, Trade? trade)> group,
        List<Trade> allTrades)
    {
        var rows = new List<PositionRow>();
        var firstLeg = group[0];

        var symbol = firstLeg.matchKey.StartsWith("option:") ? firstLeg.matchKey[7..] : null;
        var parsed = symbol != null ? ParsingHelpers.ParseOptionSymbol(symbol) : null;

        if (parsed == null)
        {
            rows.AddRange(group.Select(g => g.row));
            return rows;
        }

        var qty = group[0].row.Qty;
        var side = group.Any(g => g.row.Side == "Buy") ? "Buy" : "Sell";

        // Calculate credits from previously closed short legs (rolls)
        var closedCredits = CalculateClosedLegsCredits(allTrades, parsed.Root, parsed.Strike, parsed.CallPut);

        // Calculate net prices (long - short)
        var (netInitial, netAdjusted) = CalculateNetPrices(group, closedCredits);
        var longestExpiry = group.Max(g => g.row.Expiry) ?? DateTime.MinValue;

        // Strategy summary row
        rows.Add(new PositionRow(
            Instrument: $"{parsed.Root} {Formatters.FormatOptionDate(longestExpiry)}",
            Asset: "Option Strategy",
            OptionKind: "Calendar",
            Side: side,
            Qty: qty,
            AvgPrice: Math.Abs(netAdjusted),
            Expiry: longestExpiry,
            IsStrategyLeg: false,
            InitialAvgPrice: Math.Abs(netInitial),
            AdjustedAvgPrice: Math.Abs(netAdjusted)
        ));

        // Add leg rows with adjusted prices (sorted by expiry descending)
        foreach (var leg in group.OrderByDescending(g => g.row.Expiry))
        {
            var initialPrice = leg.row.AvgPrice;
            var adjustedPrice = initialPrice;

            // Reduce long leg cost basis by credits from closed short legs
            if (leg.row.Side == "Buy" && closedCredits > 0)
            {
                var creditPerShare = closedCredits / (leg.row.Qty * 100m);
                adjustedPrice = initialPrice - creditPerShare;
            }

            rows.Add(leg.row with
            {
                IsStrategyLeg = true,
                InitialAvgPrice = initialPrice,
                AdjustedAvgPrice = adjustedPrice
            });
        }

        return rows;
    }

    /// <summary>
    /// Calculates net initial and adjusted prices for a calendar spread.
    /// Net price = sum of long prices - sum of short prices.
    /// </summary>
    private static (decimal initial, decimal adjusted) CalculateNetPrices(
        List<(string matchKey, PositionRow row, Trade? trade)> group,
        decimal closedCredits)
    {
        var netInitial = 0m;
        var netAdjusted = 0m;

        foreach (var leg in group)
        {
            var initial = leg.row.AvgPrice;
            var adjusted = initial;

            if (leg.row.Side == "Buy" && closedCredits > 0)
            {
                var creditPerShare = closedCredits / (leg.row.Qty * 100m);
                adjusted = initial - creditPerShare;
            }

            if (leg.row.Side == "Buy")
            {
                netInitial += initial;
                netAdjusted += adjusted;
            }
            else
            {
                netInitial -= initial;
                netAdjusted -= adjusted;
            }
        }

        return (netInitial, netAdjusted);
    }

    /// <summary>
    /// Calculates total credits from fully closed short legs for a given underlying/strike/type.
    /// Used to adjust the cost basis of long legs in calendar rolls.
    /// </summary>
    private static decimal CalculateClosedLegsCredits(List<Trade> allTrades, string root, decimal strike, string callPut)
    {
        // Find all strategy leg trades matching this underlying/strike/type
        var matchingLegs = allTrades
            .Where(t => t.Asset == "Option" && t.ParentStrategySeq.HasValue)
            .Select(t => (
                trade: t,
                parsed: t.MatchKey.StartsWith("option:")
                    ? ParsingHelpers.ParseOptionSymbol(t.MatchKey[7..])
                    : null
            ))
            .Where(x => x.parsed != null &&
                        x.parsed.Root == root &&
                        x.parsed.Strike == strike &&
                        x.parsed.CallPut == callPut)
            .OrderBy(x => x.trade.Timestamp)
            .ToList();

        // Track P&L by expiration date
        var pnlByExpiry = new Dictionary<DateTime, (List<Lot> lots, decimal pnl)>();

        foreach (var (trade, parsed) in matchingLegs)
        {
            if (!pnlByExpiry.ContainsKey(parsed!.ExpiryDate))
                pnlByExpiry[parsed.ExpiryDate] = (new List<Lot>(), 0m);

            var (lots, totalPnl) = pnlByExpiry[parsed.ExpiryDate];
            var (updatedLots, realized, _) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);
            pnlByExpiry[parsed.ExpiryDate] = (updatedLots, totalPnl + realized);
        }

        // Sum credits from fully closed expirations (no remaining lots, positive P&L)
        return pnlByExpiry
            .Where(kvp => !kvp.Value.lots.Any() && kvp.Value.pnl > 0)
            .Sum(kvp => kvp.Value.pnl);
    }

    /// <summary>
    /// Builds an index mapping match keys to their first trade (for metadata lookup).
    /// </summary>
    public static Dictionary<string, Trade> BuildTradeIndex(IEnumerable<Trade> trades) =>
        trades
            .GroupBy(t => t.MatchKey)
            .ToDictionary(g => g.Key, g => g.First());
}
