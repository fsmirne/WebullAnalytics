using WebullAnalytics.AI;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Read-only cache over the per-contract option CSVs that <c>wa ai history --options</c>
/// writes to <c>data/options/&lt;root&gt;/&lt;expiry&gt;/&lt;occ&gt;.csv</c>. The backtest consults this
/// cache before falling back to Black-Scholes pricing — when a real bar exists for the leg's minute,
/// it's the most accurate source of mid + IV we have.
///
/// <para>Lookup model: keyed by (OCC symbol, minute timestamp in UTC). Files are loaded lazily on
/// first access per contract and then held in memory for the rest of the run; a typical backtest
/// touches the same 100-500 contracts repeatedly, and keeping ~390 bars × 8 fields × decimal each
/// in memory per contract is well under 10 MB even at thousand-contract scale.</para>
///
/// <para>Negative caching: contracts that don't have a CSV on disk are remembered as "missed" so
/// the backtest doesn't re-check the filesystem on every leg pricing. The cache is fully read-only
/// and safe for concurrent reads — production wires this from <see cref="BacktestQuoteSource"/>
/// which may price thousands of legs in a single tight loop.</para></summary>
internal sealed class HistoricalOptionBarCache
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private readonly string _dataDir;
	private readonly Dictionary<string, IReadOnlyDictionary<long, OptionMinuteBar>?> _byOcc =
		new(StringComparer.OrdinalIgnoreCase);
	// expiry-dir → captured OCC filenames (one filesystem scan per expiry dir, then memoized).
	private readonly Dictionary<string, IReadOnlyList<string>> _occsByExpiryDir =
		new(StringComparer.OrdinalIgnoreCase);
	// root (upper) → sorted captured expiry dates on disk (one root-dir scan per root, then memoized).
	private readonly Dictionary<string, IReadOnlyList<DateTime>> _expiriesByRoot =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly object _lock = new();

	public HistoricalOptionBarCache(string? dataDir = null)
	{
		_dataDir = dataDir ?? Program.ResolvePath("data/options");
	}

	/// <summary>Returns the bar at-or-after the given UTC minute for <paramref name="occ"/>, walking
	/// forward up to <see cref="LookupWindowMinutes"/> minutes. Returns null if the contract has no
	/// CSV on disk or no bar exists within the window. The <paramref name="minuteUtc"/> is normalized
	/// to whole-minute precision before the lookup.
	///
	/// <para>The at-or-after walk handles Webull's "first RTH bar is 09:31 ET" quirk — when callers
	/// look up "the open" at 09:30:00 UTC equivalent, the contract may not have a print exactly at
	/// the bell, and the first available bar is 09:31 or later. Without the walk, every open-pass
	/// lookup would miss for actively-traded contracts. The 5-minute cap means "if the contract
	/// didn't trade in the first 5 minutes of the session, treat it as no data" — the synthetic
	/// fallback handles the illiquid case correctly.</para></summary>
	public OptionMinuteBar? GetBar(string occ, DateTimeOffset minuteUtc)
	{
		var byTs = GetOrLoad(occ);
		if (byTs == null) return null;
		var sec = minuteUtc.ToUnixTimeSeconds();
		sec -= sec % 60; // align to minute boundary
		for (var step = 0; step <= LookupWindowMinutes; step++)
		{
			if (byTs.TryGetValue(sec + step * 60, out var bar)) return bar;
		}
		return null;
	}

	/// <summary>How many minutes after the target to walk before giving up. Sized to capture the
	/// "first print of the RTH session" semantics for SPXW: Webull's first bar is typically 09:31 ET
	/// (not 09:30), and worst-case observed in production was ~2 minutes into the session. Five
	/// minutes is generous headroom; beyond that, the contract was probably untraded and the
	/// synthetic fallback is the right answer.</summary>
	internal const int LookupWindowMinutes = 5;

	/// <summary>Captured quote points (strike, time-midpoint price, IV-fraction-if-present) for all
	/// contracts of root+expiry+right with a bar at-or-after the minute, sorted by strike. Price is
	/// the bar's time-midpoint (Open+Close)/2 so the surface IV reads the same moment-in-minute the
	/// direct captured-leg path does — both back-solve from the same price point, otherwise the
	/// surface would carry an Open-anchored bias the leg-pricing path no longer has. IV in the CSV is
	/// a percentage (15.74 → 0.1574 here); it's null for massive-sourced (expired) contracts, which
	/// is why the caller back-solves IV from the price.</summary>
	public IReadOnlyList<(decimal Strike, decimal Price, decimal? IvFraction)> GetCapturedQuotePoints(string root, DateTime expiry, string callPut, DateTimeOffset minuteUtc)
	{
		var dir = Path.Combine(_dataDir, root.ToUpperInvariant(), expiry.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
		IReadOnlyList<string> occs;
		lock (_lock)
		{
			if (!_occsByExpiryDir.TryGetValue(dir, out occs!))
			{
				occs = Directory.Exists(dir)
					? Directory.EnumerateFiles(dir, "*.csv").Select(Path.GetFileNameWithoutExtension).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
					: (IReadOnlyList<string>)Array.Empty<string>();
				_occsByExpiryDir[dir] = occs;
			}
		}

		var points = new List<(decimal, decimal, decimal?)>();
		foreach (var occ in occs)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed?.CallPut == null || !string.Equals(parsed.CallPut, callPut, StringComparison.OrdinalIgnoreCase)) continue;
			var bar = GetBar(occ, minuteUtc);
			if (bar == null || bar.Open <= 0m || bar.Close <= 0m) continue;
			var mid = (bar.Open + bar.Close) / 2m;
			decimal? ivFrac = bar.ImpliedVolatility is { } ivPct && ivPct > 0m ? ivPct / 100m : null;
			points.Add((parsed.Strike, mid, ivFrac));
		}
		points.Sort((a, b) => a.Item1.CompareTo(b.Item1));
		return points;
	}

	/// <summary>Diagnostic probe for cross-expiry recoverability: when a leg's OWN expiry has no captured
	/// strikes at the minute (the parametric VIX-smile fallback fired), which side(s) of the target expiry
	/// have a NEARBY expiry (same root+right, within ±<paramref name="maxExpiryDayGap"/> days) carrying at
	/// least one captured strike at that minute? Anchors on BOTH sides → genuine total-variance
	/// interpolation; one side only → extrapolation across the gap (far less reliable); neither → the whole
	/// local term structure was untraded that minute and no interpolation scheme can recover it. Off the
	/// hot path — called once per VIX-fallback leg in the post-run provenance pass.</summary>
	public (bool Below, bool Above) NeighborExpiryAnchors(string root, DateTime targetExpiry, string callPut, DateTimeOffset minuteUtc, int maxExpiryDayGap)
	{
		bool below = false, above = false;
		foreach (var exp in ExpiriesForRoot(root))
		{
			var gap = (exp - targetExpiry.Date).Days;
			if (gap == 0 || Math.Abs(gap) > maxExpiryDayGap) continue;
			if (below && gap < 0) continue; // already found a closer/earlier anchor on this side
			if (above && gap > 0) continue;
			if (GetCapturedQuotePoints(root, exp, callPut, minuteUtc).Count == 0) continue;
			if (gap < 0) below = true; else above = true;
			if (below && above) break;
		}
		return (below, above);
	}

	/// <summary>Captured expiry dates within ±<paramref name="maxGapDays"/> calendar days of
	/// <paramref name="targetExpiry"/> (excluding the target itself), sorted ascending. Used by the
	/// cross-expiry IV interpolation to find the bracketing neighbor expiries.</summary>
	public IReadOnlyList<DateTime> NeighborExpiriesWithin(string root, DateTime targetExpiry, int maxGapDays)
	{
		var result = new List<DateTime>();
		foreach (var exp in ExpiriesForRoot(root))
		{
			var gap = (exp - targetExpiry.Date).Days;
			if (gap == 0 || Math.Abs(gap) > maxGapDays) continue;
			result.Add(exp);
		}
		return result;
	}

	/// <summary>Sorted list of expiry dates with a captured-contract directory on disk for <paramref name="root"/>.
	/// One <c>data/options/&lt;root&gt;/</c> directory scan per root, then memoized. Non-date entries (e.g.
	/// <c>sealed.json</c>) are skipped.</summary>
	private IReadOnlyList<DateTime> ExpiriesForRoot(string root)
	{
		lock (_lock)
		{
			if (_expiriesByRoot.TryGetValue(root, out var cached)) return cached;
			var rootDir = Path.Combine(_dataDir, root.ToUpperInvariant());
			var list = new List<DateTime>();
			if (Directory.Exists(rootDir))
				foreach (var dir in Directory.EnumerateDirectories(rootDir))
				{
					if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
						list.Add(d.Date);
				}
			list.Sort();
			_expiriesByRoot[root] = list;
			return list;
		}
	}

	/// <summary>Diagnostic: true when <paramref name="occ"/> has at least one captured bar on the
	/// ET trading day <paramref name="dateEt"/>. Used by the pricing-provenance report to separate fills
	/// backed by a real captured print from those that fell through to the synthetic Black-Scholes model.
	/// Read-only and off the hot path — called once per fill leg in the post-run summary, not during the
	/// per-minute simulation.</summary>
	public bool HasBarOnDate(string occ, DateTime dateEt)
	{
		var byTs = GetOrLoad(occ);
		if (byTs == null) return false;
		foreach (var sec in byTs.Keys)
		{
			var et = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime, NyTz);
			if (et.Date == dateEt.Date) return true;
		}
		return false;
	}

	/// <summary>True when <paramref name="occ"/> has a captured CSV with at least one bar on ANY day — i.e.
	/// the contract really existed and traded at some point. Distinguishes a real strike that merely had no
	/// bar on a given day (illiquid) from a phantom strike that was never captured at all (e.g. a $1 grid
	/// strike a uniform-grid enumerator invented). Off the hot path — used only by the provenance report.</summary>
	public bool HasAnyBar(string occ) => GetOrLoad(occ) is { Count: > 0 };

	private IReadOnlyDictionary<long, OptionMinuteBar>? GetOrLoad(string occ)
	{
		lock (_lock)
		{
			if (_byOcc.TryGetValue(occ, out var existing)) return existing;
		}

		// Load outside the lock so concurrent loads of different contracts don't serialize on disk.
		// We accept the rare double-read race here (two threads loading the same OCC); the loser's
		// dict is just discarded when the winner writes first. Both produce the same data.
		var loaded = LoadFromDisk(occ);

		lock (_lock)
		{
			// Re-check in case another thread populated it while we were reading disk.
			if (_byOcc.TryGetValue(occ, out var existing)) return existing;
			_byOcc[occ] = loaded;
			return loaded;
		}
	}

	private IReadOnlyDictionary<long, OptionMinuteBar>? LoadFromDisk(string occ)
	{
		var parsed = ParsingHelpers.ParseOptionSymbol(occ);
		if (parsed == null) return null;
		var expiryDir = parsed.ExpiryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
		var path = Path.Combine(_dataDir, parsed.Root.ToUpperInvariant(), expiryDir, occ + ".csv");
		if (!File.Exists(path)) return null;

		var bars = AIHistoryOptionsBackfill.ReadOptionCsv(path);
		if (bars.Count == 0) return null;

		var byTs = new Dictionary<long, OptionMinuteBar>(bars.Count);
		foreach (var b in bars)
		{
			var sec = b.Timestamp.ToUnixTimeSeconds();
			sec -= sec % 60; // normalize
			byTs[sec] = b;
		}
		return byTs;
	}
}
