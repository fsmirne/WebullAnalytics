namespace WebullAnalytics.AI;

/// <summary>
/// Bridges an OpenPosition + EvaluationContext to a max-projected-profit value, reusing the existing
/// TimeDecayGridBuilder. Returns null when the grid cannot be built (missing IV for any leg).
/// PHASE-1 STUB: returns null so TakeProfitRule does not fire; a follow-up task wires this to
/// BreakEvenAnalyzer once the entry-point signature is finalized.
/// </summary>
internal static class ProfitProjector
{
	public static decimal? MaxForCurrentColumn(OpenPosition position, EvaluationContext ctx) => null;
}
