namespace WebullAnalytics.AI;

/// <summary>
/// The contracts the management rules may price beyond an open position's own legs — the next-weekly and next-day short rolls
/// (CloseBeforeShortExpiryRule's rollPastBreakEven), the one-increment strike steps (DefensiveRollRule), bracket strikes around spot —
/// fetched up front by <see cref="AIPipelineHelper.FetchQuotesWithHypotheticals"/>. That fetch shapes the quote snapshot every consumer
/// of a scan/watch tick reads, openers included, so the symbol list is pinned by ManagementQuotePrefetchTests.
/// </summary>
internal static class ManagementQuotePrefetch
{
	/// <summary>One leg of an open position.</summary>
	public sealed record LegInfo(string Symbol, bool IsLong, int Qty, OptionParsed Parsed);

	public enum StructureKind { SingleLong, SingleShort, Calendar, Diagonal, Vertical, Unsupported }

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

	public static IEnumerable<string> EnumerateHypotheticalSymbols(IReadOnlyList<LegInfo> legs, StructureKind kind, decimal spot, decimal strikeStep, DateTime asOf)
	{
		if (legs.Count == 0) yield break;
		var root = legs[0].Parsed.Root;
		var callPut = legs[0].Parsed.CallPut;

		if (kind == StructureKind.SingleLong)
		{
			var longLeg = legs[0];
			var shortExpiry = MarketCalendar.NextFridayAfter(asOf);
			if (shortExpiry < longLeg.Parsed.ExpiryDate)
				yield return MatchKeys.OccSymbol(root, shortExpiry, longLeg.Parsed.Strike, callPut);
		}
		else if (kind == StructureKind.Calendar || kind == StructureKind.Diagonal)
		{
			var shortLeg = legs.First(l => !l.IsLong);
			var longLeg = legs.First(l => l.IsLong);
			var newExpiry = MarketCalendar.NextFridayAfter(shortLeg.Parsed.ExpiryDate);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike, callPut);
			// CloseBeforeShortExpiryRule's rollPastBreakEven targets the next trading DAY (daily expiries), not the next weekly.
			var nextDay = MarketCalendar.NextOpenAfter(shortLeg.Parsed.ExpiryDate);
			if (nextDay < newExpiry) yield return MatchKeys.OccSymbol(root, nextDay, shortLeg.Parsed.Strike, callPut);
			// DefensiveRollRule steps the short strike one increment in either direction; StrikeGrid.Bracket(spot) may not include those strikes.
			if (shortLeg.Parsed.Strike - strikeStep > 0m) yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike - strikeStep, callPut);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike + strikeStep, callPut);
			var newLongExp = longLeg.Parsed.ExpiryDate > newExpiry ? longLeg.Parsed.ExpiryDate : newExpiry.AddDays(21);
			var oppositeCp = callPut == "C" ? "P" : "C";
			foreach (var strike in StrikeGrid.Bracket(spot, strikeStep))
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
}
