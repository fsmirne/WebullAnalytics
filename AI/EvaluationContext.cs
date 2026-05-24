namespace WebullAnalytics.AI;

/// <summary>
/// Snapshot of state passed to every rule on every tick.
/// Immutable; one instance per tick.
/// </summary>
/// <param name="Now">Logical clock for this evaluation. Live: DateTime.Now. Replay: the historical step.</param>
/// <param name="OpenPositions">All currently-open positions grouped by strategy (keyed by position key).</param>
/// <param name="UnderlyingPrices">Spot prices for each ticker under management.</param>
/// <param name="Quotes">Per-leg option quotes by OCC symbol.</param>
/// <param name="AccountCash">Free cash available (before applying reserve).</param>
/// <param name="AccountValue">Total account value (cash + positions marked to market).</param>
/// <param name="TechnicalSignals">Composite technical bias per ticker. Missing entry = neutral (no block).</param>
/// <param name="Vix">VIX index level at this tick (daily close for end-of-day eval, latest available for intraday).
/// Null when the source can't supply it. Used by regime-gated rules (e.g. LegInShortRule) to skip firing during
/// high-vol environments where the rule's tail-risk profile turns hostile.</param>
/// <param name="IntradaySpotRangePct">Today's running (high − low) / open as a percent of open, measured up to
/// <see cref="Now"/>. Null outside of intraday rule evaluation. Used as a "trend-day" proxy — large early-day
/// ranges correlate with continued large moves, which is exactly when capping the long is destructive.</param>
internal sealed record EvaluationContext(
	DateTime Now,
	IReadOnlyDictionary<string, OpenPosition> OpenPositions,
	IReadOnlyDictionary<string, decimal> UnderlyingPrices,
	IReadOnlyDictionary<string, OptionContractQuote> Quotes,
	decimal AccountCash,
	decimal AccountValue,
	IReadOnlyDictionary<string, TechnicalBias> TechnicalSignals,
	decimal? Vix = null,
	decimal? IntradaySpotRangePct = null
);

/// <summary>
/// A single open position under management. Carries enough state for rules to evaluate
/// without re-querying upstream sources.
/// </summary>
/// <param name="Key">Stable identifier; same value used in ManagementProposal.PositionKey.</param>
/// <param name="Ticker">Underlying root.</param>
/// <param name="StrategyKind">"Calendar" | "Diagonal" | "Single" | "Vertical" etc.</param>
/// <param name="Legs">Per-leg state.</param>
/// <param name="InitialNetDebit">The net debit (or credit) when the position was opened, per contract.</param>
/// <param name="AdjustedNetDebit">Break-even adjusted debit accounting for roll history.</param>
/// <param name="Quantity">Number of contracts.</param>
/// <param name="OpenedAt">Timestamp the position was first opened (null when the source can't determine it).
/// Used by OpportunisticRollRule's min-hold-days check; null disables that gate.</param>
/// <param name="MaxLossPerShare">Worst-case loss per share at expiry — wing width minus net credit for
/// credit structures, net debit for debit structures. Used by StopLossRule (and others) to fire at the
/// same threshold the opener's scorer assumed. Null when the source can't derive it (e.g., naked single
/// leg). Always non-negative when set.</param>
internal sealed record OpenPosition(
	string Key,
	string Ticker,
	string StrategyKind,
	IReadOnlyList<PositionLeg> Legs,
	decimal InitialNetDebit,
	decimal AdjustedNetDebit,
	int Quantity,
	DateTime? OpenedAt = null,
	decimal? MaxLossPerShare = null
);

/// <summary>
/// One leg of an open position.
/// </summary>
/// <param name="Symbol">OCC symbol for options; equity ticker for stock legs.</param>
/// <param name="Side">Long or short (represented as Side.Buy or Side.Sell matching the original trade).</param>
/// <param name="Strike">Strike price (0 for stock).</param>
/// <param name="Expiry">Expiration date (null for stock).</param>
/// <param name="CallPut">"C" / "P" for options; null for stock.</param>
/// <param name="Qty">Per-position leg quantity (contracts or shares).</param>
internal sealed record PositionLeg(
	string Symbol,
	Side Side,
	decimal Strike,
	DateTime? Expiry,
	string? CallPut,
	int Qty
);
