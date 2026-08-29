namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 1: close the position when realized loss reaches a configured fraction of the position's
/// max possible loss, or — independently, via <c>pctOfMaxProfit</c> — a fraction of its max possible
/// PROFIT (e.g. 1.0 gives back exactly what it could have made), or — independently again, via
/// <c>thetaExhaustShortMid</c> — when an underwater cross-expiry structure's short legs have decayed
/// to pennies (theta capture exhausted; see <see cref="EvaluateThetaExhaust"/>). Whichever trigger's
/// threshold is reached first fires; each is armed and priced independently.
///
/// The max-loss trigger mirrors the candidate scorer's terminal-PnL stop (<c>stopLossPctOfMaxLoss</c>
/// on <see cref="OpenerRealizedExpectancyConfig"/>) so realized exits track the EV the opener ranked
/// the trade against; the max-profit trigger (<c>stopLossPctOfMaxProfit</c>) is not yet mirrored in
/// the scorer's EV grid — it only affects realized exits, not open-time candidate ranking.
/// <c>MaxLossPerShare</c> / <c>MaxProfitPerShare</c> are read from the position (set at open time by
/// the source) or, when missing, derived on the fly from leg geometry via
/// <see cref="PositionRiskEstimator"/>.
///
/// Historical context: this rule used to gate on <c>currentDebit ≥ initialDebit × multiplier</c>,
/// which silently never fired on debit structures (calendars, diagonals) because their mark stays
/// positive and the "currentDebit" was clamped at 0. Aligning with the scorer's stop fixes both
/// the credit-vs-debit asymmetry and the scorer/runtime mismatch.
/// </summary>
internal sealed class StopLossRule : IManagementRule
{
	private readonly StopLossConfig _config;
	private readonly OpenerRealizedExpectancyConfig _realizedExpectancy;

	public StopLossRule(StopLossConfig config, OpenerRealizedExpectancyConfig realizedExpectancy)
	{
		_config = config;
		_realizedExpectancy = realizedExpectancy;
	}

	public string Name => "StopLossRule";
	public int Priority => 1;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		var thetaExhaustArmed = _config.ThetaExhaustShortMid > 0m;
		if (!_config.Enabled && !thetaExhaustArmed) return null;
		if (position.Legs.Count == 0) return null;

		var markPerShare = ComputeMarkPerShare(position, ctx);
		if (markPerShare == null) return null;

		// realizedLoss > 0 ⇔ position is underwater. Works for both credit (markPerShare more negative
		// than initialNetDebit) and debit (markPerShare less than initialNetDebit) structures.
		var realizedLoss = position.InitialNetDebit - markPerShare.Value;

		var maxLossPerShare = position.MaxLossPerShare
			?? PositionRiskEstimator.MaxLossPerShare(position);

		// slPct ≥ 1.0 disables the realized-loss trigger: the threshold equals the position's
		// theoretical max loss, which mirrors the scorer's terminal-PnL clamp at -1.0×maxLoss
		// (no effective stop). Closing at the max-loss floor produces the same economic outcome as
		// letting the position expire, while removing the optionality of intraday recovery.
		if (_config.Enabled && maxLossPerShare.HasValue && maxLossPerShare.Value > 0m && _realizedExpectancy.Enabled
			&& _realizedExpectancy.StopLossPctOfMaxLoss < 1m)
		{
			var threshold = maxLossPerShare.Value * _realizedExpectancy.StopLossPctOfMaxLoss;
			if (realizedLoss >= threshold)
			{
				return BuildClose(position, ctx, markPerShare.Value,
					$"realized loss ${realizedLoss:F2}/share ≥ {_realizedExpectancy.StopLossPctOfMaxLoss:P0} of max loss ${maxLossPerShare.Value:F2}");
			}
		}

		// Independent of the max-loss trigger above: close once realized loss reaches a fraction of
		// theoretical MAX PROFIT instead (e.g. 1.0 = give back exactly what the position could have
		// made). 0 (default) disables. Undefined for structures with no strike-width profit ceiling
		// (calendars/diagonals) — MaxProfitPerShare returns null there, so this silently never fires.
		var maxProfitPerShare = position.MaxProfitPerShare
			?? PositionRiskEstimator.MaxProfitPerShare(position);
		if (_config.Enabled && maxProfitPerShare.HasValue && maxProfitPerShare.Value > 0m && _realizedExpectancy.Enabled
			&& _realizedExpectancy.StopLossPctOfMaxProfit > 0m)
		{
			var threshold = maxProfitPerShare.Value * _realizedExpectancy.StopLossPctOfMaxProfit;
			if (realizedLoss >= threshold)
			{
				return BuildClose(position, ctx, markPerShare.Value,
					$"realized loss ${realizedLoss:F2}/share ≥ {_realizedExpectancy.StopLossPctOfMaxProfit:P0} of max profit ${maxProfitPerShare.Value:F2}");
			}
		}

		if (thetaExhaustArmed && realizedLoss > 0m)
		{
			var rationale = EvaluateThetaExhaust(position, ctx, realizedLoss);
			if (rationale != null) return BuildClose(position, ctx, markPerShare.Value, rationale);
		}

		return null;
	}

	/// <summary>Theta-exhaustion trigger: on an underwater cross-expiry structure, once every short leg
	/// expiring before the longest long has decayed to a mid at or below the configured floor, the
	/// position no longer earns theta — closing is recognizing the trade's thesis expired early, not a
	/// loss-percentage stop. Returns the rationale when the trigger fires, null otherwise. Expiry-day
	/// shorts are left to CloseBeforeShortExpiryRule (its defer-to-late-session behavior wins there).</summary>
	private string? EvaluateThetaExhaust(OpenPosition position, EvaluationContext ctx, decimal realizedLoss)
	{
		var longestLongExpiry = position.Legs
			.Where(l => l.Side == Side.Buy && l.CallPut != null && l.Expiry.HasValue)
			.Select(l => l.Expiry!.Value.Date)
			.DefaultIfEmpty(DateTime.MinValue)
			.Max();
		if (longestLongExpiry == DateTime.MinValue) return null;

		var earlyShorts = position.Legs
			.Where(l => l.Side == Side.Sell && l.CallPut != null && l.Expiry.HasValue && l.Expiry.Value.Date < longestLongExpiry)
			.ToList();
		if (earlyShorts.Count == 0) return null;
		if (earlyShorts.Min(l => l.Expiry!.Value.Date) <= ctx.Now.Date) return null;

		decimal worstMid = 0m;
		foreach (var leg in earlyShorts)
		{
			// ComputeMarkPerShare already required a two-sided quote on every leg, so the lookup can't miss.
			var q = ctx.Quotes[leg.Symbol];
			var mid = (q.Bid!.Value + q.Ask!.Value) / 2m;
			if (mid > _config.ThetaExhaustShortMid) return null;
			if (mid > worstMid) worstMid = mid;
		}

		var dte = (earlyShorts.Min(l => l.Expiry!.Value.Date) - ctx.Now.Date).Days;
		return $"theta exhausted: all {earlyShorts.Count} early short leg(s) at mid ≤ ${worstMid:F2} (floor ${_config.ThetaExhaustShortMid:F2}) with {dte}d to short expiry, underwater ${realizedLoss:F2}/share — no premium left to recover through";
	}

	private static ManagementProposal BuildClose(OpenPosition p, EvaluationContext ctx, decimal markPerShare, string rationale)
	{
		// Close proposes reversing every leg. Stamp each leg with per-share mid (default limit)
		// and the side-aware bid/ask edge (conservative limit) so the sink emits realistic prices.
		var legs = p.Legs.Select(l =>
		{
			var action = l.Side == Side.Buy ? "sell" : "buy";
			decimal? mid = null;
			decimal? edge = null;
			if (l.CallPut != null && ctx.Quotes.TryGetValue(l.Symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue)
			{
				mid = (q.Bid.Value + q.Ask.Value) / 2m;
				edge = action == "sell" ? q.Bid : q.Ask;
			}
			return new ProposalLeg(action, l.Symbol, l.Qty, mid, edge);
		}).ToList();

		return new ManagementProposal(
			Rule: "StopLossRule",
			Ticker: p.Ticker,
			PositionKey: p.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: markPerShare,
			Rationale: rationale
		);
	}

	/// <summary>Per-share mark (sum of leg mids signed by direction). Returns null if any leg is
	/// missing a quote — the rule defers rather than triggering on partial data.</summary>
	private static decimal? ComputeMarkPerShare(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (q.Bid == null || q.Ask == null) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}
}
