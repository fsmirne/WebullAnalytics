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

		Console.WriteLine($"Fetching orders for {config.TickerIds.Length} ticker(s)...");
		await ApiClient.FetchOrdersToJsonl(config, outputPath);
		Console.WriteLine($"Written to {outputPath}");
		return 0;
	}

	internal static ApiConfig? LoadApiConfig(string path)
	{
		var json = File.ReadAllText(path);
		var config = JsonSerializer.Deserialize<ApiConfig>(json);
		if (config == null || config.TickerIds.Length == 0)
		{
			Console.WriteLine("Error: Config file must contain tickerIds.");
			return null;
		}
		return config;
	}
}
