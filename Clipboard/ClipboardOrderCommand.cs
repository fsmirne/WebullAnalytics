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
		if (settings.DumpPre != null) File.WriteAllBytes(settings.DumpPre, ImagePreprocess.ForOcr(image));

		var words = TesseractOcr.TryRecognize(image);
		if (words == null) return 1;
		if (words.Count == 0) { Console.WriteLine("OCR found no text in the image."); return 1; }

		var rows = WebullTicketParser.ClusterRows(words);
		if (settings.ShowRows) foreach (var r in rows) Console.WriteLine($"  | {r}");
		var parse = WebullTicketParser.Parse(rows);

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
