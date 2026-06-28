using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests;

/// <summary>Locks the quotes-only NBBO lookup against the canonical SQLite store: latest-at-or-before-minute,
/// bounded staleness, two-sided-only, and missing-contract handling. (The quotes-only price foundation for
/// the pivot — there is no CSV read path.)</summary>
public class QuoteStoreCacheTests
{
	// Canonical schema — must match scripts/import_quotes_sqlite.py / QuoteStoreWriter exactly.
	private const string SchemaSql =
		"CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, " +
		"strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, " +
		"PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID";

	/// <summary>Builds a temp quotes.db holding the given rows (CSV-shaped "date,time,strike,right,bid,ask,bid_size,ask_size")
	/// for SPY expiry 2026-06-18, applying the import's two-sided filter (BOTH bid>0 AND ask>0) and canonical
	/// encoding (dates yyyymmdd, bid/ask ten-thousandths). Returns the db path.</summary>
	private static string WriteStore(params string[] rows)
	{
		var dir = Path.Combine(Path.GetTempPath(), "qstore_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var dbPath = Path.Combine(dir, "quotes.db");
		using var conn = new SqliteConnection($"Data Source={dbPath}");
		conn.Open();
		using (var schema = conn.CreateCommand()) { schema.CommandText = SchemaSql; schema.ExecuteNonQuery(); }
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT OR IGNORE INTO quotes VALUES ('SPY',20260618,$date,$sec,$strike,$right,$bid,$ask,$bsz,$asz)";
		foreach (var row in rows)
		{
			var c = row.Split(',');
			if (!decimal.TryParse(c[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var bid) || bid <= 0m) continue;  // two-sided filter
			if (!decimal.TryParse(c[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var ask) || ask <= 0m) continue;
			cmd.Parameters.Clear();
			cmd.Parameters.AddWithValue("$date", YmdInt(c[0]));
			cmd.Parameters.AddWithValue("$sec", SecOfDay(c[1]));
			cmd.Parameters.AddWithValue("$strike", (long)Math.Round(decimal.Parse(c[2], CultureInfo.InvariantCulture) * 1000m));
			cmd.Parameters.AddWithValue("$right", c[3].Trim().ToUpperInvariant());
			cmd.Parameters.AddWithValue("$bid", (long)Math.Round(bid * 10000m));
			cmd.Parameters.AddWithValue("$ask", (long)Math.Round(ask * 10000m));
			cmd.Parameters.AddWithValue("$bsz", int.Parse(c[6], CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$asz", int.Parse(c[7], CultureInfo.InvariantCulture));
			cmd.ExecuteNonQuery();
		}
		return dbPath;
	}

	private static int YmdInt(string yyyyMMdd) => int.Parse(yyyyMMdd[..4] + yyyyMMdd[5..7] + yyyyMMdd[8..10], CultureInfo.InvariantCulture);
	private static int SecOfDay(string hms)
	{
		var p = hms.Split(':');
		var s = p.Length > 2 ? int.Parse(p[2], CultureInfo.InvariantCulture) : 0;
		return int.Parse(p[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(p[1], CultureInfo.InvariantCulture) * 60 + s;
	}

	// Times are already START-of-bar (the pull normalizes ThetaData's end-of-bar stamps -60s at ingest),
	// so the cache reads them as-is — no read-time shift (mirrors how WebullChartsClient normalizes Webull).

	[Fact]
	public void ExactMinute_ReturnsLatestTwoSided()
	{
		var db = WriteStore(
			"2026-06-18,09:31:00,750,C,2.05,2.07,55,108",
			"2026-06-18,09:32:00,750,C,2.14,2.17,206,145");
		var q = new QuoteStoreCache(db, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 32, 0));
		Assert.NotNull(q);
		Assert.Equal(2.14m, q!.Value.Bid);
		Assert.Equal(2.17m, q.Value.Ask);
		Assert.Equal(2.155m, q.Value.Mid);
		Assert.Equal(0, q.Value.AgeMinutes);
	}

	[Fact]
	public void StaleWithinWindow_ReturnsPriorQuoteWithAge()
	{
		var db = WriteStore("2026-06-18,09:31:00,750,C,2.05,2.07,55,108");
		var q = new QuoteStoreCache(db, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 34, 0));
		Assert.NotNull(q);
		Assert.Equal(3, q!.Value.AgeMinutes);
	}

	[Fact]
	public void StaleBeyondWindow_ReturnsNull()
	{
		var db = WriteStore("2026-06-18,09:31:00,750,C,2.05,2.07,55,108");
		Assert.Null(new QuoteStoreCache(db, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 40, 0)));
	}

	[Fact]
	public void OneSidedRowIgnored_NoTwoSidedQuote()
	{
		var db = WriteStore("2026-06-18,09:31:00,750,C,,2.07,0,108");  // missing bid
		Assert.Null(new QuoteStoreCache(db, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 32, 0)));
	}

	[Fact]
	public void MissingContract_ReturnsNull()
	{
		var db = WriteStore("2026-06-18,09:31:00,750,C,2.05,2.07,55,108");
		Assert.Null(new QuoteStoreCache(db, 5).NbboAt("SPY260618P00750000", new DateTime(2026, 6, 18, 9, 32, 0)));
	}
}
