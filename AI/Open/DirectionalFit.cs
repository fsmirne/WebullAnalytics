namespace WebullAnalytics.AI;

internal static class DirectionalFit
{
	/// <summary>Returns +1 (bullish fit), −1 (bearish fit), or 0 (neutral) for the given structure.
	/// Diagonals and calendars stay at 0 — their edge is structural (theta capture, adjustment runway,
	/// wide profit zone), not directional bias from strike geometry. Verticals and long calls/puts get
	/// signs because the structure literally exists to bet on direction.</summary>
	public static int SignFor(OpenStructureKind kind) => kind switch
	{
		OpenStructureKind.LongCall => 1,
		OpenStructureKind.ShortPutVertical => 1,
		OpenStructureKind.LongPut => -1,
		OpenStructureKind.ShortCallVertical => -1,
		OpenStructureKind.LongCalendar => 0,
		OpenStructureKind.LongDiagonal => 0,
		_ => 0
	};
}
