using WebullAnalytics.Pricing;

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

		var currentEdgePerShare = EstimateShortExpiryEdge(position, shortLeg, spot, ctx);
		var (beLow, beHigh) = EstimateBreakEvenBand(position, shortLeg, spot, ctx);
		var insideBreakEvenBand = beLow.HasValue && beHigh.HasValue && spot >= beLow.Value && spot <= beHigh.Value;
		if (insideBreakEvenBand || currentEdgePerShare > 0m)
			return null;

		var beText = beLow.HasValue && beHigh.HasValue ? $", current break-evens ${beLow.Value:F2}-${beHigh.Value:F2}" : $", short-expiry edge ${currentEdgePerShare:+0.00;-0.00}/share";

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
              Rationale: $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}{beText}. Quote unavailable for new symbol {newSymbol}."
			);
		}

		var legs = new[]
		{
			new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty, (oldQ.Bid!.Value + oldQ.Ask.Value) / 2m, oldQ.Ask),   // close the old short at ask
			new ProposalLeg("sell", newSymbol, shortLeg.Qty, (newQ.Bid!.Value + newQ.Ask!.Value) / 2m, newQ.Bid)          // open the new short at bid
		};

		// netCredit = newBid - oldAsk (we sell the new short, buy to close the old).
		var netCredit = newQ.Bid.Value - oldQ.Ask.Value;
		var isCredit = netCredit >= 0m;

		var kind = isCredit ? ProposalKind.Roll : ProposalKind.AlertOnly;
       var rationaleBase = $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}{beText}";
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

	private static (decimal? Low, decimal? High) EstimateBreakEvenBand(OpenPosition position, PositionLeg shortLeg, decimal spot, EvaluationContext ctx)
	{
		var range = Math.Max(1m, Math.Max(Math.Abs(position.AdjustedNetDebit) * 6m, shortLeg.Strike * 0.12m));
		var notablePrices = position.Legs
			.Where(l => l.CallPut != null)
			.Select(l => l.Strike)
			.Append(shortLeg.Strike)
			.Append(spot)
			.Append(Math.Max(0m, shortLeg.Strike - range))
			.Append(shortLeg.Strike + range)
			.Distinct()
			.ToList();

		var step = OptionMath.GetPriceStep(shortLeg.Strike);
		var ladder = OptionMath.BuildPriceLadder(notablePrices, step, price => EstimateShortExpiryEdge(position, shortLeg, price, ctx), (_, _) => null);
		var breakEvens = OptionMath.FindBreakEvensNumerically(ladder, price => EstimateShortExpiryEdge(position, shortLeg, price, ctx)).OrderBy(x => x).ToList();

		return breakEvens.Count switch
		{
			0 => (null, null),
			1 => (breakEvens[0], breakEvens[0]),
			_ => (breakEvens[0], breakEvens[^1])
		};
	}

	private static decimal EstimateShortExpiryEdge(OpenPosition position, PositionLeg shortLeg, decimal underlyingPrice, EvaluationContext ctx)
	{
		if (!shortLeg.Expiry.HasValue)
			return 0m;

		var shortExpiry = shortLeg.Expiry.Value.Date + OptionMath.MarketClose;
		var qtyRef = Math.Max(1m, position.Quantity);
		decimal netValuePerShare = 0m;

		foreach (var leg in position.Legs)
		{
			if (leg.CallPut == null || !leg.Expiry.HasValue)
				continue;

			var sign = leg.Side == Side.Buy ? 1m : -1m;
			var weight = leg.Qty / qtyRef;
			var remainingYears = (leg.Expiry.Value.Date + OptionMath.MarketClose - shortExpiry).TotalDays / 365.0;
			var value = remainingYears > 0
				? OptionMath.BlackScholes(underlyingPrice, leg.Strike, remainingYears, OptionMath.RiskFreeRate, ResolveIv(leg, underlyingPrice, ctx, shortExpiry), leg.CallPut)
				: OptionMath.Intrinsic(underlyingPrice, leg.Strike, leg.CallPut);
			netValuePerShare += sign * weight * value;
		}

		return netValuePerShare - position.AdjustedNetDebit;
	}

	private static decimal ResolveIv(PositionLeg leg, decimal spot, EvaluationContext ctx, DateTime valuationTime)
	{
		if (ctx.Quotes.TryGetValue(leg.Symbol, out var q) && q.ImpliedVolatility is decimal iv && iv > 0m)
			return iv;

		if (ctx.Quotes.TryGetValue(leg.Symbol, out q) && q.Bid is decimal bid && q.Ask is decimal ask && leg.Expiry.HasValue)
		{
			var mid = (bid + ask) / 2m;
			var expiryTime = leg.Expiry.Value.Date + OptionMath.MarketClose;
			var timeYears = (expiryTime - valuationTime).TotalDays / 365.0;
			if (timeYears > 0)
				return OptionMath.ImpliedVol(spot, leg.Strike, timeYears, OptionMath.RiskFreeRate, mid, leg.CallPut!);
		}

		return 0.40m;
	}
}
