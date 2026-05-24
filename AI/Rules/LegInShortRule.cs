using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2 (runs before TakeProfit alphabetically): converts a single-leg long call/put into a
/// vertical spread by selling a higher-strike call (for calls) or lower-strike put (for puts) at the
/// same expiry. The intent is to lock in profit on a winner that's gone gamma-saturated — the long
/// keeps some delta, but capping upside is fair when each additional $1 of move is worth less in
/// premium than the day's theta. Emits <see cref="ProposalKind.LegIn"/>.
///
/// Why a separate rule from <c>OpportunisticRollRule</c>: that rule rolls existing shorts on
/// existing spreads. Single-leg longs have no short to roll — they get a short added, which is
/// structurally a different operation (cash flow from one new sell-to-open vs a buy-to-close +
/// sell-to-open pair).
/// </summary>
internal sealed class LegInShortRule : IManagementRule
{
	private readonly LegInShortConfig _config;
	private readonly IndicatorsConfig _indicators;

	public LegInShortRule(LegInShortConfig config, IndicatorsConfig indicators)
	{
		_config = config;
		_indicators = indicators;
	}

	public string Name => "LegInShortRule";
	// Priority 2 alongside CloseBeforeShortExpiry / OpportunisticRoll / TakeProfit; alphabetical
	// tiebreak (L between C and O) means we run after CloseBeforeShortExpiry (which only fires on
	// short-leg expiry day anyway) and before TakeProfit (intent: leg-in instead of flat-close when
	// both conditions apply on a saturated winner).
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		// Only single-leg long positions are eligible. LongCallVertical / LongPutVertical already
		// have a short capping upside; iron condors etc. have their own short legs.
		var isLongCall = string.Equals(position.StrategyKind, "LongCall", StringComparison.OrdinalIgnoreCase);
		var isLongPut = string.Equals(position.StrategyKind, "LongPut", StringComparison.OrdinalIgnoreCase);
		if (!isLongCall && !isLongPut) return null;
		if (position.Legs.Count != 1) return null;

		var longLeg = position.Legs[0];
		if (longLeg.Side != Side.Buy || longLeg.CallPut == null || !longLeg.Expiry.HasValue) return null;

		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m) return null;

		// Regime gates — skip on high-VIX days or trend days. Both are off when the threshold is
		// at its sentinel (999); when the source can't supply the indicator (ctx field null) the
		// gate is treated as non-binding so the rule still fires in environments without VIX data.
		if (_config.MaxVix < 999m && ctx.Vix is decimal vix && vix >= _config.MaxVix) return null;
		if (_config.MaxIntradayRangePct < 999m && ctx.IntradaySpotRangePct is decimal r && r >= _config.MaxIntradayRangePct) return null;

		// ITM check: spot ≥ K × (1 + minSpotPctITM%) for calls; spot ≤ K × (1 − minSpotPctITM%) for puts.
		var pctITM = _config.MinSpotPctITM / 100m;
		var itm = isLongCall
			? spot >= longLeg.Strike * (1m + pctITM)
			: spot <= longLeg.Strike * (1m - pctITM);
		if (!itm) return null;

		// DTE gate.
		var dte = (longLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte < _config.MinDTE) return null;

		// Compute long-leg delta. Need an IV estimate; prefer quote-supplied, fall back to back-solve from mid.
		var longIv = ResolveIv(longLeg, spot, ctx);
		var expiryTime = longLeg.Expiry.Value.Date + OptionMath.MarketClose;
		var timeYears = (expiryTime - ctx.Now).TotalDays / 365.0;
		if (timeYears <= 0) return null;
		var longDelta = OptionMath.Delta(spot, longLeg.Strike, timeYears, OptionMath.RiskFreeRate, longIv, longLeg.CallPut);
		if (Math.Abs(longDelta) < _config.MinLongDelta) return null;

		// Profit-to-date as fraction of debit. Current long mid − initial debit, normalized by debit.
		if (!ctx.Quotes.TryGetValue(longLeg.Symbol, out var longQ) || longQ.Bid == null || longQ.Ask == null) return null;
		var longMid = (longQ.Bid.Value + longQ.Ask.Value) / 2m;
		var profitPerShare = longMid - position.AdjustedNetDebit;
		if (profitPerShare <= 0m || position.AdjustedNetDebit <= 0m) return null;
		var profitPct = profitPerShare / position.AdjustedNetDebit;
		if (profitPct < _config.TriggerProfitPct) return null;

		// Find the short strike in the chain with delta closest to target, same expiry.
		var (shortSymbol, shortQuote, shortDelta) = FindShortCandidate(position.Ticker, longLeg, spot, timeYears, ctx);
		if (shortSymbol == null || shortQuote == null) return null;

		// Min credit gate: the short must pay enough to make the conversion worthwhile.
		if (shortQuote.Bid == null) return null;
		var shortCredit = shortQuote.Bid.Value;
		if (shortCredit < _config.MinShortCreditPerShare) return null;
		var shortMid = shortQuote.Ask is decimal sa ? (shortQuote.Bid.Value + sa) / 2m : shortQuote.Bid.Value;

		var newStructure = _config.CreditSpread ? "ShortVertical" : (isLongCall ? "LongCallVertical" : "LongPutVertical");
		var legs = new[]
		{
			// Single sell-to-open leg. The existing long is preserved by SimulatedBook.LegIn / ManagementAutoExecutor.
			new ProposalLeg("sell", shortSymbol, longLeg.Qty, shortMid, shortQuote.Bid)
		};

		var modeLabel = _config.CreditSpread ? "credit spread" : "debit spread";
		var rationale = $"long {(isLongCall ? "call" : "put")} ITM {(isLongCall ? "" : "−")}{Math.Abs((spot - longLeg.Strike) / longLeg.Strike) * 100m:F1}% (Δ {Math.Abs(longDelta):F2}, profit {profitPct:P0}); sell short@{shortQuote.Bid.Value:F2} (Δ {Math.Abs(shortDelta):F2}) → {newStructure} ({modeLabel})";

		return new ManagementProposal(
			Rule: "LegInShortRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.LegIn,
			Legs: legs,
			NetDebit: -shortMid, // credit received
			Rationale: rationale
		);
	}

	/// <summary>Scan ctx.Quotes for option contracts on the same underlying + expiry + call/put as the
	/// long leg, but at a strike whose delta is closest to <c>targetShortDelta</c> (within tolerance).
	/// Strikes on the wrong side of spot are excluded so we always sell OTM relative to the long's
	/// directional bet.</summary>
	private (string? Symbol, OptionContractQuote? Quote, decimal Delta) FindShortCandidate(string ticker, PositionLeg longLeg, decimal spot, double timeYears, EvaluationContext ctx)
	{
		string? bestSymbol = null;
		OptionContractQuote? bestQuote = null;
		decimal bestDelta = 0m;
		decimal bestDiff = decimal.MaxValue;

		foreach (var (sym, q) in ctx.Quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null) continue;
			if (!string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (p.ExpiryDate != longLeg.Expiry!.Value) continue;
			if (p.CallPut != longLeg.CallPut) continue;

			// Strike-direction filter depends on mode.
			// Debit-spread mode (default): cap upside — short is OTM relative to spot/long.
			//   Long call → short K > K_long. Long put → short K < K_long.
			// Credit-spread mode: monetize current ITM-ness — short is DEEPER ITM than the long.
			//   Long call → short K < K_long. Long put → short K > K_long.
			// Same-strike short would be a synthetic equity — excluded in both modes.
			var isCall = longLeg.CallPut == "C";
			if (_config.CreditSpread)
			{
				if (isCall && p.Strike >= longLeg.Strike) continue;
				if (!isCall && p.Strike <= longLeg.Strike) continue;
			}
			else
			{
				if (isCall && p.Strike <= longLeg.Strike) continue;
				if (!isCall && p.Strike >= longLeg.Strike) continue;
			}

			if (q.Bid == null || q.Ask == null) continue;

			// Estimate this strike's delta using its own IV (or back-solved from mid).
			decimal iv;
			if (q.ImpliedVolatility is decimal qiv && qiv > 0m) iv = qiv;
			else
			{
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				if (mid <= 0m) continue;
				iv = OptionMath.ImpliedVol(spot, p.Strike, timeYears, OptionMath.RiskFreeRate, mid, p.CallPut);
				if (iv <= 0m || iv > 5m) continue;
			}
			var delta = OptionMath.Delta(spot, p.Strike, timeYears, OptionMath.RiskFreeRate, iv, p.CallPut);
			var absDelta = Math.Abs(delta);
			if (Math.Abs(absDelta - _config.TargetShortDelta) > _config.ShortDeltaTolerance) continue;

			var diff = Math.Abs(absDelta - _config.TargetShortDelta);
			if (diff < bestDiff) { bestDiff = diff; bestSymbol = sym; bestQuote = q; bestDelta = delta; }
		}

		return (bestSymbol, bestQuote, bestDelta);
	}

	private static decimal ResolveIv(PositionLeg leg, decimal spot, EvaluationContext ctx)
	{
		if (ctx.Quotes.TryGetValue(leg.Symbol, out var q))
		{
			if (q.ImpliedVolatility is decimal iv && iv > 0m) return iv;
			if (q.Bid is decimal bid && q.Ask is decimal ask && leg.Expiry.HasValue)
			{
				var mid = (bid + ask) / 2m;
				if (mid > 0m)
				{
					var expiryTime = leg.Expiry.Value.Date + OptionMath.MarketClose;
					var timeYears = (expiryTime - ctx.Now).TotalDays / 365.0;
					if (timeYears > 0)
					{
						var solved = OptionMath.ImpliedVol(spot, leg.Strike, timeYears, OptionMath.RiskFreeRate, mid, leg.CallPut!);
						if (solved > 0m && solved < 5m) return solved;
					}
				}
			}
		}
		return 0.40m;
	}
}
