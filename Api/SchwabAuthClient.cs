using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebullAnalytics.Api;

/// <summary>Schwab Trader API OAuth2 (authorization-code grant) token management. The access token lives ~30 min
/// and is refreshed silently from the refresh token; the refresh token has a hard 7-day expiry that Schwab will
/// not extend, so <c>wa schwab login</c> must be re-run weekly. Tokens are cached back into api-config.json's
/// <c>schwab</c> subtree (only that subtree is rewritten, preserving the rest of the file).</summary>
internal static class SchwabAuthClient
{
	private const string AuthorizeUrl = "https://api.schwabapi.com/v1/oauth/authorize";
	private const string TokenUrl = "https://api.schwabapi.com/v1/oauth/token";
	private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

	/// <summary>The browser URL the user visits to authorize the app. After login Schwab redirects to
	/// <c>RedirectUri</c> with <c>?code=...</c> in the query string.</summary>
	public static string BuildAuthorizeUrl(SchwabConfig schwab) =>
		$"{AuthorizeUrl}?client_id={Uri.EscapeDataString(schwab.ClientId)}&redirect_uri={Uri.EscapeDataString(schwab.RedirectUri)}";

	/// <summary>Extracts the <c>code</c> query parameter from the pasted post-login redirect URL. Schwab URL-encodes
	/// the code (it commonly ends with <c>%40</c> = '@'), so we unescape it.</summary>
	public static string? ExtractCode(string redirectUrl)
	{
		if (!Uri.TryCreate(redirectUrl.Trim(), UriKind.Absolute, out var uri)) return null;
		foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = pair.IndexOf('=');
			if (eq <= 0) continue;
			if (pair[..eq] == "code")
				return Uri.UnescapeDataString(pair[(eq + 1)..]);
		}
		return null;
	}

	/// <summary>Exchanges an authorization code for an access+refresh token pair and persists them. Called by
	/// <c>wa schwab login</c>.</summary>
	public static async Task ExchangeCodeAsync(SchwabConfig schwab, string code, string apiConfigPath, CancellationToken ct)
	{
		var form = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = code,
			["redirect_uri"] = schwab.RedirectUri,
		};
		await PostTokenAsync(schwab, form, isRefresh: false, ct);
		Persist(schwab, apiConfigPath);
	}

	/// <summary>Returns a valid bearer access token, refreshing it from the refresh token when the cached one is
	/// missing or within 60s of expiry. Persists any newly-minted token. Throws <see cref="SchwabAuthException"/>
	/// when no refresh token exists or the refresh fails (refresh token expired) so the caller can tell the user
	/// to re-run <c>wa schwab login</c>.</summary>
	public static async Task<string> GetAccessTokenAsync(SchwabConfig schwab, string apiConfigPath, CancellationToken ct)
	{
		if (!string.IsNullOrEmpty(schwab.AccessToken) && schwab.AccessTokenExpiresUtc is { } exp && exp - DateTime.UtcNow > TimeSpan.FromSeconds(60))
			return schwab.AccessToken!;

		if (string.IsNullOrEmpty(schwab.RefreshToken))
			throw new SchwabAuthException("No Schwab refresh token. Run `wa schwab login` first.");

		var form = new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["refresh_token"] = schwab.RefreshToken!,
		};
		try
		{
			await PostTokenAsync(schwab, form, isRefresh: true, ct);
		}
		catch (SchwabAuthException ex)
		{
			throw new SchwabAuthException($"Schwab token refresh failed ({ex.Message}). The 7-day refresh token has likely expired — run `wa schwab login`.", ex);
		}
		Persist(schwab, apiConfigPath);
		return schwab.AccessToken!;
	}

	/// <summary>Days remaining before the refresh token's hard 7-day expiry, or null if unknown.</summary>
	public static double? RefreshTokenDaysRemaining(SchwabConfig schwab) =>
		schwab.RefreshTokenIssuedUtc is { } issued ? (issued + RefreshTokenLifetime - DateTime.UtcNow).TotalDays : null;

	private static async Task PostTokenAsync(SchwabConfig schwab, Dictionary<string, string> form, bool isRefresh, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
		var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{schwab.ClientId}:{schwab.ClientSecret}"));
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
		request.Content = new FormUrlEncodedContent(form);

		using var response = await SchwabHttp.Client.SendAsync(request, ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		if (!response.IsSuccessStatusCode)
			throw new SchwabAuthException($"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");

		using var doc = JsonDocument.Parse(body);
		var root = doc.RootElement;
		var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
		if (string.IsNullOrEmpty(access))
			throw new SchwabAuthException($"token response missing access_token: {Truncate(body, 300)}");

		schwab.AccessToken = access;
		var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 1800;
		schwab.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);

		// Schwab returns the refresh token on both grants; it stays constant across refreshes within the 7-day
		// window. Re-store it (and reset the issued clock) only on the initial code exchange.
		if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
		{
			schwab.RefreshToken = refresh;
			if (!isRefresh) schwab.RefreshTokenIssuedUtc = DateTime.UtcNow;
		}
	}

	/// <summary>Rewrites only the <c>schwab</c> subtree of api-config.json, preserving every other field and the
	/// file's tab indentation (mirrors how `wa sniff` updates the headers subtree). Writes via a temp file + move
	/// so a crash mid-write can't truncate the config.</summary>
	private static void Persist(SchwabConfig schwab, string apiConfigPath)
	{
		var root = JsonNode.Parse(File.ReadAllText(apiConfigPath))!.AsObject();
		root["schwab"] = JsonSerializer.SerializeToNode(schwab);
		var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 });
		var tmp = apiConfigPath + ".tmp";
		File.WriteAllText(tmp, json);
		File.Move(tmp, apiConfigPath, overwrite: true);
	}

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

internal sealed class SchwabAuthException : Exception
{
	public SchwabAuthException(string message, Exception? inner = null) : base(message, inner) { }
}
