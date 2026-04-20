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
		// fundable scenario (positive BPDelta ≤ availableCash, or BPDelta ≤ 0).
		ScenarioEngine.ScenarioResult? hold = scenarios.FirstOrDefault(s => s.ProposalLegs.Count == 0);
		ScenarioEngine.ScenarioResult? topFundable = null;
		foreach (var s in scenarios)
		{
			if (s.ProposalLegs.Count == 0) continue; // skip "hold"/alert-only
			var bpTotal = s.BPDeltaPerContract * s.Qty;
			if (bpTotal > 0m && availableCash > 0m && bpTotal > availableCash) continue;
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
		var rationale = $"optimizer: {topFundable.Name} projects ${topPerDay:+0.00;-0.00}/ct/day vs hold ${holdPerDay:+0.00;-0.00}/ct/day (Δ ${improvementPerDayPerContract:+0.00;-0.00}/ct/day over {topFundable.DaysToTarget}d). {topFundable.Rationale}";

		return new ManagementProposal(
			Rule: "OpportunisticRollRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: topFundable.Kind,
			Legs: topFundable.ProposalLegs,
			NetDebit: netDebitPerContract,
			Rationale: rationale);
	}
}
