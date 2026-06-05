namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: close the position when mark-to-market has captured a configured percentage of
/// max projected profit (estimated via ProfitProjector across the full remaining lifetime grid).
/// Threshold is read from <see cref="OpenerRealizedExpectancyConfig.ProfitTargetPctOfMaxProfit"/>
/// (decimal 0–1, default 0.50) so the rule fires at the same exit point the candidate scorer's
/// EV calc clamps grid scenarios at. Without this alignment the scorer ranks against a managed-exit
/// policy the runtime doesn't actually implement — the prior split (rule at 60% maxProfit, scorer
/// clamp at 50% maxProfit) made backtest P&L systematically diverge from scorer EV.
/// </summary>
internal sealed class TakeProfitRule : IManagementRule
{
	private readonly TakeProfitConfig _config;
	private readonly OpenerRealizedExpectancyConfig _realizedExpectancy;

	public TakeProfitRule(TakeProfitConfig config, OpenerRealizedExpectancyConfig realizedExpectancy)
	{
		_config = config;
		_realizedExpectancy = realizedExpectancy;
	}

	public string Name => "TakeProfitRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var currentMarkPerContract = ComputeMarkPerContract(position, ctx);
		if (currentMarkPerContract == null) return null;

		// Current realized-if-closed = mark - initial debit; positive means profit-per-contract.
		var profitPerContract = currentMarkPerContract.Value - position.AdjustedNetDebit;
		if (profitPerContract <= 0m) return null;

		string? rationale = null;

		// Target A — fixed % of net debit, fires ANY day. The discretionary "grab +X% and recycle the
		// capital" exit; triggers far earlier than the % -of-max-projected target, so check it first.
		// Independent of realizedExpectancy (it's a flat return threshold, not a grid-relative one).
		if (_config.ProfitTargetPctOfDebit > 0m && position.AdjustedNetDebit > 0m)
		{
			var pctOfDebit = profitPerContract / position.AdjustedNetDebit * 100m;
			if (pctOfDebit >= _config.ProfitTargetPctOfDebit)
				rationale = $"captured {pctOfDebit:F0}% of net debit ${position.AdjustedNetDebit:F2}/contract (target {_config.ProfitTargetPctOfDebit:F0}%)";
		}

		// Target B — % of max projected profit (aligns with the scorer's EV clamp). Needs the realized-
		// expectancy model for the threshold and the grid for the projection.
		if (rationale == null && _realizedExpectancy.Enabled)
		{
			// Max projected profit from grid: use the peak net value in the current-date column.
			var maxProjected = GetMaxProjectedProfitPerContract(position, ctx);
			if (maxProjected != null && maxProjected.Value > 0m)
			{
				var pctCapturedFraction = profitPerContract / maxProjected.Value;
				if (pctCapturedFraction >= _realizedExpectancy.ProfitTargetPctOfMaxProfit)
					rationale = $"captured {pctCapturedFraction * 100m:F0}% of max projected profit ${maxProjected.Value:F2}/contract (threshold {_realizedExpectancy.ProfitTargetPctOfMaxProfit:P0})";
			}
		}

		if (rationale == null) return null;

		// Stamp each leg with per-share mid (default limit) and the side-aware bid/ask edge so the
		// sink emits a realistic limit; otherwise the fallback path mis-scales NetDebit by quantity.
		var legs = position.Legs.Select(l =>
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
			Rule: "TakeProfitRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: currentMarkPerContract.Value,
			Rationale: rationale
		);
	}

	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue;
			var hasQuote = ctx.Quotes.TryGetValue(leg.Symbol, out var q);
			if (!hasQuote || q!.Bid == null || q.Ask == null)
			{
				// A single un-priceable leg nulls the whole mark, which silently disables the exit for an
				// otherwise-profitable position. Surface it: the usual culprit is a far-dated leg the chain
				// omitted (see WebullOptionsClient queryBatch fallback). Never let this fail without a trace.
				Console.Error.WriteLine($"[TakeProfitRule] {p.Key}: no two-sided quote for leg {leg.Symbol} (bid={q?.Bid?.ToString() ?? "-"} ask={q?.Ask?.ToString() ?? "-"}); take-profit not evaluated this tick.");
				return null;
			}
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	private static decimal? GetMaxProjectedProfitPerContract(OpenPosition p, EvaluationContext ctx) =>
		ProfitProjector.MaxForCurrentColumn(p, ctx);
}
