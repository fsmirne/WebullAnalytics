using System.Globalization;
using System.Text.Json;
using Spectre.Console;

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
    private readonly Dictionary<string, decimal> _lastScoreByFingerprint = new();

    public OpenProposalSink(LogConfig log, string mode)
    {
        _log = log;
        _mode = mode;
        var path = Program.ResolvePath(log.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public bool IsRepeat(OpenProposal p)
    {
        if (!_lastScoreByFingerprint.TryGetValue(p.Fingerprint, out var last)) return false;
        var abs = Math.Abs(p.BiasAdjustedScore - last);
        var threshold = Math.Abs(last) * 0.10m;
        return abs < threshold;
    }

    public void Emit(OpenProposal p)
    {
        var repeat = IsRepeat(p);
        WriteJsonl(p);
        if (!repeat || _log.ConsoleVerbosity == "debug") WriteConsole(p);
        _lastScoreByFingerprint[p.Fingerprint] = p.BiasAdjustedScore;
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
            directionalFit = p.DirectionalFit,
            breakevens = p.Breakevens,
            rationale = p.Rationale,
            fingerprint = p.Fingerprint,
            cashReserveBlocked = p.CashReserveBlocked,
            cashReserveDetail = p.CashReserveDetail,
            diagnostic = p.Diagnostic is null ? null : WebullAnalytics.Analyze.AnalyzePositionCommand.SerializeDiagnostic(p.Diagnostic),
        };
        _file.WriteLine(JsonSerializer.Serialize(record));
    }

    private void WriteConsole(OpenProposal p)
    {
        var color = p.StructureKind switch
        {
            OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => "green",
            OpenStructureKind.LongCall or OpenStructureKind.LongPut => "cyan",
            OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal => "magenta",
            _ => "white"
        };
        var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
        AnsiConsole.MarkupLine($"[bold {color}]{p.StructureKind}[/] [grey]{p.Ticker}[/] x{p.Qty}{blocked}");
        var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
        AnsiConsole.MarkupLine($"  {Markup.Escape(legsText)}");
        AnsiConsole.MarkupLine($"  [italic]{Markup.Escape(p.Rationale)}[/]");
        if (p.CashReserveBlocked && p.CashReserveDetail != null)
            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(p.CashReserveDetail)}[/]");
        if (p.Diagnostic is not null)
            WebullAnalytics.AI.RiskDiagnostics.RiskDiagnosticRenderer.WriteConsole(AnsiConsole.Console, p.Diagnostic);
        AnsiConsole.WriteLine();
    }

    public void Dispose() => _file.Dispose();
}
