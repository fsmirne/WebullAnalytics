using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Sniff;

class SniffSettings : CommandSettings
{
	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Program.ApiConfigPath))) return ValidationResult.Error($"Config file '{Program.ApiConfigPath}' does not exist.");
		return ValidationResult.Success();
	}
}

class SniffCommand : AsyncCommand<SniffSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, SniffSettings settings, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
		if (config == null)
		{
			Console.WriteLine("Error: Failed to parse api-config.json.");
			return 1;
		}
		if (string.IsNullOrWhiteSpace(config.Pin))
		{
			Console.WriteLine("Error: 'pin' is required in api-config.json for header sniffing.");
			return 1;
		}

		var sniffConfig = Program.LoadAppConfig("sniff");
		var autoCloseBrowser = sniffConfig != null && sniffConfig.TryGetBool("autoCloseBrowser", out var acb) && acb;

		try
		{
			var headers = await HeaderSniffer.CaptureAsync(config.Pin, autoCloseBrowser, cancellation);
			Console.WriteLine($"Captured {headers.Count} header(s).");

			var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
			root["headers"] = JsonSerializer.SerializeToNode(headers);
			File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 }));

			Console.WriteLine($"Updated headers in {configPath}");
			return 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
		{
			Console.WriteLine($"Error: {ex.Message}");
			return 1;
		}
	}
}
