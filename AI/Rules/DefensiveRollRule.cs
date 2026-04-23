namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 3: when spot is within a configured percentage of the short strike and short DTE is near,
/// propose rolling up-and-out (for short calls: higher strike, further expiry; puts mirror).
/// Constraint: proposed roll must produce a credit, reduce max-loss, or reduce delta. Otherwise
/// the proposal is emitted as AlertOnly with rationale "no-better-alternative".
/// </summary>
internal sealed class DefensiveRollRule : IManagementRule
{
	private readonly DefensiveRollConfig _config;

	public DefensiveRollRule(DefensiveRollConfig config) { _config = config; }

	public string Name => "DefensiveRollRule";
	public int Priority => 3;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null || !shortLeg.Expiry.HasValue) return null;

		var dte = (shortLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte > _config.TriggerDTE) return null;

		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot)) return null;
		var pctBand = _config.SpotWithinPctOfShortStrike / 100m;
		var nearStrike = Math.Abs(spot - shortLeg.Strike) <= shortLeg.Strike * pctBand;
		if (!nearStrike) return null;

		// Propose roll: step strike away from spot by StrikeStep; step expiry to next weekly (+7 calendar days).
		var newStrike = shortLeg.CallPut == "C"
			? shortLeg.Strike + _config.StrikeStep
			: shortLeg.Strike - _config.StrikeStep;
		var newExpiry = NextWeekly(shortLeg.Expiry.Value);
		var newSymbol = MatchKeys.OccSymbol(position.Ticker, newExpiry, newStrike, shortLeg.CallPut!);

		// Look up quotes to estimate the net. If missing, we still emit as AlertOnly (legs get no prices).
		if (!ctx.Quotes.TryGetValue(shortLeg.Symbol, out var oldQ) || !ctx.Quotes.TryGetValue(newSymbol, out var newQ) ||
		    oldQ.Ask == null || newQ.Bid == null)
		{
			var alertLegs = new[]
			{
				new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty),
				new ProposalLeg("sell", newSymbol, shortLeg.Qty)
			};
			return new ManagementProposal(
				Rule: "DefensiveRollRule",
				Ticker: position.Ticker,
				PositionKey: position.Key,
				Kind: ProposalKind.AlertOnly,
				Legs: alertLegs,
				NetDebit: 0m,
				Rationale: $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}. Quote unavailable for new symbol {newSymbol}."
			);
		}

		var legs = new[]
		{
			new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty, oldQ.Ask),   // close the old short at ask
			new ProposalLeg("sell", newSymbol, shortLeg.Qty, newQ.Bid)          // open the new short at bid
		};

		// netCredit = newBid - oldAsk (we sell the new short, buy to close the old).
		var netCredit = newQ.Bid.Value - oldQ.Ask.Value;
		var isCredit = netCredit >= 0m;

		var kind = isCredit ? ProposalKind.Roll : ProposalKind.AlertOnly;
		var rationaleBase = $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}";
		var rationale = isCredit
			? $"{rationaleBase}; roll {shortLeg.Symbol}→{newSymbol} for net credit ${netCredit:F2}"
			: $"{rationaleBase}; no-better-alternative (proposed roll debit ${-netCredit:F2}, not a credit)";

		return new ManagementProposal(
			Rule: "DefensiveRollRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: kind,
			Legs: legs,
			NetDebit: -netCredit, // negative netDebit = credit
			Rationale: rationale
		);
	}

	/// <summary>Returns the next Friday strictly after the given date (weekly expiry).</summary>
	private static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}
}
