using WebullAnalytics.AI.Events;
using Xunit;

namespace WebullAnalytics.Tests.AI.Events;

public class EventCalendarLoaderTests
{
	[Fact]
	public void ParsesOverrideEntries()
	{
		var json = """
		{
		  "AAPL": { "earnings": "2026-08-01", "earningsTime": "AMC", "exDividend": "2026-08-09", "dividendAmount": 0.24 },
		  "msft": { "earnings": "2026-07-23" }
		}
		""";
		var map = EventCalendarLoader.ParseOverrides(json);

		Assert.True(map.ContainsKey("AAPL"));
		Assert.True(map.ContainsKey("MSFT"));

		var aapl = map["AAPL"];
		Assert.Equal(new DateTime(2026, 8, 1), aapl.NextEarningsDate);
		Assert.Equal("AMC", aapl.EarningsTime);
		Assert.Equal(new DateTime(2026, 8, 9), aapl.NextExDividendDate);
		Assert.Equal(0.24m, aapl.DividendAmount);

		var msft = map["MSFT"];
		Assert.Equal(new DateTime(2026, 7, 23), msft.NextEarningsDate);
		Assert.Null(msft.NextExDividendDate);
	}

	[Fact]
	public void ReturnsEmptyMapOnMalformedJson()
	{
		Assert.Empty(EventCalendarLoader.ParseOverrides(""));
	}

	[Fact]
	public void IgnoresBadDateStrings()
	{
		var json = """{"AAPL":{"earnings":"not-a-date"}}""";
		var map = EventCalendarLoader.ParseOverrides(json);
		Assert.Single(map);
		Assert.Null(map["AAPL"].NextEarningsDate);
	}

	[Fact]
	public void UppercasesKeys()
	{
		var map = EventCalendarLoader.ParseOverrides("""{"aapl":{"earnings":"2026-08-01"}}""");
		Assert.True(map.ContainsKey("AAPL"));
		Assert.Equal("AAPL", map["AAPL"].Ticker);
	}
}
