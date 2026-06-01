using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Analyze;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Writes open proposals to JSONL and console. Every Emit writes both — the score-delta-based
/// console suppression that used to live here hid the top-ranked panel whenever its fingerprint
/// re-appeared with a similar score, which left the auto-executor's "opener auto-execute"
/// line referring to a proposal the user couldn't see on the same tick. Watch-mode dedup is
/// now exclusively the auto-executor's job (and only for live submits).
/// </summary>
internal sealed class OpenProposalSink : IDisposable
{
	private readonly StreamWriter _file;
	private readonly string _consoleVerbosity;
	private readonly string _mode;
	private readonly string _suggestPricing;
	private readonly bool _ascii;
	private readonly string _cmdPrefix;

	public OpenProposalSink(string consoleVerbosity, string ticker, string mode, string suggestPricing = SuggestionPricing.Mid, bool ascii = false)
	{
		_consoleVerbosity = consoleVerbosity;
		_mode = mode;
		_suggestPricing = SuggestionPricing.Normalize(suggestPricing);
		_ascii = ascii;
		_cmdPrefix = WebullAnalytics.IO.TextFileExporter.ReproductionLeadIn(ascii);
		var path = ProposalLog.ResolvedPath(ticker);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		_file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
	}

	public void Emit(OpenProposal p, int? rank = null)
	{
		WriteJsonl(p);
		if (_consoleVerbosity != "error") WriteConsole(p, rank);
	}

	public void Flush() => _file.Flush();

	private void WriteJsonl(OpenProposal p) => _file.WriteLine(SerializeRecord(p, _mode));

	/// <summary>Serializes one open proposal to its JSONL line. Pure (no I/O) so it's unit-testable
	/// without touching the filesystem; the sink wraps it with the append writer.</summary>
	internal static string SerializeRecord(OpenProposal p, string mode)
	{
		var record = new
		{
			type = "open",
			ts = DateTime.Now.ToString("o"),
			mode = mode,
			ticker = p.Ticker,
			structure = p.StructureKind.ToString(),
			legs = p.Legs.Select(l => new { action = l.Action, symbol = l.Symbol, qty = l.Qty }),
			qty = p.Qty,
			cashImpactPerContract = p.DebitOrCreditPerContract,
			maxProfit = p.MaxProfitPerContract,
			maxLoss = p.MaxLossPerContract,
			capitalAtRisk = p.CapitalAtRiskPerContract,
			pop = p.ProbabilityOfProfit,
			ev = p.ExpectedValuePerContract,
			daysToTarget = p.DaysToTarget,
			rawScore = p.RawScore,
			biasAdjustedScore = p.BiasAdjustedScore,
			finalScore = p.FinalScore,
			directionalFit = p.DirectionalFit,
			breakevens = p.Breakevens,
			rationale = p.Rationale,
			fingerprint = p.Fingerprint,
			cashReserveBlocked = p.CashReserveBlocked,
			cashReserveDetail = p.CashReserveDetail,
			pricingWarning = p.PricingWarning,
			thetaPerDayPerContract = p.ThetaPerDayPerContract,
			diagnostic = p.Diagnostic is null ? null : AnalyzePositionCommand.SerializeDiagnostic(p.Diagnostic),
		};
		return JsonSerializer.Serialize(record);
	}

	private void WriteConsole(OpenProposal p, int? rank)
	{
		var color = p.StructureKind switch
		{
			OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical or OpenStructureKind.IronButterfly or OpenStructureKind.IronCondor => "green",
			OpenStructureKind.LongCall or OpenStructureKind.LongPut => "cyan",
			OpenStructureKind.LongCalendar or OpenStructureKind.DoubleCalendar or OpenStructureKind.LongDiagonal or OpenStructureKind.DoubleDiagonal => "magenta",
			_ => "white"
		};

		var rows = new List<IRenderable>();
		if (p.StructureKind is OpenStructureKind.DoubleCalendar or OpenStructureKind.DoubleDiagonal)
		{
			// Both halves of the double share a single panel so the user can place either side
			// independently or both as one 4-leg order. Group by P/C; print put side first.
			var grouped = p.Legs
				.Select(l => (Leg: l, Parsed: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
				.Where(x => x.Parsed != null)
				.GroupBy(x => x.Parsed!.CallPut, StringComparer.Ordinal)
				.OrderBy(g => g.Key == "P" ? 0 : 1)
				.ToList();
			if (grouped.Count == 2 && grouped.All(g => g.Count() == 2))
			{
				foreach (var group in grouped)
				{
					var label = group.Key == "P" ? "Put side: " : "Call side:";
					var sideText = string.Join(", ", group.Select(x => $"{x.Leg.Action.ToUpperInvariant()} {x.Leg.Symbol} x{x.Leg.Qty}"));
					rows.Add(new Markup($"[bold]{label}[/] {Markup.Escape(sideText)}"));
				}
			}
			else
			{
				var legsTextFallback = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
				rows.Add(new Markup($"[bold]{Markup.Escape(legsTextFallback)}[/]"));
			}
		}
		else
		{
			var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
			rows.Add(new Markup($"[bold]{Markup.Escape(legsText)}[/]"));
		}
        if (!string.IsNullOrWhiteSpace(p.PricingWarning))
			rows.Add(new Markup($"[yellow]{Markup.Escape(p.PricingWarning)}[/]"));
		if (p.CashReserveBlocked && p.CashReserveDetail != null)
			rows.Add(new Markup($"[yellow]{Markup.Escape(p.CashReserveDetail)}[/]"));
		// Blocked proposals keep qty=1 on their legs (the enumerator's initial sizing) since ApplyCashSizing
		// only scales legs when sizing succeeds. Emit the commands anyway so the user can review the
		// structure's parameters even though they can't afford to place it as sized.
		if (p.Qty > 0 || p.CashReserveBlocked)
			AppendReproductionCommands(rows, p, _suggestPricing);
		if (p.Diagnostic is not null)
			rows.Add(RiskDiagnosticRenderer.Build(p.Diagnostic, ascii: _ascii));

		var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
		// Path-aware EV is the model's verdict that includes the StopLossRule firing intra-period.
		// When it's non-positive the candidate is the "least bad" of what was enumerated, not an
		// endorsed setup — flag it loudly in the header so it doesn't read as "#1 = best."
		var negativeEv = p.RealizedExpectedValuePerContract is decimal ev && ev <= 0m
			? " [red]⚠ negative EV[/]"
			: "";
		var rankPrefix = rank is int n ? $"[grey]#{n}[/] " : "";
		var header = $"{rankPrefix}[bold {color}]{p.StructureKind}[/] [grey]{p.Ticker}[/] x{p.Qty}{blocked}{negativeEv}";
		var panel = new Panel(new Rows(rows))
			.Header(header)
			.Expand()
			.Border(_ascii ? BoxBorder.Ascii : BoxBorder.Rounded)
			.BorderColor(SpectreColor(color));
		AnsiConsole.Write(panel);
		AnsiConsole.WriteLine();
	}

	/// <summary>Appends copy-pasteable `wa trade place` and `wa analyze trade` lines as Markup rows.
	/// trade place uses the selected price basis (mid by default, bid/ask when explicitly requested).
	/// analyze trade uses the matching placeholder keywords for the same basis. DoubleCalendar/DoubleDiagonal
	/// emit two trade place lines (Webull rejects 4-leg double-calendar tickets) but a single analyze trade
	/// covering all four legs since the analyzer scores the whole structure.</summary>
	private void AppendReproductionCommands(List<IRenderable> rows, OpenProposal p, string suggestPricing)
	{
		// Split-aware `wa trade place` line(s) + a single `wa analyze trade` line, shared with the opener's
		// debug best-candidate trace via ReproductionCommands so the two can't drift. The panel uses the
		// live MID/BID/ASK keyword pricing (re-fetched on run); the ↪ lead-in matches the rest of the report.
		foreach (var line in ReproductionCommands.Build(p, suggestPricing))
			rows.Add(new Markup($"[dim]{_cmdPrefix} {Markup.Escape(line)}[/]"));
	}

	private static Color SpectreColor(string name) => name switch
	{
		"green" => Color.Green,
		"cyan" => Color.Cyan1,
		"magenta" => Color.Magenta1,
		_ => Color.White
	};

	public void Dispose() => _file.Dispose();
}
