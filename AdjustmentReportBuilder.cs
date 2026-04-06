namespace WebullAnalytics;

public record CostStep(DateTime Timestamp, string Instrument, Side Side, int TradeQty, decimal Price, int RunningQty, decimal RunningAvg);
public record StrategyCredit(string Instrument, int Qty, decimal LotPrice, decimal ParentPrice);
public record NetDebitTrade(DateTime Timestamp, string Instrument, Side Side, int Qty, decimal Price, decimal CashImpact);
public record StrategyAdjustment(List<NetDebitTrade> Trades, decimal TotalNetDebit, DateTime? LastFlatTime);
public record PriceBreakdown(string Instrument, Asset Asset, Side PositionSide, int Qty, decimal InitPrice, decimal? AdjPrice, List<CostStep>? CostSteps, List<StrategyCredit>? Credits, List<NetDebitTrade>? NetDebitTrades, decimal? TotalNetDebit, DateTime? LastFlatTime, string? OptionKind = null);

/// <summary>
/// Builds per-position breakdowns showing how each adjusted price was calculated.
/// Strategy breakdowns use pre-computed timeline replay data from BuildFinalPositionRows.
/// </summary>
internal static class AdjustmentReportBuilder
{
    internal static List<PriceBreakdown> Build(List<PositionRow> positionRows, List<Trade> allTrades, Dictionary<string, List<Lot>> positions, Dictionary<int, StrategyAdjustment>? strategyAdjustments = null)
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
                var adjustment = strategyAdjustments?.GetValueOrDefault(i);
                var breakdown = BuildStrategyBreakdown(row, legs, allTrades, adjustment);
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

    private static PriceBreakdown? BuildStrategyBreakdown(PositionRow summaryRow, List<PositionRow> legs, List<Trade> allTrades, StrategyAdjustment? adjustment)
    {
        var initPrice = summaryRow.InitialAvgPrice ?? summaryRow.AvgPrice;

        // Non-pure strategy with pre-computed replay data
        if (adjustment != null)
        {
            if (adjustment.Trades.Count <= 2) return null;
            return new PriceBreakdown(summaryRow.Instrument, summaryRow.Asset, summaryRow.Side, summaryRow.Qty, initPrice, summaryRow.AdjustedAvgPrice, null, null, adjustment.Trades, adjustment.TotalNetDebit, adjustment.LastFlatTime, summaryRow.OptionKind);
        }

        // Pure strategy (adj == init): show only the strategy's own leg trades if there are more than 2
        var legMatchKeys = legs.Where(l => l.MatchKey != null).Select(l => l.MatchKey!).ToHashSet();
        var ownTrades = allTrades.Where(t => legMatchKeys.Contains(t.MatchKey) && t.Side is Side.Buy or Side.Sell && t.Asset == Asset.Option).OrderByDescending(t => t.Timestamp).ThenByDescending(t => t.Seq).Take(legs.Count).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq)
            .Select(t => new NetDebitTrade(t.Timestamp, t.Instrument, t.Side, t.Qty, t.Price, (t.Side == Side.Buy ? -1m : 1m) * t.Qty * t.Price * t.Multiplier)).ToList();
        if (ownTrades.Count <= 2) return null;
        return new PriceBreakdown(summaryRow.Instrument, summaryRow.Asset, summaryRow.Side, summaryRow.Qty, initPrice, summaryRow.AdjustedAvgPrice, null, null, ownTrades, null, null, summaryRow.OptionKind);
    }
}
