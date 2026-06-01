using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.RiskDiagnostics.Rules;
using WebullAnalytics.Pricing;
using WebullAnalytics.Sentiment;

namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Builds a RiskDiagnostic from normalized legs, current spot, as-of time, and an IV resolver.
/// Pure function — no I/O. All rules run unconditionally; only fired hits attach.</summary>
internal static class RiskDiagnosticBuilder
{
	private static readonly IReadOnlyList<IRiskRule> Rules = new IRiskRule[]
	{
		new ShortLegLowExtrinsicRule(),
		new DirectionalExposureRule(),
		new PremiumRatioImbalancedRule(),
		new GeometryBullishCoveredDiagonalRule(),
		new GeometryBearishInvertedDiagonalRule(),
		new ShortExpiresBeforeLongRule(),
		new VegaAdverseRule(),
		new DirectionalMismatchNearTermRule(),
		new DirectionalMismatchTodayRule(),
		new HighRealizedVolRule(),
		new WideSpreadRule(),
		new ThinOpenInterestRule(),
		new SubGridStrikeRule(),
		new MarketSentimentExtremeRule(),
		new CreditDivergenceRule(),
		new EarningsProximityRule(),
	};

	internal static RiskDiagnostic Build(
		IReadOnlyList<DiagnosticLeg> legs,
		decimal spot,
		DateTime asOf,
		Func<string, decimal> ivResolver,
		TrendSnapshot? trend,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null,
		SentimentSnapshot? sentiment = null,
		TickerEvents? events = null,
		bool isTheoretical = false)
	{
		var longLegs = legs.Where(l => l.IsLong).ToList();
		var shortLegs = legs.Where(l => !l.IsLong).ToList();

       // Greeks (per-contract). For each leg: signed × qty × per-share greek; then divide aggregate
		// by reference qty to express as per-contract. Reference qty is the first leg's qty — pipelines
		// always pass legs at the same contract multiple.
		decimal netDeltaSum = 0m, netThetaSum = 0m, netVegaSum = 0m, netTheoreticalValueSum = 0m, theoreticalLongPaidSum = 0m, theoreticalShortReceivedSum = 0m;
		foreach (var leg in legs)
		{
			var sign = leg.IsLong ? 1m : -1m;
			var iv = ivResolver(leg.Symbol);
			// Use fractional time-to-expiry against market close. Integer DTE with a Math.Max(1, …) floor
			// would price a 0DTE option as if it had a full day left, inflating extrinsic dramatically
			// (e.g., theoretical $30 vs market $0.13 on a 0DTE OTM SPX put at end of day).
			var expirationTime = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
			var t = Math.Max(0.0, (expirationTime - asOf).TotalDays / 365.0);
			var tTomorrow = Math.Max(0.0, (expirationTime - asOf.AddDays(1)).TotalDays / 365.0);
			var delta = OptionMath.Delta(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);

			// Theta via finite-difference: BS today − BS tomorrow. Negative for long options.
			var pNow = OptionMath.BlackScholes(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);
			var pTomorrow = OptionMath.BlackScholes(spot, leg.Parsed.Strike, tTomorrow, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);
			var thetaPerShare = pTomorrow - pNow;

			// OptionMath.Vega returns per 1.0 IV change (S φ(d1) √T). Divide by 100 for per-1-IV-point.
			var vegaPerShare = OptionMath.Vega(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv) / 100m;

			netDeltaSum += sign * leg.Qty * delta;
			netThetaSum += sign * leg.Qty * 100m * thetaPerShare;
			netVegaSum += sign * leg.Qty * 100m * vegaPerShare;
			netTheoreticalValueSum += sign * leg.Qty * pNow;
			if (leg.IsLong)
				theoreticalLongPaidSum += leg.Qty * pNow;
			else
				theoreticalShortReceivedSum += leg.Qty * pNow;
		}
		var qtyRef = legs.Count > 0 ? legs[0].Qty : 1;
		decimal netDelta = qtyRef > 0 ? netDeltaSum / qtyRef : 0m;
		decimal netTheta = qtyRef > 0 ? netThetaSum / qtyRef : 0m;
		decimal netVega = qtyRef > 0 ? netVegaSum / qtyRef : 0m;
		decimal? netTheoreticalValuePerShare = qtyRef > 0 ? netTheoreticalValueSum / qtyRef : null;
		decimal? theoreticalLongPaidPerShare = qtyRef > 0 ? theoreticalLongPaidSum / qtyRef : null;
		decimal? theoreticalShortReceivedPerShare = qtyRef > 0 ? theoreticalShortReceivedSum / qtyRef : null;
		decimal? theoreticalNetPremiumPerShare = theoreticalLongPaidPerShare.HasValue && theoreticalShortReceivedPerShare.HasValue ? theoreticalLongPaidPerShare.Value - theoreticalShortReceivedPerShare.Value : null;
		decimal? theoreticalPremiumRatio = theoreticalShortReceivedPerShare is decimal theoreticalShort && theoreticalShort != 0m && theoreticalLongPaidPerShare.HasValue ? theoreticalLongPaidPerShare.Value / theoreticalShort : null;

		// DTE geometry
		var shortLegDteMin = shortLegs.Count == 0 ? 0 : shortLegs.Min(l => Math.Max(0, (l.Parsed.ExpiryDate - asOf.Date).Days));
		var longLegDteMax = longLegs.Count == 0 ? 0 : longLegs.Max(l => Math.Max(0, (l.Parsed.ExpiryDate - asOf.Date).Days));
		var dteGap = (shortLegs.Count == 0 || longLegs.Count == 0) ? 0 : longLegDteMax - shortLegDteMin;

		// Premium economics (per-share). For the manage pipeline (cost basis on every leg) this reflects
		// what was paid/received at entry; for the open pipeline (no cost basis) it reflects current
		// prices. CurrentValuePerShare/UnrealizedPnlPerShare separately convey the "now" view.
		bool useCostBasis = legs.Count > 0 && legs.All(l => l.CostBasisPerShare.HasValue);
		decimal PremiumOf(DiagnosticLeg l) => useCostBasis ? l.CostBasisPerShare!.Value : (l.PricePerShare ?? 0m);
		var longPaid = longLegs.Sum(PremiumOf);
		var shortReceived = shortLegs.Sum(PremiumOf);
		var netCash = shortReceived - longPaid;
		decimal? premiumRatio = shortReceived == 0m ? null : longPaid / shortReceived;
		var hasMarketPrices = legs.All(l => l.PricePerShare.HasValue);
		decimal? marketLongPaid = hasMarketPrices ? longLegs.Sum(l => l.PricePerShare!.Value) : null;
		decimal? marketShortReceived = hasMarketPrices ? shortLegs.Sum(l => l.PricePerShare!.Value) : null;
		decimal? netMidPerShare = marketLongPaid.HasValue && marketShortReceived.HasValue ? marketLongPaid.Value - marketShortReceived.Value : null;
		decimal? marketPremiumRatio = marketShortReceived is decimal marketShort && marketShort != 0m && marketLongPaid.HasValue ? marketLongPaid.Value / marketShort : null;

		// Strike geometry
		var shortOtm = shortLegs.Count > 0 && shortLegs.All(l =>
			(l.Parsed.CallPut == "C" && spot <= l.Parsed.Strike) ||
			(l.Parsed.CallPut == "P" && spot >= l.Parsed.Strike));
		decimal shortExtrinsic = shortLegs.Count == 0 ? 0m : shortLegs.Min(l =>
		{
			var intrinsic = OptionMath.Intrinsic(spot, l.Parsed.Strike, l.Parsed.CallPut);
			return Math.Max(0m, (l.PricePerShare ?? 0m) - intrinsic);
		});

		var (structureLabel, directionalBias) = ClassifyStructure(longLegs, shortLegs);

		var longStrike = longLegs.Count > 0 ? longLegs[0].Parsed.Strike : 0m;
		var shortStrike = shortLegs.Count > 0 ? shortLegs[0].Parsed.Strike : 0m;

		// Residual delta after short expires: re-evaluate long legs at the time the earliest short expires.
		decimal netDeltaPostShort = netDelta;
		if (shortLegs.Count > 0 && longLegs.Count > 0 && shortLegDteMin < longLegDteMax)
		{
			var shortExpiryInstant = asOf.Date.AddDays(shortLegDteMin) + OptionMath.MarketClose;
			decimal residualSum = 0m;
			foreach (var leg in longLegs)
			{
				var iv = ivResolver(leg.Symbol);
				var longExpiryInstant = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
				var tPost = Math.Max(0.0, (longExpiryInstant - shortExpiryInstant).TotalDays / 365.0);
				var delta = OptionMath.Delta(spot, leg.Parsed.Strike, tPost, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);
				residualSum += leg.Qty * delta;
			}
			netDeltaPostShort = qtyRef > 0 ? residualSum / qtyRef : 0m;
		}

		// P&L (manage pipeline only — requires both cost basis and current price on every leg)
		decimal? costBasisPerShare = null, currentValuePerShare = null, unrealizedPnlPerShare = null;
		if (legs.All(l => l.CostBasisPerShare.HasValue) && legs.All(l => l.PricePerShare.HasValue))
		{
			costBasisPerShare = legs.Sum(l => (l.IsLong ? 1m : -1m) * l.CostBasisPerShare!.Value);
			currentValuePerShare = legs.Sum(l => (l.IsLong ? 1m : -1m) * l.PricePerShare!.Value);
			unrealizedPnlPerShare = currentValuePerShare - costBasisPerShare;
		}

		// Liquidity stats: worst-leg bid/ask spread (as fraction of mid), absolute minimum OI across
		// legs, and the worst leg's relative OI vs same-expiry near-spot strikes (catches sub-grid
		// strikes that have absolute OI > floor but pale next to neighbors). Worst-leg gates the exit.
		var (worstLegSpreadPct, minOpenInterest, minRelativeOi) = ComputeLiquidityStats(legs, quotes, spot);

		var hasShortCallLeg = shortLegs.Any(l => l.Parsed.CallPut == "C");
		var junkBondDemandScore = sentiment?.Components.FirstOrDefault(c => c.Key == "junk_bond_demand")?.Score;
		var facts = new RiskDiagnosticFacts(
			StructureLabel: structureLabel,
			DirectionalBias: directionalBias,
			NetDelta: netDelta,
			NetThetaPerDay: netTheta,
			NetVega: netVega,
			ShortLegDteMin: shortLegDteMin,
			LongLegDteMax: longLegDteMax,
			DteGapDays: dteGap,
			HasShortLeg: shortLegs.Count > 0,
			HasLongLeg: longLegs.Count > 0,
			LongPremiumPaid: longPaid,
			ShortPremiumReceived: shortReceived,
			NetCashPerShare: netCash,
			PremiumRatio: premiumRatio,
			Spot: spot,
			ShortLegOtm: shortOtm,
			ShortLegExtrinsic: shortExtrinsic,
			LongLegStrike: longStrike,
			ShortLegStrike: shortStrike,
			NetDeltaPostShort: netDeltaPostShort,
			Trend: trend,
			WorstLegBidAskSpreadPct: worstLegSpreadPct,
			MinOpenInterest: minOpenInterest,
			MinRelativeOpenInterest: minRelativeOi,
			MarketSentimentScore: sentiment?.Score,
			MarketSentimentRating: sentiment?.Rating,
			MarketSentimentDelta1Week: sentiment?.Delta1Week,
			JunkBondDemandScore: junkBondDemandScore,
			AsOf: asOf,
			NextEarningsDate: events?.NextEarningsDate,
			EarningsTime: events?.EarningsTime,
			NextExDividendDate: events?.NextExDividendDate,
			HasShortCallLeg: hasShortCallLeg);

		var hits = Rules
			.Select(r => r.TryEvaluate(facts))
			.Where(h => h is not null)
			.Cast<RiskRuleHit>()
			.ToList();

		return new RiskDiagnostic(
			StructureLabel: structureLabel,
			DirectionalBias: directionalBias,
			NetDelta: netDelta,
			NetThetaPerDay: netTheta,
			NetVega: netVega,
			ShortLegDteMin: shortLegDteMin,
			LongLegDteMax: longLegDteMax,
			DteGapDays: dteGap,
			LongPremiumPaid: longPaid,
			ShortPremiumReceived: shortReceived,
			NetCashPerShare: netCash,
			PremiumRatio: premiumRatio,
			SpotAtEvaluation: spot,
			BreakevenDistancePct: null,
			ShortLegOtm: shortOtm,
			ShortLegExtrinsic: shortExtrinsic,
			Trend: trend,
			CostBasisPerShare: costBasisPerShare,
			CurrentValuePerShare: currentValuePerShare,
			UnrealizedPnlPerShare: unrealizedPnlPerShare,
			Rules: hits,
			NetMidPerShare: netMidPerShare,
			TheoreticalValuePerShare: netTheoreticalValuePerShare,
			MarketLongPremiumPaid: marketLongPaid,
			MarketShortPremiumReceived: marketShortReceived,
			MarketNetPremiumPerShare: netMidPerShare,
			MarketPremiumRatio: marketPremiumRatio,
			TheoreticalLongPremiumPaid: theoreticalLongPaidPerShare,
			TheoreticalShortPremiumReceived: theoreticalShortReceivedPerShare,
			TheoreticalNetPremiumPerShare: theoreticalNetPremiumPerShare,
			TheoreticalPremiumRatio: theoreticalPremiumRatio,
			MarketSentimentScore: sentiment?.Score,
			MarketSentimentRating: sentiment?.Rating,
			MarketSentimentDelta1Week: sentiment?.Delta1Week,
			IsTheoretical: isTheoretical);
	}

	private static (string StructureLabel, string DirectionalBias) ClassifyStructure(
		List<DiagnosticLeg> longLegs, List<DiagnosticLeg> shortLegs)
	{
		if (longLegs.Count == 0 && shortLegs.Count == 0) return ("unknown", "neutral");
		if (longLegs.Count == 1 && shortLegs.Count == 0)
		{
			var cp = longLegs[0].Parsed.CallPut;
			return ("single_long", cp == "C" ? "bullish" : "bearish");
		}
		if (longLegs.Count == 0 && shortLegs.Count == 1)
		{
			var cp = shortLegs[0].Parsed.CallPut;
			return ("single_short", cp == "C" ? "bearish" : "bullish");
		}
		if (longLegs.Count == 1 && shortLegs.Count == 1)
		{
			var L = longLegs[0]; var S = shortLegs[0];
			var cp = L.Parsed.CallPut;
			if (L.Parsed.ExpiryDate == S.Parsed.ExpiryDate)
			{
				// Use cost basis when both legs have it (manage pipeline) so debit/credit matches entry,
				// not current mark-to-market. Falls back to current prices for the open pipeline.
				var longPrice = L.CostBasisPerShare ?? L.PricePerShare ?? 0m;
				var shortPrice = S.CostBasisPerShare ?? S.PricePerShare ?? 0m;
				var debit = longPrice > shortPrice;
				if (debit)
				{
					var bullish = (cp == "C" && L.Parsed.Strike < S.Parsed.Strike)
							   || (cp == "P" && L.Parsed.Strike > S.Parsed.Strike);
					return ("vertical_debit", bullish ? "bullish" : "bearish");
				}
				var bullishCredit = (cp == "C" && L.Parsed.Strike > S.Parsed.Strike)
								 || (cp == "P" && L.Parsed.Strike < S.Parsed.Strike);
				return ("vertical_credit", bullishCredit ? "bullish" : "bearish");
			}
			if (L.Parsed.Strike == S.Parsed.Strike) return ("calendar", "neutral");
			var coveredBullish = (cp == "C" && L.Parsed.Strike < S.Parsed.Strike)
							   || (cp == "P" && L.Parsed.Strike > S.Parsed.Strike);
			if (coveredBullish)
				return ("covered_diagonal", cp == "C" ? "bullish" : "bearish");
			return ("inverted_diagonal", cp == "C" ? "bearish" : "bullish");
		}

		if (longLegs.Count == 2 && shortLegs.Count == 2)
		{
			var longCalls = longLegs.Where(l => l.Parsed.CallPut == "C").ToList();
			var longPuts = longLegs.Where(l => l.Parsed.CallPut == "P").ToList();
			var shortCalls = shortLegs.Where(l => l.Parsed.CallPut == "C").ToList();
			var shortPuts = shortLegs.Where(l => l.Parsed.CallPut == "P").ToList();
			if (longCalls.Count == 1 && longPuts.Count == 1 && shortCalls.Count == 1 && shortPuts.Count == 1)
			{
				var shortCall = shortCalls[0].Parsed;
				var shortPut = shortPuts[0].Parsed;
				var longCall = longCalls[0].Parsed;
				var longPut = longPuts[0].Parsed;
				var expiries = longLegs.Concat(shortLegs).Select(l => l.Parsed.ExpiryDate).Distinct().ToList();
				// Validate the iron / double geometry here; the NAME (iron_butterfly vs iron_condor,
				// double_calendar vs double_diagonal) is then derived from the shared count-based taxonomy in
				// ParsingHelpers so the structure vocabulary lives in one place. The geometry guards stay so a
				// malformed 2L2S set still falls through to "unknown" rather than being force-named.
				var ironValid = expiries.Count == 1
					&& ((shortCall.Strike == shortPut.Strike && longPut.Strike < shortPut.Strike && longCall.Strike > shortCall.Strike)
						|| (shortPut.Strike < shortCall.Strike && longPut.Strike < shortPut.Strike && longCall.Strike > shortCall.Strike));
				var doubleValid = expiries.Count == 2
					&& shortCall.ExpiryDate == shortPut.ExpiryDate
					&& longCall.ExpiryDate == longPut.ExpiryDate
					&& longCall.ExpiryDate > shortCall.ExpiryDate
					&& shortPut.Strike < shortCall.Strike;
				if (ironValid || doubleValid)
					return (NameFromCounts(longLegs, shortLegs), "neutral");
			}
		}

		// Single-sided 2 long + 2 short across two expiries (all same call/put), each expiry forming a
		// vertical (one long + one short). Validate the shape here; the name (calendar_vertical with one
		// shared anchor, diagonal_vertical with offset anchors) again comes from the shared taxonomy.
		if (longLegs.Count == 2 && shortLegs.Count == 2
			&& longLegs.Concat(shortLegs).Select(l => l.Parsed.CallPut).Distinct().Count() == 1)
		{
			var dvExpiries = longLegs.Concat(shortLegs).Select(l => l.Parsed.ExpiryDate).Distinct().ToList();
			if (dvExpiries.Count == 2
				&& dvExpiries.All(e => longLegs.Count(l => l.Parsed.ExpiryDate == e) == 1 && shortLegs.Count(l => l.Parsed.ExpiryDate == e) == 1))
				return (NameFromCounts(longLegs, shortLegs), "neutral");
		}

		return ("unknown", "neutral");
	}

	/// <summary>Derives the multi-leg structure label from the shared count-based taxonomy
	/// (<see cref="ParsingHelpers.ClassifyStrategyKind"/>), lower-cased to snake_case to match the risk
	/// vocabulary. Only called once the caller's geometry guards have confirmed a valid shape, so the
	/// count-based kind and the geometry agree by construction — this keeps structure NAMES in one place
	/// while the geometry validation (valid vs unknown) and bias stay in the classifier.</summary>
	private static string NameFromCounts(IReadOnlyList<DiagnosticLeg> longLegs, IReadOnlyList<DiagnosticLeg> shortLegs)
	{
		var all = longLegs.Concat(shortLegs).ToList();
		var distinctExpiries = all.Select(l => l.Parsed.ExpiryDate).Distinct().Count();
		var distinctStrikes = all.Select(l => l.Parsed.Strike).Distinct().Count();
		var distinctCallPut = all.Select(l => l.Parsed.CallPut).Distinct().Count();
		return PascalToSnake(ParsingHelpers.ClassifyStrategyKind(all.Count, distinctExpiries, distinctStrikes, distinctCallPut));
	}

	/// <summary>"IronButterfly" → "iron_butterfly", "CalendarVertical" → "calendar_vertical".</summary>
	private static string PascalToSnake(string s) =>
		string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

	/// <summary>Per-leg bid/ask spread as a fraction of mid; minimum effective liquidity per leg
	/// (max of OI and intraday volume); relative liquidity per leg (leg's effective liquidity / max
	/// among same-expiry strikes within ±10% of spot). Returns the worst spread, minimum absolute
	/// effective liquidity, and minimum relative liquidity across legs. "Effective liquidity" credits
	/// either standing OI or active volume — a low-OI contract trading 500 lots today is more
	/// exit-friendly than its OI alone suggests. Values null when quotes are unavailable.</summary>
	private static (decimal? worstSpreadPct, long? minOi, decimal? minRelOi) ComputeLiquidityStats(
		IReadOnlyList<DiagnosticLeg> legs,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes,
		decimal spot)
	{
		if (quotes == null || legs.Count == 0) return (null, null, null);

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

			if (spot > 0m && legLiq.HasValue && legLiq.Value > 0)
			{
				var nearbyMax = FindMaxLiquidityNearStrike(leg.Parsed, quotes, spot);
				if (nearbyMax > 0)
				{
					var ratio = (decimal)legLiq.Value / nearbyMax;
					if (minRel == null || ratio < minRel.Value)
						minRel = ratio;
				}
			}
		}
		return (worstSpread, minOi, minRel);
	}

	/// <summary>Combined OI/volume liquidity proxy: <c>max(OI, volume)</c>. Mirrors
	/// CandidateScorer.EffectiveLiquidity; kept private here to avoid a public dependency.</summary>
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
}
