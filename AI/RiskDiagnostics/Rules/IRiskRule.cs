namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>A rule evaluates RiskDiagnosticFacts and returns a RiskRuleHit when its condition fires, or
/// null when it does not. Implementations are pure; no I/O, no static mutable state.</summary>
internal interface IRiskRule
{
	/// <summary>Stable machine-readable id, e.g. "directional_exposure".</summary>
	string Id { get; }

	/// <summary>Returns a hit with interpolated message and inputs dict when the rule fires; null otherwise.</summary>
	RiskRuleHit? TryEvaluate(RiskDiagnosticFacts facts);
}
