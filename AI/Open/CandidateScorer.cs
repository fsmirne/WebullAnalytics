using System.Linq;

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

    /// <summary>Resolves IV from live quote → config default, as a decimal fraction (e.g. 0.40).</summary>
    public static decimal ResolveIv(string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal defaultPct)
    {
        if (quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m)
            return q.ImpliedVolatility.Value;
        return defaultPct / 100m;
    }

    /// <summary>Looks up bid/ask, returning null if any leg lacks a usable two-sided quote.</summary>
    public static (decimal bid, decimal ask)? TryLiveBidAsk(string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes)
    {
        if (!quotes.TryGetValue(symbol, out var q)) return null;
        if (!q.Bid.HasValue || !q.Ask.HasValue) return null;
        if (q.Bid.Value < 0m || q.Ask.Value <= 0m) return null;
        return (q.Bid.Value, q.Ask.Value);
    }

    /// <summary>sha1-hex fingerprint of (ticker | kind | sorted legs | qty). Stable across ticks.</summary>
    public static string ComputeFingerprint(string ticker, OpenStructureKind kind, IReadOnlyList<ProposalLeg> legs, int qty)
    {
        var sortedLegs = string.Join("|", legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}").OrderBy(s => s, StringComparer.Ordinal));
        var payload = $"{ticker}|{kind}|{sortedLegs}|{qty}";
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal enum Direction { Above, Below }

    /// <summary>
    /// P(S_T &gt; level) or P(S_T &lt; level) under log-normal neutral drift:
    /// d2 = (ln(S/K) − σ²·T/2) / (σ·√T); P(S_T &gt; K) = N(d2); P(S_T &lt; K) = 1 − N(d2).
    /// </summary>
    public static decimal LogNormalProbability(Direction dir, decimal spot, decimal level, double years, double ivAnnual)
    {
        if (level <= 0m || spot <= 0m || years <= 0 || ivAnnual <= 0) return 0m;
        var sigmaSqrtT = ivAnnual * Math.Sqrt(years);
        var d2 = (Math.Log((double)spot / (double)level) - 0.5 * ivAnnual * ivAnnual * years) / sigmaSqrtT;
        var N_d2 = OptionMath.NormalCdf(d2);
        return (decimal)(dir == Direction.Above ? N_d2 : 1.0 - N_d2);
    }

    public static OpenProposal? ScoreShortVertical(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg)
    {
        var shortLeg = skel.Legs.First(l => l.Action == "sell");
        var longLeg = skel.Legs.First(l => l.Action == "buy");
        var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
        var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
        if (shortParsed == null || longParsed == null) return null;

        var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
        var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
        if (shortQ == null || longQ == null) return null;

        // Credit execution assumes short fills at bid, long fills at ask (conservative).
        var creditPerShare = shortQ.Value.bid - longQ.Value.ask;
        if (creditPerShare <= 0m) return null; // not a credit spread at these quotes

        var creditPerContract = creditPerShare * 100m;
        var width = Math.Abs(shortParsed.Strike - longParsed.Strike);
        var capitalAtRisk = width * 100m - creditPerContract;
        if (capitalAtRisk <= 0m) return null;

        var maxProfit = creditPerContract;
        var maxLoss = -capitalAtRisk;

        var isCall = skel.StructureKind == OpenStructureKind.ShortCallVertical;
        var breakeven = isCall
            ? shortParsed.Strike + creditPerShare   // call credit: loses if S_T > short + credit
            : shortParsed.Strike - creditPerShare;  // put credit: loses if S_T < short − credit

        var years = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days) / 365.0;
        var iv = ResolveIv(shortLeg.Symbol, quotes, cfg.IvDefaultPct);

        // POP = P(S_T inside profitable side of breakeven).
        var pop = LogNormalProbability(isCall ? Direction.Below : Direction.Above, spot, breakeven, years, (double)iv);

        // EV via scenario grid — payoff at expiry is piecewise linear.
        var grid = BuildScenarioGrid(spot, iv, years);
        decimal ev = 0m;
        foreach (var pt in grid)
        {
            var pnl = VerticalPnLAtExpiry(pt.SpotAtExpiry, shortParsed.Strike, longParsed.Strike, creditPerContract, isCall);
            ev += pt.Weight * pnl;
        }

        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        var fit = DirectionalFit.SignFor(skel.StructureKind);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight);
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: skel.Legs,
            Qty: 1,
            DebitOrCreditPerContract: creditPerContract,   // positive = credit received
            MaxProfitPerContract: maxProfit,
            MaxLossPerContract: maxLoss,
            CapitalAtRiskPerContract: capitalAtRisk,
            Breakevens: new[] { breakeven },
            ProbabilityOfProfit: pop,
            ExpectedValuePerContract: ev,
            DaysToTarget: daysToTarget,
            RawScore: rawScore,
            BiasAdjustedScore: biasAdj,
            DirectionalFit: fit,
            Rationale: "",
            Fingerprint: fp
        );
    }

    private static decimal VerticalPnLAtExpiry(decimal sT, decimal shortStrike, decimal longStrike, decimal creditPerContract, bool isCall)
    {
        if (isCall)
        {
            // Call credit: short above long. Profit = credit when S_T ≤ short. Loss ramps to −(width − credit) at S_T ≥ long.
            var shortPayoff = Math.Max(0m, sT - shortStrike) * 100m;
            var longPayoff = Math.Max(0m, sT - longStrike) * 100m;
            return creditPerContract - shortPayoff + longPayoff;
        }
        else
        {
            // Put credit: short below spot, long further below. Profit = credit when S_T ≥ short. Loss ramps down.
            var shortPayoff = Math.Max(0m, shortStrike - sT) * 100m;
            var longPayoff = Math.Max(0m, longStrike - sT) * 100m;
            return creditPerContract - shortPayoff + longPayoff;
        }
    }

    public static OpenProposal? ScoreLongCallPut(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg)
    {
        var leg = skel.Legs[0];
        var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
        if (parsed == null) return null;

        var quote = TryLiveBidAsk(leg.Symbol, quotes);
        if (quote == null) return null;
        var (_, ask) = quote.Value;

        var years = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days) / 365.0;
        var iv = ResolveIv(leg.Symbol, quotes, cfg.IvDefaultPct);

        var debitPerShare = ask;
        var debitPerContract = debitPerShare * 100m;
        var breakeven = parsed.CallPut == "C" ? parsed.Strike + debitPerShare : parsed.Strike - debitPerShare;

        var pop = LogNormalProbability(parsed.CallPut == "C" ? Direction.Above : Direction.Below, spot, breakeven, years, (double)iv);

        var grid = BuildScenarioGrid(spot, iv, years);
        decimal ev = 0m;
        decimal maxProfit = 0m;
        decimal maxLoss = -debitPerContract;
        foreach (var pt in grid)
        {
            var intrinsic = parsed.CallPut == "C" ? Math.Max(0m, pt.SpotAtExpiry - parsed.Strike) : Math.Max(0m, parsed.Strike - pt.SpotAtExpiry);
            var pnl = intrinsic * 100m - debitPerContract;
            ev += pt.Weight * pnl;
            if (pnl > maxProfit) maxProfit = pnl;
        }

        var capitalAtRisk = debitPerContract;
        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        var fit = DirectionalFit.SignFor(skel.StructureKind);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight);
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: skel.Legs,
            Qty: 1,
            DebitOrCreditPerContract: -debitPerContract,
            MaxProfitPerContract: maxProfit,
            MaxLossPerContract: maxLoss,
            CapitalAtRiskPerContract: capitalAtRisk,
            Breakevens: new[] { breakeven },
            ProbabilityOfProfit: pop,
            ExpectedValuePerContract: ev,
            DaysToTarget: daysToTarget,
            RawScore: rawScore,
            BiasAdjustedScore: biasAdj,
            DirectionalFit: fit,
            Rationale: "",
            Fingerprint: fp
        );
    }
}
