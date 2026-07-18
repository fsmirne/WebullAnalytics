using WebullAnalytics.AI.Output;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Runs the shared ScenarioEngine against each open position and emits a proposal when the
/// top fundable scenario projects a meaningfully higher P&L-per-day than doing nothing.
/// Replaces the discrete DefensiveRoll and RollShortOnExpiry rules — when rolling is the
/// right move, the engine surfaces it; when holding is best, nothing fires.
/// </summary>
internal sealed class OpportunisticRollRule : IManagementRule
{
	private readonly OpportunisticRollConfig _config;
	private readonly IndicatorsConfig _indicators;
	private readonly OpenerRealizedExpectancyConfig _frictionConfig;
	private readonly bool _debug;
	private readonly string _pricingMode;

	public OpportunisticRollRule(OpportunisticRollConfig config, IndicatorsConfig indicators, OpenerRealizedExpectancyConfig frictionConfig, bool debug = false, string pricingMode = SuggestionPricing.Mid)
	{
		_config = config;
		_indicators = indicators;
		_frictionConfig = frictionConfig;
		_debug = debug;
		_pricingMode = SuggestionPricing.Normalize(pricingMode);
	}

	public string Name => "OpportunisticRollRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled)
		{
			Debug(position, "skipped: rule disabled");
			return null;
		}
		if (position.Legs.Count < 1)
		{
			Debug(position, "skipped: no legs");
			return null;
		}

		// Translate OpenPosition → ScenarioEngine's neutral LegInfo.
		var legInfos = new List<ScenarioEngine.LegInfo>(position.Legs.Count);
		foreach (var leg in position.Legs)
		{
			if (leg.CallPut == null || !leg.Expiry.HasValue)
			{
				Debug(position, $"skipped: leg '{leg.Symbol}' missing option metadata");
				return null;
			}
			var parsed = new OptionParsed(position.Ticker, leg.Expiry.Value, leg.CallPut, leg.Strike);
			legInfos.Add(new ScenarioEngine.LegInfo(leg.Symbol, IsLong: leg.Side == Side.Buy, Qty: leg.Qty, parsed));
		}

		var kind = ScenarioEngine.Classify(legInfos);
		if (kind is ScenarioEngine.StructureKind.Unsupported or ScenarioEngine.StructureKind.Vertical or ScenarioEngine.StructureKind.SingleShort)
		{
			Debug(position, $"skipped: unsupported structure kind {kind}");
			return null;
		}

		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m)
		{
			Debug(position, "skipped: missing or non-positive spot");
			return null;
		}

		var shortLeg = legInfos.FirstOrDefault(l => !l.IsLong);
		var callPut = shortLeg?.Parsed.CallPut ?? legInfos[0].Parsed.CallPut;

		if (_indicators.TechnicalFilter.Enabled
			&& ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias)
			&& bias.IsAdverse(callPut, _config.BullishBlockThreshold, _config.BearishBlockThreshold))
		{
			Debug(position, $"skipped: technical filter adverse for {callPut} short (score {bias.Score:+0.00;-0.00})");
			return null;
		}

		var availableCash = Math.Max(0m, ctx.AccountCash - CashReserveHelper.ComputeReserve("percent", 0m, ctx.AccountValue));
		var opt = new ScenarioEngine.EvaluateOptions(
			InitialNetDebitPerShare: position.AdjustedNetDebit,
			IvDefault: _indicators.IvDefaultPct * 100m,   // ScenarioEngine.IvDefault is 0-100 ("percent"); IvDefaultPct is now a 0-1 fraction
			StrikeStep: _indicators.StrikeStep,
			AvailableCash: availableCash > 0m ? availableCash : null,
			IvOverrides: null,
			PricingMode: _pricingMode);

		var scenarios = ScenarioEngine.Evaluate(legInfos, kind, spot, ctx.Now, ctx.Quotes, opt);
		if (scenarios.Count == 0)
		{
			Debug(position, "skipped: scenario engine returned no scenarios");
			return null;
		}

		// Find "Hold" baseline (highest-P&L-per-day scenario that involves no execution) and top
		// fundable scenario (positive MarginDelta ≤ availableCash, or MarginDelta ≤ 0) that passes risk checks.
		ScenarioEngine.ScenarioResult? hold = scenarios.FirstOrDefault(s => s.ProposalLegs.Count == 0);
		ScenarioEngine.ScenarioResult? topFundable = null;
		ScenarioEngine.ScenarioResult? bestCandidate = scenarios.FirstOrDefault(s => s.ProposalLegs.Count > 0);
		var rollSafetyNote = "";
		foreach (var s in scenarios)
		{
			if (s.ProposalLegs.Count == 0) continue; // skip "hold"/alert-only
			var marginTotal = s.MarginDeltaPerContract * s.Qty;
			if (marginTotal > 0m && availableCash > 0m && marginTotal > availableCash)
			{
				Debug(position, $"candidate '{s.Name}' skipped: margin ${marginTotal:N2} exceeds available ${availableCash:N2}");
				continue;
			}
			if (s.Kind == ProposalKind.Roll && !PassesRollRiskChecks(s, position, spot, ctx, _config, _indicators, out rollSafetyNote, out var rejectReason))
			{
				Debug(position, $"candidate '{s.Name}' skipped: {rejectReason}");
				continue;
			}
			topFundable = s;
			break;
		}
		if (topFundable == null)
		{
			var holdPerDayMissing = hold != null ? hold.TotalPnLPerContract / Math.Max(1m, hold.DaysToTarget) : 0m;
			var bestName = bestCandidate?.Name ?? "none";
			Debug(position, $"skipped: no fundable/safe scenario survived (hold ${holdPerDayMissing:+0.00;-0.00}/ct/day, best candidate '{bestName}')");
			return null;
		}

		// Compare TOTAL projected P&L rather than per-day rate. The per-day comparison rewards "fast
		// capture" of unrealized gain (a 1-day Close action looks like $189/day vs a 4-day Hold at
		// $50/day) but ignores that after closing we're idle for the rest of the hold horizon.
		// Over the SAME calendar window the totals are what actually matter: $189 close vs $203 hold
		// means hold wins, even though close's per-day rate is bigger. ScenarioEngine.TotalPnLPerContract
		// is the right field — value at target + cash impact − initial debit, signed per contract.
		var topTotal = topFundable.TotalPnLPerContract;
		var holdTotal = hold != null ? hold.TotalPnLPerContract : 0m;
		var holdHorizonDays = hold != null ? Math.Max(1, hold.DaysToTarget) : Math.Max(1, topFundable.DaysToTarget);

		// Swap friction: a close+open pair crosses the spread twice. Subtract from the swap side so
		// the comparison reflects real execution cost. (ScenarioEngine.TotalPnLPerContract excludes
		// friction entirely.) Scenario engine only emits single-execution structures here so
		// ordersForStructure = 1; total = slippage × 100 × 2.
		var swapFrictionPerContract = _frictionConfig.Enabled && _frictionConfig.SlippagePerSharePerOrder > 0m
			? _frictionConfig.SlippagePerSharePerOrder * 100m * 2m
			: 0m;

		// Translate the per-day knob to a total-PnL threshold over the hold horizon. This keeps the
		// config knob's intuitive meaning ("need at least $0.50/day improvement") while doing the
		// comparison in total-PnL space.
		var minImprovementTotal = _config.MinImprovementPerDayPerContract * holdHorizonDays;
		var requiredImprovementTotal = minImprovementTotal + swapFrictionPerContract;

		var improvementTotal = topTotal - holdTotal;
		if (improvementTotal < requiredImprovementTotal)
		{
			Debug(position, $"skipped: best '{topFundable.Name}' improves ${improvementTotal:+0.00;-0.00}/ct (top ${topTotal:+0.00;-0.00} vs hold ${holdTotal:+0.00;-0.00} over {holdHorizonDays}d); threshold ${minImprovementTotal:F2} + friction ${swapFrictionPerContract:F2} = ${requiredImprovementTotal:F2}/ct");
			return null;
		}

		Debug(position, $"selected '{topFundable.Name}': hold ${holdTotal:+0.00;-0.00}/ct, candidate ${topTotal:+0.00;-0.00}/ct, Δ ${improvementTotal:+0.00;-0.00}/ct over {holdHorizonDays}d (threshold ${requiredImprovementTotal:F2})");

		// Build the proposal.
		var netDebitPerContract = topFundable.CashImpactPerContract;
		var biasNote = ctx.TechnicalSignals.TryGetValue(position.Ticker, out var biasSignal) ? $" [tech score {biasSignal.Score:+0.00;-0.00}]" : "";
		var rationale = $"optimizer: {topFundable.Name} projects ${topTotal:+0.00;-0.00}/ct vs hold ${holdTotal:+0.00;-0.00}/ct (Δ ${improvementTotal:+0.00;-0.00}/ct over {holdHorizonDays}d, friction ${swapFrictionPerContract:F2}){biasNote}{rollSafetyNote}. {topFundable.Rationale}";

		return new ManagementProposal(
			Rule: "OpportunisticRollRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: topFundable.Kind,
			Legs: topFundable.ProposalLegs,
			NetDebit: netDebitPerContract,
			Rationale: rationale);
	}

	private void Debug(OpenPosition position, string message)
	{
		if (!_debug) return;
		Console.Error.WriteLine($"[debug] OpportunisticRollRule {position.Key}: {message}");
	}

	/// <summary>Runs the four sequential safety gates on a Roll scenario. Returns false to skip, true to accept.
	/// On true, safetyNote contains a formatted string to embed in the proposal rationale.</summary>
	private static bool PassesRollRiskChecks(ScenarioEngine.ScenarioResult s, OpenPosition position, decimal spot, EvaluationContext ctx, OpportunisticRollConfig config, IndicatorsConfig indicators, out string safetyNote, out string rejectReason)
	{
		safetyNote = "";
		rejectReason = "";

		var newShortProposalLeg = s.ProposalLegs.FirstOrDefault(l => l.Action == "sell");
		var oldShortProposalLeg = s.ProposalLegs.FirstOrDefault(l => l.Action == "buy");
		if (newShortProposalLeg == null) return true; // "Close short only" scenario: buys back existing short without a new short leg — reduces risk, accept unconditionally

		var newShort = ParsingHelpers.ParseOptionSymbol(newShortProposalLeg.Symbol);
		if (newShort == null)
		{
			rejectReason = $"could not parse new short symbol '{newShortProposalLeg.Symbol}'";
			return false;
		}

		// Gate 1: OTM guard — new short must be out-of-the-money.
		if (newShort.CallPut == "P" && newShort.Strike >= spot)
		{
			rejectReason = $"OTM guard failed: put short strike ${newShort.Strike:F2} >= spot ${spot:F2}";
			return false;
		}
		if (newShort.CallPut == "C" && newShort.Strike <= spot)
		{
			rejectReason = $"OTM guard failed: call short strike ${newShort.Strike:F2} <= spot ${spot:F2}";
			return false;
		}

		// Gate 2: OTM buffer adjusted by technical extension.
		var compositeScore = ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias) ? bias.Score : 0m;
		var technicalFactor = 1m + Math.Abs(compositeScore) * config.TechnicalBufferMultiplier;
		var requiredOtmFraction = config.BaseOtmBufferPct * technicalFactor;
		var actualOtmFraction = newShort.CallPut == "P"
			? (spot - newShort.Strike) / spot
			: (newShort.Strike - spot) / spot;
		if (actualOtmFraction < requiredOtmFraction)
		{
			rejectReason = $"OTM buffer failed: actual {actualOtmFraction * 100m:F1}% < required {requiredOtmFraction * 100m:F1}%";
			return false;
		}

		// Gate 3: Break-even at current spot at new short expiry — must be profitable by at least minBreakEvenMarginPct of spot (widened by same technical factor as OTM buffer).
		var minBeMargin = spot * config.MinBreakEvenMarginPct * technicalFactor;
		var beMargin = ComputeBreakEvenMargin(position, newShort, spot, ctx, indicators);
		if (beMargin < minBeMargin)
		{
			rejectReason = $"break-even margin failed: +${beMargin:F2}/sh < min ${minBeMargin:F2}/sh";
			return false;
		}

		// Gate 4: Delta change cap.
		var ivDefault = indicators.IvDefaultPct;
		var currentDelta = ComputeNetDelta(position.Legs, spot, ctx.Now, ctx.Quotes, ivDefault);
		var proposedDelta = ComputeProposedDelta(position.Legs, spot, ctx.Now, ctx.Quotes, ivDefault, oldShortProposalLeg?.Symbol, newShort, newShortProposalLeg.Symbol);
		var maxAllowedAbsDelta = Math.Abs(currentDelta) * (1m + config.MaxDeltaIncreasePct);
		if (Math.Abs(proposedDelta) > maxAllowedAbsDelta)
		{
			rejectReason = $"delta cap failed: |{proposedDelta:+0.00;-0.00}| > max {maxAllowedAbsDelta:0.00}";
			return false;
		}

		safetyNote = $" [OTM: {actualOtmFraction * 100m:F1}% (req {requiredOtmFraction * 100m:F1}%), BE: +${beMargin:F2}/sh (min ${minBeMargin:F2}/sh), Δ: {currentDelta:+0.00;-0.00}→{proposedDelta:+0.00;-0.00}]";
		return true;
	}

	/// <summary>Net value per share of the position at new short expiry at current spot.
	/// Long legs are valued via Black-Scholes with their remaining time; new short is valued at intrinsic (T=0).</summary>
	private static decimal ComputeBreakEvenMargin(OpenPosition position, OptionParsed newShort, decimal spot, EvaluationContext ctx, IndicatorsConfig indicators)
	{
		var shortExpiry = newShort.ExpiryDate.Date;
		var ivDefault = indicators.IvDefaultPct;
		var longValue = 0m;
		foreach (var leg in position.Legs)
		{
			if (leg.Side != Side.Buy || leg.CallPut == null || !leg.Expiry.HasValue) continue;
			var remainingYears = Math.Max(0.0, (leg.Expiry.Value.Date - shortExpiry).TotalDays / 365.0);
			var iv = ctx.Quotes.TryGetValue(leg.Symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
				? q.ImpliedVolatility.Value : ivDefault;
			longValue += OptionMath.BlackScholes(spot, leg.Strike, remainingYears, OptionMath.RiskFreeRate, iv, leg.CallPut);
		}
		var shortIntrinsic = OptionMath.Intrinsic(spot, newShort.Strike, newShort.CallPut);
		// All terms are per-share: longValue and shortIntrinsic are BS/intrinsic values per share;
		// AdjustedNetDebit is the cumulative per-share cost basis (confirmed by ScenarioEngine call site).
		return longValue - shortIntrinsic - position.AdjustedNetDebit;
	}

	/// <summary>Net Black-Scholes delta of a set of legs at the given spot and time. Long legs contribute +delta, short legs -delta.</summary>
	private static decimal ComputeNetDelta(IReadOnlyList<PositionLeg> legs, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal ivDefault)
	{
		var netDelta = 0m;
		foreach (var leg in legs)
		{
			if (leg.CallPut == null || !leg.Expiry.HasValue) continue;
			var years = Math.Max(0.0, (leg.Expiry.Value.Date - asOf.Date).TotalDays / 365.0);
			var iv = quotes.TryGetValue(leg.Symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
				? q.ImpliedVolatility.Value : ivDefault;
			var delta = OptionMath.Delta(spot, leg.Strike, years, OptionMath.RiskFreeRate, iv, leg.CallPut);
			netDelta += leg.Side == Side.Buy ? delta : -delta;
		}
		return netDelta;
	}

	/// <summary>Net delta after the roll: removes the old short leg's contribution and adds the new short's.</summary>
	private static decimal ComputeProposedDelta(IReadOnlyList<PositionLeg> legs, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal ivDefault, string? oldShortSymbol, OptionParsed newShort, string newShortSymbol)
	{
		var netDelta = ComputeNetDelta(legs, spot, asOf, quotes, ivDefault);

		// Undo the old short's contribution (it was subtracted as a short leg; add it back).
		if (oldShortSymbol != null)
		{
			var oldLeg = legs.FirstOrDefault(l => l.Symbol == oldShortSymbol);
			if (oldLeg != null && oldLeg.CallPut != null && oldLeg.Expiry.HasValue)
			{
				var years = Math.Max(0.0, (oldLeg.Expiry.Value.Date - asOf.Date).TotalDays / 365.0);
				var iv = quotes.TryGetValue(oldShortSymbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
					? q.ImpliedVolatility.Value : ivDefault;
				netDelta += OptionMath.Delta(spot, oldLeg.Strike, years, OptionMath.RiskFreeRate, iv, oldLeg.CallPut); // undo -delta
			}
		}

		// Add new short's contribution (-delta).
		var newYears = Math.Max(0.0, (newShort.ExpiryDate.Date - asOf.Date).TotalDays / 365.0);
		// IV for new short: live quote → old short's IV proxy → default
		decimal newIv;
		if (quotes.TryGetValue(newShortSymbol, out var newQ) && newQ.ImpliedVolatility.HasValue && newQ.ImpliedVolatility.Value > 0m)
			newIv = newQ.ImpliedVolatility.Value;
		else if (oldShortSymbol != null && quotes.TryGetValue(oldShortSymbol, out var oldQ) && oldQ.ImpliedVolatility.HasValue && oldQ.ImpliedVolatility.Value > 0m)
			newIv = oldQ.ImpliedVolatility.Value;
		else
			newIv = ivDefault;

		netDelta -= OptionMath.Delta(spot, newShort.Strike, newYears, OptionMath.RiskFreeRate, newIv, newShort.CallPut);
		return netDelta;
	}
}
