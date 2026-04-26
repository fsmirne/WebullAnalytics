using System.Linq;
using WebullAnalytics.Pricing;

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
    /// Builds 5 scenario points at S_T ∈ {spot·e^(−sigmaRange·σ), spot·e^(−sigmaRange·σ/2), spot,
    /// spot·e^(+sigmaRange·σ/2), spot·e^(+sigmaRange·σ)} where σ = ivAnnual · √years.
    /// Weights = log-normal density at each point, renormalized to sum to 1. Neutral drift.
    /// sigmaRange defaults to 1.0 (±1σ and ±0.5σ). Larger values test further-out scenarios and
    /// overweight fat tails, favoring unbounded-upside structures over pin/theta structures.
    /// </summary>
    public static IReadOnlyList<ScenarioPoint> BuildScenarioGrid(decimal spot, decimal ivAnnual, double years, decimal sigmaRange = 1.0m)
    {
        var sigma = (double)ivAnnual * Math.Sqrt(Math.Max(1e-9, years));
        var range = (double)sigmaRange;
        var multipliers = new[] { -range, -range * 0.5, 0.0, range * 0.5, range };
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

    public static OpenProposal? Score(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg) => skel.StructureKind switch
    {
        OpenStructureKind.LongCall or OpenStructureKind.LongPut => ScoreLongCallPut(skel, spot, asOf, quotes, bias, cfg),
        OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => ScoreShortVertical(skel, spot, asOf, quotes, bias, cfg),
        OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal => ScoreCalendarOrDiagonal(skel, spot, asOf, quotes, bias, cfg),
        _ => null
    };

    public static string BuildRationale(OpenProposal p, decimal bias, OpenerConfig cfg)
    {
        var cashSide = p.DebitOrCreditPerContract >= 0m
            ? $"credit ${p.DebitOrCreditPerContract:F2}"
            : $"debit ${-p.DebitOrCreditPerContract:F2}";

        var biasEffectPct = cfg.DirectionalFitWeight * bias * p.DirectionalFit * 100m;
        var biasTag = p.DirectionalFit == 0
            ? $"[tech {bias:+0.00;-0.00}, fit 0 → no adjustment]"
            : $"[tech {bias:+0.00;-0.00}, fit {p.DirectionalFit:+0;-0} → {biasEffectPct:+0;-0}% {(biasEffectPct >= 0 ? "boost" : "cut")}]";

        var beStr = p.Breakevens.Count > 0 ? $"BE ${string.Join("/", p.Breakevens.Select(b => b.ToString("F2")))}, " : "";

        // R/R and premium_ratio surface the asymmetry/cushion factors that BalanceFactor folds into the
        // score. Showing them inline lets the reader compare two similarly-scored trades by their shape.
        var rr = Math.Abs(p.MaxLossPerContract) > 0m ? Math.Max(0m, p.MaxProfitPerContract / Math.Abs(p.MaxLossPerContract)) : 0m;
        var ratioStr = p.PremiumRatio.HasValue ? $", prem {p.PremiumRatio.Value:F2}x" : "";

        return $"{p.StructureKind} — {cashSide}, maxProfit ${p.MaxProfitPerContract:F2}, maxLoss ${-p.MaxLossPerContract:F2}, R/R {rr:F2}{ratioStr}, {beStr}POP {p.ProbabilityOfProfit * 100m:F0}%, EV ${p.ExpectedValuePerContract:F2}, score {p.BiasAdjustedScore:F4} {biasTag}";
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
        var grid = BuildScenarioGrid(spot, iv, years, cfg.ScenarioGridSigma);
        decimal ev = 0m;
        foreach (var pt in grid)
        {
            var pnl = VerticalPnLAtExpiry(pt.SpotAtExpiry, shortParsed.Strike, longParsed.Strike, creditPerContract, isCall);
            ev += pt.Weight * pnl;
        }

        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        var fit = DirectionalFit.SignFor(skel.StructureKind);
        var premiumRatio = ComputePremiumRatio(skel.Legs, quotes);
        var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight) * StructureWeight(cfg, skel.StructureKind) * balance;
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: PriceLegs(skel.Legs, quotes),
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
            Fingerprint: fp,
            PremiumRatio: premiumRatio
        );
    }

    /// <summary>Looks up the structure-specific multiplier from config, defaulting to 1.0 when a
    /// kind isn't listed. Used by every Score* method as the final multiplicative factor on
    /// BiasAdjustedScore so the ranking reflects the user's historical edge per structure.</summary>
    private static decimal StructureWeight(OpenerConfig cfg, OpenStructureKind kind)
    {
        return cfg.StructureWeight.TryGetValue(kind.ToString(), out var w) ? w : 1.0m;
    }

    /// <summary>Total long-leg premium paid divided by total short-leg premium received, summed across
    /// all legs (qty-weighted). Generalizes naturally to any multi-leg structure: 2-leg debits
    /// (calendar/diagonal) get long_ask/short_bid; 2-leg credits (vertical) get the same expression
    /// which lands &lt;1 because short_bid &gt; long_ask; 4-leg double diagonals / iron butterflies /
    /// broken-wing butterflies sum across both call- and put-side legs. Single-leg structures (long
    /// call/put) have no shorts so we return 1 — premium_ratio adjustment is a no-op for them.</summary>
    private static decimal ComputePremiumRatio(IReadOnlyList<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
    {
        decimal totalLongPaid = 0m;
        decimal totalShortReceived = 0m;
        foreach (var leg in legs)
        {
            var q = TryLiveBidAsk(leg.Symbol, quotes);
            if (q == null) return 1m;   // missing quote — fall back to neutral so callers don't crash
            if (leg.Action == "buy") totalLongPaid += q.Value.ask * leg.Qty;
            else totalShortReceived += q.Value.bid * leg.Qty;
        }
        if (totalShortReceived <= 0m) return 1m;   // no shorts (or all-zero short bids): single-leg fallback
        return totalLongPaid / totalShortReceived;
    }

    /// <summary>Multiplicative factor applied to BiasAdjustedScore that captures payoff asymmetry (R/R)
    /// and premium efficiency (1/premium_ratio) in one continuous expression. Both pieces are
    /// observable trade properties — no thresholds, no magic numbers. Works uniformly across
    /// structures: high-R/R high-cushion trades get boosted, low-R/R thin-cushion trades get reduced.</summary>
    private static decimal BalanceFactor(decimal maxProfit, decimal maxLoss, decimal premiumRatio)
    {
        var lossAbs = Math.Abs(maxLoss);
        var rr = lossAbs > 0m ? Math.Max(0m, maxProfit / lossAbs) : 0m;
        var ratio = Math.Max(0.01m, premiumRatio);   // guard against div-by-zero on degenerate quotes
        return rr / ratio;
    }

    /// <summary>Returns a copy of <paramref name="legs"/> with PricePerShare set to the execution
    /// price for each leg: buys fill at ask, sells fill at bid. Callers must ensure a usable
    /// two-sided quote exists for every leg (the Score* methods verify this upfront).</summary>
    private static IReadOnlyList<ProposalLeg> PriceLegs(IReadOnlyList<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
    {
        var priced = new List<ProposalLeg>(legs.Count);
        foreach (var leg in legs)
        {
            var q = TryLiveBidAsk(leg.Symbol, quotes);
            decimal? price = q.HasValue ? (leg.Action == "buy" ? q.Value.ask : q.Value.bid) : null;
            priced.Add(leg with { PricePerShare = price });
        }
        return priced;
    }

    /// <summary>Position P&amp;L per contract at the short leg's expiry as a function of S_T, used for
    /// numerical breakeven root-finding on calendars/diagonals. Long leg is BS-priced with its
    /// remaining time; short leg is intrinsic (already at expiry). The whole expression in dollars per
    /// contract minus the entry debit gives signed P&amp;L.</summary>
    private static decimal CalendarOrDiagonalPnLAtShortExpiry(decimal sT, OptionParsed shortLeg, OptionParsed longLeg, double longAtShortYears, decimal ivLong, decimal debitPerContract)
    {
        var longBS = OptionMath.BlackScholes(sT, longLeg.Strike, longAtShortYears, OptionMath.RiskFreeRate, ivLong, longLeg.CallPut);
        var shortIntrinsic = longLeg.CallPut == "C"
            ? Math.Max(0m, sT - shortLeg.Strike)
            : Math.Max(0m, shortLeg.Strike - sT);
        return (longBS - shortIntrinsic) * 100m - debitPerContract;
    }

    /// <summary>Locates the lower and upper breakeven of a calendar/diagonal at the short leg's expiry
    /// via bisection. Scans a wide grid (±60% from spot in 121 steps) to find an interval that brackets
    /// each sign change, then bisects to ~$0.01 precision. Returns (null, null) if the payoff is never
    /// positive (no breakevens exist) or if bisection can't bracket two roots.</summary>
    private static (decimal? lower, decimal? upper) ComputeCalendarOrDiagonalBreakevens(decimal spot, OptionParsed shortLeg, OptionParsed longLeg, double longAtShortYears, decimal ivLong, decimal debitPerContract)
    {
        decimal Pnl(decimal s) => CalendarOrDiagonalPnLAtShortExpiry(s, shortLeg, longLeg, longAtShortYears, ivLong, debitPerContract);

        const int steps = 120;
        var sMin = spot * 0.4m;
        var sMax = spot * 1.6m;
        var stepSize = (sMax - sMin) / steps;

        var sPrev = sMin;
        var pnlPrev = Pnl(sPrev);
        decimal? lower = null, upper = null;
        for (var i = 1; i <= steps; i++)
        {
            var sNext = sMin + stepSize * i;
            var pnlNext = Pnl(sNext);
            if ((pnlPrev < 0m && pnlNext >= 0m) || (pnlPrev >= 0m && pnlNext < 0m))
            {
                var root = BisectBreakeven(Pnl, sPrev, sNext);
                if (lower == null) lower = root;
                else upper = root;
                if (upper != null) break;
            }
            sPrev = sNext;
            pnlPrev = pnlNext;
        }
        return (lower, upper);
    }

    /// <summary>Bisection on a continuous P&amp;L curve known to have a single sign change in [a, b].
    /// Stops when the interval is ≤ 0.005 (sub-cent on a $25 underlying), capped at 60 iterations.</summary>
    private static decimal BisectBreakeven(Func<decimal, decimal> pnl, decimal a, decimal b)
    {
        var fa = pnl(a);
        for (var i = 0; i < 60; i++)
        {
            var mid = (a + b) / 2m;
            if (b - a <= 0.005m) return mid;
            var fm = pnl(mid);
            if ((fa < 0m && fm < 0m) || (fa >= 0m && fm >= 0m)) { a = mid; fa = fm; }
            else { b = mid; }
        }
        return (a + b) / 2m;
    }

    /// <summary>Locates the peak P&amp;L of a calendar/diagonal at the short leg's expiry by scanning a
    /// fine grid (±60% from spot, 240 points) and returning the maximum-PnL value sampled. The 5-point
    /// scenario grid the EV calculation uses misses the peak — for a $25 ATM diagonal the actual peak
    /// sits between two grid points, so the scenario-max underestimates max_profit by ~30%. R/R needs
    /// the true peak to be meaningful.</summary>
    private static decimal FindCalendarOrDiagonalPeakPnl(decimal spot, OptionParsed shortLeg, OptionParsed longLeg, double longAtShortYears, decimal ivLong, decimal debitPerContract)
    {
        const int steps = 240;
        var sMin = spot * 0.4m;
        var sMax = spot * 1.6m;
        var stepSize = (sMax - sMin) / steps;

        var peak = decimal.MinValue;
        for (var i = 0; i <= steps; i++)
        {
            var s = sMin + stepSize * i;
            var pnl = CalendarOrDiagonalPnLAtShortExpiry(s, shortLeg, longLeg, longAtShortYears, ivLong, debitPerContract);
            if (pnl > peak) peak = pnl;
        }
        return peak;
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

    public static OpenProposal? ScoreCalendarOrDiagonal(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg)
    {
        var shortLeg = skel.Legs.First(l => l.Action == "sell");
        var longLeg = skel.Legs.First(l => l.Action == "buy");
        var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
        var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
        if (shortParsed == null || longParsed == null) return null;

        var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
        var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
        if (shortQ == null || longQ == null) return null;

        var debitPerShare = longQ.Value.ask - shortQ.Value.bid;
        if (debitPerShare <= 0m) return null;

        var debitPerContract = debitPerShare * 100m;
        var capitalAtRisk = debitPerContract;

        var shortYears = Math.Max(1, (shortParsed.ExpiryDate.Date - asOf.Date).Days) / 365.0;
        var longAtShortYears = Math.Max(1, (longParsed.ExpiryDate.Date - shortParsed.ExpiryDate.Date).Days) / 365.0;
        var ivShort = ResolveIv(shortLeg.Symbol, quotes, cfg.IvDefaultPct);
        var ivLong = ResolveIv(longLeg.Symbol, quotes, cfg.IvDefaultPct);

        // Breakevens are the roots of the position-value-at-short-expiry curve, found numerically because
        // the curve mixes Black-Scholes (long leg) with piecewise-linear intrinsic (short leg) and has
        // no closed form. POP = P(BE_lower < S_T < BE_upper) under the same log-normal used for EV — a
        // proper breakeven-based probability rather than the fixed 5%-band stand-in we used to ship.
        var (beLower, beUpper) = ComputeCalendarOrDiagonalBreakevens(spot, shortParsed, longParsed, longAtShortYears, ivLong, debitPerContract);
        decimal pop;
        if (beLower.HasValue && beUpper.HasValue)
        {
            var pAboveLower = LogNormalProbability(Direction.Above, spot, beLower.Value, shortYears, (double)ivShort);
            var pAboveUpper = LogNormalProbability(Direction.Above, spot, beUpper.Value, shortYears, (double)ivShort);
            pop = pAboveLower - pAboveUpper;
            if (pop < 0m) pop = 0m;
        }
        else
        {
            // Fallback: payoff-positive nowhere (or bisection couldn't find both roots) — POP = 0.
            pop = 0m;
        }

        // EV uses the 5-point grid — a probability-weighted average across realistic outcomes.
        // max_profit comes from a separate fine-grid scan because the true peak typically falls between
        // 5-point grid points (e.g. an ATM call diagonal peaks at the short strike, which the ±0.5σ
        // sampling misses) — using the scenario-grid max would understate R/R by ~30%.
        var grid = BuildScenarioGrid(spot, ivShort, shortYears, cfg.ScenarioGridSigma);
        decimal ev = 0m;
        foreach (var pt in grid)
        {
            var longBS = OptionMath.BlackScholes(pt.SpotAtExpiry, longParsed.Strike, longAtShortYears, OptionMath.RiskFreeRate, ivLong, longParsed.CallPut);
            var shortIntrinsic = longParsed.CallPut == "C"
                ? Math.Max(0m, pt.SpotAtExpiry - shortParsed.Strike)
                : Math.Max(0m, shortParsed.Strike - pt.SpotAtExpiry);
            var positionValue = (longBS - shortIntrinsic) * 100m;
            ev += pt.Weight * (positionValue - debitPerContract);
        }
        var maxProfit = FindCalendarOrDiagonalPeakPnl(spot, shortParsed, longParsed, longAtShortYears, ivLong, debitPerContract);
        var maxLossPoint = -debitPerContract;   // long can decay to 0 at extreme moves; short is fully covered

        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        // Diagonals get fit=0 like calendars: the user's edge with horizontal spreads is structural
        // (long-DTE adjustment runway, theta, wide profit zone), not directional bias from strike geometry.
        // The technical bias signal still flows through verticals/long-call-puts where direction IS the bet.
        var fit = 0;
        var premiumRatio = ComputePremiumRatio(skel.Legs, quotes);
        var balance = BalanceFactor(maxProfit, maxLossPoint, premiumRatio);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight) * StructureWeight(cfg, skel.StructureKind) * balance;
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: PriceLegs(skel.Legs, quotes),
            Qty: 1,
            DebitOrCreditPerContract: -debitPerContract,
            MaxProfitPerContract: maxProfit,
            MaxLossPerContract: maxLossPoint,
            CapitalAtRiskPerContract: capitalAtRisk,
            Breakevens: (beLower.HasValue && beUpper.HasValue) ? new[] { beLower.Value, beUpper.Value } : Array.Empty<decimal>(),
            ProbabilityOfProfit: pop,
            ExpectedValuePerContract: ev,
            DaysToTarget: daysToTarget,
            RawScore: rawScore,
            BiasAdjustedScore: biasAdj,
            DirectionalFit: fit,
            Rationale: "",
            Fingerprint: fp,
            PremiumRatio: premiumRatio
        );
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

        var grid = BuildScenarioGrid(spot, iv, years, cfg.ScenarioGridSigma);
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
        // Single-leg structures: premium_ratio defaults to 1 (no shorts to receive from). BalanceFactor
        // collapses to just the R/R term, where R/R = projected_max_profit_at_+2σ / debit.
        var premiumRatio = ComputePremiumRatio(skel.Legs, quotes);
        var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight) * StructureWeight(cfg, skel.StructureKind) * balance;
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: PriceLegs(skel.Legs, quotes),
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
            Fingerprint: fp,
            PremiumRatio: null   // single-leg: ratio collapses to 1 (no shorts), don't display "prem 1.00x"
        );
    }
}
