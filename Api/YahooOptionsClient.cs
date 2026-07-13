using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WebullAnalytics.Api;

internal static class YahooOptionsClient
{
	private const string CrumbUrl = "https://query1.finance.yahoo.com/v1/test/getcrumb";
	private const string CookieBootstrapUrl = "https://fc.yahoo.com";

	// Webull root symbols that differ from Yahoo Finance ticker symbols. Used by the historical
	// daily-close fetch; Yahoo is no longer wired up as an option-quote source (it lacks Greeks,
	// HV, and iv5 — all of which Webull provides — and is on a 15-minute delay).
	private static readonly Dictionary<string, string> YahooTickerMap = new(StringComparer.OrdinalIgnoreCase) { { "SPXW", "^SPX" }, { "SPX", "^SPX" }, { "XSP", "^XSP" }, { "NDX", "^NDX" }, { "RUT", "^RUT" }, { "DJX", "^DJI" }, { "VIX", "^VIX" }, { "VIX1D", "^VIX1D" }, { "VIX9D", "^VIX9D" }, { "VIX3M", "^VIX3M" }, { "VXN", "^VXN" } };

	/// <summary>Translates a Webull root symbol to its Yahoo Finance equivalent (e.g. XSP → ^XSP,
	/// SPX/SPXW → ^SPX). Returns the input unchanged when no mapping exists. Shared by every Yahoo
	/// chart fetch so index roots resolve to the real index rather than an unrelated OTC quote.</summary>
	internal static string ToYahooTicker(string root) => YahooTickerMap.TryGetValue(root, out var mapped) ? mapped : root;

	/// <summary>Applies a fetched risk-free rate to <see cref="Pricing.OptionMath.RiskFreeRate"/> and logs it.
	/// No-op when <paramref name="rate"/> is null (fetch failed gracefully).</summary>
	internal static void ApplyToOptionMath(double? rate)
	{
		if (!rate.HasValue) return;
		Pricing.OptionMath.RiskFreeRate = rate.Value;
		Utils.Log.Debug($"Risk-free rate (13-week T-bill): {rate.Value:P2}");
	}

	/// <summary>
	/// Fetches the 13-week T-bill yield (^IRX) from Yahoo Finance as a risk-free rate.
	/// Returns the rate as a decimal (e.g., 0.043 for 4.3%), or null on failure.
	/// </summary>
	public static async Task<double?> FetchRiskFreeRateAsync(CancellationToken cancellationToken)
	{
		var handler = new HttpClientHandler
		{
			CookieContainer = new CookieContainer(),
			AutomaticDecompression = DecompressionMethods.All,
		};
		using var client = new HttpClient(handler);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
		string? crumb = null;
		var result = await FetchRiskFreeRateAsync(client, crumb, cancellationToken);
		if (result == null)
		{
			crumb = await TryGetCrumbAsync(client, cancellationToken);
			if (!string.IsNullOrWhiteSpace(crumb))
				result = await FetchRiskFreeRateAsync(client, crumb, cancellationToken);
		}
		return result;
	}

	private static async Task<double?> FetchRiskFreeRateAsync(HttpClient client, string? crumb, CancellationToken cancellationToken)
	{
		try
		{
			var url = "https://query2.finance.yahoo.com/v8/finance/chart/%5EIRX?range=1d&interval=1d";
			if (!string.IsNullOrWhiteSpace(crumb))
				url += $"&crumb={Uri.EscapeDataString(crumb)}";
			var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.Referrer = new Uri("https://finance.yahoo.com/quote/%5EIRX/");
			using var response = await client.SendAsync(request, cancellationToken);
			if (!response.IsSuccessStatusCode) return null;
			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("chart", out var chart)
				&& chart.TryGetProperty("result", out var chartResult)
				&& chartResult.ValueKind == JsonValueKind.Array && chartResult.GetArrayLength() > 0)
			{
				var meta = chartResult[0].GetProperty("meta");
				var price = GetDecimal(meta, "regularMarketPrice");
				if (price.HasValue && price.Value > 0)
					return (double)(price.Value / 100m); // ^IRX quotes in percentage points
			}
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
		}
		return null;
	}

	private static async Task<string?> TryGetCrumbAsync(HttpClient client, CancellationToken cancellationToken)
	{
		try
		{
			_ = await client.GetAsync(CookieBootstrapUrl, cancellationToken);
			var crumb = await client.GetStringAsync(CrumbUrl, cancellationToken);
			crumb = crumb?.Trim();
			return string.IsNullOrWhiteSpace(crumb) ? null : crumb;
		}
		catch
		{
			return null;
		}
	}

	private static decimal? GetDecimal(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : (decimal?)null,
			JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null,
			_ => null,
		};
	}

	/// <summary>One daily OHLCV bar plus split/dividend-adjusted close. AdjClose is what `BacktestIVProvider`
	/// uses for realized-vol calc to avoid dividend-date and split-date jumps polluting HV.</summary>
	public sealed record HistoricalBar(DateTime Date, decimal Open, decimal High, decimal Low, decimal Close, decimal AdjClose, long? Volume);

	/// <summary>Trailing-week 1-minute bars from Yahoo's chart endpoint (their 1m interval serves ~7-8
	/// calendar days, no deeper — this exists for FORWARD CAPTURE, not backfill: sources with real 1m
	/// history don't carry some CBOE indices at all, e.g. VXN, whose Webull feed died 2024-04 and whose
	/// massive index namespace is unentitled). Timestamps are bar-START epochs (Yahoo's convention —
	/// already matches the codebase's start-of-bar normalization; no −60s shift needed). Null-valued
	/// placeholder minutes are skipped. Volume is 0 for indices. Empty list on failure.</summary>
	public static async Task<IReadOnlyList<MinuteBar>> FetchIntradayMinuteBarsAsync(string ticker, CancellationToken cancellation)
	{
		var yahoTicker = ToYahooTicker(ticker);
		var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AutomaticDecompression = System.Net.DecompressionMethods.All };
		using var client = new HttpClient(handler);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");

		try
		{
			var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(yahoTicker)}?interval=1m&range=7d";
			using var response = await client.GetAsync(url, cancellation);
			if (!response.IsSuccessStatusCode)
			{
				Console.Error.WriteLine($"Warning: Yahoo intraday fetch for {ticker} returned {(int)response.StatusCode}.");
				return Array.Empty<MinuteBar>();
			}
			var json = await response.Content.ReadAsStringAsync(cancellation);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("chart", out var chart)
				|| !chart.TryGetProperty("result", out var resultArr) || resultArr.ValueKind != JsonValueKind.Array || resultArr.GetArrayLength() == 0)
				return Array.Empty<MinuteBar>();
			var series = resultArr[0];
			if (!series.TryGetProperty("timestamp", out var tsArr)
				|| !series.TryGetProperty("indicators", out var indicators)
				|| !indicators.TryGetProperty("quote", out var quoteArr) || quoteArr.GetArrayLength() == 0)
				return Array.Empty<MinuteBar>();
			var q = quoteArr[0];
			if (!q.TryGetProperty("open", out var openArr) || !q.TryGetProperty("high", out var highArr) || !q.TryGetProperty("low", out var lowArr) || !q.TryGetProperty("close", out var closeArr))
				return Array.Empty<MinuteBar>();

			var timestamps = tsArr.EnumerateArray().ToArray();
			var opens = openArr.EnumerateArray().ToArray();
			var highs = highArr.EnumerateArray().ToArray();
			var lows = lowArr.EnumerateArray().ToArray();
			var closes = closeArr.EnumerateArray().ToArray();
			var bars = new List<MinuteBar>(timestamps.Length);
			for (var i = 0; i < timestamps.Length; i++)
			{
				if (i >= opens.Length || opens[i].ValueKind != JsonValueKind.Number
					|| highs[i].ValueKind != JsonValueKind.Number || lows[i].ValueKind != JsonValueKind.Number
					|| closes[i].ValueKind != JsonValueKind.Number)
					continue;   // Yahoo pads untraded minutes with nulls
				// Yahoo serialises doubles (27.139999389648438 for a 27.14 print) — round to 4dp, comfortably
				// past any index's published precision, so the CSVs carry the actual values.
				bars.Add(new MinuteBar(
					Timestamp: DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()),
					Open: Math.Round(opens[i].GetDecimal(), 4),
					High: Math.Round(highs[i].GetDecimal(), 4),
					Low: Math.Round(lows[i].GetDecimal(), 4),
					Close: Math.Round(closes[i].GetDecimal(), 4),
					Volume: 0));
			}
			return bars;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Console.Error.WriteLine($"Warning: Yahoo intraday fetch for {ticker} failed: {ex.Message}");
			return Array.Empty<MinuteBar>();
		}
	}

	public static async Task<Dictionary<DateTime, HistoricalBar>> FetchHistoricalBarsAsync(string ticker, DateTime from, DateTime to, CancellationToken cancellation)
	{
		var yahoTicker = ToYahooTicker(ticker);
		var period1 = ToUnixTimeSecondsUtc(from);
		var period2 = ToUnixTimeSecondsUtc(to);

		var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AutomaticDecompression = System.Net.DecompressionMethods.All };
		using var client = new HttpClient(handler);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

		string? crumb = null;
		var result = await FetchHistoricalBarsAsync(client, yahoTicker, period1, period2, crumb, cancellation);
		if (result == null)
		{
			crumb = await TryGetCrumbAsync(client, cancellation);
			result = await FetchHistoricalBarsAsync(client, yahoTicker, period1, period2, crumb, cancellation);
		}
		if (result == null)
		{
			Console.Error.WriteLine($"Warning: Yahoo historical-bars fetch for {ticker} failed. Cache will be empty.");
			return new Dictionary<DateTime, HistoricalBar>();
		}
		return result;
	}

	private static async Task<Dictionary<DateTime, HistoricalBar>?> FetchHistoricalBarsAsync(HttpClient client, string ticker, long period1, long period2, string? crumb, CancellationToken cancellation)
	{
		try
		{
			var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}?period1={period1}&period2={period2}&interval=1d&events=history";
			if (!string.IsNullOrWhiteSpace(crumb)) url += $"&crumb={Uri.EscapeDataString(crumb)}";
			var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.Referrer = new Uri($"https://finance.yahoo.com/quote/{Uri.EscapeDataString(ticker)}/history/");
			using var response = await client.SendAsync(request, cancellation);
			if (!response.IsSuccessStatusCode) return null;

			var json = await response.Content.ReadAsStringAsync(cancellation);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("chart", out var chart)) return null;
			if (!chart.TryGetProperty("result", out var resultArr) || resultArr.ValueKind != JsonValueKind.Array || resultArr.GetArrayLength() == 0) return null;
			var series = resultArr[0];
			// A valid result with no timestamp/quote arrays means the requested window simply contains no published
			// daily bars (Yahoo returns a meta-only chart for very short ranges) — that is "no new data", not a fetch
			// failure. Return an empty map so the caller keeps any cached bars and does not emit a failure warning.
			if (!series.TryGetProperty("timestamp", out var tsArr)
				|| !series.TryGetProperty("indicators", out var indicators)
				|| !indicators.TryGetProperty("quote", out var quoteArr) || quoteArr.GetArrayLength() == 0)
				return new Dictionary<DateTime, HistoricalBar>();
			var q = quoteArr[0];
			if (!q.TryGetProperty("open", out var openArr) || !q.TryGetProperty("high", out var highArr) || !q.TryGetProperty("low", out var lowArr) || !q.TryGetProperty("close", out var closeArr))
				return new Dictionary<DateTime, HistoricalBar>();

			JsonElement? adjArr = null;
			if (indicators.TryGetProperty("adjclose", out var adjArrOuter) && adjArrOuter.GetArrayLength() > 0 && adjArrOuter[0].TryGetProperty("adjclose", out var adjInner))
				adjArr = adjInner;

			var volArr = q.TryGetProperty("volume", out var volEl) ? (JsonElement?)volEl : null;

			var map = new Dictionary<DateTime, HistoricalBar>();
			var timestamps = tsArr.EnumerateArray().ToArray();
			var opens = openArr.EnumerateArray().ToArray();
			var highs = highArr.EnumerateArray().ToArray();
			var lows = lowArr.EnumerateArray().ToArray();
			var closes = closeArr.EnumerateArray().ToArray();
			var adjs = adjArr?.EnumerateArray().ToArray();
			var vols = volArr?.EnumerateArray().ToArray();
			var n = new[] { timestamps.Length, opens.Length, highs.Length, lows.Length, closes.Length }.Min();
			for (int i = 0; i < n; i++)
			{
				if (closes[i].ValueKind == JsonValueKind.Null || opens[i].ValueKind == JsonValueKind.Null || highs[i].ValueKind == JsonValueKind.Null || lows[i].ValueKind == JsonValueKind.Null) continue;
				if (!closes[i].TryGetDecimal(out var close)) continue;
				if (!opens[i].TryGetDecimal(out var open)) continue;
				if (!highs[i].TryGetDecimal(out var high)) continue;
				if (!lows[i].TryGetDecimal(out var low)) continue;
				var adj = close;
				if (adjs != null && i < adjs.Length && adjs[i].ValueKind != JsonValueKind.Null && adjs[i].TryGetDecimal(out var a)) adj = a;
				long? vol = null;
				if (vols != null && i < vols.Length && vols[i].ValueKind == JsonValueKind.Number && vols[i].TryGetInt64(out var v)) vol = v;
				var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime.Date;
				map[date] = new HistoricalBar(date, open, high, low, close, adj, vol);
			}
			return map;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}

	internal static long ToUnixTimeSecondsUtc(DateTime value)
	{
		if (value.Kind == DateTimeKind.Utc)
			return new DateTimeOffset(value).ToUnixTimeSeconds();

		return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
	}

}
