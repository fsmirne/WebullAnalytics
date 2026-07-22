using System.Diagnostics;

namespace WebullAnalytics.Clipboard;

/// <summary>Reads an image off the system clipboard as PNG bytes, platform-agnostically: no UI-framework
/// dependency in the app — each OS's stock tooling is invoked as a subprocess and the bytes stay in memory.
///   Windows: PowerShell (always present) reads the clipboard image and emits it as base64 on stdout.
///   Linux:   wl-paste (Wayland) or xclip (X11) emit PNG bytes directly.
///   macOS:   pngpaste (brew install pngpaste).
/// Returns null with a printed hint when the clipboard has no image or the platform tool is missing.</summary>
internal static class ClipboardImage
{
	public static byte[]? TryRead()
	{
		if (OperatingSystem.IsWindows()) return TryWindows();
		if (OperatingSystem.IsMacOS()) return TryRun("pngpaste", "-", hint: "install it with: brew install pngpaste");
		// Linux: Wayland first when the session advertises it, else X11.
		if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
			return TryRun("wl-paste", "--type image/png", hint: "install it with: apt install wl-clipboard");
		return TryRun("xclip", "-selection clipboard -t image/png -o", hint: "install it with: apt install xclip");
	}

	private static byte[]? TryWindows()
	{
		// -STA: the clipboard is an STA-only API. The image is re-encoded to PNG in the PowerShell process and
		// shipped back as base64 so nothing touches disk.
		const string script = "$img=[System.Windows.Forms.Clipboard]::GetImage(); if($img -eq $null){exit 2}; $ms=New-Object System.IO.MemoryStream; $img.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); [Convert]::ToBase64String($ms.ToArray())";
		var psi = new ProcessStartInfo("powershell", $"-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; {script}\"")
		{ RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		using var p = Process.Start(psi);
		if (p == null) { Console.WriteLine("Could not start powershell for the clipboard read."); return null; }
		var b64 = p.StandardOutput.ReadToEnd().Trim();
		p.WaitForExit();
		if (p.ExitCode == 2 || b64.Length == 0) { Console.WriteLine("No image in the clipboard — snip the Webull order ticket first (Win+Shift+S)."); return null; }
		try { return Convert.FromBase64String(b64); }
		catch (FormatException) { Console.WriteLine("Clipboard read returned unexpected output."); return null; }
	}

	private static byte[]? TryRun(string tool, string args, string hint)
	{
		var psi = new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		try
		{
			using var p = Process.Start(psi);
			if (p == null) return null;
			using var ms = new MemoryStream();
			p.StandardOutput.BaseStream.CopyTo(ms);
			p.WaitForExit();
			if (p.ExitCode != 0 || ms.Length == 0) { Console.WriteLine($"No image in the clipboard (per {tool}) — snip the Webull order ticket first."); return null; }
			return ms.ToArray();
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Console.WriteLine($"'{tool}' not found — {hint}");
			return null;
		}
	}
}
