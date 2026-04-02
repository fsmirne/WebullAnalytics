using System.Globalization;

namespace WebullAnalytics;

public record CostStep(DateTime Timestamp, string Instrument, Side Side, int TradeQty, decimal Price, int RunningQty, decimal RunningAvg);
public record StrategyCredit(string Instrument, int Qty, decimal LotPrice, decimal ParentPrice);
public record NetDebitTrade(DateTime Timestamp, string Instrument, Side Side, int Qty, decimal Price, decimal CashImpact);
public record PriceBreakdown(string Instrument, Asset Asset, Side PositionSide, int Qty, decimal InitPrice, decimal? AdjPrice, List<CostStep>? CostSteps, List<StrategyCredit>? Credits, List<NetDebitTrade>? NetDebitTrades, decimal? TotalNetDebit, DateTime? LastFlatTime, string? OptionKind = null);

/// <summary>
/// Builds per-position breakdowns showing how each adjusted price was calculated.
/// </summary>
internal static class AdjustmentReportBuilder
{
    internal static List<PriceBreakdown> Build(List<PositionRow> positionRows, List<Trade> allTrades, Dictionary<string, List<Lot>> positions)
    {
        var result = new List<PriceBreakdown>();
        var tradeBySeq = allTrades.Where(t => t.Asset == Asset.OptionStrategy).ToDictionary(t => t.Seq);

        int i = 0;
        while (i < positionRows.Count)
        {
            var row = positionRows[i];

            if (row.Asset == Asset.OptionStrategy && !row.IsStrategyLeg)
            {
                var legs = new List<PositionRow>();
                int j = i + 1;
                while (j < positionRows.Count && positionRows[j].IsStrategyLeg)
                {
                    legs.Add(positionRows[j]);
                    j++;
                }
                var breakdown = BuildStrategyBreakdown(row, legs, allTrades);
                if (breakdown != null) result.Add(breakdown);
                i = j;
            }
            else if (!row.IsStrategyLeg)
            {
                var breakdown = BuildSingleBreakdown(row, allTrades, positions, tradeBySeq);
                if (breakdown != null) result.Add(breakdown);
                i++;
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    private static PriceBreakdown? BuildSingleBreakdown(PositionRow row, List<Trade> allTrades, Dictionary<string, List<Lot>> positions, Dictionary<int, Trade> tradeBySeq)
    {
        if (row.MatchKey == null) return null;

        var costSteps = BuildCostSteps(row.MatchKey, allTrades);
        var credits = BuildStrategyCredits(row.MatchKey, positions, allTrades, tradeBySeq);

        if (costSteps.Count <= 1 && credits.Count == 0) return null;

        var initPrice = row.InitialAvgPrice ?? row.AvgPrice;
        return new PriceBreakdown(row.Instrument, row.Asset, row.Side, row.Qty, initPrice, row.AdjustedAvgPrice, costSteps, credits.Count > 0 ? credits : null, null, null, null);
    }

    private static List<CostStep> BuildCostSteps(string matchKey, List<Trade> allTrades)
    {
        var steps = new List<CostStep>();
        int qty = 0;
        decimal avg = 0m;

        foreach (var trade in allTrades.Where(t => t.MatchKey == matchKey && t.Side is Side.Buy or Side.Sell && t.Asset != Asset.OptionStrategy).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq))
        {
            var tradeSign = trade.Side == Side.Buy ? 1 : -1;
            var newQty = qty + tradeSign * trade.Qty;
            var isOpening = qty == 0 || (qty > 0 && trade.Side == Side.Buy) || (qty < 0 && trade.Side == Side.Sell);

            if (isOpening)
                avg = (Math.Abs(qty) * avg + trade.Qty * trade.Price) / Math.Abs(newQty);
            else if (newQty == 0)
                avg = 0m;
            else if ((qty > 0 && newQty < 0) || (qty < 0 && newQty > 0))
                avg = trade.Price;

            qty = newQty;
            steps.Add(new CostStep(trade.Timestamp, trade.Instrument, trade.Side, trade.Qty, trade.Price, Math.Abs(qty), avg));
        }

        return steps;
    }

    private static List<StrategyCredit> BuildStrategyCredits(string matchKey, Dictionary<string, List<Lot>> positions, List<Trade> allTrades, Dictionary<int, Trade> tradeBySeq)
    {
        var credits = new List<StrategyCredit>();
        var lots = positions.GetValueOrDefault(matchKey, []);

        foreach (var lot in lots.Where(l => l.ParentStrategySeq.HasValue))
        {
            if (!tradeBySeq.TryGetValue(lot.ParentStrategySeq!.Value, out var parentTrade)) continue;
            var otherLegs = allTrades.Where(t => t.ParentStrategySeq == parentTrade.Seq && t.MatchKey != matchKey).ToList();
            if (otherLegs.Count == 0) continue;
            if (otherLegs.Any(leg => positions.ContainsKey(leg.MatchKey) && positions[leg.MatchKey].Sum(l => l.Qty) > 0)) continue;

            credits.Add(new StrategyCredit(otherLegs[0].Instrument, lot.Qty, lot.Price, parentTrade.Price));
        }

        return credits;
    }

    private static PriceBreakdown? BuildStrategyBreakdown(PositionRow summaryRow, List<PositionRow> legs, List<Trade> allTrades)
    {
        var parsedLegs = legs.Where(l => l.MatchKey != null).Select(l => (leg: l, parsed: MatchKeys.TryGetOptionSymbol(l.MatchKey!, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null)).Where(x => x.parsed != null).ToList();
        if (parsedLegs.Count == 0) return null;

        var root = parsedLegs[0].parsed!.Root;
        var callPut = parsedLegs[0].parsed!.CallPut;
        var strikes = parsedLegs.Select(x => x.parsed!.Strike).Distinct().OrderBy(s => s).ToList();

        var (netDebitTrades, totalNetDebit, lastFlatTime) = ReplayNetDebitCalculation(allTrades, root, strikes, callPut);

        if (netDebitTrades.Count <= 2 && summaryRow.AdjustedAvgPrice == (summaryRow.InitialAvgPrice ?? summaryRow.AvgPrice)) return null;

        var initPrice = summaryRow.InitialAvgPrice ?? summaryRow.AvgPrice;
        return new PriceBreakdown(summaryRow.Instrument, summaryRow.Asset, summaryRow.Side, summaryRow.Qty, initPrice, summaryRow.AdjustedAvgPrice, null, null, netDebitTrades, totalNetDebit, lastFlatTime, summaryRow.OptionKind);
    }

    private static (List<NetDebitTrade> trades, decimal totalNetDebit, DateTime? lastFlatTime) ReplayNetDebitCalculation(List<Trade> allTrades, string root, List<decimal> strikes, string callPut)
    {
        var optionKeyPrefix = $"{MatchKeys.OptionPrefix}{root}";

        // Expand strikes transitively through strategy parent relationships
        var expandedStrikes = new HashSet<decimal>(strikes);
        var optionBuySellTrades = allTrades.Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell).ToList();
        bool changed = true;
        while (changed)
        {
            changed = false;
            var suffixes = expandedStrikes.Select(s => $"{callPut}{(long)(s * 1000m):D8}").ToHashSet();
            foreach (var trade in optionBuySellTrades.Where(t => t.ParentStrategySeq.HasValue && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && suffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal))))
                foreach (var sibling in allTrades.Where(t => t.ParentStrategySeq == trade.ParentStrategySeq && t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal)))
                {
                    if (!MatchKeys.TryGetOptionSymbol(sibling.MatchKey, out var sym)) continue;
                    var parsed = ParsingHelpers.ParseOptionSymbol(sym);
                    if (parsed == null || parsed.CallPut != callPut || expandedStrikes.Contains(parsed.Strike)) continue;
                    var priorQty = optionBuySellTrades.Where(t => t.MatchKey == sibling.MatchKey && t.Timestamp < sibling.Timestamp).Sum(t => t.Side == Side.Buy ? t.Qty : -t.Qty);
                    if (priorQty == 0)
                    {
                        expandedStrikes.Add(parsed.Strike);
                        changed = true;
                    }
                }
        }

        var occSuffixes = expandedStrikes.Select(s => $"{callPut}{(long)(s * 1000m):D8}").ToHashSet();
        var today = DateTime.Today;

        var legEvents = allTrades
            .Where(t => t.Asset == Asset.Option && t.Side is Side.Buy or Side.Sell && t.MatchKey.StartsWith(optionKeyPrefix, StringComparison.Ordinal) && occSuffixes.Any(suffix => t.MatchKey.EndsWith(suffix, StringComparison.Ordinal)))
            .Select(t => (t.Timestamp, t.Seq, t.MatchKey, t.Side, t.Qty, t.Price, t.Multiplier, t.Instrument, IsExpiry: false))
            .ToList();

        // Add expiration events for expired contracts (needed to find flat time)
        var expiredContracts = legEvents.Select(e => e.MatchKey).Distinct()
            .Select(mk => (matchKey: mk, parsed: MatchKeys.TryGetOptionSymbol(mk, out var sym) ? ParsingHelpers.ParseOptionSymbol(sym) : null))
            .Where(x => x.parsed != null && x.parsed.ExpiryDate.Date < today)
            .ToList();

        foreach (var (matchKey, parsed) in expiredContracts)
            legEvents.Add((parsed!.ExpiryDate.Date + PositionTracker.ExpirationTime, int.MaxValue, matchKey, Side.Expire, 0, 0m, 0m, Formatters.FormatOptionDisplay(parsed.Root, parsed.ExpiryDate, parsed.Strike) + " " + ParsingHelpers.CallPutDisplayName(parsed.CallPut), IsExpiry: true));

        legEvents.Sort((a, b) => { var cmp = a.Timestamp.CompareTo(b.Timestamp); return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq); });

        // Walk events to find last flat time
        var positions = new Dictionary<string, int>();
        DateTime lastFlatTime = DateTime.MinValue;
        int lastFlatSeq = int.MinValue;

        for (int i = 0; i < legEvents.Count;)
        {
            var batchTime = legEvents[i].Timestamp;
            var batchEndSeq = legEvents[i].Seq;
            while (i < legEvents.Count && legEvents[i].Timestamp == batchTime)
            {
                var e = legEvents[i];
                if (e.IsExpiry)
                    positions[e.MatchKey] = 0;
                else
                    positions[e.MatchKey] = positions.GetValueOrDefault(e.MatchKey) + (e.Side == Side.Buy ? e.Qty : -e.Qty);
                batchEndSeq = e.Seq;
                i++;
            }
            if (positions.Values.All(v => v == 0))
            {
                lastFlatTime = batchTime;
                lastFlatSeq = batchEndSeq;
            }
        }

        // Collect trades after last flat time
        var resultTrades = new List<NetDebitTrade>();
        decimal totalDebit = 0m;

        foreach (var e in legEvents.Where(e => !e.IsExpiry && (e.Timestamp > lastFlatTime || (e.Timestamp == lastFlatTime && e.Seq > lastFlatSeq))))
        {
            var cashImpact = (e.Side == Side.Buy ? -1m : 1m) * e.Qty * e.Price * e.Multiplier;
            totalDebit -= cashImpact;
            resultTrades.Add(new NetDebitTrade(e.Timestamp, e.Instrument, e.Side, e.Qty, e.Price, cashImpact));
        }

        return (resultTrades, totalDebit, lastFlatTime == DateTime.MinValue ? null : (DateTime?)lastFlatTime);
    }
}
