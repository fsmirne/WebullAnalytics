using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.Api;

/// <summary>
/// Persists Webull OpenAPI access tokens to disk, keyed by account alias.
/// One file per installation (data/webull-tokens.json), with an entry per account.
/// </summary>
internal static class TokenStore
{
	internal const string StorePath = "data/webull-tokens.json";

	internal sealed class TokenEntry
	{
		[JsonPropertyName("token")] public string Token { get; set; } = "";
		[JsonPropertyName("expires")] public long Expires { get; set; }
		[JsonPropertyName("status")] public string Status { get; set; } = "";
	}

	/// <summary>Returns the stored token for the given account alias, or null if none cached.</summary>
	internal static TokenEntry? Load(string accountAlias)
	{
		var all = LoadAll();
		return all.TryGetValue(accountAlias, out var entry) ? entry : null;
	}

	/// <summary>Stores/updates the token for the given account alias.</summary>
	internal static void Save(string accountAlias, string token, long expires, string status)
	{
		var all = LoadAll();
		all[accountAlias] = new TokenEntry { Token = token, Expires = expires, Status = status };
		var path = Program.ResolvePath(StorePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static Dictionary<string, TokenEntry> LoadAll()
	{
		var path = Program.ResolvePath(StorePath);
		if (!File.Exists(path)) return new Dictionary<string, TokenEntry>();
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, TokenEntry>>(File.ReadAllText(path))
				?? new Dictionary<string, TokenEntry>();
		}
		catch (JsonException)
		{
			return new Dictionary<string, TokenEntry>();
		}
	}
}
