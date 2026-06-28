using Spectre.Console;

namespace WebullAnalytics.Utils;

/// <summary>
/// Checks terminal width before rendering wide console tables and offers to resize if too narrow.
/// </summary>
static class TerminalHelper
{
	public const int DetailedMinWidth = 200;
	public const int SimplifiedMinWidth = 130;

	/// <summary>Foreground color that stays legible on a given Spectre background color word — used for the
	/// tick marker in gauge bars (analyze sentiment / regime / …). Light backgrounds (yellow, grey85)
	/// get a black marker; dark ones (red, green, blue) get white. Without this a `white` marker washes out
	/// against the yellow/grey85 segments of a red→green scale.</summary>
	public static string ContrastingForeground(string background) => background switch
	{
		"yellow" or "grey85" or "white" or "silver" => "Gray15",
		_ => "white",
	};

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
		int? maxWidth = rootConfig != null && rootConfig.TryGetInt32("terminalWidth", out var tw) ? tw : null;
		EnsureTerminalWidth(simplified, autoExpand, maxWidth);
	}

	/// <summary>
	/// Ensures the terminal is wide enough for report tables. Prompts the user to resize if not.
	/// <paramref name="maxWidth"/> (from the 'terminalWidth' config key) caps the target so the app never
	/// tries to widen the terminal beyond what the user's display can physically show.
	/// </summary>
	public static void EnsureTerminalWidth(bool simplified = false, bool autoExpand = false, int? maxWidth = null)
	{
		var minimumWidth = simplified ? SimplifiedMinWidth : DetailedMinWidth;
		if (maxWidth.HasValue)
			minimumWidth = Math.Min(minimumWidth, maxWidth.Value);

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
