namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 1: close the position when realized loss reaches a configured fraction of the position's
/// max possible loss, or when spot moves beyond break-even by more than a configured percentage.
///
/// The P&amp;L trigger mirrors the candidate scorer's terminal-PnL stop (<c>stopLossPctOfMaxLoss</c> on
/// <see cref="OpenerRealizedExpectancyConfig"/>) so realized exits track the EV the opener ranked
/// the trade against. <c>MaxLossPerShare</c> is read from the position (set at open time by the
/// source) or, when missing, derived on the fly from leg geometry via
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
		if (!_config.Enabled) return null;
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
		if (maxLossPerShare.HasValue && maxLossPerShare.Value > 0m && _realizedExpectancy.Enabled
			&& _realizedExpectancy.StopLossPctOfMaxLoss < 1m)
		{
			var threshold = maxLossPerShare.Value * _realizedExpectancy.StopLossPctOfMaxLoss;
			if (realizedLoss >= threshold)
			{
				return BuildClose(position, ctx, markPerShare.Value,
					$"realized loss ${realizedLoss:F2}/share ≥ {_realizedExpectancy.StopLossPctOfMaxLoss:P0} of max loss ${maxLossPerShare.Value:F2}");
			}
		}

		// Spot-beyond-BE: separate path-risk gate. The scorer evaluates terminal P&L only and won't
		// catch positions whose spot has run past break-even but whose mark hasn't yet caught up.
		//
		// CRITICAL: this trigger must only fire when the position is actually underwater. The
		// PositionBreakEvenEstimator heuristic returns a SYMMETRIC band centered on the short
		// strike (shortStrike ± debit), which is the right shape for credit spreads (profitable
		// inside the band, losing outside) but is WRONG for debit spreads — a LongPutVertical or
		// LongCallVertical is most profitable when spot moves PAST the heuristic band in the
		// favorable direction. Without the realizedLoss > 0 gate, this rule fired StopLoss closes
		// on deep-ITM winners (e.g. a LongPutVertical opened for $24k debit closed at +$112k credit
		// during an SPX crash). Gating on realized loss ensures spot-based SL only fires when the
		// position has actually lost money — TakeProfitRule handles the profitable-spot-move case.
		if (realizedLoss > 0m && ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot))
		{
			var (beLow, beHigh, beSource) = PositionBreakEvenEstimator.Estimate(position, ctx);
			var pctBand = _config.SpotBeyondBreakevenPct / 100m;
			var sourceTag = beSource == PositionBreakEvenEstimator.BreakEvenSource.Heuristic ? " (heuristic)" : "";
			if (beLow.HasValue && spot < beLow.Value * (1m - pctBand))
				return BuildClose(position, ctx, markPerShare.Value,
					$"spot ${spot:F2} < lower break-even ${beLow.Value:F2}{sourceTag} by > {_config.SpotBeyondBreakevenPct}%");
			if (beHigh.HasValue && spot > beHigh.Value * (1m + pctBand))
				return BuildClose(position, ctx, markPerShare.Value,
					$"spot ${spot:F2} > upper break-even ${beHigh.Value:F2}{sourceTag} by > {_config.SpotBeyondBreakevenPct}%");
		}

		return null;
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
