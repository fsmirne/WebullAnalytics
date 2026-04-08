using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;

namespace WebullAnalytics;

class FetchSettings : CommandSettings
{
	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Program.ApiConfigPath))) return ValidationResult.Error($"Config file '{Program.ApiConfigPath}' does not exist.");
		return ValidationResult.Success();
	}
}

class FetchCommand : AsyncCommand<FetchSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, FetchSettings settings, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		var outputPath = Program.ResolvePath(Program.OrdersPath);

		var config = LoadApiConfig(configPath);
		if (config == null) return 1;

		Console.WriteLine($"Resolving {config.Tickers.Length} ticker symbol(s) to Webull IDs...");
		var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(config.Tickers, cancellation);
		if (resolved.Count == 0)
		{
			Console.WriteLine("Error: Could not resolve any ticker symbols.");
			return 1;
		}

		Console.WriteLine($"Fetching orders for {resolved.Count} ticker(s)...");
		await ApiClient.FetchOrdersToJsonl(config, resolved.Values.ToArray(), outputPath);
		Console.WriteLine($"Written to {outputPath}");
		return 0;
	}

	internal static ApiConfig? LoadApiConfig(string path)
	{
		var json = File.ReadAllText(path);
		var config = JsonSerializer.Deserialize<ApiConfig>(json);
		if (config == null || config.Tickers.Length == 0)
		{
			Console.WriteLine("Error: Config file must contain 'tickers'.");
			return null;
		}
		return config;
	}
}
