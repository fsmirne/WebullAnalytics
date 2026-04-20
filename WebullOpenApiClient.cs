using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics;

internal sealed class WebullOpenApiException : Exception
{
	internal string? ErrorCode { get; }
	internal int HttpStatus { get; }
	internal WebullOpenApiException(string? errorCode, string message, int httpStatus) : base(message)
	{
		ErrorCode = errorCode;
		HttpStatus = httpStatus;
	}
}

internal sealed class WebullOpenApiClient : IDisposable
{
	private readonly HttpClient _http;
	private readonly TradeAccount _account;
	private string Host => new Uri(_account.BaseUrl).Host;

	internal WebullOpenApiClient(TradeAccount account)
	{
		_account = account;
		_http = new HttpClient { BaseAddress = new Uri(account.BaseUrl) };
	}

	public void Dispose() => _http.Dispose();

	// ─── Preview / Place ──────────────────────────────────────────────────────

	internal sealed record PreviewResult(
		[property: JsonPropertyName("estimated_cost")] string? EstimatedCost,
		[property: JsonPropertyName("estimated_transaction_fee")] string? EstimatedTransactionFee);

	internal sealed record PlaceResult(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("client_combo_order_id")] string? ClientComboOrderId,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId);

	internal async Task<PreviewResult> PreviewOrderAsync(OrderRequestBody body, CancellationToken ct = default) =>
		await PostAsync<PreviewResult>("/openapi/trade/order/preview", body, ct);

	internal async Task<PlaceResult> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct = default) =>
		await PostAsync<PlaceResult>("/openapi/trade/order/place", body, ct);

	// ─── Cancel ───────────────────────────────────────────────────────────────

	private sealed class CancelRequest
	{
		[JsonPropertyName("account_id")] public string AccountId { get; set; } = "";
		[JsonPropertyName("client_order_id")] public string ClientOrderId { get; set; } = "";
	}

	internal sealed record CancelResult(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId);

	internal async Task<CancelResult> CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
		await PostAsync<CancelResult>("/openapi/trade/order/cancel",
			new CancelRequest { AccountId = _account.AccountId, ClientOrderId = clientOrderId }, ct);

	// ─── List open orders ─────────────────────────────────────────────────────

	/// <summary>Open-orders envelope: outer ClientOrderId is the cancellation key; Orders[] holds the actual detail records (same shape as /order/detail).</summary>
	internal sealed record OpenOrder(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("combo_type")] string? ComboType,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId,
		[property: JsonPropertyName("orders")] List<OrderDetailOrder>? Orders);

	/// <summary>Diagnostic: fetches the first page of open orders as raw JSON string (bypasses deserialization).</summary>
	internal async Task<string> ListOpenOrdersRawAsync(CancellationToken ct = default)
	{
		var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["account_id"] = _account.AccountId,
			["page_size"] = "100",
		};
		const string path = "/openapi/trade/order/open";
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, Host, path, query, null, _account.AppId);
		var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
		var uri = $"{path}?{qs}";
		using var req = new HttpRequestMessage(HttpMethod.Get, uri);
		foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
		using var resp = await _http.SendAsync(req, ct);
		return await resp.Content.ReadAsStringAsync(ct);
	}

	/// <summary>Iterates pages until the server returns fewer than page_size results.</summary>
	internal async Task<List<OpenOrder>> ListOpenOrdersAsync(CancellationToken ct = default)
	{
		const int pageSize = 100;
		var all = new List<OpenOrder>();
		string? cursor = null;
		while (true)
		{
			var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
			{
				["account_id"] = _account.AccountId,
				["page_size"] = pageSize.ToString(),
			};
			if (cursor != null) query["last_client_order_id"] = cursor;
			var page = await GetAsync<List<OpenOrder>>("/openapi/trade/order/open", query, ct);
			if (page.Count == 0) break;
			all.AddRange(page);
			if (page.Count < pageSize) break;
			cursor = page[^1].ClientOrderId;
			if (string.IsNullOrEmpty(cursor)) break;
		}
		return all;
	}

	// ─── Order detail ─────────────────────────────────────────────────────────

	internal sealed record OrderDetailLeg(
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("side")] string? Side,
		[property: JsonPropertyName("quantity")] string? Quantity,
		[property: JsonPropertyName("option_type")] string? OptionType,
		[property: JsonPropertyName("strike_price")] string? StrikePrice,
		[property: JsonPropertyName("option_expire_date")] string? OptionExpireDate);

	internal sealed record OrderDetailOrder(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId,
		[property: JsonPropertyName("status")] string? Status,
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("side")] string? Side,
		[property: JsonPropertyName("total_quantity")] string? TotalQuantity,
		[property: JsonPropertyName("filled_quantity")] string? FilledQuantity,
		[property: JsonPropertyName("filled_price")] string? FilledPrice,
		[property: JsonPropertyName("limit_price")] string? LimitPrice,
		[property: JsonPropertyName("place_time")] string? PlaceTime,
		[property: JsonPropertyName("place_time_at")] string? PlaceTimeAt,
		[property: JsonPropertyName("filled_time")] string? FilledTime,
		[property: JsonPropertyName("filled_time_at")] string? FilledTimeAt,
		[property: JsonPropertyName("position_intent")] string? PositionIntent,
		[property: JsonPropertyName("legs")] List<OrderDetailLeg>? Legs);

	internal sealed record OrderDetail(
		[property: JsonPropertyName("combo_type")] string? ComboType,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId,
		[property: JsonPropertyName("orders")] List<OrderDetailOrder>? Orders);

	internal async Task<OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct = default) =>
		await GetAsync<OrderDetail>("/openapi/trade/order/detail",
			new SortedDictionary<string, string>(StringComparer.Ordinal)
			{
				["account_id"] = _account.AccountId,
				["client_order_id"] = clientOrderId,
			}, ct);

	// ─── App subscriptions (account list) ────────────────────────────────────

	internal sealed record AppSubscription(
		[property: JsonPropertyName("subscription_id")] string? SubscriptionId,
		[property: JsonPropertyName("user_id")] string? UserId,
		[property: JsonPropertyName("account_id")] string? AccountId,
		[property: JsonPropertyName("account_number")] string? AccountNumber);

	/// <summary>Lists the accounts this app is subscribed to. The account_id field in the response
	/// is the identifier required for all other trade/account endpoints — distinct from the visible
	/// brokerage account_number. Does not require account_id in the request.</summary>
	internal async Task<List<AppSubscription>> ListAppSubscriptionsAsync(CancellationToken ct = default) =>
		await GetAsync<List<AppSubscription>>("/openapi/account/list", new SortedDictionary<string, string>(StringComparer.Ordinal), ct);

	// ─── Account positions ────────────────────────────────────────────────────

	internal sealed record AccountHolding(
		[property: JsonPropertyName("position_id")] string? PositionId,
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("instrument_type")] string? InstrumentType,
		[property: JsonPropertyName("currency")] string? Currency,
		[property: JsonPropertyName("cost_price")] string? CostPrice,
		[property: JsonPropertyName("quantity")] string? Quantity,
		[property: JsonPropertyName("cost")] string? Cost,
		[property: JsonPropertyName("last_price")] string? LastPrice,
		[property: JsonPropertyName("market_value")] string? MarketValue,
		[property: JsonPropertyName("unrealized_profit_loss")] string? UnrealizedProfitLoss,
		[property: JsonPropertyName("unrealized_profit_loss_rate")] string? UnrealizedProfitLossRate,
		[property: JsonPropertyName("proportion")] string? Proportion,
		[property: JsonPropertyName("day_profit_loss")] string? DayProfitLoss,
		[property: JsonPropertyName("day_realized_profit_loss")] string? DayRealizedProfitLoss);

	/// <summary>Fetches all account positions. The endpoint returns a flat array; the sandbox does not
	/// appear to paginate — if production requires it, the caller can extend this to loop on last_position_id.</summary>
	internal async Task<List<AccountHolding>> FetchAccountPositionsAsync(CancellationToken ct = default)
	{
		var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["account_id"] = _account.AccountId,
			["page_size"] = "100",
		};
		var list = await GetAsync<List<AccountHolding>>("/openapi/assets/positions", query, ct);
		return list;
	}

	// ─── Account balance ──────────────────────────────────────────────────────

	internal sealed record AccountBalance(
		[property: JsonPropertyName("total_cash_balance")] string? TotalCashBalance,
		[property: JsonPropertyName("total_unrealized_profit_loss")] string? TotalUnrealizedProfitLoss,
		[property: JsonPropertyName("total_asset_currency")] string? TotalAssetCurrency);

	/// <summary>Fetches the account balance summary.</summary>
	internal async Task<AccountBalance> FetchAccountBalanceAsync(CancellationToken ct = default) =>
		await GetAsync<AccountBalance>("/openapi/assets/balance",
			new SortedDictionary<string, string>(StringComparer.Ordinal) { ["account_id"] = _account.AccountId }, ct);

	// ─── Transport ────────────────────────────────────────────────────────────

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = false,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNameCaseInsensitive = true,
	};

	private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
	{
		var json = JsonSerializer.Serialize(body, JsonOptions);
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, Host, path, new Dictionary<string, string>(), json, _account.AppId);
		using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
		using var resp = await _http.SendAsync(req, ct);
		return await Read<T>(resp);
	}

	private async Task<T> GetAsync<T>(string path, IReadOnlyDictionary<string, string> query, CancellationToken ct)
	{
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, Host, path, query, null, _account.AppId);
		var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
		var uri = string.IsNullOrEmpty(qs) ? path : $"{path}?{qs}";
		using var req = new HttpRequestMessage(HttpMethod.Get, uri);
		foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
		using var resp = await _http.SendAsync(req, ct);
		return await Read<T>(resp);
	}

	private static async Task<T> Read<T>(HttpResponseMessage resp)
	{
		var body = await resp.Content.ReadAsStringAsync();
		if ((int)resp.StatusCode >= 500)
			throw new WebullOpenApiException(null, $"Webull API unavailable (HTTP {(int)resp.StatusCode}): {Truncate(body, 200)}", (int)resp.StatusCode);
		if ((int)resp.StatusCode >= 400)
		{
			try
			{
				using var doc = JsonDocument.Parse(body);
				var code = doc.RootElement.TryGetProperty("error_code", out var c) ? c.GetString() : null;
				var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
				throw new WebullOpenApiException(code, msg, (int)resp.StatusCode);
			}
			catch (JsonException)
			{
				throw new WebullOpenApiException(null, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}", (int)resp.StatusCode);
			}
		}
		var result = JsonSerializer.Deserialize<T>(body, JsonOptions);
		if (result == null) throw new WebullOpenApiException(null, "Empty response body", (int)resp.StatusCode);
		return result;
	}

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
