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
	/// <param name="strikeCount">When > 0, asks Schwab to return only this many strikes around the
	/// at-the-money price (per expiration) instead of the full <c>range=ALL</c> ladder. A caller that only
	/// needs near-money strikes (e.g. <c>analyze gex</c>) sets this so the common case is a single small
	/// request; 0 keeps the full ladder (what the scraper's backtest snapshot wants). Either way the body
	/// cap is enforced by the adaptive date-range split below, so no caller can 502.</param>
	public static async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainAsync(
		string accessToken, string root, DateOnly fromExpiry, DateOnly toExpiry, CancellationToken ct, int strikeCount = 0)
	{
		var symbol = ToSchwabUnderlying(root);
		return await FetchChainRangeAsync(accessToken, symbol, fromExpiry, toExpiry, strikeCount, ct);
	}

	/// <summary>Fetches [<paramref name="from"/>, <paramref name="to"/>] in one request and, when Schwab
	/// rejects the response body as too large (HTTP 502 <c>protocol.http.TooBigBody</c> — dense index roots
	/// like $SPX over a multi-day window with <c>range=ALL</c>), recursively halves the expiry window and
	/// merges the halves. Self-tunes to chain density: sparse equities resolve in one call, a 45-DTE $SPX
	/// request splits until each chunk fits — full strike coverage preserved, no per-ticker tuning, and no
	/// caller (analyze gex or the scraper, at any <c>--max-dte</c>) can overflow.</summary>
	private static async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainRangeAsync(
		string accessToken, string symbol, DateOnly from, DateOnly to, int strikeCount, CancellationToken ct)
	{
		var (spot, quotes, tooBig) = await FetchChainOnceAsync(accessToken, symbol, from, to, strikeCount, ct);
		if (!tooBig) return (spot, quotes);

		if (from >= to)
		{
			// A single expiry day still overflows at range=ALL — last resort: bound strikes around ATM so we
			// return the near-money chain rather than nothing. (Not expected in practice: 0DTE range=ALL fits.)
			if (strikeCount is 0 or > 250)
			{
				var (s, q, stillTooBig) = await FetchChainOnceAsync(accessToken, symbol, from, to, 250, ct);
				if (!stillTooBig) return (s, q);
			}
			Console.WriteLine($"Schwab: chain for {symbol} on {from:yyyy-MM-dd} exceeds the body cap even bounded; skipping that day.");
			return (null, Array.Empty<OptionContractQuote>());
		}

		var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
		var lower = await FetchChainRangeAsync(accessToken, symbol, from, mid, strikeCount, ct);
		var upper = await FetchChainRangeAsync(accessToken, symbol, mid.AddDays(1), to, strikeCount, ct);
		var merged = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in lower.Quotes) merged[q.ContractSymbol] = q;
		foreach (var q in upper.Quotes) merged[q.ContractSymbol] = q;
		return (lower.Spot ?? upper.Spot, merged.Values.ToList());
	}

	/// <summary>One chains GET. Returns <c>TooBig=true</c> (with empty quotes) on the 502 body-overflow so the
	/// caller can split the window; other non-success statuses are logged and returned as a soft miss.</summary>
	private static async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes, bool TooBig)> FetchChainOnceAsync(
		string accessToken, string symbol, DateOnly from, DateOnly to, int strikeCount, CancellationToken ct)
	{
		var url = $"{ChainsUrl}?symbol={Uri.EscapeDataString(symbol)}&contractType=ALL&strategy=SINGLE&range=ALL"
			+ $"&includeUnderlyingQuote=true&fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}"
			+ (strikeCount > 0 ? $"&strikeCount={strikeCount}" : "");

		var (statusCode, body) = await SendWithTransientRetryAsync(url, accessToken, ct);
		if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			throw new SchwabAuthException($"chains request unauthorized (HTTP {(int)statusCode}) — token rejected.");
		if (!IsSuccess(statusCode))
		{
			var tooBig = (int)statusCode == 502 && body.Contains("TooBigBody", StringComparison.OrdinalIgnoreCase);
			if (!tooBig)
				Console.WriteLine($"Schwab: chains returned {(int)statusCode} for {symbol}: {Truncate(body, 200)}");
			return (null, Array.Empty<OptionContractQuote>(), tooBig);
		}

		var (spot, quotes) = ParseChain(body, symbol);
		return (spot, quotes, false);
	}

	private static bool IsSuccess(HttpStatusCode code) => (int)code is >= 200 and <= 299;

	/// <summary>Issues the chains GET with a bounded retry on transport-level transient failures — SSL handshake
	/// aborts and mid-request connection resets (<c>HttpRequestException</c>/<c>IOException</c>, typically an inner
	/// <c>SocketException</c> "forcibly closed by the remote host") that show up as intermittent single-tick failures
	/// in the live watch loop. A fresh client+request is used per attempt; non-transport failures (auth, 502 TooBig,
	/// timeouts) propagate to the caller unchanged.</summary>
	private static async Task<(HttpStatusCode Status, string Body)> SendWithTransientRetryAsync(string url, string accessToken, CancellationToken ct)
	{
		const int maxAttempts = 3;
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
				using var request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

				using var response = await client.SendAsync(request, ct);
				var body = await response.Content.ReadAsStringAsync(ct);
				return (response.StatusCode, body);
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < maxAttempts && !ct.IsCancellationRequested)
			{
				Console.WriteLine($"Schwab: chains transport error (attempt {attempt}/{maxAttempts}), retrying: {ex.Message}");
				await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
			}
		}
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
					AskSize: GetLong(contract, "askSize"),
					// Schwab stamps each contract with quoteTimeInLong (epoch ms); feeds the live staleness guard.
					// Fall back to tradeTimeInLong when the quote time is absent.
					QuoteTime: EpochMsToOffset(GetLong(contract, "quoteTimeInLong") ?? GetLong(contract, "tradeTimeInLong"))
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

	// Epoch milliseconds -> DateTimeOffset (UTC). 0/negative (Schwab's "no value") -> null.
	private static DateTimeOffset? EpochMsToOffset(long? epochMs) => epochMs is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

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
