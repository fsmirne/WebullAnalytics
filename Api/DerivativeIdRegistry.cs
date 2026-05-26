using System.Text.Json;

namespace WebullAnalytics.Api;

/// <summary>On-disk map from OCC option symbol → Webull's per-contract <c>derivativeId</c>. Webull's
/// <c>/api/quote/option/strategy/list</c> returns the id alongside every contract in a live chain,
/// but only for currently-tradable expirations — once an option expires, neither the chain nor any
/// other Webull endpoint resolves its OCC symbol back to a <c>derivativeId</c>. So we have to harvest
/// ids while contracts are still live and persist them, otherwise we lose the ability to backfill
/// per-minute option charts (which take <c>derivativeId</c>, not OCC).
///
/// <para>Storage: <c>data/derivative-ids.json</c>, a flat JSON object <c>{ "OCC": id, ... }</c>.
/// Writes are atomic (tmp → File.Move overwrite). Reads are lazy and memoized — first access loads
/// from disk; subsequent accesses hit the in-memory dict. Writes only happen when <see cref="Register"/>
/// observes at least one new symbol, so the steady-state cost of the watch loop is one lock + one
/// dict lookup per chain fetch.</para>
///
/// <para>Thread-safety: every public method takes the same <see cref="_lock"/>. Watch ticks can run
/// concurrently with backfill / analyze commands without corrupting the file.</para></summary>
internal static class DerivativeIdRegistry
{
	private static readonly object _lock = new();
	private static Dictionary<string, long>? _cache;
	private static string? _resolvedPath;

	/// <summary>Adds entries to the registry and flushes to disk if anything new appeared. Existing
	/// entries with a different id are overwritten — Webull occasionally re-issues an id for the same
	/// OCC symbol after a corporate action / split, and the more recent value is always the one a
	/// subsequent chart fetch needs.</summary>
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
				if (_cache!.TryGetValue(symbol, out var existing) && existing == id) continue;
				_cache[symbol] = id;
				dirty = true;
			}
			if (dirty) Flush();
		}
	}

	/// <summary>Returns a copy of the current registry. Callers (e.g. the backfill command) get a stable
	/// snapshot they can iterate without holding the registry lock.</summary>
	public static IReadOnlyDictionary<string, long> Snapshot()
	{
		lock (_lock)
		{
			EnsureLoaded();
			return new Dictionary<string, long>(_cache!, StringComparer.OrdinalIgnoreCase);
		}
	}

	/// <summary>Test hook: forces the registry to read/write from a specific path and discards any cached
	/// state. Production code never touches this — it's here so xunit tests can use a tmp file without
	/// racing each other or polluting the user's prod data dir.</summary>
	internal static void ResetForTests(string? path)
	{
		lock (_lock)
		{
			_resolvedPath = path;
			_cache = null;
		}
	}

	private static void EnsureLoaded()
	{
		if (_cache != null) return;
		var path = _resolvedPath ?? Program.ResolvePath(Program.DerivativeIdsPath);
		_resolvedPath = path;
		_cache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(path)) return;
		try
		{
			var json = File.ReadAllText(path);
			if (string.IsNullOrWhiteSpace(json)) return;
			var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
			if (loaded == null) return;
			foreach (var (k, v) in loaded)
				if (!string.IsNullOrWhiteSpace(k) && v > 0) _cache[k] = v;
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

	private static void Flush()
	{
		var path = _resolvedPath!;
		try
		{
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
			var tmp = path + ".tmp";
			// Sort for stable diffs — a file that flips order on every flush is hostile to git inspection.
			var sorted = new SortedDictionary<string, long>(_cache!, StringComparer.Ordinal);
			File.WriteAllText(tmp, JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
			File.Move(tmp, path, overwrite: true);
		}
		catch (IOException ex)
		{
			Console.WriteLine($"derivative-ids: failed to write {path}: {ex.Message}");
		}
	}
}
