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
        List<Trade> trades, DateTime sinceDate)
    {
        var allTrades = new List<Trade>(trades.Where(t => t.Timestamp.Date >= sinceDate.Date));
        allTrades.AddRange(BuildExpirationTrades(trades, sinceDate));
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
            var isLeg = trade.ParentStrategySeq.HasValue;
            var isStrategyParent = trade.Asset == "Option Strategy";

            decimal realized = 0m;
            decimal closedQty = 0m;

            // Only track positions for:
            // 1. Non-strategy trades (regular stocks/options)
            // 2. Strategy parent trades (for P&L calculation)
            // 3. Strategy leg trades (for actual position tracking)
            // But SKIP position tracking for strategy parents since they're just for P&L
            if (!isStrategyParent)
            {
                var (updatedPositions, realizedPnl, closedQuantity) = ApplyTrade(positions, trade);
                positions = updatedPositions;
                realized = realizedPnl;
                closedQty = closedQuantity;
            }
            else
            {
                // For strategy parents, calculate P&L but don't track position
                var lots = positions.GetValueOrDefault(trade.MatchKey, new List<Lot>());
                var (_, realizedPnl, closedQuantity) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);
                realized = realizedPnl;
                closedQty = closedQuantity;

                // If parent strategy has no P&L (like in a roll), calculate P&L from legs
                // BUT only for SELL strategies (credit spreads closing).
                // For BUY strategies (debit spreads), the cost is already reflected in opening the position
                if (realized == 0m && closedQty == 0m && trade.Side == "Sell")
                {
                    // Find all legs for this parent strategy
                    var legs = allTrades.Where(t => t.ParentStrategySeq == trade.Seq).ToList();
                    foreach (var leg in legs)
                    {
                        var legLots = positions.GetValueOrDefault(leg.MatchKey, new List<Lot>());
                        var (_, legRealizedPnl, _) = ApplyToLots(legLots, leg.Side, leg.Qty, leg.Price, leg.Multiplier);
                        realized += legRealizedPnl;
                    }
                }

                // Update positions for strategy parent (even though we won't show it in open positions)
                var (updatedLots, _, _) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);
                var updatedPositions = new Dictionary<string, List<Lot>>(positions);
                if (updatedLots.Count > 0)
                    updatedPositions[trade.MatchKey] = updatedLots;
                else
                    updatedPositions.Remove(trade.MatchKey);
                positions = updatedPositions;
            }

            // For leg trades, show in report but don't add to P&L
            if (isLeg)
            {
                rows.Add(new ReportRow(
                    trade.Timestamp,
                    trade.Instrument,
                    trade.Asset,
                    string.IsNullOrEmpty(trade.OptionKind) ? "-" : trade.OptionKind,
                    trade.Side,
                    trade.Qty,
                    trade.Price,
                    0m,  // No closed qty for display
                    0m,  // No realized for display
                    running,  // Current running total
                    IsStrategyLeg: true
                ));
                continue;
            }

            // For parent trades, skip if it's an expiration with no closed quantity
            if (trade.Side == ExpireSide && closedQty == 0)
                continue;

            // Only accumulate P&L for non-leg trades
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
                running,
                IsStrategyLeg: false
            ));
        }

        return (rows, positions, running);
    }

    private static List<Trade> BuildExpirationTrades(List<Trade> trades, DateTime sinceDate)
    {
        if (trades.Count == 0)
            return new List<Trade>();

        var maxSeq = trades.Max(t => t.Seq);
        var seen = new Dictionary<string, Trade>();

        foreach (var trade in trades)
        {
            if (trade.Expiry == null || trade.Expiry.Value.Date > sinceDate.Date)
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
        Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
    {
        var rows = new List<PositionRow>();

        // Collect all non-strategy positions
        var allPositions = new List<(string matchKey, PositionRow row, Trade? trade)>();

        foreach (var (matchKey, lots) in positions)
        {
            if (lots.Count == 0)
                continue;

            var totalQty = lots.Sum(l => l.Qty);
            if (totalQty <= 0)
                continue;

            var trade = tradeIndex.GetValueOrDefault(matchKey);

            // Skip strategy parent positions - we only want to show the legs
            if (trade?.Asset == "Option Strategy")
                continue;

            var weighted = lots.Sum(l => l.Price * l.Qty);
            var avgPrice = weighted / totalQty;

            var instrument = trade?.Instrument ?? matchKey;
            var asset = trade?.Asset ?? "-";
            var optionKind = trade != null && !string.IsNullOrEmpty(trade.OptionKind) ? trade.OptionKind : "-";
            var expiry = trade?.Expiry;

            allPositions.Add((matchKey, new PositionRow(
                instrument, asset, optionKind, lots[0].Side, totalQty, avgPrice, expiry, IsStrategyLeg: false
            ), trade));
        }

        // Group option positions into potential strategies
        // A calendar strategy has: same underlying, same strike, different expirations, opposite sides
        var optionPositions = allPositions.Where(p => p.trade?.Asset == "Option").ToList();
        var grouped = new List<List<(string matchKey, PositionRow row, Trade? trade)>>();
        var processed = new HashSet<string>();

        // Group by root/strike/callput
        var byRootStrikeType = new Dictionary<string, List<(string matchKey, PositionRow row, Trade trade, OptionParsed parsed)>>();

        foreach (var pos in optionPositions)
        {
            if (pos.trade == null) continue;

            var symbol = pos.matchKey.StartsWith("option:") ? pos.matchKey.Substring(7) : null;
            if (symbol == null) continue;

            var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
            if (parsed == null) continue;

            var key = $"{parsed.Root}|{parsed.Strike}|{parsed.CallPut}";
            if (!byRootStrikeType.ContainsKey(key))
                byRootStrikeType[key] = new List<(string, PositionRow, Trade, OptionParsed)>();

            byRootStrikeType[key].Add((pos.matchKey, pos.row, pos.trade, parsed));
        }

        // For each group, match legs by quantity to create separate calendars
        foreach (var (key, legs) in byRootStrikeType)
        {
            // Separate into long and short positions
            var longLegs = legs.Where(l => l.row.Side == "Buy")
                .OrderByDescending(l => l.parsed.ExpiryDate)
                .ToList();

            var shortLegs = legs.Where(l => l.row.Side == "Sell")
                .OrderBy(l => l.parsed.ExpiryDate)
                .ToList();

            // Track remaining quantities
            var longRemaining = longLegs.ToDictionary(l => l.matchKey, l => l.row.Qty);
            var shortRemaining = shortLegs.ToDictionary(l => l.matchKey, l => l.row.Qty);

            // If we have both long and short legs, try to match them by quantity
            if (longLegs.Any() && shortLegs.Any())
            {
                foreach (var shortLeg in shortLegs)
                {
                    var shortQty = shortRemaining[shortLeg.matchKey];
                    if (shortQty <= 0 || processed.Contains(shortLeg.matchKey)) continue;

                    // Find a long leg with remaining quantity
                    foreach (var longLeg in longLegs)
                    {
                        var longQty = longRemaining[longLeg.matchKey];
                        if (longQty <= 0 || processed.Contains(longLeg.matchKey)) continue;

                        // Match up to the minimum of the two quantities
                        var matchedQty = Math.Min(longQty, shortQty);

                        // Create a calendar for this matched quantity
                        var calendarLegs = new List<(string, PositionRow, Trade?)>();

                        // Add long leg with matched quantity
                        calendarLegs.Add((longLeg.matchKey, longLeg.row with { Qty = matchedQty }, longLeg.trade));

                        // Add short leg with matched quantity
                        calendarLegs.Add((shortLeg.matchKey, shortLeg.row with { Qty = matchedQty }, shortLeg.trade));

                        grouped.Add(calendarLegs);

                        // Update remaining quantities
                        longRemaining[longLeg.matchKey] -= matchedQty;
                        shortRemaining[shortLeg.matchKey] -= matchedQty;
                        shortQty -= matchedQty;

                        // Mark as processed if fully consumed
                        if (shortRemaining[shortLeg.matchKey] <= 0)
                        {
                            processed.Add(shortLeg.matchKey);
                            break;
                        }
                    }
                }

                // Mark fully consumed long legs as processed
                foreach (var longLeg in longLegs)
                {
                    if (longRemaining[longLeg.matchKey] <= 0)
                        processed.Add(longLeg.matchKey);
                }

                // Add any unmatched legs as standalone positions
                foreach (var longLeg in longLegs)
                {
                    var remaining = longRemaining[longLeg.matchKey];
                    if (remaining > 0 && !processed.Contains(longLeg.matchKey))
                    {
                        grouped.Add(new List<(string, PositionRow, Trade?)>
                        {
                            (longLeg.matchKey, longLeg.row with { Qty = remaining }, longLeg.trade)
                        });
                        processed.Add(longLeg.matchKey);
                    }
                }
            }
            else
            {
                // No matching, add all legs as standalone
                foreach (var leg in legs)
                {
                    if (!processed.Contains(leg.matchKey))
                    {
                        grouped.Add(new List<(string, PositionRow, Trade?)> { (leg.matchKey, leg.row, leg.trade) });
                        processed.Add(leg.matchKey);
                    }
                }
            }
        }

        // Add option positions that weren't parsed or grouped
        foreach (var pos in optionPositions)
        {
            if (!processed.Contains(pos.matchKey))
            {
                grouped.Add(new List<(string, PositionRow, Trade?)> { pos });
                processed.Add(pos.matchKey);
            }
        }

        // Add non-option positions
        grouped.AddRange(allPositions.Where(p => p.trade?.Asset != "Option").Select(p => new List<(string, PositionRow, Trade?)> { p }));

        // Sort groups
        grouped.Sort((a, b) =>
        {
            var aFirst = a[0].row;
            var bFirst = b[0].row;

            var cmp = string.Compare(aFirst.Asset, bFirst.Asset, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            cmp = string.Compare(aFirst.Instrument, bFirst.Instrument, StringComparison.Ordinal);
            return cmp;
        });

        // First, let's just add all non-strategy positions to see if they're being tracked
        // Temporarily disable grouping to debug
        foreach (var pos in allPositions)
        {
            rows.Add(pos.row);
        }

        if (rows.Count == 0)
        {
            // No positions at all - return empty
            return rows;
        }

        // Clear and rebuild with grouping
        rows.Clear();

        // Build output rows
        foreach (var group in grouped)
        {
            if (group.Count > 1)
            {
                // This is a strategy - add a summary row
                var firstLeg = group[0];

                // Extract symbol from match key
                var symbol = firstLeg.matchKey.StartsWith("option:") ? firstLeg.matchKey.Substring(7) : null;
                var parsed = symbol != null ? ParsingHelpers.ParseOptionSymbol(symbol) : null;

                if (parsed != null)
                {
                    var qty = group[0].row.Qty; // Assume all legs have same qty
                    var side = group.Any(g => g.row.Side == "Buy") ? "Buy" : "Sell";

                    // Calculate credits from closed legs (rolls)
                    // Find all closed trades for this underlying/strike
                    var closedLegsCredits = CalculateClosedLegsCredits(allTrades, parsed.Root, parsed.Strike, parsed.CallPut);

                    // Calculate initial and adjusted prices for each leg
                    var netPriceInitial = 0m;
                    var netPriceAdjusted = 0m;

                    foreach (var leg in group)
                    {
                        var initialPrice = leg.row.AvgPrice;
                        var adjustedPrice = initialPrice;

                        // For long legs, reduce cost basis by credits from closed short legs
                        if (leg.row.Side == "Buy" && closedLegsCredits > 0)
                        {
                            // Credits are in dollars (already multiplied by 100 in ApplyToLots)
                            // Need to convert to per-share price: total credits / (qty * multiplier)
                            var creditPerShare = closedLegsCredits / (leg.row.Qty * 100m);
                            adjustedPrice = initialPrice - creditPerShare;
                        }

                        if (leg.row.Side == "Buy")
                        {
                            netPriceInitial += initialPrice;
                            netPriceAdjusted += adjustedPrice;
                        }
                        else
                        {
                            netPriceInitial -= initialPrice;
                            netPriceAdjusted -= adjustedPrice;
                        }
                    }

                    var longestExpiry = group.Max(g => g.row.Expiry) ?? DateTime.MinValue;

                    // Strategy summary row with initial and adjusted prices
                    rows.Add(new PositionRow(
                        $"{parsed.Root} {Formatters.FormatOptionDate(longestExpiry)}",
                        "Option Strategy",
                        "Calendar",
                        side,
                        qty,
                        Math.Abs(netPriceAdjusted),  // Current avg price is adjusted
                        longestExpiry,
                        IsStrategyLeg: false,
                        InitialAvgPrice: Math.Abs(netPriceInitial),
                        AdjustedAvgPrice: Math.Abs(netPriceAdjusted)
                    ));

                    // Add legs with their adjusted prices
                    foreach (var leg in group.OrderByDescending(g => g.row.Expiry))
                    {
                        var initialPrice = leg.row.AvgPrice;
                        var adjustedPrice = initialPrice;

                        // For long legs, show adjusted price
                        if (leg.row.Side == "Buy" && closedLegsCredits > 0)
                        {
                            // Credits are in dollars (already multiplied by 100 in ApplyToLots)
                            // Need to convert to per-share price: total credits / (qty * multiplier)
                            var creditPerShare = closedLegsCredits / (leg.row.Qty * 100m);
                            adjustedPrice = initialPrice - creditPerShare;
                        }

                        rows.Add(leg.row with {
                            IsStrategyLeg = true,
                            InitialAvgPrice = initialPrice,
                            AdjustedAvgPrice = adjustedPrice
                        });
                    }
                }
            }
            else
            {
                // Standalone position
                rows.Add(group[0].row);
            }
        }

        return rows;
    }

    private static decimal CalculateClosedLegsCredits(List<Trade> allTrades, string root, decimal strike, string callPut)
    {
        // Find all option leg trades for this root/strike/type
        var legTrades = allTrades
            .Where(t => t.Asset == "Option" && t.ParentStrategySeq.HasValue)
            .ToList();

        // Parse each leg and find matching ones
        var matchingLegs = new List<(Trade trade, OptionParsed parsed)>();
        foreach (var trade in legTrades)
        {
            var symbol = trade.MatchKey.StartsWith("option:") ? trade.MatchKey.Substring(7) : null;
            if (symbol == null) continue;

            var parsed = ParsingHelpers.ParseOptionSymbol(symbol);
            if (parsed != null &&
                parsed.Root == root &&
                parsed.Strike == strike &&
                parsed.CallPut == callPut)
            {
                matchingLegs.Add((trade, parsed));
            }
        }

        // Track lots and P&L for each expiration
        var lotsByExpiry = new Dictionary<DateTime, (List<Lot> lots, decimal totalPnL)>();

        foreach (var (trade, parsed) in matchingLegs.OrderBy(x => x.trade.Timestamp))
        {
            if (!lotsByExpiry.ContainsKey(parsed.ExpiryDate))
                lotsByExpiry[parsed.ExpiryDate] = (new List<Lot>(), 0m);

            var (lots, totalPnL) = lotsByExpiry[parsed.ExpiryDate];

            // Apply the trade to this expiry's lots
            var (updatedLots, realized, _) = ApplyToLots(lots, trade.Side, trade.Qty, trade.Price, trade.Multiplier);

            // Accumulate P&L for this expiration
            lotsByExpiry[parsed.ExpiryDate] = (updatedLots, totalPnL + realized);
        }

        // Only count credits from fully closed expirations (where lots.Count == 0)
        decimal totalCredits = 0m;
        foreach (var (expiry, (lots, pnl)) in lotsByExpiry)
        {
            if (lots.Count == 0 && pnl > 0)
            {
                // This expiration is fully closed and made a profit
                totalCredits += pnl;
            }
        }

        return totalCredits;
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
