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

	internal sealed record OpenOrder(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId,
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("side")] string? Side,
		[property: JsonPropertyName("status")] string? Status,
		[property: JsonPropertyName("total_quantity")] string? TotalQuantity,
		[property: JsonPropertyName("filled_quantity")] string? FilledQuantity,
		[property: JsonPropertyName("order_type")] string? OrderType,
		[property: JsonPropertyName("combo_type")] string? ComboType);

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
		[property: JsonPropertyName("place_time")] string? PlaceTime,
		[property: JsonPropertyName("filled_time")] string? FilledTime,
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
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, Host, path, new Dictionary<string, string>(), json);
		using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
		using var resp = await _http.SendAsync(req, ct);
		return await Read<T>(resp);
	}

	private async Task<T> GetAsync<T>(string path, IReadOnlyDictionary<string, string> query, CancellationToken ct)
	{
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, Host, path, query, null);
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
