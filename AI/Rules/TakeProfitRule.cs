namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: close the position on any day once mark-to-market profit reaches a fixed fraction of the
/// entry premium — debit paid or credit received (<see cref="TakeProfitConfig.ProfitTargetPctOfPremium"/>)
/// — the discretionary "grab +X% and recycle the capital" exit. For a credit structure the fraction of the
/// credit captured equals the fraction of max profit (the tastytrade convention). The former "% of max
/// projected profit" target (Target B) was removed: it
/// fired at the projector's thin per-column max (a near-zero absolute gain for low-max structures) and its
/// only defensible setting was 1.0 (disabled), so it earned nothing but confusion.
/// </summary>
internal sealed class TakeProfitRule : IManagementRule
{
	private readonly TakeProfitConfig _config;
	private readonly bool _debug;
	/// <summary>Same knob as the opener's <c>minEntryToNoiseRatio</c> (<see cref="OpenerConfig.MinEntryToNoiseRatio"/>),
	/// applied on the exit side via <see cref="CandidateScorer.PassesExitNoiseGate"/> — see that method's
	/// doc for why an entry-only gate wasn't enough. 0 (default) preserves prior behavior exactly.</summary>
	private readonly decimal _minEntryToNoiseRatio;

	public TakeProfitRule(TakeProfitConfig config, bool debug = false, decimal minEntryToNoiseRatio = 0m)
	{
		_config = config;
		_debug = debug;
		_minEntryToNoiseRatio = minEntryToNoiseRatio;
	}

	public string Name => "TakeProfitRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled || _config.ProfitTargetPctOfPremium <= 0m) return null;

		var currentMarkPerContract = ComputeMarkPerContract(position, ctx);
		if (currentMarkPerContract == null) return null;

		// Realized-if-closed = mark − entry cash. Works for both structure types: a debit position paid
		// AdjustedNetDebit (> 0) and marks positive; a credit position received it (< 0) and marks negative,
		// so mark − AdjustedNetDebit is the credit captured either way.
		var profitPerContract = currentMarkPerContract.Value - position.AdjustedNetDebit;
		var entryPremium = Math.Abs(position.AdjustedNetDebit);   // debit paid OR credit received
		if (profitPerContract <= 0m || entryPremium <= 0m) return null;

		// Fixed % of entry premium, fires ANY day: the discretionary "grab +X% and recycle the capital" exit.
		// For a credit structure this is the fraction of the credit captured (= fraction of max profit).
		var pctOfPremium = profitPerContract / entryPremium;
		if (pctOfPremium < _config.ProfitTargetPctOfPremium) return null;

		// Noise gate: don't honor a profit crossing that the legs' own quote noise fully explains. A
		// same-minute NBBO tick on one thin/far-dated leg can swing the mark past the target on a position
		// whose real economics haven't moved at all — see PassesExitNoiseGate's doc for the trade that
		// exposed this (a diagonal that "captured" 137% of its debit in one minute on a flat underlying).
		var symbols = position.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol);
		if (!CandidateScorer.PassesExitNoiseGate(symbols, ctx.Quotes, profitPerContract, _minEntryToNoiseRatio))
		{
			if (_debug)
				Console.Error.WriteLine($"[TakeProfitRule] {position.Key}: {pctOfPremium:P0} profit crossing did not clear the quote-noise floor (minRatio={_minEntryToNoiseRatio}) — treating as noise, not firing.");
			return null;
		}

		var rationale = $"captured {pctOfPremium * 100m:F0}% of net premium ${entryPremium:F2}/contract (target {_config.ProfitTargetPctOfPremium * 100m:F0}%)";

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

	private decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue;
			var hasQuote = ctx.Quotes.TryGetValue(leg.Symbol, out var q);
			if (!hasQuote || q!.Bid == null || q.Ask == null)
			{
				// A single un-priceable leg nulls the whole mark, which silently disables the exit for an
				// otherwise-profitable position. Surface it under --log-level debug only: in the per-minute
				// backtest this fires on every tick a leg lacks a two-sided quote (e.g. a thin far-dated leg
				// in quotes-only mode) and would flood stdout / corrupt the progress bar. The usual culprit
				// is a far-dated leg the chain omitted (see WebullOptionsClient queryBatch fallback).
				if (_debug)
					Console.Error.WriteLine($"[TakeProfitRule] {p.Key}: no two-sided quote for leg {leg.Symbol} (bid={q?.Bid?.ToString() ?? "-"} ask={q?.Ask?.ToString() ?? "-"}); take-profit not evaluated this tick.");
				return null;
			}
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}
}
