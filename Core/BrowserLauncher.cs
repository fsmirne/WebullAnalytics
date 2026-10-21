using System.Diagnostics;

namespace WebullAnalytics;

/// <summary>Opens a URL in the user's default browser. Best-effort: callers print the URL too, so a failure here
/// just means the user opens it themselves.</summary>
internal static class BrowserLauncher
{
	public static void TryOpen(string url)
	{
		try
		{
			if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
			// Browsers spawned via xdg-open inherit the terminal's stdio and spray warnings over interactive
			// prompts, so route the launch through a shell that discards the output.
			else Process.Start(new ProcessStartInfo("sh") { ArgumentList = { "-c", "exec xdg-open \"$1\" >/dev/null 2>&1", "sh", url } });
		}
		catch { /* fall back to the printed URL */ }
	}
}
