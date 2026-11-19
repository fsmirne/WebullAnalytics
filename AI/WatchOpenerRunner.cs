using Spectre.Console;
using WebullAnalytics.AI.Output;

namespace WebullAnalytics.AI;

/// <summary>
/// Runs the opener half of the watch loop as a background task, so candidate scoring (seconds of CPU) never holds up the management ticks that keep the
/// broker stops and exit rules on a short cadence. The loop starts a run from a management tick's frame when the opener is due and none is still running;
/// everything the old per-tick tail did for the opener (the pulse, the 0DTE gate narration, the persisted heartbeat) happens here when the run ends.
/// A failed run is counted, not thrown: the loop reads <see cref="ConsecutiveFailures"/> and <see cref="Unauthorized"/> on its next tick, which keeps the
/// circuit breaker and the auth exit where they were.
/// </summary>
internal sealed class WatchOpenerRunner(LiveTickDeps deps, OpenCandidateEvaluator? openEvaluator, OpenProposalSink? openSink, bool debug)
{
	private readonly LiveTickState _state = new();
	// 0DTE session gate: the last state rendered, so a long "waiting" stretch prints once per state change rather than once per run, and whether the day
	// has been declared closed so the close is announced exactly once — as "no trade today" only if no run ever allowed an entry.
	private Open.ZeroDte.ZeroDteGateState? _lastGateState;
	private bool _gateDayClosedNoted;
	private bool _gateEverAllowedEntry;
	private Task? _running;

	public int ProposalsEmitted { get; private set; }
	public int ConsecutiveFailures { get; private set; }
	public bool Unauthorized { get; private set; }

	/// <summary>True when no run is in flight, so a new one may start.</summary>
	public bool Idle => _running == null || _running.IsCompleted;

	/// <summary>Starts a run on <paramref name="frame"/> and returns at once. <paramref name="outsideWindow"/> withholds the opener executor for the run (the proposals still render, as informational).</summary>
	public void Start(LiveTickFrame frame, int mgmtCount, bool outsideWindow, CancellationToken cancellation)
	{
		var runDeps = outsideWindow ? deps with { OpenerExecutor = null, OutsideEntryWindow = true } : deps;
		_running = Task.Run(() => RunAsync(frame, mgmtCount, runDeps, cancellation), CancellationToken.None);
	}

	/// <summary>Waits for the run in flight, if any. Never throws: <see cref="RunAsync"/> catches everything.</summary>
	public Task DrainAsync() => _running ?? Task.CompletedTask;

	private async Task RunAsync(LiveTickFrame frame, int mgmtCount, LiveTickDeps runDeps, CancellationToken cancellation)
	{
		var config = deps.Config;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			var (openCount, spot) = await LiveTick.OpenerTickAsync(frame, runDeps, _state, cancellation);
			ProposalsEmitted += openCount;
			Pulse(frame.Now, spot, openCount, mgmtCount, clock.Elapsed);
			NarrateGate();
			// Persisted heartbeat: the console pulse dies with the terminal, so in the proposal LOG a day suppressed by MinScoreToOpen was indistinguishable from a watch
			// that never ran (2026-08-03). One lean type:"tick" line per successful run records liveness plus the best pre-gate candidate's score; type=="open" consumers never see them.
			openSink?.EmitTick(spot, openCount, mgmtCount, openEvaluator?.LastTopCandidate, config.Opener.MinScoreToOpen);
			ConsecutiveFailures = 0;
		}
		// Only a cancellation of the loop's own token ends a run quietly. An HTTP timeout surfaces as TaskCanceledException with that token untouched: a failed run.
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
		catch (UnauthorizedAccessException ex)
		{
			Console.Error.WriteLine($"Auth failure: {ex.Message}. Exiting.");
			Unauthorized = true;
		}
		catch (Exception ex)
		{
			ConsecutiveFailures++;
			AnsiConsole.MarkupLine($"[red]Opener run failed ({ConsecutiveFailures}/{WatchFailureLimit}): {Markup.Escape(ExceptionChain(ex))}[/]");
		}
	}

	/// <summary>Consecutive failed ticks (or opener runs) after which the watch exits.</summary>
	internal const int WatchFailureLimit = 5;

	/// <summary>The message of an exception and each inner one, so a wrapped HTTP failure names its cause.</summary>
	internal static string ExceptionChain(Exception ex)
	{
		var chain = new System.Text.StringBuilder(ex.Message);
		for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) chain.Append(" -> ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
		return chain.ToString();
	}

	/// <summary>A line per run so a proposal and a quiet run are both anchored in time: stamped with the time the run started, and with how long it took, so a slow run reads
	/// as one rather than as a late launch. At debug the [debug] line replaces it.</summary>
	private void Pulse(DateTime now, decimal? spot, int openCount, int mgmtCount, TimeSpan took)
	{
		var config = deps.Config;
		if (debug) { Console.Error.WriteLine($"[debug] {now:HH:mm:ss} opener run: spot={spot} openProposals={openCount} mgmtResults={mgmtCount} took={took.TotalSeconds:0.0}s"); return; }
		var spotStr = spot.HasValue ? spot.Value.ToString("0.00") : "?";
		AnsiConsole.MarkupLine($"[dim]{now:HH:mm:ss}  {config.Ticker} {spotStr} — {(openCount == 0 ? "no open proposals emitted" : $"{openCount} open proposal(s) emitted")} ({took.TotalSeconds:0.0}s)[/]");
	}

	/// <summary>The 0DTE gate's verdict, printed on every state CHANGE (a 90-minute wait is one line) and once when the day closes; without it a session the gate
	/// deliberately sits out looks the same as one where nothing scored.</summary>
	private void NarrateGate()
	{
		var config = deps.Config;
		if (!config.Opener.ZeroDteGate.Enabled || openEvaluator == null || !openEvaluator.LastGateVerdicts.TryGetValue(config.Ticker, out var verdict)) return;
		if (verdict.State != _lastGateState)
		{
			var color = verdict.AllowsEntry ? "green" : verdict.DayClosed ? "yellow" : "dim";
			AnsiConsole.MarkupLine($"[{color}]gate {verdict.State}: {Markup.Escape(verdict.Summary)}[/]");
			_lastGateState = verdict.State;
		}
		_gateEverAllowedEntry |= verdict.AllowsEntry;
		if (!verdict.DayClosed || _gateDayClosedNoted) return;
		AnsiConsole.MarkupLine(_gateEverAllowedEntry
			? $"[yellow]Entry window closed at {Markup.Escape(config.Opener.ZeroDteGate.LatestEntryEt ?? "")} ET — no new 0DTE entries today. Still managing open positions.[/]"
			: $"[yellow]No trade today — the session never satisfied the entry conditions. Still managing open positions.[/]");
		_gateDayClosedNoted = true;
	}
}
