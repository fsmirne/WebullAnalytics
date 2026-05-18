using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class WebullChartsClientTests
{
	[Fact]
	public void ParseBarRow_PrimaryOrdering_ReturnsBar()
	{
		// time,open,close,high,low,volume,vwap
		var bar = WebullChartsClient.ParseBarRow("1747837800,5125.50,5128.25,5131.00,5123.75,12345,5127.10");

		Assert.NotNull(bar);
		Assert.Equal(5125.50m, bar.Open);
		Assert.Equal(5128.25m, bar.Close);
		Assert.Equal(5131.00m, bar.High);
		Assert.Equal(5123.75m, bar.Low);
		Assert.Equal(12345, bar.Volume);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1747837800), bar.Timestamp);
	}

	[Fact]
	public void ParseBarRow_AlternateOrdering_FallsBack()
	{
		// time,open,high,low,close,volume — fallback ordering. Constructed so primary fails:
		// primary close=5131 (parts[2]); primary high=5123.75 (parts[3]) which would be < open=5125.5 → invalid.
		var bar = WebullChartsClient.ParseBarRow("1747837800,5125.50,5131.00,5123.75,5128.25,12345");

		Assert.NotNull(bar);
		Assert.Equal(5125.50m, bar.Open);
		Assert.Equal(5131.00m, bar.High);
		Assert.Equal(5123.75m, bar.Low);
		Assert.Equal(5128.25m, bar.Close);
	}

	[Fact]
	public void ParseBarRow_TruncatedRow_ReturnsNull()
	{
		Assert.Null(WebullChartsClient.ParseBarRow("1747837800,5125.50,5128.25"));
	}

	[Fact]
	public void ParseBarRow_InvalidTimestamp_ReturnsNull()
	{
		Assert.Null(WebullChartsClient.ParseBarRow("notatime,5125.50,5128.25,5131.00,5123.75,12345"));
	}

	[Fact]
	public void ParseBarRow_NegativeVolume_ClampsToZero()
	{
		var bar = WebullChartsClient.ParseBarRow("1747837800,5125.50,5128.25,5131.00,5123.75,-1");
		Assert.NotNull(bar);
		Assert.Equal(0, bar.Volume);
	}

	[Fact]
	public void ParseChartsResponse_EmptyArray_ReturnsEmpty()
	{
		var bars = WebullChartsClient.ParseChartsResponse("[]", 913324359);
		Assert.Empty(bars);
	}

	[Fact]
	public void ParseChartsResponse_SingleEnvelope_ReturnsBarsOldestFirst()
	{
		const string json = """
		[
			{
				"tickerId": 913324359,
				"data": [
					"1747837920,5128.25,5129.10,5130.50,5127.80,8500",
					"1747837860,5126.00,5128.25,5129.00,5125.50,9200",
					"1747837800,5125.50,5126.00,5127.00,5124.75,7100"
				]
			}
		]
		""";

		var bars = WebullChartsClient.ParseChartsResponse(json, 913324359);

		Assert.Equal(3, bars.Count);
		// Oldest-first.
		Assert.True(bars[0].Timestamp < bars[1].Timestamp);
		Assert.True(bars[1].Timestamp < bars[2].Timestamp);
		Assert.Equal(5125.50m, bars[0].Open);
		Assert.Equal(5128.25m, bars[2].Open);
	}

	[Fact]
	public void ParseChartsResponse_BatchResponse_PicksMatchingTickerId()
	{
		const string json = """
		[
			{"tickerId": 913354088, "data": ["1747837800,17500,17502,17505,17498,1000"]},
			{"tickerId": 913324359, "data": ["1747837800,5125.50,5128.25,5131.00,5123.75,12345"]}
		]
		""";

		var bars = WebullChartsClient.ParseChartsResponse(json, 913324359);

		Assert.Single(bars);
		Assert.Equal(5125.50m, bars[0].Open);
	}

	[Fact]
	public void ParseChartsResponse_UnparseableJson_ReturnsEmpty()
	{
		var bars = WebullChartsClient.ParseChartsResponse("not json", 913324359);
		Assert.Empty(bars);
	}
}
