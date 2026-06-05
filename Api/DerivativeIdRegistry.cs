using System.Text.Json;
using WebullAnalytics.AI;

namespace WebullAnalytics.Api;

/// <summary>One contract's persisted state in <see cref="DerivativeIdRegistry"/>: Webull's per-contract
/// <c>derivativeId</c> plus optional daily-refreshed liquidity. <see cref="Tradeable"/> means the contract
/// returned a usable bid/ask (or carried open interest) on <see cref="AsOf"/> (an ET <c>yyyy-MM-dd</c>
/// date) — the daily chain snapshot uses this to tell the opener which listed strikes actually trade,
/// since SPX-family chains list far more strikes than they quote.</summary>
internal sealed record DerivativeEntry(long Id, string? AsOf = null, bool Tradeable = false, long? OpenInterest = null);

/// <summary>On-disk map from OCC option symbol → Webull's per-contract <c>derivativeId</c>, optionally
/// enriched with a daily liquidity snapshot. Webull's <c>/api/quote/option/strategy/list</c> returns the
/// id alongside every contract in a live chain, but only for currently-tradable expirations — once an
/// option expires, neither the chain nor any other Webull endpoint resolves its OCC symbol back to a
/// <c>derivativeId</c>. So we harvest ids while contracts are still live and persist them, otherwise we
/// lose the ability to backfill per-minute option charts (which take <c>derivativeId</c>, not OCC).
///
/// <para>The same file doubles as the daily chain snapshot: <see cref="RecordSnapshot"/> marks which
/// near-the-money strikes actually quoted today, and <see cref="TradeableOccs"/> lets the opener build a
/// real (non-uniform) strike ladder without re-probing the whole chain every tick.</para>
///
/// <para>Storage: <c>data/derivative-ids.json</c>, a flat JSON object. Id-only entries serialize as a
/// bare number (<c>"OCC": 123</c>) to keep the large harvest file compact and diff-stable; entries with a
/// liquidity snapshot serialize as an object (<c>"OCC": {"id":123,"asof":"2026-06-01","tradeable":true,
/// "oi":1200}</c>). Both forms load back transparently. Writes are atomic (tmp → File.Move). Reads are
/// lazy and memoized.</para>
///
/// <para>Thread-safety: every public method takes the same <see cref="_lock"/>.</para></summary>
internal static class DerivativeIdRegistry
{
	private static readonly object _lock = new();
	private static Dictionary<string, DerivativeEntry>? _cache;
	private static string? _resolvedPath;

	/// <summary>Adds id entries and flushes to disk if anything new appeared. An existing entry's liquidity
	/// snapshot is preserved; only the id is updated (Webull occasionally re-issues an id after a corporate
	/// action, and the more recent value is the one a subsequent chart fetch needs).</summary>
	public static void Register(IReadOnlyDictionary<string, long> additions)
	{
		if (additions.Count == 0) return;
		lock (_lock)
		{
			EnsureLoaded();
			var dirty = false;
			foreach (var (symbol, id) in additions)
			{
				if (string.IsNullOrWhiteSpace(symbol) || id <= 0) continue;
				if (_cache!.TryGetValue(symbol, out var existing))
				{
					if (existing.Id == id) continue;
					_cache[symbol] = existing with { Id = id };
				}
				else _cache![symbol] = new DerivativeEntry(id);
				dirty = true;
			}
			if (dirty) Flush();
		}
	}

	/// <summary>Records the daily liquidity snapshot for a set of contracts: marks each tradeable-or-not
	/// as-of <paramref name="asOf"/> (ET <c>yyyy-MM-dd</c>), preserving the existing id. Contracts not yet in
	/// the registry are skipped (we only snapshot strikes whose id we already harvested from the chain).
	/// Always flushes — the daily snapshot is the point of the call.</summary>
	public static void RecordSnapshot(string asOf, IReadOnlyDictionary<string, (bool Tradeable, long? OpenInterest)> liquidity)
	{
		if (string.IsNullOrWhiteSpace(asOf) || liquidity.Count == 0) return;
		lock (_lock)
		{
			EnsureLoaded();
			var dirty = false;
			foreach (var (symbol, (tradeable, oi)) in liquidity)
			{
				if (!_cache!.TryGetValue(symbol, out var existing)) continue;
				var updated = existing with { AsOf = asOf, Tradeable = tradeable, OpenInterest = oi };
				if (updated == existing) continue;
				_cache[symbol] = updated;
				dirty = true;
			}
			if (dirty) Flush();
		}
	}

	/// <summary>True when the registry already holds a snapshot dated <paramref name="asOf"/> for any
	/// contract of <paramref name="ticker"/> — lets the opener skip the daily liquidity sweep if today's
	/// snapshot was already taken (by an earlier launch this session or a scheduled run).</summary>
	public static bool HasSnapshot(string ticker, string asOf)
	{
		lock (_lock)
		{
			EnsureLoaded();
			foreach (var (occ, e) in _cache!)
				if (e.AsOf == asOf && OccRoot(occ) is { } root && string.Equals(root, ticker, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}
	}

	/// <summary>OCC symbols of <paramref name="ticker"/> marked tradeable in the snapshot dated
	/// <paramref name="asOf"/>. The opener turns these into the real strike ladder.</summary>
	public static IReadOnlyList<string> TradeableOccs(string ticker, string asOf)
	{
		lock (_lock)
		{
			EnsureLoaded();
			var result = new List<string>();
			foreach (var (occ, e) in _cache!)
				if (e.Tradeable && e.AsOf == asOf && OccRoot(occ) is { } root && string.Equals(root, ticker, StringComparison.OrdinalIgnoreCase))
					result.Add(occ);
			return result;
		}
	}

	/// <summary>Resolves a single OCC symbol to its persisted derivativeId. Lets a quote fetch price a
	/// contract the live chain omitted (far-dated expiries outside the strategy/list cycle) by reusing an
	/// id harvested in an earlier session, instead of letting the leg propagate as un-priceable.</summary>
	public static bool TryGetId(string symbol, out long id)
	{
		id = 0;
		if (string.IsNullOrWhiteSpace(symbol)) return false;
		lock (_lock)
		{
			EnsureLoaded();
			if (_cache!.TryGetValue(symbol, out var e) && e.Id > 0) { id = e.Id; return true; }
			return false;
		}
	}

	/// <summary>Returns a copy of the OCC → derivativeId map (liquidity dropped) for callers like the
	/// backfill command that only need ids. Stable snapshot, iterable without the registry lock.</summary>
	public static IReadOnlyDictionary<string, long> Snapshot()
	{
		lock (_lock)
		{
			EnsureLoaded();
			var copy = new Dictionary<string, long>(_cache!.Count, StringComparer.OrdinalIgnoreCase);
			foreach (var (occ, e) in _cache!) copy[occ] = e.Id;
			return copy;
		}
	}

	/// <summary>Test hook: forces the registry to read/write a specific path and discards cached state.</summary>
	internal static void ResetForTests(string? path)
	{
		lock (_lock)
		{
			_resolvedPath = path;
			_cache = null;
		}
	}

	private static string? OccRoot(string occ) => ParsingHelpers.ParseOptionSymbol(occ)?.Root;

	private static void EnsureLoaded()
	{
		if (_cache != null) return;
		var path = _resolvedPath ?? Program.ResolvePath(Program.DerivativeIdsPath);
		_resolvedPath = path;
		_cache = new Dictionary<string, DerivativeEntry>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(path)) return;
		try
		{
			var json = File.ReadAllText(path);
			if (string.IsNullOrWhiteSpace(json)) return;
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
			foreach (var prop in doc.RootElement.EnumerateObject())
			{
				if (string.IsNullOrWhiteSpace(prop.Name)) continue;
				var entry = ParseEntry(prop.Value);
				if (entry is { Id: > 0 }) _cache[prop.Name] = entry;
			}
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"derivative-ids: failed to parse {path}: {ex.Message}. Starting empty.");
		}
		catch (IOException ex)
		{
			Console.WriteLine($"derivative-ids: failed to read {path}: {ex.Message}. Starting empty.");
		}
	}

	/// <summary>Accepts both the legacy bare-number form (<c>"OCC": 123</c>) and the enriched object form.</summary>
	private static DerivativeEntry? ParseEntry(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bareId))
			return new DerivativeEntry(bareId);
		if (value.ValueKind != JsonValueKind.Object) return null;
		if (!value.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) return null;
		var asof = value.TryGetProperty("asof", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
		var tradeable = value.TryGetProperty("tradeable", out var t) && t.ValueKind == JsonValueKind.True;
		long? oi = value.TryGetProperty("oi", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt64(out var oiv) ? oiv : null;
		return new DerivativeEntry(id, asof, tradeable, oi);
	}

	private static void Flush()
	{
		var path = _resolvedPath!;
		try
		{
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
			// Heterogeneous values: bare number for id-only entries (compact, diff-stable for the big harvest
			// file), object only when a liquidity snapshot is present. Sorted for stable diffs.
			var ordered = new SortedDictionary<string, object>(StringComparer.Ordinal);
			foreach (var (occ, e) in _cache!)
				ordered[occ] = e.AsOf == null && !e.Tradeable && e.OpenInterest == null
					? e.Id
					: new Dictionary<string, object?> { ["id"] = e.Id, ["asof"] = e.AsOf, ["tradeable"] = e.Tradeable, ["oi"] = e.OpenInterest };
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
			File.Move(tmp, path, overwrite: true);
		}
		catch (IOException ex)
		{
			Console.WriteLine($"derivative-ids: failed to write {path}: {ex.Message}");
		}
	}
}
