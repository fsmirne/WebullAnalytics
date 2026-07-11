using System.Globalization;
using System.Net.Http;
using System.Text;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Disk-cached daily CBOE S&P 500 SMILE Index — a single-number summary of how steep the SPX
/// put smile is on any given trading day. Used by <see cref="BacktestIVProvider"/> to scale the
/// index-profile smile coefficients per asOf so backtest fills reproduce the regime-dependent
/// wing premium that a constant smile model misses (e.g., 2025-04 tariff-shock days had SMILE
/// ~2,550 vs late-2025 calm days near 2,300; a constant calibration anchored to one of those
/// regimes mis-prices fills on the other).
///
/// CBOE publishes the full history at <c>https://cdn.cboe.com/api/global/us_indices/daily_prices/SMILE_History.csv</c>
/// in a single CSV (1986 → present). The cache fetches once, persists to
/// <c>data/history/SMILE.csv</c>, and reuses across runs. Settlement semantics mirror the other
/// daily caches (5pm NY cutoff); today is not persisted until settled.
/// </summary>
internal sealed class SmileIndexCache
{
	private const string Endpoint = "https://cdn.cboe.com/api/global/us_indices/daily_prices/SMILE_History.csv";
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan SettlementCutoff = TimeSpan.FromHours(17);

	private readonly string _cachePath;
	private readonly Func<CancellationToken, Task<string?>> _fetch;
	private readonly Func<DateTime> _utcNow;
	private readonly bool _offline;
	private Dictionary<DateTime, decimal>? _memory;

	public SmileIndexCache(string? cachePath = null, bool offline = false) : this(cachePath, DefaultFetchAsync, offline: offline) { }

	internal SmileIndexCache(string? cachePath, Func<CancellationToken, Task<string?>> fetch, Func<DateTime>? utcNow = null, bool offline = false)
	{
		_cachePath = cachePath ?? Program.ResolvePath("data/history/SMILE.csv");
		_fetch = fetch;
		_utcNow = utcNow ?? (() => DateTime.UtcNow);
		_offline = offline;
		Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
	}

	/// <summary>Returns the most recent settled SMILE index value strictly before <paramref name="date"/>,
	/// walking back up to 5 calendar days for weekends/holidays. Strictly-before because the value for
	/// <paramref name="date"/> itself is published at EOD on that date and is not available to a 09:30
	/// backtest decision — using it would be lookahead (same convention as
	/// <see cref="BacktestIVProvider"/>'s VIX-close lookup). Live callers are unaffected in practice:
	/// today's value never exists in the cache pre-settlement anyway. Null if the cache doesn't reach
	/// this date and we can't fetch.</summary>
	public async Task<decimal?> GetValueAsync(DateTime date, CancellationToken cancellation)
	{
		var map = await EnsureLoadedAsync(cancellation);
		for (var i = 1; i <= 6; i++)
		{
			var d = date.Date.AddDays(-i);
			if (map.TryGetValue(d, out var v)) return v;
		}
		return null;
	}

	public async Task<bool> HasCoverageAsync(DateTime from, DateTime to, CancellationToken cancellation)
	{
		var map = await EnsureLoadedAsync(cancellation);
		if (map.Count == 0) return false;
		return map.Keys.Min() <= from.Date && map.Keys.Max() >= ClampToSettled(to.Date);
	}

	/// <summary>Earliest and latest dates present in the cache (after any needed fetch), or null when the
	/// cache is empty and unreachable. Callers reporting freshness use this instead of
	/// <see cref="HasCoverageAsync"/> so they can judge the recent edge against CBOE's actual publish lag
	/// rather than the strict same-session settlement clamp the backtest gate needs.</summary>
	public async Task<(DateTime Min, DateTime Max)?> GetCoverageAsync(CancellationToken cancellation)
	{
		var map = await EnsureLoadedAsync(cancellation);
		if (map.Count == 0) return null;
		return (map.Keys.Min(), map.Keys.Max());
	}

	private async Task<Dictionary<DateTime, decimal>> EnsureLoadedAsync(CancellationToken cancellation)
	{
		if (_memory != null) return _memory;

		var fileMap = File.Exists(_cachePath)
			? ParseCsv(await File.ReadAllTextAsync(_cachePath, cancellation))
			: new Dictionary<DateTime, decimal>();

		var settled = ClampToSettled(DateTime.UtcNow);
		var fileMax = fileMap.Count > 0 ? fileMap.Keys.Max() : DateTime.MinValue;
		if (_offline || fileMax >= settled)
		{
			_memory = fileMap;
			return _memory;
		}

		// CBOE serves the FULL history (1986→present) in one ~600KB CSV — single fetch covers any backfill
		// needed, then we persist the merged map once. No chunking required.
		var body = await _fetch(cancellation);
		if (body == null)
		{
			_memory = fileMap;
			return _memory;
		}

		var fetched = ParseCsv(body);
		foreach (var (d, v) in fetched)
			fileMap[d] = v;
		if (fetched.Count > 0)
			await File.WriteAllTextAsync(_cachePath, SerializeCsv(fileMap), cancellation);

		_memory = fileMap;
		return _memory;
	}

	private DateTime ClampToSettled(DateTime utcNow)
	{
		var nowNy = TimeZoneInfo.ConvertTimeFromUtc(utcNow.Kind == DateTimeKind.Utc ? utcNow : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), NyTz);
		var settled = nowNy.TimeOfDay >= SettlementCutoff ? nowNy.Date : nowNy.Date.AddDays(-1);
		while (!MarketCalendar.IsOpen(settled)) settled = settled.AddDays(-1);
		return settled;
	}

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
	private static async Task<string?> DefaultFetchAsync(CancellationToken cancellation)
	{
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
			req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
			using var resp = await Http.SendAsync(req, cancellation);
			if (!resp.IsSuccessStatusCode) return null;
			return await resp.Content.ReadAsStringAsync(cancellation);
		}
		catch (HttpRequestException) { return null; }
		catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
	}

	/// <summary>Parses both the CBOE-native MM/DD/YYYY header format and the on-disk ISO date format the
	/// cache writes back. Skips the header row and any malformed lines.</summary>
	internal static Dictionary<DateTime, decimal> ParseCsv(string content)
	{
		var map = new Dictionary<DateTime, decimal>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Trim().Split(',');
			if (parts.Length < 2) continue;
			if (!TryParseDate(parts[0], out var d)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) continue;
			map[d] = v;
		}
		return map;
	}

	private static bool TryParseDate(string s, out DateTime d)
	{
		if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;
		if (DateTime.TryParseExact(s, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;
		return false;
	}

	private static string SerializeCsv(Dictionary<DateTime, decimal> map)
	{
		var sb = new StringBuilder("date,smile\n");
		foreach (var kv in map.OrderBy(k => k.Key))
			sb.Append(kv.Key.ToString("yyyy-MM-dd")).Append(',').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
		return sb.ToString();
	}
}
