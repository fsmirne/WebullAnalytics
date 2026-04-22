namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: close the position when mark-to-market has captured a configured percentage of
/// max projected profit (estimated via ProfitProjector across the full remaining lifetime grid).
/// </summary>
internal sealed class TakeProfitRule : IManagementRule
{
	private readonly TakeProfitConfig _config;

	public TakeProfitRule(TakeProfitConfig config) { _config = config; }

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

		// Max projected profit from grid: use the peak net value in the current-date column.
		var maxProjected = GetMaxProjectedProfitPerContract(position, ctx);
		if (maxProjected == null || maxProjected.Value <= 0m) return null;

		var pctCaptured = (profitPerContract / maxProjected.Value) * 100m;
		if (pctCaptured < _config.PctOfMaxProfit) return null;

		var legs = position.Legs.Select(l => new ProposalLeg(
			Action: l.Side == Side.Buy ? "sell" : "buy",
			Symbol: l.Symbol,
			Qty: l.Qty
		)).ToList();

		return new ManagementProposal(
			Rule: "TakeProfitRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: currentMarkPerContract.Value * position.Quantity,
			Rationale: $"captured {pctCaptured:F0}% of max projected profit ${maxProjected.Value:F2}/contract (threshold {_config.PctOfMaxProfit}%)"
		);
	}

	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
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

	private static decimal? GetMaxProjectedProfitPerContract(OpenPosition p, EvaluationContext ctx) =>
		ProfitProjector.MaxForCurrentColumn(p, ctx);
}
