using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class MassivePolygonClientTests
{
	[Fact]
	public void ParseAggregatesResponse_ExtractsBarsAndNextUrl()
	{
		var json = """
		{
		  "results": [
		    {"t": 1736946000000, "o": 6953.5, "h": 6954.1, "l": 6952.8, "c": 6952.9, "v": 10190},
		    {"t": 1736946060000, "o": 6952.9, "h": 6953.9, "l": 6952.7, "c": 6953.4, "v": 1290}
		  ],
		  "next_url": "https://example.com/next"
		}
		""";
		var (bars, next) = MassivePolygonClient.ParseAggregatesResponse(json);
		Assert.Equal(2, bars.Count);
		Assert.Equal(6953.5m, bars[0].Open);
		Assert.Equal(10190, bars[0].Volume);
		Assert.Equal("https://example.com/next", next);
	}

	[Fact]
	public void ParseAggregatesResponse_TimestampIsStartOfWindowUtc()
	{
		// 1736946000000 ms = 2025-01-15T13:00:00Z. Polygon documents `t` as "Unix Msec timestamp
		// for the start of the aggregate window". The parser preserves the raw stamp; downstream
		// consumers can rely on Polygon bars being start-of-window timestamped, which matches our
		// normalized convention (Webull bars are shifted -60s at parse to also be start-of-window).
		var json = """{"results":[{"t":1736946000000,"o":1,"h":1,"l":1,"c":1,"v":1}]}""";
		var (bars, _) = MassivePolygonClient.ParseAggregatesResponse(json);
		Assert.Single(bars);
		Assert.Equal(new DateTimeOffset(2025, 1, 15, 13, 0, 0, TimeSpan.Zero), bars[0].Timestamp);
	}

	[Fact]
	public void ParseAggregatesResponse_NoResults_ReturnsEmpty()
	{
		var (bars, _) = MassivePolygonClient.ParseAggregatesResponse("{}");
		Assert.Empty(bars);
	}

	[Fact]
	public void ParseAggregatesResponse_Malformed_ReturnsEmpty()
	{
		var (bars, _) = MassivePolygonClient.ParseAggregatesResponse("not json");
		Assert.Empty(bars);
	}
}
