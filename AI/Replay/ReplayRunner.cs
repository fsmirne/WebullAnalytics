using Spectre.Console;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI.Replay;

internal sealed class ReplayRunner
{
	private readonly AIConfig _config;
	private readonly ReplayPositionSource _positions;
	private readonly ReplayQuoteSource _quotes;
	private readonly List<Trade> _allTrades;

	public ReplayRunner(AIConfig config, ReplayPositionSource positions, ReplayQuoteSource quotes, List<Trade> allTrades)
	{
		_config = config;
		_positions = positions;
		_quotes = quotes;
		_allTrades = allTrades;
	}

	public async Task<int> RunAsync(DateTime since, DateTime until, string granularity, CancellationToken cancellation)
	{
		PrintDisclaimer();

		var tickerSet = new HashSet<string>(_config.Tickers, StringComparer.OrdinalIgnoreCase);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(_config), _config);
		using var sink = new ProposalSink(_config.Log, mode: "replay");

		var steps = EnumerateSteps(since, until, granularity).ToList();
		var ruleFireCounts = new Dictionary<string, int>();
		var agreementCounts = new Dictionary<string, int> { ["match"] = 0, ["partial"] = 0, ["miss"] = 0, ["divergent"] = 0 };
		var stepsWithPositions = 0;

		foreach (var step in steps)
		{
			cancellation.ThrowIfCancellationRequested();

			var openPositions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			if (openPositions.Count == 0) continue;
			stepsWithPositions++;

			var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
			var optionSymbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
			var quoteSnapshot = await _quotes.GetQuotesAsync(step, optionSymbols, tickerSet, cancellation);

			var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, new Dictionary<string, TechnicalBias>());
			var results = evaluator.Evaluate(ctx);

			foreach (var r in results)
			{
				sink.Emit(r.Proposal, r.IsRepeat);
				ruleFireCounts[r.Proposal.Rule] = (ruleFireCounts.TryGetValue(r.Proposal.Rule, out var n) ? n : 0) + 1;

				var agreement = ClassifyAgreement(r.Proposal, step);
				agreementCounts[agreement]++;
			}
		}

		PrintSummary(ruleFireCounts, agreementCounts, steps.Count, stepsWithPositions);
		return 0;
	}

	private IEnumerable<DateTime> EnumerateSteps(DateTime since, DateTime until, string granularity)
	{
		var d = since.Date;
		while (d <= until.Date)
		{
			if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
			{
				if (granularity == "hourly")
				{
					for (var h = 10; h <= 15; h++) yield return d.AddHours(h).AddMinutes(45);
				}
				else
				{
					yield return d.AddHours(15).AddMinutes(45);
				}
			}
			d = d.AddDays(1);
		}
	}

	/// <summary>Classifies whether the proposal aligns with what the user actually did at this timestamp.
	/// Phase-1 heuristic: any same-day trade touching the same ticker = "partial"; otherwise = "miss".</summary>
	private string ClassifyAgreement(ManagementProposal p, DateTime step)
	{
		var sameDay = _allTrades.Where(t => t.Timestamp.Date == step.Date && t.MatchKey.Contains(p.Ticker, StringComparison.OrdinalIgnoreCase)).ToList();
		if (sameDay.Count == 0) return "miss";
		return "partial";
	}

	private static void PrintDisclaimer()
	{
		AnsiConsole.MarkupLine("[yellow bold]Replay disclaimer:[/]");
		AnsiConsole.MarkupLine("[dim]  • Quotes are Black-Scholes synthesized from fill-anchored IV, not historical bid/ask.[/]");
		AnsiConsole.MarkupLine("[dim]  • Roll credits are theoretical mids, not realized fills.[/]");
		AnsiConsole.MarkupLine("[dim]  • Daily granularity misses intraday opportunities and whipsaws.[/]");
		AnsiConsole.WriteLine();
	}

	private static void PrintSummary(Dictionary<string, int> rules, Dictionary<string, int> agreement, int stepsWalked, int stepsWithPositions)
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[bold]Replay summary[/] — {stepsWalked} steps walked, {stepsWithPositions} with positions");
		if (rules.Count == 0) AnsiConsole.MarkupLine("[dim]  No rules fired.[/]");
		foreach (var kv in rules.OrderByDescending(k => k.Value))
			AnsiConsole.MarkupLine($"  {kv.Key}: {kv.Value}");
		AnsiConsole.MarkupLine($"[dim]Agreement: match={agreement["match"]} partial={agreement["partial"]} miss={agreement["miss"]} divergent={agreement["divergent"]}[/]");
	}
}
