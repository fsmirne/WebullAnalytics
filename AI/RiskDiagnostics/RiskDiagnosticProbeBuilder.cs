using System.Text.Json;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.RiskDiagnostics;

internal static class RiskDiagnosticProbeBuilder
{
	private static AIConfig? _cachedAiConfig;
	private static bool _loaded;

	internal static RiskDiagnosticProbe Build(
		IReadOnlyList<DiagnosticLeg> legs,
		decimal spot,
		DateTime asOf,
		Func<string, decimal> ivResolver,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes,
		(decimal bias, OpenerConfig cfg, string structure, int qty, string rationale, decimal creditPerContract, decimal maxProfit, decimal maxLoss, decimal risk, decimal pop, decimal ev, int days, decimal rawScore, decimal biasScore)? opener = null,
		decimal? technicalBiasOverride = null,
		bool useCostBasisForOpenerScore = false)
	{
		var legQuotes = new List<RiskDiagnosticLegQuote>();
		if (quotes != null)
		{
			// Keep labels stable: short/long for the first pair, otherwise fall back to leg1/leg2/...
			var shortIdx = 0;
			var longIdx = 0;
			foreach (var leg in legs)
			{
				var label = leg.IsLong ? (longIdx++ == 0 ? "long" : $"long{longIdx}") : (shortIdx++ == 0 ? "short" : $"short{shortIdx}");
				if (!quotes.TryGetValue(leg.Symbol, out var q))
				{
					legQuotes.Add(new RiskDiagnosticLegQuote(label, leg.Symbol, null, null, null, null, null, null, null));
					continue;
				}
				legQuotes.Add(new RiskDiagnosticLegQuote(
					Label: label,
					Symbol: q.ContractSymbol,
					Bid: q.Bid,
					Ask: q.Ask,
					ImpliedVolatility: q.ImpliedVolatility,
					HistoricalVolatility: q.HistoricalVolatility,
					ImpliedVolatility5Day: q.ImpliedVolatility5Day,
					OpenInterest: q.OpenInterest,
					Volume: q.Volume));
			}
		}

		decimal? enumDelta = null;
		decimal? enumMin = null;
		decimal? enumMax = null;
		bool? enumPass = null;

		// If this is a 2-leg short vertical, compute the opener delta-gate against ai-config.json's shortVertical band.
		// (Calendars/diagonals are also 2 legs but should not be shown here.)
		if (legs.Count == 2)
		{
			var shortLeg = legs.FirstOrDefault(l => !l.IsLong);
			var longLeg = legs.FirstOrDefault(l => l.IsLong);
			if (shortLeg != null && longLeg != null
				&& shortLeg.Parsed.Root.Equals(longLeg.Parsed.Root, StringComparison.OrdinalIgnoreCase)
				&& shortLeg.Parsed.CallPut == longLeg.Parsed.CallPut
				&& shortLeg.Parsed.ExpiryDate.Date == longLeg.Parsed.ExpiryDate.Date)
			{
				var isShortPutVertical = shortLeg.Parsed.CallPut == "P" && shortLeg.Parsed.Strike > longLeg.Parsed.Strike;
				var isShortCallVertical = shortLeg.Parsed.CallPut == "C" && shortLeg.Parsed.Strike < longLeg.Parsed.Strike;

				if (isShortPutVertical || isShortCallVertical)
				{
					var band = opener.HasValue
						? (opener.Value.cfg.Structures.ShortVertical.ShortDeltaMin, opener.Value.cfg.Structures.ShortVertical.ShortDeltaMax)
						: TryLoadAiConfigQuiet() is AIConfig ai
							? (ai.Opener.Structures.ShortVertical.ShortDeltaMin, ai.Opener.Structures.ShortVertical.ShortDeltaMax)
							: ((decimal?)null, (decimal?)null);

					enumMin = band.Item1;
					enumMax = band.Item2;
					if (enumMin.HasValue && enumMax.HasValue)
					{
						var dte = Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
						var t = dte / 365.0;
						var iv = ivResolver(shortLeg.Symbol);
						enumDelta = Math.Abs(OptionMath.Delta(spot, shortLeg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, shortLeg.Parsed.CallPut));
						enumPass = enumDelta >= enumMin && enumDelta <= enumMax;
					}
				}
			}
		}

		RiskDiagnosticOpenerScore? openerScore = null;
		if (opener.HasValue)
		{
			var o = opener.Value;
			openerScore = new RiskDiagnosticOpenerScore(
				Structure: o.structure,
				Qty: o.qty,
				DebitOrCreditPerContract: o.creditPerContract,
				MaxProfitPerContract: o.maxProfit,
				MaxLossPerContract: o.maxLoss,
				CapitalAtRiskPerContract: o.risk,
				ProbabilityOfProfit: o.pop,
				ExpectedValuePerContract: o.ev,
				DaysToTarget: o.days,
				RawScore: o.rawScore,
				BiasAdjustedScore: o.biasScore,
				Rationale: o.rationale);
		}
		else
		{
			// For non-opener callers (analyze position/risk): try to compute opener-style score/rationale
			// using the same CandidateScorer used by `wa ai once`.
			var ai = TryLoadAiConfigQuiet();
			if (ai != null && quotes != null)
			{
				var scoringQuotes = useCostBasisForOpenerScore
					? OverrideBidAskWithCostBasis(quotes, legs)
					: quotes;

				var bias = technicalBiasOverride ?? 0m;
				CandidateSkeleton? skel = null;
				if (legs.Count == 1)
				{
					var only = legs[0];
					if (only.IsLong)
					{
						var kind = only.Parsed.CallPut == "C" ? OpenStructureKind.LongCall : OpenStructureKind.LongPut;
						skel = new CandidateSkeleton(only.Parsed.Root, kind, new[] { new ProposalLeg("buy", only.Symbol, 1) }, TargetExpiry: only.Parsed.ExpiryDate);
					}
				}
				else if (legs.Count == 2)
				{
					var shortLeg = legs.FirstOrDefault(l => !l.IsLong);
					var longLeg = legs.FirstOrDefault(l => l.IsLong);
					if (shortLeg != null && longLeg != null
						&& shortLeg.Parsed.Root.Equals(longLeg.Parsed.Root, StringComparison.OrdinalIgnoreCase)
						&& shortLeg.Parsed.CallPut == longLeg.Parsed.CallPut)
					{
						OpenStructureKind? kind = null;
						DateTime target;

						if (shortLeg.Parsed.ExpiryDate.Date == longLeg.Parsed.ExpiryDate.Date)
						{
							target = shortLeg.Parsed.ExpiryDate;
							if (shortLeg.Parsed.CallPut == "P" && shortLeg.Parsed.Strike > longLeg.Parsed.Strike)
								kind = OpenStructureKind.ShortPutVertical;
							if (shortLeg.Parsed.CallPut == "C" && shortLeg.Parsed.Strike < longLeg.Parsed.Strike)
								kind = OpenStructureKind.ShortCallVertical;
						}
						else if (shortLeg.Parsed.ExpiryDate.Date < longLeg.Parsed.ExpiryDate.Date)
						{
							target = shortLeg.Parsed.ExpiryDate;
							kind = shortLeg.Parsed.Strike == longLeg.Parsed.Strike
								? OpenStructureKind.LongCalendar
								: OpenStructureKind.LongDiagonal;
						}
						else
						{
							target = shortLeg.Parsed.ExpiryDate;
						}

						if (kind.HasValue)
							skel = new CandidateSkeleton(shortLeg.Parsed.Root, kind.Value,
								new[] { new ProposalLeg("sell", shortLeg.Symbol, 1), new ProposalLeg("buy", longLeg.Symbol, 1) },
								TargetExpiry: target);
					}
				}

				if (skel != null)
				{
					var scored = CandidateScorer.Score(skel, spot, asOf, scoringQuotes, bias, ai.Opener);
					if (scored != null)
					{
						var rationale = CandidateScorer.BuildRationale(scored, bias, ai.Opener);
						openerScore = new RiskDiagnosticOpenerScore(
							Structure: scored.StructureKind.ToString(),
							Qty: scored.Qty,
							DebitOrCreditPerContract: scored.DebitOrCreditPerContract,
							MaxProfitPerContract: scored.MaxProfitPerContract,
							MaxLossPerContract: scored.MaxLossPerContract,
							CapitalAtRiskPerContract: scored.CapitalAtRiskPerContract,
							ProbabilityOfProfit: scored.ProbabilityOfProfit,
							ExpectedValuePerContract: scored.ExpectedValuePerContract,
							DaysToTarget: scored.DaysToTarget,
							RawScore: scored.RawScore,
							BiasAdjustedScore: scored.BiasAdjustedScore,
							Rationale: rationale);
					}
				}
			}
		}

		// Generic fallback rationale for non-verticals.
		if (openerScore == null)
		{
			var legParts = new List<string>();
			foreach (var l in legs)
			{
				var action = l.IsLong ? "BUY" : "SELL";
				var px = l.CostBasisPerShare ?? l.PricePerShare;
				if (px.HasValue)
					legParts.Add($"{action} {l.Symbol} @${px.Value:F2}");
			}
			var netPerShare = legs.Sum(l => (l.IsLong ? -1m : 1m) * (l.CostBasisPerShare ?? l.PricePerShare ?? 0m));
			var netPerContract = netPerShare * 100m;
			var netStr = netPerContract >= 0m
				? $"net credit ${netPerContract:F2}/contract"
				: $"net debit ${Math.Abs(netPerContract):F2}/contract";
			var legsStr = legParts.Count > 0 ? $" ({string.Join(", ", legParts)})" : "";
			openerScore = new RiskDiagnosticOpenerScore(
				Structure: "probe",
				Qty: legs.Count > 0 ? legs[0].Qty : 1,
				DebitOrCreditPerContract: null,
				MaxProfitPerContract: null,
				MaxLossPerContract: null,
				CapitalAtRiskPerContract: null,
				ProbabilityOfProfit: null,
				ExpectedValuePerContract: null,
				DaysToTarget: null,
				RawScore: null,
				BiasAdjustedScore: null,
				Rationale: $"{netStr}{legsStr}");
		}

		return new RiskDiagnosticProbe(
			EnumDelta: enumDelta,
			EnumDeltaMin: enumMin,
			EnumDeltaMax: enumMax,
			EnumDeltaPass: enumPass,
			LegQuotes: legQuotes,
			OpenerScore: openerScore);
	}

	private static IReadOnlyDictionary<string, OptionContractQuote> OverrideBidAskWithCostBasis(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		IReadOnlyList<DiagnosticLeg> legs)
	{
		var map = new Dictionary<string, OptionContractQuote>(quotes, StringComparer.OrdinalIgnoreCase);
		foreach (var leg in legs)
		{
			if (!leg.CostBasisPerShare.HasValue) continue;
			if (!map.TryGetValue(leg.Symbol, out var q)) continue;
			var px = leg.CostBasisPerShare.Value;
			map[leg.Symbol] = q with { Bid = px, Ask = px };
		}
		return map;
	}

	private static AIConfig? TryLoadAiConfigQuiet()
	{
		if (_loaded) return _cachedAiConfig;
		_loaded = true;
		try
		{
			var path = Program.ResolvePath(AIConfigLoader.ConfigPath);
			if (!File.Exists(path)) return null;
			var config = JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(path));
			if (config == null) return null;
			var err = AIConfigLoader.Validate(config);
			if (err != null) return null;
			_cachedAiConfig = config;
			return config;
		}
		catch
		{
			return null;
		}
	}
}
