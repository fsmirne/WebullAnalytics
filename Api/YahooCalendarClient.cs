using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
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
	// Cookie-bootstrap candidates, tried in order. fc.yahoo.com is the legacy endpoint (often dead now);
	// finance.yahoo.com is the live consent page that seats the A1/A3 cookie getcrumb needs.
	private static readonly string[] CookieBootstrapUrls = { "https://fc.yahoo.com", "https://finance.yahoo.com" };
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

				var parsed = body != null ? ParseResponse(ticker, body) : null;

				// quoteSummary carries earnings + (for equities) an exact ex-dividend date, but for ETFs/
				// indices the dividend fields come back empty — and its crumb path is often blocked anyway.
				// The crumb-free chart endpoint (events=div) is the authoritative source for the per-payment
				// cash amount (the real last dividend, vs quoteSummary's stale trailing-annual/4) and supplies
				// the next ex-date (projected) when quoteSummary has none. So: prefer quoteSummary's ex-date
				// when present (exact), else the chart's; always prefer the chart's amount when available.
				if (parsed == null || !parsed.NextExDividendDate.HasValue || !parsed.DividendAmount.HasValue)
				{
					var chartDiv = await TryFetchNextDividendFromChartAsync(client, ticker, asOf, cancellation);
					if (chartDiv != null)
						parsed = parsed == null
							? new TickerEvents(ticker.ToUpperInvariant(), null, null, chartDiv.Value.ExDate, chartDiv.Value.Amount)
							: parsed with
							{
								NextExDividendDate = parsed.NextExDividendDate ?? chartDiv.Value.ExDate,
								DividendAmount = chartDiv.Value.Amount,
							};
				}

				if (parsed == null) continue;

				result[ticker] = parsed;
				try
				{
					var cachePath = Path.Combine(cacheDir, $"{ticker}.json");
					await File.WriteAllTextAsync(cachePath, SerializeCache(asOf, parsed), cancellation);
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
		var url = $"{baseUrl}/{Uri.EscapeDataString(ticker)}?modules=calendarEvents,summaryDetail";
		if (!string.IsNullOrWhiteSpace(crumb))
			url += $"&crumb={Uri.EscapeDataString(crumb)}";
		return url;
	}

	private static async Task<string?> TryGetCrumbAsync(HttpClient client, CancellationToken cancellation)
	{
		// Best-effort cookie seeding: a dead/throwing bootstrap URL must NOT abort the crumb attempt —
		// the old code's single try/catch around bootstrap + getcrumb meant one failed fc.yahoo.com GET
		// skipped getcrumb entirely. Seat a cookie from whichever candidate responds, then ask for a crumb.
		foreach (var url in CookieBootstrapUrls)
		{
			try
			{
				using var seed = await client.GetAsync(url, cancellation);
				if (seed.IsSuccessStatusCode) break;
			}
			catch (Exception ex) when (ex is not OperationCanceledException) { /* try the next candidate */ }
		}

		try
		{
			var crumb = (await client.GetStringAsync(CrumbUrl, cancellation))?.Trim();
			// On failure getcrumb returns a 200 with an error JSON blob ({"finance":{"error":…}}) rather
			// than a crumb token. A real crumb is a short opaque token and never starts with '{'.
			if (string.IsNullOrWhiteSpace(crumb) || crumb.StartsWith('{')) return null;
			return crumb;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
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
						earningsDate = UnixToCalendarDate(unix.Value);
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
				if (unix.HasValue) exDivDate = UnixToCalendarDate(unix.Value);
			}

			// Per-payment cash dividend amount. calendarEvents.dividendDate is a PAY-date timestamp, not an
			// amount, so the amount comes from summaryDetail.dividendRate (forward ANNUAL rate). We assume
			// quarterly payments (true for effectively all liquid US optionable dividend payers — SPY,
			// large-caps) and divide by 4 to land a per-payment estimate. Consumers that know better can
			// override via the event-calendar override file (exact exDividend + dividendAmount).
			decimal? divAmount = null;
			if (resultArr[0].TryGetProperty("summaryDetail", out var summaryDetail) && summaryDetail.ValueKind == JsonValueKind.Object)
			{
				var annualRate = TryGetRawDecimal(summaryDetail, "dividendRate") ?? TryGetRawDecimal(summaryDetail, "trailingAnnualDividendRate");
				if (annualRate is decimal rate && rate > 0m)
					divAmount = decimal.Round(rate / 4m, 4);
			}

			if (!earningsDate.HasValue && !exDivDate.HasValue) return null;
			return new TickerEvents(ticker.ToUpperInvariant(), earningsDate, earningsTime, exDivDate, divAmount);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>Crumb-free dividend source: Yahoo's chart endpoint (<c>events=div</c>) returns historical
	/// ex-dates + cash amounts without the quoteSummary crumb dance. The NEXT ex-date isn't published
	/// here, so we project it forward from the most recent ex-date by the median spacing of recent
	/// dividends (≈ quarterly for typical payers); the amount is the most recent actual dividend. Returns
	/// null for non-payers / unmapped roots / on any failure (best-effort — never throws to the caller).</summary>
	private static async Task<(DateTime ExDate, decimal Amount)?> TryFetchNextDividendFromChartAsync(HttpClient client, string ticker, DateTime asOf, CancellationToken cancellation)
	{
		try
		{
			var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}?range=2y&interval=1d&events=div";
			using var resp = await client.GetAsync(url, cancellation);
			if (!resp.IsSuccessStatusCode) return null;
			return ParseNextDividendFromChart(await resp.Content.ReadAsStringAsync(cancellation), asOf);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>Parses a Yahoo chart <c>events=div</c> body into the projected next dividend. Public for
	/// testing without a live network. Returns null on missing/malformed payload or a non-payer. The next
	/// ex-date is projected from the most recent historical ex-date by the median dividend spacing; the
	/// amount is the most recent actual dividend.</summary>
	internal static (DateTime ExDate, decimal Amount)? ParseNextDividendFromChart(string json, DateTime asOf)
	{
		var history = ParseDividendHistory(json);
		if (history.Count == 0) return null;

		var lastEx = history[^1].Date;
		var amount = history[^1].Amount;

		// Median spacing between consecutive ex-dates (days); default to a quarter when only one is known.
		var interval = 91;
		if (history.Count >= 2)
		{
			var gaps = new List<int>();
			for (int i = 1; i < history.Count; i++) gaps.Add((int)(history[i].Date - history[i - 1].Date).TotalDays);
			gaps.Sort();
			interval = Math.Max(1, gaps[gaps.Count / 2]);
		}

		// Project forward to the first ex-date on/after asOf. Guard bounds the loop if interval is tiny.
		var next = lastEx;
		for (var guard = 0; next < asOf.Date && guard < 40; guard++) next = next.AddDays(interval);
		return (next, amount);
	}

	/// <summary>Parses a Yahoo chart <c>events=div</c> body into the full historical schedule of ACTUAL
	/// (ex-date, amount) payments, oldest-first. Unlike <see cref="ParseNextDividendFromChart"/> — which
	/// keeps only the most recent payment and projects the NEXT date forward — this returns ground truth:
	/// every dividend that actually went ex within the chart's range. The backtest consumes this so that
	/// historical option pricing sees the real ex-date and cash amount that fell inside a leg's life,
	/// matching what the live dividend-aware Black-Scholes computes. Empty for non-payers / malformed /
	/// missing payloads (→ caller applies no adjustment, the correct behaviour for index roots).</summary>
	internal static IReadOnlyList<DividendEvent> ParseDividendHistoryFromChart(string json) =>
		ParseDividendHistory(json).Select(h => new DividendEvent(h.Date, h.Amount)).ToList();

	/// <summary>Shared extraction of the chart endpoint's <c>events.dividends</c> map into a date-sorted
	/// list. Never throws — malformed JSON or a non-payer yields an empty list.</summary>
	private static List<(DateTime Date, decimal Amount)> ParseDividendHistory(string json)
	{
		var history = new List<(DateTime Date, decimal Amount)>();
		if (string.IsNullOrWhiteSpace(json)) return history;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("chart", out var chart)) return history;
			if (!chart.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return history;
			if (!arr[0].TryGetProperty("events", out var events) || !events.TryGetProperty("dividends", out var divs) || divs.ValueKind != JsonValueKind.Object) return history;

			foreach (var prop in divs.EnumerateObject())
			{
				var d = prop.Value;
				if (!d.TryGetProperty("date", out var dateEl) || dateEl.ValueKind != JsonValueKind.Number) continue;
				if (!d.TryGetProperty("amount", out var amtEl) || amtEl.ValueKind != JsonValueKind.Number) continue;
				var amt = amtEl.GetDecimal();
				if (amt <= 0m) continue;
				history.Add((UnixToCalendarDate(dateEl.GetInt64()), amt));
			}
			history.Sort((a, b) => a.Date.CompareTo(b.Date));
		}
		catch (JsonException)
		{
			return new List<(DateTime, decimal)>();
		}
		return history;
	}

	/// <summary>Fetches the full historical dividend schedule for <paramref name="ticker"/> from the
	/// crumb-free chart endpoint (<c>events=div</c>) over <paramref name="range"/> (e.g. <c>5y</c>).
	/// Best-effort: returns an empty list on any network/parse failure or for a non-payer. Constructs its
	/// own short-lived <see cref="HttpClient"/> — this is a one-shot per-ticker call used by
	/// <c>wa ai history</c>, not the batched event fetch.</summary>
	internal static async Task<IReadOnlyList<DividendEvent>> FetchDividendHistoryAsync(string ticker, string range, CancellationToken cancellation)
	{
		try
		{
			using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
			using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
			var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}?range={Uri.EscapeDataString(range)}&interval=1d&events=div";
			using var resp = await client.GetAsync(url, cancellation);
			if (!resp.IsSuccessStatusCode) return Array.Empty<DividendEvent>();
			return ParseDividendHistoryFromChart(await resp.Content.ReadAsStringAsync(cancellation));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return Array.Empty<DividendEvent>();
		}
	}

	/// <summary>Yahoo timestamps are mid-session instants (e.g. ~09:30 ET ≈ 13:30 UTC), so the UTC
	/// calendar date equals the ET ex-/earnings date. Returned as Kind=Unspecified to match the
	/// NY-local calendar-date contract of <see cref="TickerEvents"/> (and so it serializes without a Z).</summary>
	private static DateTime UnixToCalendarDate(long unixSeconds) =>
		DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Date, DateTimeKind.Unspecified);

	private static long? TryGetUnixTimestamp(JsonElement el)
	{
		if (el.ValueKind == JsonValueKind.Number) return el.GetInt64();
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Number)
			return raw.GetInt64();
		return null;
	}

	/// <summary>Reads a Yahoo numeric field that may be a bare number or a {raw,fmt} object.</summary>
	private static decimal? TryGetRawDecimal(JsonElement parent, string name)
	{
		if (!parent.TryGetProperty(name, out var el)) return null;
		if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
		if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Number)
			return raw.GetDecimal();
		return null;
	}

	private sealed class CacheEnvelope
	{
		public string FetchedAt { get; set; } = "";
		// Stores the final parsed+merged TickerEvents (quoteSummary earnings + chart-derived dividend),
		// not the raw response body — so chart-sourced dividends survive a cache round-trip and the file
		// stays human-readable (no nested-JSON " escaping).
		public TickerEvents? Events { get; set; }
	}

	private static readonly JsonSerializerOptions CacheJsonOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
	};

	private static string SerializeCache(DateTime asOf, TickerEvents events) =>
		JsonSerializer.Serialize(new CacheEnvelope
		{
			FetchedAt = asOf.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
			Events = events,
		}, CacheJsonOptions);

	private static TickerEvents? TryReadCache(string cachePath, DateTime asOf)
	{
		if (!File.Exists(cachePath)) return null;
		try
		{
			var raw = File.ReadAllText(cachePath);
			var env = JsonSerializer.Deserialize<CacheEnvelope>(raw, CacheJsonOptions);
			if (env?.Events == null) return null;
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

			return env.Events;
		}
		catch (IOException) { return null; }
		catch (JsonException) { return null; }
	}
}
