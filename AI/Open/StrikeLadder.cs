using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>The strikes actually listed for one (ticker, expiry, call/put) in the chain, sorted ascending.
/// Strike spacing on SPX-family chains varies by moneyness AND expiry ($1 near-the-money front-week, $5 in
/// the wings and back-month, $10 deep), so a single uniform <c>indicators.strikeStep</c> grid generates
/// strikes the venue never lists — those legs come back with no bid/ask and the whole candidate is dropped.
/// The ladder lets the enumerator pick only real strikes and express spread widths as a COUNT of strikes
/// along the actual ladder rather than a fixed dollar step. Built from the chain quotes the opener already
/// fetches; <see cref="IsEmpty"/> when no chain is supplied (tests / <c>--theoretical</c>), in which case
/// callers fall back to the uniform step grid in <see cref="CandidateEnumerator"/>.</summary>
internal sealed class StrikeLadder
{
	private readonly decimal[] _strikes; // sorted ascending, distinct

	private StrikeLadder(decimal[] strikes) => _strikes = strikes;

	public static readonly StrikeLadder Empty = new(Array.Empty<decimal>());

	public bool IsEmpty => _strikes.Length == 0;

	/// <summary>Builds the ladder for one (ticker, expiry, callPut) from the chain quote keys (OCC symbols).
	/// A strike is "listed" if the chain returned a contract for it — existence, not live bid/ask, because
	/// the opener's Phase-B pass fetches bid/ask for the legs it actually selects afterward (a far-DTE strike
	/// often comes back symbol-only from the chain probe and only gets priced once chosen).</summary>
	public static StrikeLadder Build(string ticker, DateTime expiry, string? callPut, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (quotes == null || quotes.Count == 0) return Empty;
		var quoted = new SortedSet<decimal>();
		var tradeable = new SortedSet<decimal>();
		var listed = new SortedSet<decimal>();
		foreach (var kv in quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(kv.Key);
			if (p == null) continue;
			if (!string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (p.ExpiryDate.Date != expiry.Date) continue;
			// callPut == null builds a combined both-sides ladder (used to count strikes across spot, e.g.
			// the iron-condor body width); otherwise restrict to the given side.
			if (callPut != null && !string.Equals(p.CallPut, callPut, StringComparison.OrdinalIgnoreCase)) continue;
			var q = kv.Value;
			listed.Add(p.Strike);
			if (q.Bid is > 0m && q.Ask is > 0m) quoted.Add(p.Strike);
			if (q.OpenInterest is > 0 || q.Volume is > 0) tradeable.Add(p.Strike);
		}
		// Preference: strikes that QUOTE right now (bid & ask) → strikes with open interest / volume. SPX-
		// family chains list illiquid near-the-money strikes (e.g. XSP $1 strikes) that never carry a bid/ask
		// AND never trade; picking those is the live failure — the leg can't be priced and the candidate
		// drops. When the chain reveals NEITHER a quote NOR open interest for an expiry (XSP future expiries
		// come back fully symbol-only — no bid/ask, no OI), there is no liquidity signal to build a ladder
		// from, so we return Empty and the enumerator falls back to the uniform strikeStep grid. We do NOT
		// fall back to bare existence: that reselects the dead $1 strikes and reproduces the failure.
		var chosen = quoted.Count > 0 ? quoted : tradeable;
		return chosen.Count == 0 ? Empty : new StrikeLadder(chosen.ToArray());
	}

	/// <summary>Listed strikes strictly below <paramref name="spot"/>, nearest-first, up to <paramref name="count"/>.</summary>
	public IEnumerable<decimal> Below(decimal spot, int count)
	{
		var n = 0;
		for (var i = _strikes.Length - 1; i >= 0 && n < count; i--)
			if (_strikes[i] < spot) { yield return _strikes[i]; n++; }
	}

	/// <summary>Listed strikes strictly above <paramref name="spot"/>, nearest-first, up to <paramref name="count"/>.</summary>
	public IEnumerable<decimal> Above(decimal spot, int count)
	{
		var n = 0;
		for (var i = 0; i < _strikes.Length && n < count; i++)
			if (_strikes[i] > spot) { yield return _strikes[i]; n++; }
	}

	/// <summary>Listed strikes bracketing <paramref name="spot"/> (up to <paramref name="count"/> each side), ascending.</summary>
	public IEnumerable<decimal> Around(decimal spot, int count)
		=> Below(spot, count).Concat(Above(spot, count)).Distinct().OrderBy(s => s).ToList();

	/// <summary>The strike <paramref name="steps"/> positions from <paramref name="from"/> along the ladder
	/// (positive = up, negative = down). <paramref name="from"/> is snapped to the nearest listed strike
	/// first. Returns null when the offset runs off either end of the ladder — the caller drops that
	/// candidate, exactly as it would live when the wing strike isn't listed.</summary>
	public decimal? Offset(decimal from, int steps)
	{
		if (_strikes.Length == 0) return null;
		var idx = NearestIndex(from);
		var target = idx + steps;
		if (target < 0 || target >= _strikes.Length) return null;
		return _strikes[target];
	}

	/// <summary>Count of listed strikes between <paramref name="a"/> and <paramref name="b"/> — the absolute
	/// index difference after snapping each to the nearest listed strike. Null when the ladder is empty. Used
	/// for iron-condor body width expressed as a count of strikes along the real ladder.</summary>
	public int? StepsBetween(decimal a, decimal b)
	{
		if (_strikes.Length == 0) return null;
		return Math.Abs(NearestIndex(a) - NearestIndex(b));
	}

	private int NearestIndex(decimal target)
	{
		var idx = 0;
		var bestDist = Math.Abs(target - _strikes[0]);
		for (var i = 1; i < _strikes.Length; i++)
		{
			var d = Math.Abs(target - _strikes[i]);
			if (d < bestDist) { bestDist = d; idx = i; }
		}
		return idx;
	}
}
