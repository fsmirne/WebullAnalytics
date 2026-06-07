using System;
using System.Collections.Generic;
using System.IO;
using WebullAnalytics.AI.Backtest;
using Xunit;

namespace WebullAnalytics.Tests;

/// <summary>Locks the quotes-only NBBO lookup: CSV parse, latest-at-or-before-minute, bounded staleness,
/// two-sided-only, and missing-contract handling. (The quotes-only price foundation for the pivot.)</summary>
public class QuoteStoreCacheTests
{
	private static string WriteStore(params string[] rows)
	{
		var dir = Path.Combine(Path.GetTempPath(), "qstore_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(dir, "SPY"));
		var lines = new List<string> { "date,time,strike,right,bid,ask,bid_size,ask_size" };
		lines.AddRange(rows);
		File.WriteAllLines(Path.Combine(dir, "SPY", "2026-06-18.csv"), lines);
		return dir;
	}

	// CSV times are already START-of-bar (the pull normalizes ThetaData's end-of-bar stamps -60s at ingest),
	// so the cache reads them as-is — no read-time shift (mirrors how WebullChartsClient normalizes Webull).

	[Fact]
	public void ExactMinute_ReturnsLatestTwoSided()
	{
		var dir = WriteStore(
			"2026-06-18,09:31:00,750,C,2.05,2.07,55,108",
			"2026-06-18,09:32:00,750,C,2.14,2.17,206,145");
		var q = new QuoteStoreCache(dir, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 32, 0));
		Assert.NotNull(q);
		Assert.Equal(2.14m, q!.Value.Bid);
		Assert.Equal(2.17m, q.Value.Ask);
		Assert.Equal(2.155m, q.Value.Mid);
		Assert.Equal(0, q.Value.AgeMinutes);
	}

	[Fact]
	public void StaleWithinWindow_ReturnsPriorQuoteWithAge()
	{
		var dir = WriteStore("2026-06-18,09:31:00,750,C,2.05,2.07,55,108");
		var q = new QuoteStoreCache(dir, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 34, 0));
		Assert.NotNull(q);
		Assert.Equal(3, q!.Value.AgeMinutes);
	}

	[Fact]
	public void StaleBeyondWindow_ReturnsNull()
	{
		var dir = WriteStore("2026-06-18,09:31:00,750,C,2.05,2.07,55,108");
		Assert.Null(new QuoteStoreCache(dir, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 40, 0)));
	}

	[Fact]
	public void OneSidedRowIgnored_NoTwoSidedQuote()
	{
		var dir = WriteStore("2026-06-18,09:31:00,750,C,,2.07,0,108");  // missing bid
		Assert.Null(new QuoteStoreCache(dir, 5).NbboAt("SPY260618C00750000", new DateTime(2026, 6, 18, 9, 32, 0)));
	}

	[Fact]
	public void MissingContract_ReturnsNull()
	{
		var dir = WriteStore("2026-06-18,09:31:00,750,C,2.05,2.07,55,108");
		Assert.Null(new QuoteStoreCache(dir, 5).NbboAt("SPY260618P00750000", new DateTime(2026, 6, 18, 9, 32, 0)));
	}
}
