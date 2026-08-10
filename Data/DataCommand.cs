using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;

namespace WebullAnalytics.Data;

/// <summary>`wa data backup` — snapshot the AppData <c>data/</c> directory into a single <c>.tar.gz</c>.
/// By default only the top-level files are archived, minus the quote store (<c>quotes.*</c>) — the
/// irreplaceable state (configs, proposals, orders — ~MBs); <c>--full</c> includes the quote store and
/// the subdirectories too (oi/, intraday/, ... — 100+ GB of market data that can be re-pulled from its
/// providers, far too much to duplicate in a daily backup).
/// Designed for portability: the archive is self-contained and re-hydrates the prod data dir on any
/// machine via <c>wa data restore</c>. tar.gz is used (not zip) because the dataset is dominated by many
/// small text files (CSV/JSON/JSONL) and solid compression typically halves the archive size vs. per-entry
/// deflate — and <c>System.Formats.Tar</c> + <c>GZipStream</c> are both in the .NET BCL, so no third-party
/// deps and no extra tool needed on the destination machine (<c>tar xzf</c> is ubiquitous).</summary>
internal sealed class DataBackupSettings : CommandSettings
{
	[CommandOption("-o|--output <path>")]
	[Description("Output archive path. Default: <BaseDir>/backups/wa-data[[-settings]]-<yyyy-MM-dd_HHmmss>.tar.gz")]
	public string? Output { get; set; }

	[CommandOption("--full")]
	[Description("Also back up the quote store (quotes.*) and the data subdirectories (oi/, intraday/, ... — 100+ GB of re-pullable market data). Default: settings only — the top-level data/ files (configs, proposals, orders) minus quotes.*.")]
	public bool Full { get; set; }
}

internal sealed class DataBackupCommand : AsyncCommand<DataBackupSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, DataBackupSettings settings, CancellationToken cancellation)
	{
		var dataDir = Program.ResolvePath("data");
		if (!Directory.Exists(dataDir))
		{
			AnsiConsole.MarkupLine($"[red]data/ not found at[/] {Markup.Escape(dataDir)}");
			return 1;
		}

		var outputPath = settings.Output ?? DefaultOutputPath(settings.Full);
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

		AnsiConsole.MarkupLine($"[bold]Backing up[/] {Markup.Escape(dataDir)} {(settings.Full ? "[grey](full)[/]" : "[grey](settings only — pass --full to include quotes.* and subdirectories)[/]")}");
		AnsiConsole.MarkupLine($"  → {Markup.Escape(outputPath)}");

		// Pre-scan the file list so the progress bar has a real total to estimate remaining time against.
		var files = Directory.EnumerateFiles(dataDir, "*", settings.Full ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(path => settings.Full || !IsQuotesStoreFile(Path.GetFileName(path))).Select(path => (Path: path, Length: new FileInfo(path).Length)).ToList();
		var uncompressedBytes = files.Sum(f => f.Length);
		var fileCount = files.Count;

		// Write to a sibling .tmp first and atomic-rename on success. A killed/crashed backup never
		// leaves a half-written .tar.gz that looks restorable but isn't.
		var tmpPath = outputPath + ".tmp";
		try
		{
			await AnsiConsole.Progress()
				.Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new TransferSpeedColumn(), new RemainingTimeColumn())
				.StartAsync(async ctx =>
				{
					var progress = ctx.AddTask($"compressing {fileCount:N0} file(s), {FormatBytes(uncompressedBytes)}", maxValue: Math.Max(1, uncompressedBytes));
					await using var fileStream = File.Create(tmpPath);
					await using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
					// Progress is counted on the tar byte flow INTO gzip — approximately the uncompressed payload — because TarWriter offers no per-file callbacks and a single huge entry (quotes.db) would otherwise pin the bar for most of the run.
					await using var countingStream = new ByteProgressStream(gzipStream, advanced => progress.Increment(advanced));
					await using var tarWriter = new TarWriter(countingStream, leaveOpen: false);
					foreach (var (path, _) in files)
					{
						cancellation.ThrowIfCancellationRequested();
						// Use forward slashes in the archive regardless of OS — tar's portable convention.
						var rel = Path.GetRelativePath(dataDir, path).Replace('\\', '/');
						await tarWriter.WriteEntryAsync(path, "data/" + rel, cancellation);
					}
					progress.Value = progress.MaxValue;
				});
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

	/// <summary>The quote store (quotes.db plus its WAL/SHM sidecars) lives at data/ top level but is 100+ GB
	/// of re-pullable market data — exactly what the settings backup exists to avoid. Matched by name so
	/// backup (exclude from the settings payload) and restore (never overlay) agree on what "settings" means.</summary>
	internal static bool IsQuotesStoreFile(string fileName) => fileName.StartsWith("quotes.", StringComparison.OrdinalIgnoreCase);

	internal static string FormatBytes(long b)
	{
		if (b < 1024) return $"{b} B";
		if (b < 1024L * 1024) return $"{b / 1024.0:F1} KB";
		if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
		return $"{b / (1024.0 * 1024 * 1024):F2} GB";
	}
}

/// <summary>`wa data restore` — inverse of `wa data backup`. Defaults to the most-recent
/// <c>wa-data-*.tar.gz</c> in <c><BaseDir>/backups/</c>. On a machine with no <c>data/</c> yet the
/// destination is the OS-canonical prod location (see <see cref="ResolveRestoreBaseDir"/>), not the
/// exe-dir fallback <see cref="Program.BaseDir"/> would give. Restore is atomic: extracts to a staging
/// directory first, then applies. A full archive swaps the whole <c>data/</c> dir in (the existing dir
/// is renamed to <c>data.bak.<timestamp>/</c>); a settings-only payload — a settings backup, or any
/// archive restored with <c>--settings</c> — is OVERLAID instead: only the top-level files are replaced
/// (originals copied to <c>data.bak.<timestamp>/</c>) and the data subdirectories and quote store
/// (<c>quotes.*</c>) are never touched, so restoring a daily settings backup can't displace 100+ GB of
/// market data or replace the live quote store with a stale copy. If <c>data/</c> already exists, either
/// path refuses unless <c>--force</c> is passed.</summary>
internal sealed class DataRestoreSettings : CommandSettings
{
	[CommandOption("-i|--input <path>")]
	[Description("Archive path to restore. Default: most recent wa-data-*.tar.gz in <BaseDir>/backups/")]
	public string? Input { get; set; }

	[CommandOption("--force")]
	[Description("Allow restoring over an existing data/: a full archive moves it to data.bak.<timestamp>/ and swaps in; a settings payload overlays the top-level files (originals backed up there). Without --force, restore refuses to touch existing data.")]
	public bool Force { get; set; }

	[CommandOption("--settings")]
	[Description("Restore only the top-level setting files from the archive, leaving the data subdirectories and the quote store (quotes.*) untouched. Implied when the archive itself is settings-only.")]
	public bool SettingsOnly { get; set; }
}

internal sealed class DataRestoreCommand : AsyncCommand<DataRestoreSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, DataRestoreSettings settings, CancellationToken cancellation)
	{
		var baseDir = ResolveRestoreBaseDir();
		var inputPath = settings.Input ?? FindLatestBackup(baseDir);
		if (inputPath == null)
		{
			var backupsDir = Path.Combine(baseDir, "backups");
			AnsiConsole.MarkupLine($"[red]no backups found[/] in {Markup.Escape(backupsDir)} — pass --input to specify one");
			return 1;
		}
		if (!File.Exists(inputPath))
		{
			AnsiConsole.MarkupLine($"[red]archive not found[/]: {Markup.Escape(inputPath)}");
			return 1;
		}

		var dataDir = Path.Combine(baseDir, "data");
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
			// Progress is counted on COMPRESSED bytes read from the archive: the total is just the file
			// length, it needs no hook into the extraction internals, and gzip consumes its input evenly
			// enough that the remaining-time estimate is honest.
			var archiveBytes = new FileInfo(inputPath).Length;
			await AnsiConsole.Progress()
				.Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new TransferSpeedColumn(), new RemainingTimeColumn())
				.StartAsync(async ctx =>
				{
					var progress = ctx.AddTask($"extracting {DataBackupCommand.FormatBytes(archiveBytes)} archive", maxValue: Math.Max(1, archiveBytes));
					await using var fileStream = File.OpenRead(inputPath);
					await using var countingStream = new ByteProgressStream(fileStream, advanced => progress.Increment(advanced));
					await using var gzipStream = new GZipStream(countingStream, CompressionMode.Decompress);
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
							if (rel.Length == 0 || rel.Contains('/') || DataBackupCommand.IsQuotesStoreFile(rel)) continue; // subdirectory content and the quote store — out of scope by design
							await entry.ExtractToFileAsync(Path.Combine(stagedDataDir, rel), overwrite: true, cancellation);
						}
					}
					else
					{
						// TarFile.ExtractToDirectoryAsync (BCL) refuses entries whose paths escape the destination,
						// so we get zip-slip / tar-slip protection for free.
						await TarFile.ExtractToDirectoryAsync(gzipStream, stagingDir, overwriteFiles: true, cancellation);
					}
					progress.Value = progress.MaxValue;
				});

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
					// Old settings archives (pre quotes.* exclusion) captured the 100+ GB quote store at top
					// level — never let a settings overlay replace the live one with a stale copy.
					if (DataBackupCommand.IsQuotesStoreFile(Path.GetFileName(staged))) continue;
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

			var fileCount = 0;
			long totalBytes = 0;
			foreach (var restoredFile in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories)) { fileCount++; totalBytes += new FileInfo(restoredFile).Length; }
			AnsiConsole.MarkupLine($"  [green]restored {fileCount} file(s)[/] ({DataBackupCommand.FormatBytes(totalBytes)})");
			return 0;
		}
		finally
		{
			if (Directory.Exists(stagingDir)) { try { Directory.Delete(stagingDir, recursive: true); } catch { } }
		}
	}

	/// <summary>Restore exists to CREATE the prod data dir, so unlike <see cref="Program.BaseDir"/> it must not
	/// land in the exe-dir fallback just because <c>data/</c> doesn't exist yet — that's exactly the fresh-machine
	/// migration case the command is for (observed on Linux: restore before install.sh populated
	/// <c>~/.local/bin/data</c>, which every later invocation ignored once the XDG dir appeared). A WA_DATA_DIR
	/// override or an already-resolved data/ next to <see cref="Program.BaseDir"/> wins as usual; otherwise
	/// target the OS-canonical location even though it's empty.</summary>
	private static string ResolveRestoreBaseDir()
	{
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WA_DATA_DIR"))) return Program.BaseDir;
		if (Directory.Exists(Path.Combine(Program.BaseDir, "data"))) return Program.BaseDir;
		return Program.CanonicalBaseDir() ?? Program.BaseDir;
	}

	private static string? FindLatestBackup(string baseDir)
	{
		var dir = Path.Combine(baseDir, "backups");
		if (!Directory.Exists(dir)) return null;
		return Directory.EnumerateFiles(dir, "wa-data-*.tar.gz")
			.OrderByDescending(f => File.GetLastWriteTimeUtc(f))
			.FirstOrDefault();
	}
}

/// <summary>Pass-through stream that reports every byte moved through it. tar+gzip streaming exposes no
/// per-file or per-block callbacks, so this is the only place a progress bar can observe the transfer:
/// backup counts writes (tar bytes into gzip ≈ uncompressed payload), restore counts reads (compressed
/// bytes out of the archive). Non-seekable by design — both sides of a gzip pipe are forward-only.</summary>
internal sealed class ByteProgressStream(Stream inner, Action<long> onBytes) : Stream
{
	public override bool CanRead => inner.CanRead;
	public override bool CanSeek => false;
	public override bool CanWrite => inner.CanWrite;
	public override long Length => inner.Length;
	public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
	public override void Flush() => inner.Flush();
	public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
	public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); onBytes(n); return n; }
	public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); onBytes(n); return n; }
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var n = await inner.ReadAsync(buffer, cancellationToken); onBytes(n); return n; }
	public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); onBytes(count); }
	public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); onBytes(buffer.Length); }
	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
	public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await inner.WriteAsync(buffer, cancellationToken); onBytes(buffer.Length); }
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
	public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
}
