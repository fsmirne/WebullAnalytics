using WebullAnalytics.AI.RiskDiagnostics;

namespace WebullAnalytics.AI;

public enum OpenStructureKind
{
	LongCalendar,
	DoubleCalendar,
	LongDiagonal,
	DoubleDiagonal,
	IronButterfly,
	IronCondor,
	ShortPutVertical,
	ShortCallVertical,
	LongCall,
	LongPut
}

/// <summary>
/// Output of the opener pipeline. One proposal per candidate that survives scoring and ranking.
/// Peer to ManagementProposal — there is no PositionKey because no position exists.
/// </summary>
/// <param name="Ticker">Underlying symbol.</param>
/// <param name="StructureKind">Which structure family this proposal belongs to.</param>
/// <param name="Legs">Opening legs in OCC notation. Reuses ProposalLeg from the management side.</param>
/// <param name="Qty">Sized to available cash. 0 when CashReserveBlocked.</param>
/// <param name="DebitOrCreditPerContract">Negative = debit paid; positive = credit received (dollars per contract, i.e. ×100).</param>
/// <param name="MaxProfitPerContract">Positive dollars. For unlimited-profit structures (long call/put), taken as projected profit at +2σ grid point.</param>
/// <param name="MaxLossPerContract">Negative dollars (loss magnitude).</param>
/// <param name="CapitalAtRiskPerContract">Debit for longs/calendars; (width×100 − credit) for short verticals. Always ≥ 0.</param>
/// <param name="Breakevens">Underlying price levels where P&L crosses zero at the target date.</param>
/// <param name="ProbabilityOfProfit">[0, 1] from Black-Scholes with neutral drift.</param>
/// <param name="ExpectedValuePerContract">From the 5-point scenario grid, dollars.</param>
/// <param name="DaysToTarget">DTE of the leg whose expiry defines the target evaluation date.</param>
/// <param name="RawScore">EV / max(1, DaysToTarget) / CapitalAtRiskPerContract.</param>
/// <param name="BiasAdjustedScore">Legacy field name: the adjusted pre-theta score after the tech, balance, volatility, and max-pain multipliers. Displayed as "adjusted".</param>
/// <param name="DirectionalFit">+1 / 0 / −1 from the structure-fit table.</param>
/// <param name="Rationale">Human-readable line; see spec for format.</param>
/// <param name="Fingerprint">sha1-hex of (ticker | kind | sorted(legs) | qty) — used for cross-tick dedup.</param>
/// <param name="CashReserveBlocked">True when sizing fell to 0 contracts due to the cash reserve.</param>
/// <param name="CashReserveDetail">"free $X, requires $Y per contract" when blocked; null otherwise.</param>
/// <param name="PricingWarning">Warning surfaced when any leg had to use fallback Black-Scholes pricing because live bid/ask was unavailable.</param>
/// <param name="PremiumRatio">Σ(buy-leg ask × qty) / Σ(sell-leg bid × qty), or null for single-leg structures
/// where the ratio collapses to 1. Surfaced separately so the rationale can render it without recomputing.</param>
/// <param name="ImpliedVolatilityAnnual">Representative annualized IV used for ranking, as a fraction (0.40 = 40%).</param>
/// <param name="HistoricalVolatilityAnnual">Annualized realized volatility over the configured lookback, as a fraction.</param>
/// <param name="VolatilityAdjustmentFactor">IV-vs-HV multiplier applied during ranking; null when HV was unavailable.</param>
/// <param name="TargetExpiryMaxPain">Max-pain price inferred from open interest for the proposal's target expiry.</param>
/// <param name="MaxPainAdjustmentFactor">Max-pain multiplier applied during ranking; null when disabled or unavailable.</param>
/// <param name="GeometryFactor">Diagonal carry-quality multiplier applied during ranking when the front short fails to collect enough rent relative to the long premium/debit.</param>
/// <param name="RunwayFactor">Residual long-leg extrinsic/adjustment-runway multiplier applied during ranking when time remains after the target expiry.</param>
/// <param name="AssignmentRiskFactor">Short-option assignment/near-spot risk multiplier applied during ranking; null when no short-leg penalty applied.</param>
/// <param name="ThetaPerDayPerContract">Finite-difference net theta per day in dollars per contract. Used as a merit signal during opener ranking.</param>
/// <param name="MarketNetPremiumPerShare">Aggregate market mid premium per share, signed (long sum − short sum). Positive = net debit, negative = net credit. Null when any leg lacks a two-sided live quote.</param>
/// <param name="TheoreticalNetPremiumPerShare">Aggregate Black-Scholes theoretical premium per share, signed (long sum − short sum), priced at each leg's quoted IV. Same sign convention as the market value. Null when any leg lacks an IV or price.</param>
/// <param name="StatArbAdjustmentFactor">Stat-arb multiplier applied during ranking. Edge = theoreticalNet − marketNet; positive edge boosts (favors the entrant — paid less than fair on debit, received more than fair on credit). Null when prerequisite values are unavailable.</param>
/// <param name="FinalScore">Final opener ranking score. This is the score used for output ordering.</param>
internal sealed record OpenProposal(
	string Ticker,
	OpenStructureKind StructureKind,
	IReadOnlyList<ProposalLeg> Legs,
	int Qty,
	decimal DebitOrCreditPerContract,
	decimal MaxProfitPerContract,
	decimal MaxLossPerContract,
	decimal CapitalAtRiskPerContract,
	IReadOnlyList<decimal> Breakevens,
	decimal ProbabilityOfProfit,
	decimal ExpectedValuePerContract,
	int DaysToTarget,
	decimal RawScore,
	decimal BiasAdjustedScore,
	int DirectionalFit,
	string Rationale,
	string Fingerprint,
	bool CashReserveBlocked = false,
	string? CashReserveDetail = null,
	string? PricingWarning = null,
	RiskDiagnostic? Diagnostic = null,
	decimal? PremiumRatio = null,
	decimal? ImpliedVolatilityAnnual = null,
	decimal? HistoricalVolatilityAnnual = null,
	decimal? VolatilityAdjustmentFactor = null,
	decimal? TargetExpiryMaxPain = null,
	decimal? MaxPainAdjustmentFactor = null,
	decimal? SetupFactor = null,
	decimal? GeometryFactor = null,
	decimal? RunwayFactor = null,
	decimal? AssignmentRiskFactor = null,
	decimal? ThetaPerDayPerContract = null,
	decimal? MarketNetPremiumPerShare = null,
	decimal? TheoreticalNetPremiumPerShare = null,
	decimal? StatArbAdjustmentFactor = null,
	decimal? FinalScore = null
);
