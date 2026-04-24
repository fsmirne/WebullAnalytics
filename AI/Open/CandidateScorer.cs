namespace WebullAnalytics.AI;

internal static class CandidateScorer
{
    /// <summary>Score formula: EV / max(1, days) / capitalAtRisk. Returns 0 when capitalAtRisk ≤ 0.</summary>
    public static decimal ComputeRawScore(decimal ev, int daysToTarget, decimal capitalAtRisk)
    {
        if (capitalAtRisk <= 0m) return 0m;
        var days = Math.Max(1, daysToTarget);
        return ev / days / capitalAtRisk;
    }

    /// <summary>BiasAdjustedScore = raw × (1 + α · bias · fit). fit = 0 yields raw unchanged regardless of bias.</summary>
    public static decimal BiasAdjust(decimal raw, decimal bias, int fit, decimal alpha)
    {
        var factor = 1m + alpha * bias * fit;
        return raw * factor;
    }
}
