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

	[Fact]
	public void ParsesPerPaymentDividendAmountFromSummaryDetail()
	{
		// summaryDetail.dividendRate is the forward ANNUAL rate; we surface a quarterly per-payment estimate.
		var json = """{"quoteSummary":{"result":[{"calendarEvents":{"exDividendDate":{"raw":1762732800}},"summaryDetail":{"dividendRate":{"raw":7.0}}}],"error":null}}""";
		var ev = YahooCalendarClient.ParseResponse("SPY", json);
		Assert.NotNull(ev);
		Assert.Equal(1.75m, ev!.DividendAmount); // 7.0 / 4
	}

	[Fact]
	public void DividendAmountNullWhenSummaryDetailAbsent()
	{
		var ev = YahooCalendarClient.ParseResponse("AAPL", SampleResponse);
		Assert.Null(ev!.DividendAmount);
	}

	// Real SPY chart events=div shape: four quarterly ex-dates (2025-06-20 … 2026-03-17), ~91d apart.
	private const string ChartDivResponse = """
	{"chart":{"result":[{"meta":{},"events":{"dividends":{
	  "1750426200":{"amount":1.761,"date":1750426200},
	  "1758288600":{"amount":1.831,"date":1758288600},
	  "1766154600":{"amount":1.993,"date":1766154600},
	  "1774013400":{"amount":1.797,"date":1774013400}
	}}}],"error":null}}
	""";

	[Fact]
	public void ProjectsNextExDateForwardFromHistory()
	{
		// asOf 2026-06-03: most recent ex-date is ~2026-03-17; projecting one ~91d quarter lands in June,
		// between a June-12 short and a June-26 long. Amount is the most recent actual dividend.
		var next = YahooCalendarClient.ParseNextDividendFromChart(ChartDivResponse, new DateTime(2026, 6, 3));
		Assert.NotNull(next);
		Assert.Equal(1.797m, next!.Value.Amount);
		Assert.True(next.Value.ExDate >= new DateTime(2026, 6, 3), "projected ex-date must be on/after asOf");
		Assert.True(next.Value.ExDate < new DateTime(2026, 7, 1), "projected ex-date should land in the next quarterly window");
	}

	[Fact]
	public void ParseNextDividendFromChart_NoDividends_ReturnsNull()
	{
		Assert.Null(YahooCalendarClient.ParseNextDividendFromChart("""{"chart":{"result":[{"meta":{}}],"error":null}}""", DateTime.Today));
		Assert.Null(YahooCalendarClient.ParseNextDividendFromChart("not json", DateTime.Today));
	}

	[Fact]
	public void ParseDividendHistoryFromChart_ReturnsFullActualSchedule_OldestFirst()
	{
		// Unlike ParseNextDividendFromChart (keeps only the latest + projects forward), the backtest source
		// keeps every ACTUAL payment, oldest-first, with the real ex-date and amount.
		var history = YahooCalendarClient.ParseDividendHistoryFromChart(ChartDivResponse);
		Assert.Equal(4, history.Count);
		Assert.Equal(new DateTime(2025, 6, 20), history[0].ExDate);
		Assert.Equal(1.761m, history[0].Amount);
		Assert.Equal(new DateTime(2026, 3, 20), history[^1].ExDate);
		Assert.Equal(1.797m, history[^1].Amount);
		// Strictly ascending by ex-date.
		for (var i = 1; i < history.Count; i++)
			Assert.True(history[i].ExDate > history[i - 1].ExDate);
	}

	[Fact]
	public void ParseDividendHistoryFromChart_NonPayerOrMalformed_ReturnsEmpty()
	{
		Assert.Empty(YahooCalendarClient.ParseDividendHistoryFromChart("""{"chart":{"result":[{"meta":{}}],"error":null}}"""));
		Assert.Empty(YahooCalendarClient.ParseDividendHistoryFromChart("not json"));
		Assert.Empty(YahooCalendarClient.ParseDividendHistoryFromChart(""));
	}
}
