using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics;

public static class ApiClient
{
	private const string OrderListUrl = "https://ustrade.webullfinance.com/api/trading/v1/webull/profitloss/ticker/orderList/v2";

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

	public static async Task FetchOrdersToJsonl(ApiConfig config, string outputPath)
	{
		using var client = new HttpClient();
		client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");

		await using var writer = new StreamWriter(outputPath);

		foreach (var tickerId in config.TickerIds)
		{
			var url = $"{OrderListUrl}?tickerId={tickerId}&startDate={config.StartDate}&endDate={config.EndDate}&limit={config.Limit}&secAccountId={config.SecAccountId}";
			var request = new HttpRequestMessage(HttpMethod.Get, url);

			foreach (var (key, value) in DefaultHeaders)
				request.Headers.TryAddWithoutValidation(key, value);
			foreach (var (key, value) in config.Headers)
				request.Headers.TryAddWithoutValidation(key, value);

			var response = await client.SendAsync(request);
			response.EnsureSuccessStatusCode();

			var json = await response.Content.ReadAsStringAsync();

			// Validate it's a JSON object with an orderList before writing
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("orderList", out var orderList))
			{
				Console.WriteLine($"Warning: tickerId {tickerId} returned no orderList, skipping.");
				continue;
			}

			Console.WriteLine($"Fetched tickerId {tickerId}: {orderList.GetArrayLength()} orders");

			// Write compact single-line JSON (one ticker per line)
			await writer.WriteLineAsync(json);
		}
	}
}

public sealed class ApiConfig
{
	[JsonPropertyName("secAccountId")]
	public string SecAccountId { get; set; } = "";

	[JsonPropertyName("tickerIds")]
	public long[] TickerIds { get; set; } = [];

	[JsonPropertyName("startDate")]
	public string StartDate { get; set; } = "";

	[JsonPropertyName("endDate")]
	public string EndDate { get; set; } = "";

	[JsonPropertyName("limit")]
	public int Limit { get; set; } = 10000;

	[JsonPropertyName("headers")]
	public Dictionary<string, string> Headers { get; set; } = new();
}
