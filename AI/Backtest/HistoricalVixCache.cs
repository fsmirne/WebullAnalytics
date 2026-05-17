using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Disk-cached daily VIX closes scraped from CNN's Fear &amp; Greed graphdata endpoint. One request to
/// .../graphdata/{YYYY-MM-DD} returns ~6 months of daily VIX values *forward* from that date through
/// "today" — so a single call typically covers a full YTD backtest window. The cache persists to
/// data/vix-history.csv. Used by <see cref="BacktestIVProvider"/> as the SPY ATM IV input.
///
/// Settlement semantics mirror <see cref="WebullAnalytics.AI.Replay.HistoricalPriceCache"/>:
/// today's intraday VIX is never persisted; only NY-settled dates land on disk.
/// </summary>
internal sealed class HistoricalVixCache
{
	private const string Endpoint = "https://production.dataviz.cnn.io/index/fearandgreed/graphdata";
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan SettlementCutoff = TimeSpan.FromHours(17);

	private readonly string _cachePath;
	private readonly Func<DateTime, CancellationToken, Task<string?>> _fetch;
	private readonly Func<DateTime> _utcNow;
	private readonly bool _offline;
	private Dictionary<DateTime, decimal>? _memory;

	public HistoricalVixCache(string? cachePath = null, bool offline = false) : this(cachePath, DefaultFetchAsync, offline: offline) { }

	internal HistoricalVixCache(string? cachePath, Func<DateTime, CancellationToken, Task<string?>> fetch, Func<DateTime>? utcNow = null, bool offline = false)
	{
		_cachePath = cachePath ?? Program.ResolvePath("data/history/VIX.csv");
		_fetch = fetch;
		_utcNow = utcNow ?? (() => DateTime.UtcNow);
		_offline = offline;
		Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
	}

	public async Task<decimal?> GetVixAsync(DateTime date, CancellationToken cancellation)
	{
		var map = await EnsureLoadedAsync(date.Date, cancellation);
		if (map.TryGetValue(date.Date, out var v)) return v;
		// Weekends/holidays: walk back up to 5 days for the most recent settled value.
		for (var i = 1; i <= 5; i++)
		{
			var prior = date.Date.AddDays(-i);
			if (map.TryGetValue(prior, out v)) return v;
		}
		return null;
	}

	/// <summary>True when the cache covers <paramref name="from"/> through <paramref name="to"/> (inclusive).</summary>
	public async Task<bool> HasCoverageAsync(DateTime from, DateTime to, CancellationToken cancellation)
	{
		var map = await EnsureLoadedAsync(to, cancellation);
		if (map.Count == 0) return false;
		return map.Keys.Min() <= from.Date && map.Keys.Max() >= ClampToSettled(to.Date);
	}

	/// <summary>Ensures the cache covers <c>[from, to]</c> by chunked fetches. The CNN F&amp;G endpoint
	/// returns ~6 months of VIX data forward from the URL date in a single response, so a 2-year backfill
	/// needs ~4 sequential calls. Each call advances the URL date past the latest data we received.
	/// Persists to disk once after the loop. No-op when <c>_offline</c> is true.</summary>
	public async Task EnsureRangeAsync(DateTime from, DateTime to, CancellationToken cancellation)
	{
		if (_offline) return;

		_memory ??= File.Exists(_cachePath) ? ParseCsv(await File.ReadAllTextAsync(_cachePath, cancellation)) : new Dictionary<DateTime, decimal>();

		var endTarget = ClampToSettled(to);
		var startTarget = from.Date;
		var totalAdded = 0;

		// Walk forward from the earliest missing date in ~5-month chunks (the endpoint reliably returns
		// ~6 months but we step by 5 to ensure overlap and tolerate weeks where it returns slightly less).
		var cursor = _memory.Count == 0 || _memory.Keys.Min() > startTarget ? startTarget : _memory.Keys.Max().AddDays(1);
		while (cursor <= endTarget)
		{
			cancellation.ThrowIfCancellationRequested();
			var body = await _fetch(cursor, cancellation);
			if (body == null) break;

			var parsed = ParseVixFromFearGreed(body).ToList();
			if (parsed.Count == 0) break;

			var addedThisChunk = 0;
			DateTime maxInChunk = cursor;
			foreach (var (d, v) in parsed)
			{
				if (d.Date > endTarget) continue;
				if (d.Date > maxInChunk) maxInChunk = d.Date;
				if (_memory.ContainsKey(d.Date)) continue;
				_memory[d.Date] = v;
				addedThisChunk++;
			}
			totalAdded += addedThisChunk;

			// Advance cursor past the last date we got. If the chunk didn't add anything new
			// AND didn't move the max date forward, we're stuck — bail to avoid an infinite loop.
			var nextCursor = maxInChunk.AddDays(1);
			if (nextCursor <= cursor) break;
			cursor = nextCursor;
		}

		if (totalAdded > 0)
			await File.WriteAllTextAsync(_cachePath, SerializeCsv(_memory), cancellation);
	}

	private async Task<Dictionary<DateTime, decimal>> EnsureLoadedAsync(DateTime neededThrough, CancellationToken cancellation)
	{
		_memory ??= File.Exists(_cachePath) ? ParseCsv(await File.ReadAllTextAsync(_cachePath, cancellation)) : new Dictionary<DateTime, decimal>();

		var effectiveThrough = ClampToSettled(neededThrough);
		if (_memory.Count > 0 && _memory.Keys.Max() >= effectiveThrough) return _memory;
		if (_offline) return _memory;

		// Fetch from the earliest needed date. If cache is empty, start 60 days before the request
		// (gives HV-style lookback context even for early backtest dates).
		var from = _memory.Count > 0 ? _memory.Keys.Max().AddDays(1) : neededThrough.AddDays(-60);
		var fetched = await _fetch(from.Date, cancellation);
		if (fetched == null) return _memory;

		var parsed = ParseVixFromFearGreed(fetched);
		var added = 0;
		foreach (var (date, vix) in parsed)
		{
			if (date.Date > effectiveThrough) continue;
			if (_memory.ContainsKey(date.Date)) continue;
			_memory[date.Date] = vix;
			added++;
		}

		if (added > 0)
			await File.WriteAllTextAsync(_cachePath, SerializeCsv(_memory), cancellation);

		return _memory;
	}

	private DateTime ClampToSettled(DateTime neededThrough)
	{
		var nowNy = TimeZoneInfo.ConvertTimeFromUtc(_utcNow(), NyTz);
		var settled = nowNy.TimeOfDay >= SettlementCutoff ? nowNy.Date : nowNy.Date.AddDays(-1);
		// VIX prints only on open NYSE sessions — weekend/holiday settled dates must roll back to
		// the most recent trading day. Same fix as HistoricalBarCache.ClampToSettled.
		while (!MarketCalendar.IsOpen(settled)) settled = settled.AddDays(-1);
		return neededThrough.Date < settled ? neededThrough.Date : settled;
	}

	private static readonly HttpClient Http = CreateHttpClient();

	private static HttpClient CreateHttpClient()
	{
		var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
		c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		c.DefaultRequestHeaders.Add("Origin", "https://edition.cnn.com");
		c.DefaultRequestHeaders.Add("Referer", "https://edition.cnn.com/");
		return c;
	}

	private static async Task<string?> DefaultFetchAsync(DateTime from, CancellationToken cancellation)
	{
		var url = $"{Endpoint}/{from:yyyy-MM-dd}";
		try
		{
			using var resp = await Http.GetAsync(url, cancellation);
			if (!resp.IsSuccessStatusCode) return null;
			return await resp.Content.ReadAsStringAsync(cancellation);
		}
		catch (HttpRequestException) { return null; }
		catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
	}

	/// <summary>Pulls the daily VIX time series out of the F&amp;G endpoint's
	/// <c>market_volatility_vix.data</c> array. Each entry is <c>{ x: epochMillis, y: vixClose }</c>.</summary>
	internal static IEnumerable<(DateTime Date, decimal Vix)> ParseVixFromFearGreed(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		if (!root.TryGetProperty("market_volatility_vix", out var component)) yield break;
		if (!component.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;

		foreach (var pt in arr.EnumerateArray())
		{
			if (!pt.TryGetProperty("x", out var xEl) || xEl.ValueKind != JsonValueKind.Number) continue;
			if (!pt.TryGetProperty("y", out var yEl) || yEl.ValueKind != JsonValueKind.Number) continue;
			var date = DateTimeOffset.FromUnixTimeMilliseconds((long)xEl.GetDouble()).UtcDateTime.Date;
			var vix = (decimal)yEl.GetDouble();
			yield return (date, vix);
		}
	}

	private static Dictionary<DateTime, decimal> ParseCsv(string content)
	{
		var map = new Dictionary<DateTime, decimal>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Trim().Split(',');
			if (parts.Length < 2) continue;
			if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) continue;
			map[d] = v;
		}
		return map;
	}

	private static string SerializeCsv(Dictionary<DateTime, decimal> map)
	{
		var sb = new StringBuilder("date,vix\n");
		foreach (var kv in map.OrderBy(k => k.Key))
			sb.Append(kv.Key.ToString("yyyy-MM-dd")).Append(',').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
		return sb.ToString();
	}
}
