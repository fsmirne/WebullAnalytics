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

		var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSec);
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
