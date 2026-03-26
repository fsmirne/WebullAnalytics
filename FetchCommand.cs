using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;

namespace WebullAnalytics;

class FetchSettings : CommandSettings
{
	[Description("Path to the API config JSON file")]
	[CommandOption("--config")]
	[DefaultValue("data/api-config.json")]
	public string Config { get; set; } = "data/api-config.json";

	[Description("Output path for the JSONL orders file")]
	[CommandOption("--output")]
	[DefaultValue("data/orders.jsonl")]
	public string Output { get; set; } = "data/orders.jsonl";

	/// <summary>Applies config.json defaults for any option not explicitly passed on the CLI.</summary>
	internal void ApplyConfig(Dictionary<string, JsonElement> cfg)
	{
		if (!Program.HasCliOption("config") && cfg.TryGetString("config", out var config)) Config = config;
		if (!Program.HasCliOption("output") && cfg.TryGetString("output", out var output)) Output = output;
	}

	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Config))) return ValidationResult.Error($"Config file '{Config}' does not exist.");
		return ValidationResult.Success();
	}
}

class FetchCommand : AsyncCommand<FetchSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, FetchSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("fetch");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		var configPath = Program.ResolvePath(settings.Config);
		var outputPath = Program.ResolvePath(settings.Output);

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
