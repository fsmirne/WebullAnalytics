using System.Collections.Concurrent;
using System.Text.Json;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Read-only cache over the scraped chain snapshots at
/// <c>data/chain-snapshots/<root>/<date>.jsonl</c>. Each line is a full per-minute chain
/// (<c>underlyingPrice</c> + <c>options[]</c>, each with <c>openInterest</c> and <c>iv</c>).
///
/// <para>Open interest settles overnight, so it is constant across a trading day's minutes — this cache
/// therefore exposes ONE representative RTH record's per-contract (OI, IV), which is exactly what
/// <see cref="Open.CandidateScorer.ComputeGex"/> and the max-pain factor need. The historical option BARS
/// the backtest prices from (<c>data/options/...csv</c>) carry no OI column, so without this cache the GEX /
/// max-pain factors are silently inert in the backtest (gravity collapses, NetGexFraction = 0) even though
/// they are live in production. This cache is what makes them computable on days a snapshot exists.</para>
///
/// <para>Loaded lazily per (root, ET date) and memoized. Absent file → empty map → GEX stays inert for that
/// day (unchanged behaviour, partial coverage is fine).</para></summary>
internal sealed class ChainSnapshotOiCache
{
	private readonly string _dir;
	private readonly ConcurrentDictionary<(string Root, DateTime Date), IReadOnlyDictionary<string, (long Oi, decimal Iv)>> _cache = new();

	public ChainSnapshotOiCache(string? dir = null) => _dir = dir ?? Program.ResolvePath("data/chain-snapshots");

	/// <summary>Per-contract (open interest, implied vol) for <paramref name="root"/> on the ET trading day of
	/// <paramref name="date"/>, keyed by OCC symbol. Empty when no snapshot file exists for that day.</summary>
	public IReadOnlyDictionary<string, (long Oi, decimal Iv)> ForDay(string root, DateTime date)
		=> _cache.GetOrAdd((root.ToUpperInvariant(), date.Date), key => Load(_dir, key.Root, key.Date));

	private static IReadOnlyDictionary<string, (long Oi, decimal Iv)> Load(string dir, string root, DateTime date)
	{
		var map = new Dictionary<string, (long, decimal)>(StringComparer.OrdinalIgnoreCase);
		var path = Path.Combine(dir, root, $"{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(path)) return map;

		// OI is static intraday, so any record's OI is representative. Prefer the first RTH record (≥09:30 ET)
		// so the snapshot's IV — used for the gravity gamma weighting — comes from regular-hours quotes rather
		// than a thin pre-market print. Only a handful of lines are fully parsed before the break.
		string? chosen = null;
		string? firstAny = null;
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			firstAny ??= line;
			using var probe = JsonDocument.Parse(line);
			if (probe.RootElement.TryGetProperty("tsEt", out var tsEl)
				&& DateTime.TryParse(tsEl.GetString(), out var et)
				&& et.TimeOfDay >= new TimeSpan(9, 30, 0))
			{
				chosen = line;
				break;
			}
		}
		chosen ??= firstAny;
		if (chosen == null) return map;

		using var doc = JsonDocument.Parse(chosen);
		if (!doc.RootElement.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array) return map;
		foreach (var o in opts.EnumerateArray())
		{
			if (!o.TryGetProperty("symbol", out var symEl) || symEl.GetString() is not string sym || sym.Length == 0) continue;
			var oi = o.TryGetProperty("openInterest", out var oiEl) && oiEl.ValueKind == JsonValueKind.Number && oiEl.TryGetInt64(out var oiv) ? oiv : 0L;
			var iv = o.TryGetProperty("iv", out var ivEl) && ivEl.ValueKind == JsonValueKind.Number ? ivEl.GetDecimal() : 0m;
			// Keep the contract for its OI even when IV is absent/degenerate. OI is all the priced near-money
			// legs need from this cache (their IV comes from the bar back-solve) and all max-pain needs at any
			// strike; requiring iv>0 here silently withheld OI from every contract whose snapshot IV was null
			// (e.g. the ThetaData EOD backfill's plain-BS iv), making GEX/max-pain inert. IV (0 when unusable)
			// is still carried for the no-bar marker strikes that fall back to it for gamma weighting.
			if (oi > 0) map[sym] = (oi, iv > 0m ? iv : 0m);
		}
		return map;
	}
}
