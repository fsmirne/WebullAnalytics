namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: close a calendar/diagonal on ANY day once spot comes within <c>bufferPct</c> of either
/// short-expiry break-even (or is already past it), regardless of P&L — "get out before the tent's edge,
/// not after". Unlike CloseBeforeShortExpiryRule's emergency close (expiry day only, fires a buffer PAST
/// the band), this guards the whole life of the trade from inside the band.
/// </summary>
internal sealed class BreakEvenGuardRule : IManagementRule
{
	private readonly BreakEvenGuardConfig _config;

	public BreakEvenGuardRule(BreakEvenGuardConfig config) { _config = config; }

	public string Name => "BreakEvenGuardRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Quantity <= 0) return null;
		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot)) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null && l.Expiry.HasValue);
		if (shortLeg == null || shortLeg.Expiry!.Value.Date < ctx.Now.Date) return null;

		var (low, high, _) = PositionBreakEvenEstimator.Estimate(position, ctx);
		var nearLow = low.HasValue && spot <= low.Value * (1m + _config.BufferPct);
		var nearHigh = high.HasValue && spot >= high.Value * (1m - _config.BufferPct);
		if (!nearLow && !nearHigh) return null;

		var be = nearLow ? low!.Value : high!.Value;
		var legs = position.Legs.Select(l => new ProposalLeg(Action: l.Side == Side.Buy ? "sell" : "buy", Symbol: l.Symbol, Qty: l.Qty)).ToList();
		return new ManagementProposal(
			Rule: "BreakEvenGuardRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: 0m,
			Rationale: $"spot ${spot:F2} within {_config.BufferPct * 100m:F2}% of {(nearLow ? "lower" : "upper")} BE ${be:F2}, close all {position.Quantity}"
		);
	}
}
