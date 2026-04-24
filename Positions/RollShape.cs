namespace WebullAnalytics.Positions;

/// <summary>
/// Shared helper for detecting whether a 2-leg roll is a same-strike calendar — the one 2-leg roll
/// shape Webull's combo engine accepts when it includes a position reversal. Used by the
/// `wa analyze position` and AI `ProposalSink` reproduction-command emitters to decide whether to
/// emit one combo `wa trade place` line or two separate single-leg lines.
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
		var parsed = new List<OptionParsed>(2);
		foreach (var sym in occSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null) return false;
			parsed.Add(p);
			if (parsed.Count > 2) return false;
		}
		if (parsed.Count != 2) return false;
		if (parsed[0].Strike != parsed[1].Strike) return false;
		if (parsed[0].ExpiryDate == parsed[1].ExpiryDate) return false;
		return true;
	}
}
