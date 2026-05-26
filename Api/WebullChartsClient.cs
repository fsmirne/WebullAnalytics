using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics.Api;

/// <summary>Fetches historical OHLCV bars from Webull's public charts endpoint. Stateless — each call
/// hits the wire and returns the most-recent <c>count</c> bars (oldest-first). Caller is responsible
/// for caching and merging across calls.
///
/// Endpoint coverage: m1 typically goes back ~5 trading days; m5/m15 several weeks; m30/h1 months;
/// d1 multi-year. SPX has no pre-market (cash index); pass SPY's tickerId when pre-market context
/// is needed. <see cref="WebullOptionsClient.KnownTickerIds"/> already has SPX/NDX/DJX/VIX.</summary>
internal static class WebullChartsClient
{
	private const string ChartsQueryUrl = "https://quotes-gw.webullfintech.com/api/quote/charts/query";
	private const string ChartsQueryMiniUrl = "https://quotes-gw.webullfintech.com/api/quote/charts/query-mini";
	private const string OptionChartKdataUrl = "https://quotes-gw.webullfintech.com/api/quote/option/chart/kdata";

	// Webull stamps each minute bar with the END of its aggregate window (the 09:31:00 bar covers
	// the 09:30→09:31 ET minute, containing the auction-cleared open). Polygon, ThinkOrSwim, and
	// TradingView all use the opposite convention — bar timestamp = START of the window — so a
	// 09:30:00 bar in those systems contains the same auction-open price. To make Webull's data
	// agree with the rest of the world, every parser in this file subtracts 60 seconds from the
	// raw timestamp before returning. Downstream code (cache lookups, intraday window bounds,
	// freshness checks) therefore always sees start-of-bar timestamps regardless of source.
	private static readonly TimeSpan WebullBarShift = TimeSpan.FromSeconds(-60);

	// Shared HttpClient. Creating one HttpClient per call (the `using var client = new HttpClient()`
	// pattern) churns through TCP sockets and ports — under sustained bulk pulls Webull's edge starts
	// dropping connections at the TLS handshake, which surfaces as "The SSL connection could not be
	// established" mid-loop. A single long-lived client with HTTP/2 connection pooling avoids it.
	private static readonly HttpClient SharedClient = new();

	static WebullChartsClient()
	{
		SharedClient.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");
		SharedClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
		SharedClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
		SharedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0");
	}

	private static readonly Dictionary<string, string> DefaultHeaders = new()
	{
		["app"] = "global",
		["app-group"] = "broker",
		["appid"] = "wb_web_app",
		["device-type"] = "Web",
		["hl"] = "en",
		["os"] = "web",
		["platform"] = "web",
	};

	/// <summary>Chart-endpoint tickerId map. Distinct from <see cref="WebullOptionsClient.KnownTickerIds"/>
	/// which serves Webull's option-chain endpoint — for cash indexes those two namespaces don't
	/// align (e.g. <c>913324359</c> is correct for SPX option chains but returns unrelated $200 data
	/// on the chart endpoint, while <c>913354362</c> is correct for the SPX index chart but is the
	/// SPXW option-chain ID). Verified empirically via <c>/api/stock/tickerRealTime/getQuote</c>.</summary>
	private static readonly Dictionary<string, long> ChartKnownTickerIds = new(StringComparer.OrdinalIgnoreCase)
	{
		["SPX"] = 913354362,
		["SPXW"] = 913354362,
	};

	/// <summary>True if a chart-namespace tickerId is registered for this symbol. Callers that
	/// otherwise fall back to <see cref="WebullOptionsClient.ResolveTickerIdsAsync"/> (option-chain
	/// namespace) should consult this map first to avoid the namespace mismatch.</summary>
	public static bool TryResolveKnownChartTickerId(string ticker, out long tickerId) =>
		ChartKnownTickerIds.TryGetValue(ticker, out tickerId);

	/// <summary>Fetches up to <paramref name="count"/> most-recent bars at the requested interval. Returns
	/// oldest-first, sorted by timestamp. Empty list on transport failure, non-2xx response, or unparseable
	/// payload — never throws unless cancelled. Caller decides what to do with the last bar (it may be the
	/// in-progress minute and so partial).
	///
	/// <paramref name="includeExtended"/>=true requests pre/post-market bars where the symbol supports
	/// them; cash indexes (SPX, NDX) ignore this and return only RTH. ETFs and single names honor it.</summary>
	public static async Task<IReadOnlyList<MinuteBar>> FetchIntradayBarsAsync(
		ApiConfig config,
		long tickerId,
		BarInterval interval,
		int count,
		bool includeExtended,
		CancellationToken cancellationToken)
	{
		if (count <= 0) return Array.Empty<MinuteBar>();

		using var client = new HttpClient();
		client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");

		var typeCode = BarIntervalToCode(interval);
		var url = $"{ChartsQueryUrl}?tickerIds={tickerId}&type={typeCode}&count={count}&extendTrading={(includeExtended ? 1 : 0)}";

		var request = new HttpRequestMessage(HttpMethod.Get, url);
		foreach (var (key, value) in DefaultHeaders) request.Headers.TryAddWithoutValidation(key, value);
		foreach (var (key, value) in config.Headers) request.Headers.TryAddWithoutValidation(key, value);

		HttpResponseMessage response;
		try
		{
			response = await client.SendAsync(request, cancellationToken);
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Webull charts: request failed for tickerId {tickerId}: {ex.Message}");
			return Array.Empty<MinuteBar>();
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				Console.WriteLine($"Webull charts: received {(int)response.StatusCode} for tickerId {tickerId}. Session may have expired — run 'sniff' to refresh.");
				return Array.Empty<MinuteBar>();
			}

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			return ParseChartsResponse(json, tickerId);
		}
	}

	/// <summary>Fetches up to <paramref name="count"/> minute bars at-or-before <paramref name="anchorUnixSec"/>
	/// (oldest-first). Hits Webull's <c>query-mini</c> endpoint, which — unlike the live <c>query</c>
	/// endpoint — accepts a <c>timestamp</c> anchor and serves arbitrary historical sessions. Required for
	/// <c>wa ai history</c> backfill: <c>query</c> only returns the most recent N bars, while
	/// <c>query-mini</c> + timestamp lets us pull any past session.
	///
	/// URL params (cargo-culted from Webull's web app DevTools to match what the chart UI sends so the
	/// endpoint doesn't degrade us into a truncated response): <c>overnight=1&amp;loadFactor=1&amp;restorationType=1</c>
	/// plus optional <c>extendTrading=1</c> for tickers with pre/post-market coverage. Empirically, the
	/// previous URL (only <c>restorationType=0</c>, no other params) silently truncated to a single bar
	/// despite valid auth — even though equivalent curl calls succeeded — likely a different code path
	/// triggered by the missing params combined with HttpClient's default Accept absence.
	///
	/// Header quirks discovered during the 2026-05-23 backfill investigation: (1) <c>t_time</c> is
	/// freshness-checked; stale values silently truncate to 1 bar. (2) <c>x-s</c>/<c>x-sv</c> are per-URL
	/// signatures from `wa sniff`; reusing them against a different URL also triggers truncation. We
	/// override <c>t_time</c> per-request and drop the signatures entirely. (3) HttpClient does not send
	/// <c>Accept</c>/<c>User-Agent</c> by default — the endpoint needs at least <c>Accept: */*</c> to
	/// route the request like a browser fetch; we set both explicitly.</summary>
	public static async Task<IReadOnlyList<MinuteBar>> FetchHistoricalMinuteBarsAsync(
		ApiConfig config,
		long tickerId,
		long anchorUnixSec,
		int count,
		bool includeExtended,
		CancellationToken cancellationToken)
	{
		if (count <= 0) return Array.Empty<MinuteBar>();

		// `loadFactor=1` is a "live mode" flag: when present, the endpoint silently ignores the
		// `timestamp` anchor for deep history (>3 days back) and returns the most-recent window
		// instead. Webull's web app sends `loadFactor=1` on live chart fetches but DROPS it when
		// paginating backward. We're always paginating here so we never send it.
		var ext = includeExtended ? 1 : 0;
		var url = $"{ChartsQueryMiniUrl}?overnight=1&type=m1&count={count}&timestamp={anchorUnixSec}&restorationType=1&extendTrading={ext}&tickerId={tickerId}";

		var request = new HttpRequestMessage(HttpMethod.Get, url);
		foreach (var (key, value) in DefaultHeaders) request.Headers.TryAddWithoutValidation(key, value);
		foreach (var (key, value) in config.Headers)
		{
			// Suppress the per-URL signature headers — they were computed for whatever request `wa sniff`
			// captured and don't match the historical URL we're about to send.
			if (string.Equals(key, "x-s", StringComparison.OrdinalIgnoreCase)) continue;
			if (string.Equals(key, "x-sv", StringComparison.OrdinalIgnoreCase)) continue;
			// t_time is overridden below.
			if (string.Equals(key, "t_time", StringComparison.OrdinalIgnoreCase)) continue;
			request.Headers.TryAddWithoutValidation(key, value);
		}
		request.Headers.TryAddWithoutValidation("t_time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

		HttpResponseMessage response;
		try
		{
			response = await SharedClient.SendAsync(request, cancellationToken);
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Webull charts (mini): request failed for tickerId {tickerId}: {ex.Message}");
			return Array.Empty<MinuteBar>();
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				Console.WriteLine($"Webull charts (mini): received {(int)response.StatusCode} for tickerId {tickerId}. Session may have expired — run 'sniff' to refresh.");
				return Array.Empty<MinuteBar>();
			}

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			return ParseChartsMiniResponse(json, tickerId);
		}
	}

	/// <summary>Fetches up to <paramref name="count"/> minute bars for a single option contract identified by its
	/// Webull <paramref name="derivativeId"/>. When <paramref name="anchorUnixSec"/> is null, returns the most-recent
	/// window (live mode); otherwise anchors at-or-before the given second-precision Unix timestamp for historical
	/// pulls. Returns oldest-first, sorted by timestamp. Each bar carries the contract's implied volatility (the
	/// trailing column Webull inlines in its option chart rows).
	///
	/// <paramref name="derivativeId"/> is Webull's per-contract integer ID — distinct from the underlying's
	/// <c>tickerId</c>. It comes from <see cref="WebullOptionsClient.FetchChainAsync"/>'s <c>DerivativeIds</c>
	/// dictionary or from the <c>tickerId</c> field on a strategy/list contract entry.
	///
	/// Header handling mirrors <see cref="FetchHistoricalMinuteBarsAsync"/>: drops the per-URL <c>x-s</c>/<c>x-sv</c>
	/// signatures from a sniffed config (they were computed for a different URL and would otherwise truncate the
	/// response) and overrides <c>t_time</c> with the current wall clock.</summary>
	public static async Task<IReadOnlyList<OptionMinuteBar>> FetchOptionContractMinuteBarsAsync(
		ApiConfig config,
		long derivativeId,
		int count,
		long? anchorUnixSec,
		CancellationToken cancellationToken)
	{
		if (count <= 0) return Array.Empty<OptionMinuteBar>();

		var url = anchorUnixSec.HasValue
			? $"{OptionChartKdataUrl}?derivativeId={derivativeId}&type=1m&count={count}&timestamp={anchorUnixSec.Value}"
			: $"{OptionChartKdataUrl}?derivativeId={derivativeId}&type=1m&count={count}";

		var request = new HttpRequestMessage(HttpMethod.Get, url);
		foreach (var (key, value) in DefaultHeaders) request.Headers.TryAddWithoutValidation(key, value);
		foreach (var (key, value) in config.Headers)
		{
			if (string.Equals(key, "x-s", StringComparison.OrdinalIgnoreCase)) continue;
			if (string.Equals(key, "x-sv", StringComparison.OrdinalIgnoreCase)) continue;
			if (string.Equals(key, "t_time", StringComparison.OrdinalIgnoreCase)) continue;
			request.Headers.TryAddWithoutValidation(key, value);
		}
		request.Headers.TryAddWithoutValidation("t_time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

		HttpResponseMessage response;
		try
		{
			response = await SharedClient.SendAsync(request, cancellationToken);
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Webull option chart: request failed for derivativeId {derivativeId}: {ex.Message}");
			return Array.Empty<OptionMinuteBar>();
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				Console.WriteLine($"Webull option chart: received {(int)response.StatusCode} for derivativeId {derivativeId}. Session may have expired — run 'sniff' to refresh.");
				return Array.Empty<OptionMinuteBar>();
			}

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			return ParseOptionChartResponse(json, derivativeId);
		}
	}

	/// <summary>Parses Webull's option-chart row schema: <c>ts,open,close,high,low,prevClose,volume,iv</c>. Same
	/// 8-column shape as <see cref="ParseChartsMiniResponse"/>'s underlying schema except the trailing column is
	/// implied volatility (percentage units) instead of VWAP. Rows that fail OHLC sanity are dropped silently.</summary>
	internal static IReadOnlyList<OptionMinuteBar> ParseOptionChartResponse(string json, long derivativeId)
	{
		List<OptionMinuteBar> bars;
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
				return Array.Empty<OptionMinuteBar>();

			var envelope = root[0];
			if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				return Array.Empty<OptionMinuteBar>();

			bars = new List<OptionMinuteBar>(data.GetArrayLength());
			foreach (var row in data.EnumerateArray())
			{
				if (row.ValueKind != JsonValueKind.String) continue;
				var s = row.GetString();
				if (string.IsNullOrEmpty(s)) continue;
				var parsed = ParseOptionBarRow(s);
				if (parsed != null) bars.Add(parsed);
			}
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"Webull option chart: failed to parse response for derivativeId {derivativeId}: {ex.Message}");
			return Array.Empty<OptionMinuteBar>();
		}

		bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return bars;
	}

	internal static OptionMinuteBar? ParseOptionBarRow(string row)
	{
		var parts = row.Split(',');
		if (parts.Length < 7) return null;

		if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSec)) return null;
		if (!TryParseDec(parts[1], out var open)) return null;
		if (!TryParseDec(parts[2], out var close)) return null;
		if (!TryParseDec(parts[3], out var high)) return null;
		if (!TryParseDec(parts[4], out var low)) return null;
		// parts[5] is prevClose — skipped.
		long volume = 0;
		if (!string.Equals(parts[6], "null", StringComparison.OrdinalIgnoreCase))
			long.TryParse(parts[6], NumberStyles.Integer | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out volume);

		decimal? iv = null;
		if (parts.Length >= 8 && !string.Equals(parts[7], "null", StringComparison.OrdinalIgnoreCase) && TryParseDec(parts[7], out var ivParsed))
			iv = ivParsed;

		if (high < Math.Max(open, close) || low > Math.Min(open, close)) return null;

		var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSec).Add(WebullBarShift);
		return new OptionMinuteBar(timestamp, open, high, low, close, Math.Max(0, volume), iv);
	}

	/// <summary>Backward-walking pagination over <see cref="FetchHistoricalMinuteBarsAsync"/>. Single-shot
	/// historical fetches anchored at deep past dates (>6 months back) silently truncate to a single
	/// bar in Webull's <c>query-mini</c> endpoint — empirically observed during the 2026-05-23 backfill,
	/// roughly 34% of days affected with no consistent pattern by date. The web app sidesteps this by
	/// paginating: starts anchored at "now", reads 800 bars, then re-anchors at the oldest bar's
	/// timestamp and reads the next 800. We do the same here. Each page round-trips ~1 sec at default
	/// pacing, and 800 SPX RTH minutes = ~2 trading days, so a 2-year SPX pull is ~250 pages.
	///
	/// Stops when (a) the oldest bar of a page falls at or below <paramref name="startUnixSec"/>,
	/// (b) the page returns no bars, or (c) the page doesn't advance (oldest_sec stays at or above the
	/// current anchor — defends against an endpoint loop). Returns bars sorted oldest-first, de-duplicated
	/// by timestamp.</summary>
	public static async Task<IReadOnlyList<MinuteBar>> FetchPaginatedHistoricalMinuteBarsAsync(
		ApiConfig config,
		long tickerId,
		long startUnixSec,
		long endUnixSec,
		bool includeExtended,
		int countPerPage,
		TimeSpan delayBetweenPages,
		Action<int, long, int>? onPageProgress,
		CancellationToken cancellation)
	{
		var allBars = new Dictionary<long, MinuteBar>();
		var anchor = endUnixSec;
		var pageCount = 0;
		while (anchor > startUnixSec)
		{
			cancellation.ThrowIfCancellationRequested();
			if (pageCount > 0) await Task.Delay(delayBetweenPages, cancellation);

			var bars = await FetchHistoricalMinuteBarsAsync(config, tickerId, anchor, countPerPage, includeExtended, cancellation);
			pageCount++;

			if (bars.Count == 0) break;

			var oldestSec = long.MaxValue;
			var newThisPage = 0;
			foreach (var b in bars)
			{
				var sec = b.Timestamp.ToUnixTimeSeconds();
				if (allBars.TryAdd(sec, b)) newThisPage++;
				if (sec < oldestSec) oldestSec = sec;
			}

			onPageProgress?.Invoke(pageCount, oldestSec, allBars.Count);

			if (oldestSec >= anchor) break;          // didn't advance — bail to avoid loop
			if (newThisPage == 0) break;             // all duplicates — endpoint exhausted at this anchor
			anchor = oldestSec;
		}

		var sorted = new List<MinuteBar>(allBars.Count);
		foreach (var b in allBars.Values) sorted.Add(b);
		sorted.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return sorted;
	}

	/// <summary>Parses <c>query-mini</c>'s 8-column row schema: <c>ts,open,close,high,low,prevClose,volume,vwap</c>.
	/// Distinct from <see cref="ParseChartsResponse"/> which handles the live <c>query</c> endpoint's
	/// 6/7-column schema without the <c>prevClose</c> field. Rows that fail OHLC sanity (high &lt;
	/// max(open,close) or low &gt; min(open,close)) are dropped silently rather than mis-parsed.</summary>
	internal static IReadOnlyList<MinuteBar> ParseChartsMiniResponse(string json, long tickerId)
	{
		List<MinuteBar> bars;
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
				return Array.Empty<MinuteBar>();

			var envelope = root[0];
			if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				return Array.Empty<MinuteBar>();

			bars = new List<MinuteBar>(data.GetArrayLength());
			foreach (var row in data.EnumerateArray())
			{
				if (row.ValueKind != JsonValueKind.String) continue;
				var s = row.GetString();
				if (string.IsNullOrEmpty(s)) continue;
				var parsed = ParseMiniBarRow(s);
				if (parsed != null) bars.Add(parsed);
			}
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"Webull charts (mini): failed to parse response for tickerId {tickerId}: {ex.Message}");
			return Array.Empty<MinuteBar>();
		}

		bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return bars;
	}

	internal static MinuteBar? ParseMiniBarRow(string row)
	{
		var parts = row.Split(',');
		if (parts.Length < 7) return null;

		if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSec)) return null;
		if (!TryParseDec(parts[1], out var open)) return null;
		if (!TryParseDec(parts[2], out var close)) return null;
		if (!TryParseDec(parts[3], out var high)) return null;
		if (!TryParseDec(parts[4], out var low)) return null;
		// parts[5] is prevClose — skipped.
		// parts[6] is volume; SPX index always returns the literal "null" string here.
		long volume = 0;
		if (!string.Equals(parts[6], "null", StringComparison.OrdinalIgnoreCase))
			long.TryParse(parts[6], NumberStyles.Integer | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out volume);

		if (high < Math.Max(open, close) || low > Math.Min(open, close)) return null;

		var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSec).Add(WebullBarShift);
		return new MinuteBar(timestamp, open, high, low, close, Math.Max(0, volume));
	}

	internal static IReadOnlyList<MinuteBar> ParseChartsResponse(string json, long tickerId)
	{
		List<MinuteBar> bars;
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
				return Array.Empty<MinuteBar>();

			// Endpoint can be batched (tickerIds=a,b,c) so the response is an array of per-ticker
			// envelopes. We pass a single id so taking the first entry that matches (or just the
			// first envelope) is sufficient.
			JsonElement envelope = default;
			var found = false;
			foreach (var entry in root.EnumerateArray())
			{
				if (entry.TryGetProperty("tickerId", out var tid) && tid.TryGetInt64(out var id) && id == tickerId)
				{
					envelope = entry;
					found = true;
					break;
				}
			}
			if (!found) envelope = root[0];

			if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				return Array.Empty<MinuteBar>();

			bars = new List<MinuteBar>(data.GetArrayLength());
			foreach (var row in data.EnumerateArray())
			{
				if (row.ValueKind != JsonValueKind.String) continue;
				var s = row.GetString();
				if (string.IsNullOrEmpty(s)) continue;
				var parsed = ParseBarRow(s);
				if (parsed != null) bars.Add(parsed);
			}
		}
		catch (JsonException ex)
		{
			Console.WriteLine($"Webull charts: failed to parse response for tickerId {tickerId}: {ex.Message}");
			return Array.Empty<MinuteBar>();
		}

		bars.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return bars;
	}

	/// <summary>Parses one row of Webull's chart data array. Rows are comma-separated with the
	/// observed-in-the-wild ordering <c>time,open,close,high,low,volume[,vwap]</c>. We sanity-check the
	/// ordering by requiring <c>high ≥ max(open, close)</c> and <c>low ≤ min(open, close)</c>; if that
	/// fails we try the alternative <c>time,open,high,low,close,volume</c> before giving up on the row.
	/// Returns null on any unparseable field — caller can drop the row silently.</summary>
	internal static MinuteBar? ParseBarRow(string row)
	{
		var parts = row.Split(',');
		if (parts.Length < 6) return null;

		if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSec)) return null;
		if (!TryParseDec(parts[1], out var open)) return null;
		if (!TryParseDec(parts[2], out var second)) return null;
		if (!TryParseDec(parts[3], out var third)) return null;
		if (!TryParseDec(parts[4], out var fourth)) return null;
		long.TryParse(parts[5], NumberStyles.Integer | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var volume);

		decimal close, high, low;
		// Primary ordering: t,o,c,h,l,v
		var primaryClose = second; var primaryHigh = third; var primaryLow = fourth;
		var primaryOk = primaryHigh >= Math.Max(open, primaryClose) && primaryLow <= Math.Min(open, primaryClose);
		if (primaryOk)
		{
			close = primaryClose; high = primaryHigh; low = primaryLow;
		}
		else
		{
			// Fallback ordering: t,o,h,l,c,v
			var altHigh = second; var altLow = third; var altClose = fourth;
			var altOk = altHigh >= Math.Max(open, altClose) && altLow <= Math.Min(open, altClose);
			if (!altOk) return null;
			close = altClose; high = altHigh; low = altLow;
		}

		var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSec).Add(WebullBarShift);
		return new MinuteBar(timestamp, open, high, low, close, Math.Max(0, volume));
	}

	private static bool TryParseDec(string s, out decimal value) =>
		decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

	private static string BarIntervalToCode(BarInterval interval) => interval switch
	{
		BarInterval.M1 => "m1",
		BarInterval.M5 => "m5",
		BarInterval.M15 => "m15",
		BarInterval.M30 => "m30",
		BarInterval.H1 => "h1",
		BarInterval.D1 => "d1",
		_ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unsupported bar interval")
	};
}

internal enum BarInterval { M1, M5, M15, M30, H1, D1 }
