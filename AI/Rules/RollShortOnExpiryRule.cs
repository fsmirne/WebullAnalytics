namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 4: routine maintenance — when the short leg is near expiry and its premium has decayed
/// below a threshold, propose rolling it to the next weekly at the same strike for a net credit.
/// Suppressed if a higher-priority rule already matched.
/// </summary>
internal sealed class RollShortOnExpiryRule : IManagementRule
{
	private readonly RollShortOnExpiryConfig _config;

	public RollShortOnExpiryRule(RollShortOnExpiryConfig config) { _config = config; }

	public string Name => "RollShortOnExpiryRule";
	public int Priority => 4;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null || !shortLeg.Expiry.HasValue) return null;

		var dte = (shortLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte > _config.TriggerDTE) return null;

		if (!ctx.Quotes.TryGetValue(shortLeg.Symbol, out var oldQ)) return null;
		if (oldQ.Bid == null || oldQ.Ask == null) return null;
		var oldMid = (oldQ.Bid.Value + oldQ.Ask.Value) / 2m;
		if (oldMid > _config.MaxShortPremium) return null;

		// Same strike, next weekly.
		var newExpiry = NextWeekly(shortLeg.Expiry.Value);
		var newSymbol = MatchKeys.OccSymbol(position.Ticker, newExpiry, shortLeg.Strike, shortLeg.CallPut!);

		if (!ctx.Quotes.TryGetValue(newSymbol, out var newQ) || newQ.Bid == null || newQ.Ask == null) return null;
		var newMid = (newQ.Bid.Value + newQ.Ask.Value) / 2m;

		// netCredit = new bid - old ask (realistic fill for seller rolling to next week).
		var netCredit = newQ.Bid.Value - oldQ.Ask.Value;
		if (netCredit < _config.MinRollCredit) return null;

		var legs = new[]
		{
			new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty, oldMid, oldQ.Ask),
			new ProposalLeg("sell", newSymbol, shortLeg.Qty, newMid, newQ.Bid)
		};

		return new ManagementProposal(
			Rule: "RollShortOnExpiryRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Roll,
			Legs: legs,
			NetDebit: -netCredit,
			Rationale: $"short mid ${oldMid:F2} ≤ threshold ${_config.MaxShortPremium:F2}, DTE {dte}, roll credit ${netCredit:F2}"
		);
	}

	private static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}
}
