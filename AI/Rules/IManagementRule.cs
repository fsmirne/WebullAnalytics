namespace WebullAnalytics.AI.Rules;

/// <summary>
/// A management rule. Each rule is stateless: all state comes through the EvaluationContext.
/// Rules are evaluated per-position in priority order; the first match wins for that position in that tick.
/// </summary>
internal interface IManagementRule
{
	/// <summary>Unique rule name; used in ManagementProposal.Rule and for config lookup.</summary>
	string Name { get; }

	/// <summary>Priority for ordering (1 = highest). Ties are broken by Name alphabetical.</summary>
	int Priority { get; }

	/// <summary>Evaluate this rule against the given position. Returns null when the rule does not fire.</summary>
	ManagementProposal? Evaluate(OpenPosition position, EvaluationContext context);
}
