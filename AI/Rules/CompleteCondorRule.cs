using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// "Complete the condor": converts a held single-sided short vertical (ShortPutVertical or
/// ShortCallVertical) into a 4-leg iron condor by selling the opposite-side short vertical at the same
/// expiry. Emits <see cref="ProposalKind.LegIn"/> with TWO legs (sell short + buy long wing) and relabels
/// the position to IronCondor.
///
/// Mirrors <see cref="LegInShortRule"/>'s shape — regime gates, delta-band short selection, min-credit
/// gate — but the held structure is a 2-leg credit spread (not a single-leg long) and the conversion adds
/// a full vertical rather than one leg.
///
/// CRITICAL RISK the gates exist to bound: completing the condor caps the held side's winning direction
/// and re-arms a loss on the opposite side. On a trend day that keeps going, the newly-sold side gets run
/// over and a winning trade becomes a loser. The held-side cushion + VIX/trend-day gates are what keep
/// this from systematically selling premium into the direction the market is already moving. It is a
/// range-bound bet: only fire it once the held side has earned real distance from spot.
/// </summary>
internal sealed class CompleteCondorRule : IManagementRule
{
	private readonly CompleteCondorConfig _config;

	public CompleteCondorRule(CompleteCondorConfig config) => _config = config;

	public string Name => "CompleteCondorRule";
	// Priority 3: runs after exits (StopLoss / CloseBeforeShortExpiry at priority 1-2) so a position being
	// stopped out or unwound on expiry day takes that path instead of having a second side bolted on.
	public int Priority => 3;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var isPutVert = string.Equals(position.StrategyKind, "ShortPutVertical", StringComparison.OrdinalIgnoreCase);
		var isCallVert = string.Equals(position.StrategyKind, "ShortCallVertical", StringComparison.OrdinalIgnoreCase);
		if (!isPutVert && !isCallVert) return null;
		if (position.Legs.Count != 2) return null;

		// Both legs must be the held side (all puts or all calls) at one expiry, with one sell + one buy.
		var heldSide = isPutVert ? "P" : "C";
		if (position.Legs.Any(l => l.CallPut != heldSide || !l.Expiry.HasValue)) return null;
		var exp = position.Legs[0].Expiry!.Value;
		if (position.Legs.Any(l => l.Expiry!.Value != exp)) return null;
		var heldShort = position.Legs.FirstOrDefault(l => l.Side == Side.Sell);
		var heldLong = position.Legs.FirstOrDefault(l => l.Side == Side.Buy);
		if (heldShort == null || heldLong == null) return null;
		var heldWidth = Math.Abs(heldShort.Strike - heldLong.Strike);
		if (heldWidth <= 0m) return null;

		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m) return null;

		// Regime gates (mirror LegInShortRule). Off at the 999 sentinel; non-binding when the source can't
		// supply the indicator so the rule still fires in environments without VIX / intraday-range data.
		if (_config.MaxVix < 999m && ctx.Vix is decimal vix && vix >= _config.MaxVix) return null;
		if (_config.MaxIntradayRangePct < 9.99m && ctx.IntradaySpotRangePct is decimal r && r >= _config.MaxIntradayRangePct) return null;

		// Held-side cushion: the held short must sit at least minHeldSidePctOtm OTM (fraction) — i.e. spot has
		// moved away from it. Put short is below spot (safe once spot rose); call short is above spot (safe once
		// spot fell). This is the "held side is winning" trigger that makes adding the other side a range
		// bet rather than a blind premium grab.
		var cushion = _config.MinHeldSidePctOtm;
		var heldSafe = isPutVert
			? spot >= heldShort.Strike * (1m + cushion)
			: spot <= heldShort.Strike * (1m - cushion);
		if (!heldSafe) return null;

		var newSide = isPutVert ? "C" : "P";
		var expiryTime = exp.Date + OptionMath.MarketClose;
		var timeYears = (expiryTime - ctx.Now).TotalDays / 365.0;
		if (timeYears <= 0) return null;

		var (shortSymbol, shortQuote, shortStrike) = FindOppositeShort(position.Ticker, exp, newSide, spot, timeYears, ctx);
		if (shortSymbol == null || shortQuote == null || shortQuote.Bid == null || shortQuote.Ask == null) return null;

		// Long wing = short ± heldWidth (mirror the held side's width for a symmetric condor). Must be
		// priced in the chain (the minute-walk pre-generates opposite-side strikes around spot).
		var longStrike = newSide == "C" ? shortStrike + heldWidth : shortStrike - heldWidth;
		var longSymbol = MatchKeys.OccSymbol(position.Ticker, exp, longStrike, newSide);
		if (!ctx.Quotes.TryGetValue(longSymbol, out var longQuote) || longQuote.Bid == null || longQuote.Ask == null) return null;

		// Net credit, conservative: short bid − long ask. Must clear the slippage/commission floor.
		var netCredit = shortQuote.Bid.Value - longQuote.Ask.Value;
		if (netCredit < _config.MinCreditPerShare) return null;

		var shortMid = (shortQuote.Bid.Value + shortQuote.Ask.Value) / 2m;
		var longMid = (longQuote.Bid.Value + longQuote.Ask.Value) / 2m;

		var newStructure = isPutVert ? "ShortCallVertical" : "ShortPutVertical";
		var legs = new[]
		{
			new ProposalLeg("sell", shortSymbol, position.Quantity, shortMid, shortQuote.Bid),
			new ProposalLeg("buy", longSymbol, position.Quantity, longMid, longQuote.Ask)
		};

		var heldPctOtm = Math.Abs((spot - heldShort.Strike) / heldShort.Strike) * 100m;
		var rationale = $"held {position.StrategyKind} short {heldShort.Strike:F0}{heldSide} is {heldPctOtm:F1}% OTM; sell {shortStrike:F0}/{longStrike:F0}{newSide} ({newStructure}) @ net {netCredit:F2} cr → IronCondor";

		return new ManagementProposal(
			Rule: Name,
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.LegIn,
			Legs: legs,
			NetDebit: -(shortMid - longMid), // net credit received at mid (negative debit = credit)
			Rationale: rationale
		);
	}

	/// <summary>Scan <see cref="EvaluationContext.Quotes"/> for OTM opposite-side contracts at the target
	/// expiry, returning the strike whose |delta| is closest to the middle of the configured band (and
	/// inside it). OTM = strike above spot for calls, below spot for puts.</summary>
	private (string? Symbol, OptionContractQuote? Quote, decimal Strike) FindOppositeShort(string ticker, DateTime exp, string side, decimal spot, double timeYears, EvaluationContext ctx)
	{
		var target = (_config.ShortDeltaMin + _config.ShortDeltaMax) / 2m;
		string? bestSymbol = null;
		OptionContractQuote? bestQuote = null;
		decimal bestStrike = 0m;
		decimal bestDiff = decimal.MaxValue;

		foreach (var (sym, q) in ctx.Quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || p.CallPut == null) continue;
			if (!string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (p.ExpiryDate != exp) continue;
			if (p.CallPut != side) continue;
			if (side == "C" && p.Strike <= spot) continue;
			if (side == "P" && p.Strike >= spot) continue;
			if (q.Bid == null || q.Ask == null) continue;

			decimal iv;
			if (q.ImpliedVolatility is decimal qiv && qiv > 0m) iv = qiv;
			else
			{
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				if (mid <= 0m) continue;
				iv = OptionMath.ImpliedVol(spot, p.Strike, timeYears, OptionMath.RiskFreeRate, mid, side);
				if (iv <= 0m || iv > 5m) continue;
			}
			var delta = Math.Abs(OptionMath.Delta(spot, p.Strike, timeYears, OptionMath.RiskFreeRate, iv, side));
			if (delta < _config.ShortDeltaMin || delta > _config.ShortDeltaMax) continue;

			var diff = Math.Abs(delta - target);
			if (diff < bestDiff) { bestDiff = diff; bestSymbol = sym; bestQuote = q; bestStrike = p.Strike; }
		}

		return (bestSymbol, bestQuote, bestStrike);
	}
}
