using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics.Api;

internal static class WebullOptionsClient
{
	private const string TickerSearchUrl = "https://quotes-gw.webullfintech.com/api/search/pc/tickers";
	private const string StrategyListUrl = "https://quotes-gw.webullfintech.com/api/quote/option/strategy/list";
	private const string QueryBatchUrl = "https://quotes-gw.webullfintech.com/api/quote/option/quotes/queryBatch";

	// Index/derivative tickers that the search endpoint can't resolve.
	// Add entries here as needed — use 'sniff' or browser network tools to find the tickerId
	// from Webull's option chain requests for the index.
	//
	// Note: 913324359 was previously labeled "SPX" but is actually SPXC (SPX Technologies Inc, a
	// $200 industrial-products stock on NYSE) — Webull's symbol-search abbreviation collision.
	// The S&P 500 cash index lives at 913354362, which is the SPXW entry below. SPXW is the
	// canonical entry for both option-chain and chart usage on the index; no separate SPX entry
	// is needed.
	private static readonly Dictionary<string, long> KnownTickerIds = new(StringComparer.OrdinalIgnoreCase)
	{
		["SPXW"] = 913354362,
		["NDX"] = 913354088,
		["DJX"] = 925377883,
		["VIX"] = 925323875,
	};

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

	// Bound every HTTP call so a Webull stall (throttle, dropped connection, partial response)
	// can't freeze a watch tick for the .NET default 100s. 15s is comfortably above normal
	// chain/queryBatch round-trip (~1-3s) and short enough to give the tick interval headroom.
	private static HttpClient CreateClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");
		return client;
	}

	public static async Task<(IReadOnlyDictionary<string, OptionContractQuote> OptionQuotes, IReadOnlyDictionary<string, decimal> UnderlyingPrices)> FetchOptionQuotesAsync(ApiConfig config, IEnumerable<PositionRow> positionRows, CancellationToken cancellationToken)
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

		var roots = wantedSymbols.Select(s => ParsingHelpers.ParseOptionSymbol(s)).Where(p => p != null).Select(p => p!.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		using var client = CreateClient();

		// derivativeIdMap is populated as a side effect of FetchChainsAsync so we can run the queryBatch
		// fallback below for position legs that came back without a usable bid/ask.
		var derivativeIdMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var (chainQuotes, chainUnderlyings) = await FetchChainsInternalAsync(client, config, roots, derivativeIdMap, cancellationToken);
		var result = new Dictionary<string, OptionContractQuote>(chainQuotes, StringComparer.OrdinalIgnoreCase);
		var underlyingPrices = new Dictionary<string, decimal>(chainUnderlyings, StringComparer.OrdinalIgnoreCase);

		// Batch-fetch quotes for contracts the caller asked about that came back without a usable bid/ask.
		// Restrict to wantedSymbols so we don't hammer the batch endpoint with the thousands of
		// illiquid strikes the chain returns now that ParseStrategyListResponse keeps them all.
		// Trigger on missing bid OR ask (not just both-null + iv-null): the chain frequently inlines
		// IV but omits one or both of bid/ask for after-hours / low-liquidity legs, and we still need
		// queryBatch to fill them in — otherwise the leg silently propagates as un-priceable downstream.
		var needsBatch = wantedSymbols.Where(s => result.TryGetValue(s, out var q) && (q.Bid == null || q.Ask == null || q.ImpliedVolatility == null) && derivativeIdMap.ContainsKey(s)).ToList();
		if (needsBatch.Count > 0)
		{
			var ids = needsBatch.Select(s => derivativeIdMap[s]).ToList();
			Console.WriteLine($"Webull: batch-fetching {needsBatch.Count} contract(s) with missing quotes...");
			// Chunk the request: each derivativeId is 9-13 digits, and 200+ ids in a single GET URL
			// pushes past common 2 KB URL limits, after which Webull returns truncated/partial JSON
			// and many of the ids come back with null bid/ask even though the data exists. Splitting
			// into ~50-id batches keeps each URL safely under 1 KB.
			const int batchSize = 50;
			for (int i = 0; i < ids.Count; i += batchSize)
			{
				var chunk = ids.GetRange(i, Math.Min(batchSize, ids.Count - i));
				var batchQuotes = await FetchQueryBatchAsync(client, config, chunk, cancellationToken);
				foreach (var quote in batchQuotes)
					result[quote.ContractSymbol] = quote;
			}
		}

		// Surface any wanted symbols that ended up without a usable bid/ask so the user can see them
		// in the scan output (instead of silently scoring them as un-priceable). This is the diagnostic
		// the user needs to tell apart "Webull genuinely has no quote" from "our pipeline dropped it".
		var unresolved = wantedSymbols
			.Where(s => !result.TryGetValue(s, out var q) || q.Bid == null || q.Ask == null || q.Ask.Value <= 0m)
			.OrderBy(s => s, StringComparer.Ordinal)
			.ToList();
		if (unresolved.Count > 0)
			Console.WriteLine($"Webull: {unresolved.Count} wanted symbol(s) still missing bid/ask after chain+queryBatch: {string.Join(", ", unresolved.Take(10))}{(unresolved.Count > 10 ? $", +{unresolved.Count - 10} more" : "")}");

		return (result, underlyingPrices);
	}

	/// <summary>Fetches the full option chain (all contracts across all expirations) for a single ticker.
	/// Returns the raw chain quotes plus the underlying spot price reported by Webull's strategy/list endpoint
	/// and a symbol→derivativeId map. Webull's strategy/list only inlines full OI/IV for the front-most
	/// expiration; other expirations come back as stubs. Pass the returned <c>derivativeIds</c> map plus the
	/// chain dict to <see cref="RefreshContractsAsync"/> to fill in OI/IV for symbols beyond the front month.</summary>
	public static async Task<(IReadOnlyDictionary<string, OptionContractQuote> OptionQuotes, decimal? UnderlyingPrice, IReadOnlyDictionary<string, long> DerivativeIds)> FetchChainAsync(ApiConfig config, string ticker, CancellationToken cancellationToken)
	{
		using var client = CreateClient();
		var derivativeIdMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var (quotes, underlyings) = await FetchChainsInternalAsync(client, config, new[] { ticker }, derivativeIdMap, cancellationToken);
		underlyings.TryGetValue(ticker, out var spot);
		return (quotes, spot > 0m ? spot : null, derivativeIdMap);
	}

	/// <summary>Fetches the full chain for a ticker and refreshes OI/IV for chain symbols at the given
	/// expiries within ±<paramref name="strikeRangeFraction"/> of spot. Use this when downstream scoring
	/// (max-pain, GEX) needs comprehensive chain data at the position's target expiries; the standard
	/// <see cref="FetchOptionQuotesAsync"/> only fills OI/IV for the position's own legs at non-front-month
	/// expiries, which leaves max-pain/GEX with a single-strike chain that locks onto the position's strike.</summary>
	public static async Task<(IReadOnlyDictionary<string, OptionContractQuote> Quotes, decimal? Spot, IReadOnlyDictionary<string, long> DerivativeIds)> FetchChainWithExpiryRefreshAsync(
		ApiConfig config,
		string ticker,
		IReadOnlyCollection<DateTime> targetExpiries,
		decimal strikeRangeFraction,
		CancellationToken cancellationToken)
	{
		var (chain, spot, derivativeIds) = await FetchChainAsync(config, ticker, cancellationToken);
		if (!spot.HasValue || spot.Value <= 0m || chain.Count == 0 || targetExpiries.Count == 0)
			return (chain, spot, derivativeIds);

		var minStrike = spot.Value * (1m - strikeRangeFraction);
		var maxStrike = spot.Value * (1m + strikeRangeFraction);
		var expirySet = new HashSet<DateTime>(targetExpiries.Select(d => d.Date));

		var symbolsToRefresh = new List<string>();
		foreach (var (sym, q) in chain)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (!expirySet.Contains(p.ExpiryDate.Date)) continue;
			if (p.Strike < minStrike || p.Strike > maxStrike) continue;
			var hasOi = q.OpenInterest.HasValue && q.OpenInterest.Value > 0;
			var hasIv = q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m;
			if (hasOi && hasIv) continue;
			symbolsToRefresh.Add(sym);
		}

		if (symbolsToRefresh.Count == 0)
			return (chain, spot, derivativeIds);

		Console.WriteLine($"Webull: refreshing {symbolsToRefresh.Count} non-front-month chain contract(s) for max-pain/GEX accuracy...");
		var mutableChain = new Dictionary<string, OptionContractQuote>(chain, StringComparer.OrdinalIgnoreCase);
		await RefreshContractsAsync(config, mutableChain, symbolsToRefresh, derivativeIds, cancellationToken);
		return (mutableChain, spot, derivativeIds);
	}

	/// <summary>Refreshes a subset of an already-fetched chain by re-pulling each contract through Webull's
	/// queryBatch endpoint. Use this to fill in OI/IV for non-front-month contracts after <see cref="FetchChainAsync"/>.
	/// Mutates <paramref name="chain"/> in place: each successfully refreshed symbol overwrites its entry.
	/// Returns the count of contracts that were refreshed.</summary>
	public static async Task<int> RefreshContractsAsync(
		ApiConfig config,
		IDictionary<string, OptionContractQuote> chain,
		IEnumerable<string> symbols,
		IReadOnlyDictionary<string, long> derivativeIdMap,
		CancellationToken cancellationToken)
	{
		var ids = new List<long>();
		foreach (var sym in symbols)
			if (derivativeIdMap.TryGetValue(sym, out var id))
				ids.Add(id);

		if (ids.Count == 0) return 0;

		using var client = CreateClient();

		var refreshed = 0;
		const int batchSize = 50;
		for (int i = 0; i < ids.Count; i += batchSize)
		{
			var chunk = ids.GetRange(i, Math.Min(batchSize, ids.Count - i));
			var batchQuotes = await FetchQueryBatchAsync(client, config, chunk, cancellationToken);
			foreach (var quote in batchQuotes)
			{
				chain[quote.ContractSymbol] = quote;
				refreshed++;
			}
		}
		return refreshed;
	}

	/// <summary>Iterates roots, hits strategy/list per root, and accumulates contracts + underlying prices.
	/// Shared by <see cref="FetchOptionQuotesAsync"/> (which then runs queryBatch for position legs) and
	/// <see cref="FetchChainAsync"/> (which doesn't need queryBatch).</summary>
	private static async Task<(Dictionary<string, OptionContractQuote> OptionQuotes, Dictionary<string, decimal> UnderlyingPrices)> FetchChainsInternalAsync(
		HttpClient client, ApiConfig config, IEnumerable<string> roots, Dictionary<string, long> derivativeIdMap, CancellationToken cancellationToken)
	{
		var result = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var underlyingPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		foreach (var root in roots)
		{
			var tickerId = await ResolveTickerIdAsync(client, root, cancellationToken);
			if (tickerId == null)
			{
				Console.WriteLine($"Webull: could not resolve ticker ID for '{root}', skipping.");
				continue;
			}

			Console.WriteLine($"Webull: requesting option chain for {root} (tickerId {tickerId})...");

			var request = new HttpRequestMessage(HttpMethod.Post, StrategyListUrl);
			foreach (var (key, value) in DefaultHeaders) request.Headers.TryAddWithoutValidation(key, value);
			foreach (var (key, value) in config.Headers) request.Headers.TryAddWithoutValidation(key, value);

			var body = JsonSerializer.Serialize(new { expireCycle = new[] { 3, 2, 4 }, type = 0, quoteMultiplier = 100, count = -1, direction = "all", tickerId = tickerId.Value });
			request.Content = new StringContent(body, Encoding.UTF8, "application/json");

			HttpResponseMessage response;
			try
			{
				response = await client.SendAsync(request, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				// HttpClient.Timeout surfaces as TaskCanceledException with the user token not requested;
				// the when-filter above keeps real user cancels propagating, while this branch treats
				// timeouts and other transient errors as a per-root skip.
				Console.WriteLine($"Webull: request failed for {root}: {ex.Message}");
				continue;
			}

			using (response)
			{
				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"Webull: received {(int)response.StatusCode} for {root}. Session may have expired — run 'sniff' to refresh.");
					continue;
				}

				var json = await response.Content.ReadAsStringAsync(cancellationToken);
				var parsed = ParseStrategyListResponse(json, derivativeIdMap);
				if (parsed.UnderlyingPrice.HasValue)
					underlyingPrices[root] = parsed.UnderlyingPrice.Value;
				foreach (var quote in parsed.Quotes)
					result[quote.ContractSymbol] = quote;
				// Persist the symbol → derivativeId map so backfill / replay can later resolve OCC
				// symbols to the per-contract chart endpoint. Webull only serves ids for currently-live
				// expirations; once a contract expires, this registry is the only path back to its id.
				DerivativeIdRegistry.Register(derivativeIdMap);
			}
		}

		return (result, underlyingPrices);
	}

	public static async Task<Dictionary<string, long>> ResolveTickerIdsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
	{
		using var client = CreateClient();
		var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		foreach (var symbol in symbols)
		{
			var id = await ResolveTickerIdAsync(client, symbol, cancellationToken);
			if (id.HasValue)
			{
				result[symbol] = id.Value;
				Console.WriteLine($"  {symbol} → {id.Value}");
			}
			else
			{
				Console.WriteLine($"  {symbol} → not found, skipping");
			}
		}
		return result;
	}

	private static async Task<long?> ResolveTickerIdAsync(HttpClient client, string symbol, CancellationToken cancellationToken)
	{
		if (KnownTickerIds.TryGetValue(symbol, out var knownId))
			return knownId;

		try
		{
			var url = $"{TickerSearchUrl}?keyword={Uri.EscapeDataString(symbol)}&pageIndex=1&pageSize=20&regionId=6";
			var json = await client.GetStringAsync(url, cancellationToken);
			using var doc = JsonDocument.Parse(json);

			// Response is {"data": [...]} — extract the array.
			JsonElement dataArray;
			if (doc.RootElement.ValueKind == JsonValueKind.Array)
				dataArray = doc.RootElement;
			else if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
				dataArray = d;
			else
				return null;

			// Prefer exact symbol or disSymbol match.
			foreach (var item in dataArray.EnumerateArray())
			{
				var sym = item.TryGetProperty("symbol", out var s) ? s.GetString() : null;
				var disSym = item.TryGetProperty("disSymbol", out var ds) ? ds.GetString() : null;
				if ((string.Equals(sym, symbol, StringComparison.OrdinalIgnoreCase) || string.Equals(disSym, symbol, StringComparison.OrdinalIgnoreCase)) && item.TryGetProperty("tickerId", out var tid) && tid.TryGetInt64(out var id))
					return id;
			}

			// Fall back to first result with a tickerId.
			foreach (var item in dataArray.EnumerateArray())
			{
				if (item.TryGetProperty("tickerId", out var tid) && tid.TryGetInt64(out var id))
					return id;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			Console.WriteLine($"Webull: ticker search failed for '{symbol}': {ex.Message}");
		}
		return null;
	}

	private static async Task<List<OptionContractQuote>> FetchQueryBatchAsync(HttpClient client, ApiConfig config, List<long> derivativeIds, CancellationToken cancellationToken)
	{
		var quotes = new List<OptionContractQuote>();
		try
		{
			var idsParam = string.Join(",", derivativeIds);
			var url = $"{QueryBatchUrl}?derivativeIds={idsParam}";
			var request = new HttpRequestMessage(HttpMethod.Get, url);
			foreach (var (key, value) in DefaultHeaders) request.Headers.TryAddWithoutValidation(key, value);
			foreach (var (key, value) in config.Headers) request.Headers.TryAddWithoutValidation(key, value);

			var response = await client.SendAsync(request, cancellationToken);
			using (response)
			{
				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"Webull: queryBatch returned {(int)response.StatusCode}.");
					return quotes;
				}

				var json = await response.Content.ReadAsStringAsync(cancellationToken);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.ValueKind != JsonValueKind.Array)
					return quotes;

				foreach (var contract in doc.RootElement.EnumerateArray())
				{
					var symbol = contract.TryGetProperty("symbol", out var s) ? s.GetString() : null;
					if (string.IsNullOrWhiteSpace(symbol)) continue;
					// queryBatch was called with derivativeIds we explicitly asked for, so any contract in
					// the response is wanted by definition. The previous wantedSymbols.Contains() guard
					// silently dropped responses whose symbol formatting drifted from our canonical OCC
					// (whitespace padding, case), leaving the caller's result dict with the original
					// null-priced chain entry — exactly the "scan returns null bid/ask" symptom.

					quotes.Add(new OptionContractQuote(
						ContractSymbol: symbol,
						LastPrice: GetDecimal(contract, "close"),
						Bid: GetBestPrice(contract, "bidList"),
						Ask: GetBestPrice(contract, "askList"),
						Change: GetDecimal(contract, "change"),
						PercentChange: GetDecimal(contract, "changeRatio"),
						Volume: GetLong(contract, "volume"),
						OpenInterest: GetLong(contract, "openInterest"),
						ImpliedVolatility: GetDecimal(contract, "impVol"),
						HistoricalVolatility: GetDecimal(contract, "hiv"),
						ImpliedVolatility5Day: GetDecimal(contract, "iv5")
					));
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			Console.WriteLine($"Webull: queryBatch failed: {ex.Message}");
		}
		return quotes;
	}

	/// <summary>Parses every contract in the strategy/list response into the result list. The caller-side
	/// <c>wantedSymbols</c> filter that used to live here was removed so a single chain fetch populates the
	/// entire chain — the opener bootstrap pass (which only knows a placeholder symbol) needs all contracts
	/// downstream, and other callers tolerate the larger dictionary because they look up by symbol anyway.</summary>
	private static (List<OptionContractQuote> Quotes, decimal? UnderlyingPrice) ParseStrategyListResponse(string json, Dictionary<string, long> derivativeIdMap)
	{
		var quotes = new List<OptionContractQuote>();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		decimal? underlyingPrice = GetDecimal(root, "close");

		if (!root.TryGetProperty("expireDateList", out var expiryList) || expiryList.ValueKind != JsonValueKind.Array)
			return (quotes, underlyingPrice);

		foreach (var expiry in expiryList.EnumerateArray())
		{
			if (!expiry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				continue;

			foreach (var contract in data.EnumerateArray())
			{
				var symbol = contract.TryGetProperty("symbol", out var s) ? s.GetString() : null;
				if (string.IsNullOrWhiteSpace(symbol)) continue;

				if (contract.TryGetProperty("tickerId", out var tid) && tid.TryGetInt64(out var derivId))
					derivativeIdMap[symbol] = derivId;

				quotes.Add(new OptionContractQuote(
					ContractSymbol: symbol,
					LastPrice: GetDecimal(contract, "close"),
					Bid: GetBestPrice(contract, "bidList"),
					Ask: GetBestPrice(contract, "askList"),
					Change: GetDecimal(contract, "change"),
					PercentChange: GetDecimal(contract, "changeRatio"),
					Volume: GetLong(contract, "volume"),
					OpenInterest: GetLong(contract, "openInterest"),
					ImpliedVolatility: GetDecimal(contract, "impVol"),
					HistoricalVolatility: GetDecimal(contract, "hiv"),
					ImpliedVolatility5Day: GetDecimal(contract, "iv5")
				));
			}
		}

		return (quotes, underlyingPrice);
	}

	private static decimal? GetBestPrice(JsonElement contract, string listProp)
	{
		if (!contract.TryGetProperty(listProp, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
			return null;
		return GetDecimal(list[0], "price");
	}

	private static decimal? GetDecimal(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : null,
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
}
