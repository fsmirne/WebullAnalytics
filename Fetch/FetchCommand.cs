using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.Fetch;

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
	protected override async Task<int> ExecuteAsync(CommandContext context, FetchSettings settings, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		var outputPath = Program.ResolvePath(Program.OrdersPath);

		var config = LoadApiConfig(configPath);
		if (config == null) return 1;

		Console.WriteLine($"Resolving {config.Webull.Tickers.Length} ticker symbol(s) to Webull IDs...");
		var resolved = await WebullOptionsClient.ResolveTickerIdsAsync(config.Webull.Tickers, cancellation);
		if (resolved.Count == 0)
		{
			Console.WriteLine("Error: Could not resolve any ticker symbols.");
			return 1;
		}

		// Broker-posted cash truth for the report's money columns. Runs BEFORE the orders pull: the two
		// artifacts are independent, and this endpoint tolerates an older session (it drops the per-URL
		// signature headers) while orderList can 403 first. Non-fatal: a stale cashrecord.jsonl only means
		// report falls back to computed cash for rows it doesn't cover.
		var cashRecordPath = Program.ResolvePath(Program.CashRecordPath);
		try
		{
			var rows = await ApiClient.FetchCashRecordToJsonl(config, cashRecordPath, cancellation);
			Console.WriteLine($"Written {rows} cash-record row(s) to {cashRecordPath}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Warning: cash-record fetch failed ({ex.Message}); report will use computed cash for rows not in the existing {Path.GetFileName(cashRecordPath)}.");
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
		if (config == null || config.Webull.Tickers.Length == 0)
		{
			Console.WriteLine("Error: Config file must contain 'tickers'.");
			return null;
		}
		return config;
	}
}
