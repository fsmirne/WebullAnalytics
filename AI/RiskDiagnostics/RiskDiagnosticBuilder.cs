using WebullAnalytics.AI.RiskDiagnostics.Rules;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Builds a RiskDiagnostic from normalized legs, current spot, as-of time, and an IV resolver.
/// Pure function — no I/O. Ten rules run unconditionally; only fired hits attach.</summary>
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
	};

	internal static RiskDiagnostic Build(
		IReadOnlyList<DiagnosticLeg> legs,
		decimal spot,
		DateTime asOf,
		Func<string, decimal> ivResolver,
		TrendSnapshot? trend)
	{
		var longLegs = legs.Where(l => l.IsLong).ToList();
		var shortLegs = legs.Where(l => !l.IsLong).ToList();

		// Greeks (per-contract). For each leg: signed × qty × per-share greek; then divide aggregate
		// by reference qty to express as per-contract. Reference qty is the first leg's qty — pipelines
		// always pass legs at the same contract multiple.
		decimal netDeltaSum = 0m, netThetaSum = 0m, netVegaSum = 0m;
		foreach (var leg in legs)
		{
			var sign = leg.IsLong ? 1m : -1m;
			var iv = ivResolver(leg.Symbol);
			var dteRaw = (leg.Parsed.ExpiryDate - asOf.Date).Days;
			var dte = Math.Max(1, dteRaw);
			var t = dte / 365.0;
			var delta = OptionMath.Delta(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);

			// Theta via finite-difference: BS today − BS tomorrow. Negative for long options.
			var pNow = OptionMath.BlackScholes(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);
			var pTomorrow = OptionMath.BlackScholes(spot, leg.Parsed.Strike, Math.Max(1, dte - 1) / 365.0, OptionMath.RiskFreeRate, iv, leg.Parsed.CallPut);
			var thetaPerShare = pTomorrow - pNow;

			// OptionMath.Vega returns per 1.0 IV change (S φ(d1) √T). Divide by 100 for per-1-IV-point.
			var vegaPerShare = OptionMath.Vega(spot, leg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv) / 100m;

			netDeltaSum += sign * leg.Qty * delta;
			netThetaSum += sign * leg.Qty * 100m * thetaPerShare;
			netVegaSum += sign * leg.Qty * 100m * vegaPerShare;
		}
		var qtyRef = legs.Count > 0 ? legs[0].Qty : 1;
		decimal netDelta = qtyRef > 0 ? netDeltaSum / qtyRef : 0m;
		decimal netTheta = qtyRef > 0 ? netThetaSum / qtyRef : 0m;
		decimal netVega = qtyRef > 0 ? netVegaSum / qtyRef : 0m;

		// DTE geometry
		var shortLegDteMin = shortLegs.Count == 0 ? 0 : shortLegs.Min(l => Math.Max(0, (l.Parsed.ExpiryDate - asOf.Date).Days));
		var longLegDteMax = longLegs.Count == 0 ? 0 : longLegs.Max(l => Math.Max(0, (l.Parsed.ExpiryDate - asOf.Date).Days));
		var dteGap = (shortLegs.Count == 0 || longLegs.Count == 0) ? 0 : longLegDteMax - shortLegDteMin;

		// Premium economics (per-share)
		var longPaid = longLegs.Sum(l => l.PricePerShare ?? 0m);
		var shortReceived = shortLegs.Sum(l => l.PricePerShare ?? 0m);
		var netCash = shortReceived - longPaid;
		decimal? premiumRatio = shortReceived == 0m ? null : longPaid / shortReceived;

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

		// Residual delta after short expires: re-evaluate long legs at (long_dte − short_dte) days.
		decimal netDeltaPostShort = netDelta;
		if (shortLegs.Count > 0 && longLegs.Count > 0 && shortLegDteMin < longLegDteMax)
		{
			decimal residualSum = 0m;
			foreach (var leg in longLegs)
			{
				var iv = ivResolver(leg.Symbol);
				var dteRaw = (leg.Parsed.ExpiryDate - asOf.Date).Days;
				var dtePost = Math.Max(1, dteRaw - shortLegDteMin);
				var tPost = dtePost / 365.0;
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
			Trend: trend);

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
			Rules: hits);
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
				var longPrice = L.PricePerShare ?? 0m;
				var shortPrice = S.PricePerShare ?? 0m;
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
		return ("unknown", "neutral");
	}
}
