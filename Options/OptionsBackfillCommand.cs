using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using WebullAnalytics.AI;

namespace WebullAnalytics.Options;

/// <summary>`wa options backfill &lt;ticker&gt;` — pulls per-minute bars for OCCs that appear in the
/// discovery log (default) or the full Webull registry (--all). Routes each OCC to Webull (live
/// contracts; fast, unrestricted) or massive.com (expired contracts; rate-limited to 5 req/min).
/// Writes one CSV per contract under <c>data/options/&lt;root&gt;/&lt;expiry&gt;/&lt;OCC&gt;.csv</c>.
/// Merge-by-timestamp on re-runs picks up new minutes without losing existing ones; --force re-
/// fetches from scratch.</summary>
internal sealed class OptionsBackfillSettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Ticker root to backfill (e.g. SPXW). The discovery log at `data/options-discovery/<TICKER>.jsonl` is the default candidate list; with --all, falls back to the full Webull registry.")]
	public string Ticker { get; set; } = "";

	[CommandOption("--force")]
	[Description("Drop any existing CSV and refetch from scratch. Default (without --force) is to merge by timestamp — re-runs pick up new minutes without losing existing ones.")]
	public bool Force { get; set; }

	[CommandOption("--all")]
	[Description("Backfill every registry entry matching the ticker (full chain — typically tens of thousands of strikes including illiquid wings the bot never touched). Without this flag, the backfill defaults to contracts that appear in `ai-proposals.jsonl`, `orders.jsonl`, or `data/options-discovery/<TICKER>.jsonl`.")]
	public bool All { get; set; }

	[CommandOption("--since <DATE>")]
	[Description("Only backfill contracts whose expiry is on or after this date (YYYY-MM-DD). Bounds the work to a window of interest — e.g. --since 2026-01-01 to focus on the YTD sweep window instead of the full multi-year catalog.")]
	public string? Since { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("ticker is required");
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		return ValidationResult.Success();
	}
}

internal sealed class OptionsBackfillCommand : AsyncCommand<OptionsBackfillSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, OptionsBackfillSettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker.ToUpperInvariant();
		DateTime? since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: null;
		return await AIHistoryOptionsBackfill.RunAsync(ticker, settings.Force, settings.All, since, cancellation);
	}
}
