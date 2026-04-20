using System.Security.Cryptography;
using System.Text;

namespace WebullAnalytics;

/// <summary>
/// Builds the seven x-* headers required by the Webull OpenAPI.
/// Pure: given the same inputs (including the injected timestamp and nonce), produces the same outputs.
/// </summary>
internal static class OpenApiSigner
{
	/// <summary>
	/// Canonical signing flow:
	///   1. str1 = all query params + signing headers (minus x-signature, x-version) sorted alphabetically,
	///      joined as key1=val1&key2=val2&...
	///   2. str2 = uppercase MD5 of the compact JSON body (if a body is present).
	///   3. str3 = path & str1              (no body)
	///            path & str1 & str2        (with body)
	///   4. encoded = Uri.EscapeDataString(str3)
	///   5. signature = base64(HMAC-SHA1(appSecret + "&", encoded))
	/// The App Secret is NEVER transmitted in any header.
	/// </summary>
	internal static Dictionary<string, string> SignRequest(
		string appKey,
		string appSecret,
		string host,
		string path,
		IReadOnlyDictionary<string, string> queryParams,
		string? jsonBody,
		string? appId = null)
		=> SignRequest(appKey, appSecret, host, path, queryParams, jsonBody, DateTime.UtcNow, Guid.NewGuid().ToString("N"), appId);

	/// <summary>Test-friendly overload: timestamp and nonce injectable for deterministic output.</summary>
	internal static Dictionary<string, string> SignRequest(
		string appKey,
		string appSecret,
		string host,
		string path,
		IReadOnlyDictionary<string, string> queryParams,
		string? jsonBody,
		DateTime timestampUtc,
		string nonce,
		string? appId = null)
	{
		var timestamp = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");

		// Signing headers (exclude x-signature and x-version per spec).
		var signingHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["x-app-key"] = appKey,
			["x-timestamp"] = timestamp,
			["x-signature-algorithm"] = "HMAC-SHA1",
			["x-signature-nonce"] = nonce,
			["x-signature-version"] = "1.0",
			["host"] = host,
		};
		if (!string.IsNullOrEmpty(appId))
			signingHeaders["x-app-id"] = appId;

		// Merge query + headers, sort alphabetically, join.
		var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (k, v) in queryParams) merged[k] = v;
		foreach (var (k, v) in signingHeaders) merged[k] = v;
		var str1 = string.Join("&", merged.Select(kv => $"{kv.Key}={kv.Value}"));

		var str3 = string.IsNullOrEmpty(jsonBody)
			? $"{path}&{str1}"
			: $"{path}&{str1}&{UppercaseMd5(jsonBody!)}";

		var encoded = Uri.EscapeDataString(str3);

		var signingKey = Encoding.UTF8.GetBytes(appSecret + "&");
		using var hmac = new HMACSHA1(signingKey);
		var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(encoded)));

		var result = new Dictionary<string, string>
		{
			["x-app-key"] = appKey,
			["x-timestamp"] = timestamp,
			["x-signature"] = signature,
			["x-signature-algorithm"] = "HMAC-SHA1",
			["x-signature-version"] = "1.0",
			["x-signature-nonce"] = nonce,
			["x-version"] = "v2",
		};
		if (!string.IsNullOrEmpty(appId))
			result["x-app-id"] = appId;
		return result;
	}

	private static string UppercaseMd5(string input)
	{
		using var md5 = MD5.Create();
		var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
		var sb = new StringBuilder(hash.Length * 2);
		foreach (var b in hash) sb.Append(b.ToString("X2"));
		return sb.ToString();
	}
}
