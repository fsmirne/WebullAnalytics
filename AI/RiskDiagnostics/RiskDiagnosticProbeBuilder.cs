using System.Text.Json;
using WebullAnalytics.AI.Events;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.RiskDiagnostics;

internal static class RiskDiagnosticProbeBuilder
{
	private static readonly Dictionary<string, (AIConfig? Config, string? Reason)> _cachedConfigsByTicker = new(StringComparer.OrdinalIgnoreCase);

	internal static RiskDiagnosticProbe Build(
		IReadOnlyList<DiagnosticLeg> legs,
		decimal spot,
		DateTime asOf,
		Func<string, decimal> ivResolver,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes,
		(decimal bias, OpenerConfig cfg, string structure, int qty, string rationale, decimal creditPerContract, decimal maxProfit, decimal maxLoss, decimal risk, decimal pop, decimal ev, int days, decimal rawScore, decimal biasScore, decimal? thetaPerDayPerContract, decimal? finalScore)? opener = null,
		decimal? technicalBiasOverride = null,
		bool useCostBasisForOpenerScore = false,
		decimal? historicalVolAnnual = null,
		bool useMarketImpliedIv = true,
		decimal? sentimentScore = null,
		TickerEvents? events = null)
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
					Volume: q.Volume,
					VendorImpliedVolatility: q.VendorImpliedVolatility));
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
						: TryLoadAiConfigQuiet(shortLeg.Parsed.Root, out _) is AIConfig ai
							? (ai.Opener.Structures.ShortVertical.ShortDeltaMin, ai.Opener.Structures.ShortVertical.ShortDeltaMax)
							: ((decimal?)null, (decimal?)null);

					enumMin = band.Item1;
					enumMax = band.Item2;
					if (enumMin.HasValue && enumMax.HasValue)
					{
						// Use the same time-to-expiry formula the enumerator uses
						// (OpenerExpiryHelpers.TimeYearsToExpiry — fractional for 0DTE,
						// dte/365 otherwise). The earlier `Math.Max(1, dte)/365` was a
						// stale pre-651eaa2 convention that floored 0DTE TTE at one full
						// day; it produced delta values 3-4× larger than what the
						// enumerator actually computed, so the "FAIL" tag mis-fired on
						// every 0DTE short vertical the engine picked.
						var t = OpenerExpiryHelpers.TimeYearsToExpiry(asOf, shortLeg.Parsed.ExpiryDate);
						// Prefer the chain's live IV (same source the enumerator now reads via
						// ResolveIv). Fall back to ivResolver / IvDefaultPct only when no live
						// quote is available for this strike — that's also the fallback chain
						// the enumerator uses, so the diagnostic and the enumerator stay in
						// lockstep.
						var liveIv = ivResolver(shortLeg.Symbol);
						var iv = liveIv > 0m
							? liveIv
							: (opener.HasValue ? opener.Value.cfg.Indicators.IvDefaultPct / 100m : liveIv);
						enumDelta = Math.Abs(OptionMath.Delta(spot, shortLeg.Parsed.Strike, t, OptionMath.RiskFreeRate, iv, shortLeg.Parsed.CallPut));
						enumPass = enumDelta >= enumMin && enumDelta <= enumMax;
					}
				}
			}
		}

		RiskDiagnosticOpenerScore? openerScore = null;
		string? configUnavailableReason = null;
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
				Rationale: o.rationale,
				ThetaPerDayPerContract: o.thetaPerDayPerContract,
				MarginPerContract: ComputeMarginPerContract(o.structure, legs, o.risk));
		}
		else
		{
			// For non-opener callers (analyze position/risk): try to compute opener-style score/rationale
			// using the same CandidateScorer used by `wa ai once`.
			var ai = TryLoadAiConfigQuiet(legs.Count > 0 ? legs[0].Parsed.Root : null, out configUnavailableReason);
			if (ai != null && quotes != null)
			{
				var scoringQuotes = useCostBasisForOpenerScore
					? OverrideBidAskWithCostBasis(quotes, legs)
					: quotes;

				var bias = technicalBiasOverride ?? 0m;
				var skel = TryBuildCandidateSkeleton(legs);

				if (skel != null)
				{
					// applyLiquidityGate: false — analyze position/risk evaluates positions you've
					// already opened (or are explicitly modeling), so the hard liquidity gate must
					// not silently reject them. The liq factor and rules still surface, so a poorly-
					// liquid existing position is still flagged in the panel.
					// useMarketImpliedIv is suppressed in cost-basis mode: OverrideBidAskWithCostBasis
					// collapses each leg's bid/ask to the entry price, so MarketImpliedIv would
					// back-solve from that stale price instead of the current market mid — inflating
					// ivLong and producing an artificially low breakeven / high POP. ResolveIv (the
					// false branch) reads the Iv field from the quote, which OverrideBidAskWithCostBasis
					// does not touch, giving the current broker-reported IV.
					var effectiveUseMarketImpliedIv = useCostBasisForOpenerScore ? false : useMarketImpliedIv;
					var scored = CandidateScorer.Score(skel, spot, asOf, scoringQuotes, bias, ai.Opener, historicalVolAnnual, applyLiquidityGate: false, useMarketImpliedIv: effectiveUseMarketImpliedIv, sentimentScore: sentimentScore, events: events);
					if (scored != null)
					{
						// When cost-basis override is in play, the scorer's view of bid/ask is collapsed
						// to (px, px) — spread = 0. Liquidity is a forward-looking property of the
						// current market, not of what you paid, so we recompute it from the original
						// quotes and rescale BiasAdjustedScore/FinalScore proportionally (the liquidity
						// factor enters the score chain multiplicatively).
						if (useCostBasisForOpenerScore && quotes != null)
						{
							var (realSpread, realMinOi, realMinRelOi) = CandidateScorer.ComputeLegLiquidityStats(skel.Legs, quotes, spot);
							var realLiqFactor = CandidateScorer.ComputeLiquidityFactor(realSpread, realMinOi, realMinRelOi, ai.Opener.Liquidity.Weight);
							var oldLiqFactor = scored.LiquidityAdjustmentFactor ?? 1m;
							var newLiqFactor = realLiqFactor ?? 1m;
							if (oldLiqFactor > 0m)
							{
								var ratio = newLiqFactor / oldLiqFactor;
								scored = scored with
								{
									WorstLegBidAskSpreadPct = realSpread,
									MinOpenInterest = realMinOi,
									MinRelativeOpenInterest = realMinRelOi,
									LiquidityAdjustmentFactor = realLiqFactor,
									BiasAdjustedScore = scored.BiasAdjustedScore * ratio,
									FinalScore = scored.FinalScore.HasValue ? scored.FinalScore.Value * ratio : (decimal?)null,
								};
							}

							// OverrideBidAskWithCostBasis collapses each leg's bid/ask to the entry price,
							// so ComputeMarketTheoreticalAggregate inside Score() treats the cost basis as
							// "current market mid." Recompute stat-arb from live quotes so the market-net
							// indicator reflects what the position costs to enter today, not what was paid.
							var liveStatArb = CandidateScorer.ComputeMarketTheoreticalAggregate(legs.Select(l => (l.Symbol, l.Parsed, l.IsLong)), spot, asOf, quotes, ai.Indicators.IvDefaultPct, events, ai.Opener.Indicators.Events);
							var newStatArbFactor = CandidateScorer.ComputeStatArbAdjustmentFactor(liveStatArb?.MarketNet, liveStatArb?.TheoreticalNet, liveStatArb?.GrossTheoretical, ai.Opener.Weights.StatArb);
							var oldStatArbFactor = scored.StatArbAdjustmentFactor ?? 1m;
							var newStatArbFactorVal = newStatArbFactor ?? 1m;
							if (oldStatArbFactor > 0m)
							{
								var statArbRatio = newStatArbFactorVal / oldStatArbFactor;
								scored = scored with
								{
									MarketNetPremiumPerShare = liveStatArb?.MarketNet,
									TheoreticalNetPremiumPerShare = liveStatArb?.TheoreticalNet,
									StatArbAdjustmentFactor = newStatArbFactor,
									BiasAdjustedScore = scored.BiasAdjustedScore * statArbRatio,
									FinalScore = scored.FinalScore.HasValue ? scored.FinalScore.Value * statArbRatio : (decimal?)null,
								};
							}
						}

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
							Rationale: rationale,
							ThetaPerDayPerContract: scored.ThetaPerDayPerContract,
							FinalScore: scored.FinalScore,
							MarginPerContract: ComputeMarginPerContract(scored.StructureKind.ToString(), legs, scored.CapitalAtRiskPerContract));
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
			OpenerScore: openerScore,
			ScoreUnavailableReason: configUnavailableReason);
	}

	internal static CandidateSkeleton? TryBuildCandidateSkeleton(IReadOnlyList<DiagnosticLeg> legs)
	{
		if (legs.Count == 0) return null;
		if (legs.Select(l => l.Parsed.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) return null;

		if (legs.Count == 1)
		{
			var only = legs[0];
			if (!only.IsLong) return null;
			var kind = only.Parsed.CallPut == "C" ? OpenStructureKind.LongCall : OpenStructureKind.LongPut;
			return new CandidateSkeleton(only.Parsed.Root, kind, new[] { new ProposalLeg("buy", only.Symbol, 1) }, only.Parsed.ExpiryDate);
		}

		if (legs.Count == 2)
		{
			var shortLeg = legs.FirstOrDefault(l => !l.IsLong);
			var longLeg = legs.FirstOrDefault(l => l.IsLong);
			if (shortLeg == null || longLeg == null) return null;
			if (!shortLeg.Parsed.Root.Equals(longLeg.Parsed.Root, StringComparison.OrdinalIgnoreCase) || shortLeg.Parsed.CallPut != longLeg.Parsed.CallPut)
				return null;

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
				kind = shortLeg.Parsed.Strike == longLeg.Parsed.Strike ? OpenStructureKind.LongCalendar : OpenStructureKind.LongDiagonal;
			}
			else
			{
				return null;
			}

			return kind.HasValue
				? new CandidateSkeleton(shortLeg.Parsed.Root, kind.Value, new[] { new ProposalLeg("sell", shortLeg.Symbol, 1), new ProposalLeg("buy", longLeg.Symbol, 1) }, target)
				: null;
		}

		if (legs.Count == 4)
		{
			var shorts = legs.Where(l => !l.IsLong).ToList();
			var longs = legs.Where(l => l.IsLong).ToList();
			if (shorts.Count != 2 || longs.Count != 2) return null;

			var distinctExpiries = legs.Select(l => l.Parsed.ExpiryDate.Date).Distinct().Count();
			var distinctStrikes = legs.Select(l => l.Parsed.Strike).Distinct().Count();
			var distinctCallPut = legs.Select(l => l.Parsed.CallPut).Distinct().Count();

			// Single-sided condor: one expiry, all puts OR all calls, four distinct strikes, longs at the
			// wings + shorts in the body (long condor) or the reverse (short condor). Maps to the Condor
			// kind so ScoreMultiLeg can price it (EM / breakevens / PoP / EV) for an already-open position;
			// the opener never builds this skeleton, so it's still never proposed.
			if (distinctExpiries == 1 && distinctCallPut == 1 && distinctStrikes == 4)
			{
				var byStrike = legs.OrderBy(l => l.Parsed.Strike).ToList();
				var longWings = byStrike[0].IsLong && !byStrike[1].IsLong && !byStrike[2].IsLong && byStrike[3].IsLong;
				var shortWings = !byStrike[0].IsLong && byStrike[1].IsLong && byStrike[2].IsLong && !byStrike[3].IsLong;
				if (longWings || shortWings)
				{
					var orderedLegs = byStrike.Select(l => new ProposalLeg(l.IsLong ? "buy" : "sell", l.Symbol, 1)).ToList();
					return new CandidateSkeleton(legs[0].Parsed.Root, OpenStructureKind.Condor, orderedLegs, legs[0].Parsed.ExpiryDate);
				}
			}

			if (distinctExpiries == 1 && distinctCallPut == 2)
			{
				var exp = legs[0].Parsed.ExpiryDate;
				var kind = distinctStrikes <= 3 ? OpenStructureKind.IronButterfly : OpenStructureKind.IronCondor;
				var orderedLegs = longs.Where(l => l.Parsed.CallPut == "P").Select(l => new ProposalLeg("buy", l.Symbol, 1))
					.Concat(shorts.Where(l => l.Parsed.CallPut == "P").Select(l => new ProposalLeg("sell", l.Symbol, 1)))
					.Concat(shorts.Where(l => l.Parsed.CallPut == "C").Select(l => new ProposalLeg("sell", l.Symbol, 1)))
					.Concat(longs.Where(l => l.Parsed.CallPut == "C").Select(l => new ProposalLeg("buy", l.Symbol, 1)))
					.ToList();
				return new CandidateSkeleton(legs[0].Parsed.Root, kind, orderedLegs, exp);
			}

			if (distinctExpiries == 2 && distinctCallPut == 2)
			{
				var shortPut = shorts.FirstOrDefault(l => l.Parsed.CallPut == "P");
				var longPut = longs.FirstOrDefault(l => l.Parsed.CallPut == "P");
				var shortCall = shorts.FirstOrDefault(l => l.Parsed.CallPut == "C");
				var longCall = longs.FirstOrDefault(l => l.Parsed.CallPut == "C");
				if (shortPut == null || longPut == null || shortCall == null || longCall == null) return null;
				if (shortPut.Parsed.ExpiryDate.Date != shortCall.Parsed.ExpiryDate.Date) return null;
				if (longPut.Parsed.ExpiryDate.Date != longCall.Parsed.ExpiryDate.Date) return null;
				if (shortPut.Parsed.ExpiryDate.Date >= longPut.Parsed.ExpiryDate.Date || shortCall.Parsed.ExpiryDate.Date >= longCall.Parsed.ExpiryDate.Date) return null;

				var kind = shortPut.Parsed.Strike == longPut.Parsed.Strike && shortCall.Parsed.Strike == longCall.Parsed.Strike
					? OpenStructureKind.DoubleCalendar
					: OpenStructureKind.DoubleDiagonal;
				var orderedLegs = new[]
				{
					new ProposalLeg("sell", shortPut.Symbol, 1),
					new ProposalLeg("buy", longPut.Symbol, 1),
					new ProposalLeg("sell", shortCall.Symbol, 1),
					new ProposalLeg("buy", longCall.Symbol, 1),
				};
				return new CandidateSkeleton(legs[0].Parsed.Root, kind, orderedLegs, shortPut.Parsed.ExpiryDate);
			}
		}

		return null;
	}

	/// <summary>
	/// Broker margin per contract for the proposal. Short verticals and iron spreads collateralize
	/// the full capital-at-risk. Long calendars and covered diagonals hold no broker margin — the
	/// debit is cash, not collateral. Inverted diagonals (long strike past the short for calls,
	/// below for puts) realize the strike gap on assignment, so collateral = strike_loss × 100 +
	/// debit × 100 per AnalyzeCommand.ComputeLegMargin.
	/// </summary>
	private static decimal? ComputeMarginPerContract(string structure, IReadOnlyList<DiagnosticLeg> legs, decimal capitalAtRisk)
	{
		if (structure.Equals(nameof(OpenStructureKind.ShortPutVertical), StringComparison.OrdinalIgnoreCase)
			|| structure.Equals(nameof(OpenStructureKind.ShortCallVertical), StringComparison.OrdinalIgnoreCase)
			|| structure.Equals(nameof(OpenStructureKind.IronButterfly), StringComparison.OrdinalIgnoreCase)
			|| structure.Equals(nameof(OpenStructureKind.IronCondor), StringComparison.OrdinalIgnoreCase))
			return capitalAtRisk;

		if (structure.Equals(nameof(OpenStructureKind.LongCalendar), StringComparison.OrdinalIgnoreCase)
			|| structure.Equals(nameof(OpenStructureKind.LongDiagonal), StringComparison.OrdinalIgnoreCase)
			|| structure.Equals(nameof(OpenStructureKind.DoubleCalendar), StringComparison.OrdinalIgnoreCase)
			|| structure.Equals(nameof(OpenStructureKind.DoubleDiagonal), StringComparison.OrdinalIgnoreCase))
		{
			decimal totalGapPerShare = 0m;
			decimal totalDebitPerShare = 0m;
			foreach (var s in legs.Where(l => !l.IsLong))
			{
				var match = legs.FirstOrDefault(l => l.IsLong && l.Parsed.CallPut == s.Parsed.CallPut);
				if (match == null) continue;
				var gap = s.Parsed.CallPut == "C"
					? Math.Max(match.Parsed.Strike - s.Parsed.Strike, 0m)
					: Math.Max(s.Parsed.Strike - match.Parsed.Strike, 0m);
				totalGapPerShare += gap;
				var sPx = s.PricePerShare ?? s.CostBasisPerShare ?? 0m;
				var lPx = match.PricePerShare ?? match.CostBasisPerShare ?? 0m;
				totalDebitPerShare += Math.Max(lPx - sPx, 0m);
			}
			return totalGapPerShare == 0m ? 0m : (totalGapPerShare + totalDebitPerShare) * 100m;
		}

		return 0m;
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

	/// <summary>Loads the opener config for scoring the probe, merging base → ai-config.<TICKER>.json the
	/// same way AIContext.ResolveConfig does for the live pipeline. Validating the bare base layer is wrong:
	/// it is intentionally incomplete (e.g. indicators.strikeStep lives only in the per-ticker layer), so the
	/// unmerged config fails validation and the probe silently degrades to the generic no-score rationale.</summary>
	/// <summary>Loads the opener config for scoring the probe (base → ai-config.&lt;TICKER&gt;.json, same as
	/// AIContext.ResolveConfig). Returns null when no usable config exists, with <paramref name="unavailableReason"/>
	/// set to a human-readable explanation — surfaced in the diagnostic so the user knows exactly what to create
	/// (e.g. a missing per-ticker ai-config.&lt;TICKER&gt;.json supplying indicators.strikeStep) rather than the probe
	/// silently degrading to a no-score rationale.</summary>
	private static AIConfig? TryLoadAiConfigQuiet(string? ticker, out string? unavailableReason)
	{
		var key = ticker?.ToUpperInvariant() ?? "";
		if (_cachedConfigsByTicker.TryGetValue(key, out var cached)) { unavailableReason = cached.Reason; return cached.Config; }
		AIConfig? result = null;
		string? reason = null;
		try
		{
			var basePath = Program.ResolvePath(AIConfigLoader.ConfigPath);
			if (!File.Exists(basePath))
				reason = $"no base config at {basePath}";
			else
			{
				var paths = new List<string?> { basePath };
				var hasTickerConfig = false;
				if (key.Length > 0)
				{
					var tickerPath = Path.Combine(Path.GetDirectoryName(basePath) ?? string.Empty, $"ai-config.{key}.json");
					if (File.Exists(tickerPath)) { paths.Add(tickerPath); hasTickerConfig = true; }
				}
				var config = AIConfigMerge.LoadMerged(paths.ToArray());
				if (config == null)
					reason = "config failed to load";
				else
				{
					config.Opener.Indicators = config.Indicators; // same wiring as ResolveConfig — cfg-only helpers reach indicators through Opener
					var err = AIConfigLoader.Validate(config);
					if (err == null) result = config;
					else reason = hasTickerConfig
						? $"ai-config.{key}.json is invalid: {err}"
						: $"no per-ticker config ai-config.{key}.json — {err}";
				}
			}
		}
		catch (Exception ex)
		{
			result = null;
			reason = ex.Message;
		}
		_cachedConfigsByTicker[key] = (result, reason);
		unavailableReason = reason;
		return result;
	}
}
