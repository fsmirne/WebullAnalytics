using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.Analyze;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Positions;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Writes proposals to the console (human-readable, Spectre-formatted) and a JSONL log (machine-parseable).
/// Idempotency dedup is handled by RuleEvaluator; this sink respects the `isRepeat` flag to suppress
/// repeat console lines at normal verbosity while always appending to the JSONL file.
/// </summary>
internal sealed class ProposalSink : IDisposable
{
	private readonly StreamWriter _file;
	private readonly LogConfig _log;
	private readonly string _mode; // "watch" | "once" | "replay"
	private readonly string _suggestPricing;

	public ProposalSink(LogConfig log, string mode, string suggestPricing = SuggestionPricing.Mid)
	{
		_log = log;
		_mode = mode;
		_suggestPricing = SuggestionPricing.Normalize(suggestPricing);
		var path = Program.ResolvePath(log.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		_file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
	}

	public void Emit(ManagementProposal p, bool isRepeat)
	{
		WriteJsonl(p);
		WriteConsole(p, isRepeat);
	}

	private void WriteJsonl(ManagementProposal p)
	{
		var record = new
		{
			type = "management",
			ts = DateTime.Now.ToString("o"),
			rule = p.Rule,
			ticker = p.Ticker,
			positionKey = p.PositionKey,
			proposal = new
			{
				type = p.Kind.ToString().ToLowerInvariant(),
				legs = p.Legs.Select(l => new { action = l.Action, symbol = l.Symbol, qty = l.Qty }),
				netDebit = p.NetDebit
			},
			rationale = p.Rationale,
			diagnostic = p.Diagnostic is null ? null : AnalyzePositionCommand.SerializeDiagnostic(p.Diagnostic),
			cashReserveBlocked = p.CashReserveBlocked,
			cashReserveDetail = p.CashReserveDetail,
			mode = _mode
		};
		_file.WriteLine(JsonSerializer.Serialize(record));
	}

	private void WriteConsole(ManagementProposal p, bool isRepeat)
	{
		if (_log.ConsoleVerbosity == "error") return;
		if (isRepeat && _log.ConsoleVerbosity == "information") return;

		var color = p.Kind switch
		{
			ProposalKind.Close => "yellow",
			ProposalKind.Roll => "blue",
			ProposalKind.AlertOnly => "grey",
			_ => "white"
		};

		var rows = new List<IRenderable>();

		if (p.Legs.Count > 0)
		{
			var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
			var netLabel = p.NetDebit >= 0m ? $"net credit ${p.NetDebit:F2}" : $"net debit ${-p.NetDebit:F2}";
			rows.Add(new Markup($"[bold]{Markup.Escape(legsText)}[/] [dim]→ {Markup.Escape(netLabel)}[/]"));
		}

		if (p.CashReserveBlocked && p.CashReserveDetail != null)
			rows.Add(new Markup($"[yellow]{Markup.Escape(p.CashReserveDetail)}[/]"));

		if (p.Kind != ProposalKind.AlertOnly && p.Legs.Count > 0)
		{
			var analyzeArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{SuggestionPricing.AnalyzeKeywordFor(l, _suggestPricing)}"));

			// 4-leg reset (close existing + open new): split into close-half and open-half combos — Webull
			// rejects 4-leg orders that aren't iron condor/butterfly. ScenarioEngine.EmitReset emits legs
			// in close-first/open-second order, so slice by position.
			var canSplitReset = p.Kind == ProposalKind.Roll
				&& p.Legs.Count == 4
				&& p.Legs.All(l => SuggestionPricing.PriceFor(l, _suggestPricing).HasValue);

			// Non-calendar 2-leg rolls get split into per-leg orders so Webull's combo engine accepts the reversal.
			var canSplit2 = p.Kind == ProposalKind.Roll
				&& p.Legs.Count == 2
				&& p.Legs.All(l => SuggestionPricing.PriceFor(l, _suggestPricing).HasValue)
				&& !RollShape.IsSameStrikeCalendar(p.Legs.Select(l => l.Symbol));

			if (canSplitReset)
			{
				AppendComboLine(rows, p.Legs.Take(2), _suggestPricing, null);
				AppendComboLine(rows, p.Legs.Skip(2), _suggestPricing, null);
			}
			else if (canSplit2)
			{
				foreach (var leg in p.Legs)
				{
					var legLimit = SuggestionPricing.PriceFor(leg, _suggestPricing)!.Value.ToString("F2", CultureInfo.InvariantCulture);
					rows.Add(new Markup($"[dim]↪ wa trade place --trade \"{Markup.Escape($"{leg.Action}:{leg.Symbol}:{leg.Qty}")}\" --limit {legLimit}[/]"));
				}
			}
			else
			{
				var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
				var limitPerShare = SuggestionPricing.TryGetLimitPerShare(p.Legs, _suggestPricing) ?? Math.Abs(p.NetDebit / 100m);
				var limit = limitPerShare.ToString("F2", CultureInfo.InvariantCulture);
				rows.Add(new Markup($"[dim]↪ wa trade place --trade \"{Markup.Escape(tradesArg)}\" --limit {limit}[/]"));
			}

			rows.Add(new Markup($"[dim]↪ wa analyze trade \"{Markup.Escape(analyzeArg)}\"[/]"));
		}

		if (p.Diagnostic is not null)
			rows.Add(RiskDiagnosticRenderer.Build(p.Diagnostic));

		rows.Add(new Markup($"[italic]{Markup.Escape(p.Rationale)}[/]"));

		var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
		var header = $"[bold {color}]Manage: {p.Rule}[/] [grey]{p.Ticker}[/] [dim]{p.PositionKey}[/]{blocked}";
		var panel = new Panel(new Rows(rows))
			.Header(header)
			.Expand()
			.BorderColor(SpectreColor(color));
		AnsiConsole.Write(panel);
		AnsiConsole.WriteLine();
	}

	/// <summary>Emits a single combo `wa trade place` line for the given legs. `--limit` is the absolute
	/// per-share signed net (sell prices add, buy prices subtract); Webull infers side from the legs.
	/// Callers must ensure every leg has PricePerShare set.</summary>
	private static void AppendComboLine(List<IRenderable> rows, IEnumerable<ProposalLeg> legs, string suggestPricing, decimal? fallbackLimitPerShare)
	{
		var list = legs.ToList();
		var limitPerShare = SuggestionPricing.TryGetLimitPerShare(list, suggestPricing)
			   ?? fallbackLimitPerShare
			   ?? 0m;
		var limit = limitPerShare.ToString("F2", CultureInfo.InvariantCulture);
		var arg = string.Join(",", list.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		rows.Add(new Markup($"[dim]↪ wa trade place --trade \"{Markup.Escape(arg)}\" --limit {limit}[/]"));
	}

	private static Color SpectreColor(string name) => name switch
	{
		"yellow" => Color.Yellow,
		"blue" => Color.Blue,
		"grey" => Color.Grey,
		_ => Color.White
	};

	public void Dispose() => _file.Dispose();
}
