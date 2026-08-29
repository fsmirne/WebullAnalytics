namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 1: force-close a position once elapsed calendar days since open reach a configured
/// fraction of its original DTE (days from open to its furthest leg's expiry), regardless of P&L.
/// Independent of StopLossRule/TakeProfitRule — this is a time-budget exit, not a P&L-driven one: the
/// thesis is that most of a credit structure's edge decays away in the first half of its life, so
/// there's no reason to keep capital tied up (and tail risk on) for the second half chasing the last
/// scraps of theta.
///
/// Needs <see cref="OpenPosition.OpenedAt"/> to be set to fire; positions the source can't date (e.g.
/// a live position opened before this run started tracking it — see LivePositionSource's same-day-only
/// enrichment) are skipped rather than guessed at.
/// </summary>
internal sealed class TimeStopRule : IManagementRule
{
	private readonly TimeStopConfig _config;

	public TimeStopRule(TimeStopConfig config)
	{
		_config = config;
	}

	public string Name => "TimeStopRule";
	public int Priority => 1;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled || _config.LifeFractionElapsed <= 0m) return null;
		if (position.OpenedAt is not { } openedAt) return null;
		if (position.Legs.Count == 0) return null;

		var furthestExpiry = position.Legs
			.Where(l => l.Expiry.HasValue)
			.Select(l => l.Expiry!.Value.Date)
			.DefaultIfEmpty(DateTime.MinValue)
			.Max();
		if (furthestExpiry == DateTime.MinValue) return null;

		var originalDte = (furthestExpiry - openedAt.Date).Days;
		if (originalDte <= 0) return null;

		var elapsedDays = (ctx.Now.Date - openedAt.Date).Days;
		var triggerDay = originalDte * _config.LifeFractionElapsed;
		if (elapsedDays < triggerDay) return null;

		var markPerShare = ComputeMarkPerShare(position, ctx);
		if (markPerShare == null) return null;

		var rationale = $"time stop: {elapsedDays}d elapsed ≥ {triggerDay:F1}d ({_config.LifeFractionElapsed:P0} of {originalDte}d original DTE), regardless of P&L";
		return BuildClose(position, ctx, markPerShare.Value, rationale);
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
			Rule: "TimeStopRule",
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
