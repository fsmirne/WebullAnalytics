using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;

namespace WebullAnalytics.Data;

/// <summary>`wa data backup` — snapshot the AppData <c>data/</c> directory into a single <c>.tar.gz</c>.
/// Designed for portability: the archive is self-contained and re-hydrates the prod data dir on any
/// machine via <c>wa data restore</c>. tar.gz is used (not zip) because the dataset is dominated by many
/// small text files (CSV/JSON/JSONL) and solid compression typically halves the archive size vs. per-entry
/// deflate — and <c>System.Formats.Tar</c> + <c>GZipStream</c> are both in the .NET BCL, so no third-party
/// deps and no extra tool needed on the destination machine (<c>tar xzf</c> is ubiquitous).</summary>
internal sealed class DataBackupSettings : CommandSettings
{
	[CommandOption("-o|--output <path>")]
	[Description("Output archive path. Default: <BaseDir>/backups/wa-data-<yyyy-MM-dd_HHmmss>.tar.gz")]
	public string? Output { get; set; }
}

internal sealed class DataBackupCommand : AsyncCommand<DataBackupSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, DataBackupSettings settings, CancellationToken cancellation)
	{
		var dataDir = Program.ResolvePath("data");
		if (!Directory.Exists(dataDir))
		{
			AnsiConsole.MarkupLine($"[red]data/ not found at[/] {Markup.Escape(dataDir)}");
			return 1;
		}

		var outputPath = settings.Output ?? DefaultOutputPath();
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

		AnsiConsole.MarkupLine($"[bold]Backing up[/] {Markup.Escape(dataDir)}");
		AnsiConsole.MarkupLine($"  → {Markup.Escape(outputPath)}");

		// Write to a sibling .tmp first and atomic-rename on success. A killed/crashed backup never
		// leaves a half-written .tar.gz that looks restorable but isn't.
		var tmpPath = outputPath + ".tmp";
		long uncompressedBytes = 0;
		int fileCount = 0;
		try
		{
			await using (var fileStream = File.Create(tmpPath))
			await using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
			await using (var tarWriter = new TarWriter(gzipStream, leaveOpen: false))
			{
				foreach (var path in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
				{
					cancellation.ThrowIfCancellationRequested();
					// Use forward slashes in the archive regardless of OS — tar's portable convention.
					var rel = Path.GetRelativePath(dataDir, path).Replace('\\', '/');
					await tarWriter.WriteEntryAsync(path, "data/" + rel, cancellation);
					uncompressedBytes += new FileInfo(path).Length;
					fileCount++;
				}
			}
			File.Move(tmpPath, outputPath, overwrite: true);
		}
		catch
		{
			if (File.Exists(tmpPath)) { try { File.Delete(tmpPath); } catch { } }
			throw;
		}

		var compressedBytes = new FileInfo(outputPath).Length;
		var ratio = uncompressedBytes > 0 ? (double)compressedBytes / uncompressedBytes : 0;
		AnsiConsole.MarkupLine($"  [green]wrote {fileCount} file(s)[/] — {FormatBytes(uncompressedBytes)} → {FormatBytes(compressedBytes)} ({ratio:P1} of original)");
		return 0;
	}

	private static string DefaultOutputPath()
	{
		var backupsDir = Path.Combine(Program.BaseDir, "backups");
		var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
		return Path.Combine(backupsDir, $"wa-data-{stamp}.tar.gz");
	}

	internal static string FormatBytes(long b)
	{
		if (b < 1024) return $"{b} B";
		if (b < 1024L * 1024) return $"{b / 1024.0:F1} KB";
		if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
		return $"{b / (1024.0 * 1024 * 1024):F2} GB";
	}
}

/// <summary>`wa data restore` — inverse of `wa data backup`. Defaults to the most-recent
/// <c>wa-data-*.tar.gz</c> in <c><BaseDir>/backups/</c>. Restore is atomic: extracts to a staging
/// directory first, then swaps in. If <c>data/</c> already exists, the command refuses unless
/// <c>--force</c> is passed; with <c>--force</c>, the existing dir is renamed to
/// <c>data.bak.<timestamp>/</c> so the old state is recoverable.</summary>
internal sealed class DataRestoreSettings : CommandSettings
{
	[CommandOption("-i|--input <path>")]
	[Description("Archive path to restore. Default: most recent wa-data-*.tar.gz in <BaseDir>/backups/")]
	public string? Input { get; set; }

	[CommandOption("--force")]
	[Description("If data/ already exists, rename it to data.bak.<timestamp>/ and restore over it. Without --force, restore refuses to overwrite existing data.")]
	public bool Force { get; set; }
}

internal sealed class DataRestoreCommand : AsyncCommand<DataRestoreSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, DataRestoreSettings settings, CancellationToken cancellation)
	{
		var inputPath = settings.Input ?? FindLatestBackup();
		if (inputPath == null)
		{
			var backupsDir = Path.Combine(Program.BaseDir, "backups");
			AnsiConsole.MarkupLine($"[red]no backups found[/] in {Markup.Escape(backupsDir)} — pass --input to specify one");
			return 1;
		}
		if (!File.Exists(inputPath))
		{
			AnsiConsole.MarkupLine($"[red]archive not found[/]: {Markup.Escape(inputPath)}");
			return 1;
		}

		var dataDir = Program.ResolvePath("data");
		var parent = Path.GetDirectoryName(dataDir)!;
		Directory.CreateDirectory(parent);

		if (Directory.Exists(dataDir) && !settings.Force)
		{
			AnsiConsole.MarkupLine($"[red]refusing to overwrite[/] {Markup.Escape(dataDir)} — pass --force to move it aside and restore");
			return 1;
		}

		AnsiConsole.MarkupLine($"[bold]Restoring[/] {Markup.Escape(inputPath)}");
		AnsiConsole.MarkupLine($"  → {Markup.Escape(dataDir)}");

		// Stage to a sibling dir first so a corrupt/truncated archive can't trash the existing data
		// before we've confirmed the extraction succeeded. The staging name is collision-proof via Guid
		// so concurrent restores don't step on each other.
		var stagingDir = Path.Combine(parent, $".wa-restore-staging-{Guid.NewGuid():N}");
		Directory.CreateDirectory(stagingDir);
		try
		{
			await using (var fileStream = File.OpenRead(inputPath))
			await using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
			{
				// TarFile.ExtractToDirectoryAsync (BCL) refuses entries whose paths escape the destination,
				// so we get zip-slip / tar-slip protection for free.
				await TarFile.ExtractToDirectoryAsync(gzipStream, stagingDir, overwriteFiles: true, cancellation);
			}

			var stagedDataDir = Path.Combine(stagingDir, "data");
			if (!Directory.Exists(stagedDataDir))
			{
				AnsiConsole.MarkupLine($"[red]archive does not contain a data/ root[/] — wrong file?");
				return 1;
			}

			if (Directory.Exists(dataDir))
			{
				var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
				var bakDir = dataDir + $".bak.{stamp}";
				Directory.Move(dataDir, bakDir);
				AnsiConsole.MarkupLine($"  [yellow]moved existing data/ →[/] {Markup.Escape(Path.GetFileName(bakDir))}");
			}
			Directory.Move(stagedDataDir, dataDir);

			var fileCount = Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).Count();
			var totalBytes = Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
			AnsiConsole.MarkupLine($"  [green]restored {fileCount} file(s)[/] ({DataBackupCommand.FormatBytes(totalBytes)})");
			return 0;
		}
		finally
		{
			if (Directory.Exists(stagingDir)) { try { Directory.Delete(stagingDir, recursive: true); } catch { } }
		}
	}

	private static string? FindLatestBackup()
	{
		var dir = Path.Combine(Program.BaseDir, "backups");
		if (!Directory.Exists(dir)) return null;
		return Directory.EnumerateFiles(dir, "wa-data-*.tar.gz")
			.OrderByDescending(f => File.GetLastWriteTimeUtc(f))
			.FirstOrDefault();
	}
}
