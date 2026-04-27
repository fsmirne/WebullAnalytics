using WebullAnalytics.AI.Output;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>
/// Shared scenario enumeration and scoring engine used by both `analyze position` and the
/// AI rule pipeline. Given an open option position and market snapshot, produces a ranked
/// list of candidate management moves (hold, close, various rolls, reset) with projected
/// P&L, cash impact, BP impact, and days-to-target.
///
/// Neutral over the caller's position representation — both `PositionSnapshot` (user-spec input)
/// and `OpenPosition` (broker-fed state) convert to `LegInfo` before invoking.
/// </summary>
internal static class ScenarioEngine
{
	/// <summary>Neutral per-leg state. Cost basis intentionally omitted — callers supply
	/// the position's net initial debit externally since it's the only value the scoring uses.</summary>
	public sealed record LegInfo(string Symbol, bool IsLong, int Qty, OptionParsed Parsed);

	public enum StructureKind { SingleLong, SingleShort, Calendar, Diagonal, Vertical, Unsupported }

	/// <summary>Full scenario result. Includes structured proposal legs (for ai rule → ManagementProposal
	/// conversion), plus all the numeric fields `analyze position` renders.</summary>
	public sealed record ScenarioResult(
		string Name,
		string ActionSummary,
		IReadOnlyList<ProposalLeg> ProposalLegs,
		ProposalKind Kind,
		decimal CashImpactPerContract,
		decimal ProjectedValuePerContract,
		decimal TotalPnLPerContract,
		decimal BPDeltaPerContract,
		int Qty,
		int DaysToTarget,
		string Rationale);

	// ─── Classifier ──────────────────────────────────────────────────────────

	public static StructureKind Classify(IReadOnlyList<LegInfo> legs)
	{
		if (legs.Count == 1) return legs[0].IsLong ? StructureKind.SingleLong : StructureKind.SingleShort;

		if (legs.Count == 2)
		{
			var sl = legs.FirstOrDefault(l => !l.IsLong);
			var ll = legs.FirstOrDefault(l => l.IsLong);
			if (sl == null || ll == null) return StructureKind.Unsupported;
			if (sl.Parsed.Root != ll.Parsed.Root || sl.Parsed.CallPut != ll.Parsed.CallPut) return StructureKind.Unsupported;
			if (sl.Parsed.ExpiryDate == ll.Parsed.ExpiryDate) return StructureKind.Vertical;
			if (sl.Parsed.ExpiryDate < ll.Parsed.ExpiryDate)
				return sl.Parsed.Strike == ll.Parsed.Strike ? StructureKind.Calendar : StructureKind.Diagonal;
		}

		return StructureKind.Unsupported;
	}

	// ─── Hypothetical-symbol enumeration (for up-front quote pre-fetch) ──────

	public static IEnumerable<string> EnumerateHypotheticalSymbols(IReadOnlyList<LegInfo> legs, StructureKind kind, decimal spot, decimal strikeStep, DateTime asOf)
	{
		if (legs.Count == 0) yield break;
		var root = legs[0].Parsed.Root;
		var callPut = legs[0].Parsed.CallPut;

		if (kind == StructureKind.SingleLong)
		{
			var longLeg = legs[0];
			var shortExpiry = NextWeeklyFromDate(asOf);
			if (shortExpiry < longLeg.Parsed.ExpiryDate)
				yield return MatchKeys.OccSymbol(root, shortExpiry, longLeg.Parsed.Strike, callPut);
		}
		else if (kind == StructureKind.Calendar || kind == StructureKind.Diagonal)
		{
			var shortLeg = legs.First(l => !l.IsLong);
			var longLeg = legs.First(l => l.IsLong);
			var newExpiry = NextWeekly(shortLeg.Parsed.ExpiryDate);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike, callPut);
			// DefensiveRollRule steps the short strike one increment in either direction; BracketStrikes(spot) may not include those strikes.
			if (shortLeg.Parsed.Strike - strikeStep > 0m) yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike - strikeStep, callPut);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike + strikeStep, callPut);
			var newLongExp = longLeg.Parsed.ExpiryDate > newExpiry ? longLeg.Parsed.ExpiryDate : newExpiry.AddDays(21);
			var oppositeCp = callPut == "C" ? "P" : "C";
			foreach (var strike in BracketStrikes(spot, strikeStep))
			{
				if (strike <= 0m) continue;
				if (strike != shortLeg.Parsed.Strike)
				{
					yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, strike, callPut); // same-expiry
					yield return MatchKeys.OccSymbol(root, newExpiry, strike, callPut);                 // next-weekly
				}
				yield return MatchKeys.OccSymbol(root, newLongExp, strike, callPut);                     // reset long leg
																										 // "Add" scenarios: same-side new calendar + opposite-side double calendar/diagonal.
				yield return MatchKeys.OccSymbol(root, newExpiry, strike, oppositeCp);
				yield return MatchKeys.OccSymbol(root, newLongExp, strike, oppositeCp);
			}
		}
	}

	// ─── Main evaluator ──────────────────────────────────────────────────────

	public sealed record EvaluateOptions(
		decimal InitialNetDebitPerShare,
		decimal IvDefault,                // percent, e.g. 40
		decimal StrikeStep,                // e.g. 0.50
		decimal? AvailableCash,
		IReadOnlyDictionary<string, decimal>? IvOverrides,
		string PricingMode = SuggestionPricing.Mid); // symbol → IV percent

	public static IReadOnlyList<ScenarioResult> Evaluate(
		IReadOnlyList<LegInfo> legs,
		StructureKind kind,
		decimal spot,
		DateTime asOf,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes,
		EvaluateOptions options) =>
		kind switch
		{
			StructureKind.SingleLong => SingleLongScenarios(legs[0], options, spot, asOf, quotes),
			StructureKind.Calendar or StructureKind.Diagonal => SpreadScenarios(legs, options, spot, asOf, quotes),
			_ => Array.Empty<ScenarioResult>()
		};

	// ─── Single long ─────────────────────────────────────────────────────────

	private static List<ScenarioResult> SingleLongScenarios(LegInfo longLeg, EvaluateOptions opt, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var list = new List<ScenarioResult>();
		var iv = ResolveIV(longLeg.Symbol, opt, quotes);
		var callPut = longLeg.Parsed.CallPut;
		var initialDebit = opt.InitialNetDebitPerShare;

		// 1. Hold to expiry.
		var valueAtExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		var holdDte = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		list.Add(BuildSingleLeg("Hold to expiry", "—", longLeg.Qty, 0m, valueAtExpiry, 0m, initialDebit, holdDte,
			$"value at expiry ({longLeg.Parsed.ExpiryDate:yyyy-MM-dd}) = intrinsic ${valueAtExpiry:F2}/share",
			ProposalKind.AlertOnly, Array.Empty<ProposalLeg>()));

		// 2. Close now.
		var dteNow = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var midNow = LiveOrBsMid(quotes, longLeg.Symbol, spot, longLeg.Parsed.Strike, dteNow, iv, callPut);
		var (bidNow, _) = LiveBidAsk(quotes, longLeg.Symbol, midNow);
		list.Add(BuildSingleLeg("Close now", $"SELL {longLeg.Symbol} x{longLeg.Qty}", longLeg.Qty, midNow, 0m, 0m, initialDebit, 1,
			$"sell at mid ${midNow:F2}/share → close position",
			ProposalKind.Close, new[] { new ProposalLeg("sell", longLeg.Symbol, longLeg.Qty, midNow, bidNow) }));

		// 3. Convert to calendar.
		var shortExpiry = NextWeeklyFromDate(asOf);
		if (shortExpiry < longLeg.Parsed.ExpiryDate)
		{
			var newShortSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, shortExpiry, longLeg.Parsed.Strike, callPut);
			var ivNewShort = ResolveIV(newShortSym, opt, quotes);
			var dteShort = Math.Max(1, (shortExpiry - asOf).Days);
			var shortMid = LiveOrBsMid(quotes, newShortSym, spot, longLeg.Parsed.Strike, dteShort, ivNewShort, callPut);
			var (shortBid, _) = LiveBidAsk(quotes, newShortSym, shortMid);
			var dteLongAtShortExp = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days);
			var longAtShortExp = (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, dteLongAtShortExp / 365.0, 0.036, iv, callPut);
			var shortAtShortExp = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
			var net = longAtShortExp - shortAtShortExp;
			list.Add(BuildSingleLeg($"Convert to calendar (sell {shortExpiry:yyyy-MM-dd} @ ${longLeg.Parsed.Strike:F2})",
				$"SELL {newShortSym} x{longLeg.Qty}", longLeg.Qty, shortMid, net, 0m, initialDebit, dteShort,
				$"collect ${shortMid:F2}/share short premium; at short exp: long ${longAtShortExp:F2} - short ${shortAtShortExp:F2} = ${net:F2}",
				ProposalKind.Roll, new[] { new ProposalLeg("sell", newShortSym, longLeg.Qty, shortMid, shortBid) }));
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract / Math.Max(1m, s.DaysToTarget)).ToList();
	}

	// ─── Spread (Calendar / Diagonal) ────────────────────────────────────────

	private static List<ScenarioResult> SpreadScenarios(IReadOnlyList<LegInfo> legs, EvaluateOptions opt, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var list = new List<ScenarioResult>();
		var shortLeg = legs.First(l => !l.IsLong);
		var longLeg = legs.First(l => l.IsLong);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, opt, quotes);
		var ivLong = ResolveIV(longLeg.Symbol, opt, quotes);
		var initialDebit = opt.InitialNetDebitPerShare;
		var qty = legs[0].Qty;

		var shortMidNow = LiveOrBsMid(quotes, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivShort, callPut);
		var longMidNow = LiveOrBsMid(quotes, longLeg.Symbol, spot, longLeg.Parsed.Strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivLong, callPut);
		var (shortBidNow, shortAskNow) = LiveBidAsk(quotes, shortLeg.Symbol, shortMidNow);
		var (longBidNow, longAskNow) = LiveBidAsk(quotes, longLeg.Symbol, longMidNow);
		var shortCloseNow = BuyPrice(shortMidNow, shortAskNow, opt.PricingMode);
		var longCloseNow = SellPrice(longMidNow, longBidNow, opt.PricingMode);

		var currentBp = AnalyzeCommon.ComputeLegMargin(shortLeg.Parsed, 1, spot, shortMidNow, longLeg.Parsed, null, 1, longMidNow, isExisting: true).Total;

		decimal LongValueAtShortExpiry(decimal longStrike, DateTime shortExpiry) =>
			(decimal)OptionMath.BlackScholes(spot, longStrike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days) / 365.0, 0.036, ivLong, callPut);

		var longAtOriginalExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
		var shortAtOriginalExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
		var holdNetPerShare = longAtOriginalExp - shortAtOriginalExp;
		var origShortDte = Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);

		// 1. Hold to short expiry.
		list.Add(BuildSpread("Hold to short expiry", "—", qty, 0m, holdNetPerShare, 0m, initialDebit, origShortDte,
			$"at {shortLeg.Parsed.ExpiryDate:yyyy-MM-dd}: long ${longAtOriginalExp:F2} - short ${shortAtOriginalExp:F2} intrinsic = ${holdNetPerShare:F2}", ProposalKind.AlertOnly, Array.Empty<ProposalLeg>()));

		// 2. Close short only.
		{
			var cash = -shortCloseNow;
			var bpDelta = 0m - currentBp;
			list.Add(BuildSpread("Close short only", $"BUY {shortLeg.Symbol} x{qty}", qty, cash, longAtOriginalExp, bpDelta, initialDebit, origShortDte,
				$"buy back short at ${shortCloseNow:F2}/share; keep long → ${longAtOriginalExp:F2}/share at short exp",
				ProposalKind.Roll, [new ProposalLeg("buy", shortLeg.Symbol, qty, shortMidNow, shortAskNow)]));
		}

		// 3. Close all.
		{
			var cash = longCloseNow - shortCloseNow;
			var bpDelta = 0m - currentBp;
			list.Add(BuildSpread("Close all", $"BUY {shortLeg.Symbol} x{qty}, SELL {longLeg.Symbol} x{qty}", qty, cash, 0m, bpDelta, initialDebit, 1,
				$"close at ${cash:+0.00;-0.00}/share net using {PricingLabel(opt.PricingMode)} pricing",
				ProposalKind.Close, new[]
				{
					new ProposalLeg("buy", shortLeg.Symbol, qty, shortMidNow, shortAskNow),
					new ProposalLeg("sell", longLeg.Symbol, qty, longMidNow, longBidNow)
				}));
		}

		var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
		if (newExp < longLeg.Parsed.ExpiryDate)
		{
			// 4. Roll short same strike, next weekly.
			{
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, shortLeg.Parsed.Strike, callPut);
				if (quotes == null || HasLiveQuote(quotes, newSym))
				{
					var ivNewShort = ResolveIV(newSym, opt, quotes);
					var dteNewShort = Math.Max(1, (newExp - asOf).Days);
					var newShortMidExec = LiveOrBsMid(quotes, newSym, spot, shortLeg.Parsed.Strike, dteNewShort, ivNewShort, callPut);
					var (newShortBid, _) = LiveBidAsk(quotes, newSym, newShortMidExec);
					var oldShortClose = BuyPrice(shortMidNow, shortAskNow, opt.PricingMode);
					var newShortOpen = SellPrice(newShortMidExec, newShortBid, opt.PricingMode);
					var cashPerShare = newShortOpen - oldShortClose;
					var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
					var shortAtNewShortExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
					var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
					var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, shortLeg.Parsed.Strike);
					var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
					var bpDelta = newBp - currentBp;
					EmitRoll(list, qty, $"Roll short ({newExp:MM-dd}, same strike)",
					  shortLeg.Symbol, newSym, oldShortPrice: shortMidNow, newShortPrice: newShortMidExec, oldShortExecutionPrice: shortAskNow, newShortExecutionPrice: newShortBid, cashPerShare, newProjectedPerShare, holdNetPerShare, bpDelta, initialDebit, dteNewShort, opt.AvailableCash,
						$"roll at ${cashPerShare:+0.00;-0.00}/share using {PricingLabel(opt.PricingMode)} pricing; at new exp: ${newProjectedPerShare:F2}");
				}
			}

			// 4.5. Roll short to bracket strikes, SAME expiry.
			foreach (var newStrike in BracketStrikes(spot, opt.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;
				var sameExpSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, newStrike, callPut);
				if (quotes != null && !HasLiveQuote(quotes, sameExpSym)) continue;

				var ivSameExp = ResolveIV(sameExpSym, opt, quotes);
				var dteSameExp = Math.Max(1, (shortLeg.Parsed.ExpiryDate - asOf).Days);
				var newShortMidSameExp = LiveOrBsMid(quotes, sameExpSym, spot, newStrike, dteSameExp, ivSameExp, callPut);
				var (newShortBidSameExp, _) = LiveBidAsk(quotes, sameExpSym, newShortMidSameExp);
				var oldShortClose = BuyPrice(shortMidNow, shortAskNow, opt.PricingMode);
				var newShortOpenSameExp = SellPrice(newShortMidSameExp, newShortBidSameExp, opt.PricingMode);
				var cashPerShareSameExp = newShortOpenSameExp - oldShortClose;
				var longAtOrigExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
				var newShortIntrinsicAtExp = Intrinsic(spot, newStrike, callPut);
				var projSameExpPerShare = longAtOrigExp - newShortIntrinsicAtExp;
				var sameExpShortParsed = new OptionParsed(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, callPut, newStrike);
				var sameExpBp = AnalyzeCommon.ComputeLegMargin(sameExpShortParsed, 1, spot, newShortMidSameExp, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var sameExpBpDelta = sameExpBp - currentBp;
				var sameExpStructure = StructureLabel(callPut, newStrike, longLeg.Parsed.Strike);

				EmitRoll(list, qty, $"Roll short to ${newStrike:F2} (same exp {shortLeg.Parsed.ExpiryDate:MM-dd}, {sameExpStructure})",
					shortLeg.Symbol, sameExpSym, oldShortPrice: shortMidNow, newShortPrice: newShortMidSameExp, oldShortExecutionPrice: shortAskNow, newShortExecutionPrice: newShortBidSameExp, cashPerShareSameExp, projSameExpPerShare, holdNetPerShare, sameExpBpDelta, initialDebit, dteSameExp, opt.AvailableCash,
				  $"shift to ${newStrike:F2} strike, keep {shortLeg.Parsed.ExpiryDate:MM-dd} expiry — collect theta this week; net ${cashPerShareSameExp:+0.00;-0.00}/share using {PricingLabel(opt.PricingMode)} pricing; at exp: ${projSameExpPerShare:F2}");
			}

			// 5. Roll short to bracket strikes, NEXT weekly.
			foreach (var newStrike in BracketStrikes(spot, opt.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, newStrike, callPut);
				if (quotes != null && !HasLiveQuote(quotes, newSym)) continue;

				var ivNewShort = ResolveIV(newSym, opt, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotes, newSym, spot, newStrike, dteNewShort, ivNewShort, callPut);
				var (newShortBid, _) = LiveBidAsk(quotes, newSym, newShortMidExec);
				var oldShortClose = BuyPrice(shortMidNow, shortAskNow, opt.PricingMode);
				var newShortOpen = SellPrice(newShortMidExec, newShortBid, opt.PricingMode);
				var cashPerShare = newShortOpen - oldShortClose;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, newStrike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var bpDelta = newBp - currentBp;
				var structureLabel = StructureLabel(callPut, newStrike, longLeg.Parsed.Strike);

				EmitRoll(list, qty, $"Roll short to ${newStrike:F2} ({newExp:MM-dd}, {structureLabel})",
					shortLeg.Symbol, newSym, oldShortPrice: shortMidNow, newShortPrice: newShortMidExec, oldShortExecutionPrice: shortAskNow, newShortExecutionPrice: newShortBid, cashPerShare, newProjectedPerShare, holdNetPerShare, bpDelta, initialDebit, dteNewShort, opt.AvailableCash,
				  $"step short to ${newStrike:F2} (spot ${spot:F2}); net ${cashPerShare:+0.00;-0.00}/share using {PricingLabel(opt.PricingMode)} pricing; at new exp: ${newProjectedPerShare:F2}");
			}
		}

		// 6. Reset at bracket strikes.
		{
			var newShortExp = newExp;
			var newLongExp = longLeg.Parsed.ExpiryDate > newShortExp ? longLeg.Parsed.ExpiryDate : newShortExp.AddDays(21);
			foreach (var newStrike in BracketStrikes(spot, opt.StrikeStep))
			{
				if (newStrike <= 0m) continue;
				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newShortExp, newStrike, callPut);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newLongExp, newStrike, callPut);
				if (quotes != null && (!HasLiveQuote(quotes, newShortSym) || !HasLiveQuote(quotes, newLongSym))) continue;

				var ivNewShort = ResolveIV(newShortSym, opt, quotes);
				var ivNewLong = ResolveIV(newLongSym, opt, quotes);
				var dteNewShort = Math.Max(1, (newShortExp - asOf).Days);
				var dteNewLong = Math.Max(1, (newLongExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotes, newShortSym, spot, newStrike, dteNewShort, ivNewShort, callPut);
				var newLongMidExec = LiveOrBsMid(quotes, newLongSym, spot, newStrike, dteNewLong, ivNewLong, callPut);
				var (newShortBid, _) = LiveBidAsk(quotes, newShortSym, newShortMidExec);
				var (_, newLongAsk) = LiveBidAsk(quotes, newLongSym, newLongMidExec);
				var closeNet = longCloseNow - shortCloseNow;
				var openNet = SellPrice(newShortMidExec, newShortBid, opt.PricingMode) - BuyPrice(newLongMidExec, newLongAsk, opt.PricingMode);
				var cashPerShare = closeNet + openNet;
				var longAtNewShortExp = (decimal)OptionMath.BlackScholes(spot, newStrike, Math.Max(1, (newLongExp.Date - newShortExp.Date).Days) / 365.0, 0.036, ivNewLong, callPut);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newShortExp, callPut, newStrike);
				var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newLongExp, callPut, newStrike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, newLongParsed, null, 1, newLongMidExec, isExisting: false).Total;
				var bpDelta = newBp - currentBp;

				EmitReset(list, qty, $"Reset to ${newStrike:F2} calendar",
					shortLeg.Symbol, longLeg.Symbol, newShortSym, newLongSym, oldShortPrice: shortMidNow, oldLongPrice: longMidNow, newShortPrice: newShortMidExec, newLongPrice: newLongMidExec, oldShortExecutionPrice: shortAskNow, oldLongExecutionPrice: longBidNow, newShortExecutionPrice: newShortBid, newLongExecutionPrice: newLongAsk,
					cashPerShare, newProjectedPerShare, holdNetPerShare, bpDelta, initialDebit, dteNewShort, opt.AvailableCash,
				 $"close net ${closeNet:+0.00;-0.00}, open new net ${openNet:+0.00;-0.00} using {PricingLabel(opt.PricingMode)} pricing; projected at new short exp: ${newProjectedPerShare:F2}");
			}
		}

		// 7. Add new position alongside existing (hedging / diversification).
		// Iterates both same-side (add second call/put at different strike) and opposite-side
		// (create double calendar/diagonal) structures at bracket strikes. Existing position is untouched.
		{
			var addShortExp = newExp;
			var addLongExp = longLeg.Parsed.ExpiryDate > addShortExp ? longLeg.Parsed.ExpiryDate : addShortExp.AddDays(21);
			foreach (var addCp in new[] { callPut, callPut == "C" ? "P" : "C" })
			{
				var isOppositeSide = addCp != callPut;
				foreach (var newStrike in BracketStrikes(spot, opt.StrikeStep))
				{
					if (newStrike <= 0m) continue;
					if (!isOppositeSide && newStrike == shortLeg.Parsed.Strike) continue; // avoid doubling same-CP same-strike

					var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, addShortExp, newStrike, addCp);
					var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, addLongExp, newStrike, addCp);
					if (quotes != null && (!HasLiveQuote(quotes, newShortSym) || !HasLiveQuote(quotes, newLongSym))) continue;

					var ivNewShort = ResolveIV(newShortSym, opt, quotes);
					var ivNewLong = ResolveIV(newLongSym, opt, quotes);
					var dteNewShort = Math.Max(1, (addShortExp - asOf).Days);
					var dteNewLong = Math.Max(1, (addLongExp - asOf).Days);
					var newShortMid = LiveOrBsMid(quotes, newShortSym, spot, newStrike, dteNewShort, ivNewShort, addCp);
					var newLongMid = LiveOrBsMid(quotes, newLongSym, spot, newStrike, dteNewLong, ivNewLong, addCp);
					var (newShortBid, _) = LiveBidAsk(quotes, newShortSym, newShortMid);
					var (_, newLongAsk) = LiveBidAsk(quotes, newLongSym, newLongMid);
					var cashPerShare = SellPrice(newShortMid, newShortBid, opt.PricingMode) - BuyPrice(newLongMid, newLongAsk, opt.PricingMode);

					// Project the NEW position at the existing short's expiry (first milestone).
					var origShortExp = shortLeg.Parsed.ExpiryDate;
					var daysToOrigShort = origShortDte;
					var tRemainNewShort = Math.Max(1, (addShortExp.Date - origShortExp.Date).Days) / 365.0;
					var tRemainNewLong = Math.Max(1, (addLongExp.Date - origShortExp.Date).Days) / 365.0;
					var newShortAtOrigExp = (decimal)OptionMath.BlackScholes(spot, newStrike, tRemainNewShort, 0.036, ivNewShort, addCp);
					var newLongAtOrigExp = (decimal)OptionMath.BlackScholes(spot, newStrike, tRemainNewLong, 0.036, ivNewLong, addCp);
					var newPositionValuePerShare = newLongAtOrigExp - newShortAtOrigExp;

					// Opening a long calendar/diagonal is pure-debit: BP required = the selected price-basis
					// debit paid. MID is the default; bid/ask is available only when explicitly requested.
					var newBp = Math.Max(-cashPerShare, 0m) * 100m;

					var sideLabel = addCp == "C" ? "call" : "put";
					// The added trade has both legs at `newStrike` with different expiries — always a calendar.
					// Same-side adds a second calendar at a different strike; opposite-side creates a double calendar.
					var structureLabel = addCp == callPut ? "second-strike calendar" : "double calendar";

					EmitAdd(list, qty, $"Add ${newStrike:F2} {sideLabel} {addShortExp:MM-dd}/{addLongExp:MM-dd} ({structureLabel}, keep existing)",
						newShortSym, newLongSym, newShortMid, newLongMid, newShortBid, newLongAsk, cashPerShare, newPositionValuePerShare, holdNetPerShare,
						newBp, initialDebit, daysToOrigShort, opt.AvailableCash,
						$"open new {sideLabel} calendar at ${newStrike:F2} (debit ${-cashPerShare:F2}/share); existing untouched → at {origShortExp:MM-dd}: existing ${holdNetPerShare:F2} + new ${newPositionValuePerShare:F2} = ${holdNetPerShare + newPositionValuePerShare:F2}/share");
				}
			}
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract / Math.Max(1m, s.DaysToTarget)).ToList();
	}

	// ─── Scenario builders ───────────────────────────────────────────────────

	private static ScenarioResult BuildSingleLeg(string name, string actionSummary, int qty, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, decimal initialDebit, int daysToTarget, string rationale, ProposalKind kind, IReadOnlyList<ProposalLeg> legs)
	{
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new ScenarioResult(name, actionSummary, legs, kind, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, qty, daysToTarget, rationale);
	}

	private static ScenarioResult BuildSpread(string name, string actionSummary, int qty, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, decimal initialDebit, int daysToTarget, string rationale, ProposalKind kind, IReadOnlyList<ProposalLeg> legs)
	{
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new ScenarioResult(name, actionSummary, legs, kind, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, qty, daysToTarget, rationale);
	}

	/// <summary>Emits a full-qty roll scenario plus (when cash-constrained) a partial variant sized to fit.</summary>
	private static void EmitRoll(List<ScenarioResult> list, int fullQty, string name, string oldShortSym, string newShortSym, decimal oldShortPrice, decimal newShortPrice, decimal oldShortExecutionPrice, decimal newShortExecutionPrice, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
	{
		var initialDebitPerContract = initialDebitPerShare * 100m;

		var fullCashPerContract = cashPerShareOfChange * 100m;
		var fullProjectedPerContract = newProjectedPerShare * 100m;
		var fullTotalPerContract = fullProjectedPerContract + fullCashPerContract - initialDebitPerContract;
		list.Add(new ScenarioResult(name, $"BUY {oldShortSym} x{fullQty}, SELL {newShortSym} x{fullQty}",
			new[] { new ProposalLeg("buy", oldShortSym, fullQty, oldShortPrice, oldShortExecutionPrice), new ProposalLeg("sell", newShortSym, fullQty, newShortPrice, newShortExecutionPrice) },
			ProposalKind.Roll, fullCashPerContract, fullProjectedPerContract, fullTotalPerContract, bpPerContract, fullQty, daysToTarget, rationale));

		if (!availableCash.HasValue || bpPerContract <= 0m) return;
		var maxPartial = (int)Math.Floor(availableCash.Value / bpPerContract);
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialProjectedTotal = newProjectedPerShare * 100m * maxPartial + unchangedProjectedPerShare * 100m * (fullQty - maxPartial);
		var partialTotalPnL = partialCashTotal + partialProjectedTotal - initialDebitPerContract * fullQty;
		list.Add(new ScenarioResult($"{name} · partial {maxPartial}/{fullQty}",
			$"BUY {oldShortSym} x{maxPartial}, SELL {newShortSym} x{maxPartial}",
			new[] { new ProposalLeg("buy", oldShortSym, maxPartial, oldShortPrice, oldShortExecutionPrice), new ProposalLeg("sell", newShortSym, maxPartial, newShortPrice, newShortExecutionPrice) },
			ProposalKind.Roll, partialCashTotal / fullQty, partialProjectedTotal / fullQty, partialTotalPnL / fullQty, bpPerContract * maxPartial / fullQty, fullQty, daysToTarget,
			$"execute on {maxPartial} contracts (${bpPerContract * maxPartial:N0} BP); hold remaining {fullQty - maxPartial} as original → ${unchangedProjectedPerShare:F2}/share at original exp"));
	}

	/// <summary>Emits a reset (close-all + reopen) scenario plus optional partial variant.</summary>
	private static void EmitReset(List<ScenarioResult> list, int fullQty, string name, string oldShortSym, string oldLongSym, string newShortSym, string newLongSym, decimal oldShortPrice, decimal oldLongPrice, decimal newShortPrice, decimal newLongPrice, decimal oldShortExecutionPrice, decimal oldLongExecutionPrice, decimal newShortExecutionPrice, decimal newLongExecutionPrice, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
	{
		var initialDebitPerContract = initialDebitPerShare * 100m;
		var action = $"BUY {oldShortSym} x{fullQty}, SELL {oldLongSym} x{fullQty}, BUY {newLongSym} x{fullQty}, SELL {newShortSym} x{fullQty}";
		var fullLegs = new[]
		{
			new ProposalLeg("buy", oldShortSym, fullQty, oldShortPrice, oldShortExecutionPrice),
			new ProposalLeg("sell", oldLongSym, fullQty, oldLongPrice, oldLongExecutionPrice),
			new ProposalLeg("buy", newLongSym, fullQty, newLongPrice, newLongExecutionPrice),
			new ProposalLeg("sell", newShortSym, fullQty, newShortPrice, newShortExecutionPrice),
		};

		var fullCashPerContract = cashPerShareOfChange * 100m;
		var fullProjectedPerContract = newProjectedPerShare * 100m;
		var fullTotalPerContract = fullProjectedPerContract + fullCashPerContract - initialDebitPerContract;
		list.Add(new ScenarioResult(name, action, fullLegs, ProposalKind.Roll, fullCashPerContract, fullProjectedPerContract, fullTotalPerContract, bpPerContract, fullQty, daysToTarget, rationale));

		if (!availableCash.HasValue || bpPerContract <= 0m) return;
		var maxPartial = (int)Math.Floor(availableCash.Value / bpPerContract);
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialProjectedTotal = newProjectedPerShare * 100m * maxPartial + unchangedProjectedPerShare * 100m * (fullQty - maxPartial);
		var partialTotalPnL = partialCashTotal + partialProjectedTotal - initialDebitPerContract * fullQty;
		var partialLegs = new[]
		{
			new ProposalLeg("buy", oldShortSym, maxPartial, oldShortPrice, oldShortExecutionPrice),
			new ProposalLeg("sell", oldLongSym, maxPartial, oldLongPrice, oldLongExecutionPrice),
			new ProposalLeg("buy", newLongSym, maxPartial, newLongPrice, newLongExecutionPrice),
			new ProposalLeg("sell", newShortSym, maxPartial, newShortPrice, newShortExecutionPrice),
		};
		list.Add(new ScenarioResult($"{name} · partial {maxPartial}/{fullQty}",
			$"BUY {oldShortSym} x{maxPartial}, SELL {oldLongSym} x{maxPartial}, BUY {newLongSym} x{maxPartial}, SELL {newShortSym} x{maxPartial}",
			partialLegs, ProposalKind.Roll, partialCashTotal / fullQty, partialProjectedTotal / fullQty, partialTotalPnL / fullQty, bpPerContract * maxPartial / fullQty, fullQty, daysToTarget,
			$"execute on {maxPartial} contracts (${bpPerContract * maxPartial:N0} BP); hold remaining {fullQty - maxPartial} as original → ${unchangedProjectedPerShare:F2}/share at original exp"));
	}

	/// <summary>Emits an "add new position alongside existing" scenario. Existing position is untouched;
	/// new structure adds BP and debit. Combined projection = existing hold + new position value at target.
	/// Partial variant sizes the added quantity to fit available cash while keeping ALL existing contracts.</summary>
	private static void EmitAdd(List<ScenarioResult> list, int fullQty, string name, string newShortSym, string newLongSym, decimal newShortPrice, decimal newLongPrice, decimal newShortExecutionPrice, decimal newLongExecutionPrice, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
	{
		var initialDebitPerContract = initialDebitPerShare * 100m;
		var action = $"BUY {newLongSym} x{fullQty}, SELL {newShortSym} x{fullQty}";
		var fullLegs = new[]
		{
			new ProposalLeg("buy", newLongSym, fullQty, newLongPrice, newLongExecutionPrice),
			new ProposalLeg("sell", newShortSym, fullQty, newShortPrice, newShortExecutionPrice),
		};

		var fullCashPerContract = cashPerShareOfChange * 100m;
		var fullCombinedValuePerContract = (unchangedProjectedPerShare + newProjectedPerShare) * 100m;
		var fullTotalPerContract = fullCombinedValuePerContract + fullCashPerContract - initialDebitPerContract;
		list.Add(new ScenarioResult(name, action, fullLegs, ProposalKind.Roll, fullCashPerContract, fullCombinedValuePerContract, fullTotalPerContract, bpPerContract, fullQty, daysToTarget, rationale));

		if (!availableCash.HasValue || bpPerContract <= 0m) return;
		var maxPartial = (int)Math.Floor(availableCash.Value / bpPerContract);
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		// Partial: add `maxPartial` new contracts, keep all `fullQty` existing.
		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialNewValue = newProjectedPerShare * 100m * maxPartial;
		var partialExistingValue = unchangedProjectedPerShare * 100m * fullQty;
		var partialCombinedValue = partialNewValue + partialExistingValue;
		var partialTotalPnL = partialCashTotal + partialCombinedValue - initialDebitPerContract * fullQty;

		var partialLegs = new[]
		{
			new ProposalLeg("buy", newLongSym, maxPartial, newLongPrice, newLongExecutionPrice),
			new ProposalLeg("sell", newShortSym, maxPartial, newShortPrice, newShortExecutionPrice),
		};
		list.Add(new ScenarioResult($"{name} · partial {maxPartial}",
			$"BUY {newLongSym} x{maxPartial}, SELL {newShortSym} x{maxPartial}",
			partialLegs, ProposalKind.Roll, partialCashTotal / fullQty, partialCombinedValue / fullQty, partialTotalPnL / fullQty, bpPerContract * maxPartial / fullQty, fullQty, daysToTarget,
			$"add {maxPartial} new contract(s) (${bpPerContract * maxPartial:N0} BP); keep all {fullQty} existing"));
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static string StructureLabel(string callPut, decimal newStrike, decimal longStrike) =>
		callPut == "C"
			? (newStrike < longStrike ? "inverted diagonal" : newStrike > longStrike ? "covered diagonal" : "calendar")
			: (newStrike > longStrike ? "inverted diagonal" : newStrike < longStrike ? "covered diagonal" : "calendar");

	public static bool HasLiveQuote(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol) =>
		quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Ask.Value > 0m;

	public static decimal LiveOrBsMid(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal spot, decimal strike, int dte, decimal iv, string callPut)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value + q.Ask.Value) / 2m;
		return (decimal)OptionMath.BlackScholes(spot, strike, dte / 365.0, 0.036, iv, callPut);
	}

	private static decimal BuyPrice(decimal mid, decimal ask, string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? ask : mid;

	private static decimal SellPrice(decimal mid, decimal bid, string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? bid : mid;

	private static string PricingLabel(string pricingMode) =>
		SuggestionPricing.Normalize(pricingMode) == SuggestionPricing.BidAsk ? "bid/ask" : "mid";

	public static (decimal bid, decimal ask) LiveBidAsk(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal fallbackMid)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value, q.Ask.Value);
		var spread = Math.Max(0.01m, fallbackMid * 0.01m);
		return (Math.Max(0m, fallbackMid - spread), fallbackMid + spread);
	}

	public static decimal ResolveIV(string symbol, EvaluateOptions opt, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (opt.IvOverrides != null && opt.IvOverrides.TryGetValue(symbol, out var ovr)) return ovr / 100m;
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m) return q.ImpliedVolatility.Value;
		return opt.IvDefault / 100m;
	}

	public static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}

	public static DateTime NextWeeklyFromDate(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}

	public static decimal Intrinsic(decimal spot, decimal strike, string callPut) =>
		callPut == "C" ? Math.Max(0m, spot - strike) : Math.Max(0m, strike - spot);

	public static IEnumerable<decimal> BracketStrikes(decimal spot, decimal step)
	{
		var below = Math.Floor(spot / step) * step;
		var above = Math.Ceiling(spot / step) * step;
		yield return below;
		if (above != below) yield return above;
	}
}
