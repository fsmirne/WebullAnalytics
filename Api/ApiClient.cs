using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.Trading;

namespace WebullAnalytics.Api;

internal static class ApiClient
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

	internal static async Task FetchOrdersToJsonl(ApiConfig config, IReadOnlyCollection<long> tickerIds, string outputPath)
	{
		using var client = new HttpClient();
		client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");
		var tmpPath = outputPath + ".tmp";
		var wroteAny = false;

		try
		{
			await using var writer = new StreamWriter(tmpPath);

			foreach (var tickerId in tickerIds)
			{
				var url = $"{OrderListUrl}?tickerId={tickerId}&startDate={config.Webull.StartDate}&endDate={config.Webull.EndDate}&limit={config.Webull.Limit}&secAccountId={config.Webull.SecAccountId}";
				var request = new HttpRequestMessage(HttpMethod.Get, url);

				foreach (var (key, value) in DefaultHeaders)
					request.Headers.TryAddWithoutValidation(key, value);
				foreach (var (key, value) in config.Webull.Headers)
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
				wroteAny = true;
			}
		}
		catch
		{
			try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
			throw;
		}

		if (!wroteAny)
		{
			try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
			throw new InvalidOperationException("No valid orderList responses were returned; refusing to overwrite existing orders file.");
		}

		File.Move(tmpPath, outputPath, overwrite: true);
	}
}

/// <summary>Top-level api-config.json. Provider credentials are grouped into per-source sections so the file
/// stays legible as more data sources are added. <see cref="Webull"/> and <see cref="Massive"/> always exist
/// (empty if absent); <see cref="Schwab"/> is null until `wa schwab login` populates it.</summary>
internal sealed class ApiConfig
{
	[JsonPropertyName("webull")]
	public WebullConfig Webull { get; set; } = new();

	[JsonPropertyName("massive")]
	public MassiveConfig Massive { get; set; } = new();

	[JsonPropertyName("schwab")]
	public SchwabConfig? Schwab { get; set; }
}

/// <summary>Webull credentials: scraped session headers + 6-digit unlock pin (for `wa sniff` / option-quote and
/// chart fetches), order-history lookup params, and the Webull OpenAPI trade accounts used for order placement.</summary>
internal sealed class WebullConfig
{
	[JsonPropertyName("secAccountId")]
	public string SecAccountId { get; set; } = "";

	[JsonPropertyName("tickers")]
	public string[] Tickers { get; set; } = [];

	[JsonPropertyName("startDate")]
	public string StartDate { get; set; } = "";

	[JsonPropertyName("endDate")]
	public string EndDate { get; set; } = "";

	[JsonPropertyName("limit")]
	public int Limit { get; set; } = 10000;

	[JsonPropertyName("pin")]
	public string Pin { get; set; } = "";

	[JsonPropertyName("headers")]
	public Dictionary<string, string> Headers { get; set; } = new();

	[JsonPropertyName("defaultAccount")]
	public string? DefaultAccount { get; set; }

	[JsonPropertyName("accounts")]
	public List<TradeAccount> Accounts { get; set; } = new();
}

/// <summary>massive.com (Polygon mirror) credentials for the option-bar backfill.</summary>
internal sealed class MassiveConfig
{
	[JsonPropertyName("apiKey")]
	public string ApiKey { get; set; } = "";

	/// <summary>massive.com requests-per-minute cap. The basic (free) tier hard-caps at 5 req/min (6th → HTTP 429);
	/// paid tiers (Options Starter+) are "Unlimited API Calls". Set this to match your tier so the client paces
	/// to the real limit instead of self-throttling to 5. Use 0 (or any large value) to disable pacing entirely
	/// on an unlimited plan. Default 5 keeps the free tier safe out of the box.</summary>
	[JsonPropertyName("maxRequestsPerMinute")]
	public int MaxRequestsPerMinute { get; set; } = 5;
}

/// <summary>Schwab Trader API OAuth credentials + token cache. <c>ClientId</c>/<c>ClientSecret</c>/<c>RedirectUri</c>
/// come from the registered app at developer.schwab.com and are set by hand. The token fields are populated by
/// <c>wa schwab login</c> (refresh token) and refreshed automatically during capture (access token). The refresh
/// token has a hard 7-day expiry that cannot be extended — re-run <c>wa schwab login</c> weekly.</summary>
internal sealed class SchwabConfig
{
	[JsonPropertyName("clientId")]
	public string ClientId { get; set; } = "";

	[JsonPropertyName("clientSecret")]
	public string ClientSecret { get; set; } = "";

	[JsonPropertyName("redirectUri")]
	public string RedirectUri { get; set; } = "";

	[JsonPropertyName("refreshToken")]
	public string? RefreshToken { get; set; }

	[JsonPropertyName("accessToken")]
	public string? AccessToken { get; set; }

	/// <summary>UTC instant the cached access token stops being valid (issued-at + expires_in, ~30 min).</summary>
	[JsonPropertyName("accessTokenExpiresUtc")]
	public DateTime? AccessTokenExpiresUtc { get; set; }

	/// <summary>UTC instant the refresh token was issued by the last <c>wa schwab login</c>. Schwab forces a hard
	/// 7-day expiry, so this drives the "re-login soon" warning.</summary>
	[JsonPropertyName("refreshTokenIssuedUtc")]
	public DateTime? RefreshTokenIssuedUtc { get; set; }
}
