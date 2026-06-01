namespace WebullAnalytics.AI;

/// <summary>
/// Decides how a proposal's legs map onto broker orders. Most structures place as a single combo;
/// the three structures Webull won't accept as one 4-leg ticket place as TWO orders:
///   • DiagonalVertical → near vertical + far vertical (grouped by expiry)
///   • DoubleCalendar / DoubleDiagonal → put side + call side (grouped by call/put)
/// Both the proposal display (<see cref="Output.OpenProposalSink"/>) and the auto-executor
/// (<see cref="OpenerAutoExecutor"/>) split the same way, so a split structure shows and submits
/// identically — and the executor counts the whole thing as ONE trade against the daily cap so it
/// can never strand half the structure. Falls back to a single combo when parsing/grouping is
/// degenerate (the caller then prices the whole leg set as one order).
/// </summary>
internal static class StructureOrderSplit
{
	public static IReadOnlyList<(string Label, IReadOnlyList<ProposalLeg> Legs)> Split(OpenStructureKind kind, IReadOnlyList<ProposalLeg> legs)
	{
		if (kind is OpenStructureKind.DoubleCalendar or OpenStructureKind.DoubleDiagonal or OpenStructureKind.DiagonalVertical)
		{
			var parsed = legs
				.Select(l => (Leg: l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
				.Where(x => x.Parsed != null)
				.ToList();
			var groups = kind == OpenStructureKind.DiagonalVertical
				? parsed.GroupBy(x => x.Parsed!.ExpiryDate).OrderBy(g => g.Key)
					.Select((g, i) => (Label: i == 0 ? "near vertical" : "far vertical", Legs: (IReadOnlyList<ProposalLeg>)g.Select(x => x.Leg).ToList()))
					.ToList()
				: parsed.GroupBy(x => x.Parsed!.CallPut, StringComparer.Ordinal).OrderBy(g => g.Key == "P" ? 0 : 1)
					.Select(g => (Label: g.Key == "P" ? "put side" : "call side", Legs: (IReadOnlyList<ProposalLeg>)g.Select(x => x.Leg).ToList()))
					.ToList();
			if (groups.Count == 2 && groups.All(g => g.Legs.Count == 2))
				return groups;
		}
		return new (string, IReadOnlyList<ProposalLeg>)[] { ("", legs) };
	}
}
