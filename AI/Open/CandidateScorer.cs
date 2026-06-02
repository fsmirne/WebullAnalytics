using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Output;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using WebullAnalytics.Sentiment;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace WebullAnalytics.AI;

internal static class CandidateScorer
{
   /// <summary>One point in the 5-point log-normal scenario grid used to compute EV at target expiry.</summary>
	public readonly record struct ScenarioPoint(decimal SpotAtExpiry, decimal Weight);

	/// <summary>Score formula: EV / max(1, days) / max(1, capitalAtRisk). Capital normalization
	/// rewards capital efficiency — a $2-EV trade tying up $30 outranks a $2-EV trade tying up
	/// $190, all else equal, because the cheaper trade lets you make the same EV with less risk
	/// per dollar. The lottery-ticket failure mode this used to enable (narrow $20-maxLoss
	/// structures outranking wider ones on per-dollar basis even when they were coin flips) is
	/// caught by the rest of the chain now: popFactor heavily penalizes low POP, BalanceFactor's
	/// 0.05 floor lets pathological R/R show real damage, and ApplyFactor's sign-symmetric form
	/// compounds penalties on bad trades instead of canceling them. With those in place, capital
	/// efficiency is a useful signal again — without them it was confusing leverage with quality.</summary>
	public static decimal ComputeRawScore(decimal ev, int daysToTarget, decimal capitalAtRisk)
	{
		var days = Math.Max(1, daysToTarget);
		var capital = Math.Max(1m, capitalAtRisk);
		return ev / days / capital;
	}

	/// <summary>Sign-symmetric factor application. For a positive score, multiplying by
	/// <paramref name="factor"/> &lt; 1 is a penalty (smaller positive). For a negative score, the
	/// same factor &lt; 1 used to bring the score TOWARD zero — i.e., make a bad trade look less
	/// bad — which collapsed obviously-bad candidates into the "least bad" ranking position. The
	/// fix: when score is negative, apply <c>(2 − factor)</c> so a penalty actually compounds the
	/// badness. Continuous at <c>factor = 1</c> (no-op for either sign), symmetric around zero.
	/// </summary>
	public static decimal ApplyFactor(decimal score, decimal factor)
	{
		return score >= 0m ? score * factor : score * (2m - factor);
	}

	/// <summary>BiasAdjustedScore = raw × (1 + α · bias · fit) using sign-symmetric multiplication.
	/// fit = 0 yields raw unchanged regardless of bias.</summary>
	public static decimal BiasAdjust(decimal raw, decimal bias, int fit, decimal alpha)
	{
		var factor = 1m + alpha * bias * fit;
		return ApplyFactor(raw, factor);
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

	/// <summary>Vega-aware IV/HV richness factor. Replaces the old kind-based fit sign with a continuous
	/// signal driven by the candidate's actual net vega per contract: long-vega positions read negative,
	/// short-vega positions read positive. Magnitude discriminates structures that share a sign — a
	/// double calendar with high vega earns a sharper boost when IV is cheap (and a sharper cut when
	/// rich) than a double diagonal whose long wings sit further OTM. <paramref name="vegaRef"/> is the
	/// $/IV-point at which the position-vega axis saturates at ±1; defaults to 3 (a meaningful
	/// long/short vol exposure).</summary>
	public static decimal? ComputeVolatilityAdjustmentFactor(decimal netVegaPerContract, decimal ivAnnual, decimal? historicalVolAnnual, decimal weight, decimal vegaRef = 3m)
	{
		if (weight <= 0m || ivAnnual <= 0m || !historicalVolAnnual.HasValue || historicalVolAnnual.Value <= 0m || vegaRef <= 0m)
			return null;

		var vegaScaled = Math.Clamp(netVegaPerContract / vegaRef, -1m, 1m);
		if (vegaScaled == 0m) return null;

		var richness = Math.Clamp(ivAnnual / historicalVolAnnual.Value - 1m, -1m, 1m);
		// Boost when (long-vega AND IV cheap) or (short-vega AND IV rich); cut otherwise. Both products
		// share a sign in the favorable cases, so the negation flips the alignment to a positive lift.
		return Math.Max(0.10m, 1m - weight * vegaScaled * richness);
	}

	public static decimal VolatilityAdjust(decimal score, decimal netVegaPerContract, decimal ivAnnual, decimal? historicalVolAnnual, decimal weight, decimal vegaRef = 3m)
	{
		var factor = ComputeVolatilityAdjustmentFactor(netVegaPerContract, ivAnnual, historicalVolAnnual, weight, vegaRef);
		return factor.HasValue ? ApplyFactor(score, factor.Value) : score;
	}

	// Max-pain and GEX depend only on (ticker, expiry, spot, chain), all constant across every candidate scored
	// in one evaluation tick, yet each was recomputed (full chain scan) per candidate. Memoize per (root, expiry)
	// keyed on the quotes object — the quotes dictionary is replaced each tick, so the cache auto-evicts and is
	// implicitly per-tick (same pattern as StrikeLadder's ChainIndex). Scoring is byte-identical, just cached.
	private static readonly ConditionalWeakTable<object, ChainScalarCache> _chainScalarCache = new();

	private sealed class ChainScalarCache
	{
		public readonly ConcurrentDictionary<(string, DateTime), decimal?> MaxPain = new();
		public readonly ConcurrentDictionary<(string, DateTime), GexResult> Gex = new();
	}

	private static decimal? MaxPainCached(string ticker, DateTime expiry, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal? spot)
	{
		if (quotes == null) return ComputeMaxPainPrice(ticker, expiry, quotes!, spot);
		return _chainScalarCache.GetValue(quotes, static _ => new ChainScalarCache())
			.MaxPain.GetOrAdd((ticker.ToUpperInvariant(), expiry.Date), _ => ComputeMaxPainPrice(ticker, expiry, quotes, spot));
	}

	private static GexResult GexCached(string ticker, DateTime expiry, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		if (quotes == null) return ComputeGex(ticker, expiry, spot, asOf, quotes!);
		return _chainScalarCache.GetValue(quotes, static _ => new ChainScalarCache())
			.Gex.GetOrAdd((ticker.ToUpperInvariant(), expiry.Date), _ => ComputeGex(ticker, expiry, spot, asOf, quotes));
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

	/// <summary>
	/// Computes the GEX gravity strike and net-gamma fraction for the target expiry.
	/// Per-strike gross gamma exposure = (callGamma × callOI + putGamma × putOI) × 100 × spot — the total
	/// dealer hedging activity at that strike. GexGravity is the strike with the highest gross exposure
	/// (matches the "gamma concentration" / "pin" definition used by Barchart, MenthorQ, and most public
	/// GEX tools). The previous net-call-minus-put rule biased toward strikes with calls and few puts and
	/// did not match what public sources call the gravity point.
	/// NetGexFraction = (totalCallGex − totalPutGex) / (totalCallGex + totalPutGex), normalized to [−1, +1];
	/// positive means call gamma dominates the chain.
	/// </summary>
	public static GexResult ComputeGex(string ticker, DateTime expiry, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var timeYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, expiry);
		var callGexByStrike = new Dictionary<decimal, double>();
		var putGexByStrike = new Dictionary<decimal, double>();
		foreach (var kv in quotes)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(kv.Key);
			if (parsed == null || !string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase) || parsed.ExpiryDate.Date != expiry.Date) continue;
			if (!kv.Value.OpenInterest.HasValue || kv.Value.OpenInterest.Value <= 0 || !kv.Value.ImpliedVolatility.HasValue || kv.Value.ImpliedVolatility.Value <= 0m) continue;
			var gamma = (double)OptionMath.Gamma(spot, parsed.Strike, timeYears, OptionMath.RiskFreeRate, kv.Value.ImpliedVolatility.Value);
			var dollars = gamma * (double)kv.Value.OpenInterest.Value * 100.0 * (double)spot;
			var bucket = parsed.CallPut == "C" ? callGexByStrike : putGexByStrike;
			bucket.TryGetValue(parsed.Strike, out var existing);
			bucket[parsed.Strike] = existing + dollars;
		}

		var allStrikes = new HashSet<decimal>(callGexByStrike.Keys);
		allStrikes.UnionWith(putGexByStrike.Keys);
		if (allStrikes.Count == 0) return new GexResult(null, 0m);

		decimal? gravity = null;
		var bestGross = double.MinValue;
		var totalCallGex = 0.0;
		var totalPutGex = 0.0;
		foreach (var strike in allStrikes)
		{
			callGexByStrike.TryGetValue(strike, out var c);
			putGexByStrike.TryGetValue(strike, out var p);
			var gross = c + p;
			if (gross > bestGross) { bestGross = gross; gravity = strike; }
			totalCallGex += c;
			totalPutGex += p;
		}

		var totalAbs = totalCallGex + totalPutGex;
		var netGexFraction = totalAbs > 0 ? (decimal)((totalCallGex - totalPutGex) / totalAbs) : 0m;
		return new GexResult(gravity, netGexFraction);
	}

	public static decimal MaxPainAdjust(decimal score, decimal? factor) => factor.HasValue ? ApplyFactor(score, factor.Value) : score;
	public static decimal GexAdjust(decimal score, decimal? factor) => factor.HasValue ? ApplyFactor(score, factor.Value) : score;
	public static decimal AssignmentRiskAdjust(decimal score, decimal? factor) => factor.HasValue ? ApplyFactor(score, factor.Value) : score;
	public static decimal StatArbAdjust(decimal score, decimal? factor) => factor.HasValue ? ApplyFactor(score, factor.Value) : score;
	public static decimal SentimentAdjust(decimal score, decimal? factor) => factor.HasValue ? ApplyFactor(score, factor.Value) : score;

	/// <summary>
	/// Contrarian Fear & Greed regime overlay. Maps the 0–100 composite to a bias signal in [−1, +1]:
	/// <c>sentimentBias = (50 − score) / 50</c>. Score=0 (extreme fear) → +1 (max contrarian bullish edge);
	/// score=100 (extreme greed) → −1 (max bearish edge); score=50 → 0.
	/// Factor = <c>max(0.10, 1 + weight × sentimentBias × directionalFit)</c>: aligned-with-crowd
	/// structures get penalized at extremes; contrarian structures get boosted. Neutral fits (fit=0
	/// for calendars/diagonals/condors) bypass the overlay (factor=1).
	/// Returns null when weight is 0 or no score is available, signaling the cascade to skip the factor.
	/// </summary>
	public static decimal? ComputeSentimentFactor(decimal? sentimentScore, int directionalFit, decimal weight)
	{
		if (weight <= 0m || !sentimentScore.HasValue) return null;
		if (directionalFit == 0) return 1m;
		var s = Math.Clamp(sentimentScore.Value, 0m, 100m);
		var bias = (50m - s) / 50m;
		return Math.Max(0.10m, 1m + weight * bias * directionalFit);
	}

	/// <summary>
	/// Per-expiry GEX result. GexGravity is the strike with the highest gross gamma×OI (calls + puts) — the
	/// gravity / pin point where dealer hedging is most concentrated.
	/// NetGexFraction is total net gamma exposure normalized to [−1, +1]: positive = call gamma dominates (suppressive),
	/// negative = put gamma dominates (amplifying).
	/// </summary>
	internal readonly record struct GexResult(decimal? GexGravity, decimal NetGexFraction);

	internal readonly record struct MarketTheoreticalAggregate(decimal MarketNet, decimal TheoreticalNet, decimal GrossTheoretical);

	/// <summary>
	/// Per-share aggregate of market mid vs Black-Scholes theoretical priced at each leg's quoted IV.
	/// Returns null if any leg lacks a two-sided live quote, IV, or yields a non-positive theoretical price.
	/// Sign convention: net = long_sum − short_sum (positive = debit, negative = credit). GrossTheoretical
	/// is the unsigned sum (theoLong + theoShort) used to normalize the edge into a unitless ratio.
	/// </summary>
	internal static MarketTheoreticalAggregate? ComputeMarketTheoreticalAggregate(
		IEnumerable<(string Symbol, OptionParsed Parsed, bool IsLong)> legs,
		decimal spot,
		DateTime asOf,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		decimal defaultIvPct)
	{
		decimal marketLong = 0m, marketShort = 0m, theoLong = 0m, theoShort = 0m;
		foreach (var leg in legs)
		{
			var live = TryLiveBidAsk(leg.Symbol, quotes);
			if (live == null) return null;
			var mid = (live.Value.bid + live.Value.ask) / 2m;

			if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.ImpliedVolatility.HasValue || q.ImpliedVolatility.Value <= 0m) return null;
			var iv = q.ImpliedVolatility.Value;

			var expirationTime = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
			var t = Math.Max(0.0, (expirationTime - asOf).TotalDays / 365.0);
			var theo = OptionMath.BlackScholes(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);
			if (theo <= 0m) return null;

			if (leg.IsLong) { marketLong += mid; theoLong += theo; }
			else { marketShort += mid; theoShort += theo; }
		}
		var gross = theoLong + theoShort;
		if (gross <= 0m) return null;
		return new MarketTheoreticalAggregate(marketLong - marketShort, theoLong - theoShort, gross);
	}

	/// <summary>
	/// Stat-arb factor: market option prices vs Black-Scholes theoretical. Edge = theoreticalNet − marketNet.
	/// Positive edge means the market entry is favorable to whoever opened — paid less than BS-fair on a debit
	/// or received more than BS-fair on a credit. Same sign for both directions because the signed-net
	/// difference encodes the direction inherently. Factor = max(0.10, 1 + weight × clamp(edge / gross, −1, 1)).
	/// </summary>
	public static decimal? ComputeStatArbAdjustmentFactor(decimal? marketNet, decimal? theoreticalNet, decimal? grossTheoretical, decimal weight)
	{
		if (weight <= 0m || !marketNet.HasValue || !theoreticalNet.HasValue || !grossTheoretical.HasValue || grossTheoretical.Value <= 0m)
			return null;
		var edge = theoreticalNet.Value - marketNet.Value;
		var relative = Math.Clamp(edge / grossTheoretical.Value, -1m, 1m);
		return Math.Max(0.10m, 1m + weight * relative);
	}

	public static decimal ComputeThetaFactor(decimal? thetaPerDayPerContract, decimal capitalAtRiskPerContract)
	{
		if (!thetaPerDayPerContract.HasValue || thetaPerDayPerContract.Value <= 0m || capitalAtRiskPerContract <= 0m)
			return 1m;

		var boost = Math.Clamp(thetaPerDayPerContract.Value / capitalAtRiskPerContract * 1.5m, 0m, 0.25m);
		return 1m + boost;
	}

	public static decimal ComputeFinalScore(decimal adjustedScore, decimal? thetaPerDayPerContract, decimal capitalAtRiskPerContract) =>
		ApplyFactor(adjustedScore, ComputeThetaFactor(thetaPerDayPerContract, capitalAtRiskPerContract));

	internal static decimal ComputeProbabilityFactor(decimal probabilityOfProfit)
	{
		var pop = Math.Clamp(probabilityOfProfit, 0m, 1m);
		var normalized = pop / 0.50m;
		var factor = normalized * normalized;
		factor *= factor;
		return Math.Clamp(factor, 0.01m, 1.25m);
	}

	/// <summary>Hard-filter gate: reject the candidate when any leg fails the OI checks. Spread is
	/// no longer a hard gate (single dominant wide quote was wiping entire chains on lightly-traded
	/// names); it still penalizes survivors through the <c>liq</c> score factor. The relative-OI
	/// check has an absolute escape hatch so it doesn't over-reject actively-traded strikes on
	/// chains where one dominant strike dwarfs every neighbor (where 6k OI nearby reads as 12% of
	/// the 50k-OI max). Returns true when no leg fails (or when liquidity stats can't be computed —
	/// defer to downstream scoring rather than discard silently).</summary>
	public static bool PassesLiquidityGate(IEnumerable<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes, OpenerLiquidityConfig cfg, decimal? spot = null, IReadOnlySet<string>? snapshotTradeable = null)
	{
		return GetLiquidityFailures(legs, quotes, cfg, spot, snapshotTradeable).Count == 0;
	}

	/// <summary>Returns the list of per-leg failure descriptions for the liquidity gate. Empty list
	/// means the candidate passes. Used by both the gate and the debug diagnostic so the rejection
	/// reason in the log line matches the actual gating decision.
	///
	/// <para><paramref name="snapshotTradeable"/>, when supplied (live opener with a daily chain snapshot),
	/// is the set of OCC symbols the snapshot confirmed are tradeable (carry a real bid/ask). A leg in that
	/// set bypasses the open-interest checks: the snapshot is the authoritative liquidity signal for
	/// thin index roots (e.g. XSP) whose far-dated strikes are market-maker-quoted but carry little/no open
	/// interest. The liquidity *factor* (scoring) still uses real OI, so these strikes score honestly low —
	/// they just aren't hard-rejected before scoring.</para></summary>
	internal static List<string> GetLiquidityFailures(IEnumerable<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes, OpenerLiquidityConfig cfg, decimal? spot, IReadOnlySet<string>? snapshotTradeable = null)
	{
		var failures = new List<string>();
		foreach (var leg in legs)
		{
			if (snapshotTradeable != null && snapshotTradeable.Contains(leg.Symbol)) continue;
			if (!quotes.TryGetValue(leg.Symbol, out var q)) continue;

			var legLiq = EffectiveLiquidity(q);
			if (legLiq.HasValue && legLiq.Value < cfg.MinOpenInterest)
				failures.Add($"{leg.Symbol} oi {legLiq.Value}<{cfg.MinOpenInterest}");

			// Relative-OI check: leg fails only if the ratio is below the relative threshold AND its
			// absolute OI is below the absolute floor. High-absolute-OI strikes always pass.
			if (cfg.MinRelativeOpenInterest > 0m && spot.HasValue && spot.Value > 0m && legLiq.HasValue && legLiq.Value > 0)
			{
				if (legLiq.Value >= cfg.MinAbsoluteOpenInterest) continue;
				var legParsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
				if (legParsed == null) continue;
				var nearbyMax = FindMaxLiquidityNearStrike(legParsed, quotes, spot.Value);
				if (nearbyMax <= 0) continue;
				var ratio = (decimal)legLiq.Value / nearbyMax;
				if (ratio < cfg.MinRelativeOpenInterest)
					failures.Add($"{leg.Symbol} relOi {ratio:P0}<{cfg.MinRelativeOpenInterest:P0} (abs {legLiq.Value}<{cfg.MinAbsoluteOpenInterest})");
			}
		}
		return failures;
	}

	/// <summary>Worst-leg bid/ask spread (as fraction of mid), minimum effective liquidity across legs,
	/// and minimum *relative* liquidity — each leg's effective liquidity as a fraction of the maximum
	/// among same-expiry strikes within ±10% of spot. "Effective liquidity" is <c>max(OI, volume)</c>:
	/// a contract with low OI but high intraday volume is being actively traded, which means market
	/// makers are engaged and exit liquidity is real even if the standing book is thin. Volume falls
	/// back to OI alone outside market hours (volume = 0 / null). Pass <paramref name="spot"/> = null
	/// to skip the relative computation.</summary>
	public static (decimal? worstSpreadPct, long? minOi, decimal? minRelOi) ComputeLegLiquidityStats(IEnumerable<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal? spot)
	{
		decimal? worstSpread = null;
		long? minOi = null;
		decimal? minRel = null;
		foreach (var leg in legs)
		{
			if (!quotes.TryGetValue(leg.Symbol, out var q)) continue;
			if (q.Bid.HasValue && q.Ask.HasValue && q.Ask.Value > 0m)
			{
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				if (mid > 0m)
				{
					var spreadPct = (q.Ask.Value - q.Bid.Value) / mid;
					if (worstSpread == null || spreadPct > worstSpread.Value)
						worstSpread = spreadPct;
				}
			}
			var legLiq = EffectiveLiquidity(q);
			if (legLiq.HasValue && (minOi == null || legLiq.Value < minOi.Value))
				minOi = legLiq.Value;

			if (spot.HasValue && spot.Value > 0m && legLiq.HasValue && legLiq.Value > 0)
			{
				var legParsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
				if (legParsed != null)
				{
					var nearbyMax = FindMaxLiquidityNearStrike(legParsed, quotes, spot.Value);
					if (nearbyMax > 0)
					{
						var ratio = (decimal)legLiq.Value / nearbyMax;
						if (minRel == null || ratio < minRel.Value)
							minRel = ratio;
					}
				}
			}
		}
		return (worstSpread, minOi, minRel);
	}

	/// <summary>Combined OI/volume liquidity proxy: <c>max(OI, volume)</c>. Volume captures contracts
	/// that may have low standing OI but are being actively traded today (market makers engaged,
	/// real exit demand). OI captures stable holders. The max gives credit to either signal —
	/// after-hours when volume is null/zero this collapses to OI alone.</summary>
	private static long? EffectiveLiquidity(OptionContractQuote q)
	{
		var oi = q.OpenInterest ?? -1;
		var vol = q.Volume ?? -1;
		if (oi < 0 && vol < 0) return null;
		return Math.Max(Math.Max(oi, 0), Math.Max(vol, 0));
	}

	/// <summary>Maximum effective liquidity (max OI/volume) across same-expiry, same-call/put strikes
	/// within ±10% of spot. Used to detect sub-grid strikes by comparing a leg's liquidity to the
	/// local maximum.</summary>
	private static long FindMaxLiquidityNearStrike(OptionParsed leg, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal spot)
	{
		long maxLiq = 0;
		foreach (var (sym, q) in quotes)
		{
			var liq = EffectiveLiquidity(q);
			if (!liq.HasValue || liq.Value <= maxLiq) continue;
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null) continue;
			if (!p.Root.Equals(leg.Root, StringComparison.OrdinalIgnoreCase)) continue;
			if (p.ExpiryDate.Date != leg.ExpiryDate.Date) continue;
			if (p.CallPut != leg.CallPut) continue;
			if (Math.Abs(p.Strike - spot) / spot > 0.10m) continue;
			maxLiq = liq.Value;
		}
		return maxLiq;
	}

	/// <summary>Worst-leg liquidity penalty. Combines three signals into a multiplicative factor in
	/// [0.20, 1.00]: bid/ask spread as fraction of mid, absolute open interest, and *relative* OI vs
	/// neighboring strikes at the same expiry. The relative-OI component is what catches sub-grid
	/// strikes — e.g., a $26.50 strike with OI 67 next to $26 / $27 strikes with OI 1000+ — even when
	/// the absolute OI clears the floor. Returns null when no signals are available. Weight scales how
	/// hard the factor bites: weight=0 returns 1.0; weight=1 applies the full penalty curve.</summary>
	public static decimal? ComputeLiquidityFactor(decimal? worstLegSpreadPct, long? minOpenInterest, decimal? minRelativeOi, decimal weight)
	{
		if (weight <= 0m) return null;
		if (!worstLegSpreadPct.HasValue && !minOpenInterest.HasValue && !minRelativeOi.HasValue) return null;

		// Spread component: 1.0 when spread ≤ 5%, decays toward 0 at 50% spread.
		var spreadComponent = 1m;
		if (worstLegSpreadPct is decimal s)
		{
			var raw = s <= 0.05m ? 1m : Math.Max(0m, 1m - (s - 0.05m) / 0.45m);
			spreadComponent = (decimal)Math.Sqrt((double)Math.Clamp(raw, 0m, 1m));
		}

		// Absolute OI component: 1.0 when OI ≥ 200, drops to ~0.31 at OI=20, hits floor at OI < 5.
		var oiComponent = 1m;
		if (minOpenInterest is long oi)
		{
			if (oi < 5) oiComponent = 0.20m;
			else
			{
				var ratio = (decimal)oi / 200m;
				oiComponent = (decimal)Math.Sqrt((double)Math.Clamp(ratio, 0m, 1m));
				oiComponent = Math.Max(oiComponent, 0.30m);
			}
		}

		// Relative OI component: ratio of leg's OI to max OI among same-expiry near-spot strikes.
		// Catches sub-grid strikes that have absolute OI > floor but pale next to neighbors. Hits the
		// floor at relOI ≤ 5% (severe outlier) and reaches 1.0 at relOI ≥ 80% (this is the local max).
		var relOiComponent = 1m;
		if (minRelativeOi is decimal rel)
		{
			var clamped = Math.Clamp(rel, 0m, 1m);
			relOiComponent = (decimal)Math.Sqrt((double)clamped);
			relOiComponent = Math.Max(relOiComponent, 0.20m);
		}

		// Multiplicative combine + power-curve weight: factor = combined^weight, clamp [0.20, 1.0].
		// More aggressive than the prior `1 − weight × (1 − combined)` form: at weight=0.5 a "combined"
		// of 0.10 (terrible across all signals) maps to factor ≈ 0.32 instead of 0.55, so a structurally
		// illiquid leg can no longer outrank a clean alternative on raw EV alone.
		var combined = spreadComponent * oiComponent * relOiComponent;
		var factor = (decimal)Math.Pow((double)combined, (double)weight);
		return Math.Clamp(factor, 0.20m, 1m);
	}

	internal static decimal ComputeCapitalScaleFactor(decimal capitalAtRiskPerContract)
	{
		if (capitalAtRiskPerContract <= 0m)
			return 0m;

		var scaled = capitalAtRiskPerContract / (capitalAtRiskPerContract + 100m);
		return Math.Clamp((decimal)Math.Sqrt((double)scaled), 0.35m, 1m);
	}

	/// <summary>Counts trading days (Mon–Fri) strictly between <paramref name="asOf"/> and
	/// <paramref name="target"/>. US market holidays are not subtracted — for EM scaling on typical
	/// 1–60 DTE structures the weekend exclusion captures ~95% of the calendar-vs-trading-day gap and
	/// avoids carrying a holiday calendar around just for this signal.</summary>
	internal static int CountTradingDays(DateTime asOf, DateTime target)
	{
		if (target.Date <= asOf.Date) return 0;
		var count = 0;
		for (var d = asOf.Date.AddDays(1); d <= target.Date; d = d.AddDays(1))
		{
			if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
				count++;
		}
		return count;
	}

	/// <summary>Path-aware safety factor: ratio of nearest-breakeven distance to one-sigma expected
	/// move over the holding period (<c>spot × IV × √(tradingDays/252)</c>), squashed through tanh. The
	/// existing SetupFactor measures static centeredness inside the BE band; this one measures how
	/// much vol-time cushion the band provides before the underlying is statistically likely to
	/// touch it. Narrow-wing IBs on high-vol single stocks (e.g. a 1.5-wide GME IB at 65% IV with
	/// 7 DTE) score ~0.30; wider structures on the same name and tight indexes score above 0.6.
	/// Null when inputs are degenerate; falls to the 0.10 floor when spot has already crossed the
	/// safe band for a two-BE structure. Trading-day denominator matches retail-desk EM convention
	/// (vol doesn't realize over weekends) — numerically close to calendar/365 on long DTE but
	/// meaningfully different on short-DTE structures spanning a weekend.</summary>
	internal static decimal? ComputeBreakevenRoomFactor(decimal spot, decimal iv, int tradingDaysToTarget, IReadOnlyList<decimal> breakevens)
	{
		if (spot <= 0m || iv <= 0m || tradingDaysToTarget <= 0 || breakevens.Count == 0) return null;
		var years = (decimal)tradingDaysToTarget / 252m;
		var expectedMove = spot * iv * (decimal)Math.Sqrt((double)years);
		if (expectedMove <= 0m) return null;

		decimal edgeDistance;
		if (breakevens.Count >= 2)
		{
			var lower = breakevens.Min();
			var upper = breakevens.Max();
			if (upper <= lower) return null;
			if (spot < lower || spot > upper) return 0.10m;
			edgeDistance = Math.Min(spot - lower, upper - spot);
		}
		else
		{
			edgeDistance = Math.Abs(spot - breakevens[0]);
		}

		var safetyRatio = edgeDistance / expectedMove;
		var factor = (decimal)Math.Tanh((double)safetyRatio);
		return Math.Clamp(factor, 0.10m, 1m);
	}

	/// <summary>One-sigma expected-move price envelope: <c>(spot − EM, spot + EM)</c> where
	/// <c>EM = spot × IV × √(tradingDays/252)</c>. Trading-day denominator mirrors the EM-based
	/// scoring factors (BreakevenRoomFactor, ExpectedMoveCreditFactor) so the rendered range lines
	/// up with what those factors evaluated against. Null when inputs are degenerate.</summary>
	internal static (decimal Lower, decimal Upper)? ComputeExpectedMoveBounds(decimal spot, decimal iv, int tradingDaysToTarget)
	{
		if (spot <= 0m || iv <= 0m || tradingDaysToTarget <= 0) return null;
		var years = (decimal)tradingDaysToTarget / 252m;
		var em = spot * iv * (decimal)Math.Sqrt((double)years);
		if (em <= 0m) return null;
		return (spot - em, spot + em);
	}

	/// <summary>EM-vs-short-strike safety factor for credit trades. Measures how many one-sigma
	/// expected moves of cushion exist between spot and the nearest short strike — the price at
	/// which assignment risk and the loss zone begin. Distinct from BreakevenRoomFactor: the credit
	/// cushion makes BE look ~credit/share further from spot than the short strike, overstating
	/// safety. Only fires on credit trades (<paramref name="debitOrCreditPerContract"/> ≥ 0);
	/// returns null on debit trades, no shorts, or degenerate inputs. Signal is a linear ramp
	/// centered on 1σ cushion: −1 at ≤0.5σ (or spot already past a short), +1 at ≥1.5σ. Factor:
	/// <c>max(0.10, 1 + weight × signal)</c>.</summary>
	internal static decimal? ComputeExpectedMoveCreditFactor(decimal spot, decimal iv, int tradingDaysToTarget, decimal debitOrCreditPerContract, IReadOnlyList<(decimal Strike, string CallPut)> shortLegs, decimal weight)
	{
		if (weight <= 0m || debitOrCreditPerContract < 0m || spot <= 0m || iv <= 0m || tradingDaysToTarget <= 0 || shortLegs.Count == 0)
			return null;
		var years = (decimal)tradingDaysToTarget / 252m;
		var expectedMove = spot * iv * (decimal)Math.Sqrt((double)years);
		if (expectedMove <= 0m) return null;

		var minSafeDistance = decimal.MaxValue;
		foreach (var (strike, callPut) in shortLegs)
		{
			// Loss begins when spot crosses the short strike on the wrong side: above K for short calls,
			// below K for short puts. Positive signed-distance = spot is still on the safe side.
			var signedDistance = callPut == "C" ? (strike - spot) : (spot - strike);
			var distanceEm = signedDistance / expectedMove;
			if (distanceEm < minSafeDistance) minSafeDistance = distanceEm;
		}
		if (minSafeDistance == decimal.MaxValue) return null;

		var signal = Math.Clamp((minSafeDistance - 1m) / 0.5m, -1m, 1m);
		return Math.Max(0.10m, 1m + weight * signal);
	}

	/// <summary>"Is the trade fighting the vol regime?" factor. Distinct from the vega-aware
	/// VolatilityAdjustmentFactor: that one scales by net-vega magnitude (small for plain credit
	/// verticals, large for calendars/diagonals); this one fires on trade-type sign alone, so even
	/// near-zero-vega credit/debit structures still get a regime read. Credit trades favored when
	/// IV &gt; HV (rich premium to collect); debit trades favored when IV &lt; HV (cheap premium
	/// to pay). Premium clamped to ±100% so a 3× IV/HV blowout doesn't dominate the chain. Null
	/// when HV unavailable or weight = 0.</summary>
	internal static decimal? ComputeIvRealizedPremiumFactor(decimal iv, decimal? historicalVolAnnual, decimal debitOrCreditPerContract, decimal weight)
	{
		if (weight <= 0m || iv <= 0m || !historicalVolAnnual.HasValue || historicalVolAnnual.Value <= 0m)
			return null;
		var premium = Math.Clamp(iv / historicalVolAnnual.Value - 1m, -1m, 1m);
		var isCredit = debitOrCreditPerContract >= 0m;
		var signal = isCredit ? premium : -premium;
		return Math.Max(0.10m, 1m + weight * signal);
	}

	private static IReadOnlyList<(decimal Strike, string CallPut)> ExtractShortLegStrikes(IReadOnlyList<ProposalLeg> legs)
	{
		var result = new List<(decimal, string)>();
		foreach (var leg in legs)
		{
			if (leg.Action != "sell") continue;
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed != null) result.Add((parsed.Strike, parsed.CallPut));
		}
		return result;
	}

	internal static decimal? ComputeSetupFactor(OpenStructureKind kind, decimal spot, IReadOnlyList<decimal> breakevens)
	{
		// Setup factor measures spot's centeredness in a two-breakeven safety zone, so we gate on the
		// breakeven count rather than the structure kind — a directionally-classified diagonal still has
		// two breakevens and benefits from the same comfort signal.
		if (breakevens.Count < 2 || spot <= 0m)
			return null;

		var lower = breakevens.Min();
		var upper = breakevens.Max();
		if (upper <= lower)
			return null;

		var halfWidth = (upper - lower) / 2m;
		if (halfWidth <= 0m)
			return null;

		var center = (lower + upper) / 2m;
		var edgeDistance = Math.Min(Math.Abs(spot - lower), Math.Abs(upper - spot));
		var safetyRatio = Math.Clamp(edgeDistance / halfWidth, 0m, 1m);
		var centerOffsetRatio = Math.Clamp(Math.Abs(spot - center) / halfWidth, 0m, 1m);
		var edgeFactor = Math.Clamp((decimal)Math.Sqrt((double)safetyRatio), 0.10m, 1m);
		var centerFactor = Math.Clamp(1m - centerOffsetRatio * centerOffsetRatio, 0.10m, 1m);
		// Arithmetic mean of the two components rather than the product. The multiplicative form
		// double-counted "centered + safe" because both terms move together for any well-placed
		// trade, which structurally over-rewarded wide-band combos (double calendars / diagonals)
		// where centering and safety come "for free" with the structure, vs single-side calendars
		// at OTM strikes where spot is *necessarily* off-center relative to its own narrow band.
		// AM preserves the directional signal (centered + safe > off-center + edgy) while
		// compressing the range, letting raw EV/cap/day actually win out on equally-good
		// candidates of different shape.
		return Math.Clamp((edgeFactor + centerFactor) / 2m, 0.10m, 1m);
	}

	internal static decimal? ComputeAdjustmentRunwayFactor(CandidateSkeleton skel, DateTime asOf, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var targetExpiry = skel.TargetExpiry.Date;
		var targetDte = Math.Max(1, (targetExpiry - asOf.Date).Days);
		var longLegs = skel.Legs
			.Where(l => l.Action == "buy")
			.Select(l => (Leg: l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.Where(x => x.Parsed != null && x.Parsed.ExpiryDate.Date > targetExpiry)
			.Select(x => (x.Leg, Parsed: x.Parsed!))
			.ToList();
		if (longLegs.Count == 0)
			return null;

		decimal totalRatio = 0m;
		var counted = 0;
		foreach (var (leg, parsed) in longLegs)
		{
			var q = TryLiveBidAsk(leg.Symbol, quotes);
			if (q == null)
				continue;

			var mid = (q.Value.bid + q.Value.ask) / 2m;
			var intrinsic = parsed.CallPut == "C" ? Math.Max(0m, spot - parsed.Strike) : Math.Max(0m, parsed.Strike - spot);
			var extrinsic = Math.Max(0m, mid - intrinsic);
			var extrinsicRatio = mid > 0m ? Math.Clamp(extrinsic / mid, 0m, 1m) : 0m;
			var residualDays = Math.Max(0, (parsed.ExpiryDate.Date - targetExpiry).Days);
			var runwayRatio = Math.Clamp((decimal)residualDays / Math.Max(7m, targetDte), 0m, 4m);
			totalRatio += extrinsicRatio * runwayRatio;
			counted++;
		}
		if (counted == 0)
			return null;

		var averageRatio = totalRatio / counted;
		return Math.Clamp(1m + 0.18m * averageRatio, 1m, 1.35m);
	}

	public static decimal? ComputeAssignmentRiskFactor(CandidateSkeleton skel, decimal spot, DateTime asOf, decimal strikeStep, decimal technicalBias)
	{
		// Cash-settled index options (SPX/SPXW/NDX/XSP/RUT/DJX/VIX) are European: they cannot be
		// exercised early, so early-assignment risk is structurally zero. Applying the penalty here was
		// incorrect — it docked credit/neutral structures (short verticals, iron condors/butterflies)
		// ~0.10–0.21× on exactly these roots for a risk that cannot occur, burying flat-day premium
		// trades far below minScoreToOpen. No penalty for these roots.
		if (OptionSettlement.IsCashSettledIndex(skel.Ticker)) return null;

		var shortLegs = skel.Legs
			.Where(l => l.Action == "sell")
			.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol))
			.Where(p => p != null)
			.Select(p => p!)
			.ToList();
		if (shortLegs.Count == 0)
			return null;

		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var step = Math.Max(0.01m, strikeStep);
		decimal totalPenalty = 0m;
		foreach (var shortLeg in shortLegs)
			totalPenalty += AnalyzePositionCommand.ComputeAssignmentRiskPenaltyPerContract(spot, shortLeg.Strike, shortLeg.CallPut, daysToTarget, step, technicalBias);

		if (totalPenalty <= 0m)
			return null;

		var scale = Math.Max(100m, step * 100m * shortLegs.Count * 5m);
		return Math.Max(0.10m, 1m / (1m + totalPenalty / scale));
	}

	/// <summary>
	/// Builds 5 scenario points at S_T ∈ {spot·e^(−sigmaRange·σ), spot·e^(−sigmaRange·σ/2), spot,
	/// spot·e^(+sigmaRange·σ/2), spot·e^(+sigmaRange·σ)} where σ = ivAnnual · √years.
	/// Weights = log-normal density at each point, renormalized to sum to 1. Neutral drift.
	/// sigmaRange defaults to 1.0 (±1σ and ±0.5σ). Larger values test further-out scenarios and
	/// overweight fat tails, favoring unbounded-upside structures over pin/theta structures.
	/// </summary>
	public static IReadOnlyList<ScenarioPoint> BuildScenarioGrid(decimal spot, decimal ivAnnual, double years, decimal sigmaRange = 1.0m, decimal biasShiftSigmas = 0m)
	{
		var sigma = (double)ivAnnual * Math.Sqrt(Math.Max(1e-9, years));
		var range = (double)sigmaRange;
		// biasShiftSigmas (typically bias × cfg.Weights.BiasDrift) translates the grid center along the
		// log-spot axis. Positive shifts scenarios up; negative shifts down. Allows long-premium
		// structures to score positive raw EV under strong directional signals — sign-symmetric
		// ApplyFactor downstream can't flip a negative score, so the bias must be encoded here.
		var drift = (double)biasShiftSigmas * sigma;
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
			var sT = (decimal)((double)spot * Math.Exp(drift + multipliers[i] * sigma));
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

	/// <summary>Returns the IV that, when fed to Black-Scholes, reproduces the option's market mid
	/// `(bid + ask) / 2`. This calibrated IV is essential for EV math on calendars/diagonals where the
	/// long leg survives to target with time value: scoring against the broker's reported IV would
	/// produce theoretical exit prices that exceed the market mid (the wider the bid/ask spread, the
	/// larger the gap), creating phantom alpha for illiquid contracts. Using the market-implied IV
	/// keeps entry pricing (market mid) and exit pricing (BS at calibrated IV) on the same scale.
	/// Falls back to <see cref="ResolveIv"/> when bid/ask is missing, mid ≤ intrinsic, or the solver
	/// fails to converge.</summary>
	public static decimal MarketImpliedIv(string symbol, OptionParsed parsed, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal defaultPct)
	{
		var brokerIv = ResolveIv(symbol, quotes, defaultPct);
		if (!quotes.TryGetValue(symbol, out var q)) return brokerIv;
		if (!q.Bid.HasValue || !q.Ask.HasValue) return brokerIv;
		if (q.Bid.Value < 0m || q.Ask.Value <= 0m) return brokerIv;
		var mid = (q.Bid.Value + q.Ask.Value) / 2m;
		if (mid <= 0m) return brokerIv;

		var intrinsic = OptionMath.Intrinsic(spot, parsed.Strike, parsed.CallPut);
		if (mid <= intrinsic) return brokerIv;

		var expirationTime = parsed.ExpiryDate.Date + OptionMath.MarketClose;
		var t = (expirationTime - asOf).TotalDays / 365.0;
		if (t <= 0) return brokerIv;
		try
		{
			var iv = OptionMath.ImpliedVol(spot, parsed.Strike, t, OptionMath.RiskFreeRate, mid, parsed.CallPut);
			if (iv > 0m && iv < 5m) return iv;
		}
		catch { }
		return brokerIv;
	}

	/// <summary>Looks up bid/ask, returning null if any leg lacks a usable two-sided quote.</summary>
	public static (decimal bid, decimal ask)? TryLiveBidAsk(string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		if (!quotes.TryGetValue(symbol, out var q)) return null;
		if (!q.Bid.HasValue || !q.Ask.HasValue) return null;
		if (q.Bid.Value < 0m || q.Ask.Value <= 0m) return null;
		return (q.Bid.Value, q.Ask.Value);
	}

	private readonly record struct ResolvedLegPrice(decimal Bid, decimal Ask, bool UsedFallback);

	private static ResolvedLegPrice? ResolveLegPrice(string symbol, OptionParsed parsed, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal defaultIv)
	{
	   if (!quotes.TryGetValue(symbol, out _))
			return null;

		var live = TryLiveBidAsk(symbol, quotes);
		if (live.HasValue)
			return new ResolvedLegPrice(live.Value.bid, live.Value.ask, UsedFallback: false);

		var iv = ResolveIv(symbol, quotes, defaultIv);
		var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, parsed.ExpiryDate);
		var theoretical = OptionMath.BlackScholes(spot, parsed.Strike, years, OptionMath.RiskFreeRate, iv, parsed.CallPut);
		return theoretical > 0m ? new ResolvedLegPrice(theoretical, theoretical, UsedFallback: true) : null;
	}

	private static string? BuildPricingWarning(bool usedFallback) => usedFallback
		? "Warning: fallback Black-Scholes pricing used for one or more legs because live bid/ask was unavailable."
		: null;

	private static decimal ResolveStrikeStep(OpenerConfig cfg, string ticker) =>
		cfg.Indicators.StrikeStep > 0m ? cfg.Indicators.StrikeStep : 1.0m;

	/// <summary>sha1-hex fingerprint of (ticker | kind | sorted legs | qty). Stable across ticks.</summary>
	public static string ComputeFingerprint(string ticker, OpenStructureKind kind, IReadOnlyList<ProposalLeg> legs, int qty)
	{
		var sortedLegs = string.Join("|", legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}").OrderBy(s => s, StringComparer.Ordinal));
		var payload = $"{ticker}|{kind}|{sortedLegs}|{qty}";
		using var sha = System.Security.Cryptography.SHA1.Create();
		var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	/// <summary>Score a candidate. <paramref name="applyLiquidityGate"/> controls whether the hard
	/// liquidity filter (<see cref="PassesLiquidityGate"/>) can return null to reject the candidate
	/// entirely. The gate is meant for the opener pipeline to skip doomed-exit *new* candidates;
	/// callers analyzing *existing* positions should pass false so the scorer always returns a
	/// proposal — the liq factor and rules still surface in the rationale either way.
	///
	/// <paramref name="useMarketImpliedIv"/> controls whether the long-leg IV is back-solved from the
	/// market mid (true, default) or taken straight from the broker's reported IV (false). Callers
	/// running at a hypothetical spot (e.g., <c>--spot</c> override) should pass false,
	/// because the market mid was set at a different spot and back-solving against it produces a
	/// nonsensical IV that collapses calendar/diagonal residual time value.</summary>
	public static OpenProposal? Score(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid, bool applyLiquidityGate = true, bool useMarketImpliedIv = true, decimal? sentimentScore = null, TickerEvents? events = null, IReadOnlySet<string>? snapshotTradeable = null) => skel.StructureKind switch
	{
		OpenStructureKind.LongCall or OpenStructureKind.LongPut => ScoreLongCallPut(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode, applyLiquidityGate, sentimentScore, events, snapshotTradeable),
		OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => ScoreShortVertical(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode, applyLiquidityGate, sentimentScore, events, snapshotTradeable),
		OpenStructureKind.LongCallVertical or OpenStructureKind.LongPutVertical => ScoreLongVertical(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode, applyLiquidityGate, sentimentScore, events, snapshotTradeable),
		OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal => ScoreCalendarOrDiagonal(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode, applyLiquidityGate, useMarketImpliedIv, sentimentScore, events, snapshotTradeable),
		OpenStructureKind.DoubleCalendar or OpenStructureKind.DoubleDiagonal or OpenStructureKind.IronButterfly or OpenStructureKind.IronCondor or OpenStructureKind.DiagonalVertical or OpenStructureKind.CalendarVertical => ScoreMultiLeg(skel, spot, asOf, quotes, bias, cfg, historicalVolAnnual, pricingMode, applyLiquidityGate, useMarketImpliedIv, sentimentScore, events, snapshotTradeable),
		_ => null
	};

	public static string BuildRationale(OpenProposal p, decimal bias, OpenerConfig cfg)
	{
		var cashSide = p.DebitOrCreditPerContract >= 0m
			? $"credit ${p.DebitOrCreditPerContract:F2}"
			: $"debit ${-p.DebitOrCreditPerContract:F2}";

		var techAdjusted = BiasAdjust(p.RawScore, bias, p.DirectionalFit, cfg.Weights.DirectionalFit);
		var biasEffectPct = cfg.Weights.DirectionalFit * bias * p.DirectionalFit * 100m;
		var biasTag = p.DirectionalFit == 0
			? $"[tech {bias:+0.00;-0.00}, fit 0 → no tech adjustment]"
			: $"[tech {bias:+0.00;-0.00}, fit {p.DirectionalFit:+0;-0} → {biasEffectPct:+0;-0}% {(biasEffectPct >= 0 ? "tech boost" : "tech cut")}]";

		var bePart = p.Breakevens.Count > 0 ? $"BE ${string.Join("/", p.Breakevens.Select(b => b.ToString("F2")))}" : "";
		var emPart = (p.ExpectedMoveLower.HasValue && p.ExpectedMoveUpper.HasValue)
			? $"EM ${p.ExpectedMoveLower.Value:F2}/{p.ExpectedMoveUpper.Value:F2}"
			: "";
		var beStr = (bePart, emPart) switch
		{
			("", "") => "",
			("", _) => $"{emPart}, ",
			(_, "") => $"{bePart}, ",
			_ => $"{bePart}, {emPart}, "
		};

		// R/R and premium_ratio surface the asymmetry/cushion factors that BalanceFactor folds into the
		// score. Showing them inline lets the reader compare two similarly-scored trades by their shape.
		var rr = Math.Abs(p.MaxLossPerContract) > 0m ? Math.Max(0m, p.MaxProfitPerContract / Math.Abs(p.MaxLossPerContract)) : 0m;
		var ratioStr = p.PremiumRatio.HasValue ? $", prem {p.PremiumRatio.Value:F2}x" : "";
		// Capital efficiency uses the cash actually deployed (debit) when the structure was a debit
		// trade; for credit structures the deployed capital is the margin held against max-loss,
		// which equals CapitalAtRiskPerContract. Mirrors the divisor used inside ScoreCalendarOrDiagonal
		// / ScoreMultiLeg so the rationale's recomputed factors line up with the actual score.
		var debitPaid = Math.Max(0m, -p.DebitOrCreditPerContract);
		var efficiencyCapital = debitPaid > 0m ? debitPaid : p.CapitalAtRiskPerContract;
		var popFactor = ComputeProbabilityFactor(p.ProbabilityOfProfit);
		var scaleFactor = ComputeCapitalScaleFactor(efficiencyCapital);
		var setupFactor = p.SetupFactor;
		var balance = BalanceFactor(p.MaxProfitPerContract, p.MaxLossPerContract, p.PremiumRatio ?? 1m);
		var factorParts = new List<string>
		{
			$"pop {popFactor:F2}",
			$"scale {scaleFactor:F2}",
		};
		if (setupFactor.HasValue)
			factorParts.Add($"setup {setupFactor.Value:F2}");
		if (p.RunwayFactor.HasValue)
			factorParts.Add($"runway {p.RunwayFactor.Value:F2}");
		if (p.BreakevenRoomFactor.HasValue)
			factorParts.Add($"be-room {p.BreakevenRoomFactor.Value:F2}");
		if (p.ExpectedMoveCreditFactor.HasValue)
			factorParts.Add($"em-cred {p.ExpectedMoveCreditFactor.Value:F2}");
		if (p.IvRealizedPremiumFactor.HasValue)
			factorParts.Add($"iv-rv {p.IvRealizedPremiumFactor.Value:F2}");
		factorParts.Add($"bal {balance:F2}");
		if (p.LiquidityAdjustmentFactor.HasValue)
			factorParts.Add($"liq {p.LiquidityAdjustmentFactor.Value:F2}");

		var indicatorParts = new List<string>();
		if (p.LiquidityAdjustmentFactor.HasValue)
		{
			var spreadStr = p.WorstLegBidAskSpreadPct is decimal s ? $"{(s * 100m).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}%" : "n/a";
			var liqStr = p.MinOpenInterest.HasValue ? p.MinOpenInterest.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";
			var relStr = p.MinRelativeOpenInterest is decimal rel ? $"{(rel * 100m).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}%" : "n/a";
			indicatorParts.Add($"worst-leg spread {spreadStr}, min OI/vol {liqStr} (rel {relStr}) → liq {p.LiquidityAdjustmentFactor.Value:F2}");
		}
		string? volDetail = null;
		if (p.VolatilityAdjustmentFactor.HasValue && p.ImpliedVolatilityAnnual.HasValue && p.HistoricalVolatilityAnnual.HasValue && p.HistoricalVolatilityAnnual.Value > 0m)
		{
			var richness = p.ImpliedVolatilityAnnual.Value / p.HistoricalVolatilityAnnual.Value;
			factorParts.Add($"vol {p.VolatilityAdjustmentFactor.Value:F2}");
			var vegaStr = p.NetVegaPerContract is decimal v
				? $", ν {v.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture)}/IV pt"
				: "";
			volDetail = $"rep IV {p.ImpliedVolatilityAnnual.Value:P1} / underlying HV {p.HistoricalVolatilityAnnual.Value:P1} = {richness:F2}x{vegaStr} → vol {p.VolatilityAdjustmentFactor.Value:F2}";
			indicatorParts.Add(volDetail);
		}
		if (p.MaxPainAdjustmentFactor.HasValue && p.TargetExpiryMaxPain.HasValue)
		{
			factorParts.Add($"pain {p.MaxPainAdjustmentFactor.Value:F2}");
			indicatorParts.Add($"max-pain target ${p.TargetExpiryMaxPain.Value:F2} → pain {p.MaxPainAdjustmentFactor.Value:F2}");
		}
		if (p.GexAdjustmentFactor.HasValue)
		{
			factorParts.Add($"gex {p.GexAdjustmentFactor.Value:F2}");
			var gravityStr = p.GexGravity.HasValue ? $"gravity ${p.GexGravity.Value:F2}, " : "";
			var envStr = p.NetGexFraction.HasValue ? $"net gamma {p.NetGexFraction.Value:+0.00;-0.00}" : "";
			indicatorParts.Add($"GEX {gravityStr}{envStr} → gex {p.GexAdjustmentFactor.Value:F2}");
		}
		if (p.AssignmentRiskFactor.HasValue)
			factorParts.Add($"assign {p.AssignmentRiskFactor.Value:F2}");
		if (p.StatArbAdjustmentFactor.HasValue && p.MarketNetPremiumPerShare.HasValue && p.TheoreticalNetPremiumPerShare.HasValue)
		{
			factorParts.Add($"arb {p.StatArbAdjustmentFactor.Value:F2}");
			var edge = p.TheoreticalNetPremiumPerShare.Value - p.MarketNetPremiumPerShare.Value;
			indicatorParts.Add($"market net ${p.MarketNetPremiumPerShare.Value:+0.00;-0.00} / theoretical net ${p.TheoreticalNetPremiumPerShare.Value:+0.00;-0.00}, edge ${edge:+0.00;-0.00}/share → arb {p.StatArbAdjustmentFactor.Value:F2}");
		}
		if (p.SentimentAdjustmentFactor.HasValue && p.MarketSentimentScore.HasValue)
		{
			factorParts.Add($"sentiment {p.SentimentAdjustmentFactor.Value:F2}");
			var rating = p.MarketSentimentRating ?? SentimentRating.FromScore(p.MarketSentimentScore.Value);
			indicatorParts.Add($"F&G {p.MarketSentimentScore.Value:F0}/100 ({rating}), fit {p.DirectionalFit:+0;-0} → sentiment {p.SentimentAdjustmentFactor.Value:F2}");
		}
		var finalScore = p.FinalScore ?? ComputeFinalScore(p.BiasAdjustedScore, p.ThetaPerDayPerContract, efficiencyCapital);
		var thetaFactor = ComputeThetaFactor(p.ThetaPerDayPerContract, efficiencyCapital);

		// Theta factor is the last multiplicand in the factors chain when present, so the line goes
		// tech-adjusted → … → final in one calculation rather than splitting at "adjusted".
		if (p.ThetaPerDayPerContract.HasValue)
			factorParts.Add($"theta factor {thetaFactor:F2} ({p.ThetaPerDayPerContract.Value:+0.00;-0.00}/day on ${efficiencyCapital:F0} deployed)");

		var realizedTag = p.RealizedExpectedValuePerContract.HasValue
			? $" (real ${p.RealizedExpectedValuePerContract.Value:F2}{(p.EstimatedSlippagePerContract is decimal slip && slip > 0m ? $", −${slip:F2} fric" : "")})"
			: "";
		var rationaleLine = $"{cashSide}, maxProfit ${p.MaxProfitPerContract:F2}, maxLoss ${-p.MaxLossPerContract:F2}, R/R {rr:F2}{ratioStr}, {beStr}POP {p.ProbabilityOfProfit * 100m:F1}%, EV ${p.ExpectedValuePerContract:F2}{realizedTag}";
		var scoreLine = $"raw {p.RawScore:F6} → tech-adjusted {techAdjusted:F6} {biasTag} → final {finalScore:F6}";
		// Factors can chain 10+ multiplicands; balance into at most two lines so neither wraps mid-token
		// and we don't waste vertical space with a third nearly-empty row.
		// `\v` is a section-internal soft-break that the renderer expands to "\n + label-aligned padding"
		// — the outer `\n` join below splits sections, so we can't reuse it inside the factors string.
		var factorsPerLine = Math.Max(1, (factorParts.Count + 1) / 2);
		var factorChunks = factorParts
			.Select((part, idx) => (part, idx))
			.GroupBy(x => x.idx / factorsPerLine)
			.Select(g => string.Join(" × ", g.Select(x => x.part)))
			.ToList();
		var factorsBody = factorChunks.Count > 1
			? string.Join("\v× ", factorChunks)
			: factorChunks[0];
		var factorsLine = $"tech-adjusted × {factorsBody} = final {finalScore:F6}";

		// Indicator parts each get their own line so a 4+ indicator panel doesn't wrap. The renderer
		// labels the first one "Indicators:" and gives subsequent ones an empty label so they align
		// underneath as continuation rows. Section order: rationale → indicators → score → factors,
		// so Indicators (the explanation) precedes the Score and Factors (the math).
		if (indicatorParts.Count > 0)
		{
			var sections = new List<string> { rationaleLine };
			sections.AddRange(indicatorParts);
			sections.Add(scoreLine);
			sections.Add(factorsLine);
			return string.Join("\n", sections);
		}

		return string.Join("\n", new[] { rationaleLine, scoreLine, factorsLine });
	}

	private static decimal? ComputeMaxPainAdjustmentFactor(CandidateSkeleton skel, decimal spot, DateTime asOf, decimal targetIv, decimal? maxPain, OpenerConfig cfg, IReadOnlyList<decimal>? breakevens = null)
	{
		if (cfg.Weights.MaxPain <= 0m || !maxPain.HasValue || spot <= 0m || targetIv <= 0m)
			return null;

		var targetYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, skel.TargetExpiry);
		var expectedMove = Math.Max(ResolveStrikeStep(cfg, skel.Ticker), spot * targetIv * (decimal)Math.Sqrt(targetYears));
		var signal = ComputeMaxPainSignal(skel, spot, maxPain.Value, expectedMove, breakevens);
		return Math.Max(0.10m, 1m + cfg.Weights.MaxPain * signal);
	}

	/// <summary>
	/// GEX adjustment factor. Combines two signals:
	///   Pin signal (60%): how well the GEX gravity pin aligns with the position, using the same positional logic as max pain.
	///   Environment signal (40%): whether the net dealer gamma regime (suppressive vs. amplifying) favors the structure's vol profile.
	/// Returns null when gexWeight = 0 or when IV data is insufficient to compute gamma.
	/// </summary>
	private static decimal? ComputeGexAdjustmentFactor(CandidateSkeleton skel, decimal spot, DateTime asOf, decimal targetIv, GexResult gex, OpenerConfig cfg, IReadOnlyList<decimal>? breakevens = null)
	{
		if (cfg.Weights.Gex <= 0m || spot <= 0m || targetIv <= 0m) return null;
		var targetYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, skel.TargetExpiry);
		var expectedMove = Math.Max(ResolveStrikeStep(cfg, skel.Ticker), spot * targetIv * (decimal)Math.Sqrt(targetYears));
		var pinSignal = gex.GexGravity.HasValue ? ComputeMaxPainSignal(skel, spot, gex.GexGravity.Value, expectedMove, breakevens) : 0m;
		var envSignal = Math.Clamp(gex.NetGexFraction * VolatilityFitSign(skel.StructureKind), -1m, 1m);
		var combined = gex.GexGravity.HasValue ? 0.60m * pinSignal + 0.40m * envSignal : envSignal;
		return Math.Max(0.10m, 1m + cfg.Weights.Gex * combined);
	}

	internal static decimal ComputeMaxPainSignal(CandidateSkeleton skel, decimal spot, decimal maxPain, decimal expectedMove, IReadOnlyList<decimal>? breakevens = null)
	{
		var scale = Math.Max(0.01m, expectedMove);
		var fit = DirectionalFit.SignFor(skel);
		if (fit != 0)
			return Math.Clamp(((maxPain - spot) / scale) * fit, -1m, 1m);

		var shortStrikes = skel.Legs
			 .Where(l => l.Action == "sell")
			 .Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol))
			 .Where(p => p != null)
			 .Select(p => p!.Strike)
			 .OrderBy(s => s)
			 .ToList();
		if (shortStrikes.Count == 0)
			return 0m;

		var pinSignal = ShortStrikeRangeSignal(shortStrikes, maxPain, scale);
		var sideSignal = ShortStrikeSideSignal(shortStrikes, spot, maxPain);
		var beSignal = BreakevenBandSignal(maxPain, scale, breakevens);

		if (breakevens != null && breakevens.Count >= 2)
			return Math.Clamp(0.45m * beSignal + 0.35m * sideSignal + 0.20m * pinSignal, -1m, 1m);

		return Math.Clamp(0.65m * sideSignal + 0.35m * pinSignal, -1m, 1m);
	}

	private static decimal ShortStrikeRangeSignal(IReadOnlyList<decimal> shortStrikes, decimal maxPain, decimal scale)
	{
		var lower = shortStrikes.Min();
		var upper = shortStrikes.Max();
		if (maxPain >= lower && maxPain <= upper)
			return 1m;

		var distance = maxPain < lower ? lower - maxPain : maxPain - upper;
		return 1m - 2m * Math.Clamp(distance / scale, 0m, 1m);
	}

	private static decimal ShortStrikeSideSignal(IReadOnlyList<decimal> shortStrikes, decimal spot, decimal maxPain)
	{
		var lower = shortStrikes.Min();
		var upper = shortStrikes.Max();
		if (maxPain >= lower && maxPain <= upper)
			return 1m;

		var anchor = (lower + upper) / 2m;
		return SameSideOfSpotSignal(anchor - spot, maxPain - spot);
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
	/// Folds a path-dependent stop into a terminal-only EV by blending: with probability
	/// <c>P_hit</c> the stop fires intra-period (P&L = −stopPct × |maxLoss| − friction); with
	/// probability <c>1 − P_hit</c> the position rides to expiry with the terminal EV the caller
	/// already computed. Friction is subtracted in both branches so the result is comparable to
	/// the raw <c>realizedEv</c> input. Returns <paramref name="realizedEv"/> unchanged when
	/// realizedExpectancy is disabled (no stop modeled). Conservative: barriers chosen by the
	/// caller (typically break-even levels) sit closer to spot than the actual stop trigger, so
	/// P_hit slightly overestimates real stop firing.
	/// </summary>
	internal static decimal AdjustEvForBarrier(decimal realizedEv, decimal spot, double ivAnnual, double years,
		decimal? lowerBarrier, decimal? upperBarrier, decimal maxLossSigned, decimal frictionPerContract,
		OpenerRealizedExpectancyConfig cfg)
	{
		if (!cfg.Enabled) return realizedEv;
		var hitProb = ComputeBarrierHitProbability(spot, ivAnnual, years, lowerBarrier, upperBarrier);
		if (hitProb <= 0m) return realizedEv;
		var stopLossAtBarrier = -cfg.StopLossPctOfMaxLoss * Math.Abs(maxLossSigned) - frictionPerContract;
		return (1m - hitProb) * realizedEv + hitProb * stopLossAtBarrier;
	}

	/// <summary>
	/// Probability that <paramref name="spot"/>, evolving as zero-drift GBM with vol
	/// <paramref name="ivAnnual"/> over <paramref name="years"/>, touches either of two price
	/// barriers at any point during the interval. Closed-form single-side first-passage formula:
	/// <c>P(min S_t ≤ B) = 2·N(ln(B/S)/σ√T)</c> for B &lt; S, and the symmetric upper version.
	/// Combines two barriers via union bound (capped at 0.99) — slightly conservative for tight
	/// two-sided traps where the bounds overlap, exact for distant one-sided cases.
	/// Returns 0 when inputs are degenerate; returns 1 when spot has already crossed a barrier.
	/// </summary>
	public static decimal ComputeBarrierHitProbability(decimal spot, double ivAnnual, double years, decimal? lowerBarrier, decimal? upperBarrier)
	{
		if (spot <= 0m || ivAnnual <= 0 || years <= 0) return 0m;
		if (!lowerBarrier.HasValue && !upperBarrier.HasValue) return 0m;
		var sigmaSqrtT = ivAnnual * Math.Sqrt(years);

		double pLower = 0.0, pUpper = 0.0;
		if (lowerBarrier.HasValue && lowerBarrier.Value > 0m)
		{
			if (spot <= lowerBarrier.Value) pLower = 1.0;
			else
			{
				var z = Math.Log((double)lowerBarrier.Value / (double)spot) / sigmaSqrtT;
				pLower = Math.Min(1.0, 2.0 * OptionMath.NormalCdf(z));
			}
		}
		if (upperBarrier.HasValue && upperBarrier.Value > 0m)
		{
			if (spot >= upperBarrier.Value) pUpper = 1.0;
			else
			{
				var z = -Math.Log((double)upperBarrier.Value / (double)spot) / sigmaSqrtT;
				pUpper = Math.Min(1.0, 2.0 * OptionMath.NormalCdf(z));
			}
		}
		return (decimal)Math.Min(0.99, pLower + pUpper);
	}

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

	public static OpenProposal? ScoreShortVertical(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid, bool applyLiquidityGate = true, decimal? sentimentScore = null, TickerEvents? events = null, IReadOnlySet<string>? snapshotTradeable = null)
	{
		if (applyLiquidityGate && EventVeto.ShouldVeto(skel, asOf, events, cfg.Indicators.Events, out _)) return null;
		var shortLeg = skel.Legs.First(l => l.Action == "sell");
		var longLeg = skel.Legs.First(l => l.Action == "buy");
		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		if (shortParsed == null || longParsed == null) return null;

		var shortQ = ResolveLegPrice(shortLeg.Symbol, shortParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var longQ = ResolveLegPrice(longLeg.Symbol, longParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		if (shortQ == null || longQ == null) return null;
		if (applyLiquidityGate && !PassesLiquidityGate(skel.Legs, quotes, cfg.Liquidity, spot, snapshotTradeable)) return null;
		var pricingWarning = BuildPricingWarning(shortQ.Value.UsedFallback || longQ.Value.UsedFallback);

		var shortMid = (shortQ.Value.Bid + shortQ.Value.Ask) / 2m;
		var longMid = (longQ.Value.Bid + longQ.Value.Ask) / 2m;
		var creditPerShare = PriceForSell(shortMid, shortQ.Value.Bid, pricingMode) - PriceForBuy(longMid, longQ.Value.Ask, pricingMode);
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

		var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, skel.TargetExpiry);
		var iv = ResolveIv(shortLeg.Symbol, quotes, cfg.Indicators.IvDefaultPct);

		// POP = P(S_T inside profitable side of breakeven).
		var pop = LogNormalProbability(isCall ? Direction.Below : Direction.Above, spot, breakeven, years, (double)iv);

	   // EV via scenario grid — payoff at expiry is piecewise linear.
		var grid = BuildScenarioGrid(spot, iv, years, cfg.ScenarioGridSigma, bias * cfg.Weights.BiasDrift);
		decimal PnlAtExpiry(decimal sT) => VerticalPnLAtExpiry(sT, shortParsed.Strike, longParsed.Strike, creditPerContract, isCall);
		decimal ev = 0m;
		foreach (var pt in grid)
			ev += pt.Weight * PnlAtExpiry(pt.SpotAtExpiry);

		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var friction = RealizedExpectancy.ComputeFrictionPerContract(cfg.RealizedExpectancy, skel.StructureKind);
		var realizedEv = RealizedExpectancy.RealizeEv(grid, PnlAtExpiry, maxProfit, maxLoss, friction, cfg.RealizedExpectancy);
		// Terminal-EV scoring. The previous barrier-aware adjustment was asymmetric across structure
		// types — applied to multi-leg credit structures but not to single-leg longs, which let
		// LongCall/LongPut candidates skate past more conservative calendars/diagonals even when the
		// directional trade had worse POP and worse expected value. BreakevenRoomFactor and the
		// sign-symmetric factor chain already encode path safety as a ranking signal in a more
		// proportionate way that applies consistently across all structures.
		var rawScore = ComputeRawScore(realizedEv, daysToTarget, capitalAtRisk);
		var fit = DirectionalFit.SignFor(skel);
		var popFactor = ComputeProbabilityFactor(pop);
		var scaleFactor = ComputeCapitalScaleFactor(capitalAtRisk);
		var setupFactor = ComputeSetupFactor(skel.StructureKind, spot, [breakeven]);
		// CountTradingDays returns 0 for same-day (0DTE) targets, which silently nulls the EM-based
		// factors (breakevenRoom, expectedMoveBounds, expectedMoveCredit) since their guard rejects
		// tradingDays <= 0. A 0DTE position at the morning open is exposed to one full session of
		// vol — that's 1 trading day's worth — so clamp to ≥ 1 to keep those factors firing.
		var tradingDaysToTarget = Math.Max(1, CountTradingDays(asOf, skel.TargetExpiry.Date));
		var breakevenRoomFactor = ComputeBreakevenRoomFactor(spot, iv, tradingDaysToTarget, [breakeven]);
		var expectedMoveBounds = ComputeExpectedMoveBounds(spot, iv, tradingDaysToTarget);
		var shortLegStrikes = ExtractShortLegStrikes(skel.Legs);
		var expectedMoveCreditFactor = ComputeExpectedMoveCreditFactor(spot, iv, tradingDaysToTarget, creditPerContract, shortLegStrikes, cfg.Weights.ExpectedMoveCredit);
		var ivRealizedPremiumFactor = ComputeIvRealizedPremiumFactor(iv, historicalVolAnnual, creditPerContract, cfg.Weights.IvRealizedPremium);
		var longIv = ResolveIv(longLeg.Symbol, quotes, cfg.Indicators.IvDefaultPct);
		var legGreeks = new[]
		{
			(Parsed: shortParsed, Iv: iv, IsLong: false),
			(Parsed: longParsed, Iv: longIv, IsLong: true)
		};
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(legGreeks, asOf, spot);
		var netVegaPerContract = ComputeNetVegaPerContract(legGreeks, asOf, spot);
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
		var representativeIv = (iv + longIv) / 2m;
		var volFactor = ComputeVolatilityAdjustmentFactor(netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit);
		var maxPain = MaxPainCached(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, iv, maxPain, cfg);
		var gex = GexCached(skel.Ticker, skel.TargetExpiry.Date, spot, asOf, quotes);
		var gexFactor = ComputeGexAdjustmentFactor(skel, spot, asOf, iv, gex, cfg);
		var assignmentFactor = ComputeAssignmentRiskFactor(skel, spot, asOf, ResolveStrikeStep(cfg, skel.Ticker), bias);
		var statArb = ComputeMarketTheoreticalAggregate(
			new (string, OptionParsed, bool)[] { (shortLeg.Symbol, shortParsed, false), (longLeg.Symbol, longParsed, true) },
			spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var statArbFactor = ComputeStatArbAdjustmentFactor(statArb?.MarketNet, statArb?.TheoreticalNet, statArb?.GrossTheoretical, cfg.Weights.StatArb);
		var (worstSpread, minOi, minRelOi) = ComputeLegLiquidityStats(skel.Legs, quotes, spot);
		var liquidityFactor = ComputeLiquidityFactor(worstSpread, minOi, minRelOi, cfg.Liquidity.Weight);
		var sentimentFactor = ComputeSentimentFactor(sentimentScore, fit, cfg.Weights.Sentiment);
		var biasAdjBase = BiasAdjust(rawScore, bias, fit, cfg.Weights.DirectionalFit);
		var afterFactors = ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(biasAdjBase, popFactor), scaleFactor), setupFactor ?? 1m), breakevenRoomFactor ?? 1m), expectedMoveCreditFactor ?? 1m), ivRealizedPremiumFactor ?? 1m), balance), liquidityFactor ?? 1m);
		var biasAdj = SentimentAdjust(StatArbAdjust(AssignmentRiskAdjust(GexAdjust(MaxPainAdjust(VolatilityAdjust(afterFactors, netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit), maxPainFactor), gexFactor), assignmentFactor), statArbFactor), sentimentFactor);
		var finalScore = ComputeFinalScore(biasAdj, thetaPerDayPerContract, capitalAtRisk);
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
			PricingWarning: pricingWarning,
			PremiumRatio: premiumRatio,
			ImpliedVolatilityAnnual: representativeIv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			GexGravity: gex.GexGravity,
			NetGexFraction: gex.NetGexFraction,
			GexAdjustmentFactor: gexFactor,
			SetupFactor: setupFactor,
			AssignmentRiskFactor: assignmentFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract,
			NetVegaPerContract: netVegaPerContract,
			MarketNetPremiumPerShare: statArb?.MarketNet,
			TheoreticalNetPremiumPerShare: statArb?.TheoreticalNet,
			StatArbAdjustmentFactor: statArbFactor,
			FinalScore: finalScore,
			WorstLegBidAskSpreadPct: worstSpread,
			MinOpenInterest: minOi,
			MinRelativeOpenInterest: minRelOi,
			LiquidityAdjustmentFactor: liquidityFactor,
			MarketSentimentScore: sentimentScore,
			MarketSentimentRating: sentimentScore.HasValue ? SentimentRating.FromScore(sentimentScore.Value) : null,
			SentimentAdjustmentFactor: sentimentFactor,
			RealizedExpectedValuePerContract: cfg.RealizedExpectancy.Enabled ? realizedEv : null,
			EstimatedSlippagePerContract: cfg.RealizedExpectancy.Enabled ? friction : null,
			ProfitTargetPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.ProfitTargetPerContract(maxProfit, cfg.RealizedExpectancy) : null,
			StopLossPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.StopLossPerContract(maxLoss, cfg.RealizedExpectancy) : null,
			BreakevenRoomFactor: breakevenRoomFactor,
			ExpectedMoveCreditFactor: expectedMoveCreditFactor,
			IvRealizedPremiumFactor: ivRealizedPremiumFactor,
			ExpectedMoveLower: expectedMoveBounds?.Lower,
			ExpectedMoveUpper: expectedMoveBounds?.Upper
		);
	}

	/// <summary>Score a long (debit) vertical: bull call spread or bear put spread. Mirror of
	/// <see cref="ScoreShortVertical"/> with the cash-flow direction inverted (we pay debit, not
	/// receive credit). Capital-at-risk = debit paid; max profit = width − debit; both bounded.
	/// Like <see cref="ScoreLongCallPut"/> the popFactor penalty is skipped — debit verticals are
	/// positive-skew bets where a low POP is the trade's structural feature, not a defect, and
	/// applying ComputeProbabilityFactor would unfairly demote them against credit spreads.</summary>
	public static OpenProposal? ScoreLongVertical(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid, bool applyLiquidityGate = true, decimal? sentimentScore = null, TickerEvents? events = null, IReadOnlySet<string>? snapshotTradeable = null)
	{
		// Long-only structures aren't event-vetoed (no short leg to take assignment risk on).
		_ = events;
		var shortLeg = skel.Legs.First(l => l.Action == "sell");
		var longLeg = skel.Legs.First(l => l.Action == "buy");
		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		if (shortParsed == null || longParsed == null) return null;

		var shortQ = ResolveLegPrice(shortLeg.Symbol, shortParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var longQ = ResolveLegPrice(longLeg.Symbol, longParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		if (shortQ == null || longQ == null) return null;
		if (applyLiquidityGate && !PassesLiquidityGate(skel.Legs, quotes, cfg.Liquidity, spot, snapshotTradeable)) return null;
		var pricingWarning = BuildPricingWarning(shortQ.Value.UsedFallback || longQ.Value.UsedFallback);

		var shortMid = (shortQ.Value.Bid + shortQ.Value.Ask) / 2m;
		var longMid = (longQ.Value.Bid + longQ.Value.Ask) / 2m;
		var debitPerShare = PriceForBuy(longMid, longQ.Value.Ask, pricingMode) - PriceForSell(shortMid, shortQ.Value.Bid, pricingMode);
		if (debitPerShare <= 0m) return null; // not a debit spread at these quotes

		var debitPerContract = debitPerShare * 100m;
		var width = Math.Abs(shortParsed.Strike - longParsed.Strike);
		var maxProfitPerContract = width * 100m - debitPerContract;
		if (maxProfitPerContract <= 0m) return null; // debit larger than width = no upside

		var capitalAtRisk = debitPerContract;
		var maxProfit = maxProfitPerContract;
		var maxLoss = -debitPerContract;

		var isCall = skel.StructureKind == OpenStructureKind.LongCallVertical;
		// Breakeven: long leg's strike adjusted by debit (call: above; put: below). LongCallVertical's
		// long leg is the LOWER strike (we paid for the call); profit appears when S_T > longStrike + debit.
		var breakeven = isCall
			? longParsed.Strike + debitPerShare
			: longParsed.Strike - debitPerShare;

		var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, skel.TargetExpiry);
		var iv = ResolveIv(longLeg.Symbol, quotes, cfg.Indicators.IvDefaultPct);

		// POP = P(S_T past breakeven in the profitable direction).
		var pop = LogNormalProbability(isCall ? Direction.Above : Direction.Below, spot, breakeven, years, (double)iv);

		var grid = BuildScenarioGrid(spot, iv, years, cfg.ScenarioGridSigma, bias * cfg.Weights.BiasDrift);
		// VerticalPnLAtExpiry uses signed netEntry where credit=positive. For a debit spread we pay,
		// so pass -debitPerContract as the "credit" parameter.
		decimal PnlAtExpiry(decimal sT) => VerticalPnLAtExpiry(sT, shortParsed.Strike, longParsed.Strike, -debitPerContract, isCall);
		decimal ev = 0m;
		foreach (var pt in grid)
			ev += pt.Weight * PnlAtExpiry(pt.SpotAtExpiry);

		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var friction = RealizedExpectancy.ComputeFrictionPerContract(cfg.RealizedExpectancy, skel.StructureKind);
		var realizedEv = RealizedExpectancy.RealizeEv(grid, PnlAtExpiry, maxProfit, maxLoss, friction, cfg.RealizedExpectancy);
		var rawScore = ComputeRawScore(realizedEv, daysToTarget, capitalAtRisk);
		var fit = DirectionalFit.SignFor(skel);
		// popFactor skipped — see method-level XML comment.
		var popFactor = 1m;
		var scaleFactor = ComputeCapitalScaleFactor(capitalAtRisk);
		var setupFactor = ComputeSetupFactor(skel.StructureKind, spot, [breakeven]);
		// CountTradingDays returns 0 for same-day (0DTE) targets, which silently nulls the EM-based
		// factors (breakevenRoom, expectedMoveBounds, expectedMoveCredit) since their guard rejects
		// tradingDays <= 0. A 0DTE position at the morning open is exposed to one full session of
		// vol — that's 1 trading day's worth — so clamp to ≥ 1 to keep those factors firing.
		var tradingDaysToTarget = Math.Max(1, CountTradingDays(asOf, skel.TargetExpiry.Date));
		var breakevenRoomFactor = ComputeBreakevenRoomFactor(spot, iv, tradingDaysToTarget, [breakeven]);
		var expectedMoveBounds = ComputeExpectedMoveBounds(spot, iv, tradingDaysToTarget);
		var ivRealizedPremiumFactor = ComputeIvRealizedPremiumFactor(iv, historicalVolAnnual, -debitPerContract, cfg.Weights.IvRealizedPremium);
		var longIv = ResolveIv(longLeg.Symbol, quotes, cfg.Indicators.IvDefaultPct);
		var legGreeks = new[]
		{
			(Parsed: shortParsed, Iv: iv, IsLong: false),
			(Parsed: longParsed, Iv: longIv, IsLong: true)
		};
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(legGreeks, asOf, spot);
		var netVegaPerContract = ComputeNetVegaPerContract(legGreeks, asOf, spot);
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
		var representativeIv = (iv + longIv) / 2m;
		var volFactor = ComputeVolatilityAdjustmentFactor(netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit);
		var maxPain = MaxPainCached(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, iv, maxPain, cfg);
		var gex = GexCached(skel.Ticker, skel.TargetExpiry.Date, spot, asOf, quotes);
		var gexFactor = ComputeGexAdjustmentFactor(skel, spot, asOf, iv, gex, cfg);
		var assignmentFactor = ComputeAssignmentRiskFactor(skel, spot, asOf, ResolveStrikeStep(cfg, skel.Ticker), bias);
		var statArb = ComputeMarketTheoreticalAggregate(
			new (string, OptionParsed, bool)[] { (shortLeg.Symbol, shortParsed, false), (longLeg.Symbol, longParsed, true) },
			spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var statArbFactor = ComputeStatArbAdjustmentFactor(statArb?.MarketNet, statArb?.TheoreticalNet, statArb?.GrossTheoretical, cfg.Weights.StatArb);
		var (worstSpread, minOi, minRelOi) = ComputeLegLiquidityStats(skel.Legs, quotes, spot);
		var liquidityFactor = ComputeLiquidityFactor(worstSpread, minOi, minRelOi, cfg.Liquidity.Weight);
		var sentimentFactor = ComputeSentimentFactor(sentimentScore, fit, cfg.Weights.Sentiment);
		// Directional-conviction gate (debit verticals are directional long premium too): de-rate when
		// the trade-aligned bias is weak. Disabled by default (factor 1.0).
		var convictionFactor = cfg.LongConvictionGate.Factor(bias * fit);
		var biasAdjBase = BiasAdjust(rawScore, bias, fit, cfg.Weights.DirectionalFit);
		var afterFactors = ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(biasAdjBase, popFactor), scaleFactor), setupFactor ?? 1m), breakevenRoomFactor ?? 1m), ivRealizedPremiumFactor ?? 1m), balance), liquidityFactor ?? 1m), convictionFactor);
		var biasAdj = SentimentAdjust(StatArbAdjust(AssignmentRiskAdjust(GexAdjust(MaxPainAdjust(VolatilityAdjust(afterFactors, netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit), maxPainFactor), gexFactor), assignmentFactor), statArbFactor), sentimentFactor);
		var finalScore = ComputeFinalScore(biasAdj, thetaPerDayPerContract, capitalAtRisk);
		var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

		return new OpenProposal(
			Ticker: skel.Ticker,
			StructureKind: skel.StructureKind,
			Legs: PriceLegs(skel.Legs, quotes),
			Qty: 1,
			DebitOrCreditPerContract: -debitPerContract,   // negative = debit paid
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
			PricingWarning: pricingWarning,
			PremiumRatio: premiumRatio,
			ImpliedVolatilityAnnual: representativeIv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			GexGravity: gex.GexGravity,
			NetGexFraction: gex.NetGexFraction,
			GexAdjustmentFactor: gexFactor,
			SetupFactor: setupFactor,
			AssignmentRiskFactor: assignmentFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract,
			NetVegaPerContract: netVegaPerContract,
			MarketNetPremiumPerShare: statArb?.MarketNet,
			TheoreticalNetPremiumPerShare: statArb?.TheoreticalNet,
			StatArbAdjustmentFactor: statArbFactor,
			FinalScore: finalScore,
			WorstLegBidAskSpreadPct: worstSpread,
			MinOpenInterest: minOi,
			MinRelativeOpenInterest: minRelOi,
			LiquidityAdjustmentFactor: liquidityFactor,
			MarketSentimentScore: sentimentScore,
			MarketSentimentRating: sentimentScore.HasValue ? SentimentRating.FromScore(sentimentScore.Value) : null,
			SentimentAdjustmentFactor: sentimentFactor,
			RealizedExpectedValuePerContract: cfg.RealizedExpectancy.Enabled ? realizedEv : null,
			EstimatedSlippagePerContract: cfg.RealizedExpectancy.Enabled ? friction : null,
			ProfitTargetPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.ProfitTargetPerContract(maxProfit, cfg.RealizedExpectancy) : null,
			StopLossPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.StopLossPerContract(maxLoss, cfg.RealizedExpectancy) : null,
			BreakevenRoomFactor: breakevenRoomFactor,
			ExpectedMoveCreditFactor: null,
			IvRealizedPremiumFactor: ivRealizedPremiumFactor,
			ExpectedMoveLower: expectedMoveBounds?.Lower,
			ExpectedMoveUpper: expectedMoveBounds?.Upper
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
		EventVetoed,
		LiquidityGated,
	}

	internal static ShortVerticalRejectReason DiagnoseShortVerticalRejection(
		CandidateSkeleton skel,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		out string detail,
		DateTime asOf = default,
		OpenerConfig? cfg = null,
		TickerEvents? events = null,
		decimal spot = 0m)
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

		// Mirror ScoreShortVertical's null-return order: event veto first, then quote checks, then
		// liquidity gate, then credit/risk checks. Without this order Unknown swallows legitimate
		// reasons that fire before the credit math runs.
		if (cfg != null && EventVeto.ShouldVeto(skel, asOf, events, cfg.Indicators.Events, out var vetoReason))
		{
			detail = vetoReason ?? "event_vetoed";
			return ShortVerticalRejectReason.EventVetoed;
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

		if (cfg != null)
		{
			var failures = GetLiquidityFailures(skel.Legs, quotes, cfg.Liquidity, spot > 0m ? spot : (decimal?)null);
			if (failures.Count > 0)
			{
				detail = string.Join("; ", failures);
				return ShortVerticalRejectReason.LiquidityGated;
			}
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
	internal static decimal BalanceFactor(decimal maxProfit, decimal maxLoss, decimal premiumRatio)
	{
		var lossAbs = Math.Abs(maxLoss);
		var rr = lossAbs > 0m ? Math.Max(0m, maxProfit / lossAbs) : 0m;
		var ratio = Math.Max(1m, premiumRatio);
		if (rr <= 0m)
			return 0m;

		var rrComponent = (decimal)Math.Sqrt((double)Math.Min(rr, 3m));
		var ratioPenalty = (decimal)Math.Sqrt((double)ratio);
		// Floor at 0.05, not 0.25. The higher floor was clamping out real signal: genuinely
		// asymmetric structures (R/R 0.14 with premium ratio 12, computing factor 0.108) got
		// pulled back up to 0.25 and rode the rest of the score chain on the strength of POP
		// alone — exactly the "POP 80%, R/R 0.14" pattern where a single stop slip or gap
		// destroys multiple winning trades. 0.05 is enough headroom to keep BalanceFactor from
		// completely zeroing the score on pathological inputs, but lets the factor actually
		// reflect proportionate badness on the lower end.
		return Math.Clamp(rrComponent / ratioPenalty, 0.05m, 1.25m);
	}

	private static int VolatilityFitSign(OpenStructureKind kind) => StructureKindInfo.VolatilityFitSign(kind);
	private readonly record struct MultiLegDefinition(ProposalLeg Proposal, OptionParsed Parsed, bool IsLong, decimal Iv);

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

	private static decimal NetEntryPerContract(IReadOnlyList<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes, string pricingMode)
	{
		decimal netPerShare = 0m;
		foreach (var leg in legs)
		{
			var q = TryLiveBidAsk(leg.Symbol, quotes);
			if (q == null) return 0m;
			var mid = (q.Value.bid + q.Value.ask) / 2m;
			var price = leg.Action == "buy" ? PriceForBuy(mid, q.Value.ask, pricingMode) : PriceForSell(mid, q.Value.bid, pricingMode);
			netPerShare += leg.Action == "buy" ? -price * leg.Qty : price * leg.Qty;
		}
		return netPerShare * 100m;
	}

	private static decimal OptionValueAtTarget(decimal spotAtTarget, DateTime targetExpiry, OptionParsed parsed, decimal iv)
	{
		if (parsed.ExpiryDate.Date <= targetExpiry.Date)
			return parsed.CallPut == "C" ? Math.Max(0m, spotAtTarget - parsed.Strike) : Math.Max(0m, parsed.Strike - spotAtTarget);

		var years = Math.Max(1, (parsed.ExpiryDate.Date - targetExpiry.Date).Days) / 365.0;
		return OptionMath.BlackScholes(spotAtTarget, parsed.Strike, years, OptionMath.RiskFreeRate, iv, parsed.CallPut);
	}

	private static decimal PositionValueAtTarget(decimal spotAtTarget, DateTime targetExpiry, IReadOnlyList<MultiLegDefinition> legs)
	{
		decimal total = 0m;
		foreach (var leg in legs)
		{
			var valuePerShare = OptionValueAtTarget(spotAtTarget, targetExpiry, leg.Parsed, leg.Iv);
			var signed = leg.IsLong ? valuePerShare : -valuePerShare;
			total += signed * leg.Proposal.Qty * 100m;
		}
		return total;
	}

	private static (decimal lower, decimal upper) GetScanRange(decimal spot, IReadOnlyList<MultiLegDefinition> legs, decimal step)
	{
		var minStrike = legs.Min(l => l.Parsed.Strike);
		var maxStrike = legs.Max(l => l.Parsed.Strike);
		var lower = Math.Max(0.01m, Math.Min(spot * 0.40m, minStrike - 4m * step));
		var upper = Math.Max(spot * 1.60m, maxStrike + 4m * step);
		return (lower, upper);
	}

	/// <summary>The narrow price window FindBreakevens needs to search — distinct from <see cref="GetScanRange"/>,
	/// which stays wide for the max-profit/loss scan. Breakevens of these structures can't sit arbitrarily far
	/// from the strikes: when EVERY leg expires at the target the payoff is flat outside [minStrike, maxStrike],
	/// so a few strikes of margin is provably complete. When a leg SURVIVES past the target (calendar / diagonal /
	/// DV long leg) the payoff still slopes beyond the outer strikes, so the margin widens to six standard
	/// deviations of the survivor's residual move — comfortably past any realistic crossing. This replaces
	/// scanning the old spot×0.40–1.60 window, which on SPX-class tickers ran ~18k steps at $0.50, almost all in
	/// deep wings that never hold a breakeven. Max profit/loss are unaffected (they still use the wide range), so
	/// only the breakeven SEARCH narrows — POP depends solely on the breakevens, which are unchanged in practice.</summary>
	private static (decimal lower, decimal upper) BreakevenScanRange(decimal spot, IReadOnlyList<MultiLegDefinition> legs, decimal step, DateTime targetExpiry, decimal representativeIv)
	{
		var minStrike = legs.Min(l => l.Parsed.Strike);
		var maxStrike = legs.Max(l => l.Parsed.Strike);
		var longestExpiry = legs.Max(l => l.Parsed.ExpiryDate.Date);
		var residualYears = Math.Max(0.0, (longestExpiry - targetExpiry.Date).TotalDays / 365.0);
		var margin = residualYears > 0.0
			? Math.Max(4m * step, 6m * spot * representativeIv * (decimal)Math.Sqrt(residualYears))
			: 4m * step;
		return (Math.Max(0.01m, minStrike - margin), maxStrike + margin);
	}

	/// <summary>Closed-form breakevens for the simple credit structures whose payoff is fully determined by
	/// strikes + credit. Returns an empty list for structures that don't have a closed form (calendars,
	/// diagonals) so the caller falls back to numerical scanning. The piecewise-linear payoff of an
	/// iron condor or iron butterfly crosses zero at <c>short_strike ± credit_per_share</c>.</summary>
	private static IReadOnlyList<decimal> TryAnalyticalBreakevens(CandidateSkeleton skel, IReadOnlyList<MultiLegDefinition> defs, decimal netEntryPerContract)
	{
		// Net credit per share: negative netEntry means we received credit. Per-contract → per-share by /100.
		var creditPerShare = -netEntryPerContract / 100m;
		if (creditPerShare <= 0m) return Array.Empty<decimal>(); // not a credit structure

		switch (skel.StructureKind)
		{
			case OpenStructureKind.IronCondor:
			case OpenStructureKind.IronButterfly:
			{
				// Identify short strikes. IronCondor has one short put + one short call (different strikes);
				// IronButterfly has both shorts at the same body strike.
				var shorts = defs.Where(d => !d.IsLong).ToList();
				var shortPut = shorts.FirstOrDefault(d => d.Parsed.CallPut == "P");
				var shortCall = shorts.FirstOrDefault(d => d.Parsed.CallPut == "C");
				if (shortPut.Parsed == null || shortCall.Parsed == null) return Array.Empty<decimal>();
				var lowerBe = shortPut.Parsed.Strike - creditPerShare;
				var upperBe = shortCall.Parsed.Strike + creditPerShare;
				if (lowerBe <= 0m || upperBe <= lowerBe) return Array.Empty<decimal>();
				return new[] { lowerBe, upperBe };
			}
			default:
				return Array.Empty<decimal>();
		}
	}

	private static IReadOnlyList<decimal> FindBreakevens(Func<decimal, decimal> pnl, decimal lower, decimal upper, int steps = 240)
	{
		var roots = new List<decimal>();
		var stepSize = (upper - lower) / steps;
		var sPrev = lower;
		var pnlPrev = pnl(sPrev);
		for (var i = 1; i <= steps; i++)
		{
			var sNext = lower + stepSize * i;
			var pnlNext = pnl(sNext);
			if ((pnlPrev < 0m && pnlNext >= 0m) || (pnlPrev >= 0m && pnlNext < 0m))
				roots.Add(BisectBreakeven(pnl, sPrev, sNext));
			sPrev = sNext;
			pnlPrev = pnlNext;
		}
		return roots;
	}

	private static decimal ProbabilityBetween(decimal spot, decimal? lower, decimal? upper, double years, decimal ivAnnual)
	{
		if (lower.HasValue && upper.HasValue)
			return LogNormalProbability(Direction.Above, spot, lower.Value, years, (double)ivAnnual) - LogNormalProbability(Direction.Above, spot, upper.Value, years, (double)ivAnnual);
		if (lower.HasValue)
			return LogNormalProbability(Direction.Above, spot, lower.Value, years, (double)ivAnnual);
		if (upper.HasValue)
			return LogNormalProbability(Direction.Below, spot, upper.Value, years, (double)ivAnnual);
		return 1m;
	}

	private static decimal ComputeProbabilityOfProfit(Func<decimal, decimal> pnl, IReadOnlyList<decimal> breakevens, decimal spot, double years, decimal ivAnnual)
	{
		decimal pop = 0m;
		for (var i = 0; i <= breakevens.Count; i++)
		{
			decimal? lower = i == 0 ? null : breakevens[i - 1];
			decimal? upper = i == breakevens.Count ? null : breakevens[i];
			var sample = lower.HasValue && upper.HasValue
				? (lower.Value + upper.Value) / 2m
				: lower.HasValue
					? Math.Max(lower.Value + 1m, lower.Value * 1.50m)
					: upper.HasValue
						? Math.Max(0.01m, upper.Value / 2m)
						: spot;
			if (pnl(sample) > 0m)
				pop += ProbabilityBetween(spot, lower, upper, years, ivAnnual);
		}
		return Math.Clamp(pop, 0m, 1m);
	}

	private static (decimal maxProfit, decimal maxLoss) ScanPnlRange(Func<decimal, decimal> pnl, decimal lower, decimal upper, int steps = 360)
	{
		var stepSize = (upper - lower) / steps;
		var maxProfit = decimal.MinValue;
		var maxLoss = decimal.MaxValue;
		for (var i = 0; i <= steps; i++)
		{
			var s = lower + stepSize * i;
			var value = pnl(s);
			if (value > maxProfit) maxProfit = value;
			if (value < maxLoss) maxLoss = value;
		}
		return (maxProfit, maxLoss);
	}

	public static OpenProposal? ScoreMultiLeg(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid, bool applyLiquidityGate = true, bool useMarketImpliedIv = true, decimal? sentimentScore = null, TickerEvents? events = null, IReadOnlySet<string>? snapshotTradeable = null)
	{
		if (applyLiquidityGate && EventVeto.ShouldVeto(skel, asOf, events, cfg.Indicators.Events, out _)) return null;
		var defs = new List<MultiLegDefinition>(skel.Legs.Count);
		var usedFallback = false;
		foreach (var leg in skel.Legs)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed == null) return null;
			var resolved = ResolveLegPrice(leg.Symbol, parsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
			if (resolved == null) return null;
			usedFallback |= resolved.Value.UsedFallback;
			// Legs that survive past the target (e.g., long wings of a double calendar/diagonal) need
			// market-implied IV so the BS exit value matches the entry debit's pricing convention.
			// Legs that expire at target are intrinsic-only and IV is irrelevant. Calibration is
			// skipped at hypothetical spots — the market mid is stale w.r.t. the new spot.
			var legIv = parsed.ExpiryDate.Date > skel.TargetExpiry.Date && useMarketImpliedIv
				? MarketImpliedIv(leg.Symbol, parsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct)
				: ResolveIv(leg.Symbol, quotes, cfg.Indicators.IvDefaultPct);
			defs.Add(new MultiLegDefinition(leg, parsed, leg.Action == "buy", legIv));
		}
		if (applyLiquidityGate && !PassesLiquidityGate(skel.Legs, quotes, cfg.Liquidity, spot, snapshotTradeable)) return null;
		var pricingWarning = BuildPricingWarning(usedFallback);

		var netEntryPerContract = NetEntryPerContract(skel.Legs, quotes, pricingMode);
		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, skel.TargetExpiry);
		var targetLegs = defs.Where(l => l.Parsed.ExpiryDate.Date == skel.TargetExpiry.Date).ToList();
		var representativeIv = (targetLegs.Count > 0 ? targetLegs : defs).Average(l => l.Iv);
		var pnl = (decimal sT) => PositionValueAtTarget(sT, skel.TargetExpiry.Date, defs) + netEntryPerContract;
		var (scanLower, scanUpper) = GetScanRange(spot, defs, ResolveStrikeStep(cfg, skel.Ticker));
		// FindBreakevens uses fixed-step sampling. At 240 steps over the structure's full scan
		// range (~$830 on SPY) the step size is ~$3.50 — wider than the profitable zone of
		// tightly-priced 1-DTE iron butterflies, so the scanner silently steps over the zone and
		// returns no break-evens. That made the barrier-aware EV bypass kick in for the exact
		// candidates that path-aware ranking is supposed to penalize. Scaling steps with scan
		// width preserves sub-$1 resolution for any structure.
		// Analytical-first: IronCondor and IronButterfly have closed-form breakevens determined entirely
		// by the short strikes and the net credit. Skipping the 2000-step numerical scan for these is a
		// large per-candidate speedup (the scan dominates ScoreMultiLeg cost on SPX-class tickers where
		// scanRange ≈ $2k). Cross-expiry structures (DoubleCalendar, DoubleDiagonal) still need the scan
		// because their P&L curve depends on long-leg time value remaining at the short expiry.
		var breakevens = TryAnalyticalBreakevens(skel, defs, netEntryPerContract);
		if (breakevens.Count == 0)
		{
			// Search for breakevens on the NARROW strike-relative window, not the wide max-P/L range: on
			// SPX-class tickers the wide range scanned ~18k points at $0.50, almost all in deep wings with no
			// crossing. The narrow window preserves the same $0.50 resolution (so tight iron-butterfly zones
			// are still resolved) over far fewer points. Max profit/loss below stay on the wide range.
			var (beLower, beUpper) = BreakevenScanRange(spot, defs, ResolveStrikeStep(cfg, skel.Ticker), skel.TargetExpiry, representativeIv);
			var scanSteps = Math.Max(240, (int)((beUpper - beLower) / 0.5m));
			breakevens = FindBreakevens(pnl, beLower, beUpper, scanSteps);
		}
		var pop = ComputeProbabilityOfProfit(pnl, breakevens, spot, years, representativeIv);
		var grid = BuildScenarioGrid(spot, representativeIv, years, cfg.ScenarioGridSigma, bias * cfg.Weights.BiasDrift);
		decimal ev = 0m;
		foreach (var pt in grid)
			ev += pt.Weight * pnl(pt.SpotAtExpiry);
		var (maxProfit, maxLoss) = ScanPnlRange(pnl, scanLower, scanUpper);
		var capitalAtRisk = Math.Abs(Math.Min(0m, maxLoss));
		if (capitalAtRisk <= 0m) return null;
		// For debit structures (DC/DD) the score divisor uses cash actually deployed, not worst-case
		// max-loss — same reasoning as ScoreCalendarOrDiagonal. For credit structures (IB/IC) the
		// margin held against the trade IS the capital deployed, so capitalAtRisk stays the divisor.
		var debitPaid = Math.Max(0m, -netEntryPerContract);
		var efficiencyCapital = debitPaid > 0m ? debitPaid : capitalAtRisk;
		var friction = RealizedExpectancy.ComputeFrictionPerContract(cfg.RealizedExpectancy, skel.StructureKind);
		var realizedEv = RealizedExpectancy.RealizeEv(grid, pnl, maxProfit, maxLoss, friction, cfg.RealizedExpectancy);
		// Terminal-EV scoring. See ScoreShortVertical for full rationale on dropping the
		// barrier-aware adjustment; in short, applying it only to some structure types created an
		// asymmetric bias that buried calendars/diagonals/IBs in favor of single-leg longs.
		// BreakevenRoomFactor + sign-symmetric factor chain carries the path-safety signal.
		var rawScore = ComputeRawScore(realizedEv, daysToTarget, efficiencyCapital);
		var fit = DirectionalFit.SignFor(skel);
		var popFactor = ComputeProbabilityFactor(pop);
		var scaleFactor = ComputeCapitalScaleFactor(efficiencyCapital);
		var setupFactor = ComputeSetupFactor(skel.StructureKind, spot, breakevens);
		var runwayFactor = ComputeAdjustmentRunwayFactor(skel, asOf, spot, quotes);
		var tradingDaysToTargetMl = Math.Max(1, CountTradingDays(asOf, skel.TargetExpiry.Date));
		var breakevenRoomFactor = ComputeBreakevenRoomFactor(spot, representativeIv, tradingDaysToTargetMl, breakevens);
		var expectedMoveBoundsMl = ComputeExpectedMoveBounds(spot, representativeIv, tradingDaysToTargetMl);
		var shortLegStrikesMl = ExtractShortLegStrikes(skel.Legs);
		var expectedMoveCreditFactor = ComputeExpectedMoveCreditFactor(spot, representativeIv, tradingDaysToTargetMl, netEntryPerContract, shortLegStrikesMl, cfg.Weights.ExpectedMoveCredit);
		var ivRealizedPremiumFactor = ComputeIvRealizedPremiumFactor(representativeIv, historicalVolAnnual, netEntryPerContract, cfg.Weights.IvRealizedPremium);
		var legGreeks = defs.Select(d => (d.Parsed, d.Iv, d.IsLong)).ToList();
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(legGreeks, asOf, spot);
		var netVegaPerContract = ComputeNetVegaPerContract(legGreeks, asOf, spot);
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
		var volFactor = ComputeVolatilityAdjustmentFactor(netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit);
		var maxPain = MaxPainCached(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, representativeIv, maxPain, cfg, breakevens);
		var gex = GexCached(skel.Ticker, skel.TargetExpiry.Date, spot, asOf, quotes);
		var gexFactor = ComputeGexAdjustmentFactor(skel, spot, asOf, representativeIv, gex, cfg, breakevens);
		var assignmentFactor = ComputeAssignmentRiskFactor(skel, spot, asOf, ResolveStrikeStep(cfg, skel.Ticker), bias);
		var statArb = ComputeMarketTheoreticalAggregate(defs.Select(d => (d.Proposal.Symbol, d.Parsed, d.IsLong)), spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var statArbFactor = ComputeStatArbAdjustmentFactor(statArb?.MarketNet, statArb?.TheoreticalNet, statArb?.GrossTheoretical, cfg.Weights.StatArb);
		var (worstSpread, minOi, minRelOi) = ComputeLegLiquidityStats(skel.Legs, quotes, spot);
		var liquidityFactor = ComputeLiquidityFactor(worstSpread, minOi, minRelOi, cfg.Liquidity.Weight);
		var sentimentFactor = ComputeSentimentFactor(sentimentScore, fit, cfg.Weights.Sentiment);
		var biasAdjBaseMl = BiasAdjust(rawScore, bias, fit, cfg.Weights.DirectionalFit);
		var afterFactorsMl = ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(biasAdjBaseMl, popFactor), scaleFactor), setupFactor ?? 1m), runwayFactor ?? 1m), breakevenRoomFactor ?? 1m), expectedMoveCreditFactor ?? 1m), ivRealizedPremiumFactor ?? 1m), balance), liquidityFactor ?? 1m);
		var biasAdj = SentimentAdjust(StatArbAdjust(AssignmentRiskAdjust(GexAdjust(MaxPainAdjust(VolatilityAdjust(afterFactorsMl, netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit), maxPainFactor), gexFactor), assignmentFactor), statArbFactor), sentimentFactor);
		var finalScore = ComputeFinalScore(biasAdj, thetaPerDayPerContract, efficiencyCapital);
		var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

		return new OpenProposal(
			Ticker: skel.Ticker,
			StructureKind: skel.StructureKind,
			Legs: PriceLegs(skel.Legs, quotes),
			Qty: 1,
			DebitOrCreditPerContract: netEntryPerContract,
			MaxProfitPerContract: maxProfit,
			MaxLossPerContract: maxLoss,
			CapitalAtRiskPerContract: capitalAtRisk,
			Breakevens: breakevens,
			ProbabilityOfProfit: pop,
			ExpectedValuePerContract: ev,
			DaysToTarget: daysToTarget,
			RawScore: rawScore,
			BiasAdjustedScore: biasAdj,
			DirectionalFit: fit,
			Rationale: "",
			Fingerprint: fp,
			PricingWarning: pricingWarning,
			PremiumRatio: premiumRatio,
			ImpliedVolatilityAnnual: representativeIv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			GexGravity: gex.GexGravity,
			NetGexFraction: gex.NetGexFraction,
			GexAdjustmentFactor: gexFactor,
			SetupFactor: setupFactor,
			RunwayFactor: runwayFactor,
			AssignmentRiskFactor: assignmentFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract,
			NetVegaPerContract: netVegaPerContract,
			MarketNetPremiumPerShare: statArb?.MarketNet,
			TheoreticalNetPremiumPerShare: statArb?.TheoreticalNet,
			StatArbAdjustmentFactor: statArbFactor,
			FinalScore: finalScore,
			WorstLegBidAskSpreadPct: worstSpread,
			MinOpenInterest: minOi,
			MinRelativeOpenInterest: minRelOi,
			LiquidityAdjustmentFactor: liquidityFactor,
			MarketSentimentScore: sentimentScore,
			MarketSentimentRating: sentimentScore.HasValue ? SentimentRating.FromScore(sentimentScore.Value) : null,
			SentimentAdjustmentFactor: sentimentFactor,
			RealizedExpectedValuePerContract: cfg.RealizedExpectancy.Enabled ? realizedEv : null,
			EstimatedSlippagePerContract: cfg.RealizedExpectancy.Enabled ? friction : null,
			ProfitTargetPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.ProfitTargetPerContract(maxProfit, cfg.RealizedExpectancy) : null,
			StopLossPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.StopLossPerContract(maxLoss, cfg.RealizedExpectancy) : null,
			BreakevenRoomFactor: breakevenRoomFactor,
			ExpectedMoveCreditFactor: expectedMoveCreditFactor,
			IvRealizedPremiumFactor: ivRealizedPremiumFactor,
			ExpectedMoveLower: expectedMoveBoundsMl?.Lower,
			ExpectedMoveUpper: expectedMoveBoundsMl?.Upper);
	}

	public static OpenProposal? ScoreCalendarOrDiagonal(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid, bool applyLiquidityGate = true, bool useMarketImpliedIv = true, decimal? sentimentScore = null, TickerEvents? events = null, IReadOnlySet<string>? snapshotTradeable = null)
	{
		if (applyLiquidityGate && EventVeto.ShouldVeto(skel, asOf, events, cfg.Indicators.Events, out _)) return null;
		var shortLeg = skel.Legs.First(l => l.Action == "sell");
		var longLeg = skel.Legs.First(l => l.Action == "buy");
		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		if (shortParsed == null || longParsed == null) return null;

		var shortQ = ResolveLegPrice(shortLeg.Symbol, shortParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var longQ = ResolveLegPrice(longLeg.Symbol, longParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		if (shortQ == null || longQ == null) return null;
		if (applyLiquidityGate && !PassesLiquidityGate(skel.Legs, quotes, cfg.Liquidity, spot, snapshotTradeable)) return null;
		var pricingWarning = BuildPricingWarning(shortQ.Value.UsedFallback || longQ.Value.UsedFallback);

		var shortMid = (shortQ.Value.Bid + shortQ.Value.Ask) / 2m;
		var longMid = (longQ.Value.Bid + longQ.Value.Ask) / 2m;
		var debitPerShare = PriceForBuy(longMid, longQ.Value.Ask, pricingMode) - PriceForSell(shortMid, shortQ.Value.Bid, pricingMode);
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
		// Capital efficiency is rated against the cash actually deployed (the debit), not the worst-
		// case assignment loss. The strike gap is a contingent exposure that rarely realizes —
		// diagonals are normally closed or rolled before short expiry, and even on assignment the
		// long retains time value. Folding the strike gap into the score divisor systematically
		// buried diagonals beneath same-strike calendars on capital efficiency. The full
		// debit+strike-gap number stays in CapitalAtRiskPerContract / MaxLossPerContract for
		// position sizing and risk reporting.
		var efficiencyCapital = debitPerContract;

		var shortYears = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, shortParsed.ExpiryDate);
		var longAtShortYears = OpenerExpiryHelpers.TimeYearsToExpiry(shortParsed.ExpiryDate, longParsed.ExpiryDate);
		var ivShort = ResolveIv(shortLeg.Symbol, quotes, cfg.Indicators.IvDefaultPct);
		// Long-leg IV is back-solved from the market mid so EV/breakeven/maxProfit math uses pricing
		// consistent with the entry debit. When the long leg has a wide bid/ask, the broker's reported
		// IV implies a BS price well above mid — using it would inflate residual time value at short
		// expiry and create phantom alpha for illiquid contracts. Skipped at hypothetical spots
		// (--spot overrides) because the stale market mid no longer reflects the new spot.
		var ivLong = useMarketImpliedIv
			? MarketImpliedIv(longLeg.Symbol, longParsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct)
			: ResolveIv(longLeg.Symbol, quotes, cfg.Indicators.IvDefaultPct);

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
		var grid = BuildScenarioGrid(spot, ivShort, shortYears, cfg.ScenarioGridSigma, bias * cfg.Weights.BiasDrift);
		decimal PnlAtTarget(decimal sT)
		{
			var longBS = OptionMath.BlackScholes(sT, longParsed.Strike, longAtShortYears, OptionMath.RiskFreeRate, ivLong, longParsed.CallPut);
			var shortIntrinsic = longParsed.CallPut == "C"
				? Math.Max(0m, sT - shortParsed.Strike)
				: Math.Max(0m, shortParsed.Strike - sT);
			return (longBS - shortIntrinsic) * 100m - debitPerContract;
		}
		decimal ev = 0m;
		foreach (var pt in grid)
			ev += pt.Weight * PnlAtTarget(pt.SpotAtExpiry);
		var maxProfit = FindCalendarOrDiagonalPeakPnl(spot, shortParsed, longParsed, longAtShortYears, ivLong, debitPerContract);
		var maxLossPoint = -(debitPerContract + strikeLossPerContract);

		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var friction = RealizedExpectancy.ComputeFrictionPerContract(cfg.RealizedExpectancy, skel.StructureKind);
		var realizedEv = RealizedExpectancy.RealizeEv(grid, PnlAtTarget, maxProfit, maxLossPoint, friction, cfg.RealizedExpectancy);
		// Terminal-EV scoring (see ScoreShortVertical for rationale on dropping AdjustEvForBarrier).
		// For calendars/diagonals the barrier-aware penalty was particularly miscalibrated: BE-as-stop
		// massively overstated P_hit because theta accumulates favorably over the holding period and
		// the actual stop fires much deeper than break-even.
		var rawScore = ComputeRawScore(realizedEv, daysToTarget, efficiencyCapital);
		// LongCalendar reads neutral (0); LongDiagonal picks up its sign from the strike layout via the
		// skeleton overload, so the technical bias factor lifts/cuts a diagonal whose direction matches
		// or fights the trend. Calendars stay neutral by construction.
		var fit = DirectionalFit.SignFor(skel);
		var popFactor = ComputeProbabilityFactor(pop);
		var scaleFactor = ComputeCapitalScaleFactor(efficiencyCapital);
		var beList = (beLower.HasValue && beUpper.HasValue) ? new[] { beLower.Value, beUpper.Value } : Array.Empty<decimal>();
		var setupFactor = ComputeSetupFactor(skel.StructureKind, spot, beList);
		var runwayFactor = ComputeAdjustmentRunwayFactor(skel, asOf, spot, quotes);
		var representativeIvEarly = (ivShort + ivLong) / 2m;
		var tradingDaysToTargetCd = Math.Max(1, CountTradingDays(asOf, skel.TargetExpiry.Date));
		var breakevenRoomFactor = ComputeBreakevenRoomFactor(spot, representativeIvEarly, tradingDaysToTargetCd, beList);
		var expectedMoveBoundsCd = ComputeExpectedMoveBounds(spot, representativeIvEarly, tradingDaysToTargetCd);
		var shortLegStrikesCd = ExtractShortLegStrikes(skel.Legs);
		var expectedMoveCreditFactor = ComputeExpectedMoveCreditFactor(spot, representativeIvEarly, tradingDaysToTargetCd, -debitPerContract, shortLegStrikesCd, cfg.Weights.ExpectedMoveCredit);
		var ivRealizedPremiumFactor = ComputeIvRealizedPremiumFactor(representativeIvEarly, historicalVolAnnual, -debitPerContract, cfg.Weights.IvRealizedPremium);
		var legGreeks = new[]
		{
			(Parsed: shortParsed, Iv: ivShort, IsLong: false),
			(Parsed: longParsed, Iv: ivLong, IsLong: true)
		};
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(legGreeks, asOf, spot);
		var netVegaPerContract = ComputeNetVegaPerContract(legGreeks, asOf, spot);
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLossPoint, premiumRatio);
		var representativeIv = representativeIvEarly;
		var volFactor = ComputeVolatilityAdjustmentFactor(netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit);
		var maxPain = MaxPainCached(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, ivShort, maxPain, cfg);
		var gex = GexCached(skel.Ticker, skel.TargetExpiry.Date, spot, asOf, quotes);
		var gexFactor = ComputeGexAdjustmentFactor(skel, spot, asOf, ivShort, gex, cfg);
		var assignmentFactor = ComputeAssignmentRiskFactor(skel, spot, asOf, ResolveStrikeStep(cfg, skel.Ticker), bias);
		var statArb = ComputeMarketTheoreticalAggregate(
			new (string, OptionParsed, bool)[] { (shortLeg.Symbol, shortParsed, false), (longLeg.Symbol, longParsed, true) },
			spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var statArbFactor = ComputeStatArbAdjustmentFactor(statArb?.MarketNet, statArb?.TheoreticalNet, statArb?.GrossTheoretical, cfg.Weights.StatArb);
		var (worstSpread, minOi, minRelOi) = ComputeLegLiquidityStats(skel.Legs, quotes, spot);
		var liquidityFactor = ComputeLiquidityFactor(worstSpread, minOi, minRelOi, cfg.Liquidity.Weight);
		var sentimentFactor = ComputeSentimentFactor(sentimentScore, fit, cfg.Weights.Sentiment);
		var biasAdjBaseCd = BiasAdjust(rawScore, bias, fit, cfg.Weights.DirectionalFit);
		var afterFactorsCd = ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(biasAdjBaseCd, popFactor), scaleFactor), setupFactor ?? 1m), runwayFactor ?? 1m), breakevenRoomFactor ?? 1m), expectedMoveCreditFactor ?? 1m), ivRealizedPremiumFactor ?? 1m), balance), liquidityFactor ?? 1m);
		var biasAdj = SentimentAdjust(StatArbAdjust(AssignmentRiskAdjust(GexAdjust(MaxPainAdjust(VolatilityAdjust(afterFactorsCd, netVegaPerContract, representativeIv, historicalVolAnnual, cfg.Weights.VolatilityFit), maxPainFactor), gexFactor), assignmentFactor), statArbFactor), sentimentFactor);
		var finalScore = ComputeFinalScore(biasAdj, thetaPerDayPerContract, efficiencyCapital);
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
			PricingWarning: pricingWarning,
			PremiumRatio: premiumRatio,
			ImpliedVolatilityAnnual: representativeIv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			GexGravity: gex.GexGravity,
			NetGexFraction: gex.NetGexFraction,
			GexAdjustmentFactor: gexFactor,
			SetupFactor: setupFactor,
			RunwayFactor: runwayFactor,
			AssignmentRiskFactor: assignmentFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract,
			NetVegaPerContract: netVegaPerContract,
			MarketNetPremiumPerShare: statArb?.MarketNet,
			TheoreticalNetPremiumPerShare: statArb?.TheoreticalNet,
			StatArbAdjustmentFactor: statArbFactor,
			FinalScore: finalScore,
			WorstLegBidAskSpreadPct: worstSpread,
			MinOpenInterest: minOi,
			MinRelativeOpenInterest: minRelOi,
			LiquidityAdjustmentFactor: liquidityFactor,
			MarketSentimentScore: sentimentScore,
			MarketSentimentRating: sentimentScore.HasValue ? SentimentRating.FromScore(sentimentScore.Value) : null,
			SentimentAdjustmentFactor: sentimentFactor,
			RealizedExpectedValuePerContract: cfg.RealizedExpectancy.Enabled ? realizedEv : null,
			EstimatedSlippagePerContract: cfg.RealizedExpectancy.Enabled ? friction : null,
			ProfitTargetPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.ProfitTargetPerContract(maxProfit, cfg.RealizedExpectancy) : null,
			StopLossPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.StopLossPerContract(maxLossPoint, cfg.RealizedExpectancy) : null,
			BreakevenRoomFactor: breakevenRoomFactor,
			ExpectedMoveCreditFactor: expectedMoveCreditFactor,
			IvRealizedPremiumFactor: ivRealizedPremiumFactor,
			ExpectedMoveLower: expectedMoveBoundsCd?.Lower,
			ExpectedMoveUpper: expectedMoveBoundsCd?.Upper
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
			var expirationTime = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
			var t = Math.Max(0.0, (expirationTime - asOf).TotalDays / 365.0);
			var tTomorrow = Math.Max(0.0, (expirationTime - asOf.AddDays(1)).TotalDays / 365.0);
			var now = OptionMath.BlackScholes(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, leg.Iv, leg.Parsed.CallPut);
			var tomorrow = OptionMath.BlackScholes(spot, leg.Parsed.Strike, tTomorrow, OptionMath.RiskFreeRate, leg.Iv, leg.Parsed.CallPut);
			var thetaPerShare = tomorrow - now;
			netThetaPerShare += leg.IsLong ? thetaPerShare : -thetaPerShare;
		}

		return netThetaPerShare * 100m;
	}

	/// <summary>Net vega in dollars per contract per 1 percentage-point of IV change. Long legs add,
	/// short legs subtract. Uses the same signing convention as the risk diagnostic so the value the
	/// scorer sees here matches what gets rendered in the panel's Greeks line.</summary>
	private static decimal ComputeNetVegaPerContract(IEnumerable<(OptionParsed Parsed, decimal Iv, bool IsLong)> legs, DateTime asOf, decimal spot)
	{
		decimal netVegaPerShare = 0m;
		foreach (var leg in legs)
		{
			var expirationTime = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
			var t = Math.Max(0.0, (expirationTime - asOf).TotalDays / 365.0);
			// OptionMath.Vega returns per 1.0 IV change; divide by 100 for per-1-IV-point.
			var vegaPerShare = OptionMath.Vega(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, leg.Iv) / 100m;
			netVegaPerShare += leg.IsLong ? vegaPerShare : -vegaPerShare;
		}

		return netVegaPerShare * 100m;
	}

	public static OpenProposal? ScoreLongCallPut(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg, decimal? historicalVolAnnual = null, string pricingMode = SuggestionPricing.Mid, bool applyLiquidityGate = true, decimal? sentimentScore = null, TickerEvents? events = null, IReadOnlySet<string>? snapshotTradeable = null)
	{
		// Long-only structures are never vetoed by events (EventVeto.HasShortLeg returns false). The
		// parameter is plumbed for signature symmetry and so the diagnostic builder can surface the
		// catalyst on these proposals via EarningsProximityRule.
		_ = events;
		var leg = skel.Legs[0];
		var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
		if (parsed == null) return null;

		var quote = ResolveLegPrice(leg.Symbol, parsed, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		if (quote == null) return null;
		if (applyLiquidityGate && !PassesLiquidityGate(skel.Legs, quotes, cfg.Liquidity, spot, snapshotTradeable)) return null;
		var pricingWarning = BuildPricingWarning(quote.Value.UsedFallback);
		var bid = quote.Value.Bid;
		var ask = quote.Value.Ask;
		var mid = (bid + ask) / 2m;

		var years = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, skel.TargetExpiry);
		var iv = ResolveIv(leg.Symbol, quotes, cfg.Indicators.IvDefaultPct);

		var debitPerShare = PriceForBuy(mid, ask, pricingMode);
		var debitPerContract = debitPerShare * 100m;
		var breakeven = parsed.CallPut == "C" ? parsed.Strike + debitPerShare : parsed.Strike - debitPerShare;

		var pop = LogNormalProbability(parsed.CallPut == "C" ? Direction.Above : Direction.Below, spot, breakeven, years, (double)iv);

		var grid = BuildScenarioGrid(spot, iv, years, cfg.ScenarioGridSigma, bias * cfg.Weights.BiasDrift);
		decimal PnlAtExpiry(decimal sT)
		{
			var intrinsic = parsed.CallPut == "C" ? Math.Max(0m, sT - parsed.Strike) : Math.Max(0m, parsed.Strike - sT);
			return intrinsic * 100m - debitPerContract;
		}
		decimal ev = 0m;
		decimal maxProfit = 0m;
		decimal maxLoss = -debitPerContract;
		foreach (var pt in grid)
		{
			var pnl = PnlAtExpiry(pt.SpotAtExpiry);
			ev += pt.Weight * pnl;
			if (pnl > maxProfit) maxProfit = pnl;
		}

		var capitalAtRisk = debitPerContract;
		var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
		var friction = RealizedExpectancy.ComputeFrictionPerContract(cfg.RealizedExpectancy, skel.StructureKind);
		var realizedEv = RealizedExpectancy.RealizeEv(grid, PnlAtExpiry, maxProfit, maxLoss, friction, cfg.RealizedExpectancy);
		var rawScore = ComputeRawScore(realizedEv, daysToTarget, capitalAtRisk);
		var fit = DirectionalFit.SignFor(skel);
		// LongCall/LongPut is a positive-skew lottery trade: low POP is the SHAPE of the trade, not
		// a defect. ComputeProbabilityFactor was designed for credit structures where high POP is the
		// goal and low POP signals a coin-flip — applying it to long premium multiplies an explicitly
		// chosen low-probability bet by a 16× penalty, which makes long calls/puts permanently lose
		// to credit spreads in a competing-structure scorer (the hybrid-mode failure mode). Skip it.
		// Other guardrails on this candidate — friction in realizedEv, scaleFactor, balance, theta —
		// still rank long premium against itself and against the bias-driven raw EV.
		var popFactor = 1m;
		var scaleFactor = ComputeCapitalScaleFactor(capitalAtRisk);
		var setupFactor = ComputeSetupFactor(skel.StructureKind, spot, [breakeven]);
		var tradingDaysToTargetLc = Math.Max(1, CountTradingDays(asOf, skel.TargetExpiry.Date));
		var expectedMoveBoundsLc = ComputeExpectedMoveBounds(spot, iv, tradingDaysToTargetLc);
		var ivRealizedPremiumFactor = ComputeIvRealizedPremiumFactor(iv, historicalVolAnnual, -debitPerContract, cfg.Weights.IvRealizedPremium);
		var legGreeks = new[] { (Parsed: parsed, Iv: iv, IsLong: true) };
		var thetaPerDayPerContract = ComputeNetThetaPerDayPerContract(legGreeks, asOf, spot);
		var netVegaPerContract = ComputeNetVegaPerContract(legGreeks, asOf, spot);
		// Single-leg structures: premium_ratio defaults to 1 (no shorts to receive from). BalanceFactor
		// collapses to just the R/R term, where R/R = projected_max_profit_at_+2σ / debit.
		var premiumRatio = ComputePremiumRatio(skel.Legs, quotes, pricingMode);
		var balance = BalanceFactor(maxProfit, maxLoss, premiumRatio);
		var volFactor = ComputeVolatilityAdjustmentFactor(netVegaPerContract, iv, historicalVolAnnual, cfg.Weights.VolatilityFit);
		var maxPain = MaxPainCached(skel.Ticker, skel.TargetExpiry.Date, quotes, spot);
		var maxPainFactor = ComputeMaxPainAdjustmentFactor(skel, spot, asOf, iv, maxPain, cfg);
		var gex = GexCached(skel.Ticker, skel.TargetExpiry.Date, spot, asOf, quotes);
		var gexFactor = ComputeGexAdjustmentFactor(skel, spot, asOf, iv, gex, cfg);
		var assignmentFactor = ComputeAssignmentRiskFactor(skel, spot, asOf, ResolveStrikeStep(cfg, skel.Ticker), bias);
		var statArb = ComputeMarketTheoreticalAggregate(new (string, OptionParsed, bool)[] { (leg.Symbol, parsed, true) }, spot, asOf, quotes, cfg.Indicators.IvDefaultPct);
		var statArbFactor = ComputeStatArbAdjustmentFactor(statArb?.MarketNet, statArb?.TheoreticalNet, statArb?.GrossTheoretical, cfg.Weights.StatArb);
		var (worstSpread, minOi, minRelOi) = ComputeLegLiquidityStats(skel.Legs, quotes, spot);
		var liquidityFactor = ComputeLiquidityFactor(worstSpread, minOi, minRelOi, cfg.Liquidity.Weight);
		var sentimentFactor = ComputeSentimentFactor(sentimentScore, fit, cfg.Weights.Sentiment);
		// Directional-conviction gate: long premium needs follow-through to beat theta; de-rate it when
		// the trade-aligned bias is weak (the flat/choppy-day coin-flips that dominate long losses).
		var convictionFactor = cfg.LongConvictionGate.Factor(bias * fit);
		var biasAdjBaseLc = BiasAdjust(rawScore, bias, fit, cfg.Weights.DirectionalFit);
		var afterFactorsLc = ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(ApplyFactor(biasAdjBaseLc, popFactor), scaleFactor), setupFactor ?? 1m), ivRealizedPremiumFactor ?? 1m), balance), liquidityFactor ?? 1m), convictionFactor);
		var biasAdj = SentimentAdjust(StatArbAdjust(AssignmentRiskAdjust(GexAdjust(MaxPainAdjust(VolatilityAdjust(afterFactorsLc, netVegaPerContract, iv, historicalVolAnnual, cfg.Weights.VolatilityFit), maxPainFactor), gexFactor), assignmentFactor), statArbFactor), sentimentFactor);
		var finalScore = ComputeFinalScore(biasAdj, thetaPerDayPerContract, capitalAtRisk);
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
			PricingWarning: pricingWarning,
			PremiumRatio: null,   // single-leg: ratio collapses to 1 (no shorts), don't display "prem 1.00x"
			ImpliedVolatilityAnnual: iv,
			HistoricalVolatilityAnnual: historicalVolAnnual,
			VolatilityAdjustmentFactor: volFactor,
			TargetExpiryMaxPain: maxPain,
			MaxPainAdjustmentFactor: maxPainFactor,
			GexGravity: gex.GexGravity,
			NetGexFraction: gex.NetGexFraction,
			GexAdjustmentFactor: gexFactor,
			SetupFactor: setupFactor,
			AssignmentRiskFactor: assignmentFactor,
			ThetaPerDayPerContract: thetaPerDayPerContract,
			NetVegaPerContract: netVegaPerContract,
			MarketNetPremiumPerShare: statArb?.MarketNet,
			TheoreticalNetPremiumPerShare: statArb?.TheoreticalNet,
			StatArbAdjustmentFactor: statArbFactor,
			FinalScore: finalScore,
			WorstLegBidAskSpreadPct: worstSpread,
			MinOpenInterest: minOi,
			MinRelativeOpenInterest: minRelOi,
			LiquidityAdjustmentFactor: liquidityFactor,
			MarketSentimentScore: sentimentScore,
			MarketSentimentRating: sentimentScore.HasValue ? SentimentRating.FromScore(sentimentScore.Value) : null,
			SentimentAdjustmentFactor: sentimentFactor,
			RealizedExpectedValuePerContract: cfg.RealizedExpectancy.Enabled ? realizedEv : null,
			EstimatedSlippagePerContract: cfg.RealizedExpectancy.Enabled ? friction : null,
			ProfitTargetPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.ProfitTargetPerContract(maxProfit, cfg.RealizedExpectancy) : null,
			StopLossPerContract: cfg.RealizedExpectancy.Enabled ? RealizedExpectancy.StopLossPerContract(maxLoss, cfg.RealizedExpectancy) : null,
			IvRealizedPremiumFactor: ivRealizedPremiumFactor,
			ExpectedMoveLower: expectedMoveBoundsLc?.Lower,
			ExpectedMoveUpper: expectedMoveBoundsLc?.Upper
		);
	}

	private static decimal PriceForBuy(decimal mid, decimal ask, string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? ask : mid;

	private static decimal PriceForSell(decimal mid, decimal bid, string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? bid : mid;
}
