using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.Api;

namespace WebullAnalytics.Trading;

internal sealed class TradeAccount
{
	[JsonPropertyName("alias")] public string Alias { get; set; } = "";
	[JsonPropertyName("accountId")] public string AccountId { get; set; } = "";
	[JsonPropertyName("appKey")] public string AppKey { get; set; } = "";
	[JsonPropertyName("appSecret")] public string AppSecret { get; set; } = "";
	[JsonPropertyName("appId")] public string? AppId { get; set; }
	[JsonPropertyName("sandbox")] public bool Sandbox { get; set; } = true;

	public string BaseUrl => Sandbox
		? "https://us-openapi-alb.uat.webullbroker.com"
		: "https://api.webull.com";
}

internal static class TradeConfig
{
	/// <summary>Loads api-config.json and returns it for callers that need account lookup. Returns null
	/// (with stderr message) if the file is missing, malformed, or has no accounts configured.</summary>
	internal static ApiConfig? Load(bool quiet = false)
	{
		var path = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(path))
		{
			if (!quiet)
			{
				Console.Error.WriteLine($"Error: api config not found at '{Program.ApiConfigPath}'.");
				Console.Error.WriteLine($"  Run: cp api-config.example.json {Program.ApiConfigPath} and edit.");
			}
			return null;
		}
		try
		{
			var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(path));
			if (config == null || config.Accounts.Count == 0)
			{
				if (!quiet)
					Console.Error.WriteLine("Error: api-config.json must contain at least one entry under 'accounts'.");
				return null;
			}
			return config;
		}
		catch (JsonException ex)
		{
			if (!quiet)
				Console.Error.WriteLine($"Error: failed to parse api-config.json: {ex.Message}");
			return null;
		}
	}

	/// <summary>Resolves the account to use given the --account flag (which may be null/empty).
	/// Returns null (with stderr message) if resolution fails.</summary>
	internal static TradeAccount? Resolve(ApiConfig config, string? accountFlag, bool quiet = false)
	{
		var key = string.IsNullOrWhiteSpace(accountFlag) ? config.DefaultAccount : accountFlag;
		if (string.IsNullOrWhiteSpace(key))
		{
			if (!quiet)
				Console.Error.WriteLine("Error: no --account flag and no 'defaultAccount' in api-config.json.");
			return null;
		}
		var match = config.Accounts.FirstOrDefault(a =>
			string.Equals(a.Alias, key, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(a.AccountId, key, StringComparison.OrdinalIgnoreCase));
		if (match == null)
		{
			if (!quiet)
			{
				var aliases = string.Join(", ", config.Accounts.Select(a => a.Alias));
				Console.Error.WriteLine($"Error: account '{key}' not found. Valid aliases: {aliases}");
			}
			return null;
		}
		return match;
	}
}
