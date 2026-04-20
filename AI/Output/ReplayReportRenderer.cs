namespace WebullAnalytics.AI.Output;

/// <summary>
/// Formats the replay comparison output. For phase 1 this is a thin wrapper around
/// the console output ProposalSink already produces — it exists as a seam so text-file
/// output (for --output text) can route through here. Phase 2 adds the side-by-side
/// "what the rules proposed vs what the user did" comparison block.
/// </summary>
internal static class ReplayReportRenderer
{
	/// <summary>Renders the final summary block. Called from ReplayRunner after the walk finishes.</summary>
	public static void RenderSummary(IReadOnlyDictionary<string, int> ruleFireCounts, IReadOnlyDictionary<string, int> agreementCounts, int stepsWalked)
	{
		// In phase 1 the ReplayRunner prints its own summary. This method is reserved for
		// --output text path (file writer) which needs plain-text rendering instead of Spectre markup.
	}
}
