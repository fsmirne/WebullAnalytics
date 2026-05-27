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
	private readonly string _dataDir;
	private readonly Dictionary<string, IReadOnlyDictionary<long, OptionMinuteBar>?> _byOcc =
		new(StringComparer.OrdinalIgnoreCase);
	// expiry-dir → captured OCC filenames (one filesystem scan per expiry dir, then memoized).
	private readonly Dictionary<string, IReadOnlyList<string>> _occsByExpiryDir =
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

	/// <summary>Captured quote points (strike, open price, IV-fraction-if-present) for all contracts of
	/// root+expiry+right with a bar at-or-after the minute, sorted by strike. IV in the CSV is a
	/// percentage (15.74 → 0.1574 here); it's null for massive-sourced (expired) contracts, which is
	/// why the caller back-solves IV from the open price. Used to build the real-IV surface that keeps
	/// a spread's missing/out-of-band legs on the same basis as its captured legs — pricing them with
	/// VIX1D instead opens a ~10-point IV gap that fabricates spread edge.</summary>
	public IReadOnlyList<(decimal Strike, decimal Open, decimal? IvFraction)> GetCapturedQuotePoints(string root, DateTime expiry, string callPut, DateTimeOffset minuteUtc)
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
			if (bar == null || bar.Open <= 0m) continue;
			decimal? ivFrac = bar.ImpliedVolatility is { } ivPct && ivPct > 0m ? ivPct / 100m : null;
			points.Add((parsed.Strike, bar.Open, ivFrac));
		}
		points.Sort((a, b) => a.Item1.CompareTo(b.Item1));
		return points;
	}

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
