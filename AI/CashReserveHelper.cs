namespace WebullAnalytics.AI;

/// <summary>
/// Funding check for proposed rolls/closes. Pure function — no I/O, no side effects.
/// Computes whether a proposal is fundable given current cash and a required reserve.
/// </summary>
internal static class CashReserveHelper
{
	/// <summary>
	/// Returns the reserve amount for a given mode + value + account value.
	/// "percent" mode: reserve = accountValue * value/100.
	/// "absolute" mode: reserve = value (in dollars).
	/// </summary>
	internal static decimal ComputeReserve(string mode, decimal value, decimal accountValue) =>
		mode switch
		{
			"percent" => accountValue * (value / 100m),
			"absolute" => value,
			_ => throw new ArgumentException($"Unknown cash-reserve mode: '{mode}'")
		};

	/// <summary>
	/// Result of a funding check.
	/// </summary>
	/// <param name="FreeAfter">Cash remaining after applying the proposed credit/debit and honoring the reserve.</param>
	/// <param name="RequiredFree">Reserve amount that must stay free.</param>
	/// <param name="Blocked">True when FreeAfter would be negative (i.e., proposal would violate the reserve).</param>
	/// <param name="Detail">Human-readable summary: "free $Y, requires $X".</param>
	internal readonly record struct FundingCheck(decimal FreeAfter, decimal RequiredFree, bool Blocked, string Detail);

	/// <summary>
	/// Checks whether a proposal with the given net debit (negative = debit paid, positive = credit received)
	/// can be executed without violating the configured reserve.
	/// </summary>
	/// <param name="netDebit">Negative for debit paid (cash out); positive for credit received (cash in).</param>
	/// <param name="currentCash">Current free cash.</param>
	/// <param name="accountValue">Total account value including positions marked to market.</param>
	/// <param name="reserveMode">"percent" or "absolute".</param>
	/// <param name="reserveValue">Reserve magnitude (percent or dollars).</param>
	internal static FundingCheck Check(decimal netDebit, decimal currentCash, decimal accountValue, string reserveMode, decimal reserveValue)
	{
		var reserve = ComputeReserve(reserveMode, reserveValue, accountValue);
		// netDebit is negative for debit (cash out); adding it reduces cash.
		var cashAfter = currentCash + netDebit;
		var freeAfter = cashAfter - reserve;
		var blocked = freeAfter < 0m;
		var detail = $"free ${Math.Max(0m, cashAfter):N2}, requires ${reserve:N2}";
		return new FundingCheck(freeAfter, reserve, blocked, detail);
	}
}
