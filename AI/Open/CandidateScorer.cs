using WebullAnalytics.AI.Output;
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

	public static decimal? ComputeHistoricalVolatilityAnnualized(IReadOnlyList<decimal> closes)
	{
		if (closes.Count < 3) return null;

		var returns = new List<double>(closes.Count - 1);
		for (var i = 1; i < closes.Count; i++)
		{
			var prior = closes[i - 1];
			var current = closes[i];
			if (prior <= 0m || current <= 0m) continue;
			returns.Add(Math.Log((double)(current / prior)));
		}

		if (returns.Count < 2) return null;

		var mean = returns.Average();
		var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Count - 1);
		var annualized = Math.Sqrt(variance) * Math.Sqrt(252.0);
		return double.IsFinite(annualized) ? (decimal)annualized : null;
	}

	public static decimal? ComputeVolatilityAdjustmentFactor(OpenStructureKind kind, decimal ivAnnual, decimal? historicalVolAnnual, decimal weight)
	{
		if (weight <= 0m || ivAnnual <= 0m || !historicalVolAnnual.HasValue || historicalVolAnnual.Value <= 0m)
			return null;

		var fit = VolatilityFitSign(kind);
		if (fit == 0) return null;

		var richness = Math.Clamp(ivAnnual / historicalVolAnnual.Value - 1m, -1m, 1m);
		return Math.Max(0.10m, 1m + weight * richness * fit);
	}

	public static decimal VolatilityAdjust(decimal score, OpenStructureKind kind, decimal ivAnnual, decimal? historicalVolAnnual, decimal weight)
	{
		var factor = ComputeVolatilityAdjustmentFactor(kind, ivAnnual, historicalVolAnnual, weight);
		return factor.HasValue ? score * factor.Value : score;
	}

	public static decimal? ComputeMaxPainPrice(string ticker, DateTime expiry, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal? spot = null)
	{
		var chain = quotes
			.Select(kv => (parsed: ParsingHelpers.ParseOptionSymbol(kv.Key), quote: kv.Value))
			.Where(x => x.parsed != null
				&& string.Equals(x.parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)
				&& x.parsed.ExpiryDate.Date == expiry.Date
				&& x.quote.OpenInterest.HasValue
				&& x.quote.OpenInterest.Value > 0)
			.Select(x => (parsed: x.parsed!, openInterest: x.quote.OpenInterest!.Value))
			.ToList();

		if (chain.Count == 0)
			return null;

		var candidateStrikes = chain.Select(x => x.parsed.Strike).Distinct().OrderBy(s => s).ToList();
		decimal? bestStrike = null;
		decimal? bestPain = null;
		foreach (var settle in candidateStrikes)
		{
			decimal totalPain = 0m;
			foreach (var leg in chain)
			{
				var intrinsic = leg.parsed.CallPut == "C"
					? Math.Max(0m, settle - leg.parsed.Strike)
					: Math.Max(0m, leg.parsed.Strike - settle);
				totalPain += intrinsic * leg.openInterest;
			}

			if (!bestPain.HasValue || totalPain < bestPain.Value)
			{
				bestPain = totalPain;
				bestStrike = settle;
				continue;
			}

			if (totalPain != bestPain.Value || !bestStrike.HasValue)
				continue;

			if (spot.HasValue)
			{
				var currentDistance = Math.Abs(settle - spot.Value);
				var bestDistance = Math.Abs(bestStrike.Value - spot.Value);
				if (currentDistance < bestDistance || (currentDistance == bestDistance && settle < bestStrike.Value))
					bestStrike = settle;
			}
			else if (settle < bestStrike.Value)
			{
				bestStrike = settle;
			}
		}

		return bestStrike;
	}

	public static decimal MaxPainAdjust(decimal score, decimal? factor) => factor.HasValue ? score * factor.Value : score;

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

	public static OpenProposal? Score(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid) => skel.StructureKind switch
	{
		OpenStructureKind.LongCall or OpenStructureKind.LongPut => ScoreLongCallPut(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode),
		OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => ScoreShortVertical(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode),
		OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal => ScoreCalendarOrDiagonal(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode),
		_ => null
	};

	public static string BuildRationale(OpenProposal p, decimal bias, OpenerConfig cfg)
	{
		var cashSide = p.DebitOrCreditPerContract >= 0m
			? $"credit ${p.DebitOrCreditPerContract:F2}"
			: $"debit ${-p.DebitOrCreditPerContract:F2}";

		var techAdjusted = BiasAdjust(p.RawScore, bias, p.DirectionalFit, cfg.DirectionalFitWeight);
		var biasEffectPct = cfg.DirectionalFitWeight * bias * p.DirectionalFit * 100m;
		var biasTag = p.DirectionalFit == 0
			? $"[tech {bias:+0.00;-0.00}, fit 0 → no tech adjustment]"
			: $"[tech {bias:+0.00;-0.00}, fit {p.DirectionalFit:+0;-0} → {biasEffectPct:+0;-0}% {(biasEffectPct >= 0 ? "tech boost" : "tech cut")}]";

		var beStr = p.Breakevens.Count > 0 ? $"BE ${string.Join("/", p.Breakevens.Select(b => b.ToString("F2")))}, " : "";

		// R/R and premium_ratio surface the asymmetry/cushion factors that BalanceFactor folds into the
		// score. Showing them inline lets the reader compare two similarly-scored trades by their shape.
		var rr = Math.Abs(p.MaxLossPerContract) > 0m ? Math.Max(0m, p.MaxProfitPerContract / Math.Abs(p.MaxLossPerContract)) : 0m;
		var ratioStr = p.PremiumRatio.HasValue ? $", prem {p.PremiumRatio.Value:F2}x" : "";
		var balance = BalanceFactor(p.MaxProfitPerContract, p.MaxLossPerContract, p.PremiumRatio ?? 1m);
		var volStr = "";
		if (p.VolatilityAdjustmentFactor.HasValue && p.ImpliedVolatilityAnnual.HasValue && p.HistoricalVolatilityAnnual.HasValue && p.HistoricalVolatilityAnnual.Value > 0m)
		{
			var richness = p.ImpliedVolatilityAnnual.Value / p.HistoricalVolatilityAnnual.Value;
			volStr = $" × vol {p.VolatilityAdjustmentFactor.Value:F2} (rep IV {p.ImpliedVolatilityAnnual.Value:P1} / underlying HV {p.HistoricalVolatilityAnnual.Value:P1} = {richness:F2}x)";
		}
		var painStr = "";
		if (p.MaxPainAdjustmentFactor.HasValue && p.TargetExpiryMaxPain.HasValue)
			painStr = $" × maxPain {p.MaxPainAdjustmentFactor.Value:F2} (target ${p.TargetExpiryMaxPain.Value:F2})";
		var thetaStr = p.ThetaPerDayPerContract.HasValue
			? $" × theta/day {p.ThetaPerDayPerContract.Value:+0.00;-0.00}/contract"
			: "";

		var rationaleLine = $"{cashSide}, maxProfit ${p.MaxProfitPerContract:F2}, maxLoss ${-p.MaxLossPerContract:F2}, R/R {rr:F2}{ratioStr}, {beStr}POP {p.ProbabilityOfProfit * 100m:F1}%, EV ${p.ExpectedValuePerContract:F2}";
		var scoreLine = $"raw {p.RawScore:F6} → tech-adjusted {techAdjusted:F6} {biasTag} → adjusted {p.BiasAdjustedScore:F6}";
     var factorsLine = $"tech-adjusted × balance {balance:F2}{thetaStr}{volStr}{painStr}";

		return $"{rationaleLine}\n{scoreLine}\n{factorsLine}";
	}

	private static decimal? ComputeMaxPainAdjustmentFactor(CandidateSkeleton skel, decimal spot, DateTime asOf, decimal targetIv, decimal? maxPain, OpenerConfig cfg, IReadOnlyList<decimal>? breakevens = null)
	{
		if (cfg.MaxPainWeight <= 0m || !maxPain.HasValue || spot <= 0m || targetIv <= 0m)
			return null;

		var targetYears = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days) / 365.0;
		var expectedMove = Math.Max(cfg.StrikeStep, spot * targetIv * (decimal)Math.Sqrt(targetYears));
		var signal = ComputeMaxPainSignal(skel, spot, maxPain.Value, expectedMove, breakevens);
		return Math.Max(0.10m, 1m + cfg.MaxPainWeight * signal);
	}

	internal static decimal ComputeMaxPainSignal(CandidateSkeleton skel, decimal spot, decimal maxPain, decimal expectedMove, IReadOnlyList<decimal>? breakevens = null)
	{
		var scale = Math.Max(0.01m, expectedMove);
		var fit = DirectionalFit.SignFor(skel.StructureKind);
		if (fit != 0)
			return Math.Clamp(((maxPain - spot) / scale) * fit, -1m, 1m);

		var shortLeg = skel.Legs.FirstOrDefault(l => l.Action == "sell");
		var shortParsed = shortLeg != null ? ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol) : null;
		if (shortParsed == null)
			return 0m;

		var pinSignal = 1m - 2m * Math.Clamp(Math.Abs(shortParsed.Strike - maxPain) / scale, 0m, 1m);
		var sideSignal = SameSideOfSpotSignal(shortParsed.Strike - spot, maxPain - spot);
		var beSignal = BreakevenBandSignal(maxPain, scale, breakevens);

		if (breakevens != null && breakevens.Count >= 2)
			return Math.Clamp(0.45m * beSignal + 0.35m * sideSignal + 0.20m * pinSignal, -1m, 1m);

		return Math.Clamp(0.65m * sideSignal + 0.35m * pinSignal, -1m, 1m);
	}

	private static decimal SameSideOfSpotSignal(decimal shortOffset, decimal painOffset)
	{
		var shortSign = Math.Sign(shortOffset);
		var painSign = Math.Sign(painOffset);
		if (shortSign == 0 && painSign == 0) return 1m;
		if (shortSign == 0 || painSign == 0) return 0m;
		return shortSign == painSign ? 1m : -1m;
	}

	private static decimal BreakevenBandSignal(decimal maxPain, decimal scale, IReadOnlyList<decimal>? breakevens)
	{
		if (breakevens == null || breakevens.Count < 2)
			return 0m;

		var lower = breakevens.Min();
		var upper = breakevens.Max();
		if (maxPain >= lower && maxPain <= upper)
			return 1m;

		var outside = maxPain < lower ? lower - maxPain : maxPain - upper;
		return -Math.Clamp(outside / scale, 0m, 1m);
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

	public static OpenProposal? ScoreShortVertical(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid)
	{
		var shortLeg = skel.Legs.First(l => l.Action == "sell");
		var longLeg = skel.Legs.First(l => l.Action == "buy");
		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		if (shortParsed == null || longParsed == null) return null;

		var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
		var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
		if (shortQ == null || longQ == null) return null;

		var shortMid = (shortQ.Value.bid + shortQ.Value.ask) / 2m;
		var longMid = (longQ.Value.bid + longQ.Value.ask) / 2m;
		var creditPerShare = PriceForSell(shortMid, shortQ.Value.bid, pricingMode) - PriceForBuy(longMid, longQ.Value.ask, pricingMode);
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
		var longIv = ResolveIv(longLeg.Symbol, quotes, cfg.IvDefaultPct);
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(
			(Parsed: shortParsed, Iv: iv, IsLong: false),
			(Parsed: longParsed, Iv: longIv, IsLong: true),
			asOf,
			spot);
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
		var representativeIv = (iv + longIv) / 2m;
		var volFactor = ComputeVolatilityAdjustmentFactor(skel.StructureKind, representativeIv, historicalVolAnnual, cfg.VolatilityFitWeight);
		var maxPain = ComputeMaxPainPrice(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, iv, maxPain, cfg);
		var biasAdj = MaxPainAdjust(VolatilityAdjust(BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight) * balance, skel.StructureKind, representativeIv, historicalVolAnnual, cfg.VolatilityFitWeight), maxPainFactor);
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
			Breakevens: [breakeven],
			ProbabilityOfProfit: pop,
			ExpectedValuePerContract: ev,
			DaysToTarget: daysToTarget,
			RawScore: rawScore,
			BiasAdjustedScore: biasAdj,
			DirectionalFit: fit,
			Rationale: "",
			Fingerprint: fp,
			PremiumRatio: premiumRatio,
			ImpliedVolatilityAnnual: representativeIv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract
		);
	}

	internal enum ShortVerticalRejectReason
	{
		Unknown,
		ParseFailed,
		MissingShortQuote,
		MissingLongQuote,
		NotCreditAtBidAsk,
		NonPositiveCapitalAtRisk,
	}

	internal static ShortVerticalRejectReason DiagnoseShortVerticalRejection(
		CandidateSkeleton skel,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		out string detail)
	{
		var shortLeg = skel.Legs.First(l => l.Action == "sell");
		var longLeg = skel.Legs.First(l => l.Action == "buy");

		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		if (shortParsed == null || longParsed == null)
		{
			detail = $"parse_failed short={shortLeg.Symbol} long={longLeg.Symbol}";
			return ShortVerticalRejectReason.ParseFailed;
		}

		var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
		if (shortQ == null)
		{
			detail = $"missing_short_quote {shortLeg.Symbol} ({DescribeQuote(shortLeg.Symbol, quotes)})";
			return ShortVerticalRejectReason.MissingShortQuote;
		}

		var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
		if (longQ == null)
		{
			detail = $"missing_long_quote {longLeg.Symbol} ({DescribeQuote(longLeg.Symbol, quotes)})";
			return ShortVerticalRejectReason.MissingLongQuote;
		}

		var creditPerShare = shortQ.Value.bid - longQ.Value.ask;
		if (creditPerShare <= 0m)
		{
			detail = $"not_credit shortBid={shortQ.Value.bid:F2} longAsk={longQ.Value.ask:F2} (credit={creditPerShare:F2})";
			return ShortVerticalRejectReason.NotCreditAtBidAsk;
		}

		var creditPerContract = creditPerShare * 100m;
		var width = Math.Abs(shortParsed.Strike - longParsed.Strike);
		var capitalAtRisk = width * 100m - creditPerContract;
		if (capitalAtRisk <= 0m)
		{
			detail = $"capital_at_risk<=0 width={width:F2} credit={creditPerContract:F2}";
			return ShortVerticalRejectReason.NonPositiveCapitalAtRisk;
		}

		detail = "unknown";
		return ShortVerticalRejectReason.Unknown;
	}

	private static string DescribeQuote(string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		if (!quotes.TryGetValue(symbol, out var q)) return "no_symbol";
		var bid = q.Bid.HasValue ? q.Bid.Value.ToString("F2") : "null";
		var ask = q.Ask.HasValue ? q.Ask.Value.ToString("F2") : "null";
		var iv = q.ImpliedVolatility.HasValue ? q.ImpliedVolatility.Value.ToString("F2") : "null";
		return $"bid={bid} ask={ask} iv={iv}";
	}

	/// <summary>Total long-leg premium paid divided by total short-leg premium received, summed across
	/// all legs (qty-weighted). Generalizes naturally to any multi-leg structure: 2-leg debits
	/// (calendar/diagonal) get long_ask/short_bid; 2-leg credits (vertical) get the same expression
	/// which lands &lt;1 because short_bid &gt; long_ask; 4-leg double diagonals / iron butterflies /
	/// broken-wing butterflies sum across both call- and put-side legs. Single-leg structures (long
	/// call/put) have no shorts so we return 1 — premium_ratio adjustment is a no-op for them.</summary>
	private static decimal ComputePremiumRatio(IReadOnlyList<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes, string pricingMode)
	{
		decimal totalLongPaid = 0m;
		decimal totalShortReceived = 0m;
		foreach (var leg in legs)
		{
			var q = TryLiveBidAsk(leg.Symbol, quotes);
			if (q == null) return 1m;   // missing quote — fall back to neutral so callers don't crash
			var mid = (q.Value.bid + q.Value.ask) / 2m;
			if (leg.Action == "buy") totalLongPaid += PriceForBuy(mid, q.Value.ask, pricingMode) * leg.Qty;
			else totalShortReceived += PriceForSell(mid, q.Value.bid, pricingMode) * leg.Qty;
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

	private static int VolatilityFitSign(OpenStructureKind kind) => kind switch
	{
		OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => 1,
		OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal or OpenStructureKind.LongCall or OpenStructureKind.LongPut => -1,
		_ => 0,
	};

	/// <summary>Returns a copy of <paramref name="legs"/> with PricePerShare set to the midpoint
	/// for default suggestions and ExecutionPricePerShare set to conservative bid/ask fills.
	/// Callers must ensure a usable two-sided quote exists for every leg (the Score* methods verify this upfront).</summary>
	private static IReadOnlyList<ProposalLeg> PriceLegs(IReadOnlyList<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var priced = new List<ProposalLeg>(legs.Count);
		foreach (var leg in legs)
		{
			var q = TryLiveBidAsk(leg.Symbol, quotes);
			decimal? price = q.HasValue ? (q.Value.bid + q.Value.ask) / 2m : null;
			decimal? executionPrice = q.HasValue ? (leg.Action == "buy" ? q.Value.ask : q.Value.bid) : null;
			priced.Add(leg with { PricePerShare = price, ExecutionPricePerShare = executionPrice });
		}
		return priced;
	}

	/// <summary>Position P&L per contract at the short leg's expiry as a function of S_T, used for
	/// numerical breakeven root-finding on calendars/diagonals. Long leg is BS-priced with its
	/// remaining time; short leg is intrinsic (already at expiry). The whole expression in dollars per
	/// contract minus the entry debit gives signed P&L.</summary>
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

	/// <summary>Bisection on a continuous P&L curve known to have a single sign change in [a, b].
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

	/// <summary>Locates the peak P&L of a calendar/diagonal at the short leg's expiry by scanning a
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

	public static OpenProposal? ScoreCalendarOrDiagonal(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid)
	{
		var shortLeg = skel.Legs.First(l => l.Action == "sell");
		var longLeg = skel.Legs.First(l => l.Action == "buy");
		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		if (shortParsed == null || longParsed == null) return null;

		var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
		var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
		if (shortQ == null || longQ == null) return null;

		var shortMid = (shortQ.Value.bid + shortQ.Value.ask) / 2m;
		var longMid = (longQ.Value.bid + longQ.Value.ask) / 2m;
		var debitPerShare = PriceForBuy(longMid, longQ.Value.ask, pricingMode) - PriceForSell(shortMid, shortQ.Value.bid, pricingMode);
		if (debitPerShare <= 0m) return null;

		var debitPerContract = debitPerShare * 100m;
		// For calendars and "covered" diagonals, max loss is typically the debit.
		// For inverted-strike diagonals, assignment at the short expiry can force exercising the long,
		// realizing the strike gap in addition to the debit.
		var strikeLossPerShare = longParsed.CallPut == "C"
			? Math.Max(longParsed.Strike - shortParsed.Strike, 0m)
			: Math.Max(shortParsed.Strike - longParsed.Strike, 0m);
		var strikeLossPerContract = strikeLossPerShare * 100m;
		var capitalAtRisk = debitPerContract + strikeLossPerContract;

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
		var maxLossPoint = -(debitPerContract + strikeLossPerContract);

		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
		// Diagonals get fit=0 like calendars: the user's edge with horizontal spreads is structural
		// (long-DTE adjustment runway, theta, wide profit zone), not directional bias from strike geometry.
		// The technical bias signal still flows through verticals/long-call-puts where direction IS the bet.
		var fit = 0;
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(
			(Parsed: shortParsed, Iv: ivShort, IsLong: false),
			(Parsed: longParsed, Iv: ivLong, IsLong: true),
			asOf,
			spot);
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLossPoint, premiumRatio);
		var representativeIv = (ivShort + ivLong) / 2m;
		var volFactor = ComputeVolatilityAdjustmentFactor(skel.StructureKind, representativeIv, historicalVolAnnual, cfg.VolatilityFitWeight);
		var maxPain = ComputeMaxPainPrice(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, ivShort, maxPain, cfg);
		var biasAdj = MaxPainAdjust(VolatilityAdjust(BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight) * balance, skel.StructureKind, representativeIv, historicalVolAnnual, cfg.VolatilityFitWeight), maxPainFactor);
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
			PremiumRatio: premiumRatio,
			ImpliedVolatilityAnnual: representativeIv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract
		);
	}

	private static decimal ComputeNetThetaPerDayPerContract((OptionParsed Parsed, decimal Iv, bool IsLong) leg0, (OptionParsed Parsed, decimal Iv, bool IsLong) leg1, DateTime asOf, decimal spot)
	{
		return ComputeNetThetaPerDayPerContract(new[] { leg0, leg1 }, asOf, spot);
	}

	private static decimal ComputeNetThetaPerDayPerContract((OptionParsed Parsed, decimal Iv, bool IsLong) leg, DateTime asOf, decimal spot)
	{
		return ComputeNetThetaPerDayPerContract(new[] { leg }, asOf, spot);
	}

	private static decimal ComputeNetThetaPerDayPerContract(IEnumerable<(OptionParsed Parsed, decimal Iv, bool IsLong)> legs, DateTime asOf, decimal spot)
	{
		decimal netThetaPerShare = 0m;
		foreach (var leg in legs)
		{
			var dte = Math.Max(1, (leg.Parsed.ExpiryDate - asOf.Date).Days);
			var now = OptionMath.BlackScholes(spot, leg.Parsed.Strike, dte / 365.0, OptionMath.RiskFreeRate, leg.Iv, leg.Parsed.CallPut);
			var tomorrow = OptionMath.BlackScholes(spot, leg.Parsed.Strike, Math.Max(1, dte - 1) / 365.0, OptionMath.RiskFreeRate, leg.Iv, leg.Parsed.CallPut);
			var thetaPerShare = tomorrow - now;
			netThetaPerShare += leg.IsLong ? thetaPerShare : -thetaPerShare;
		}

		return netThetaPerShare * 100m;
	}

	public static OpenProposal? ScoreLongCallPut(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid)
	{
		var leg = skel.Legs[0];
		var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
		if (parsed == null) return null;

		var quote = TryLiveBidAsk(leg.Symbol, quotes);
		if (quote == null) return null;
		var (bid, ask) = quote.Value;
		var mid = (bid + ask) / 2m;

		var years = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days) / 365.0;
		var iv = ResolveIv(leg.Symbol, quotes, cfg.IvDefaultPct);

		var debitPerShare = PriceForBuy(mid, ask, pricingMode);
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
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract((Parsed: parsed, Iv: iv, IsLong: true), asOf, spot);
		// Single-leg structures: premium_ratio defaults to 1 (no shorts to receive from). BalanceFactor
		// collapses to just the R/R term, where R/R = projected_max_profit_at_+2σ / debit.
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
		var volFactor = ComputeVolatilityAdjustmentFactor(skel.StructureKind, iv, historicalVolAnnual, cfg.VolatilityFitWeight);
		var maxPain = ComputeMaxPainPrice(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, iv, maxPain, cfg);
		var biasAdj = MaxPainAdjust(VolatilityAdjust(BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight) * balance, skel.StructureKind, iv, historicalVolAnnual, cfg.VolatilityFitWeight), maxPainFactor);
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
			Breakevens: [breakeven],
			ProbabilityOfProfit: pop,
			ExpectedValuePerContract: ev,
			DaysToTarget: daysToTarget,
			RawScore: rawScore,
			BiasAdjustedScore: biasAdj,
			DirectionalFit: fit,
			Rationale: "",
			Fingerprint: fp,
			PremiumRatio: null,   // single-leg: ratio collapses to 1 (no shorts), don't display "prem 1.00x"
			ImpliedVolatilityAnnual: iv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract
		);
	}

	private static decimal PriceForBuy(decimal mid, decimal ask, string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? ask : mid;

	private static decimal PriceForSell(decimal mid, decimal bid, string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? bid : mid;
}
