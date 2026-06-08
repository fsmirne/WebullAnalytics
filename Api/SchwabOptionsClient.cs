using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WebullAnalytics.Api;

/// <summary>Schwab Trader market-data option-chain client. One GET to <c>/marketdata/v1/chains</c> returns the
/// whole chain for an underlying with real NBBO (bid/ask), open interest, volume, IV, and greeks — the data
/// Webull's snapshot path leaves null for non-front expiries. Maps Schwab's nested callExpDateMap/putExpDateMap
/// into the internal <see cref="OptionContractQuote"/> model with the same compact OCC symbol the rest of the
/// pipeline uses.</summary>
internal static class SchwabOptionsClient
{
	private const string ChainsUrl = "https://api.schwabapi.com/marketdata/v1/chains";

	/// <summary>Internal ticker root → Schwab underlying symbol. Index options use a '$' prefix; requesting
	/// <c>$SPX</c> returns BOTH the SPX (AM monthly) and SPXW (PM weekly) roots, so a single call covers the
	/// SPXW research vehicle. Equities/ETFs (SPY) pass through unchanged.</summary>
	private static readonly Dictionary<string, string> UnderlyingMap = new(StringComparer.OrdinalIgnoreCase)
	{
		["SPX"] = "$SPX",
		["SPXW"] = "$SPX",
		["XSP"] = "$XSP",
		["VIX"] = "$VIX",
		["NDX"] = "$NDX",
		["RUT"] = "$RUT",
		["DJX"] = "$DJX",
	};

	public static string ToSchwabUnderlying(string root) =>
		UnderlyingMap.TryGetValue(root, out var s) ? s : root.ToUpperInvariant();

	/// <summary>Fetches the full chain for <paramref name="root"/> between the given expiry dates (inclusive).
	/// Returns the underlying spot and every quoted contract. An empty result (e.g. an unsupported index symbol)
	/// comes back as <c>(null, empty)</c> rather than throwing, so the capture loop treats it as a soft miss.</summary>
	public static async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainAsync(
		string accessToken, string root, DateOnly fromExpiry, DateOnly toExpiry, CancellationToken ct)
	{
		var symbol = ToSchwabUnderlying(root);
		var url = $"{ChainsUrl}?symbol={Uri.EscapeDataString(symbol)}&contractType=ALL&strategy=SINGLE&range=ALL"
			+ $"&includeUnderlyingQuote=true&fromDate={fromExpiry:yyyy-MM-dd}&toDate={toExpiry:yyyy-MM-dd}";

		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

		using var response = await client.SendAsync(request, ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			throw new SchwabAuthException($"chains request unauthorized (HTTP {(int)response.StatusCode}) — token rejected.");
		if (!response.IsSuccessStatusCode)
		{
			Console.WriteLine($"Schwab: chains returned {(int)response.StatusCode} for {symbol}: {Truncate(body, 200)}");
			return (null, Array.Empty<OptionContractQuote>());
		}

		return ParseChain(body, symbol);
	}

	private static (decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes) ParseChain(string json, string symbol)
	{
		var quotes = new List<OptionContractQuote>();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Schwab signals an empty/failed lookup with status SUCCESS but null maps, or status FAILED.
		var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
		if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"Schwab: chains status FAILED for {symbol} (symbol may be unsupported).");
			return (null, quotes);
		}

		decimal? spot = GetDecimal(root, "underlyingPrice");

		foreach (var mapName in new[] { "callExpDateMap", "putExpDateMap" })
		{
			if (!root.TryGetProperty(mapName, out var expMap) || expMap.ValueKind != JsonValueKind.Object) continue;
			foreach (var expEntry in expMap.EnumerateObject())          // "YYYY-MM-DD:DTE"
			foreach (var strikeEntry in expEntry.Value.EnumerateObject()) // "500.0"
			foreach (var contract in strikeEntry.Value.EnumerateArray())
			{
				var rawSymbol = contract.TryGetProperty("symbol", out var s) ? s.GetString() : null;
				if (string.IsNullOrWhiteSpace(rawSymbol)) continue;

				quotes.Add(new OptionContractQuote(
					ContractSymbol: NormalizeSymbol(rawSymbol),
					LastPrice: PositiveOrNull(GetDecimal(contract, "last")),
					Bid: PositiveOrNull(GetDecimal(contract, "bid")),
					Ask: PositiveOrNull(GetDecimal(contract, "ask")),
					Change: GetDecimal(contract, "netChange"),
					PercentChange: GetDecimal(contract, "percentChange"),
					Volume: GetLong(contract, "totalVolume"),
					OpenInterest: GetLong(contract, "openInterest"),
					// Schwab quotes volatility as an annualized percent (e.g. 23.45); the model stores a fraction.
					// -999.0 is Schwab's "no value" sentinel.
					ImpliedVolatility: ToFraction(GetDecimal(contract, "volatility")),
					BidSize: GetLong(contract, "bidSize"),
					AskSize: GetLong(contract, "askSize")
				));
			}
		}

		return (spot, quotes);
	}

	/// <summary>Schwab pads the OCC root to 6 chars (e.g. <c>"SPY   260620C00500000"</c>); strip the spaces to
	/// get the compact OCC symbol (<c>"SPY260620C00500000"</c>) the rest of the pipeline parses.</summary>
	internal static string NormalizeSymbol(string schwabSymbol) => schwabSymbol.Replace(" ", "");

	private static decimal? ToFraction(decimal? pct) => pct is { } v && v >= 0m ? v / 100m : null;

	private static decimal? PositiveOrNull(decimal? v) => v is { } d && d > 0m ? d : null;

	private static decimal? GetDecimal(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetDecimal(out var d) && d > -900m ? d : null,
			JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
			_ => null,
		};
	}

	private static long? GetLong(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetInt64(out var l) ? l : null,
			JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : null,
			_ => null,
		};
	}

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
