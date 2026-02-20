using Spectre.Console;

namespace WebullAnalytics;

/// <summary>
/// Checks terminal width before rendering wide console tables and offers to resize if too narrow.
/// </summary>
static class TerminalHelper
{
	public const int DetailedMinWidth = 185;
	public const int SimplifiedMinWidth = 130;

	/// <summary>
	/// Ensures the terminal is wide enough for report tables. Prompts the user to resize if not.
	/// Returns true in all cases (resize success, user declined, or detection failure) so rendering always proceeds.
	/// </summary>
	public static bool EnsureTerminalWidth(bool simplified = false)
	{
		var minimumWidth = simplified ? SimplifiedMinWidth : DetailedMinWidth;

		int currentWidth;
		try { currentWidth = Console.WindowWidth; }
		catch { return true; } // Can't determine width (e.g. WSL interop, piped output), proceed optimistically

		if (currentWidth >= minimumWidth)
			return true;

		var proceed = AnsiConsole.Confirm($"Your terminal is [yellow]{currentWidth}[/] columns wide. This report displays best at [green]{minimumWidth}+[/] columns. Expand terminal to {minimumWidth} columns?", defaultValue: true);

		if (!proceed)
			return true;

		if (TryResize(minimumWidth, Console.WindowHeight))
			return true;

		AnsiConsole.MarkupLine("[yellow]Warning:[/] Could not resize terminal. Report may appear wrapped.");
		return true;
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
