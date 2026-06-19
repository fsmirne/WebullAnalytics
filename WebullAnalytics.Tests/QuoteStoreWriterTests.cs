using Microsoft.Data.Sqlite;
using WebullAnalytics;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests;

public class QuoteStoreWriterTests
{
	[Fact]
	public void WriteTick_StoresTwoSidedWithCanonicalEncoding_SkipsOneSided_IsIdempotent()
	{
		var db = Path.Combine(Path.GetTempPath(), $"qsw_{Guid.NewGuid():N}.db");
		try
		{
			// Two-sided → written; one-sided (no bid) → skipped, matching the reader's both-sides requirement.
			var twoSided = new OptionContractQuote("XSP260619P00740000", null, 0.10m, 0.11m, null, null, null, null, 0.20m, BidSize: 5, AskSize: 7);
			var oneSided = new OptionContractQuote("XSP260619C00755000", null, null, 0.05m, null, null, null, null, 0.20m);

			using (var w = new QuoteStoreWriter(db))
			{
				Assert.Equal(1, w.WriteTick("xsp", "2026-06-19", "11:49:00", new[] { twoSided, oneSided }));  // only the two-sided row
				Assert.Equal(0, w.WriteTick("xsp", "2026-06-19", "11:49:00", new[] { twoSided, oneSided }));  // re-tick → PK dup → INSERT OR IGNORE → 0
			}

			using var c = new SqliteConnection($"Data Source={db}");
			c.Open();
			using var cmd = c.CreateCommand();
			cmd.CommandText = "SELECT root,expiry,date,time_sec,strike_milli,right,bid,ask,bid_size,ask_size FROM quotes";
			using var r = cmd.ExecuteReader();
			Assert.True(r.Read());
			Assert.Equal("XSP", r.GetString(0));        // upper-cased
			Assert.Equal(20260619, r.GetInt32(1));      // expiry yyyymmdd
			Assert.Equal(20260619, r.GetInt32(2));      // date yyyymmdd
			Assert.Equal(11 * 3600 + 49 * 60, r.GetInt32(3));  // 11:49:00 → 42540s
			Assert.Equal(740000L, r.GetInt64(4));       // strike × 1000
			Assert.Equal("P", r.GetString(5));
			Assert.Equal(1000L, r.GetInt64(6));         // 0.10 × 10000
			Assert.Equal(1100L, r.GetInt64(7));         // 0.11 × 10000
			Assert.Equal(5L, r.GetInt64(8));
			Assert.Equal(7L, r.GetInt64(9));
			Assert.False(r.Read(), "only the two-sided quote should be stored");
		}
		finally
		{
			SqliteConnection.ClearAllPools();  // release pooled file handles so the temp DB can be deleted
			foreach (var f in new[] { db, db + "-wal", db + "-shm" })
				try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort temp cleanup */ }
		}
	}
}
