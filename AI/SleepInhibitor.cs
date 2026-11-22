using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WebullAnalytics.AI;

/// <summary>
/// Keeps the machine from sleeping for inactivity while the live watch loop runs: a watch the OS suspends mid-session leaves its positions
/// unmanaged. Only idle sleep is held off — closing the lid, the power button or a critical battery still suspend, because those are deliberate
/// (a lid closed for the bag must not keep a hot laptop running inside it). Best-effort: where the platform mechanism is missing, the watch runs
/// without it and says so.
/// <list type="bullet">
/// <item><description>Linux: a systemd-logind block inhibitor (<c>systemd-inhibit --what=idle:sleep</c>) held by <c>tail --pid</c> on this process,
/// so it is released even if this process dies without disposing it.</description></item>
/// <item><description>macOS: <c>caffeinate -i -w</c> on this process, released the same way.</description></item>
/// <item><description>Windows: <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)</c> on a dedicated thread (the state belongs to the
/// thread that set it, and an async continuation can resume on any thread), cleared on dispose; Windows drops it when the process exits.</description></item>
/// </list>
/// </summary>
internal sealed class SleepInhibitor : IDisposable
{
	private readonly Process? _holder;
	private readonly ManualResetEventSlim? _release;

	private SleepInhibitor(Process holder) => _holder = holder;
	private SleepInhibitor(ManualResetEventSlim release) => _release = release;

	/// <summary>Holds off idle sleep until disposed, or returns null (after saying why) when this platform offers no way to.</summary>
	public static SleepInhibitor? Acquire(string why)
	{
		try
		{
			var inhibitor = OperatingSystem.IsWindows() ? AcquireWindows()
				: OperatingSystem.IsLinux() ? AcquireHolder("systemd-inhibit", ["--what=idle:sleep", "--who=wa", $"--why={why}", "--mode=block", "tail", $"--pid={Environment.ProcessId}", "-f", "/dev/null"])
				: OperatingSystem.IsMacOS() ? AcquireHolder("caffeinate", ["-i", "-w", Environment.ProcessId.ToString()])
				: null;
			if (inhibitor != null) AnsiConsole.MarkupLine("[dim]Idle sleep blocked while the watch runs (closing the lid still suspends).[/]");
			else AnsiConsole.MarkupLine("[yellow]Could not block idle sleep on this platform — keep the machine awake yourself.[/]");
			return inhibitor;
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Could not block idle sleep ({Markup.Escape(ex.Message)}) — keep the machine awake yourself.[/]");
			return null;
		}
	}

	/// <summary>Starts a helper process that holds the inhibitor for as long as it lives. A helper that exits at once failed (no logind, a
	/// policy refusal): its error is reported and nothing is held.</summary>
	private static SleepInhibitor? AcquireHolder(string fileName, string[] args)
	{
		var start = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var a in args) start.ArgumentList.Add(a);
		var holder = Process.Start(start);
		if (holder == null) return null;
		if (!holder.WaitForExit(TimeSpan.FromMilliseconds(300))) return new SleepInhibitor(holder);
		var error = holder.StandardError.ReadToEnd().Trim();
		var exitCode = holder.ExitCode;
		holder.Dispose();
		throw new InvalidOperationException($"{fileName} exited with code {exitCode}{(error.Length > 0 ? $": {error}" : "")}");
	}

	private static SleepInhibitor AcquireWindows()
	{
		var release = new ManualResetEventSlim();
		new Thread(HoldAwake) { IsBackground = true, Name = "wa sleep inhibitor" }.Start(release);
		return new SleepInhibitor(release);
	}

	/// <summary>The Windows holder thread: sets the execution state, waits for the release, and clears it on the same thread.</summary>
	private static void HoldAwake(object? release)
	{
		SetThreadExecutionState(EsContinuous | EsSystemRequired);
		((ManualResetEventSlim)release!).Wait();
		SetThreadExecutionState(EsContinuous);
	}

	public void Dispose()
	{
		if (_holder != null)
		{
			try { if (!_holder.HasExited) _holder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
			_holder.Dispose();
		}
		_release?.Set();
	}

	private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

	[DllImport("kernel32.dll")]
	private static extern uint SetThreadExecutionState(uint esFlags);
}
