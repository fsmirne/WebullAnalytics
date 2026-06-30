using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.Ledger;

internal sealed class LedgerSettings : CommandSettings
{
	[CommandOption("-n|--count <COUNT>")]
	[Description("Number of most-recent cash-record entries to pull (1-200, default 50).")]
	[DefaultValue(50)]
	public int Count { get; set; } = 50;

	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Program.ApiConfigPath))) return ValidationResult.Error($"Config file '{Program.ApiConfigPath}' does not exist.");
		if (Count is < 1 or > 200) return ValidationResult.Error("--count must be between 1 and 200.");
		return ValidationResult.Success();
	}
}

/// <summary>`wa ledger` — pulls the Webull cash-record activity ledger (the running-balance feed the
/// platform shows) on demand. Uses the same scraped session as `wa fetch`/`wa sniff`; refresh it with
/// `wa sniff` if the call 401s. This is broker truth for cash, distinct from `wa report`'s computed cash
/// (which models ITM expiry settlements immediately, while Webull posts them ~T+1).</summary>
internal sealed class LedgerCommand : AsyncCommand<LedgerSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, LedgerSettings settings, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);

		ApiConfig? config;
		try { config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath)); }
		catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Failed to read {Markup.Escape(configPath)}:[/] {Markup.Escape(ex.Message)}"); return 1; }

		if (config == null || string.IsNullOrEmpty(config.Webull.SecAccountId) || config.Webull.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("[red]Webull session not configured[/] (missing secAccountId or scraped headers). Run [yellow]wa sniff[/] first.");
			return 1;
		}

		ApiClient.FundActivitiesResult result;
		try
		{
			result = await ApiClient.FetchFundActivitiesAsync(config, settings.Count, cancellation);
		}
		catch (System.Net.Http.HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Ledger fetch failed:[/] {Markup.Escape(ex.Message)}  [dim](session may be stale — run [yellow]wa sniff[/])[/]");
			return 3;
		}

		if (result.Items.Count == 0)
		{
			AnsiConsole.MarkupLine("[dim]No cash-record activity returned.[/]");
			return 0;
		}

		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("Time (ET)");
		table.AddColumn("Type");
		table.AddColumn("Description");
		table.AddColumn(new TableColumn("Amount").RightAligned());
		table.AddColumn(new TableColumn("Running").RightAligned());

		foreach (var it in result.Items)
		{
			var amtColor = it.Amount < 0 ? "red" : "green";
			var type = string.IsNullOrEmpty(it.Name) ? it.Category : it.Name;
			table.AddRow(
				Markup.Escape(it.OccurredTime),
				Markup.Escape(type),
				Markup.Escape(it.Description),
				$"[{amtColor}]{it.Amount:+0.00;-0.00;0.00}[/]",
				$"{it.RunningTotal:N2}");
		}

		if (!string.IsNullOrEmpty(result.UpdateTime))
			AnsiConsole.MarkupLine($"[dim]Webull cash record — updated {Markup.Escape(result.UpdateTime!)}[/]");
		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine($"[bold]Current balance: {result.Items[0].RunningTotal:N2}[/]  [dim](most recent of {result.Items.Count} entries)[/]");
		return 0;
	}
}
