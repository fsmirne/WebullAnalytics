using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WebullAnalytics.Api;

/// <summary>Fetches 1-minute aggregate bars from massive.com (Polygon-mirror endpoint shape). Used by
/// <c>wa ai history</c> to backfill <c>data/intraday/<TICKER>/<date>.csv</c> for dates the
/// live Webull session missed (holidays, outages, late starts). Same auth/response shape as Polygon.
///
/// Auth: <c>apiKey</c> query param. Returns oldest-first <see cref="MinuteBar"/> across [from, to]
/// (inclusive). Empty on transport failure, non-2xx, or HTTP 429 — never throws unless cancelled.
/// Honors Polygon's <c>next_url</c> pagination (50,000-row page cap); appends <c>apiKey</c> on each
/// follow-up because the mirror strips it from <c>next_url</c>.
///
/// Rate limit: basic tier is 5 req/min; 6th request in a minute returns HTTP 429. Caller is
/// responsible for pacing across tickers — a single ticker/date-range typically fits in one or two
/// requests so per-invocation pacing is rarely needed.</summary>
internal static class MassivePolygonClient
{
	private const string BaseUrl = "https://api.massive.com";

	// Requests-per-60s cap. Track the timestamps of the most recent up-to-MaxRequestsPerWindow requests;
	// when the next is about to exceed it, wait just long enough for the oldest of those to age past the
	// 60s window. Burst-then-wait is materially faster than evenly-spacing requests — 5 paginated pages
	// complete in ~5s instead of ~52s — without exceeding the cap. Static across the process so pagination
	// loops and any back-to-back call sites observe the same rolling window.
	//
	// Defaults to the basic (free) tier's 5 req/min. Set from api-config.json's massiveMaxRequestsPerMinute
	// to match a paid tier (Options Starter+ is "Unlimited API Calls"); 0 disables pacing entirely. The
	// backfill entry point assigns it once at startup before any fetch fires.
	internal static int MaxRequestsPerWindow { get; set; } = 5;
	private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan RateWindowSlack = TimeSpan.FromMilliseconds(500);
	private static readonly SemaphoreSlim PaceLock = new(1, 1);
	private static readonly Queue<DateTime> RecentRequests = new();

	// 429 recovery: massive.com / Polygon return Retry-After when the rate limit is hit. Honor it (with
	// a sane floor and ceiling) and retry the same URL a couple of times before giving up — the pacing
	// above should prevent 429 in steady state, but the first burst on a cold process or a clock skew
	// can still trip it.
	private const int MaxRetriesPerUrl = 2;
	private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(90);

	// Shared, long-lived client. Creating one HttpClient per call (the `using var client = new HttpClient()`
	// pattern) churns through TCP sockets/ports — under a sustained bulk pull (e.g. 1000+ option contracts
	// back-to-back) massive's edge starts dropping connections at the TLS handshake, surfacing as
	// "The SSL connection could not be established" mid-loop. A single pooled client (HTTP/2 keep-alive)
	// avoids the churn. Same fix already applied to WebullChartsClient for the identical symptom.
	private static readonly HttpClient SharedClient = new();
	// Backoff for transient transport errors (SSL handshake drops, connection resets). Distinct from the
	// 429 path: those are rate-limit signals with Retry-After; these are connection-level hiccups that
	// usually clear on an immediate retry once the socket pool recovers.
	private static readonly TimeSpan TransportRetryDelay = TimeSpan.FromSeconds(2);

	internal static async Task<IReadOnlyList<MinuteBar>> FetchMinuteAggregatesAsync(
		string apiKey,
		string ticker,
		DateOnly from,
		DateOnly to,
		CancellationToken cancellation)
	{
		if (string.IsNullOrWhiteSpace(apiKey))
		{
			Console.WriteLine("Massive: apiKey is empty; skipping fetch.");
			return Array.Empty<MinuteBar>();
		}
		if (from > to) return Array.Empty<MinuteBar>();

		var client = SharedClient;
		var encodedTicker = Uri.EscapeDataString(ticker);
		var encodedKey = Uri.EscapeDataString(apiKey);
		var url = $"{BaseUrl}/v2/aggs/ticker/{encodedTicker}/range/1/minute/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}?adjusted=true&sort=asc&limit=50000&apiKey={encodedKey}";

		var all = new List<MinuteBar>();
		var page = 0;
		while (!string.IsNullOrEmpty(url))
		{
			page++;
			var retries = 0;
			while (true)
			{
				await ThrottleAsync(cancellation);

				HttpResponseMessage response;
				try
				{
					response = await client.GetAsync(url, cancellation);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// Transient transport error (TLS handshake drop, connection reset). Retry a couple
					// of times with a short backoff before giving up — the shared pooled client usually
					// recovers the socket immediately. The throttle still applies on the retry.
					if (retries < MaxRetriesPerUrl)
					{
						Console.WriteLine($"Massive: transport error on {ticker} page {page} ({ex.Message}); retry {retries + 1}/{MaxRetriesPerUrl} in {TransportRetryDelay.TotalSeconds:F0}s.");
						try { await Task.Delay(TransportRetryDelay, cancellation); }
						catch (OperationCanceledException) { return all; }
						retries++;
						continue;
					}
					Console.WriteLine($"Massive: request failed for {ticker} page {page} after {MaxRetriesPerUrl} retries: {ex.Message}");
					return all;
				}

				using (response)
				{
					if (response.StatusCode == HttpStatusCode.TooManyRequests && retries < MaxRetriesPerUrl)
					{
						var wait = ResolveRetryAfter(response);
						Console.WriteLine($"Massive: HTTP 429 on {ticker} page {page}; pausing {wait.TotalSeconds:F0}s before retry ({retries + 1}/{MaxRetriesPerUrl}).");
						try { await Task.Delay(wait, cancellation); }
						catch (OperationCanceledException) { return all; }
						retries++;
						continue;
					}

					if (!response.IsSuccessStatusCode)
					{
						var status = (int)response.StatusCode;
						var detail = ExtractErrorDetail(await response.Content.ReadAsStringAsync(cancellation));
						var hint = status switch
						{
							429 => " (rate limit; retries exhausted — basic tier caps at 5 req/min)",
							401 => " (auth failure; check apiKey)",
							403 => " (not authorized — plan entitlement, e.g. data timeframe)",
							_ => "",
						};
						Console.WriteLine($"Massive: HTTP {status} for {ticker} page {page}{hint}{(detail is null ? "" : $": {detail}")}.");
						return all;
					}

					var json = await response.Content.ReadAsStringAsync(cancellation);
					var (bars, nextUrl) = ParseAggregatesResponse(json);
					all.AddRange(bars);
					if (string.IsNullOrEmpty(nextUrl))
					{
						url = null;
					}
					else
					{
						var sep = nextUrl.Contains('?') ? '&' : '?';
						url = $"{nextUrl}{sep}apiKey={encodedKey}";
					}
					break;
				}
			}
		}
		return all;
	}

	/// <summary>Implements the rolling-window rate limit. Drops timestamps older than 60s, then either
	/// admits the request immediately (queue has slots) or sleeps until the oldest queued timestamp
	/// exits the window. Adds a small slack so a borderline timestamp definitely ages out before we
	/// retry.</summary>
	private static async Task ThrottleAsync(CancellationToken cancellation)
	{
		if (MaxRequestsPerWindow <= 0) return; // unlimited tier — no self-throttle
		await PaceLock.WaitAsync(cancellation);
		try
		{
			var now = DateTime.UtcNow;
			while (RecentRequests.Count > 0 && now - RecentRequests.Peek() > RateWindow)
				RecentRequests.Dequeue();

			if (RecentRequests.Count >= MaxRequestsPerWindow)
			{
				var oldest = RecentRequests.Peek();
				var wait = RateWindow - (now - oldest) + RateWindowSlack;
				if (wait > TimeSpan.Zero)
				{
					Console.WriteLine($"Massive: rate-limit window full ({MaxRequestsPerWindow} req in last 60s); waiting {wait.TotalSeconds:F0}s.");
					await Task.Delay(wait, cancellation);
				}
				RecentRequests.Dequeue();
			}

			RecentRequests.Enqueue(DateTime.UtcNow);
		}
		finally
		{
			PaceLock.Release();
		}
	}

	/// <summary>Reads the <c>Retry-After</c> header (seconds or HTTP-date) and clamps it to a sane
	/// window. Falls back to <see cref="DefaultRetryAfter"/> if the header is missing or unparseable.</summary>
	private static TimeSpan ResolveRetryAfter(HttpResponseMessage response)
	{
		var headers = response.Headers;
		TimeSpan? requested = null;
		if (headers.RetryAfter is { } retryAfter)
		{
			if (retryAfter.Delta.HasValue) requested = retryAfter.Delta.Value;
			else if (retryAfter.Date.HasValue)
			{
				var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
				if (delta > TimeSpan.Zero) requested = delta;
			}
		}
		var picked = requested ?? DefaultRetryAfter;
		if (picked < MinRetryAfter) picked = MinRetryAfter;
		if (picked > MaxRetryAfter) picked = MaxRetryAfter;
		return picked;
	}

	/// <summary>Pulls the human-readable error string from a Polygon/massive error body — <c>message</c>
	/// (NOT_AUTHORIZED responses, e.g. "Your plan doesn't include this data timeframe") or <c>error</c>
	/// (e.g. "API Key was not provided"). Null when the body isn't JSON or carries neither field, so the
	/// caller can omit the suffix. Surfacing this turns an opaque "403 (check apiKey)" into the upstream's
	/// actual reason — the hint alone misdiagnoses entitlement failures as auth failures.</summary>
	private static string? ExtractErrorDetail(string body)
	{
		if (string.IsNullOrWhiteSpace(body)) return null;
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
			if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
		}
		catch { /* non-JSON body — nothing to surface */ }
		return null;
	}

	internal static (IReadOnlyList<MinuteBar> Bars, string? NextUrl) ParseAggregatesResponse(string json)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			string? next = null;
			if (root.TryGetProperty("next_url", out var nu) && nu.ValueKind == JsonValueKind.String)
				next = nu.GetString();

			if (!root.TryGetProperty("results", out var res) || res.ValueKind != JsonValueKind.Array)
				return (Array.Empty<MinuteBar>(), next);

			var bars = new List<MinuteBar>(res.GetArrayLength());
			foreach (var row in res.EnumerateArray())
			{
				if (row.ValueKind != JsonValueKind.Object) continue;
				if (!row.TryGetProperty("t", out var tEl) || !tEl.TryGetInt64(out var tMs)) continue;
				if (!row.TryGetProperty("o", out var oEl) || !oEl.TryGetDecimal(out var o)) continue;
				if (!row.TryGetProperty("h", out var hEl) || !hEl.TryGetDecimal(out var h)) continue;
				if (!row.TryGetProperty("l", out var lEl) || !lEl.TryGetDecimal(out var l)) continue;
				if (!row.TryGetProperty("c", out var cEl) || !cEl.TryGetDecimal(out var c)) continue;
				long v = 0;
				if (row.TryGetProperty("v", out var vEl) && vEl.TryGetDecimal(out var vDec))
					v = (long)Math.Max(0m, Math.Round(vDec));

				bars.Add(new MinuteBar(DateTimeOffset.FromUnixTimeMilliseconds(tMs), o, h, l, c, v));
			}
			bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
			return (bars, next);
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"Massive: failed to parse aggregates response: {ex.Message}");
			return (Array.Empty<MinuteBar>(), null);
		}
	}
}
