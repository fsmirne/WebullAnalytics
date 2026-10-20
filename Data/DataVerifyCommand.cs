using Microsoft.Data.Sqlite;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace WebullAnalytics.Data;

internal sealed class DataVerifySettings : DataDbSettings
{
	[CommandOption("--roots <LIST>")]
	[Description("Comma-separated roots to report. Default: every root in the store.")]
	public string? Roots { get; set; }

	[CommandOption("--full")]
	[Description("Scan the WHOLE table per root (rows, trade days, date range, crossed quotes) — slow on the multi-GB store.")]
	public bool Full { get; set; }
}

/// <summary>`wa data verify` — the post-backfill coverage + crossed-quote check of <c>data/quotes.db</c> (the last step of the
/// nightly). The default is cheap, pure primary-key seeks with no scan: per root, its newest expiry, that expiry's newest session, and
/// the row and crossed-quote (bid &gt; ask) counts of that one day's chain — confirming coverage advanced and the freshest slice is
/// sane. A date-window query would still pull SPY's fat 60-DTE forward expiries. <c>--full</c> is the whole-table report.</summary>
internal sealed class DataVerifyCommand : Command<DataVerifySettings>
{
	protected override int Execute(CommandContext context, DataVerifySettings settings, CancellationToken cancellation)
	{
		using var conn = DataDb.Open(settings.DbPath);
		if (conn == null) return 1;

		List<string> present;
		try { present = Roots(conn); }
		catch (SqliteException ex)
		{
			Console.WriteLine($"verify: no quotes table ({ex.Message})");
			return 1;
		}
		var wanted = settings.Roots?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToUpperInvariant());
		var roots = wanted == null ? present : wanted.Where(present.Contains).ToList();   // in the order asked for

		if (settings.Full)
		{
			Console.WriteLine("[full-table scan]");
			Console.WriteLine($"{"root",-6} {"rows",14} {"trade-days",11} {"min",10} {"max",10} {"crossed",9}");
			foreach (var root in roots)
			{
				var r = Row(conn, "SELECT COUNT(*), COUNT(DISTINCT date), MIN(date), MAX(date), SUM(CASE WHEN bid > ask THEN 1 ELSE 0 END) FROM quotes WHERE root=$root", ("$root", root));
				Console.WriteLine($"{root,-6} {r[0],14:N0} {r[1],11:N0} {r[2],10} {r[3],10} {r[4] ?? 0,9:N0}");
			}
			return 0;
		}

		Console.WriteLine("[freshest-slice check]");
		Console.WriteLine($"{"root",-6} {"max_expiry",11} {"latest_date",11} {"slice_rows",11} {"crossed",9}");
		foreach (var root in roots)
		{
			if (Row(conn, "SELECT MAX(expiry) FROM quotes WHERE root=$root", ("$root", root))[0] is not long maxExpiry)
			{
				Console.WriteLine($"{root,-6} {"(empty)",11}");
				continue;
			}
			var maxDate = (long)Row(conn, "SELECT MAX(date) FROM quotes WHERE root=$root AND expiry=$exp", ("$root", root), ("$exp", maxExpiry))[0]!;
			var r = Row(conn, "SELECT COUNT(*), SUM(CASE WHEN bid > ask THEN 1 ELSE 0 END) FROM quotes WHERE root=$root AND expiry=$exp AND date=$date", ("$root", root), ("$exp", maxExpiry), ("$date", maxDate));
			Console.WriteLine($"{root,-6} {maxExpiry,11} {maxDate,11} {r[0] ?? 0L,11:N0} {r[1] ?? 0L,9:N0}");
		}
		return 0;
	}

	/// <summary>Every root in the store, in order, by a loose index scan: one primary-key seek per root. SELECT DISTINCT root has no
	/// index to skip with and scans the whole multi-GB table.</summary>
	private static List<string> Roots(SqliteConnection conn)
	{
		var roots = new List<string>();
		for (var next = Row(conn, "SELECT MIN(root) FROM quotes")[0] as string; next != null; next = Row(conn, "SELECT MIN(root) FROM quotes WHERE root > $prev", ("$prev", next))[0] as string)
			roots.Add(next);
		return roots;
	}

	/// <summary>The single result row, NULLs as null.</summary>
	private static object?[] Row(SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
		using var reader = cmd.ExecuteReader();
		reader.Read();
		return [.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))];
	}
}
