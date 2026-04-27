namespace WebullAnalytics.AI;

/// <summary>
/// Kind of action a rule proposes for a position.
/// </summary>
internal enum ProposalKind
{
	Close,       // flat-close the position at current mid
	Roll,        // roll the short leg (same strike or up-and-out)
	AlertOnly    // rule matched but no actionable improvement; surface for awareness
}

/// <summary>
/// A structured leg in a proposed action: action + OCC symbol + quantity.
/// For Close proposals, the legs describe the closing trades.
/// For Roll proposals, the legs describe the buy-to-close and sell-to-open pair (or vice-versa).
/// </summary>
/// <param name="Action">"buy" or "sell" (explicit, no sign math).</param>
/// <param name="Symbol">OCC option symbol or equity ticker.</param>
/// <param name="Qty">Positive integer.</param>
/// <param name="PricePerShare">Suggested default limit price per share, typically mid.</param>
/// <param name="ExecutionPricePerShare">Optional bid/ask execution price per share for conservative suggestions.</param>
internal record ProposalLeg(string Action, string Symbol, int Qty, decimal? PricePerShare = null, decimal? ExecutionPricePerShare = null);

/// <summary>
/// Output of a single rule evaluation.
/// </summary>
/// <param name="Rule">Rule class name, e.g., "StopLossRule".</param>
/// <param name="Ticker">Underlying symbol the proposal concerns, e.g., "GME".</param>
/// <param name="PositionKey">Stable identifier for the position, used for fingerprinting.
/// Format: "{ticker}_{strategyKind}_{strike}_{expiry:yyyyMMdd}".</param>
/// <param name="Kind">Close / Roll / AlertOnly.</param>
/// <param name="Legs">Structured leg list describing the proposed trades.</param>
/// <param name="NetDebit">Net price across all legs (negative = debit paid; positive = credit received).</param>
/// <param name="Rationale">Human-readable explanation of why the rule fired, with concrete numbers.</param>
/// <param name="CashReserveBlocked">True if this proposal would violate the configured cash reserve.</param>
/// <param name="CashReserveDetail">When blocked, a detail string like "free $Y, requires $X". Null otherwise.</param>
internal sealed record ManagementProposal(
	string Rule,
	string Ticker,
	string PositionKey,
	ProposalKind Kind,
	IReadOnlyList<ProposalLeg> Legs,
	decimal NetDebit,
	string Rationale,
	bool CashReserveBlocked = false,
	string? CashReserveDetail = null
);
