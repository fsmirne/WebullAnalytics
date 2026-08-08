namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: on the day the short leg expires, propose closing the entire position once it's
/// comfortably profitable. Decision-only — it emits a single Close proposal for the full quantity.
/// Time-windowed scaled execution lives in <c>WatchAutoExecutor</c>; this rule does not care how
/// the close is filled, only when to surface the proposal.
///
/// Triggers:
/// 1. Short leg has DTE == 0 (we're inside the expiry session).
/// 2. Mark-to-market profit on the position is at least <c>minProfitPct</c> of initial debit
///    (default 30%) — locking in a real win, not bailing on a marginal trade.
/// 3. Emergency override: spot is past the calendar/diagonal break-even band (with the configured
///    buffer), in which case we close regardless of profit threshold — past BE every additional
///    tick is realized loss.
/// </summary>
internal sealed class CloseBeforeShortExpiryRule : IManagementRule
{
	private readonly CloseBeforeShortExpiryConfig _config;
	private readonly ExpiryRegimeProvider? _regimes;

	public CloseBeforeShortExpiryRule(CloseBeforeShortExpiryConfig config, ExpiryRegimeProvider? regimes = null) { _config = config; _regimes = regimes; }

	public string Name => "CloseBeforeShortExpiryRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Quantity <= 0) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null || !shortLeg.Expiry.HasValue) return null;

		// Only fire on the actual expiry day; pre-expiry adjustments belong to the roll rules.
		var dte = (shortLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte != 0) return null;

		// Assignment-risk safety: if ANY short leg is currently ITM on expiry day, close the position
		// regardless of P&L. A retail account can't cover a SPY short put assignment (100 shares of
		// underlying per contract) and a real broker would force-close. Modeling this lets us catch
		// situations where the position is unprofitable but still inside the buffered BE band — the
		// emergency-BE trigger below misses those.
		if (ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spotForITM) && HasItmShortLeg(position, spotForITM, out var itmDetail))
		{
			return BuildClose(position,
				$"assignment-risk: short leg ITM at expiry ({itmDetail}, spot ${spotForITM:F2}), close to avoid assignment",
				isEmergency: true);
		}

		// Emergency: spot past BE band → close immediately, profit threshold doesn't apply. With
		// rollPastBreakEven, an OTM short (the ITM case closed above) rolls one trading day at the
		// same strike instead, harvesting theta while waiting for spot to re-enter the band.
		if (IsSpotPastBreakEven(position, ctx, out var spot, out var beLow, out var beHigh))
		{
			if (_config.RollPastBreakEven)
			{
				var roll = TryBuildRoll(position, shortLeg, ctx, spot, beLow, beHigh);
				if (roll != null) return roll;
			}
			return BuildClose(position,
				$"emergency: spot ${spot:F2} outside BE band [${beLow:F2}, ${beHigh:F2}] on expiry day, close all {position.Quantity}",
				isEmergency: true);
		}

		// Layer-2 regime modulation (campaign: gex_layers). PIN sessions defer the PROFIT-gated close
		// (emergencies above always run) so pin-day decay keeps working; AMPLIFICATION sessions lower the
		// profit threshold so the smaller win is banked before the terrain can manufacture the tail.
		// Unclassified days (regime None / provider absent) are bit-identical to the unmodulated rule.
		var regime = _regimes != null && _config.Regime.Enabled ? _regimes.Get(position.Ticker, ctx.Now.Date) : ExpiryDayRegime.None;
		if (regime == ExpiryDayRegime.Pin && !string.IsNullOrWhiteSpace(_config.Regime.PinDeferProfitCloseUntilEt)
			&& TimeSpan.TryParse(_config.Regime.PinDeferProfitCloseUntilEt, System.Globalization.CultureInfo.InvariantCulture, out var deferUntil)
			&& ctx.Now.TimeOfDay < deferUntil)
			return null;

		// Profit gate: mark-to-market value vs. initial debit.
		var markPerContract = ComputeMarkPerContract(position, ctx);
		if (markPerContract == null) return null;

		var initialDebit = Math.Abs(position.InitialNetDebit);
		if (initialDebit <= 0m) return null;

		var effectiveMinProfitPct = regime == ExpiryDayRegime.Amplification ? _config.MinProfitPct * _config.Regime.AmpMinProfitPctFactor : _config.MinProfitPct;
		var profitPerContract = markPerContract.Value - initialDebit;
		var profitPct = profitPerContract / initialDebit;
		if (profitPct < effectiveMinProfitPct) return null;

		return BuildClose(position,
			$"expiry day, profit ${profitPerContract:F2}/contract = {profitPct * 100m:F1}% ≥ threshold {effectiveMinProfitPct * 100m:F1}%{(regime == ExpiryDayRegime.Amplification ? " (amplification regime, lowered)" : regime == ExpiryDayRegime.Pin ? " (pin regime, deferred)" : "")}, close all {position.Quantity}",
			isEmergency: false);
	}

	/// <summary>Computes the per-contract mark-to-market value (sum of leg midpoint values signed by
	/// direction). Returns null when any leg lacks a usable bid/ask.</summary>
	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (q.Bid == null || q.Ask == null || q.Ask.Value <= 0m) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	/// <summary>Returns true when any short leg of the position is in-the-money at <paramref name="spot"/>.
	/// <paramref name="detail"/> describes the worst offender for the rationale string.</summary>
	private static bool HasItmShortLeg(OpenPosition position, decimal spot, out string detail)
	{
		detail = "";
		decimal worstDepth = 0m;
		foreach (var leg in position.Legs)
		{
			if (leg.Side != Side.Sell || leg.CallPut == null) continue;
			var depth = leg.CallPut == "C"
				? spot - leg.Strike   // short call: ITM when spot > strike
				: leg.Strike - spot;  // short put: ITM when spot < strike
			if (depth > 0m && depth > worstDepth)
			{
				worstDepth = depth;
				detail = $"short {leg.CallPut}@{leg.Strike:F2} ITM by ${depth:F2}";
			}
		}
		return worstDepth > 0m;
	}

	private bool IsSpotPastBreakEven(OpenPosition position, EvaluationContext ctx, out decimal spot, out decimal beLow, out decimal beHigh)
	{
		spot = 0m; beLow = 0m; beHigh = 0m;
		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out spot)) return false;

		var (low, high, _) = PositionBreakEvenEstimator.Estimate(position, ctx);
		if (!low.HasValue || !high.HasValue) return false;
		beLow = low.Value;
		beHigh = high.Value;

		var buffer = _config.EmergencyBreakEvenBufferPct;
		return spot < beLow * (1m - buffer) || spot > beHigh * (1m + buffer);
	}

	/// <summary>Builds the same-strike next-trading-day roll for the OTM short on expiry day. Returns null
	/// (caller falls back to the emergency close) when the roll would reach the long leg's expiry or the
	/// old/new contract lacks a two-sided quote.</summary>
	private ManagementProposal? TryBuildRoll(OpenPosition position, PositionLeg shortLeg, EvaluationContext ctx, decimal spot, decimal beLow, decimal beHigh)
	{
		var longExpiry = position.Legs.Where(l => l.Side == Side.Buy && l.Expiry.HasValue).Select(l => l.Expiry!.Value.Date).DefaultIfEmpty(DateTime.MinValue).Min();
		var newExpiry = MarketCalendar.NextOpenAfter(shortLeg.Expiry!.Value.Date);
		if (longExpiry == DateTime.MinValue || newExpiry >= longExpiry) return null;

		if (!ctx.Quotes.TryGetValue(shortLeg.Symbol, out var oldQ) || oldQ.Bid == null || oldQ.Ask == null) return null;
		var newSymbol = MatchKeys.OccSymbol(position.Ticker, newExpiry, shortLeg.Strike, shortLeg.CallPut!);
		if (!ctx.Quotes.TryGetValue(newSymbol, out var newQ) || newQ.Bid == null || newQ.Ask == null) return null;

		var oldMid = (oldQ.Bid.Value + oldQ.Ask.Value) / 2m;
		var newMid = (newQ.Bid.Value + newQ.Ask.Value) / 2m;
		var legs = new[]
		{
			new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty, oldMid, oldQ.Ask),
			new ProposalLeg("sell", newSymbol, shortLeg.Qty, newMid, newQ.Bid)
		};

		return new ManagementProposal(
			Rule: Name,
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Roll,
			Legs: legs,
			NetDebit: oldMid - newMid,
			Rationale: $"[roll] spot ${spot:F2} outside BE band [${beLow:F2}, ${beHigh:F2}] on expiry day, short OTM — roll to {newExpiry:yyyy-MM-dd} same strike ${shortLeg.Strike:F2}, mid credit ${newMid - oldMid:F2}"
		);
	}

	private static ManagementProposal BuildClose(OpenPosition p, string rationale, bool isEmergency)
	{
		var legs = p.Legs.Select(l => new ProposalLeg(
			Action: l.Side == Side.Buy ? "sell" : "buy",
			Symbol: l.Symbol,
			Qty: l.Qty
		)).ToList();

		return new ManagementProposal(
			Rule: "CloseBeforeShortExpiryRule",
			Ticker: p.Ticker,
			PositionKey: p.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: 0m,
			Rationale: (isEmergency ? "[emergency] " : "") + rationale
		);
	}
}
