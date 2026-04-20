using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Checks terminal width before rendering wide console tables and offers to resize if too narrow.
/// </summary>
static class TerminalHelper
{
	public const int DetailedMinWidth = 200;
	public const int SimplifiedMinWidth = 130;

	/// <summary>
	/// Ensures the terminal is wide enough for wide tables, honoring the 'autoExpandTerminal' flag
	/// from data/config.json. Callers that already have the flag resolved can call EnsureTerminalWidth
	/// directly; callers starting from a CLI entry point should use this overload to share the
	/// root-config lookup.
	/// </summary>
	public static void EnsureTerminalWidthFromConfig(bool simplified = false)
	{
		var rootConfig = Program.LoadAppConfigRoot();
		var autoExpand = rootConfig != null && rootConfig.TryGetBool("autoExpandTerminal", out var ae) && ae;
		EnsureTerminalWidth(simplified, autoExpand);
	}

	/// <summary>
	/// Ensures the terminal is wide enough for report tables. Prompts the user to resize if not.
	/// </summary>
	public static void EnsureTerminalWidth(bool simplified = false, bool autoExpand = false)
	{
		var minimumWidth = simplified ? SimplifiedMinWidth : DetailedMinWidth;

		int currentWidth;
		try { currentWidth = Console.WindowWidth; }
		catch { return; } // Can't determine width (e.g. WSL interop, piped output), proceed optimistically

		if (currentWidth >= minimumWidth)
			return;

		if (!autoExpand && !AnsiConsole.Confirm($"Your terminal is [yellow]{currentWidth}[/] columns wide. This report displays best at [green]{minimumWidth}+[/] columns. Expand terminal to {minimumWidth} columns?", defaultValue: true))
			return;

		if (!TryResize(minimumWidth, Console.WindowHeight))
			AnsiConsole.MarkupLine("[yellow]Warning:[/] Could not resize terminal. Report may appear wrapped.");
	}

	private static bool TryResize(int columns, int rows)
	{
		// Try xterm escape sequence first — actually resizes the window in Windows Terminal, xterm, and most modern emulators.
		// Console.SetWindowSize only changes the logical buffer view, not the actual terminal window.
		try
		{
			Console.Write($"\x1b[8;{rows};{columns}t");
			Thread.Sleep(100);
			if (Console.WindowWidth >= columns)
				return true;
		}
		catch { }

		// Fallback: legacy Console API (works in conhost.exe / older Windows console)
		if (OperatingSystem.IsWindows())
		{
			try
			{
				Console.SetBufferSize(Math.Max(Console.BufferWidth, columns), Console.BufferHeight);
				Console.SetWindowSize(columns, Console.WindowHeight);
				return Console.WindowWidth >= columns;
			}
			catch { }
		}

		return false;
	}
}
