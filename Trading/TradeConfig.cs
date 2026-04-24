using System.Text.Json;
using System.Text.Json.Serialization;

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

internal sealed class TradeConfigFile
{
	[JsonPropertyName("defaultAccount")] public string? DefaultAccount { get; set; }
	[JsonPropertyName("accounts")] public List<TradeAccount> Accounts { get; set; } = new();
}

internal static class TradeConfig
{
	internal const string ConfigPath = "data/trade-config.json";

	/// <summary>Loads and parses the trade-config.json file. Returns null (with stderr message) if the file is missing or malformed.</summary>
	internal static TradeConfigFile? Load()
	{
		var path = Program.ResolvePath(ConfigPath);
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"Error: trade config not found at '{ConfigPath}'.");
			Console.Error.WriteLine($"  Run: cp trade-config.example.json {ConfigPath} and edit.");
			return null;
		}
		try
		{
			var config = JsonSerializer.Deserialize<TradeConfigFile>(File.ReadAllText(path));
			if (config == null || config.Accounts.Count == 0)
			{
				Console.Error.WriteLine("Error: trade-config.json must contain at least one account.");
				return null;
			}
			return config;
		}
		catch (JsonException ex)
		{
			Console.Error.WriteLine($"Error: failed to parse trade-config.json: {ex.Message}");
			return null;
		}
	}

	/// <summary>Resolves the account to use given the --account flag (which may be null/empty).
	/// Returns null (with stderr message) if resolution fails.</summary>
	internal static TradeAccount? Resolve(TradeConfigFile config, string? accountFlag)
	{
		var key = string.IsNullOrWhiteSpace(accountFlag) ? config.DefaultAccount : accountFlag;
		if (string.IsNullOrWhiteSpace(key))
		{
			Console.Error.WriteLine("Error: no --account flag and no 'defaultAccount' in trade-config.json.");
			return null;
		}
		var match = config.Accounts.FirstOrDefault(a =>
			string.Equals(a.Alias, key, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(a.AccountId, key, StringComparison.OrdinalIgnoreCase));
		if (match == null)
		{
			var aliases = string.Join(", ", config.Accounts.Select(a => a.Alias));
			Console.Error.WriteLine($"Error: account '{key}' not found. Valid aliases: {aliases}");
			return null;
		}
		return match;
	}
}
