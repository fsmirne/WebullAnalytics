using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class CboeIndexHistoryClientTests
{
	private const string SampleCsv =
		"DATE,OPEN,HIGH,LOW,CLOSE\n" +
		"06/18/2026,17.230000,17.600000,16.350000,16.400000\n" +
		"06/19/2026,17.040000,17.270000,16.720000,16.780000\n" +   // Juneteenth: NYSE holiday with a CBOE GTH print — must be dropped
		"06/22/2026,17.480000,17.920000,16.490000,17.280000\n" +
		"garbage,line,with,not,enough\n" +
		"06/23/2026,bad,17.920000,16.490000,17.280000\n";

	[Fact]
	public void ParseHistoryCsvDropsHolidayPrintsAndMalformedRows()
	{
		var map = CboeIndexHistoryClient.ParseHistoryCsv(SampleCsv, new DateTime(2026, 6, 1), new DateTime(2026, 7, 1));

		Assert.Equal(2, map.Count);
		var bar = map[new DateTime(2026, 6, 18)];
		Assert.Equal(17.23m, bar.Open);
		Assert.Equal(17.60m, bar.High);
		Assert.Equal(16.35m, bar.Low);
		Assert.Equal(16.40m, bar.Close);
		Assert.Equal(bar.Close, bar.AdjClose);
		Assert.Null(bar.Volume);
		Assert.False(map.ContainsKey(new DateTime(2026, 6, 19)));
		Assert.True(map.ContainsKey(new DateTime(2026, 6, 22)));
	}

	[Fact]
	public void ParseHistoryCsvHonorsWindowWithExclusiveEnd()
	{
		var map = CboeIndexHistoryClient.ParseHistoryCsv(SampleCsv, new DateTime(2026, 6, 22), new DateTime(2026, 6, 22));
		Assert.Empty(map);

		map = CboeIndexHistoryClient.ParseHistoryCsv(SampleCsv, new DateTime(2026, 6, 22), new DateTime(2026, 6, 23));
		Assert.Single(map);
		Assert.True(map.ContainsKey(new DateTime(2026, 6, 22)));
	}

	[Fact]
	public void VixFamilyRoutesToCboeButUnderlyingsDoNot()
	{
		Assert.True(CboeIndexHistoryClient.IsCboeSeries("VIX"));
		Assert.True(CboeIndexHistoryClient.IsCboeSeries("vix1d"));
		Assert.True(CboeIndexHistoryClient.IsCboeSeries("VIX9D"));
		Assert.True(CboeIndexHistoryClient.IsCboeSeries("VIX3M"));
		Assert.False(CboeIndexHistoryClient.IsCboeSeries("SPY"));
		Assert.False(CboeIndexHistoryClient.IsCboeSeries("XSP"));
		Assert.False(CboeIndexHistoryClient.IsCboeSeries("SPXW"));
	}
}
