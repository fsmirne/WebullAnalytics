using System.Globalization;

namespace WebullAnalytics.Api;

/// <summary>
/// Daily OHLC history for CBOE-published volatility indices (VIX, VIX1D, VIX9D, VIX3M) from CBOE's own
/// daily-prices CSVs (<c>https://cdn.cboe.com/api/global/us_indices/daily_prices/{SERIES}_History.csv</c>).
/// CBOE is the authoritative source for its indices; Yahoo's mirror of these series has two proven failure
/// modes we can't detect downstream (recent days silently absent, and rows frozen at a mid-session snapshot
/// — e.g. ^VIX 2026-02-06 closed 17.76 but Yahoo kept a stale 20.37 print), so the VIX family never falls
/// back to Yahoo: an unreachable CBOE keeps the cache stale and reports failure instead of quietly serving
/// corrupt data. Yahoo remains the source for underlying tickers (SPY, XSP, GME, …) which CBOE doesn't publish.
/// </summary>
internal static class CboeIndexHistoryClient
{
	private const string EndpointFormat = "https://cdn.cboe.com/api/global/us_indices/daily_prices/{0}_History.csv";
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
	private static readonly HashSet<string> Series = new(StringComparer.OrdinalIgnoreCase) { "VIX", "VIX1D", "VIX9D", "VIX3M" };

	/// <summary>True when <paramref name="ticker"/> is a CBOE-published index series whose history must come from CBOE rather than Yahoo.</summary>
	internal static bool IsCboeSeries(string ticker) => Series.Contains(ticker);

	/// <summary>Fetches daily bars for a CBOE index series in [<paramref name="from"/>, <paramref name="to"/>), matching
	/// <see cref="YahooOptionsClient.FetchHistoricalBarsAsync"/> semantics so <c>HistoricalBarCache</c> can use either
	/// source interchangeably. Returns an empty map on network failure (caller keeps any cached bars). AdjClose mirrors
	/// Close (indices have no splits/dividends) and Volume is null (CBOE publishes prices only).</summary>
	internal static async Task<Dictionary<DateTime, YahooOptionsClient.HistoricalBar>> FetchHistoricalBarsAsync(string series, DateTime from, DateTime to, CancellationToken cancellation)
	{
		string csv;
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(CultureInfo.InvariantCulture, EndpointFormat, series.ToUpperInvariant()));
			req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
			using var resp = await Http.SendAsync(req, cancellation);
			if (!resp.IsSuccessStatusCode)
			{
				Console.Error.WriteLine($"Warning: CBOE history fetch for {series} failed (HTTP {(int)resp.StatusCode}). Cache will keep existing bars.");
				return new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
			}
			csv = await resp.Content.ReadAsStringAsync(cancellation);
		}
		catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellation.IsCancellationRequested))
		{
			Console.Error.WriteLine($"Warning: CBOE history fetch for {series} failed ({ex.Message}). Cache will keep existing bars.");
			return new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
		}
		return ParseHistoryCsv(csv, from, to);
	}

	/// <summary>Parses CBOE's <c>DATE,OPEN,HIGH,LOW,CLOSE</c> (MM/DD/YYYY) history CSV into bars within
	/// [<paramref name="from"/>, <paramref name="to"/>). Rows on non-trading days are dropped: CBOE prints
	/// Global-Trading-Hours values for VIX on NYSE holidays, but the shorter-tenor series (VIX1D/VIX9D) have
	/// no holiday prints — importing them would misalign the VIX term-structure comparison and break the
	/// no-bar-on-holidays assumption shared by the coverage/settlement clamps.</summary>
	internal static Dictionary<DateTime, YahooOptionsClient.HistoricalBar> ParseHistoryCsv(string csv, DateTime from, DateTime to)
	{
		var map = new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
		foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Trim().Split(',');
			if (parts.Length < 5) continue;
			if (!DateTime.TryParseExact(parts[0], "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (d < from.Date || d >= to.Date || !MarketCalendar.IsOpen(d)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
			if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			map[d] = new YahooOptionsClient.HistoricalBar(d, open, high, low, close, close, null);
		}
		return map;
	}
}
