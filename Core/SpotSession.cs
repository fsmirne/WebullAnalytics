namespace WebullAnalytics;

/// <summary>Which live session's underlying price a run projects at: <c>Regular</c> keeps the book's own spot, <c>Extended</c>
/// (<c>--extended</c>) the latest pre/post-market price, <c>Overnight</c> (<c>--overnight</c>) the latest overnight-session
/// (20:00–04:00 ET) price. Explicit <c>--spot</c> entries win over either.</summary>
internal enum SpotSession { Regular, Extended, Overnight }

internal static class SpotSessionOptions
{
	/// <summary>The session two flags select, or an error when both are set.</summary>
	internal static (SpotSession Session, string? Error) From(bool extended, bool overnight) =>
		(extended, overnight) switch
		{
			(true, true) => (SpotSession.Regular, "--extended and --overnight are mutually exclusive"),
			(true, false) => (SpotSession.Extended, null),
			(false, true) => (SpotSession.Overnight, null),
			_ => (SpotSession.Regular, null),
		};

	/// <summary>The CLI flag that selects <paramref name="session"/>; null for <see cref="SpotSession.Regular"/>.</summary>
	internal static string? Flag(this SpotSession session) => session switch
	{
		SpotSession.Extended => "--extended",
		SpotSession.Overnight => "--overnight",
		_ => null,
	};
}
