namespace WebullAnalytics.AI;

/// <summary>
/// Intermediate type produced by CandidateEnumerator. Contains only structural information —
/// no quotes, no scoring. Consumed by CandidateScorer which runs the BS math against current quotes.
/// </summary>
/// <param name="Ticker">Underlying symbol.</param>
/// <param name="StructureKind">Which structure family.</param>
/// <param name="Legs">Opening legs (buy/sell × OCC × qty=1). Scorer multiplies out per-contract numbers.</param>
/// <param name="TargetExpiry">The date used as the "target" for scoring: short-leg expiry for calendars/diagonals and short verticals; the leg's own expiry for long call/put.</param>
internal sealed record CandidateSkeleton(
    string Ticker,
    OpenStructureKind StructureKind,
    IReadOnlyList<ProposalLeg> Legs,
    DateTime TargetExpiry
);
