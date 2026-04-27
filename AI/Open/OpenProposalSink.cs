using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
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
            diagnostic = p.Diagnostic is null ? null : AnalyzePositionCommand.SerializeDiagnostic(p.Diagnostic),
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

        var rows = new List<IRenderable>();
        var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
        rows.Add(new Markup($"[bold]{Markup.Escape(legsText)}[/]"));
        if (p.CashReserveBlocked && p.CashReserveDetail != null)
            rows.Add(new Markup($"[yellow]{Markup.Escape(p.CashReserveDetail)}[/]"));
        if (!p.CashReserveBlocked && p.Qty > 0)
            AppendReproductionCommands(rows, p);
        if (p.Diagnostic is not null)
            rows.Add(RiskDiagnosticRenderer.Build(p.Diagnostic));

        var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
        var header = $"[bold {color}]{p.StructureKind}[/] [grey]{p.Ticker}[/] x{p.Qty}{blocked}";
        var panel = new Panel(new Rows(rows))
            .Header(header)
            .Expand()
            .BorderColor(SpectreColor(color));
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>Appends copy-pasteable `wa trade place` and `wa analyze trade` lines as Markup rows.
    /// trade place uses the absolute per-share net from DebitOrCreditPerContract (already computed at
    /// conservative execution prices: buys at ask, sells at bid). analyze trade uses @MID placeholders
    /// so the user can re-run against live quotes.</summary>
    private static void AppendReproductionCommands(List<IRenderable> rows, OpenProposal p)
    {
        var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
        var limit = Math.Abs(p.DebitOrCreditPerContract / 100m).ToString("F2", CultureInfo.InvariantCulture);
        rows.Add(new Markup($"[dim]↪ wa trade place --trade \"{Markup.Escape(tradesArg)}\" --limit {limit}[/]"));

        var analyzeArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@MID"));
        rows.Add(new Markup($"[dim]↪ wa analyze trade \"{Markup.Escape(analyzeArg)}\"[/]"));
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
