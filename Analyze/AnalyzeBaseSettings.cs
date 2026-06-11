using Spectre.Console.Cli;
using Spectre.Console;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Lean base for analyze subcommands that don't pipe through the full report renderer.
/// <c>analyze risk</c>, <c>analyze position</c>, and <c>analyze roll</c> inherit from this; they
/// only need a small slice of the broader report options. <c>analyze trade</c> is the exception —
/// it injects synthetic trades into the report pipeline and inherits <see cref="AnalyzeSubcommandSettings"/>
/// (which extends <see cref="WebullAnalytics.Report.ReportSettings"/>) instead.
///
/// Subcommands that need additional report options (e.g., <c>--range</c>/<c>--view</c>/<c>--levels</c>
/// for the grid in <c>analyze roll</c>) declare them locally rather than inheriting the entire
/// report option set.
/// </summary>
internal abstract class AnalyzeBaseSettings : CommandSettings
{
	[CommandOption("--date")]
	[Description("Override 'today' for evaluation. Simulates running on a different date (e.g., after short leg expiration). Format: YYYY-MM-DD")]
	public string? Date { get; set; }

	[CommandOption("--spot")]
	[Description("Override underlying spot price(s). Format: TICKER:PRICE (e.g., GME:24.88). Comma-separated for multiple tickers (e.g., GME:24.88,SPY:580.50)")]
	public string? Spot { get; set; }

	[CommandOption("--iv")]
	[Description("Override implied volatility per option leg. Format: SYMBOL:IV% (e.g., GME260213C00025000:50). Comma-separated for multiple legs.")]
	public string? IvOverrides { get; set; }

	[CommandOption("--output")]
	[DefaultValue("console")]
	[Description("Output format: 'console' (default) or 'text' (writes to a default .txt file when --output-path is omitted)")]
	public string OutputFormat { get; set; } = "console";

	[CommandOption("--output-path")]
	[Description("Path for output file (used with --output text)")]
	public string? OutputPath { get; set; }

	internal DateTime? EvaluationDateOverride =>
		Date != null ? DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

	/// <summary>Reads the same `report` config block as <see cref="WebullAnalytics.Report.ReportSettings.ApplyConfig"/>,
	/// but only applies keys whose corresponding options live on this lean base. Unrecognized
	/// report-only keys (e.g., <c>source</c>, <c>since</c>, <c>view</c>) are ignored without warning
	/// — they belong to the report renderer, which these subcommands don't invoke.</summary>
	internal virtual void ApplyConfig(Dictionary<string, JsonElement> cfg)
	{
		if (!Program.HasCliOption("output") && cfg.TryGetString("output", out var output)) OutputFormat = output;
		if (!Program.HasCliOption("output-path") && cfg.TryGetString("outputPath", out var outputPath)) OutputPath = outputPath;
		if (!Program.HasCliOption("iv") && cfg.TryGetString("iv", out var iv)) IvOverrides = iv;
		if (!Program.HasCliOption("spot") && cfg.TryGetString("spot", out var spot)) Spot = spot;
	}

	public override ValidationResult Validate()
	{
		if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--date: expected format YYYY-MM-DD, got '{Date}'");

		var format = OutputFormat.ToLowerInvariant();
		if (format is not ("console" or "text"))
			return ValidationResult.Error("--output must be 'console' or 'text'");

		if (Spot != null)
		{
			foreach (var pair in Spot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2 || !decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
					return ValidationResult.Error($"--spot: invalid entry '{pair}'. Expected format: TICKER:PRICE (e.g., GME:24.88)");
			}
		}

		return ValidationResult.Success();
	}
}
