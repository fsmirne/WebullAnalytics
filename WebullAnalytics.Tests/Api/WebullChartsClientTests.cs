using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class WebullChartsClientTests
{
	[Fact]
	public void ParseBarRow_PrimaryOrdering_ReturnsBar()
	{
		// time,open,close,high,low,volume,vwap. The raw timestamp is shifted -60s by the parser to
		// normalize Webull's end-of-bar convention onto the start-of-bar convention used by Polygon,
		// ToS, and TradingView (see `WebullBarShift` in WebullChartsClient).
		var bar = WebullChartsClient.ParseBarRow("1747837800,5125.50,5128.25,5131.00,5123.75,12345,5127.10");

		Assert.NotNull(bar);
		Assert.Equal(5125.50m, bar.Open);
		Assert.Equal(5128.25m, bar.Close);
		Assert.Equal(5131.00m, bar.High);
		Assert.Equal(5123.75m, bar.Low);
		Assert.Equal(12345, bar.Volume);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1747837800 - 60), bar.Timestamp);
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
	public void ParseOptionBarRow_FullRowWithIv_ReturnsBar()
	{
		// time,open,close,high,low,prevClose,volume,iv  (observed in /api/quote/option/chart/kdata)
		// Parser applies the same -60s start-of-bar normalization here as in ParseBarRow.
		var bar = WebullChartsClient.ParseOptionBarRow("1779806220,22.70,22.80,23.91,22.60,22.60,76,15.75");

		Assert.NotNull(bar);
		Assert.Equal(22.70m, bar!.Open);
		Assert.Equal(22.80m, bar.Close);
		Assert.Equal(23.91m, bar.High);
		Assert.Equal(22.60m, bar.Low);
		Assert.Equal(76, bar.Volume);
		Assert.Equal(15.75m, bar.ImpliedVolatility);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1779806220 - 60), bar.Timestamp);
	}

	[Fact]
	public void ParseOptionBarRow_MissingIv_ReturnsBarWithNullIv()
	{
		// 7-column row (no IV column) — still a valid bar; just no IV.
		var bar = WebullChartsClient.ParseOptionBarRow("1779806220,22.70,22.80,23.91,22.60,22.60,76");
		Assert.NotNull(bar);
		Assert.Null(bar!.ImpliedVolatility);
		Assert.Equal(22.80m, bar.Close);
	}

	[Fact]
	public void ParseOptionBarRow_IvLiteralNull_TreatedAsMissing()
	{
		var bar = WebullChartsClient.ParseOptionBarRow("1779806220,22.70,22.80,23.91,22.60,22.60,76,null");
		Assert.NotNull(bar);
		Assert.Null(bar!.ImpliedVolatility);
	}

	[Fact]
	public void ParseOptionBarRow_VolumeLiteralNull_BecomesZero()
	{
		// SPX index bars come back with volume="null"; check the option parser also handles that.
		var bar = WebullChartsClient.ParseOptionBarRow("1779806220,22.70,22.80,23.91,22.60,22.60,null,15.75");
		Assert.NotNull(bar);
		Assert.Equal(0, bar!.Volume);
		Assert.Equal(15.75m, bar.ImpliedVolatility);
	}

	[Fact]
	public void ParseOptionBarRow_FailsOhlcSanity_ReturnsNull()
	{
		// high < max(open, close) — invalid bar.
		Assert.Null(WebullChartsClient.ParseOptionBarRow("1779806220,22.70,22.80,22.50,22.60,22.60,76,15.75"));
	}

	[Fact]
	public void ParseOptionBarRow_TruncatedRow_ReturnsNull()
	{
		Assert.Null(WebullChartsClient.ParseOptionBarRow("1779806220,22.70,22.80"));
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
