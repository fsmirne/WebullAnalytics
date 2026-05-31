namespace WebullAnalytics.AI;

internal static class DirectionalFit
{
	/// <summary>Returns +1 (bullish fit), −1 (bearish fit), or 0 (neutral) for the given structure kind.
	/// Calendars and the typical (symmetric) DD stay at 0; verticals and long calls/puts get signs by
	/// construction. LongDiagonal returns 0 here because its sign depends on the strike layout — use the
	/// skeleton overload for the strike-aware classification.</summary>
	public static int SignFor(OpenStructureKind kind) => kind switch
	{
		OpenStructureKind.LongCall => 1,
		OpenStructureKind.LongCallVertical => 1,
		OpenStructureKind.ShortPutVertical => 1,
		OpenStructureKind.LongPut => -1,
		OpenStructureKind.LongPutVertical => -1,
		OpenStructureKind.ShortCallVertical => -1,
		OpenStructureKind.LongCalendar => 0,
		OpenStructureKind.DoubleCalendar => 0,
		OpenStructureKind.LongDiagonal => 0,
		OpenStructureKind.DoubleDiagonal => 0,
		OpenStructureKind.IronButterfly => 0,
		OpenStructureKind.IronCondor => 0,
		_ => 0
	};

	/// <summary>Strike-aware fit. For LongDiagonal, the sign is determined by the long/short strike
	/// layout: long.strike &lt; short.strike → bullish (+1), long.strike &gt; short.strike → bearish (−1),
	/// equal strikes → neutral (the structure is really a calendar). The same rule holds for both calls
	/// and puts because in either case "long below short" produces positive net delta. All other kinds
	/// fall through to the kind-only overload.</summary>
	public static int SignFor(CandidateSkeleton skel)
	{
		// LongDiagonal and DiagonalVertical both lean directional via their long leg vs the next-OTM strike:
		// for the diagonal-vertical the first buy is the long-vertical anchor and the first sell its wing, so
		// the same "long below short → bullish" rule reads the debit vertical's direction correctly.
		if (skel.StructureKind is not (OpenStructureKind.LongDiagonal or OpenStructureKind.DiagonalVertical)) return SignFor(skel.StructureKind);

		var longLeg = skel.Legs.FirstOrDefault(l => l.Action == "buy");
		var shortLeg = skel.Legs.FirstOrDefault(l => l.Action == "sell");
		if (longLeg == null || shortLeg == null) return 0;

		var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
		var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
		if (longParsed == null || shortParsed == null || longParsed.CallPut != shortParsed.CallPut) return 0;
		if (longParsed.Strike == shortParsed.Strike) return 0;

		return longParsed.Strike < shortParsed.Strike ? 1 : -1;
	}
}
