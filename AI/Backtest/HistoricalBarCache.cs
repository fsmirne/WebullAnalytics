using System.Globalization;
using System.Text;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Disk-cached daily OHLC + Adj Close bars. Underlying tickers come from Yahoo; VIX-family index series
/// come from CBOE's own daily-prices CSVs (see <see cref="CboeIndexHistoryClient"/> for why Yahoo is never
/// used for those, not even as a fallback). Backtest uses Open for N+1 fill timing, High/Low
/// for intraday stop-loss / take-profit fidelity, and AdjClose for HV calc (so dividends and splits
/// don't leave fake gaps in realized vol). Cache files live under <c>data/history/{TICKER}.csv</c>
/// alongside <see cref="WebullAnalytics.AI.Replay.HistoricalPriceCache"/>'s close-only files. Both caches
/// read the same file format-tolerantly: the bar cache requires ≥6 columns; legacy 2-col files are
/// refetched in full on next access.
/// </summary>
internal sealed class HistoricalBarCache
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan SettlementCutoff = TimeSpan.FromHours(17);

	private readonly string _cacheDir;
	private readonly Func<string, DateTime, DateTime, CancellationToken, Task<Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>> _fetch;
	private readonly Func<DateTime> _utcNow;
	private readonly bool _offline;
	private readonly Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>> _memory = new(StringComparer.OrdinalIgnoreCase);

	public HistoricalBarCache(string? cacheDir = null, bool offline = false) : this(cacheDir, DispatchFetchAsync, offline: offline) { }

	private static Task<Dictionary<DateTime, YahooOptionsClient.HistoricalBar>> DispatchFetchAsync(string ticker, DateTime from, DateTime to, CancellationToken cancellation)
		=> CboeIndexHistoryClient.IsCboeSeries(ticker)
			? CboeIndexHistoryClient.FetchHistoricalBarsAsync(ticker, from, to, cancellation)
			: YahooOptionsClient.FetchHistoricalBarsAsync(ticker, from, to, cancellation);

	internal HistoricalBarCache(string? cacheDir, Func<string, DateTime, DateTime, CancellationToken, Task<Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>> fetch, Func<DateTime>? utcNow = null, bool offline = false)
	{
		_cacheDir = cacheDir ?? Program.ResolvePath("data/history");
		_fetch = fetch;
		_utcNow = utcNow ?? (() => DateTime.UtcNow);
		_offline = offline;
		Directory.CreateDirectory(_cacheDir);
	}

	public async Task<YahooOptionsClient.HistoricalBar?> GetBarAsync(string ticker, DateTime date, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, date.Date, cancellation);
		return map.TryGetValue(date.Date, out var bar) ? bar : null;
	}

	/// <summary>Returns the last <paramref name="count"/> Adj-Close values strictly before <paramref name="asOf"/>, oldest-first.
	/// Used by HV computation in <see cref="BacktestIVProvider"/>. Strict-less-than prevents lookahead in
	/// backtest mode where today's bar is already cached when the model is making its 09:30 decision.</summary>
	public async Task<IReadOnlyList<decimal>> GetRecentAdjClosesAsync(string ticker, int count, DateTime asOf, CancellationToken cancellation)
		=> await GetRecentValuesAsync(ticker, count, asOf, b => b.AdjClose, cancellation);

	/// <summary>Returns the last <paramref name="count"/> Close values strictly before <paramref name="asOf"/>, oldest-first.
	/// Same strict-less-than lookahead guard as <see cref="GetRecentAdjClosesAsync"/>; uses raw Close rather
	/// than AdjClose so callers that care about the actual print on screen (technical-bias EMA/RSI inputs,
	/// VIX term structure, intraday gap reference) don't see split/dividend-adjusted prices.</summary>
	public async Task<IReadOnlyList<decimal>> GetRecentClosesAsync(string ticker, int count, DateTime asOf, CancellationToken cancellation)
		=> await GetRecentValuesAsync(ticker, count, asOf, b => b.Close, cancellation);

	private async Task<IReadOnlyList<decimal>> GetRecentValuesAsync(string ticker, int count, DateTime asOf, Func<YahooOptionsClient.HistoricalBar, decimal> select, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, asOf.Date, cancellation);
		return map
			.Where(kv => kv.Key.Date < asOf.Date)
			.OrderByDescending(kv => kv.Key)
			.Take(count)
			.OrderBy(kv => kv.Key)
			.Select(kv => select(kv.Value))
			.ToList();
	}

	/// <summary>Returns the *next* trading-day's bar after <paramref name="asOf"/> within <paramref name="maxLookahead"/> calendar days.
	/// Used by the runner to fill open signals at next-day open. Null if none cached within the window.</summary>
	public async Task<YahooOptionsClient.HistoricalBar?> GetNextBarAsync(string ticker, DateTime asOf, int maxLookahead, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, asOf.Date.AddDays(maxLookahead), cancellation);
		for (var i = 1; i <= maxLookahead; i++)
		{
			var d = asOf.Date.AddDays(i);
			if (map.TryGetValue(d, out var bar)) return bar;
		}
		return null;
	}

	private async Task<Dictionary<DateTime, YahooOptionsClient.HistoricalBar>> LoadOrFetchAsync(string ticker, DateTime neededThrough, CancellationToken cancellation)
	{
		var effectiveThrough = ClampToSettled(neededThrough);
		if (_memory.TryGetValue(ticker, out var cached) && !NeedsRefresh(cached, effectiveThrough)) return cached;

		var path = Path.Combine(_cacheDir, $"{ticker.ToUpperInvariant()}.csv");
		Dictionary<DateTime, YahooOptionsClient.HistoricalBar> map;
		if (File.Exists(path))
		{
			map = ParseCsv(await File.ReadAllTextAsync(path, cancellation));
			if (!_offline && NeedsRefresh(map, effectiveThrough))
			{
				// Yahoo's chart endpoint returns a meta-only response (no daily series) for very short ranges, so a
				// one-or-two-day incremental window comes back empty and the cache would never advance past its last
				// bar. Always request at least a two-week trailing window; the merge below is keyed by date, so
				// re-fetching the days we already hold is idempotent.
				var from = map.Count > 0 ? map.Keys.Max().AddDays(1) : InitialFetchFrom(ticker, effectiveThrough);
				var minFrom = effectiveThrough.AddDays(-14);
				if (from > minFrom) from = minFrom;
				var refreshed = await _fetch(ticker, from, effectiveThrough.AddDays(1), cancellation);
				foreach (var (d, b) in refreshed) map[d.Date] = b;
				if (refreshed.Count > 0) await File.WriteAllTextAsync(path, SerializeCsv(map), cancellation);
			}
		}
		else
		{
			if (_offline)
			{
				map = new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
			}
			else
			{
				map = await _fetch(ticker, InitialFetchFrom(ticker, effectiveThrough), effectiveThrough.AddDays(1), cancellation);
				if (map.Count > 0) await File.WriteAllTextAsync(path, SerializeCsv(map), cancellation);
			}
		}

		_memory[ticker] = map;
		return map;
	}

	/// <summary>True when the on-disk cache covers <paramref name="from"/> through <paramref name="to"/> (inclusive).
	/// Used by the backtest to validate inputs before running so we fail fast instead of mid-loop.</summary>
	public async Task<bool> HasCoverageAsync(string ticker, DateTime from, DateTime to, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, to, cancellation);
		if (map.Count == 0) return false;
		var min = map.Keys.Min();
		var max = map.Keys.Max();
		return min <= from.Date && max >= ClampToSettled(to.Date, CboeIndexHistoryClient.IsCboeSeries(ticker));
	}

	/// <summary>Start of the window requested when a ticker has no cached bars yet. CBOE series take their
	/// entire published history: the client downloads the full CSV regardless (the window only discards rows),
	/// so capping at two years would throw away free data — VIX reaches back to 1990. Yahoo requests stay at
	/// two years; its chart API charges for the range and nothing consumes deeper underlying history.</summary>
	private static DateTime InitialFetchFrom(string ticker, DateTime effectiveThrough)
		=> CboeIndexHistoryClient.IsCboeSeries(ticker) ? DateTime.MinValue : effectiveThrough.AddYears(-2);

	private DateTime ClampToSettled(DateTime neededThrough, bool cboeLagTolerant = false)
	{
		var nowNy = TimeZoneInfo.ConvertTimeFromUtc(_utcNow(), NyTz);
		var settled = nowNy.TimeOfDay >= SettlementCutoff ? nowNy.Date : nowNy.Date.AddDays(-1);
		// Yahoo never prints a bar on weekends or NYSE holidays — walk BOTH the settled ceiling and the
		// requested end back to the most recent open trading day, so neither a Saturday "today" nor a
		// weekend/holiday --until demands a bar that will never exist. (settled=today after the 17:00 cutoff
		// is intentional — it lets a backtest include today once its EOD bar has posted.)
		while (!MarketCalendar.IsOpen(settled)) settled = settled.AddDays(-1);
		// CBOE index EOD values (VIX/VIX1D/VIX9D) publish later than Yahoo's underlyings — often well after our
		// 17:00 cutoff — so a coverage check must not demand *today's* CBOE bar the instant the cutoff passes, or
		// it reports a spurious "partial" every evening until CBOE posts. Back the requirement off to the prior
		// settled session for CBOE series; the fetch path keeps the strict ceiling so it still pulls today's bar
		// the moment it's available.
		if (cboeLagTolerant && settled == nowNy.Date)
			settled = MarketCalendar.PreviousOpenOnOrBefore(settled.AddDays(-1));
		var neededOpen = MarketCalendar.PreviousOpenOnOrBefore(neededThrough.Date);
		return neededOpen < settled ? neededOpen : settled;
	}

	private static bool NeedsRefresh(Dictionary<DateTime, YahooOptionsClient.HistoricalBar> map, DateTime neededThrough)
	{
		if (map.Count == 0) return true;
		return map.Keys.Max().Date < neededThrough.Date;
	}

	private static Dictionary<DateTime, YahooOptionsClient.HistoricalBar> ParseCsv(string content)
	{
		var map = new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Trim().Split(',');
			if (parts.Length < 6) continue;
			if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
			if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			if (!decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var adj)) continue;
			long? vol = null;
			if (parts.Length >= 7 && long.TryParse(parts[6], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) vol = v;
			map[d] = new YahooOptionsClient.HistoricalBar(d, open, high, low, close, adj, vol);
		}
		return map;
	}

	private static string SerializeCsv(Dictionary<DateTime, YahooOptionsClient.HistoricalBar> map)
	{
		var sb = new StringBuilder("date,open,high,low,close,adj_close,volume\n");
		foreach (var kv in map.OrderBy(k => k.Key))
		{
			var b = kv.Value;
			sb.Append(b.Date.ToString("yyyy-MM-dd")).Append(',')
				.Append(b.Open.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(b.High.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(b.Low.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(b.Close.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(b.AdjClose.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(b.Volume?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');
		}
		return sb.ToString();
	}
}
