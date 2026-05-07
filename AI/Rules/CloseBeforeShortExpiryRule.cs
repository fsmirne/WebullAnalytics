namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: on the day the short leg expires, propose closing the entire position once it's
/// comfortably profitable. Decision-only — it emits a single Close proposal for the full quantity.
/// Time-windowed scaled execution lives in <c>WatchAutoExecutor</c>; this rule does not care how
/// the close is filled, only when to surface the proposal.
///
/// Triggers:
/// 1. Short leg has DTE == 0 (we're inside the expiry session).
/// 2. Mark-to-market profit on the position is at least <c>minProfitPct</c> of initial debit
///    (default 30%) — locking in a real win, not bailing on a marginal trade.
/// 3. Emergency override: spot is past the calendar/diagonal break-even band (with the configured
///    buffer), in which case we close regardless of profit threshold — past BE every additional
///    tick is realized loss.
/// </summary>
internal sealed class CloseBeforeShortExpiryRule : IManagementRule
{
	private readonly CloseBeforeShortExpiryConfig _config;

	public CloseBeforeShortExpiryRule(CloseBeforeShortExpiryConfig config) { _config = config; }

	public string Name => "CloseBeforeShortExpiryRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Quantity <= 0) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null || !shortLeg.Expiry.HasValue) return null;

		// Only fire on the actual expiry day; pre-expiry adjustments belong to the roll rules.
		var dte = (shortLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte != 0) return null;

		// Emergency: spot past BE band → close immediately, profit threshold doesn't apply.
		if (IsSpotPastBreakEven(position, ctx, out var spot, out var beLow, out var beHigh))
		{
			return BuildClose(position,
				$"emergency: spot ${spot:F2} outside BE band [${beLow:F2}, ${beHigh:F2}] on expiry day, close all {position.Quantity}",
				isEmergency: true);
		}

		// Profit gate: mark-to-market value vs. initial debit.
		var markPerContract = ComputeMarkPerContract(position, ctx);
		if (markPerContract == null) return null;

		var initialDebit = Math.Abs(position.InitialNetDebit);
		if (initialDebit <= 0m) return null;

		var profitPerContract = markPerContract.Value - initialDebit;
		var profitPct = profitPerContract / initialDebit * 100m;
		if (profitPct < _config.MinProfitPct) return null;

		return BuildClose(position,
			$"expiry day, profit ${profitPerContract:F2}/contract = {profitPct:F1}% ≥ threshold {_config.MinProfitPct:F1}%, close all {position.Quantity}",
			isEmergency: false);
	}

	/// <summary>Computes the per-contract mark-to-market value (sum of leg midpoint values signed by
	/// direction). Returns null when any leg lacks a usable bid/ask.</summary>
	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (q.Bid == null || q.Ask == null || q.Ask.Value <= 0m) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	private bool IsSpotPastBreakEven(OpenPosition position, EvaluationContext ctx, out decimal spot, out decimal beLow, out decimal beHigh)
	{
		spot = 0m; beLow = 0m; beHigh = 0m;
		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out spot)) return false;

		var (low, high, _) = PositionBreakEvenEstimator.Estimate(position, ctx);
		if (!low.HasValue || !high.HasValue) return false;
		beLow = low.Value;
		beHigh = high.Value;

		var buffer = _config.EmergencyBreakEvenBufferPct / 100m;
		return spot < beLow * (1m - buffer) || spot > beHigh * (1m + buffer);
	}

	private static ManagementProposal BuildClose(OpenPosition p, string rationale, bool isEmergency)
	{
		var legs = p.Legs.Select(l => new ProposalLeg(
			Action: l.Side == Side.Buy ? "sell" : "buy",
			Symbol: l.Symbol,
			Qty: l.Qty
		)).ToList();

		return new ManagementProposal(
			Rule: "CloseBeforeShortExpiryRule",
			Ticker: p.Ticker,
			PositionKey: p.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: 0m,
			Rationale: (isEmergency ? "[emergency] " : "") + rationale
		);
	}
}
