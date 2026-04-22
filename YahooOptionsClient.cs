using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WebullAnalytics;

public static class YahooOptionsClient
{
	private static readonly string[] OptionsUrls =
	[
		"https://query2.finance.yahoo.com/v7/finance/options",
		"https://query1.finance.yahoo.com/v7/finance/options",
	];

	private const string CrumbUrl = "https://query1.finance.yahoo.com/v1/test/getcrumb";
	private const string CookieBootstrapUrl = "https://fc.yahoo.com";

	// Webull root symbols that differ from Yahoo Finance ticker symbols.
	private static readonly Dictionary<string, string> YahooTickerMap = new(StringComparer.OrdinalIgnoreCase) { { "SPXW", "^SPX" }, { "SPX", "^SPX" }, { "NDX", "^NDX" }, { "RUT", "^RUT" }, { "DJX", "^DJI" }, { "VIX", "^VIX" } };

	private static string ToYahooTicker(string root) => YahooTickerMap.TryGetValue(root, out var mapped) ? mapped : root;

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

	public static async Task<(IReadOnlyDictionary<string, OptionContractQuote> OptionQuotes, IReadOnlyDictionary<string, decimal> UnderlyingPrices)> FetchOptionQuotesAsync(IEnumerable<PositionRow> positionRows, CancellationToken cancellationToken)
	{
		var wantedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var row in positionRows)
		{
			if (row.MatchKey == null) continue;
			if (!MatchKeys.TryGetOptionSymbol(row.MatchKey, out var symbol)) continue;
			wantedSymbols.Add(symbol);
		}

		if (wantedSymbols.Count == 0)
			return (new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

		var groups = wantedSymbols
			.Select(s => (symbol: s, parsed: ParsingHelpers.ParseOptionSymbol(s)))
			.Where(x => x.parsed != null)
			.Select(x => (x.symbol, parsed: x.parsed!))
			.GroupBy(x => (x.parsed.Root, Expiry: x.parsed.ExpiryDate.Date))
			.ToList();

		var handler = new HttpClientHandler
		{
			CookieContainer = new CookieContainer(),
			AutomaticDecompression = DecompressionMethods.All,
		};
		using var client = new HttpClient(handler);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

		var result = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var underlyingPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		string? crumb = null;

		foreach (var group in groups)
		{
			var root = group.Key.Root;
			var ticker = ToYahooTicker(root);
			var expiry = group.Key.Expiry;
			var unixDate = new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)).ToUnixTimeSeconds();
			Console.WriteLine($"Yahoo Finance: requesting {ticker} options for {expiry:yyyy-MM-dd}...");
			HttpResponseMessage response;
			try
			{
				response = await SendOptionsRequestAsync(client, ticker, unixDate, crumb, cancellationToken);
				if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
				{
					crumb ??= await TryGetCrumbAsync(client, cancellationToken);
					if (!string.IsNullOrWhiteSpace(crumb))
					{
						response.Dispose();
						response = await SendOptionsRequestAsync(client, ticker, unixDate, crumb, cancellationToken);
					}
				}
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException) throw;
				Console.WriteLine($"Warning: Yahoo Finance request failed for {ticker} {expiry:yyyy-MM-dd}: {ex.Message}");
				continue;
			}

			using (response)
			{
				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"Warning: Yahoo Finance returned {(int)response.StatusCode} for {ticker} {expiry:yyyy-MM-dd}.");
					continue;
				}

				var json = await response.Content.ReadAsStringAsync(cancellationToken);
				var parsed = ParseOptionChain(json);
				if (parsed.UnderlyingPrice.HasValue)
					underlyingPrices[root] = parsed.UnderlyingPrice.Value;
				foreach (var quote in parsed.Quotes)
				{
					if (!wantedSymbols.Contains(quote.ContractSymbol)) continue;
					result[quote.ContractSymbol] = quote;
				}
			}
		}

		// Fetch underlying prices for any tickers not already captured from the options response.
		var allTickers = groups.Select(g => g.Key.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var missingTickers = allTickers.Where(t => !underlyingPrices.ContainsKey(t)).ToList();
		if (missingTickers.Count > 0)
		{
			var fetched = await FetchUnderlyingPricesAsync(client, missingTickers, crumb, cancellationToken);
			foreach (var (ticker2, price) in fetched)
				underlyingPrices[ticker2] = price;
		}

		return (result, underlyingPrices);
	}

	private static async Task<Dictionary<string, decimal>> FetchUnderlyingPricesAsync(HttpClient client, List<string> roots, string? crumb, CancellationToken cancellationToken)
	{
		var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			try
			{
				var yahooTicker = ToYahooTicker(root);
				var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(yahooTicker)}?range=1d&interval=1d";
				if (!string.IsNullOrWhiteSpace(crumb))
					url += $"&crumb={Uri.EscapeDataString(crumb)}";
				var request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Headers.Referrer = new Uri($"https://finance.yahoo.com/quote/{Uri.EscapeDataString(yahooTicker)}/");
				using var response = await client.SendAsync(request, cancellationToken);
				if (!response.IsSuccessStatusCode) continue;
				var json = await response.Content.ReadAsStringAsync(cancellationToken);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("chart", out var chart) && chart.TryGetProperty("result", out var chartResult) && chartResult.ValueKind == JsonValueKind.Array && chartResult.GetArrayLength() > 0)
				{
					var meta = chartResult[0].GetProperty("meta");
					var price = GetDecimal(meta, "regularMarketPrice");
					if (price.HasValue)
						prices[root] = price.Value;
				}
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException) throw;
			}
		}
		return prices;
	}

	private static async Task<HttpResponseMessage> SendOptionsRequestAsync(HttpClient client, string ticker, long unixDate, string? crumb, CancellationToken cancellationToken)
	{
		var baseUrl = OptionsUrls[0];
		var url = $"{baseUrl}/{Uri.EscapeDataString(ticker)}?date={unixDate}";
		if (!string.IsNullOrWhiteSpace(crumb))
			url += $"&crumb={Uri.EscapeDataString(crumb)}";

		var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Referrer = new Uri($"https://finance.yahoo.com/quote/{Uri.EscapeDataString(ticker)}/options/?date={unixDate}");

		var response = await client.SendAsync(request, cancellationToken);

		// If query2 is blocked, fall back to query1.
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			var fallbackUrl = $"{OptionsUrls[1]}/{Uri.EscapeDataString(ticker)}?date={unixDate}";
			if (!string.IsNullOrWhiteSpace(crumb))
				fallbackUrl += $"&crumb={Uri.EscapeDataString(crumb)}";
			var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, fallbackUrl);
			fallbackRequest.Headers.Referrer = request.Headers.Referrer;
			response.Dispose();
			response = await client.SendAsync(fallbackRequest, cancellationToken);
		}

		return response;
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

	private static (List<OptionContractQuote> Quotes, decimal? UnderlyingPrice) ParseOptionChain(string json)
	{
		var quotes = new List<OptionContractQuote>();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		if (!root.TryGetProperty("optionChain", out var optionChain))
			return (quotes, null);
		if (!optionChain.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
			return (quotes, null);

		var res0 = result[0];

		// The v7 options endpoint may include a quote object, or the underlying price at the top level.
		decimal? underlyingPrice = null;
		if (res0.TryGetProperty("quote", out var quote) && quote.ValueKind == JsonValueKind.Object)
			underlyingPrice = GetDecimal(quote, "regularMarketPrice");
		underlyingPrice ??= GetDecimal(res0, "underlyingPrice");

		if (!res0.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() == 0)
			return (quotes, underlyingPrice);

		var chain = options[0];

		if (chain.TryGetProperty("calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in calls.EnumerateArray())
			{
				var q = ParseContractQuote(item);
				if (q != null) quotes.Add(q);
			}
		}

		if (chain.TryGetProperty("puts", out var puts) && puts.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in puts.EnumerateArray())
			{
				var q = ParseContractQuote(item);
				if (q != null) quotes.Add(q);
			}
		}

		return (quotes, underlyingPrice);
	}

	private static OptionContractQuote? ParseContractQuote(JsonElement item)
	{
		var contractSymbol = item.TryGetProperty("contractSymbol", out var cs) ? cs.GetString() : null;
		if (string.IsNullOrWhiteSpace(contractSymbol)) return null;

		return new OptionContractQuote(
			ContractSymbol: contractSymbol,
			LastPrice: GetDecimal(item, "lastPrice"),
			Bid: GetDecimal(item, "bid"),
			Ask: GetDecimal(item, "ask"),
			Change: GetDecimal(item, "change"),
			PercentChange: GetDecimal(item, "percentChange"),
			Volume: GetLong(item, "volume"),
			OpenInterest: GetLong(item, "openInterest"),
			ImpliedVolatility: GetDecimal(item, "impliedVolatility")
		);
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

	private static long? GetLong(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (long?)null,
			JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : (long?)null,
			_ => null,
		};
	}

	public static async Task<Dictionary<DateTime, decimal>> FetchHistoricalClosesAsync(string ticker, DateTime from, DateTime to, CancellationToken cancellation)
	{
		var yahoTicker = ToYahooTicker(ticker);
		var period1 = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeSeconds();
		var period2 = new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeSeconds();

		var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AutomaticDecompression = System.Net.DecompressionMethods.All };
		using var client = new HttpClient(handler);
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebullAnalytics/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

		string? crumb = null;
		var result = await FetchHistoricalClosesAsync(client, yahoTicker, period1, period2, crumb, cancellation);
		if (result == null)
		{
			crumb = await TryGetCrumbAsync(client, cancellation);
			result = await FetchHistoricalClosesAsync(client, yahoTicker, period1, period2, crumb, cancellation);
		}

		if (result == null)
		{
			Console.Error.WriteLine($"Warning: Yahoo historical fetch for {ticker} failed. Cache will be empty.");
			return new Dictionary<DateTime, decimal>();
		}
		return result;
	}

	private static async Task<Dictionary<DateTime, decimal>?> FetchHistoricalClosesAsync(HttpClient client, string ticker, long period1, long period2, string? crumb, CancellationToken cancellation)
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
			if (!series.TryGetProperty("timestamp", out var tsArr)) return null;
			if (!series.TryGetProperty("indicators", out var indicators)) return null;
			if (!indicators.TryGetProperty("quote", out var quoteArr) || quoteArr.GetArrayLength() == 0) return null;
			if (!quoteArr[0].TryGetProperty("close", out var closeArr)) return null;

			var map = new Dictionary<DateTime, decimal>();
			var timestamps = tsArr.EnumerateArray().ToArray();
			var closes = closeArr.EnumerateArray().ToArray();
			for (int i = 0; i < Math.Min(timestamps.Length, closes.Length); i++)
			{
				if (closes[i].ValueKind == JsonValueKind.Null) continue;
				if (!closes[i].TryGetDecimal(out var close)) continue;
				var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime.Date;
				map[date] = close;
			}
			return map;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}
}
