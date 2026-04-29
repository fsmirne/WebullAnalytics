using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Analyze;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Writes open proposals to JSONL and console. Keeps per-fingerprint score history for console dedup.
/// JSONL always gets the entry; console suppresses repeats unless the bias-adjusted score has moved
/// by ≥ 10% since last emission.
/// </summary>
internal sealed class OpenProposalSink : IDisposable
{
	private readonly StreamWriter _file;
	private readonly LogConfig _log;
	private readonly string _mode;
	private readonly string _suggestPricing;
	private readonly bool _ascii;
	private readonly string _cmdPrefix;
	private readonly Dictionary<string, decimal> _lastScoreByFingerprint = new();

	public OpenProposalSink(LogConfig log, string mode, string suggestPricing = SuggestionPricing.Mid, bool ascii = false)
	{
		_log = log;
		_mode = mode;
		_suggestPricing = SuggestionPricing.Normalize(suggestPricing);
		_ascii = ascii;
		_cmdPrefix = ascii ? "L-" : "↪";
		var path = Program.ResolvePath(log.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		_file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
	}

	public bool IsRepeat(OpenProposal p)
	{
		if (!_lastScoreByFingerprint.TryGetValue(p.Fingerprint, out var last)) return false;
		var current = p.FinalScore ?? p.BiasAdjustedScore;
		var abs = Math.Abs(current - last);
		var threshold = Math.Abs(last) * 0.10m;
		return abs < threshold;
	}

	public void Emit(OpenProposal p)
	{
		var repeat = IsRepeat(p);
		WriteJsonl(p);
		if (_log.ConsoleVerbosity != "error" && (!repeat || _log.ConsoleVerbosity == "debug")) WriteConsole(p);
		_lastScoreByFingerprint[p.Fingerprint] = p.FinalScore ?? p.BiasAdjustedScore;
	}

	public void Flush() => _file.Flush();

	private void WriteJsonl(OpenProposal p)
	{
		var record = new
		{
			type = "open",
			ts = DateTime.Now.ToString("o"),
			mode = _mode,
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
			thetaPerDayPerContract = p.ThetaPerDayPerContract,
			diagnostic = p.Diagnostic is null ? null : AnalyzePositionCommand.SerializeDiagnostic(p.Diagnostic),
		};
		_file.WriteLine(JsonSerializer.Serialize(record));
	}

	private void WriteConsole(OpenProposal p)
	{
		var color = p.StructureKind switch
		{
			OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical or OpenStructureKind.IronButterfly or OpenStructureKind.IronCondor => "green",
			OpenStructureKind.LongCall or OpenStructureKind.LongPut => "cyan",
			OpenStructureKind.LongCalendar or OpenStructureKind.DoubleCalendar or OpenStructureKind.LongDiagonal or OpenStructureKind.DoubleDiagonal => "magenta",
			_ => "white"
		};

		var rows = new List<IRenderable>();
		var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
		rows.Add(new Markup($"[bold]{Markup.Escape(legsText)}[/]"));
		if (p.CashReserveBlocked && p.CashReserveDetail != null)
			rows.Add(new Markup($"[yellow]{Markup.Escape(p.CashReserveDetail)}[/]"));
		if (!p.CashReserveBlocked && p.Qty > 0)
			AppendReproductionCommands(rows, p, _suggestPricing);
		if (p.Diagnostic is not null)
			rows.Add(RiskDiagnosticRenderer.Build(p.Diagnostic, ascii: _ascii));

		var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
		var header = $"[bold {color}]{p.StructureKind}[/] [grey]{p.Ticker}[/] x{p.Qty}{blocked}";
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
	/// analyze trade uses the matching placeholder keywords for the same basis.</summary>
	private void AppendReproductionCommands(List<IRenderable> rows, OpenProposal p, string suggestPricing)
	{
		var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var limitPerShare = SuggestionPricing.TryGetLimitPerShare(p.Legs, suggestPricing) ?? Math.Abs(p.DebitOrCreditPerContract / 100m);
		var limit = limitPerShare.ToString("F2", CultureInfo.InvariantCulture);
		rows.Add(new Markup($"[dim]{_cmdPrefix} wa trade place --trade \"{Markup.Escape(tradesArg)}\" --limit {limit}[/]"));

		var analyzeArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{SuggestionPricing.AnalyzeKeywordFor(l, suggestPricing)}"));
		rows.Add(new Markup($"[dim]{_cmdPrefix} wa analyze trade \"{Markup.Escape(analyzeArg)}\"[/]"));
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
