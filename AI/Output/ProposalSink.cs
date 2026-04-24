using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using WebullAnalytics;
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

	public ProposalSink(LogConfig log, string mode)
	{
		_log = log;
		_mode = mode;
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
			cashReserveBlocked = p.CashReserveBlocked,
			cashReserveDetail = p.CashReserveDetail,
			mode = _mode
		};
		_file.WriteLine(JsonSerializer.Serialize(record));
	}

	private void WriteConsole(ManagementProposal p, bool isRepeat)
	{
		if (isRepeat && _log.ConsoleVerbosity == "normal") return;

		var color = p.Kind switch
		{
			ProposalKind.Close => "yellow",
			ProposalKind.Roll => "cyan",
			ProposalKind.AlertOnly => "grey",
			_ => "white"
		};
		var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
		AnsiConsole.MarkupLine($"[bold {color}]{p.Rule}[/] [grey]{p.Ticker}[/] [dim]{p.PositionKey}[/]{blocked}");

		if (p.Legs.Count > 0)
		{
			var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
			var netLabel = p.NetDebit >= 0m ? $"net credit ${p.NetDebit:F2}" : $"net debit ${-p.NetDebit:F2}";
			AnsiConsole.MarkupLine($"  {Markup.Escape(legsText)} [dim]→ {Markup.Escape(netLabel)}[/]");
		}

		if (p.Kind != ProposalKind.AlertOnly && p.Legs.Count > 0)
		{
			var analyzeArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@MID"));

			// 4-leg reset (close existing + open new): split into close-half and open-half combos — Webull
			// rejects 4-leg orders that aren't iron condor/butterfly. ScenarioEngine.EmitReset emits legs
			// in close-first/open-second order, so slice by position.
			var canSplitReset = p.Kind == ProposalKind.Roll
				&& p.Legs.Count == 4
				&& p.Legs.All(l => l.PricePerShare.HasValue);

			// Non-calendar 2-leg rolls get split into per-leg orders so Webull's combo engine accepts the reversal.
			var canSplit2 = p.Kind == ProposalKind.Roll
				&& p.Legs.Count == 2
				&& p.Legs.All(l => l.PricePerShare.HasValue)
				&& !RollShape.IsSameStrikeCalendar(p.Legs.Select(l => l.Symbol));

			if (canSplitReset)
			{
				EmitComboLine(p.Legs.Take(2));
				EmitComboLine(p.Legs.Skip(2));
			}
			else if (canSplit2)
			{
				foreach (var leg in p.Legs)
				{
					var legLimit = leg.PricePerShare!.Value.ToString("F2", CultureInfo.InvariantCulture);
					AnsiConsole.MarkupLine($"  [dim]wa trade place --trade \"{Markup.Escape($"{leg.Action}:{leg.Symbol}:{leg.Qty}")}\" --limit {legLimit}[/]");
				}
			}
			else
			{
				var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
				var limit = Math.Abs(p.NetDebit / 100m).ToString("F2", CultureInfo.InvariantCulture);
				AnsiConsole.MarkupLine($"  [dim]wa trade place --trade \"{Markup.Escape(tradesArg)}\" --limit {limit}[/]");
			}

			AnsiConsole.MarkupLine($"  [dim]wa analyze trade \"{Markup.Escape(analyzeArg)}\"[/]");
		}

		AnsiConsole.MarkupLine($"  [italic]{Markup.Escape(p.Rationale)}[/]");
		if (p.CashReserveBlocked && p.CashReserveDetail != null)
			AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(p.CashReserveDetail)}[/]");
		AnsiConsole.WriteLine();
	}

	/// <summary>Emits a single combo `wa trade place` line for the given legs. `--limit` is the absolute
	/// per-share signed net (sell prices add, buy prices subtract); Webull infers side from the legs.
	/// Callers must ensure every leg has PricePerShare set.</summary>
	private static void EmitComboLine(IEnumerable<ProposalLeg> legs)
	{
		var list = legs.ToList();
		decimal signedNet = 0m;
		foreach (var l in list)
			signedNet += l.Action == "sell" ? l.PricePerShare!.Value : -l.PricePerShare!.Value;
		var limit = Math.Abs(signedNet).ToString("F2", CultureInfo.InvariantCulture);
		var arg = string.Join(",", list.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		AnsiConsole.MarkupLine($"  [dim]wa trade place --trade \"{Markup.Escape(arg)}\" --limit {limit}[/]");
	}

	public void Dispose() => _file.Dispose();
}
