using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics.Api;

/// <summary>One option contract row from Webull's market-wide option leaderboard. <see cref="ChangeRatio"/> and
/// <see cref="ImpliedVol"/> are 0-1 fractions as Webull serves them; <see cref="OiChange"/> is the day's open-interest delta.</summary>
internal sealed record OptionRankRow(string Symbol, string Underlying, DateOnly Expiry, decimal Strike, string Right, decimal? Last, decimal? ChangeRatio, decimal? Mid, long Volume, long OpenInterest, long OiChange, decimal? ImpliedVol, decimal? Turnover);

/// <summary><see cref="HasOpraPermission"/> is Webull's verdict on the session: false means the caller was served as an anonymous
/// (no real-time OPRA entitlement) client.</summary>
internal sealed record OptionRankPage(DateTimeOffset? UpdatedAt, bool HasOpraPermission, IReadOnlyList<OptionRankRow> Rows);

/// <summary>Fetches Webull's "Top Options" leaderboard (the web client's option rank list). The endpoint answers anonymously,
/// but then reports <c>hasOpraPermission:false</c>; the sniffed session's <c>access_token</c> alone flips it to true — no
/// identity headers or <c>x-s</c> signature needed, and a stale/invalid token reads as false (verified 2026-10-17). It ranks
/// contracts across the whole US market — there is no underlying filter — and only lists unexpired contracts, so after the
/// close the session's expiring contracts (e.g. SPY 0DTE) have already dropped out.</summary>
internal static class WebullOptionRankClient
{
	private const string RankListUrl = "https://quotes-gw.webullfintech.com/api/wlas/option/rank/list";
	private const int UsRegionId = 6;

	private static readonly HttpClient SharedClient = new(PooledHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

	static WebullOptionRankClient()
	{
		SharedClient.DefaultRequestHeaders.Referrer = new Uri("https://app.webull.com/");
	}

	/// <summary>Webull rankType codes keyed by the CLI's <c>--by</c> names. The contract-level tabs only: the
	/// "Total Vol"/"Total OI" tabs (totalVolume/totalPosition) rank underlyings and return a different row shape.</summary>
	internal static readonly IReadOnlyDictionary<string, string> RankTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["volume"] = "volume",
		["turnover"] = "turnover",
		["oi"] = "position",
		["oi-up"] = "posIncrease",
		["oi-down"] = "posDecrease",
		["iv"] = "impVol",
	};

	/// <summary>Returns the top <paramref name="count"/> contracts for the given Webull rankType, highest first, in one page,
	/// authenticated with the sniffed session's <c>access_token</c>.</summary>
	public static async Task<OptionRankPage> FetchAsync(string accessToken, string rankType, int count, CancellationToken cancellationToken)
	{
		var url = $"{RankListUrl}?regionId={UsRegionId}&rankType={Uri.EscapeDataString(rankType)}&pageIndex=1&pageSize={count.ToString(CultureInfo.InvariantCulture)}";
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("access_token", accessToken);
		using var response = await SharedClient.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		return ParseResponse(await response.Content.ReadAsStringAsync(cancellationToken));
	}

	internal static OptionRankPage ParseResponse(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		DateTimeOffset? updatedAt = root.TryGetProperty("latestUpdateTime", out var ts) && ts.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
		var hasOpraPermission = root.TryGetProperty("hasOpraPermission", out var opra) && opra.ValueKind == JsonValueKind.True;
		var rows = new List<OptionRankRow>();
		if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
			foreach (var item in data.EnumerateArray())
				if (ParseRow(item) is { } row) rows.Add(row);
		return new OptionRankPage(updatedAt, hasOpraPermission, rows);
	}

	private static OptionRankRow? ParseRow(JsonElement item)
	{
		if (!item.TryGetProperty("derivative", out var d) || !item.TryGetProperty("values", out var v)) return null;
		if (GetString(d, "symbol") is not { } symbol || GetString(d, "unSymbol") is not { } underlying) return null;
		if (!DateOnly.TryParseExact(GetString(d, "expireDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) return null;
		if (GetDecimal(d, "strikePrice") is not { } strike) return null;
		var right = GetString(d, "direction") == "put" ? "P" : "C";
		return new OptionRankRow(symbol, underlying, expiry, strike, right, GetDecimal(v, "price"), GetDecimal(v, "changeRatio"), GetDecimal(v, "middlePrice"), GetLong(v, "volume"), GetLong(v, "position"), GetLong(v, "positionChange"), GetDecimal(v, "implVol"), GetDecimal(v, "turnover"));
	}

	private static string? GetString(JsonElement e, string prop) => e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

	private static decimal? GetDecimal(JsonElement e, string prop) => decimal.TryParse(GetString(e, prop), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

	private static long GetLong(JsonElement e, string prop) => long.TryParse(GetString(e, prop), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
