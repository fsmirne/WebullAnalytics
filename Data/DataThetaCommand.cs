using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data;

internal sealed class DataThetaSettings : CommandSettings
{
	[CommandArgument(0, "<occ>")]
	[Description("OCC option symbol, e.g. SPY261021C00670000.")]
	public string Occ { get; set; } = "";

	[CommandOption("--date <yyyy-MM-dd>")]
	[Description("Session date for --at / --from. Default: today ET.")]
	public string? Date { get; set; }

	[CommandOption("--at <HH:mm>")]
	[Description("Last NBBO at this time ET (option_at_time_quote).")]
	public string? At { get; set; }

	[CommandOption("--from <HH:mm>")]
	[Description("Minute NBBO from this time ET (option_history_quote, 1m); needs --to.")]
	public string? From { get; set; }

	[CommandOption("--to <HH:mm>")]
	[Description("End time ET for --from.")]
	public string? To { get; set; }

	public override ValidationResult Validate()
	{
		if (OptionContract.FromOcc(Occ) == null) return ValidationResult.Error($"not an OCC option symbol: {Occ}");
		if ((From == null) != (To == null)) return ValidationResult.Error("--from and --to go together");
		if (At != null && From != null) return ValidationResult.Error("pass --at or --from/--to, not both");
		return ValidationResult.Success();
	}
}

/// <summary>`wa data theta` (hidden): one raw ThetaData call through the C# client, printed as CSV with the server's columns —
/// the latest NBBO by default, the NBBO at a time with --at, or minute NBBO with --from/--to. The debugging and parity tool for
/// the unsupported direct client: its output lines up column-for-column with the Python library's DataFrame.</summary>
internal sealed class DataThetaCommand : AsyncCommand<DataThetaSettings>
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	protected override async Task<int> ExecuteAsync(CommandContext context, DataThetaSettings settings, CancellationToken cancellation)
	{
		var contract = OptionContract.FromOcc(settings.Occ)!;
		var date = settings.Date != null ? DateOnly.ParseExact(settings.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, NyTz));
		try
		{
			using var client = await ThetaDataClient.ConnectAsync(ThetaDataClient.DefaultCredsPath(), maxConcurrent: 1, stallTimeout: TimeSpan.FromMinutes(3), cancellation);
			var table = await ThetaTable.CollectAsync(settings.At != null ? client.OptionAtTimeQuote(contract, date, date, ParseTime(settings.At), cancellation)
				: settings.From != null ? client.OptionHistoryQuote(contract, "1m", date, date, cancellation, TimeText(settings.From), TimeText(settings.To!))
				: client.OptionSnapshotQuote(contract, cancellation));
			if (table.RowCount == 0)
			{
				AnsiConsole.MarkupLine("[yellow]no data[/] (ThetaData NOT_FOUND)");
				return 0;
			}
			Console.WriteLine(string.Join(',', table.Headers));
			for (var row = 0; row < table.RowCount; row++)
				Console.WriteLine(string.Join(',', Enumerable.Range(0, table.Headers.Count).Select(column => Format(table.Value(row, column)))));
			return 0;
		}
		catch (Exception ex) when (ex is ThetaSessionException or ThetaStallException)
		{
			AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
			return 1;
		}
	}

	private static TimeOnly ParseTime(string text) => TimeOnly.ParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture);
	private static string TimeText(string text) => ParseTime(text).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

	// Round-trip formats so a value can be compared bit-for-bit against the Python library's.
	private static string Format(object? value) => value switch
	{
		null => "",
		double d => d.ToString("R", CultureInfo.InvariantCulture),
		DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
		IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
		_ => value.ToString() ?? ""
	};
}
