namespace WebullAnalytics.AI;

internal static class CandidateEnumerator
{
    public static IEnumerable<CandidateSkeleton> Enumerate(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg)
    {
        if (cfg.Structures.LongCalendar.Enabled)
            foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongCalendar, OpenStructureKind.LongCalendar))
                yield return sk;

        if (cfg.Structures.LongDiagonal.Enabled)
            foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongDiagonal, OpenStructureKind.LongDiagonal))
                yield return sk;
    }

    private static IEnumerable<CandidateSkeleton> EnumerateCalendarLike(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, OpenerCalendarLikeConfig sCfg, OpenStructureKind kind)
    {
        var shortExps = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
        var longExps = OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
        if (shortExps.Count == 0 || longExps.Count == 0) yield break;

        var step = cfg.StrikeStep;
        foreach (var shortStrike in StrikeGrid(spot, step))
        {
            // Skip strikes that are ITM by more than one step on either side (bad entry for a debit calendar).
            foreach (var callPut in new[] { "C", "P" })
            {
                if (callPut == "C" && shortStrike < spot - step) continue;
                if (callPut == "P" && shortStrike > spot + step) continue;

                foreach (var shortExp in shortExps)
                    foreach (var longExp in longExps)
                    {
                        if (longExp <= shortExp) continue;

                        if (kind == OpenStructureKind.LongCalendar)
                        {
                            var longStrike = shortStrike;
                            yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
                        }
                        else
                        {
                            // Diagonal: long strike one step above or below short strike.
                            foreach (var longStrike in new[] { shortStrike - step, shortStrike + step })
                            {
                                if (longStrike <= 0m) continue;
                                yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
                            }
                        }
                    }
            }
        }
    }

    /// <summary>Five distinct strikes centered on spot: floor(spot/step)*step, ceil(spot/step)*step, and ±1 step around each.</summary>
    internal static IReadOnlyList<decimal> StrikeGrid(decimal spot, decimal step)
    {
        var atmBelow = Math.Floor(spot / step) * step;
        var atmAbove = Math.Ceiling(spot / step) * step;
        var set = new HashSet<decimal>
        {
            atmBelow,
            atmAbove,
            atmBelow - step,
            atmAbove + step,
            atmBelow - 2m * step
        };
        return set.Where(s => s > 0m).OrderBy(s => s).ToList();
    }

    private static CandidateSkeleton BuildSpread(string ticker, OpenStructureKind kind, DateTime shortExp, DateTime longExp, decimal shortStrike, decimal longStrike, string callPut)
    {
        var shortSym = MatchKeys.OccSymbol(ticker, shortExp, shortStrike, callPut);
        var longSym = MatchKeys.OccSymbol(ticker, longExp, longStrike, callPut);
        var legs = new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        };
        return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: shortExp);
    }
}
