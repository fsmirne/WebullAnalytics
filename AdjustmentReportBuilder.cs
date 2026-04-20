namespace WebullAnalytics;

public record CostStep(DateTime Timestamp, string Instrument, Side Side, int TradeQty, decimal Price, int RunningQty, decimal RunningAvg);
public record StrategyCredit(string Instrument, int Qty, decimal LotPrice, decimal ParentPrice);
public record NetDebitTrade(DateTime Timestamp, string Instrument, Side Side, int Qty, decimal Price, decimal CashImpact);
public record StrategyAdjustment(List<NetDebitTrade> Trades, decimal TotalNetDebit, DateTime? LastFlatTime, decimal? InitNetDebit = null);
public record PriceBreakdown(string Instrument, Asset Asset, Side PositionSide, int Qty, decimal InitPrice, decimal? AdjPrice, List<CostStep>? CostSteps, List<StrategyCredit>? Credits, List<NetDebitTrade>? NetDebitTrades, decimal? TotalNetDebit, DateTime? LastFlatTime, string? OptionKind = null, decimal? InitNetDebit = null, List<NetDebitTrade>? StandaloneAdjustments = null);

/// <summary>
/// Builds per-position breakdowns showing how each adjusted price was calculated.
/// Strategy breakdowns use pre-computed timeline replay data from BuildFinalPositionRows.
/// </summary>
internal static class AdjustmentReportBuilder
{
    internal static List<PriceBreakdown> Build(List<PositionRow> positionRows, List<Trade> allTrades, Dictionary<string, List<Lot>> positions, Dictionary<int, StrategyAdjustment>? strategyAdjustments = null, Dictionary<string, List<NetDebitTrade>>? singleLegStandalones = null)
    {
        var result = new List<PriceBreakdown>();
        var tradeBySeq = allTrades.Where(t => t.Asset == Asset.OptionStrategy).ToDictionary(t => t.Seq);

        int i = 0;
        while (i < positionRows.Count)
        {
            var row = positionRows[i];

            if (row.Asset == Asset.OptionStrategy && !row.IsStrategyLeg)
            {
                int j = i + 1;
                while (j < positionRows.Count && positionRows[j].IsStrategyLeg)
                    j++;
                var adjustment = strategyAdjustments?.GetValueOrDefault(i);
                var breakdown = BuildStrategyBreakdown(row, adjustment);
                if (breakdown != null) result.Add(breakdown);
                i = j;
            }
            else if (!row.IsStrategyLeg)
            {
                var standalones = row.MatchKey != null ? singleLegStandalones?.GetValueOrDefault(row.MatchKey) : null;
                var breakdown = BuildSingleBreakdown(row, allTrades, positions, tradeBySeq, standalones);
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

    private static PriceBreakdown? BuildSingleBreakdown(PositionRow row, List<Trade> allTrades, Dictionary<string, List<Lot>> positions, Dictionary<int, Trade> tradeBySeq, List<NetDebitTrade>? standaloneAdjustments)
    {
        if (row.MatchKey == null) return null;

        var costSteps = BuildCostSteps(row.MatchKey, allTrades);
        var credits = BuildStrategyCredits(row.MatchKey, positions, allTrades, tradeBySeq);

        var hasAdjustment = row.AdjustedAvgPrice.HasValue && row.InitialAvgPrice.HasValue && row.AdjustedAvgPrice.Value != row.InitialAvgPrice.Value;
        if (!hasAdjustment && credits.Count == 0) return null;

        var initPrice = row.InitialAvgPrice ?? row.AvgPrice;
        return new PriceBreakdown(row.Instrument, row.Asset, row.Side, row.Qty, initPrice, row.AdjustedAvgPrice, costSteps, credits.Count > 0 ? credits : null, null, null, null, StandaloneAdjustments: standaloneAdjustments);
    }

    private static List<CostStep> BuildCostSteps(string matchKey, List<Trade> allTrades)
    {
        var steps = new List<CostStep>();
        var state = (qty: 0, avg: 0m);
        int lastFlatIndex = -1;

        foreach (var trade in allTrades.Where(t => t.MatchKey == matchKey && t.Side is Side.Buy or Side.Sell && t.Asset != Asset.OptionStrategy).OrderBy(t => t.Timestamp).ThenBy(t => t.Seq))
        {
            state = PositionTracker.StepAverageCost(state, trade.Side, trade.Qty, trade.Price);
            steps.Add(new CostStep(trade.Timestamp, trade.Instrument, trade.Side, trade.Qty, trade.Price, Math.Abs(state.qty), state.avg));
            if (state.qty == 0) lastFlatIndex = steps.Count - 1;
        }

        if (lastFlatIndex >= 0)
            return steps.GetRange(lastFlatIndex + 1, steps.Count - lastFlatIndex - 1);

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

    private static PriceBreakdown? BuildStrategyBreakdown(PositionRow summaryRow, StrategyAdjustment? adjustment)
    {
        if (adjustment != null && adjustment.Trades.Count >= 2)
        {
            var initPrice = summaryRow.InitialAvgPrice ?? summaryRow.AvgPrice;
            return new PriceBreakdown(summaryRow.Instrument, summaryRow.Asset, summaryRow.Side, summaryRow.Qty, initPrice, summaryRow.AdjustedAvgPrice, null, null, adjustment.Trades, adjustment.TotalNetDebit, adjustment.LastFlatTime, summaryRow.OptionKind, adjustment.InitNetDebit);
        }

        // Fallback: for partial-brand-new groups the replay produced no trades, but the strategy still
        // has a non-trivial Init vs Adj delta because one leg inherited an adjusted price from a sibling
        // group. Emit a minimal breakdown so the user can see where the adjustment came from.
        if (summaryRow.InitialAvgPrice.HasValue && summaryRow.AdjustedAvgPrice.HasValue
            && summaryRow.InitialAvgPrice.Value != summaryRow.AdjustedAvgPrice.Value)
        {
            var initDebit = summaryRow.InitialAvgPrice.Value * summaryRow.Qty * Trade.OptionMultiplier;
            var adjDebit = summaryRow.AdjustedAvgPrice.Value * summaryRow.Qty * Trade.OptionMultiplier;
            return new PriceBreakdown(summaryRow.Instrument, summaryRow.Asset, summaryRow.Side, summaryRow.Qty, summaryRow.InitialAvgPrice.Value, summaryRow.AdjustedAvgPrice, null, null, new List<NetDebitTrade>(), adjDebit, null, summaryRow.OptionKind, initDebit);
        }

        return null;
    }
}
