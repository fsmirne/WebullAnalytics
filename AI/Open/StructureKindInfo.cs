namespace WebullAnalytics.AI;

/// <summary>
/// Central per-<see cref="OpenStructureKind"/> property table. Every structure attribute that a dispatch
/// site needs — order count, how it splits into broker orders, calendar-likeness, credit/debit nature,
/// volatility-fit and directional sign — is defined here ONCE, so adding a new structure means editing a
/// single file instead of hunting down scattered switches (the failure mode that left CalendarVertical
/// mis-scored when it was first added).
///
/// Scope: this table is KIND-ONLY. Classification that needs the actual geometry (strikes, expiries,
/// prices) — debit vs credit verticals, covered vs inverted diagonals, valid vs malformed irons — stays in
/// the two purpose-built classifiers (<see cref="ParsingHelpers.ClassifyStrategyKind"/> from counts and
/// <c>RiskDiagnosticBuilder.ClassifyStructure</c> from geometry). Those answer different questions and
/// legitimately remain separate functions.
/// </summary>
internal static class StructureKindInfo
{
	/// <summary>Structures Webull won't accept as a single 4-leg combo ticket — they go out as two
	/// orders (see <see cref="StructureOrderSplit"/>). Everything else fills as one.</summary>
	public static bool RequiresTwoOrders(OpenStructureKind k) =>
		k is OpenStructureKind.DoubleCalendar or OpenStructureKind.DoubleDiagonal
		  or OpenStructureKind.DiagonalVertical or OpenStructureKind.CalendarVertical;

	/// <summary>Number of independent broker orders required to enter the structure.</summary>
	public static int OrderCount(OpenStructureKind k) => RequiresTwoOrders(k) ? 2 : 1;

	/// <summary>How a two-order structure splits: by expiry (near/far vertical) for the single-sided
	/// diagonal/calendar verticals; by call/put side for the two-sided doubles.</summary>
	public static bool SplitsByExpiry(OpenStructureKind k) =>
		k is OpenStructureKind.DiagonalVertical or OpenStructureKind.CalendarVertical;

	/// <summary>Long-vega, theta-positive structures whose value rests on the residual time value of a
	/// far-dated leg — priced with market-implied IV and tie-broken by DaysToTarget.</summary>
	public static bool IsCalendarLike(OpenStructureKind k) =>
		k is OpenStructureKind.LongCalendar or OpenStructureKind.DoubleCalendar
		  or OpenStructureKind.LongDiagonal or OpenStructureKind.DoubleDiagonal
		  or OpenStructureKind.DiagonalVertical or OpenStructureKind.CalendarVertical;

	/// <summary>Net-credit structures (collect premium up front; defined risk on the wings).</summary>
	public static bool IsCreditStructure(OpenStructureKind k) =>
		k is OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical
		  or OpenStructureKind.IronCondor or OpenStructureKind.IronButterfly;

	/// <summary>+1 short-vol (wants realized below implied), −1 long-vol (wants expansion), 0 neutral.</summary>
	public static int VolatilityFitSign(OpenStructureKind k) => k switch
	{
		OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical or OpenStructureKind.IronButterfly or OpenStructureKind.IronCondor or OpenStructureKind.Condor => 1,
		OpenStructureKind.LongCalendar or OpenStructureKind.DoubleCalendar or OpenStructureKind.LongDiagonal or OpenStructureKind.DoubleDiagonal or OpenStructureKind.LongCall or OpenStructureKind.LongPut or OpenStructureKind.DiagonalVertical or OpenStructureKind.CalendarVertical => -1,
		_ => 0,
	};

	/// <summary>Kind-only directional sign: +1 bullish, −1 bearish, 0 neutral. Strike-dependent kinds
	/// (LongDiagonal, DiagonalVertical) return 0 here and are resolved from the actual strike layout by
	/// <see cref="DirectionalFit.SignFor(CandidateSkeleton)"/>.</summary>
	public static int DirectionalSign(OpenStructureKind k) => k switch
	{
		OpenStructureKind.LongCall or OpenStructureKind.LongCallVertical or OpenStructureKind.ShortPutVertical => 1,
		OpenStructureKind.LongPut or OpenStructureKind.LongPutVertical or OpenStructureKind.ShortCallVertical => -1,
		_ => 0,
	};
}
