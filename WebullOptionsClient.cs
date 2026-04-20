using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics;

public static class WebullOptionsClient
{
    private const string TickerSearchUrl = "https://quotes-gw.webullfintech.com/api/search/pc/tickers";
    private const string StrategyListUrl = "https://quotes-gw.webullfintech.com/api/quote/option/strategy/list";
    private const string QueryBatchUrl = "https://quotes-gw.webullfintech.com/api/quote/option/quotes/queryBatch";

    // Index/derivative tickers that the search endpoint can't resolve.
    // Add entries here as needed — use 'sniff' or browser network tools to find the tickerId
    // from Webull's option chain requests for the index.
    private static readonly Dictionary<string, long> KnownTickerIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPX"] = 913324359,
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

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");

        var result = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
        var underlyingPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        // OCC symbol → derivative tickerId, for contracts needing a batch refresh.
        var derivativeIdMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

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
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
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
                var parsed = ParseStrategyListResponse(json, wantedSymbols, derivativeIdMap);
                if (parsed.UnderlyingPrice.HasValue)
                    underlyingPrices[root] = parsed.UnderlyingPrice.Value;
                foreach (var quote in parsed.Quotes)
                    result[quote.ContractSymbol] = quote;
            }
        }

        // Batch-fetch quotes for contracts that had no pricing data in the strategy/list response.
        var needsBatch = result.Where(kv => kv.Value.Bid == null && kv.Value.Ask == null && kv.Value.ImpliedVolatility == null).Select(kv => kv.Key).Where(derivativeIdMap.ContainsKey).ToList();
        if (needsBatch.Count > 0)
        {
            var ids = needsBatch.Select(s => derivativeIdMap[s]).ToList();
            Console.WriteLine($"Webull: batch-fetching {needsBatch.Count} contract(s) with missing quotes...");
            var batchQuotes = await FetchQueryBatchAsync(client, config, ids, wantedSymbols, cancellationToken);
            foreach (var quote in batchQuotes)
                result[quote.ContractSymbol] = quote;
        }

        return (result, underlyingPrices);
    }

    public static async Task<Dictionary<string, long>> ResolveTickerIdsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
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
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) throw;
            Console.WriteLine($"Webull: ticker search failed for '{symbol}': {ex.Message}");
        }
        return null;
    }

    private static async Task<List<OptionContractQuote>> FetchQueryBatchAsync(HttpClient client, ApiConfig config, List<long> derivativeIds, HashSet<string> wantedSymbols, CancellationToken cancellationToken)
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
                    if (string.IsNullOrWhiteSpace(symbol) || !wantedSymbols.Contains(symbol)) continue;

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
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) throw;
            Console.WriteLine($"Webull: queryBatch failed: {ex.Message}");
        }
        return quotes;
    }

    private static (List<OptionContractQuote> Quotes, decimal? UnderlyingPrice) ParseStrategyListResponse(string json, HashSet<string> wantedSymbols, Dictionary<string, long> derivativeIdMap)
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
                if (string.IsNullOrWhiteSpace(symbol) || !wantedSymbols.Contains(symbol)) continue;

                // Always capture the derivative tickerId for potential batch refresh.
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
