namespace WebullAnalytics;

/// <summary>
/// Centralized evaluation instant for the pricing pipeline (ET wall clock). Unset = live: the real clock.
/// Pinned by --date (<see cref="AsOf"/>), a fill's instant, or a replay, to evaluate at a past or future moment.
/// </summary>
internal static class EvaluationDate
{
	private static DateTime? _instant;

	/// <summary>The pinned instant (ET), or null when evaluation is live.</summary>
	internal static DateTime? Instant => _instant;

	internal static DateTime Today => _instant?.Date ?? DateTime.Today;

	/// <summary>True when an evaluation instant was pinned (a historical or forward simulation), so wall-clock "now" is not the moment
	/// the loaded quotes were struck.</summary>
	internal static bool IsOverridden => _instant.HasValue;

	/// <summary>True when the pinned instant has already happened: its market comes from the stores, not a live fetch.</summary>
	internal static bool IsPast => _instant is { } instant && instant < DateTime.Now;

	internal static void Set(DateTime instantEt) => _instant = instantEt;

	/// <summary>Clears any pin so subsequent reads return the wall clock. Intended for tests that pin the evaluation instant to keep
	/// behavior reproducible across days — pair with a try/finally (or IDisposable on the test class) to avoid leaking state to other tests.</summary>
	internal static void Reset() => _instant = null;
}
