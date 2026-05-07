namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 1: close the position when mark-to-market debit grows past a multiplier of initial debit,
/// or when spot moves beyond break-even by more than a configured percentage.
/// </summary>
internal sealed class StopLossRule : IManagementRule
{
	private readonly StopLossConfig _config;

	public StopLossRule(StopLossConfig config) { _config = config; }

	public string Name => "StopLossRule";
	public int Priority => 1;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Legs.Count == 0) return null;

		// 1) Compute current mark-to-market net debit from quotes.
		var currentMarkPerContract = ComputeMarkPerContract(position, ctx);
		if (currentMarkPerContract == null) return null;

		var initialDebit = Math.Abs(position.InitialNetDebit);
		if (initialDebit <= 0m) return null; // credit-received positions aren't a "debit stop" candidate
		var currentDebit = Math.Max(0m, -currentMarkPerContract.Value); // positive when underwater

		// 2) Trigger on debit multiplier.
		if (currentDebit >= initialDebit * _config.MaxDebitMultiplier)
		{
			return BuildClose(position, currentMarkPerContract.Value,
				$"mark debit ${currentDebit:F2}/contract ≥ {_config.MaxDebitMultiplier}× initial ${initialDebit:F2}");
		}

		// 3) Trigger on spot beyond break-even.
		if (ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot))
		{
			var (beLow, beHigh, beSource) = PositionBreakEvenEstimator.Estimate(position, ctx);
			var pctBand = _config.SpotBeyondBreakevenPct / 100m;
			var sourceTag = beSource == PositionBreakEvenEstimator.BreakEvenSource.Heuristic ? " (heuristic)" : "";
			if (beLow.HasValue && spot < beLow.Value * (1m - pctBand))
				return BuildClose(position, currentMarkPerContract.Value,
					$"spot ${spot:F2} < lower break-even ${beLow.Value:F2}{sourceTag} by > {_config.SpotBeyondBreakevenPct}%");
			if (beHigh.HasValue && spot > beHigh.Value * (1m + pctBand))
				return BuildClose(position, currentMarkPerContract.Value,
					$"spot ${spot:F2} > upper break-even ${beHigh.Value:F2}{sourceTag} by > {_config.SpotBeyondBreakevenPct}%");
		}

		return null;
	}

	private static ManagementProposal BuildClose(OpenPosition p, decimal markPerContract, string rationale)
	{
		// Close proposes reversing every leg.
		var legs = p.Legs.Select(l => new ProposalLeg(
			Action: l.Side == Side.Buy ? "sell" : "buy",
			Symbol: l.Symbol,
			Qty: l.Qty
		)).ToList();

		return new ManagementProposal(
			Rule: "StopLossRule",
			Ticker: p.Ticker,
			PositionKey: p.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: markPerContract * p.Quantity,
			Rationale: rationale
		);
	}

	/// <summary>Computes the per-contract mark value (sum of leg midpoint values signed by direction).
	/// Returns null if any leg is missing a quote.</summary>
	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue; // skip stock legs here; they don't alter the option-mark
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (q.Bid == null || q.Ask == null) return null;
			var mid = ((q.Bid.Value + q.Ask.Value) / 2m);
			// Long leg contributes +mid; short leg contributes -mid (you'd pay to close it).
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

}
