namespace WebullAnalytics.AI;

internal static class CandidateScorer
{
    /// <summary>One point in the 5-point log-normal scenario grid used to compute EV at target expiry.</summary>
    public readonly record struct ScenarioPoint(decimal SpotAtExpiry, decimal Weight);

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

    /// <summary>
    /// Builds 5 scenario points at S_T ∈ {spot·e^(−2σ), spot·e^(−σ), spot, spot·e^(+σ), spot·e^(+2σ)}
    /// where σ = ivAnnual · √years. Weights = log-normal density at each point, renormalized to sum to 1.
    /// Neutral drift.
    /// </summary>
    public static IReadOnlyList<ScenarioPoint> BuildScenarioGrid(decimal spot, decimal ivAnnual, double years)
    {
        var sigma = (double)ivAnnual * Math.Sqrt(Math.Max(1e-9, years));
        var multipliers = new[] { -2.0, -1.0, 0.0, 1.0, 2.0 };
        var points = new ScenarioPoint[5];

        // Unnormalized log-normal density at each z-point: φ(z) = (1/√(2π)) · e^(−z²/2). Weights are the densities.
        double[] rawWeights = new double[5];
        double totalWeight = 0;
        for (int i = 0; i < 5; i++)
        {
            var z = multipliers[i];
            rawWeights[i] = Math.Exp(-z * z / 2.0);
            totalWeight += rawWeights[i];
        }
        for (int i = 0; i < 5; i++)
        {
            var sT = (decimal)((double)spot * Math.Exp(multipliers[i] * sigma));
            var w = (decimal)(rawWeights[i] / totalWeight);
            points[i] = new ScenarioPoint(sT, w);
        }
        return points;
    }
}
