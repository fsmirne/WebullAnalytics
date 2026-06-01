using System.Runtime.CompilerServices;
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

	private StrikeLadder(decimal[] strikes, bool chainPresent)
	{
		_strikes = strikes;
		ChainPresent = chainPresent;
	}

	/// <summary>No chain was supplied at all (tests / <c>--theoretical</c>). Distinct from a chain that WAS
	/// supplied but listed no tradeable strikes for this (expiry, side) — see <see cref="ChainPresent"/>.</summary>
	public static readonly StrikeLadder Empty = new(Array.Empty<decimal>(), chainPresent: false);

	public bool IsEmpty => _strikes.Length == 0;

	/// <summary>True when a chain WAS supplied to <see cref="Build"/> (even if it listed no tradeable strikes
	/// for this expiry/side). When a chain is present the ladder is AUTHORITATIVE: callers emit only its
	/// strikes and never fall back to the uniform strikeStep grid — an empty-but-chain-present ladder means
	/// "nothing tradeable here," not "guess a grid." Only when no chain is present at all do callers fall
	/// back to the uniform grid. This is what keeps the backtest from filling phantom strikes that the
	/// captured chain never recorded.</summary>
	public bool ChainPresent { get; }

	private static readonly StrikeLadder EmptyChainPresent = new(Array.Empty<decimal>(), chainPresent: true);

	// One parsed chain index per quotes object (i.e. per evaluation tick): the O(chain) parse happens ONCE
	// and is shared across the dozens of (expiry, side) ladders the opener builds per evaluation. Re-parsing
	// thousands of quote keys on every Build call was the dominant backtest cost once the captured chain grew
	// large. Auto-evicted when the quotes object is collected (next tick brings a fresh dictionary).
	private static readonly ConditionalWeakTable<object, ChainIndex> _indexCache = new();

	/// <summary>Builds the ladder for one (ticker, expiry, callPut) from the chain. The preference is strikes
	/// that QUOTE (bid & ask) → strikes with open interest / volume; SPX-family chains list illiquid strikes
	/// (e.g. XSP $1 strikes) that never quote or trade, and picking those is the live/backtest failure. With
	/// neither signal the ladder is empty-but-chain-present, so the enumerator emits nothing rather than
	/// inventing a uniform grid of phantom strikes. <paramref name="callPut"/> = null builds a combined
	/// both-sides ladder (iron-condor body width). The chain is parsed once per quotes object; this is an
	/// O(1) lookup thereafter.</summary>
	public static StrikeLadder Build(string ticker, DateTime expiry, string? callPut, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (quotes == null || quotes.Count == 0) return Empty;
		var index = _indexCache.GetValue(quotes, static q => ChainIndex.Build((IReadOnlyDictionary<string, OptionContractQuote>)q));
		return index.GetLadder(ticker, expiry, callPut);
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

	/// <summary>The whole chain parsed once into per-(root, expiry, side) sorted strike arrays — the quoted
	/// set (bid&ask) and the tradeable set (open interest/volume). One instance per quotes object; ladder
	/// lookups are an O(1) dictionary hit plus a shared-array wrap.</summary>
	private sealed class ChainIndex
	{
		private readonly Dictionary<(string Root, DateTime Expiry, string Side), decimal[]> _quoted;
		private readonly Dictionary<(string Root, DateTime Expiry, string Side), decimal[]> _tradeable;

		private ChainIndex(Dictionary<(string, DateTime, string), decimal[]> quoted, Dictionary<(string, DateTime, string), decimal[]> tradeable)
		{
			_quoted = quoted;
			_tradeable = tradeable;
		}

		public static ChainIndex Build(IReadOnlyDictionary<string, OptionContractQuote> quotes)
		{
			var quoted = new Dictionary<(string, DateTime, string), SortedSet<decimal>>();
			var tradeable = new Dictionary<(string, DateTime, string), SortedSet<decimal>>();
			foreach (var kv in quotes)
			{
				var p = ParsingHelpers.ParseOptionSymbol(kv.Key);
				if (p?.CallPut == null) continue;
				var key = (p.Root.ToUpperInvariant(), p.ExpiryDate.Date, p.CallPut.ToUpperInvariant());
				var q = kv.Value;
				if (q.Bid is > 0m && q.Ask is > 0m) Add(quoted, key, p.Strike);
				if (q.OpenInterest is > 0 || q.Volume is > 0) Add(tradeable, key, p.Strike);
			}
			return new ChainIndex(Freeze(quoted), Freeze(tradeable));
		}

		private static void Add(Dictionary<(string, DateTime, string), SortedSet<decimal>> map, (string, DateTime, string) key, decimal strike)
		{
			if (!map.TryGetValue(key, out var set)) map[key] = set = new SortedSet<decimal>();
			set.Add(strike);
		}

		private static Dictionary<(string, DateTime, string), decimal[]> Freeze(Dictionary<(string, DateTime, string), SortedSet<decimal>> map)
		{
			var result = new Dictionary<(string, DateTime, string), decimal[]>(map.Count);
			foreach (var (k, v) in map) result[k] = v.ToArray();
			return result;
		}

		public StrikeLadder GetLadder(string ticker, DateTime expiry, string? callPut)
		{
			var root = ticker.ToUpperInvariant();
			var exp = expiry.Date;
			decimal[] chosen;
			if (callPut != null)
			{
				var side = callPut.ToUpperInvariant();
				chosen = Lookup(_quoted, root, exp, side);
				if (chosen.Length == 0) chosen = Lookup(_tradeable, root, exp, side);
			}
			else
			{
				// Combined both-sides ladder (iron-condor body width): union of C+P quoted, else of C+P
				// tradeable — mirrors the per-side quoted→tradeable preference across both rights.
				chosen = Union(Lookup(_quoted, root, exp, "C"), Lookup(_quoted, root, exp, "P"));
				if (chosen.Length == 0) chosen = Union(Lookup(_tradeable, root, exp, "C"), Lookup(_tradeable, root, exp, "P"));
			}
			return chosen.Length == 0 ? EmptyChainPresent : new StrikeLadder(chosen, chainPresent: true);
		}

		private static decimal[] Lookup(Dictionary<(string, DateTime, string), decimal[]> map, string root, DateTime exp, string side)
			=> map.TryGetValue((root, exp, side), out var arr) ? arr : Array.Empty<decimal>();

		private static decimal[] Union(decimal[] a, decimal[] b)
		{
			if (a.Length == 0) return b;
			if (b.Length == 0) return a;
			var set = new SortedSet<decimal>(a);
			foreach (var x in b) set.Add(x);
			return set.ToArray();
		}
	}
}
