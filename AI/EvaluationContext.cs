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
/// <param name="HistoricalVolByTicker">Underlying 20-session annualized realized vol per ticker — the same
/// vendor-independent metric the opener and `wa analyze` compute. Feeds the risk-diagnostic HV display so the
/// watch panel never shows the vendor's per-contract hiv (Webull-only; Schwab reports none). Null/missing entry
/// leaves the HV line blank.</param>
internal sealed record EvaluationContext(
	DateTime Now,
	IReadOnlyDictionary<string, OpenPosition> OpenPositions,
	IReadOnlyDictionary<string, decimal> UnderlyingPrices,
	IReadOnlyDictionary<string, OptionContractQuote> Quotes,
	decimal AccountCash,
	decimal AccountValue,
	IReadOnlyDictionary<string, TechnicalBias> TechnicalSignals,
	decimal? Vix = null,
	decimal? IntradaySpotRangePct = null,
	IReadOnlyDictionary<string, decimal>? HistoricalVolByTicker = null
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
/// <param name="MaxProfitPerShare">Best-case profit per share at expiry — the credit collected for
/// credit structures, wing width minus net debit for a defined-risk debit spread. Used by StopLossRule's
/// max-profit-based stop. Null when the source can't derive it (calendars/diagonals have no strike-width
/// profit ceiling; naked single legs have none either). Always non-negative when set.</param>
/// <param name="PositionId">Broker-assigned position identifier (Webull holdings position_id). Only set
/// by LivePositionSource; null for replay/backtest sources. Lets `wa trade close` target one position.</param>
internal sealed record OpenPosition(
	string Key,
	string Ticker,
	string StrategyKind,
	IReadOnlyList<PositionLeg> Legs,
	decimal InitialNetDebit,
	decimal AdjustedNetDebit,
	int Quantity,
	DateTime? OpenedAt = null,
	decimal? MaxLossPerShare = null,
	decimal? MaxProfitPerShare = null,
	string? PositionId = null
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

/// <summary>Shared by the live opener (<see cref="OpenerAutoExecutor"/>) and the backtest book
/// (<see cref="Backtest.SimulatedBook"/>) so both refuse the exact same trade — a new candidate whose legs
/// oppose a DIFFERENT already-open position's legs on the same symbol.</summary>
internal static class HeldLegGuard
{
	/// <summary>True when <paramref name="candidateLegs"/> holds the OPPOSITE side of a symbol a held position
	/// (not a clean re-take of the SAME structure) already holds. A real account can never independently carry
	/// a short lot from one strategy and a long lot from another on the same option symbol — the broker/clearer
	/// nets them, which would break the held position out from under it. Adding to the SAME side (selling more
	/// of a symbol already sold elsewhere, or buying more of one already bought) is fine — it's exactly how a
	/// real account accumulates size on a symbol, tracked here as independent lineages that each demand their
	/// own margin, which sums to the same total a single combined lot would. A candidate whose leg-symbol set
	/// exactly matches an already-held position's is NOT a collision either way — that's a legitimate add/re-take
	/// of the SAME structure, governed by the caller's own held-position policy (e.g. <c>allowAddToHeldPosition</c>),
	/// not this guard.</summary>
	public static bool CollidesWithHeldLeg(IEnumerable<(string Symbol, Side Side)> candidateLegs, IEnumerable<OpenPosition> heldPositions)
	{
		var candidate = candidateLegs as IReadOnlyCollection<(string Symbol, Side Side)> ?? candidateLegs.ToList();
		var candidateSymbols = new HashSet<string>(candidate.Select(l => l.Symbol), StringComparer.OrdinalIgnoreCase);
		foreach (var pos in heldPositions)
		{
			var heldSymbols = new HashSet<string>(pos.Legs.Select(l => l.Symbol), StringComparer.OrdinalIgnoreCase);
			if (heldSymbols.SetEquals(candidateSymbols)) continue;   // same structure re-taken — not a collision
			foreach (var held in pos.Legs)
				foreach (var cand in candidate)
					if (cand.Side != held.Side && string.Equals(cand.Symbol, held.Symbol, StringComparison.OrdinalIgnoreCase))
						return true;
		}
		return false;
	}
}
