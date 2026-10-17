using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.Api;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// <c>wa analyze top</c> — Webull's market-wide "Top Options" leaderboard: the most active option contracts across all
/// US underlyings, ranked by volume (default), turnover, open interest, OI change, or implied volatility.
/// </summary>
internal sealed class AnalyzeTopSettings : CommandSettings
{
	[CommandOption("--by <RANK>")]
	[DefaultValue("volume")]
	[Description("Ranking: volume (default), turnover, oi, oi-up, oi-down, or iv.")]
	public string By { get; set; } = "volume";

	[CommandOption("--count <N>")]
	[DefaultValue(50)]
	[Description("Number of contracts to list (1-500). Default 50.")]
	public int Count { get; set; } = 50;

	public override ValidationResult Validate()
	{
		if (!WebullOptionRankClient.RankTypes.ContainsKey(By)) return ValidationResult.Error($"--by: expected one of {string.Join(", ", WebullOptionRankClient.RankTypes.Keys)}, got '{By}'");
		if (Count is < 1 or > 500) return ValidationResult.Error($"--count: must be in [1, 500], got {Count}");
		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeTopCommand : AsyncCommand<AnalyzeTopSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeTopSettings settings, CancellationToken cancellation)
	{
		TerminalHelper.EnsureTerminalWidthFromConfig();

		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		var apiConfig = File.Exists(configPath) ? JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath)) : null;
		if (apiConfig == null || !apiConfig.Webull.Headers.TryGetValue("access_token", out var accessToken) || string.IsNullOrEmpty(accessToken))
		{
			AnsiConsole.MarkupLine("[red]Error: no Webull access_token in api-config.json. Run 'wa sniff' first.[/]");
			return 1;
		}

		OptionRankPage page;
		try
		{
			page = await WebullOptionRankClient.FetchAsync(accessToken, WebullOptionRankClient.RankTypes[settings.By], settings.Count, cancellation);
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Failed to fetch Webull's option leaderboard:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}

		// Without OPRA permission Webull serves the anonymous (non-real-time) feed; refuse rather than show it.
		if (!page.HasOpraPermission)
		{
			AnsiConsole.MarkupLine("[red]Error: Webull reported hasOpraPermission=false — the sniffed session is expired or invalid, so the leaderboard would not be real-time. Run 'wa sniff' to refresh it.[/]");
			return 1;
		}

		var asOf = page.UpdatedAt is { } ts ? TimeZoneInfo.ConvertTime(ts, NyTz).ToString("yyyy-MM-dd HH:mm 'ET'", CultureInfo.InvariantCulture) : "unknown";
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title($"[bold]Top options by {Markup.Escape(settings.By.ToLowerInvariant())}[/]  [dim]as of {asOf}[/]");
		table.AddColumn(new TableColumn("#").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Underlying").NoWrap());
		table.AddColumn(new TableColumn("Expiry").NoWrap());
		table.AddColumn(new TableColumn("Strike").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("C/P").NoWrap());
		table.AddColumn(new TableColumn("Last").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Chg").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Mid").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Volume").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("OI").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("OI Δ").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Vol/OI").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("IV").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Turnover").RightAligned().NoWrap());

		var rank = 0;
		foreach (var r in page.Rows)
		{
			rank++;
			table.AddRow(
				rank.ToString(CultureInfo.InvariantCulture),
				$"[bold]{Markup.Escape(r.Underlying)}[/]",
				r.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				r.Strike.ToString("0.##", CultureInfo.InvariantCulture),
				r.Right == "C" ? "[green]C[/]" : "[red]P[/]",
				FormatPrice(r.Last),
				FormatChange(r.ChangeRatio),
				FormatPrice(r.Mid),
				r.Volume.ToString("N0", CultureInfo.InvariantCulture),
				r.OpenInterest.ToString("N0", CultureInfo.InvariantCulture),
				r.OiChange.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture),
				r.OpenInterest > 0 ? ((decimal)r.Volume / r.OpenInterest).ToString("F2", CultureInfo.InvariantCulture) : "[dim]—[/]",
				r.ImpliedVol is { } iv ? (iv * 100m).ToString("F1", CultureInfo.InvariantCulture) + "%" : "[dim]—[/]",
				r.Turnover is { } t ? "$" + t.ToString("N0", CultureInfo.InvariantCulture) : "[dim]—[/]");
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Source: Webull Top Options (all US underlyings). Unexpired contracts only — after the close, that session's expiring contracts are already gone.[/]");
		return 0;
	}

	private static string FormatPrice(decimal? price) => price is { } p ? p.ToString("F2", CultureInfo.InvariantCulture) : "[dim]—[/]";

	private static string FormatChange(decimal? ratio) => ratio is { } c ? $"[{(c >= 0m ? "green" : "red")}]{(c * 100m).ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%[/]" : "[dim]—[/]";
}
