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
	// Single-sided 4-leg condor (all puts OR all calls): long wings + short body (long/debit condor) or
	// the reverse (short/credit condor). Same neutral, two-breakeven payoff family as an iron condor. The
	// opener does NOT enumerate or propose these — there is no Condor entry in OpenerConfig.Structures and
	// CandidateEnumerator never builds one — but Score()/ScoreMultiLeg handles a Condor skeleton so an
	// already-open condor can be scored (EM / breakevens / PoP / EV) in `analyze position`'s risk diagnostic.
	Condor,
	ShortPutVertical,
	ShortCallVertical,
	LongCall,
	LongPut,
	// Debit (long) verticals: long leg near/ATM, short leg further OTM. Directional bet with both
	// sides bounded — cheaper than naked long premium, capped upside but capped loss too. Fills the
	// gap between LongCall/LongPut (uncapped upside, full premium at risk) and ShortVertical
	// (credit collected, large max loss if breached).
	LongCallVertical,
	LongPutVertical,
	// Diagonal built from two defined-risk verticals on one side (all calls or all puts): a near-dated
	// SHORT vertical (credit) + a far-dated LONG vertical (debit). Approximates a calendar/diagonal's
	// theta+vega profile but every leg is bounded, so nothing is ever naked — the tradeable form on
	// venues that reject true calendars/diagonals (e.g. Webull SPXW/XSP). Multi-expiry: the near short
	// vertical expires first (partial settle), the far long vertical is closed same-day.
	DiagonalVertical,
	// Calendar built from two defined-risk verticals on one side: identical to DiagonalVertical EXCEPT both
	// verticals share the SAME anchor strike across the two expiries (a calendar, not a diagonal). Net = a
	// long calendar at the anchor capped by a short calendar at the wing — long theta+vega, every leg
	// bounded, places as two Webull-valid verticals. Distinguished from DiagonalVertical by geometry: 2
	// distinct strikes (calendar) vs 3-4 (diagonal), the same way DoubleCalendar splits from DoubleDiagonal.
	CalendarVertical
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
/// <param name="TargetExpiryMaxPain">Max-pain price inferred from open interest for the proposal's target expiry (display only; the directional max-pain pull lives in the maxPainBiasPull grid magnet).</param>
/// <param name="GexGravity">Strike with the highest gross gamma×OI (calls + puts) at the target expiry — the gravity / pin point where dealer hedging is most concentrated, matching the convention used by Barchart and most public GEX tools. Drives the gexBiasPull grid magnet.</param>
/// <param name="NetGexFraction">Net dealer gamma exposure normalized to [−1, +1]: positive = call gamma dominates (dealers net long gamma, suppressive regime); negative = put gamma dominates (amplifying regime).</param>
/// <param name="GammaRegimeFactor">Net dealer-gamma REGIME multiplier (the volatility tilt: NetGexFraction × structure vol-fit sign) applied during ranking; null when the gammaRegime weight is 0.</param>
/// <param name="GexBiasPullSigmas">The gexBiasPull component of the scenario-grid magnet shift, in sigma units (signed: negative pulls the EV distribution below spot toward gravity). Display only — surfaced for the informational GEX line; null when the gexBiasPull weight is 0 or gravity was unavailable ("off").</param>
/// <param name="RunwayFactor">Residual long-leg extrinsic/adjustment-runway multiplier applied during ranking when time remains after the target expiry.</param>
/// <param name="AssignmentRiskFactor">Short-option assignment/near-spot risk multiplier applied during ranking; null when no short-leg penalty applied.</param>
/// <param name="ThetaPerDayPerContract">Finite-difference net theta per day in dollars per contract. Used as a merit signal during opener ranking.</param>
/// <param name="NetVegaPerContract">Closed-form Black-Scholes net vega in dollars per contract per 1 percentage-point of IV change. Long legs add, short legs subtract. Drives the vega-aware vol factor — long-vega positions get boosted when IV is cheap vs HV and cut when IV is rich; short-vega positions are mirror-image.</param>
/// <param name="MarketNetPremiumPerShare">Aggregate market mid premium per share, signed (long sum − short sum). Positive = net debit, negative = net credit. Null when any leg lacks a two-sided live quote.</param>
/// <param name="TheoreticalNetPremiumPerShare">Aggregate Black-Scholes theoretical premium per share, signed (long sum − short sum), priced at each leg's quoted IV. Same sign convention as the market value. Null when any leg lacks an IV or price.</param>
/// <param name="StatArbAdjustmentFactor">Stat-arb multiplier applied during ranking. Edge = theoreticalNet − marketNet; positive edge boosts (favors the entrant — paid less than fair on debit, received more than fair on credit). Null when prerequisite values are unavailable.</param>
/// <param name="FinalScore">Final opener ranking score. This is the score used for output ordering.</param>
/// <param name="ExpectedMoveCreditFactor">EM-vs-short-strike cushion factor for credit trades only. Measures the spot-to-nearest-short distance in one-sigma EM units (spot × IV × √(trading-days/252)); <1σ is unsafe, >1.5σ is safe. Null for debit trades, structures without shorts, or degenerate inputs.</param>
/// <param name="IvRealizedPremiumFactor">"Trade vs vol regime" factor based on IV/HV richness, distinct from the vega-aware adjustment. Credit favored when IV > HV; debit favored when IV < HV. Null when HV unavailable or weight = 0.</param>
/// <param name="ExpectedMoveLower">Lower bound of the one-sigma expected-move price envelope at the target expiry, <c>spot − spot × IV × √(trading-days/252)</c>. Surfaced for display next to <see cref="Breakevens"/>. Null when IV or DTE are unavailable.</param>
/// <param name="ExpectedMoveUpper">Upper bound of the one-sigma expected-move price envelope at the target expiry, <c>spot + spot × IV × √(trading-days/252)</c>. Surfaced for display next to <see cref="Breakevens"/>. Null when IV or DTE are unavailable.</param>
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
	decimal? GexGravity = null,
	decimal? NetGexFraction = null,
	decimal? GammaRegimeFactor = null,
	decimal? GexBiasPullSigmas = null,
	decimal? SetupFactor = null,
	decimal? RunwayFactor = null,
	decimal? AssignmentRiskFactor = null,
	decimal? ThetaPerDayPerContract = null,
	decimal? NetVegaPerContract = null,
	decimal? MarketNetPremiumPerShare = null,
	decimal? TheoreticalNetPremiumPerShare = null,
	decimal? StatArbAdjustmentFactor = null,
	decimal? FinalScore = null,
	decimal? WorstLegBidAskSpreadPct = null,
	long? MinOpenInterest = null,
	decimal? MinRelativeOpenInterest = null,
	decimal? LiquidityAdjustmentFactor = null,
	decimal? MarketSentimentScore = null,
	string? MarketSentimentRating = null,
	decimal? SentimentAdjustmentFactor = null,
	decimal? RealizedExpectedValuePerContract = null,
	decimal? EstimatedSlippagePerContract = null,
	decimal? ProfitTargetPerContract = null,
	decimal? StopLossPerContract = null,
	decimal? BreakevenRoomFactor = null,
	decimal? ExpectedMoveCreditFactor = null,
	decimal? IvRealizedPremiumFactor = null,
	decimal? ExpectedMoveLower = null,
	decimal? ExpectedMoveUpper = null,
	// True when this proposal is surfaced only for visibility — the best candidate of an enabled
	// structure that didn't clear the global top-N / MinScoreToOpen bar. Shown in the scan output so the
	// user can see what each enabled structure would propose, but NEVER auto-executed (the auto-executor
	// filters these out). Used to surface DoubleCalendar/DoubleDiagonal, which are genuinely less
	// capital-efficient than single calendars/diagonals and so rank below them.
	bool Informational = false,
	// Underlying spot at the minute this proposal was scored — carried onto the backtest fill ledger so
	// strikes can be compared against spot at entry. 0 when unset (non-backtest callers don't populate it).
	decimal Spot = 0m
);
