using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WebullAnalytics.Sentiment;

/// <summary>Fetches CNN's Fear & Greed Index from the public dataviz endpoint, parses the response
/// into a <see cref="SentimentSnapshot"/>, and caches the raw JSON to <c>data/sentiment-cache/{date}.json</c>.
/// The endpoint requires a browser-shaped User-Agent + Origin/Referer pair; without them it returns 418.
/// Failures (network, 4xx/5xx, malformed JSON) return null so callers can degrade gracefully — the
/// sentiment factor / rule are opt-in extras and must never block scoring on outage.
///
/// Cache freshness mirrors <c>HistoricalPriceCache</c>: a date is "settled" once NY time crosses 5pm
/// on that date or later. Settled dates are cached forever (the published score is immutable).
/// Today's intra-day reading is never written to disk because the score moves throughout the session
/// and we don't want a stale mid-day snapshot to masquerade as the day's final value on the next run.
/// Each call before settlement re-fetches from CNN.
///
/// Cache writes are also gated on the date being a trading day (<see cref="MarketCalendar.IsOpen"/>):
/// CNN serves a snapshot for any calendar date, but on a weekend/holiday the payload's internal
/// timestamp points at the prior trading day. Persisting a weekend file with the prior Friday's
/// data inside silently duplicates Friday and confuses anything that joins on the filename date.
/// Non-trading-day fetches still return a snapshot in-memory; they just don't persist.</summary>
internal static class FearGreedClient
{
	private const string Endpoint = "https://production.dataviz.cnn.io/index/fearandgreed/graphdata";
	private const string CacheDir = "data/sentiment-cache";
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan SettlementCutoff = TimeSpan.FromHours(17);

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

	/// <summary>Returns the sentiment snapshot for <paramref name="asOf"/>, using the on-disk cache when
	/// the date is settled. Intra-day calls always hit the network. Returns null on any failure.</summary>
	public static async Task<SentimentSnapshot?> FetchAsync(DateTime asOf, CancellationToken cancellation)
	{
		var dateStr = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var cachePath = Program.ResolvePath(Path.Combine(CacheDir, $"{dateStr}.json"));
		var settled = IsSettled(asOf, DateTime.UtcNow);

		if (settled && File.Exists(cachePath))
		{
			try
			{
				var cached = await File.ReadAllTextAsync(cachePath, cancellation);
				var parsed = ParseResponse(cached);
				if (parsed != null) return parsed;
			}
			catch (IOException) { }
			catch (JsonException) { }
		}

		string body;
		try
		{
			using var resp = await Http.GetAsync($"{Endpoint}/{dateStr}", cancellation);
			if (!resp.IsSuccessStatusCode) return null;
			body = await resp.Content.ReadAsStringAsync(cancellation);
		}
		catch (HttpRequestException) { return null; }
		catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { return null; }

		if (settled && MarketCalendar.IsOpen(asOf.Date))
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
				await File.WriteAllTextAsync(cachePath, body, cancellation);
			}
			catch (IOException) { }
		}

		return ParseResponse(body);
	}

	/// <summary>True when <paramref name="asOf"/> is on or before the most recent settled NY date.
	/// Internal so tests can verify the cutoff logic without depending on wall-clock time.</summary>
	internal static bool IsSettled(DateTime asOf, DateTime utcNow)
	{
		var nowNy = TimeZoneInfo.ConvertTimeFromUtc(utcNow, NyTz);
		var latestSettled = nowNy.TimeOfDay >= SettlementCutoff ? nowNy.Date : nowNy.Date.AddDays(-1);
		return asOf.Date <= latestSettled;
	}

	/// <summary>Parses the CNN response body. Public for unit tests; production callers use
	/// <see cref="FetchAsync"/>.</summary>
	internal static SentimentSnapshot? ParseResponse(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (!root.TryGetProperty("fear_and_greed", out var fg)) return null;
			if (!fg.TryGetProperty("score", out var scoreEl)) return null;

			var score = (decimal)scoreEl.GetDouble();
			var rating = fg.TryGetProperty("rating", out var rEl) ? rEl.GetString() ?? SentimentRating.FromScore(score) : SentimentRating.FromScore(score);
			var ts = TryParseCompositeTimestamp(fg);
			var prevClose = TryGetDecimal(fg, "previous_close");
			var prev1w = TryGetDecimal(fg, "previous_1_week");
			var prev1m = TryGetDecimal(fg, "previous_1_month");
			var prev1y = TryGetDecimal(fg, "previous_1_year");

			var components = new List<SentimentComponent>(7);
			AddComponent(root, "market_momentum_sp500", "S&P 500 momentum (vs 125-day MA)", components);
			AddComponent(root, "stock_price_strength", "Stock price strength (52-wk highs vs lows)", components);
			AddComponent(root, "stock_price_breadth", "Stock price breadth (McClellan Volume)", components);
			AddComponent(root, "put_call_options", "Put/call options ratio", components);
			AddComponent(root, "market_volatility_vix", "Market volatility (VIX)", components);
			AddComponent(root, "junk_bond_demand", "Junk bond demand (yield spread)", components);
			AddComponent(root, "safe_haven_demand", "Safe haven demand (stocks vs bonds)", components);

			return new SentimentSnapshot(score, rating, ts, prevClose, prev1w, prev1m, prev1y, components);
		}
		catch (JsonException) { return null; }
		catch (FormatException) { return null; }
	}

	private static void AddComponent(JsonElement root, string key, string label, List<SentimentComponent> sink)
	{
		if (!root.TryGetProperty(key, out var el)) return;
		if (!el.TryGetProperty("score", out var scoreEl)) return;

		var score = (decimal)scoreEl.GetDouble();
		var rating = el.TryGetProperty("rating", out var rEl) ? rEl.GetString() ?? SentimentRating.FromScore(score) : SentimentRating.FromScore(score);
		var raw = TryGetRawValue(el);
		var ts = TryParseComponentTimestamp(el);

		sink.Add(new SentimentComponent(key, label, score, rating, raw, ts));
	}

	private static decimal? TryGetDecimal(JsonElement obj, string field) =>
		obj.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.Number ? (decimal)el.GetDouble() : null;

	/// <summary>Best-effort ISO 8601 parse of the composite timestamp. Falls back to <c>DateTime.UtcNow</c>
	/// on any parse failure — the snapshot is still useful without a precise timestamp.</summary>
	private static DateTime TryParseCompositeTimestamp(JsonElement fg)
	{
		if (!fg.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String) return DateTime.UtcNow;
		var raw = tsEl.GetString();
		if (string.IsNullOrEmpty(raw)) return DateTime.UtcNow;
		if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
			return dto.UtcDateTime;
		return DateTime.UtcNow;
	}

	/// <summary>Per-component timestamps come back as Unix epoch millis. Falls back to <c>UtcNow</c> on
	/// any unexpected shape.</summary>
	private static DateTime TryParseComponentTimestamp(JsonElement component)
	{
		if (!component.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.Number) return DateTime.UtcNow;
		try { return DateTimeOffset.FromUnixTimeMilliseconds((long)tsEl.GetDouble()).UtcDateTime; }
		catch (ArgumentOutOfRangeException) { return DateTime.UtcNow; }
		catch (InvalidOperationException) { return DateTime.UtcNow; }
	}

	/// <summary>Pulls the underlying market reading from the component's <c>data[0].y</c> entry. CNN
	/// always emits a single-point series there with the raw value before normalization.</summary>
	private static decimal? TryGetRawValue(JsonElement component)
	{
		if (!component.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
		foreach (var pt in arr.EnumerateArray())
		{
			if (pt.TryGetProperty("y", out var yEl) && yEl.ValueKind == JsonValueKind.Number)
				return (decimal)yEl.GetDouble();
		}
		return null;
	}
}
