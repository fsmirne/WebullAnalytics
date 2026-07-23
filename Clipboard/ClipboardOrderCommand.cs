using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;

namespace WebullAnalytics.Clipboard;

internal sealed class ClipboardOrderSettings : CommandSettings
{
	[CommandOption("--path <FILE>")]
	[Description("Read the ticket image from a file instead of the clipboard (testing / automation).")]
	public string? Path { get; init; }

	[CommandOption("--rows")]
	[Description("Also print the raw OCR rows (debugging a misparse).")]
	public bool ShowRows { get; init; }

	[CommandOption("--scale <N>", IsHidden = true)]
	[Description("Force a single OCR upscale factor instead of the multi-scale ensemble (debugging).")]
	public int? Scale { get; init; }

	[CommandOption("--dump-clip <FILE>", IsHidden = true)]
	[Description("Write the raw image obtained from the clipboard/file to a file (debugging the clipboard adapter).")]
	public string? DumpClip { get; init; }

	[CommandOption("--dump-pre <FILE>", IsHidden = true)]
	[Description("Write the preprocessed (upscaled/inverted) image to a file for OCR debugging.")]
	public string? DumpPre { get; init; }
}

/// <summary>`wa clipboard order` — OCR a snipped Webull order ticket straight off the clipboard and print
/// the matching `wa trade place` + `wa analyze trade` lines (via <see cref="ReproductionCommands"/>, the
/// same generator the scan/watch proposal panels use, so the shapes can never drift).
///
/// Money-adjacent OCR is guarded twice: the ticket's redundant header fields are cross-checked against the
/// leg rows (any mismatch is shown loudly and suppresses the place line), and `wa trade place` itself stays
/// a dry-run unless the user adds --submit by hand — this command never prints --submit.</summary>
internal sealed class ClipboardOrderCommand : Command<ClipboardOrderSettings>
{
	protected override int Execute(CommandContext context, ClipboardOrderSettings settings, CancellationToken cancellation)
	{
		byte[]? image = settings.Path != null ? File.ReadAllBytes(settings.Path) : ClipboardImage.TryRead();
		if (image == null) return 1;
		using (var probe = SkiaSharp.SKBitmap.Decode(image))
			Console.WriteLine($"[image: {image.Length:N0} bytes, {(probe == null ? "UNDECODABLE" : $"{probe.Width}x{probe.Height}")}, source {(settings.Path != null ? "file" : "clipboard")}]");
		if (settings.DumpClip != null) File.WriteAllBytes(settings.DumpClip, image);
		if (settings.DumpPre != null) File.WriteAllBytes(settings.DumpPre, ImagePreprocess.ForOcr(image, settings.Scale ?? 3));

		// Multi-scale ensemble: each upscale factor misreads different rows of the dark-theme ticket, so we
		// OCR at several and merge (union of legs, conflicts flagged). --scale forces a single pass.
		// (scale, psm, greenChannel): green passes read the ClearType-fringed backbone (all but red text),
		// max-channel passes recover the red words. See ImagePreprocess.
		// Dimensions: channel (green = ClearType backbone, max = red text) x psm (4 = layout, 11 = sparse) x
		// grid-lines (removed = frees boxed rows, kept = some rows read better boxed). Union over all.
		var passes = settings.Scale.HasValue
			? new[] { (settings.Scale.Value, 4, false, true) }
			: [(3, 4, true, true), (3, 11, true, true), (3, 4, false, true), (3, 11, false, true), (3, 4, true, false), (3, 11, true, false), (3, 4, false, false), (3, 11, false, false)];
		var parses = new List<TicketParse>();
		foreach (var (sc, psm, green, lines) in passes)
		{
			var words = TesseractOcr.TryRecognize(image, sc, psm, green, lines);
			if (words == null) return 1;   // tesseract missing/broken — hint already printed
			if (words.Count == 0) continue;
			var rows = WebullTicketParser.ClusterRows(words);
			if (settings.ShowRows) { Console.WriteLine($"  -- scale {sc} psm {psm}{(green ? " green" : " max")}{(lines ? " nolines" : " lines")}:"); foreach (var r in rows) Console.WriteLine($"  | {r}"); }
			parses.Add(WebullTicketParser.Parse(rows));
		}
		if (parses.Count == 0) { Console.WriteLine("OCR found no text in the image."); return 1; }
		var parse = parses.Count == 1 ? parses[0] : WebullTicketParser.Merge(parses);

		if (parse.Legs.Count == 0)
		{
			Console.WriteLine("Could not parse the ticket. OCR rows:");
			foreach (var r in parse.RowTexts) Console.WriteLine($"  | {r}");
			return 1;
		}

		var tifText = parse.Tif == "gtc" ? "GTC" : "Day";
		AnsiConsole.MarkupLine($"Parsed: [bold]{parse.Legs.Count}[/] leg(s), net limit {(parse.NetLimit.HasValue ? $"[bold]${parse.NetLimit.Value:0.00}[/]" : "[red]n/a[/]")}, TIF {tifText}");
		foreach (var l in parse.Legs)
			Console.WriteLine($"  {l.Action,-4} {l.Symbol} {l.Expiry:yyyy-MM-dd} {l.Strike}{l.CallPut} x{l.Qty}   [{l.OccSymbol}]");

		foreach (var w in parse.Warnings) AnsiConsole.MarkupLine($"[yellow]  ⚠ {Markup.Escape(w)}[/]");

		if (parse.Problems.Count > 0)
		{
			AnsiConsole.MarkupLine("[red]CROSS-CHECK FAILED — re-snip the ticket or fix by hand; commands below are NOT trustworthy:[/]");
			foreach (var p in parse.Problems) AnsiConsole.MarkupLine($"[red]  ! {Markup.Escape(p)}[/]");
		}

		// Sell legs first — the convention every other command generator in the app uses.
		var ordered = parse.Legs.OrderBy(l => l.Action == "sell" ? 0 : 1)
			.Select(l => new ProposalLeg(l.Action, l.OccSymbol, l.Qty)).ToList();
		Console.WriteLine();
		foreach (var line in ReproductionCommands.Build(ordered, parse.NetLimit, parse.Tif, emitPlace: parse.Problems.Count == 0))
			Console.WriteLine(line);
		Console.WriteLine();
		Console.WriteLine("(trade place is a dry-run by default; add --submit yourself after reviewing.)");
		return parse.Problems.Count == 0 ? 0 : 3;
	}
}
