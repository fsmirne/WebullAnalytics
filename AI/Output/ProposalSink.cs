using System.Globalization;
using System.Text.Json;
using Spectre.Console;

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

	public ProposalSink(LogConfig log, string mode)
	{
		_log = log;
		_mode = mode;
		var path = Program.ResolvePath(log.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		_file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
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
			cashReserveBlocked = p.CashReserveBlocked,
			cashReserveDetail = p.CashReserveDetail,
			mode = _mode
		};
		_file.WriteLine(JsonSerializer.Serialize(record));
	}

	private void WriteConsole(ManagementProposal p, bool isRepeat)
	{
		if (isRepeat && _log.ConsoleVerbosity == "normal") return;
		if (_log.ConsoleVerbosity == "quiet" && !p.CashReserveBlocked && p.Kind != ProposalKind.AlertOnly && !isRepeat)
		{
			// In quiet mode we only print non-actionable tags and new proposals; this branch permits
			// the default (logged proposals). Tune as needed.
		}

		var color = p.Kind switch
		{
			ProposalKind.Close => "yellow",
			ProposalKind.Roll => "cyan",
			ProposalKind.AlertOnly => "grey",
			_ => "white"
		};
		var header = $"[bold {color}]{p.Rule}[/] [grey]{p.Ticker}[/] [dim]{p.PositionKey}[/]";
		if (p.CashReserveBlocked) header += " [yellow]⚠ blocked by cash reserve[/]";
		AnsiConsole.MarkupLine(header);

		foreach (var leg in p.Legs)
			AnsiConsole.MarkupLine($"  {Markup.Escape(leg.Action.ToUpperInvariant())} {Markup.Escape(leg.Symbol)} x{leg.Qty}");

		var netLabel = p.NetDebit >= 0m ? $"credit ${p.NetDebit:F2}" : $"debit ${-p.NetDebit:F2}";
		AnsiConsole.MarkupLine($"  [dim]net {Markup.Escape(netLabel)}[/]");
		AnsiConsole.MarkupLine($"  [italic]{Markup.Escape(p.Rationale)}[/]");
		if (p.CashReserveBlocked && p.CashReserveDetail != null)
			AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(p.CashReserveDetail)}[/]");
		WriteCommands(p);
		AnsiConsole.WriteLine();
	}

	private static void WriteCommands(ManagementProposal p)
	{
		if (p.Kind == ProposalKind.AlertOnly || p.Legs.Count == 0) return;

		var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var analyzeArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@MID"));
		var limit = (p.NetDebit / 100m).ToString("F2", CultureInfo.InvariantCulture);

		AnsiConsole.MarkupLine($"  [dim]trade place --trades \"{Markup.Escape(tradesArg)}\" --limit {limit}[/]");
		AnsiConsole.MarkupLine($"  [dim]analyze trade \"{Markup.Escape(analyzeArg)}\" --api yahoo[/]");
	}

	public void Dispose() => _file.Dispose();
}
