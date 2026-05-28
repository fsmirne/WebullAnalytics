using System.Globalization;
using System.Net;
using System.Text.Json;
using WebullAnalytics.AI.Events;

namespace WebullAnalytics.Api;

/// <summary>Fetches scheduled-catalyst dates (next earnings, next ex-dividend) from Yahoo Finance's
/// <c>quoteSummary</c> endpoint and caches the parsed result to <c>data/event-cache/{TICKER}.json</c>.
/// The endpoint shape mirrors <see cref="YahooOptionsClient"/>: it needs a browser-shaped UA, may
/// require a crumb on 401/403, and benefits from a per-call retry. Failures (network, 4xx/5xx,
/// malformed JSON) return null — the event filter degrades gracefully so a Yahoo outage never
/// silences the opener.
///
/// Cache freshness: an entry written today is reused unless it's older than 12 hours of wall-clock
/// time. Earnings/ex-div dates are quasi-static (revisions are days-ahead), so a daily-ish refresh
/// is fine. The cache file holds (fetchTime, payload) so we don't depend on filesystem mtime.</summary>
internal static class YahooCalendarClient
{
	private const string EndpointBase = "https://query2.finance.yahoo.com/v10/finance/quoteSummary";
	private const string FallbackEndpointBase = "https://query1.finance.yahoo.com/v10/finance/quoteSummary";
	private const string CrumbUrl = "https://query1.finance.yahoo.com/v1/test/getcrumb";
	private const string CookieBootstrapUrl = "https://fc.yahoo.com";
	private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

	/// <summary>Batch-fetch events for a set of tickers. Uses one HttpClient + cookie jar across the
	/// batch so the crumb is reused. Per-ticker failures are swallowed — the returned dictionary only
	/// contains tickers we successfully resolved. The caller treats absent tickers as "no events known"
	/// (no veto, no diagnostic hit).
	/// When <paramref name="cacheOnly"/> is true, the network branch is skipped — returns only what's
	/// cached. Used by the backtest path: Yahoo's quoteSummary returns current state regardless of
	/// asOf, so a backtest network call would leak future events into historical decisions. Reading
	/// the cache is safe because <see cref="TryReadCache"/> rejects entries whose fetchedAt is more
	/// than CacheTtl outside asOf in either direction — so a cache fetched far in the future of asOf
	/// won't be honored.</summary>
	internal static async Task<IReadOnlyDictionary<string, TickerEvents>> FetchEventsAsync(IEnumerable<string> tickers, DateTime asOf, CancellationToken cancellation, bool cacheOnly = false)
	{
		var distinct = tickers
			.Where(t => !string.IsNullOrWhiteSpace(t))
			.Select(t => t.Trim().ToUpperInvariant())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		var result = new Dictionary<string, TickerEvents>(StringComparer.OrdinalIgnoreCase);
		if (distinct.Count == 0) return result;

		var cacheDir = Program.ResolvePath("data/event-cache");
		Directory.CreateDirectory(cacheDir);

		// Try cache first for each ticker. Anything that hits stays out of the network call list.
		var needsFetch = new List<string>();
		foreach (var ticker in distinct)
		{
			var cachePath = Path.Combine(cacheDir, $"{ticker}.json");
			var cached = TryReadCache(cachePath, asOf);
			if (cached != null)
				result[ticker] = cached;
			else
				needsFetch.Add(ticker);
		}

		if (cacheOnly) return result;
		if (needsFetch.Count == 0) return result;

		using var handler = new HttpClientHandler
		{
			CookieContainer = new CookieContainer(),
			AutomaticDecompression = DecompressionMethods.All,
		};
		using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

		string? crumb = null;

		foreach (var ticker in needsFetch)
		{
			cancellation.ThrowIfCancellationRequested();
			try
			{
				var (body, gotCrumb) = await FetchOnceAsync(client, ticker, crumb, cancellation);
				crumb = gotCrumb ?? crumb;
				if (body == null) continue;

				var parsed = ParseResponse(ticker, body);
				if (parsed == null) continue;

				result[ticker] = parsed;
				try
				{
					var cachePath = Path.Combine(cacheDir, $"{ticker}.json");
					await File.WriteAllTextAsync(cachePath, SerializeCache(asOf, body), cancellation);
				}
				catch (IOException) { /* cache is best-effort */ }
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Console.WriteLine($"Warning: Yahoo calendar fetch failed for {ticker}: {ex.Message}");
			}
		}

		return result;
	}

	private static async Task<(string? Body, string? Crumb)> FetchOnceAsync(HttpClient client, string ticker, string? crumb, CancellationToken cancellation)
	{
		var url = BuildUrl(EndpointBase, ticker, crumb);
		using var resp = await client.GetAsync(url, cancellation);
		if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			var newCrumb = await TryGetCrumbAsync(client, cancellation);
			if (!string.IsNullOrWhiteSpace(newCrumb))
			{
				using var retry = await client.GetAsync(BuildUrl(EndpointBase, ticker, newCrumb), cancellation);
				if (retry.IsSuccessStatusCode)
					return (await retry.Content.ReadAsStringAsync(cancellation), newCrumb);
				using var fallback = await client.GetAsync(BuildUrl(FallbackEndpointBase, ticker, newCrumb), cancellation);
				if (fallback.IsSuccessStatusCode)
					return (await fallback.Content.ReadAsStringAsync(cancellation), newCrumb);
			}
			return (null, newCrumb);
		}
		if (!resp.IsSuccessStatusCode) return (null, crumb);
		return (await resp.Content.ReadAsStringAsync(cancellation), crumb);
	}

	private static string BuildUrl(string baseUrl, string ticker, string? crumb)
	{
		var url = $"{baseUrl}/{Uri.EscapeDataString(ticker)}?modules=calendarEvents";
		if (!string.IsNullOrWhiteSpace(crumb))
			url += $"&crumb={Uri.EscapeDataString(crumb)}";
		return url;
	}

	private static async Task<string?> TryGetCrumbAsync(HttpClient client, CancellationToken cancellation)
	{
		try
		{
			_ = await client.GetAsync(CookieBootstrapUrl, cancellation);
			var crumb = await client.GetStringAsync(CrumbUrl, cancellation);
			crumb = crumb?.Trim();
			return string.IsNullOrWhiteSpace(crumb) ? null : crumb;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Parses Yahoo's <c>quoteSummary?modules=calendarEvents</c> response body for one ticker.
	/// Public to enable testing without a live network. Returns null on missing/malformed payload —
	/// callers treat null as "no events known" and skip the veto.</summary>
	internal static TickerEvents? ParseResponse(string ticker, string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("quoteSummary", out var summary)) return null;
			if (!summary.TryGetProperty("result", out var resultArr) || resultArr.ValueKind != JsonValueKind.Array || resultArr.GetArrayLength() == 0) return null;
			if (!resultArr[0].TryGetProperty("calendarEvents", out var ev)) return null;

			DateTime? earningsDate = null;
			string? earningsTime = null;
			if (ev.TryGetProperty("earnings", out var earnings))
			{
				if (earnings.TryGetProperty("earningsDate", out var datesArr) && datesArr.ValueKind == JsonValueKind.Array)
				{
					foreach (var d in datesArr.EnumerateArray())
					{
						var unix = TryGetUnixTimestamp(d);
						if (!unix.HasValue) continue;
						earningsDate = DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime.Date;
						break;
					}
				}
				if (earnings.TryGetProperty("earningsCallTime", out var callTimeEl) && callTimeEl.ValueKind == JsonValueKind.String)
					earningsTime = callTimeEl.GetString();
			}

			DateTime? exDivDate = null;
			if (ev.TryGetProperty("exDividendDate", out var exDivEl))
			{
				var unix = TryGetUnixTimestamp(exDivEl);
				if (unix.HasValue) exDivDate = DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime.Date;
			}

			decimal? divAmount = null;
			if (ev.TryGetProperty("dividendDate", out var divDateEl) && divDateEl.ValueKind == JsonValueKind.Object)
			{
				// Some payloads expose the amount on dividendDate.value; others on a separate field. Best-effort.
				if (divDateEl.TryGetProperty("raw", out var rawEl) && rawEl.ValueKind == JsonValueKind.Number)
				{
					/* dividendDate.raw is a timestamp not amount; ignore. */
				}
			}
			// dividendRate / trailingAnnualDividendRate live in summaryDetail, not calendarEvents — we
			// don't fetch that module to keep the request light. Leave null; the soft factor doesn't use it.

			if (!earningsDate.HasValue && !exDivDate.HasValue) return null;
			return new TickerEvents(ticker.ToUpperInvariant(), earningsDate, earningsTime, exDivDate, divAmount);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static long? TryGetUnixTimestamp(JsonElement el)
	{
		if (el.ValueKind == JsonValueKind.Number) return el.GetInt64();
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Number)
			return raw.GetInt64();
		return null;
	}

	private sealed class CacheEnvelope
	{
		public string FetchedAt { get; set; } = "";
		public string Body { get; set; } = "";
	}

	private static string SerializeCache(DateTime asOf, string body) =>
		JsonSerializer.Serialize(new CacheEnvelope
		{
			FetchedAt = asOf.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
			Body = body,
		});

	private static TickerEvents? TryReadCache(string cachePath, DateTime asOf)
	{
		if (!File.Exists(cachePath)) return null;
		try
		{
			var raw = File.ReadAllText(cachePath);
			var env = JsonSerializer.Deserialize<CacheEnvelope>(raw);
			if (env == null || string.IsNullOrEmpty(env.Body)) return null;
			if (!DateTime.TryParse(env.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetchedAt)) return null;
			// Reject cache entries whose fetchedAt is too far from asOf in either direction.
			// Existing-side check (asOf - fetchedAt > TTL) catches stale caches in live use.
			// Future-side check (fetchedAt - asOf > TTL) prevents a backtest replaying a past
			// date from inheriting a cache fetched LATER than asOf — Yahoo's quoteSummary
			// returns the "next earnings / ex-div" as of the fetch time, so a cache written
			// today would leak future events into a 2025 replay. Symmetric tolerance keeps
			// both behaviors with one check.
			var driftAbs = (asOf.ToUniversalTime() - fetchedAt).Duration();
			if (driftAbs > CacheTtl) return null;

			var ticker = Path.GetFileNameWithoutExtension(cachePath);
			return ParseResponse(ticker, env.Body);
		}
		catch (IOException) { return null; }
		catch (JsonException) { return null; }
	}
}
