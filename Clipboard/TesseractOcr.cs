using System.Diagnostics;
using System.Globalization;

namespace WebullAnalytics.Clipboard;

/// <summary>Runs the Tesseract CLI over an in-memory image and returns word-level boxes. Tesseract is the
/// one OCR engine available on every platform, so requiring it uniformly keeps a single code path — no
/// per-OS engines, no UI-framework references. Image bytes go in on stdin, TSV comes back on stdout;
/// nothing touches disk.
///
/// Install: Windows `winget install tesseract-ocr.tesseract` — Linux `apt install tesseract-ocr` —
/// macOS `brew install tesseract`.</summary>
internal static class TesseractOcr
{
	/// <summary>Windows installers put tesseract.exe here without adding it to PATH.</summary>
	private static readonly string[] WindowsFallbacks =
	[
		@"C:\Program Files\Tesseract-OCR\tesseract.exe",
		@"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
	];

	public static IReadOnlyList<OcrWord>? TryRecognize(byte[] imageBytes)
	{
		var exe = Resolve();
		if (exe == null)
		{
			var install = OperatingSystem.IsWindows() ? "winget install tesseract-ocr.tesseract"
				: OperatingSystem.IsMacOS() ? "brew install tesseract"
				: "sudo apt install tesseract-ocr";
			Console.WriteLine($"tesseract not found. Install it: {install}");
			return null;
		}

		imageBytes = ImagePreprocess.ForOcr(imageBytes);

		// `stdin ... stdout tsv` = read image from stdin, emit word-level TSV on stdout. PSM 4 ("single column of variable-size text") reads the ticket's styled header row reliably where PSM 6 drops it (verified on the preprocessed sample).
		var psi = new ProcessStartInfo(exe, "stdin stdout --psm 4 tsv")
		{ RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		using var p = Process.Start(psi);
		if (p == null) return null;
		p.StandardInput.BaseStream.Write(imageBytes);
		p.StandardInput.Close();
		var tsv = p.StandardOutput.ReadToEnd();
		var err = p.StandardError.ReadToEnd();
		p.WaitForExit();
		if (p.ExitCode != 0) { Console.WriteLine($"tesseract failed (rc={p.ExitCode}): {err.Trim()}"); return null; }

		// TSV columns: level page block par line word left top width height conf text — words are level 5.
		var words = new List<OcrWord>();
		foreach (var line in tsv.Split('\n'))
		{
			var c = line.TrimEnd('\r').Split('\t');
			if (c.Length < 12 || c[0] != "5") continue;
			var text = c[11].Trim();
			if (text.Length == 0) continue;
			words.Add(new OcrWord(text,
				double.Parse(c[6], CultureInfo.InvariantCulture),
				double.Parse(c[7], CultureInfo.InvariantCulture),
				double.Parse(c[9], CultureInfo.InvariantCulture)));
		}
		return words;
	}

	private static string? Resolve()
	{
		// PATH first (all platforms), then the Windows installer's default directory.
		var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
		var names = OperatingSystem.IsWindows() ? new[] { "tesseract.exe" } : new[] { "tesseract" };
		foreach (var dir in pathDirs)
			foreach (var n in names)
			{
				if (dir.Length == 0) continue;
				var full = Path.Combine(dir, n);
				if (File.Exists(full)) return full;
			}
		if (OperatingSystem.IsWindows())
			foreach (var f in WindowsFallbacks)
				if (File.Exists(f)) return f;
		return null;
	}
}
