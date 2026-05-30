namespace WebullAnalytics.AI;

/// <summary>Settlement-style classification for option roots. Cash-settled index options
/// (SPX/SPXW/NDX/XSP/RUT/DJX/VIX) are European: they cannot be exercised early and settle to cash at
/// expiry, so early-assignment risk is structurally zero. Equity/ETF options are American-style and
/// physically settled, so an in-the-money short leg can be assigned at any time. This is the single
/// source of truth for that distinction across the engine (scoring) and the backtest (fees, expiry).</summary>
internal static class OptionSettlement
{
	/// <summary>Cash-settled, European-exercise index roots traded on this platform. Membership means
	/// "no early assignment is possible and the position settles to cash at expiry."</summary>
	public static readonly IReadOnlySet<string> CashSettledIndexRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"SPX", "SPXW", "NDX", "XSP", "RUT", "DJX", "VIX"
	};

	/// <summary>True when <paramref name="ticker"/> is a cash-settled, European-exercise index option
	/// root. Such options carry no early-assignment risk — scoring must not penalize their short legs
	/// for assignment, and the simulator settles them to intrinsic cash at expiry.</summary>
	public static bool IsCashSettledIndex(string? ticker) =>
		!string.IsNullOrEmpty(ticker) && CashSettledIndexRoots.Contains(ticker);
}
