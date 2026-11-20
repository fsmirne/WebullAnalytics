namespace WebullAnalytics.Positions;

/// <summary>
/// Shared helper for classifying 2-leg roll shapes against Webull's combo order rules:
///   • Same-strike calendar (same strike, distinct expiries) → supported as 1 combo order.
///   • Same-expiry vertical (same expiry, distinct strikes) → supported as 1 combo order.
///   • Diagonal (distinct strike AND distinct expiry) → NOT supported as a combo; must split into 2 single-leg orders.
/// Note: 4-leg resets (close existing + open new) are split by the emitters themselves — Webull
/// only accepts iron condor/butterfly as 4-leg combos, so resets always emit as two 2-leg combos
/// (close half + open half) based on EmitReset's positional contract, not on shape detection.
/// </summary>
internal static class RollShape
{
	/// <summary>
	/// Returns true iff `occSymbols` contains exactly two OCC option symbols that share one
	/// strike and have distinct expiries. Returns false for any other shape, including single
	/// leg, 3+ legs, equity legs, unparseable symbols, same-expiry, and different-strike inputs.
	/// </summary>
	internal static bool IsSameStrikeCalendar(IEnumerable<string> occSymbols)
	{
		var parsed = ParsePair(occSymbols);
		return parsed != null
			&& parsed.Value.A.Strike == parsed.Value.B.Strike
			&& parsed.Value.A.ExpiryDate != parsed.Value.B.ExpiryDate;
	}

	/// <summary>
	/// Returns true iff `occSymbols` contains exactly two OCC option symbols that share one
	/// expiry and have distinct strikes (a vertical roll).
	/// </summary>
	internal static bool IsSameExpiryVertical(IEnumerable<string> occSymbols)
	{
		var parsed = ParsePair(occSymbols);
		return parsed != null
			&& parsed.Value.A.ExpiryDate == parsed.Value.B.ExpiryDate
			&& parsed.Value.A.Strike != parsed.Value.B.Strike;
	}

	/// <summary>
	/// Returns true iff Webull's combo engine accepts the 2-leg roll as a single combo order:
	/// same-expiry verticals and same-strike calendars are supported; diagonals (different strike
	/// AND different expiry) are not and must be split into two single-leg orders.
	/// </summary>
	internal static bool SupportsComboRoll(IEnumerable<string> occSymbols) =>
		IsSameStrikeCalendar(occSymbols) || IsSameExpiryVertical(occSymbols);

	private static (OptionParsed A, OptionParsed B)? ParsePair(IEnumerable<string> occSymbols)
	{
		var parsed = new List<OptionParsed>(2);
		foreach (var sym in occSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null) return null;
			parsed.Add(p);
			if (parsed.Count > 2) return null;
		}
		return parsed.Count == 2 ? (parsed[0], parsed[1]) : null;
	}
}
