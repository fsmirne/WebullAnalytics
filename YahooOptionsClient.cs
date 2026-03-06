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

	public static async Task<IReadOnlyDictionary<string, OptionContractQuote>> FetchOptionQuotesAsync(IEnumerable<PositionRow> positionRows, CancellationToken cancellationToken)
	{
		var wantedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var row in positionRows)
		{
			if (row.MatchKey == null) continue;
			if (!MatchKeys.TryGetOptionSymbol(row.MatchKey, out var symbol)) continue;
			wantedSymbols.Add(symbol);
		}

		if (wantedSymbols.Count == 0)
			return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

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
		string? crumb = null;

		foreach (var group in groups)
		{
			var ticker = group.Key.Root;
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
				foreach (var quote in ParseOptionQuotes(json))
				{
					if (!wantedSymbols.Contains(quote.ContractSymbol)) continue;
					result[quote.ContractSymbol] = quote;
				}
			}
		}

		return result;
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

	private static IEnumerable<OptionContractQuote> ParseOptionQuotes(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		if (!root.TryGetProperty("optionChain", out var optionChain))
			yield break;
		if (!optionChain.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
			yield break;

		var res0 = result[0];
		if (!res0.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() == 0)
			yield break;

		var chain = options[0];

		if (chain.TryGetProperty("calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in calls.EnumerateArray())
			{
				var q = ParseContractQuote(item);
				if (q != null) yield return q;
			}
		}

		if (chain.TryGetProperty("puts", out var puts) && puts.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in puts.EnumerateArray())
			{
				var q = ParseContractQuote(item);
				if (q != null) yield return q;
			}
		}
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
}
