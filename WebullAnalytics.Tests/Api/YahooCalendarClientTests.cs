using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class YahooCalendarClientTests
{
	private const string SampleResponse = """
	{
	  "quoteSummary": {
	    "result": [{
	      "calendarEvents": {
	        "earnings": {
	          "earningsDate": [
	            { "raw": 1762128000, "fmt": "2025-11-03" }
	          ],
	          "earningsCallTime": "AMC"
	        },
	        "exDividendDate": { "raw": 1762732800, "fmt": "2025-11-10" }
	      }
	    }],
	    "error": null
	  }
	}
	""";

	[Fact]
	public void ParsesEarningsDateFromRawTimestamp()
	{
		var ev = YahooCalendarClient.ParseResponse("AAPL", SampleResponse);
		Assert.NotNull(ev);
		Assert.Equal("AAPL", ev!.Ticker);
		Assert.Equal(new DateTime(2025, 11, 3), ev.NextEarningsDate);
	}

	[Fact]
	public void ParsesEarningsCallTime()
	{
		var ev = YahooCalendarClient.ParseResponse("AAPL", SampleResponse);
		Assert.Equal("AMC", ev!.EarningsTime);
	}

	[Fact]
	public void ParsesExDividendDate()
	{
		var ev = YahooCalendarClient.ParseResponse("AAPL", SampleResponse);
		Assert.Equal(new DateTime(2025, 11, 10), ev!.NextExDividendDate);
	}

	[Fact]
	public void UppercasesTicker()
	{
		var ev = YahooCalendarClient.ParseResponse("aapl", SampleResponse);
		Assert.Equal("AAPL", ev!.Ticker);
	}

	[Fact]
	public void ReturnsNullOnEmptyResult()
	{
		var ev = YahooCalendarClient.ParseResponse("AAPL", """{"quoteSummary":{"result":[],"error":null}}""");
		Assert.Null(ev);
	}

	[Fact]
	public void ReturnsNullOnMissingCalendarEvents()
	{
		var ev = YahooCalendarClient.ParseResponse("AAPL", """{"quoteSummary":{"result":[{"price":{}}],"error":null}}""");
		Assert.Null(ev);
	}

	[Fact]
	public void ReturnsNullOnMalformedJson()
	{
		Assert.Null(YahooCalendarClient.ParseResponse("AAPL", "not json"));
		Assert.Null(YahooCalendarClient.ParseResponse("AAPL", ""));
	}

	[Fact]
	public void ReturnsNullWhenBothEarningsAndExDivMissing()
	{
		var json = """{"quoteSummary":{"result":[{"calendarEvents":{}}],"error":null}}""";
		Assert.Null(YahooCalendarClient.ParseResponse("AAPL", json));
	}

	[Fact]
	public void ReturnsParsedWhenOnlyEarningsPresent()
	{
		var json = """{"quoteSummary":{"result":[{"calendarEvents":{"earnings":{"earningsDate":[{"raw":1762128000}]}}}],"error":null}}""";
		var ev = YahooCalendarClient.ParseResponse("AAPL", json);
		Assert.NotNull(ev);
		Assert.NotNull(ev!.NextEarningsDate);
		Assert.Null(ev.NextExDividendDate);
	}

	[Fact]
	public void ReturnsParsedWhenOnlyExDivPresent()
	{
		var json = """{"quoteSummary":{"result":[{"calendarEvents":{"exDividendDate":{"raw":1762732800}}}],"error":null}}""";
		var ev = YahooCalendarClient.ParseResponse("AAPL", json);
		Assert.NotNull(ev);
		Assert.Null(ev!.NextEarningsDate);
		Assert.NotNull(ev.NextExDividendDate);
	}
}
