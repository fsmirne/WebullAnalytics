using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.Trading;

namespace WebullAnalytics.Api;

internal static class ApiClient
{
	private const string OrderListUrl = "https://ustrade.webullfinance.com/api/trading/v1/webull/profitloss/ticker/orderList/v2";
	private const string CashRecordUrl = "https://ustrade.webullfinance.com/api/trading/v1/webull/cashrecord/activities/v2";

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

	/// <summary>One fund activity from Webull's consumer cash-record ledger: a trade fill, expiry settlement,
	/// fee, interest, or transfer. <see cref="RunningTotal"/> is the post-event cash balance Webull
	/// stamps on each row (its <c>totalAmount</c> field), so the first item's running total is the
	/// platform's current cash. <see cref="Amount"/> is signed (incoming positive, outgoing negative).</summary>
	internal sealed record FundActivity(string OccurredTime, string Name, string Category, string Description, decimal Amount, decimal RunningTotal, string Direction);

	/// <summary>Result of <see cref="FetchFundActivitiesAsync"/>: the ledger rows (newest-first, as Webull
	/// returns them) plus the server's <c>updateTime</c> stamp.</summary>
	internal sealed record FundActivitiesResult(string? UpdateTime, IReadOnlyList<FundActivity> Items);

	/// <summary>Pulls the most-recent <paramref name="pageSize"/> fund activities (the running-balance
	/// ledger the Webull platform shows). Uses the same scraped session as <see cref="FetchOrdersToJsonl"/> /
	/// <c>wa sniff</c> against the same trading host. Mirrors <see cref="WebullChartsClient"/>'s contract:
	/// drops the per-URL <c>x-s</c>/<c>x-sv</c> signature headers (the endpoint doesn't validate them —
	/// verified — and a stale value computed for a different request is meaningless) and refreshes
	/// <c>t_time</c> to defeat the freshness check. Throws on transport error or non-2xx so the caller can
	/// surface a "session may be stale — run wa sniff" hint.</summary>
	internal static async Task<FundActivitiesResult> FetchFundActivitiesAsync(ApiConfig config, int pageSize, CancellationToken cancellation)
	{
		using var client = new HttpClient();
		client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");
		var json = await SendCashRecordPageAsync(client, config, pageIndex: 1, pageSize, dateRange: "CY", cancellation);
		return ParseFundActivities(json);
	}

	/// <summary>POSTs one cash-record page and returns the raw response body. Shared by the on-demand
	/// `wa ledger` view (page 1, CY) and the full-history pager <see cref="FetchCashRecordToJsonl"/>.</summary>
	private static async Task<string> SendCashRecordPageAsync(HttpClient client, ApiConfig config, int pageIndex, int pageSize, string dateRange, CancellationToken cancellation)
	{
		var secId = config.Webull.SecAccountId;
		var url = $"{CashRecordUrl}?secAccountId={secId}";
		var body = $"{{\"secAccountId\":{secId},\"pageIndex\":{pageIndex},\"pageSize\":{pageSize},\"conditions\":[{{\"key\":\"date\",\"values\":[\"{dateRange}\"]}},{{\"key\":\"category\",\"values\":[\"all\"]}}]}}";

		using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		request.Headers.TryAddWithoutValidation("accept", "*/*");
		foreach (var (key, value) in DefaultHeaders)
			request.Headers.TryAddWithoutValidation(key, value);
		foreach (var (key, value) in config.Webull.Headers)
		{
			if (string.Equals(key, "x-s", StringComparison.OrdinalIgnoreCase)) continue;
			if (string.Equals(key, "x-sv", StringComparison.OrdinalIgnoreCase)) continue;
			if (string.Equals(key, "t_time", StringComparison.OrdinalIgnoreCase)) continue;
			request.Headers.TryAddWithoutValidation(key, value);
		}
		request.Headers.TryAddWithoutValidation("t_time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

		var response = await client.SendAsync(request, cancellation);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync(cancellation);
	}

	/// <summary>Pages the FULL cash record (date=all — verified accepted alongside the ledger view's CY; avoids
	/// losing prior-year rows every January) into a JSONL file, one raw item per line, newest-first as Webull
	/// returns them. This file is the broker's posted-cash truth `wa report` overlays on orders.jsonl: exact
	/// per-leg amounts with the REAL fees folded in (the order export's fee field overstates XSP by ~25x) and
	/// the cash settlements the order export never carries. Same scraped session as <see cref="FetchOrdersToJsonl"/>;
	/// throws on transport error / non-2xx; refuses to overwrite an existing file when nothing comes back.</summary>
	internal static async Task<int> FetchCashRecordToJsonl(ApiConfig config, string outputPath, CancellationToken cancellation)
	{
		const int pageSize = 100;
		const int maxPages = 100;  // 10k rows — far above any realistic ledger; guards a paging bug from looping forever

		using var client = new HttpClient();
		client.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");
		var tmpPath = outputPath + ".tmp";
		var total = 0;

		try
		{
			await using var writer = new StreamWriter(tmpPath);
			for (var page = 1; page <= maxPages; page++)
			{
				var json = await SendCashRecordPageAsync(client, config, page, pageSize, dateRange: "all", cancellation);
				using var doc = JsonDocument.Parse(json);
				if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
					break;
				foreach (var item in items.EnumerateArray())
					await writer.WriteLineAsync(JsonSerializer.Serialize(item));
				total += items.GetArrayLength();
				if (items.GetArrayLength() < pageSize) break;
				await Task.Delay(300, cancellation);  // stay polite on the consumer endpoint
			}
		}
		catch
		{
			try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
			throw;
		}

		if (total == 0)
		{
			try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
			throw new InvalidOperationException("Cash record returned no items; refusing to overwrite the existing file.");
		}

		File.Move(tmpPath, outputPath, overwrite: true);
		return total;
	}

	/// <summary>Parses the cash-record activities payload. Tolerant of missing/typed-null fields — every
	/// item field defaults to empty/zero rather than throwing, since Webull's schema varies by row type.</summary>
	internal static FundActivitiesResult ParseFundActivities(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		var updateTime = root.TryGetProperty("updateTime", out var ut) && ut.ValueKind == JsonValueKind.String ? ut.GetString() : null;

		var items = new List<FundActivity>();
		if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			foreach (var it in arr.EnumerateArray())
			{
				string Str(string name) => it.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
				decimal Dec(string name) => it.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
				items.Add(new FundActivity(Str("occurredTime"), Str("name"), Str("category2"), Str("description"), Dec("amount"), Dec("totalAmount"), Str("direction")));
			}
		}

		return new FundActivitiesResult(updateTime, items);
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
