using System.Collections.Concurrent;
using System.Text.Json;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Read-only cache over the daily full-chain OI snapshot store at
/// <c>data/oi/<root>/<date>.jsonl</c> (written by both the ThetaData backfill and the live scraper).
/// The file holds one record per trading day (<c>underlyingPrice</c> + <c>options[]</c>, each with
/// <c>openInterest</c> and <c>iv</c>).
///
/// <para>Open interest settles overnight, so it is constant across a trading day's minutes — this cache
/// therefore exposes that one day's per-contract (OI, IV), which is exactly what
/// <see cref="Open.CandidateScorer.ComputeGex"/> and the max-pain factor need. The minute NBBO the backtest
/// prices from (<c>data/quotes/<root>/<expiry>.csv</c>) carries no OI column, so without this cache the
/// GEX / max-pain factors are silently inert in the backtest (gravity collapses, NetGexFraction = 0) even
/// though they are live in production. This cache is what makes them computable on days a snapshot exists.</para>
///
/// <para>Causality: OI is keyed to its REPORT date (published each morning, reflecting the prior close),
/// so day T's OI is knowable at T's open — always served as-is. IV is not: the ThetaData backfill
/// back-solves it from day T's EOD mids and stamps the record 16:00 ET, so serving that IV intraday on
/// day T would leak end-of-day vol into a 09:30 GEX weighting (the same EOD-IV leakage that invalidated
/// the GEX-shelf study). For EOD-stamped records this cache therefore substitutes the PRIOR trading
/// day's snapshot IV per contract (0 = unusable when the contract has none there — ComputeGex skips its
/// gamma; OI still counts for max-pain). Morning-stamped records (the live scraper's first RTH capture)
/// keep their same-day IV — it was observable at capture time.</para>
///
/// <para>Loaded lazily per (root, ET date) and memoized. Absent file → empty map → GEX stays inert for that
/// day (unchanged behaviour, partial coverage is fine).</para></summary>
internal sealed class ChainSnapshotOiCache
{
	// Records stamped at or after this ET wall-clock time are EOD snapshots (the backfill stamps exactly
	// 16:00): their IV encodes end-of-day information and must not be served intraday for the same date.
	private static readonly TimeSpan EodStampCutoff = new(16, 0, 0);

	private readonly string _dir;
	// Raw per-file loads: the day's per-contract map + whether the chosen record was EOD-stamped.
	private readonly ConcurrentDictionary<(string Root, DateTime Date), (IReadOnlyDictionary<string, (long Oi, decimal Iv)> Map, bool IsEod)> _raw = new();
	// Causal merged views served to consumers (day-T OI, prior-day IV for EOD-stamped files).
	private readonly ConcurrentDictionary<(string Root, DateTime Date), IReadOnlyDictionary<string, (long Oi, decimal Iv)>> _merged = new();

	public ChainSnapshotOiCache(string? dir = null) => _dir = dir ?? Program.ResolvePath("data/oi");

	/// <summary>Per-contract (open interest, implied vol) for <paramref name="root"/> on the ET trading day of
	/// <paramref name="date"/>, keyed by OCC symbol, with only intraday-knowable values (see class remarks for
	/// the EOD-IV substitution). Empty when no snapshot file exists for that day.</summary>
	public IReadOnlyDictionary<string, (long Oi, decimal Iv)> ForDay(string root, DateTime date)
		=> _merged.GetOrAdd((root.ToUpperInvariant(), date.Date), key => BuildCausal(key.Root, key.Date));

	private IReadOnlyDictionary<string, (long Oi, decimal Iv)> BuildCausal(string root, DateTime date)
	{
		var (dayMap, isEod) = _raw.GetOrAdd((root, date), key => Load(_dir, key.Root, key.Date));
		if (!isEod || dayMap.Count == 0) return dayMap;

		var priorDate = MarketCalendar.PreviousOpenOnOrBefore(date.AddDays(-1));
		var (priorMap, _) = _raw.GetOrAdd((root, priorDate), key => Load(_dir, key.Root, key.Date));
		var causal = new Dictionary<string, (long Oi, decimal Iv)>(dayMap.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, v) in dayMap)
			causal[sym] = (v.Oi, priorMap.TryGetValue(sym, out var prior) ? prior.Iv : 0m);
		return causal;
	}

	private static (IReadOnlyDictionary<string, (long Oi, decimal Iv)> Map, bool IsEod) Load(string dir, string root, DateTime date)
	{
		var map = new Dictionary<string, (long, decimal)>(StringComparer.OrdinalIgnoreCase);
		var path = Path.Combine(dir, root, $"{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(path)) return (map, false);

		// OI is static intraday, so any record's OI is representative. Prefer the first RTH record (≥09:30 ET)
		// so the snapshot's IV — used for the gravity gamma weighting — comes from regular-hours quotes rather
		// than a thin pre-market print. Only a handful of lines are fully parsed before the break.
		string? chosen = null;
		string? firstAny = null;
		var chosenEt = default(TimeSpan);
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			firstAny ??= line;
			using var probe = JsonDocument.Parse(line);
			// DateTimeOffset keeps the ET wall clock as written ("T16:00:00-05:00" → 16:00), unlike
			// DateTime.TryParse which shifts offset-bearing stamps to machine-local time.
			if (probe.RootElement.TryGetProperty("tsEt", out var tsEl)
				&& DateTimeOffset.TryParse(tsEl.GetString(), out var et)
				&& et.DateTime.TimeOfDay >= new TimeSpan(9, 30, 0))
			{
				chosen = line;
				chosenEt = et.DateTime.TimeOfDay;
				break;
			}
		}
		chosen ??= firstAny;
		if (chosen == null) return (map, false);

		using var doc = JsonDocument.Parse(chosen);
		if (!doc.RootElement.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array) return (map, false);
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
		return (map, chosenEt >= EodStampCutoff);
	}
}
