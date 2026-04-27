using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal sealed class AIWatchSettings : AISubcommandSettings
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

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Tick.HasValue && (Tick.Value < 1 || Tick.Value > 3600))
			return ValidationResult.Error($"--tick: must be in [1, 3600], got {Tick.Value}");
		if (Duration != null && !TryParseDuration(Duration, out _))
			return ValidationResult.Error($"--duration: must be like '6h' or '90m', got '{Duration}'");

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
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var tickSeconds = settings.Tick ?? config.TickIntervalSeconds;
		var stopAt = ComputeStopTime(settings, config);

		var positions = AIContext.BuildLivePositionSource(config);
		var quotes = AIContext.BuildLiveQuoteSource(config);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config), config);
		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);

		using var sink = new ProposalSink(config.Log, mode: "watch");
		OpenProposalSink? openSink = null;
		OpenCandidateEvaluator? openEvaluator = null;
		if (config.Opener.Enabled && settings.EmitOpenProposals)
		{
			openSink = new OpenProposalSink(config.Log, mode: "watch");
			openEvaluator = new OpenCandidateEvaluator(config, quotes);
		}
		var priceCache = new Replay.HistoricalPriceCache();

		AnsiConsole.MarkupLine($"[bold]ai watch[/] tickers={string.Join(",", config.Tickers)} tick={tickSeconds}s stopAt={stopAt:HH:mm:ss}");

		var failures = 0;
		var ticksRun = 0;
		var proposalsEmitted = 0;

		while (!cancellation.IsCancellationRequested && DateTime.Now < stopAt)
		{
			if (!settings.IgnoreMarketHours && !IsMarketOpen(config.MarketHours))
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
				var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, priceCache, config.Rules.OpportunisticRoll.TechnicalFilter, now, cancellation);

				var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
				var results = evaluator.Evaluate(ctx);
				if (settings.EmitManagementProposals)
					foreach (var r in results) { sink.Emit(r.Proposal, r.IsRepeat); proposalsEmitted++; }

				if (openEvaluator != null && openSink != null)
				{
					var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
					foreach (var p in openResults) { openSink.Emit(p); proposalsEmitted++; }
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
	}

	private static DateTime ComputeStopTime(AIWatchSettings s, AIConfig config)
	{
		if (s.Duration != null && AIWatchSettings.TryParseDuration(s.Duration, out var span))
			return DateTime.Now + span;
		// Default: today's market close in the configured timezone.
		var tz = TimeZoneInfo.FindSystemTimeZoneById(config.MarketHours.Tz);
		var nowLocal = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
		var endParts = config.MarketHours.End.Split(':');
		var closeLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, int.Parse(endParts[0]), int.Parse(endParts[1]), 0, DateTimeKind.Unspecified);
		return TimeZoneInfo.ConvertTimeToUtc(closeLocal, tz).ToLocalTime();
	}

	private static bool IsMarketOpen(MarketHoursConfig mh)
	{
		var tz = TimeZoneInfo.FindSystemTimeZoneById(mh.Tz);
		var nowLocal = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
		if (nowLocal.DayOfWeek == DayOfWeek.Saturday || nowLocal.DayOfWeek == DayOfWeek.Sunday) return false;
		var start = TimeSpan.Parse(mh.Start);
		var end = TimeSpan.Parse(mh.End);
		var t = nowLocal.TimeOfDay;
		return t >= start && t <= end;
	}
}
