namespace WebullAnalytics;

/// <summary>
/// Centralized "today" for the evaluation pipeline. Defaults to the real date.
/// Set via --date on AnalyzeCommand to simulate a future (or past) trading day.
/// </summary>
internal static class EvaluationDate
{
	private static DateTime? _override;

	internal static DateTime Today => _override ?? DateTime.Today;

	internal static DateTime Now => _override.HasValue ? _override.Value + DateTime.Now.TimeOfDay : DateTime.Now;

	/// <summary>True when a specific evaluation date was pinned via --date (a historical/future simulation),
	/// so wall-clock "now" is not the moment the loaded quotes were struck. Lets pricing anchor a back-solve
	/// at a session convention for those runs instead of the real clock.</summary>
	internal static bool IsOverridden => _override.HasValue;

	internal static void Set(DateTime date) => _override = date.Date;

	/// <summary>Clears any override so subsequent reads return the wall-clock date. Intended for tests
	/// that pin the evaluation date to keep behavior reproducible across days — pair with a try/finally
	/// (or IDisposable on the test class) to avoid leaking state to other tests.</summary>
	internal static void Reset() => _override = null;
}
