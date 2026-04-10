namespace WebullAnalytics;

/// <summary>
/// Centralized "today" for the evaluation pipeline. Defaults to the real date.
/// Set via --date on ResearchCommand to simulate a future (or past) trading day.
/// </summary>
internal static class EvaluationDate
{
	private static DateTime? _override;

	internal static DateTime Today => _override ?? DateTime.Today;

	internal static DateTime Now => _override.HasValue ? _override.Value + DateTime.Now.TimeOfDay : DateTime.Now;

	internal static void Set(DateTime date) => _override = date.Date;
}
