using System.Globalization;

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

	public OpportunisticRollRule(OpportunisticRollConfig config) { _config = config; }

	public string Name => "OpportunisticRollRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Legs.Count < 1) return null;

		// Translate OpenPosition → ScenarioEngine's neutral LegInfo.
		var legInfos = new List<ScenarioEngine.LegInfo>(position.Legs.Count);
		foreach (var leg in position.Legs)
		{
			if (leg.CallPut == null || !leg.Expiry.HasValue) return null;
			var parsed = new OptionParsed(position.Ticker, leg.Expiry.Value, leg.CallPut, leg.Strike);
			legInfos.Add(new ScenarioEngine.LegInfo(leg.Symbol, IsLong: leg.Side == Side.Buy, Qty: leg.Qty, parsed));
		}

		var kind = ScenarioEngine.Classify(legInfos);
		if (kind is ScenarioEngine.StructureKind.Unsupported or ScenarioEngine.StructureKind.Vertical or ScenarioEngine.StructureKind.SingleShort)
			return null;

		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m) return null;

		var shortLeg = legInfos.FirstOrDefault(l => !l.IsLong);
		var callPut = shortLeg?.Parsed.CallPut ?? legInfos[0].Parsed.CallPut;

		if (_config.TechnicalFilter.Enabled
			&& ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias)
			&& bias.IsAdverse(callPut, _config.TechnicalFilter.BullishBlockThreshold, _config.TechnicalFilter.BearishBlockThreshold))
			return null;

		var availableCash = Math.Max(0m, ctx.AccountCash - CashReserveHelper.ComputeReserve("percent", 0m, ctx.AccountValue));
		var opt = new ScenarioEngine.EvaluateOptions(
			InitialNetDebitPerShare: position.AdjustedNetDebit,
			IvDefault: _config.IvDefaultPct,
			StrikeStep: _config.StrikeStep,
			AvailableCash: availableCash > 0m ? availableCash : null,
			IvOverrides: null);

		var scenarios = ScenarioEngine.Evaluate(legInfos, kind, spot, ctx.Now, ctx.Quotes, opt);
		if (scenarios.Count == 0) return null;

		// Find "Hold" baseline (highest-P&L-per-day scenario that involves no execution) and top
		// fundable scenario (positive BPDelta ≤ availableCash, or BPDelta ≤ 0) that passes risk checks.
		ScenarioEngine.ScenarioResult? hold = scenarios.FirstOrDefault(s => s.ProposalLegs.Count == 0);
		ScenarioEngine.ScenarioResult? topFundable = null;
		var rollSafetyNote = "";
		foreach (var s in scenarios)
		{
			if (s.ProposalLegs.Count == 0) continue; // skip "hold"/alert-only
			var bpTotal = s.BPDeltaPerContract * s.Qty;
			if (bpTotal > 0m && availableCash > 0m && bpTotal > availableCash) continue;
			if (s.Kind == ProposalKind.Roll && !PassesRollRiskChecks(s, position, spot, ctx, _config, out rollSafetyNote)) continue;
			topFundable = s;
			break;
		}
		if (topFundable == null) return null;

		// Compare P&L-per-day vs hold. If hold is null (shouldn't normally be), treat hold as 0.
		var topPerDay = topFundable.TotalPnLPerContract / Math.Max(1m, topFundable.DaysToTarget);
		var holdPerDay = hold != null ? hold.TotalPnLPerContract / Math.Max(1m, hold.DaysToTarget) : 0m;

		// Require absolute P&L-per-day improvement above the configured threshold, in dollars per contract per day.
		// (Relative-percent thresholds are unreliable near zero.)
		var improvementPerDayPerContract = topPerDay - holdPerDay;
		if (improvementPerDayPerContract < _config.MinImprovementPerDayPerContract) return null;

		// Build the proposal.
		var netDebitPerContract = topFundable.CashImpactPerContract;
		var biasNote = ctx.TechnicalSignals.TryGetValue(position.Ticker, out var biasSignal) ? $" [tech score {biasSignal.Score:+0.00;-0.00}]" : "";
		var rationale = $"optimizer: {topFundable.Name} projects ${topPerDay:+0.00;-0.00}/ct/day vs hold ${holdPerDay:+0.00;-0.00}/ct/day (Δ ${improvementPerDayPerContract:+0.00;-0.00}/ct/day over {topFundable.DaysToTarget}d){biasNote}{rollSafetyNote}. {topFundable.Rationale}";

		return new ManagementProposal(
			Rule: "OpportunisticRollRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: topFundable.Kind,
			Legs: topFundable.ProposalLegs,
			NetDebit: netDebitPerContract,
			Rationale: rationale);
	}

	/// <summary>Runs the four sequential safety gates on a Roll scenario. Returns false to skip, true to accept.
	/// On true, safetyNote contains a formatted string to embed in the proposal rationale.</summary>
	private static bool PassesRollRiskChecks(ScenarioEngine.ScenarioResult s, OpenPosition position, decimal spot, EvaluationContext ctx, OpportunisticRollConfig config, out string safetyNote)
	{
		safetyNote = "";

		var newShortProposalLeg = s.ProposalLegs.FirstOrDefault(l => l.Action == "sell");
		var oldShortProposalLeg = s.ProposalLegs.FirstOrDefault(l => l.Action == "buy");
		if (newShortProposalLeg == null) return true; // "Close short only" scenario: buys back existing short without a new short leg — reduces risk, accept unconditionally

		var newShort = ParsingHelpers.ParseOptionSymbol(newShortProposalLeg.Symbol);
		if (newShort == null) return false;

		// Gate 1: OTM guard — new short must be out-of-the-money.
		if (newShort.CallPut == "P" && newShort.Strike >= spot) return false;
		if (newShort.CallPut == "C" && newShort.Strike <= spot) return false;

		// Gate 2: OTM buffer adjusted by technical extension.
		var compositeScore = ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias) ? bias.Score : 0m;
		var requiredOtmFraction = config.BaseOtmBufferPct * (1m + Math.Abs(compositeScore) * config.TechnicalBufferMultiplier) / 100m;
		var actualOtmFraction = newShort.CallPut == "P"
			? (spot - newShort.Strike) / spot
			: (newShort.Strike - spot) / spot;
		if (actualOtmFraction < requiredOtmFraction) return false;

		// Gate 3: Break-even at current spot at new short expiry.
		var beMargin = ComputeBreakEvenMargin(position, newShort, spot, ctx, config);
		if (beMargin < 0m) return false;

		// Gate 4: Delta change cap.
		var ivDefault = config.IvDefaultPct / 100m;
		var currentDelta = ComputeNetDelta(position.Legs, spot, ctx.Now, ctx.Quotes, ivDefault);
		var proposedDelta = ComputeProposedDelta(position.Legs, spot, ctx.Now, ctx.Quotes, ivDefault, oldShortProposalLeg?.Symbol, newShort, newShortProposalLeg.Symbol);
		var maxAllowedAbsDelta = Math.Abs(currentDelta) * (1m + config.MaxDeltaIncreasePct / 100m);
		if (Math.Abs(proposedDelta) > maxAllowedAbsDelta) return false;

		safetyNote = $" [OTM: {actualOtmFraction * 100m:F1}% (req {requiredOtmFraction * 100m:F1}%), BE: {(beMargin >= 0m ? "+" : "")}${beMargin:F2}/sh, Δ: {currentDelta:+0.00;-0.00}→{proposedDelta:+0.00;-0.00}]";
		return true;
	}

	/// <summary>Net value per share of the position at new short expiry at current spot.
	/// Long legs are valued via Black-Scholes with their remaining time; new short is valued at intrinsic (T=0).</summary>
	private static decimal ComputeBreakEvenMargin(OpenPosition position, OptionParsed newShort, decimal spot, EvaluationContext ctx, OpportunisticRollConfig config)
	{
		var shortExpiry = newShort.ExpiryDate.Date;
		var ivDefault = config.IvDefaultPct / 100m;
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
