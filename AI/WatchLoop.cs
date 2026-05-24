using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI.Output;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal sealed class AIWatchSettings : AISingleTickerSubcommandSettings
{
	[CommandOption("--tick <SECONDS>")]
	[Description("Override tickIntervalSeconds.")]
	public int? Tick { get; set; }

	[CommandOption("--duration <DURATION>")]
	[Description("Stop after duration (e.g., 6h, 90m). Default: until market close.")]
	public string? Duration { get; set; }

	[CommandOption("--ignore-market-hours")]
	[Description("Run regardless of clock (for testing).")]
	public bool IgnoreMarketHours { get; set; }

	[CommandOption("--account <ALIAS>")]
	[Description("Account alias or ID from api-config.json. Mirrors `wa trade place --account`: overrides defaultAccount for this run. Affects both the live-position read and any auto-executed orders.")]
	public string? Account { get; set; }

	[CommandOption("--submit")]
	[Description("Override autoExecute.{management,opener}.submit=true for this run. Mirrors `wa trade place --submit`: keep config safe at dry-run, flip live from the CLI when ready.")]
	public bool Submit { get; set; }

	[CommandOption("--tif <VALUE>")]
	[Description("Override autoExecute.{management,opener}.timeInForce for this run. Mirrors `wa trade place --tif`: DAY (in-session only) or GTC (queues across sessions, accepted off-hours). Default: whatever config says, which itself defaults to DAY.")]
	public string? Tif { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Tick.HasValue && (Tick.Value < 1 || Tick.Value > 3600))
			return ValidationResult.Error($"--tick: must be in [1, 3600], got {Tick.Value}");
		if (Duration != null && !TryParseDuration(Duration, out _))
			return ValidationResult.Error($"--duration: must be like '6h' or '90m', got '{Duration}'");
		if (Tif != null && !string.Equals(Tif, "day", StringComparison.OrdinalIgnoreCase) && !string.Equals(Tif, "gtc", StringComparison.OrdinalIgnoreCase))
			return ValidationResult.Error($"--tif: must be 'day' or 'gtc', got '{Tif}'");

		return ValidationResult.Success();
	}

	internal static bool TryParseDuration(string s, out TimeSpan span)
	{
		span = default;
		if (string.IsNullOrWhiteSpace(s)) return false;
		var suffix = s[^1];
		var numPart = s[..^1];
		if (!int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
		span = suffix switch
		{
			'h' or 'H' => TimeSpan.FromHours(n),
			'm' or 'M' => TimeSpan.FromMinutes(n),
			's' or 'S' => TimeSpan.FromSeconds(n),
			_ => TimeSpan.Zero
		};
		return span != TimeSpan.Zero;
	}
}

internal sealed class AIWatchCommand : AsyncCommand<AIWatchSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIWatchSettings settings, CancellationToken cancellation)
		=> await AITextOutput.RunAsync(settings, "AIWatch", async () =>
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;
		if (settings.Submit) { config.AutoExecute.Management.Submit = true; config.AutoExecute.Opener.Submit = true; }
		if (settings.Tif != null) { config.AutoExecute.Management.TimeInForce = settings.Tif.ToUpperInvariant(); config.AutoExecute.Opener.TimeInForce = settings.Tif.ToUpperInvariant(); }

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var tickSeconds = settings.Tick ?? config.TickIntervalSeconds;
		var stopAt = ComputeStopTime(settings);

		var positions = AIContext.BuildLivePositionSource(config, settings.Account);
		var quotes = AIContext.BuildLiveQuoteSource(config);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config, settings.Pricing), config);
		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);

		// Auto-executors: turn proposals into real (or dry-run) order submissions. Both off by default;
		// both honor enabled/submit gates independently. Shared with `wa ai scan` via AIContext.BuildAutoExecutors.
		var (autoExecutor, openerExecutor) = AIContext.BuildAutoExecutors(config, settings.Account);

		var priceCache = new Replay.HistoricalPriceCache();

		using var sink = new ProposalSink(config.Log, mode: "watch", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
		OpenProposalSink? openSink = null;
		OpenCandidateEvaluator? openEvaluator = null;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			openSink = new OpenProposalSink(config.Log, mode: "watch", suggestPricing: settings.Pricing, ascii: settings.UseTextOutput);
			openEvaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache);
		}

		AnsiConsole.MarkupLine($"[bold]ai watch[/] tickers={string.Join(",", config.Tickers)} tick={tickSeconds}s stopAt={stopAt:HH:mm:ss}");

		var failures = 0;
		var ticksRun = 0;
		var proposalsEmitted = 0;

		while (!cancellation.IsCancellationRequested && DateTime.Now < stopAt)
		{
			if (!settings.IgnoreMarketHours && !IsMarketOpen())
			{
				var sleep = TimeSpan.FromSeconds(Math.Min(tickSeconds * 5, 300));
				try { await Task.Delay(sleep, cancellation); } catch (OperationCanceledException) { break; }
				continue;
			}

			try
			{
				var now = DateTime.Now;
				var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
				var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);
				var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, quotes, config, cancellation);
				var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, priceCache, config.Indicators.TechnicalFilter, now, cancellation);

				var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
				var results = evaluator.Evaluate(ctx);
				if (settings.EmitManagementProposals)
					foreach (var r in results) { sink.Emit(r.Proposal, r.IsRepeat); proposalsEmitted++; }

				if (autoExecutor != null)
					await autoExecutor.HandleAsync(results, ctx, cancellation);

				if (openEvaluator != null && openSink != null)
				{
					var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
					for (var i = 0; i < openResults.Count; i++) { openSink.Emit(openResults[i], rank: i + 1); proposalsEmitted++; }
					if (openerExecutor != null)
						await openerExecutor.HandleAsync(openResults, now, cancellation);
				}

				ticksRun++;
				failures = 0;
			}
			catch (OperationCanceledException) { break; }
			catch (UnauthorizedAccessException ex)
			{
				Console.Error.WriteLine($"Auth failure: {ex.Message}. Exiting.");
				return 2;
			}
			catch (Exception ex)
			{
				failures++;
				AnsiConsole.MarkupLine($"[red]Tick {ticksRun + 1} failed ({failures}/5): {Markup.Escape(ex.Message)}[/]");
				if (failures >= 5)
				{
					Console.Error.WriteLine("Circuit breaker: 5 consecutive tick failures. Exiting.");
					return 3;
				}
			}

			try { await Task.Delay(TimeSpan.FromSeconds(tickSeconds), cancellation); } catch (OperationCanceledException) { break; }
		}

		openSink?.Dispose();
		AnsiConsole.MarkupLine($"[dim]Loop exited. ticks={ticksRun} proposals={proposalsEmitted} failures={failures}[/]");
		return 0;
	});

	// NYSE regular session, hardcoded. The watch loop is the only consumer; every other code path
	// reuses MarketCalendar.IsOpen for day-of-week / holiday handling. If we ever need to support a
	// different exchange, lift these constants — but the rest of the app assumes US equity/options too.
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan MarketOpenEt = new(9, 30, 0);
	private static readonly TimeSpan MarketCloseEt = new(16, 0, 0);

	private static DateTime ComputeStopTime(AIWatchSettings s)
	{
		if (s.Duration != null && AIWatchSettings.TryParseDuration(s.Duration, out var span))
			return DateTime.Now + span;
		// Default: today's market close in ET.
		var nowLocal = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		var closeLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, MarketCloseEt.Hours, MarketCloseEt.Minutes, 0, DateTimeKind.Unspecified);
		return TimeZoneInfo.ConvertTimeToUtc(closeLocal, NyTz).ToLocalTime();
	}

	private static bool IsMarketOpen()
	{
		var nowLocal = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		if (!MarketCalendar.IsOpen(nowLocal.Date)) return false;
		var t = nowLocal.TimeOfDay;
		return t >= MarketOpenEt && t <= MarketCloseEt;
	}
}
