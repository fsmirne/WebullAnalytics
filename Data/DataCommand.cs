using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;

namespace WebullAnalytics.Data;

/// <summary>`wa data backup` — snapshot the AppData <c>data/</c> directory into a single <c>.tar.gz</c>.
/// By default only the top-level files are archived (configs, proposals, orders — the irreplaceable
/// state, ~MBs); <c>--full</c> includes the subdirectories too (quotes/, oi/, ... — many GB of market
/// data that can be re-pulled from its providers, far too much to duplicate in a daily backup).
/// Designed for portability: the archive is self-contained and re-hydrates the prod data dir on any
/// machine via <c>wa data restore</c>. tar.gz is used (not zip) because the dataset is dominated by many
/// small text files (CSV/JSON/JSONL) and solid compression typically halves the archive size vs. per-entry
/// deflate — and <c>System.Formats.Tar</c> + <c>GZipStream</c> are both in the .NET BCL, so no third-party
/// deps and no extra tool needed on the destination machine (<c>tar xzf</c> is ubiquitous).</summary>
internal sealed class DataBackupSettings : CommandSettings
{
	[CommandOption("-o|--output <path>")]
	[Description("Output archive path. Default: <BaseDir>/backups/wa-data[-settings]-<yyyy-MM-dd_HHmmss>.tar.gz")]
	public string? Output { get; set; }

	[CommandOption("--full")]
	[Description("Also back up the data subdirectories (quotes/, oi/, intraday/, ... — many GB of re-pullable market data). Default: settings only — the top-level data/ files (configs, proposals, orders).")]
	public bool Full { get; set; }
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

		var outputPath = settings.Output ?? DefaultOutputPath(settings.Full);
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

		AnsiConsole.MarkupLine($"[bold]Backing up[/] {Markup.Escape(dataDir)} {(settings.Full ? "[grey](full)[/]" : "[grey](settings only — pass --full to include subdirectories)[/]")}");
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
				foreach (var path in Directory.EnumerateFiles(dataDir, "*", settings.Full ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
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

	private static string DefaultOutputPath(bool full)
	{
		var backupsDir = Path.Combine(Program.BaseDir, "backups");
		var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
		// Settings-only archives carry "-settings" in the name so a daily backup is never mistaken for a
		// full snapshot; both shapes still match restore's wa-data-*.tar.gz default-discovery glob.
		return Path.Combine(backupsDir, $"wa-data{(full ? "" : "-settings")}-{stamp}.tar.gz");
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
/// directory first, then applies. A full archive swaps the whole <c>data/</c> dir in (the existing dir
/// is renamed to <c>data.bak.<timestamp>/</c>); a settings-only payload — a settings backup, or any
/// archive restored with <c>--settings</c> — is OVERLAID instead: only the top-level files are replaced
/// (originals copied to <c>data.bak.<timestamp>/</c>) and the data subdirectories are never touched, so
/// restoring a daily settings backup can't displace many GB of market data. If <c>data/</c> already
/// exists, either path refuses unless <c>--force</c> is passed.</summary>
internal sealed class DataRestoreSettings : CommandSettings
{
	[CommandOption("-i|--input <path>")]
	[Description("Archive path to restore. Default: most recent wa-data-*.tar.gz in <BaseDir>/backups/")]
	public string? Input { get; set; }

	[CommandOption("--force")]
	[Description("Allow restoring over an existing data/: a full archive moves it to data.bak.<timestamp>/ and swaps in; a settings payload overlays the top-level files (originals backed up there). Without --force, restore refuses to touch existing data.")]
	public bool Force { get; set; }

	[CommandOption("--settings")]
	[Description("Restore only the top-level setting files from the archive, leaving the data subdirectories untouched. Implied when the archive itself is settings-only.")]
	public bool SettingsOnly { get; set; }
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
			var stagedDataDir = Path.Combine(stagingDir, "data");
			await using (var fileStream = File.OpenRead(inputPath))
			await using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
			{
				if (settings.SettingsOnly)
				{
					// Extract only the top-level data/ files: a full archive carries many GB of subdirectory
					// market data that --settings must neither stage to disk nor restore. Targets are built
					// from the entry's file NAME only, so hostile paths can't escape the staging dir.
					Directory.CreateDirectory(stagedDataDir);
					await using var tarReader = new TarReader(gzipStream, leaveOpen: false);
					while (await tarReader.GetNextEntryAsync(false, cancellation) is { } entry)
					{
						if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
						var name = entry.Name.Replace('\\', '/');
						if (!name.StartsWith("data/")) continue;
						var rel = name["data/".Length..];
						if (rel.Length == 0 || rel.Contains('/')) continue; // subdirectory content — out of scope by design
						await entry.ExtractToFileAsync(Path.Combine(stagedDataDir, rel), overwrite: true, cancellation);
					}
				}
				else
				{
					// TarFile.ExtractToDirectoryAsync (BCL) refuses entries whose paths escape the destination,
					// so we get zip-slip / tar-slip protection for free.
					await TarFile.ExtractToDirectoryAsync(gzipStream, stagingDir, overwriteFiles: true, cancellation);
				}
			}

			if (!Directory.Exists(stagedDataDir))
			{
				AnsiConsole.MarkupLine($"[red]archive does not contain a data/ root[/] — wrong file?");
				return 1;
			}

			var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
			var bakDir = dataDir + $".bak.{stamp}";

			// A payload with no subdirectories is a settings restore (a settings-only backup, or --settings
			// on a full archive): overlay the top-level files into the existing data/ and leave the
			// subdirectories alone. Swapping the whole dir for a settings archive would displace many GB
			// of market data with a few MB of configs.
			if (!Directory.EnumerateDirectories(stagedDataDir).Any())
			{
				Directory.CreateDirectory(dataDir);
				var replaced = 0;
				var restored = 0;
				foreach (var staged in Directory.EnumerateFiles(stagedDataDir))
				{
					cancellation.ThrowIfCancellationRequested();
					var target = Path.Combine(dataDir, Path.GetFileName(staged));
					if (File.Exists(target))
					{
						Directory.CreateDirectory(bakDir);
						File.Copy(target, Path.Combine(bakDir, Path.GetFileName(staged)), overwrite: true);
						replaced++;
					}
					File.Move(staged, target, overwrite: true);
					restored++;
				}
				AnsiConsole.MarkupLine($"  [green]restored {restored} setting file(s)[/] into data/ — subdirectories untouched");
				if (replaced > 0)
					AnsiConsole.MarkupLine($"  [yellow]{replaced} overwritten file(s) backed up →[/] {Markup.Escape(Path.GetFileName(bakDir))}");
				return 0;
			}

			if (Directory.Exists(dataDir))
			{
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
