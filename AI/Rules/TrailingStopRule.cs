using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 1: closes a position once it has given back <see cref="TrailingStopConfig.TrailPct"/> of what it still had at risk at its
/// best level since entry. The position is valued at what closing it would fetch right now — long legs at the bid, short legs at the
/// ask — and the peak is the highest such value seen, starting at the first one. It does not start at the entry price: a fill is
/// near the mid, so measuring closing values against it would put a wide-spread structure partway to its stop (or past it) on the
/// spread alone, before the market moved. What is at risk at the peak is the distance from
/// there down to the worst the structure can be worth (entry value minus its max loss): for a long option or any debit structure that
/// floor is $0, so the stop is "trailPct below the peak"; for a credit spread it is minus the wing width, so the stop is trailPct of
/// the remaining width, not of the credit (a fraction of the credit is inside the quote noise of the spread). A structure with no
/// defined max loss is not trailed.
///
/// Valuing at the closing side, not the mid, keeps a one-sided quote spike out of the peak: a momentarily wide ask lifts a long
/// leg's mid but not its bid. A crossed leg (bid above ask) is a torn quote and the position is not evaluated that tick.
///
/// Webull's API has no trailing stop for options, so the watch loop carries it: the peak is sampled once per tick (a spike between
/// ticks is not seen) and lives in <see cref="TrailingStopStore"/>. The close is flagged <see cref="ManagementProposal.Marketable"/>
/// (sell at the bid, buy at the ask, re-priced every tick until it fills) and <c>[emergency]</c> (the whole position at once, outside
/// the scale-out schedule): a stop resting at a price the market has already left is not a stop.
///
/// When enabled it trails every open position except the structures named in <see cref="TrailingStopConfig.Exclude"/>. The peak is
/// keyed by the position and its entry price, so a position re-opened on the same contract(s) at another price starts a fresh peak.
/// </summary>
internal sealed class TrailingStopRule : IManagementRule
{
	private readonly TrailingStopConfig _config;
	private readonly TrailingStopStore _store;

	public TrailingStopRule(TrailingStopConfig config, TrailingStopStore store)
	{
		_config = config;
		_store = store;
	}

	public string Name => "TrailingStopRule";
	public int Priority => 1;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled || _config.TrailPct <= 0m || _config.TrailPct >= 1m) return null;
		if (position.Legs.Count == 0 || position.InitialNetDebit == 0m) return null;
		if (_config.Exclude.Contains(position.StrategyKind, StringComparer.OrdinalIgnoreCase)) return null;
		if ((position.MaxLossPerShare ?? PositionRiskEstimator.MaxLossPerShare(position)) is not decimal maxLoss) return null;

		var value = 0m;
		var closeLegs = new List<ProposalLeg>(position.Legs.Count);
		foreach (var leg in position.Legs)
		{
			if (leg.CallPut == null) return null;
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q) || q.Bid is not decimal bid || q.Ask is not decimal ask || bid > ask) return null;
			var isLong = leg.Side == Side.Buy;
			value += isLong ? bid : -ask;
			closeLegs.Add(new ProposalLeg(isLong ? "sell" : "buy", leg.Symbol, leg.Qty, (bid + ask) / 2m, isLong ? bid : ask));
		}

		var key = $"{position.Key}|{position.InitialNetDebit:0.####}";
		var peak = _store.Raise(key, value, ctx.Now);
		var atRisk = peak - (position.InitialNetDebit - maxLoss);
		if (atRisk <= 0m) return null;
		var givenBack = (peak - value) / atRisk;
		if (givenBack < _config.TrailPct) return null;

		return new ManagementProposal(
			Rule: Name,
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Close,
			Legs: closeLegs,
			NetDebit: value,
			Rationale: $"[emergency] closing value ${value:F2} gave back {givenBack * 100m:F0}% of the ${atRisk:F2} at risk from its peak ${peak:F2} (trail {_config.TrailPct * 100m:F0}%, entry ${position.InitialNetDebit:F2})",
			Marketable: true);
	}
}
