using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace WebullAnalytics.Data;

/// <summary>Shared base for the `wa data` SQLite-maintenance commands (vacuum / optimize / check / stats).
/// All of them act on the canonical minute-NBBO store (<c>data/quotes.db</c>) — the only SQLite database the
/// project keeps — but every command takes <c>--db</c> so the same plumbing works against a copy or a test
/// store. A 60s busy-timeout is set on every connection because the scraper and the ThetaData backfill are
/// concurrent writers; maintenance should wait for a lock rather than error out mid-run.</summary>
internal abstract class DataDbSettings : CommandSettings
{
	[CommandOption("--db <path>")]
	[Description("SQLite database to operate on. Default: data/quotes.db")]
	public string? Db { get; set; }

	public string DbPath => Db ?? Program.ResolvePath("data/quotes.db");
}

internal static class DataDb
{
	/// <summary>Opens a read/write connection with the standard busy-timeout, or prints an error and returns
	/// null if the file is missing. The WAL sidecars (<c>-wal</c>/<c>-shm</c>) are created lazily by SQLite and
	/// are not required to exist.</summary>
	public static SqliteConnection? Open(string dbPath)
	{
		if (!File.Exists(dbPath))
		{
			AnsiConsole.MarkupLine($"[red]database not found[/]: {Markup.Escape(dbPath)}");
			return null;
		}
		var conn = new SqliteConnection($"Data Source={dbPath}");
		conn.Open();
		Exec(conn, "PRAGMA busy_timeout=60000");
		return conn;
	}

	public static void Exec(SqliteConnection conn, string sql)
	{
		using var c = conn.CreateCommand();
		c.CommandText = sql;
		c.ExecuteNonQuery();
	}

	public static long Scalar(SqliteConnection conn, string sql)
	{
		using var c = conn.CreateCommand();
		c.CommandText = sql;
		return Convert.ToInt64(c.ExecuteScalar());
	}

	/// <summary>The on-disk footprint of the store = the main file plus its WAL and shared-memory sidecars.
	/// VACUUM's payoff is only visible if the WAL is counted too, since a checkpoint(TRUNCATE) is what zeroes
	/// the <c>-wal</c> file.</summary>
	public static long TotalBytes(string dbPath)
	{
		long total = 0;
		foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
			if (File.Exists(p)) total += new FileInfo(p).Length;
		return total;
	}
}

/// <summary>`wa data vacuum` — rebuild <c>data/quotes.db</c> to reclaim freelist pages and defragment, then
/// truncate the WAL. VACUUM rewrites the entire file, so on the multi-GB quote store it is slow and needs
/// roughly the database's own size again in free temp space; it therefore skips when there is little to
/// reclaim (pass <c>--force</c> to vacuum anyway, e.g. purely to defragment). Run it when the scraper and the
/// nightly backfill are idle — a concurrent writer's lock will stall the rebuild.</summary>
internal sealed class DataVacuumSettings : DataDbSettings
{
	[CommandOption("--force")]
	[Description("VACUUM even when little space is reclaimable (defragment-only rewrite).")]
	public bool Force { get; set; }
}

internal sealed class DataVacuumCommand : AsyncCommand<DataVacuumSettings>
{
	// Below this much reclaimable freelist, a full rewrite of a multi-GB file isn't worth the time/temp-space.
	private const long ReclaimThresholdBytes = 256L * 1024 * 1024;

	protected override async Task<int> ExecuteAsync(CommandContext context, DataVacuumSettings settings, CancellationToken cancellation)
	{
		using var conn = DataDb.Open(settings.DbPath);
		if (conn == null) return 1;

		var before = DataDb.TotalBytes(settings.DbPath);
		var pageSize = DataDb.Scalar(conn, "PRAGMA page_size");
		var freeList = DataDb.Scalar(conn, "PRAGMA freelist_count");
		var reclaimable = pageSize * freeList;

		AnsiConsole.MarkupLine($"[bold]VACUUM[/] {Markup.Escape(settings.DbPath)} — {DataBackupCommand.FormatBytes(before)} on disk, {DataBackupCommand.FormatBytes(reclaimable)} reclaimable");

		if (reclaimable < ReclaimThresholdBytes && !settings.Force)
		{
			AnsiConsole.MarkupLine($"  [yellow]skipped[/] — under {DataBackupCommand.FormatBytes(ReclaimThresholdBytes)} reclaimable; pass --force to defragment anyway");
			return 0;
		}

		AnsiConsole.MarkupLine("  [grey]rewriting the whole file — this can take several minutes and needs ~the DB's size in free temp space[/]");

		await AnsiConsole.Status().StartAsync("vacuuming…", async _ =>
		{
			await Task.Run(() =>
			{
				DataDb.Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
				DataDb.Exec(conn, "VACUUM");
				DataDb.Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
			}, cancellation);
		});

		var after = DataDb.TotalBytes(settings.DbPath);
		var saved = before - after;
		AnsiConsole.MarkupLine($"  [green]done[/] — {DataBackupCommand.FormatBytes(before)} → {DataBackupCommand.FormatBytes(after)} ([green]freed {DataBackupCommand.FormatBytes(saved)}[/])");
		return 0;
	}
}

/// <summary>`wa data optimize` — refresh the query planner's statistics so per-expiry slice loads keep hitting
/// the WITHOUT-ROWID primary key efficiently. <c>PRAGMA optimize</c> is the cheap, recommended default (it
/// only re-analyzes tables whose stats have drifted); <c>--analyze</c> forces a full <c>ANALYZE</c>, which
/// scans the store and is slow on the multi-GB file but produces the most accurate stats. Unlike vacuum this
/// never rewrites the data, so it is safe to run any time.</summary>
internal sealed class DataOptimizeSettings : DataDbSettings
{
	[CommandOption("--analyze")]
	[Description("Force a full ANALYZE (slow) instead of the incremental PRAGMA optimize.")]
	public bool Analyze { get; set; }
}

internal sealed class DataOptimizeCommand : AsyncCommand<DataOptimizeSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, DataOptimizeSettings settings, CancellationToken cancellation)
	{
		using var conn = DataDb.Open(settings.DbPath);
		if (conn == null) return 1;

		AnsiConsole.MarkupLine($"[bold]optimize[/] {Markup.Escape(settings.DbPath)} — {(settings.Analyze ? "full ANALYZE" : "PRAGMA optimize")}");

		await AnsiConsole.Status().StartAsync("refreshing statistics…", async _ =>
		{
			await Task.Run(() =>
			{
				if (settings.Analyze) DataDb.Exec(conn, "ANALYZE");
				DataDb.Exec(conn, "PRAGMA optimize");
			}, cancellation);
		});

		AnsiConsole.MarkupLine("  [green]done[/] — planner statistics refreshed");
		return 0;
	}
}

/// <summary>`wa data check` — verify <c>data/quotes.db</c> is not corrupt. <c>PRAGMA integrity_check</c> reads
/// every page and validates the b-tree structure (slow on the multi-GB store); <c>--quick</c> runs
/// <c>quick_check</c>, which skips the more expensive index-vs-table cross-checks. A clean store prints "ok"
/// and exits 0; any reported problem is listed and the command exits 1.</summary>
internal sealed class DataCheckSettings : DataDbSettings
{
	[CommandOption("--quick")]
	[Description("Run the faster quick_check (skips index cross-checks) instead of the full integrity_check.")]
	public bool Quick { get; set; }
}

internal sealed class DataCheckCommand : AsyncCommand<DataCheckSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, DataCheckSettings settings, CancellationToken cancellation)
	{
		using var conn = DataDb.Open(settings.DbPath);
		if (conn == null) return 1;

		AnsiConsole.MarkupLine($"[bold]{(settings.Quick ? "quick_check" : "integrity_check")}[/] {Markup.Escape(settings.DbPath)}");

		var problems = new List<string>();
		await AnsiConsole.Status().StartAsync("checking…", async _ =>
		{
			await Task.Run(() =>
			{
				using var c = conn.CreateCommand();
				c.CommandText = settings.Quick ? "PRAGMA quick_check" : "PRAGMA integrity_check";
				using var r = c.ExecuteReader();
				while (r.Read())
				{
					var line = r.GetString(0);
					if (!string.Equals(line, "ok", StringComparison.Ordinal)) problems.Add(line);
				}
			}, cancellation);
		});

		if (problems.Count == 0)
		{
			AnsiConsole.MarkupLine("  [green]ok[/] — no corruption detected");
			return 0;
		}
		AnsiConsole.MarkupLine($"  [red]{problems.Count} problem(s) detected:[/]");
		foreach (var p in problems) AnsiConsole.MarkupLine($"    {Markup.Escape(p)}");
		return 1;
	}
}

/// <summary>`wa data stats` — a health readout for <c>data/quotes.db</c>. The default is all-cheap metadata:
/// on-disk size (file + WAL), page geometry, the freelist (i.e. how much a VACUUM would reclaim), journal
/// mode, and a per-root summary from the small <c>sealed</c> table (finalized expiries). <c>--rows</c> adds
/// the genuine row counts and per-root date coverage, which require a full scan of the multi-GB store and can
/// take minutes.</summary>
internal sealed class DataStatsSettings : DataDbSettings
{
	[CommandOption("--rows")]
	[Description("Also report total/per-root row counts and date coverage (full table scan — slow).")]
	public bool Rows { get; set; }
}

internal sealed class DataStatsCommand : AsyncCommand<DataStatsSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, DataStatsSettings settings, CancellationToken cancellation)
	{
		using var conn = DataDb.Open(settings.DbPath);
		if (conn == null) return 1;

		var pageSize = DataDb.Scalar(conn, "PRAGMA page_size");
		var pageCount = DataDb.Scalar(conn, "PRAGMA page_count");
		var freeList = DataDb.Scalar(conn, "PRAGMA freelist_count");
		var journal = JournalMode(conn);
		var dbBytes = File.Exists(settings.DbPath) ? new FileInfo(settings.DbPath).Length : 0;
		var walBytes = File.Exists(settings.DbPath + "-wal") ? new FileInfo(settings.DbPath + "-wal").Length : 0;
		var reclaimable = pageSize * freeList;
		var fragPct = pageCount > 0 ? (double)freeList / pageCount : 0;

		var grid = new Grid().AddColumn().AddColumn();
		grid.AddRow("[grey]path[/]", Markup.Escape(settings.DbPath));
		grid.AddRow("[grey]file size[/]", DataBackupCommand.FormatBytes(dbBytes));
		grid.AddRow("[grey]wal size[/]", DataBackupCommand.FormatBytes(walBytes));
		grid.AddRow("[grey]journal mode[/]", Markup.Escape(journal));
		grid.AddRow("[grey]page size[/]", $"{pageSize} B");
		grid.AddRow("[grey]pages[/]", $"{pageCount:N0}");
		grid.AddRow("[grey]freelist[/]", $"{freeList:N0} pages ({DataBackupCommand.FormatBytes(reclaimable)} reclaimable, {fragPct:P1})");
		AnsiConsole.Write(grid);

		RenderSealedSummary(conn);

		if (settings.Rows)
		{
			AnsiConsole.MarkupLine("\n[grey]scanning rows (this can take minutes)…[/]");
			await RenderRowCoverage(conn, cancellation);
		}
		return 0;
	}

	private static string JournalMode(SqliteConnection conn)
	{
		using var c = conn.CreateCommand();
		c.CommandText = "PRAGMA journal_mode";
		return c.ExecuteScalar()?.ToString() ?? "?";
	}

	/// <summary>The <c>sealed</c> table is tiny (root, expiry) so this is cheap — it tells you which roots are
	/// present and how many of their expiries have been finalized by the backfill.</summary>
	private static void RenderSealedSummary(SqliteConnection conn)
	{
		using var c = conn.CreateCommand();
		c.CommandText = "SELECT root, COUNT(*), MIN(expiry), MAX(expiry) FROM sealed GROUP BY root ORDER BY root";
		using var r = c.ExecuteReader();
		var table = new Table().Title("[bold]sealed expiries[/]").Border(TableBorder.Rounded);
		table.AddColumn("root");
		table.AddColumn(new TableColumn("expiries").RightAligned());
		table.AddColumn("min");
		table.AddColumn("max");
		var any = false;
		while (r.Read())
		{
			any = true;
			table.AddRow(Markup.Escape(r.GetString(0)), $"{r.GetInt64(1):N0}", r.GetInt64(2).ToString(), r.GetInt64(3).ToString());
		}
		if (any) AnsiConsole.Write(table);
		else AnsiConsole.MarkupLine("[grey](no sealed expiries recorded)[/]");
	}

	private static async Task RenderRowCoverage(SqliteConnection conn, CancellationToken cancellation)
	{
		var table = new Table().Title("[bold]row coverage[/]").Border(TableBorder.Rounded);
		table.AddColumn("root");
		table.AddColumn(new TableColumn("rows").RightAligned());
		table.AddColumn("first date");
		table.AddColumn("last date");

		long total = 0;
		await AnsiConsole.Status().StartAsync("counting…", async _ =>
		{
			await Task.Run(() =>
			{
				using var c = conn.CreateCommand();
				c.CommandText = "SELECT root, COUNT(*), MIN(date), MAX(date) FROM quotes GROUP BY root ORDER BY root";
				using var r = c.ExecuteReader();
				while (r.Read())
				{
					var rows = r.GetInt64(1);
					total += rows;
					table.AddRow(Markup.Escape(r.GetString(0)), $"{rows:N0}", r.GetInt64(2).ToString(), r.GetInt64(3).ToString());
				}
			}, cancellation);
		});

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine($"[grey]total rows:[/] {total:N0}");
	}
}
