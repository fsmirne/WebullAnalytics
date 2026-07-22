using WebullAnalytics.Sentiment;
using Xunit;

namespace WebullAnalytics.Tests.Sentiment;

public class FearGreedClientTests
{
	private const string SampleResponse = """
	{
	  "fear_and_greed": {
	    "score": 66.9142857142857,
	    "rating": "greed",
	    "timestamp": "2026-05-08T23:33:07+00:00",
	    "previous_close": 67.5714285714286,
	    "previous_1_week": 71.17142857142856,
	    "previous_1_month": 29.171428571428574,
	    "previous_1_year": 57.65714285714286
	  },
	  "market_momentum_sp500": {
	    "timestamp": 1778273206000.0, "score": 99.6, "rating": "extreme greed",
	    "data": [{"x": 1778273206000.0, "y": 7398.93, "rating": "extreme greed"}]
	  },
	  "stock_price_strength": {
	    "timestamp": 1778283187000.0, "score": 61.4, "rating": "greed",
	    "data": [{"x": 1778283187000.0, "y": 3.82327366285503, "rating": "extreme fear"}]
	  },
	  "stock_price_breadth": {
	    "timestamp": 1778283187000.0, "score": 62.2, "rating": "greed",
	    "data": [{"x": 1778283187000.0, "y": 1201.95180278505, "rating": "extreme greed"}]
	  },
	  "put_call_options": {
	    "timestamp": 1778273714000.0, "score": 77.4, "rating": "extreme greed",
	    "data": [{"x": 1778273714000.0, "y": 0.674610208778126, "rating": "extreme fear"}]
	  },
	  "market_volatility_vix": {
	    "timestamp": 1778271301000.0, "score": 50, "rating": "neutral",
	    "data": [{"x": 1778271301000.0, "y": 17.19, "rating": "extreme fear"}]
	  },
	  "junk_bond_demand": {
	    "timestamp": 1778279400000.0, "score": 24, "rating": "extreme fear",
	    "data": [{"x": 1778279400000.0, "y": 1.31475670301332, "rating": "extreme fear"}]
	  },
	  "safe_haven_demand": {
	    "timestamp": 1778270399000.0, "score": 93.6, "rating": "extreme greed",
	    "data": [{"x": 1778270399000.0, "y": 8.00321225911172, "rating": "extreme fear"}]
	  }
	}
	""";

	// Shape of a bulk-backfilled cache file: fetched 2026-05-08 for a 2022 start date, so the top level
	// is the 2026 reading while the requested date's truth sits in fear_and_greed_historical.data.
	private const string StaleBulkResponse = """
	{
	  "fear_and_greed": {
	    "score": 66.9142857142857,
	    "rating": "greed",
	    "timestamp": "2026-05-08T23:33:07+00:00",
	    "previous_close": 67.5714285714286
	  },
	  "fear_and_greed_historical": {
	    "timestamp": 1778283187000.0,
	    "score": 66.9142857142857,
	    "rating": "greed",
	    "data": [
	      {"x": 1651104000000.0, "y": 24.25714285714286, "rating": "extreme fear"},
	      {"x": 1651190400000.0, "y": 27.6, "rating": "fear"},
	      {"x": 1778283187000.0, "y": 66.9142857142857, "rating": "greed"}
	    ]
	  }
	}
	""";

	[Fact]
	public void DateAwareParseUsesHistoricalPointWhenTopLevelIsStale()
	{
		// 1651104000000 = 2022-04-28 00:00 UTC; the top level is 2026-05-08 and must not be served.
		var s = FearGreedClient.ParseResponse(StaleBulkResponse, new DateTime(2022, 4, 28));
		Assert.NotNull(s);
		Assert.InRange(s!.Score, 24.2m, 24.3m);
		Assert.Equal("extreme fear", s.Rating);
		Assert.Null(s.PreviousClose);   // array starts at the requested date - no prior point to read
		Assert.Empty(s.Components);
	}

	[Fact]
	public void DateAwareParseCarriesPreviousCloseFromPriorPoint()
	{
		var s = FearGreedClient.ParseResponse(StaleBulkResponse, new DateTime(2022, 4, 29));
		Assert.NotNull(s);
		Assert.InRange(s!.Score, 27.5m, 27.7m);
		Assert.InRange(s.PreviousClose ?? 0m, 24.2m, 24.3m);
	}

	[Fact]
	public void DateAwareParseReturnsNullWhenDateAbsent()
	{
		// 2022-05-02 sits between array points; wrong-date data must never be served for it.
		Assert.Null(FearGreedClient.ParseResponse(StaleBulkResponse, new DateTime(2022, 5, 2)));
	}

	[Fact]
	public void DateAwareParseTrustsMatchingTopLevel()
	{
		// SampleResponse's composite timestamp is 2026-05-08T23:33:07Z = 19:33 NY on 05-08: a same-day
		// snapshot, so the full top-level parse (including components) applies.
		var s = FearGreedClient.ParseResponse(SampleResponse, new DateTime(2026, 5, 8));
		Assert.NotNull(s);
		Assert.InRange(s!.Score, 66.9m, 67.0m);
		Assert.Equal(7, s.Components.Count);
	}

	[Fact]
	public void NormalizeForCacheReducesStalePayloadToSingleDate()
	{
		var reduced = FearGreedClient.NormalizeForCache(StaleBulkResponse, new DateTime(2022, 4, 29));
		Assert.NotNull(reduced);
		Assert.True(reduced!.Length < StaleBulkResponse.Length);
		// The reduced payload must parse as a same-day snapshot for its date (top level now trusted)...
		var s = FearGreedClient.ParseResponse(reduced, new DateTime(2022, 4, 29));
		Assert.NotNull(s);
		Assert.InRange(s!.Score, 27.5m, 27.7m);
		Assert.Equal("fear", s.Rating);
		Assert.InRange(s.PreviousClose ?? 0m, 24.2m, 24.3m);
		// ...and must NOT parse for a different date (no other-day data survives the reduction).
		Assert.Null(FearGreedClient.ParseResponse(reduced, new DateTime(2022, 4, 28)));
	}

	[Fact]
	public void NormalizeForCachePassesSameDayPayloadThrough()
	{
		Assert.Same(SampleResponse, FearGreedClient.NormalizeForCache(SampleResponse, new DateTime(2026, 5, 8)));
	}

	[Fact]
	public void NormalizeForCacheReturnsNullWhenDateAbsent()
	{
		Assert.Null(FearGreedClient.NormalizeForCache(StaleBulkResponse, new DateTime(2022, 5, 2)));
	}

	[Fact]
	public void ParsesCompositeScoreAndRating()
	{
		var s = FearGreedClient.ParseResponse(SampleResponse);
		Assert.NotNull(s);
		Assert.InRange(s!.Score, 66.9m, 67.0m);
		Assert.Equal("greed", s.Rating);
	}

	[Fact]
	public void ParsesHistoricalFields()
	{
		var s = FearGreedClient.ParseResponse(SampleResponse)!;
		Assert.InRange(s.PreviousClose ?? 0m, 67.5m, 67.6m);
		Assert.InRange(s.Previous1Week ?? 0m, 71.1m, 71.2m);
		Assert.InRange(s.Previous1Month ?? 0m, 29.1m, 29.2m);
		Assert.InRange(s.Previous1Year ?? 0m, 57.6m, 57.7m);
	}

	[Fact]
	public void ComputesDeltas()
	{
		var s = FearGreedClient.ParseResponse(SampleResponse)!;
		Assert.NotNull(s.Delta1Week);
		Assert.Equal(s.Score - s.Previous1Week!.Value, s.Delta1Week!.Value);
	}

	[Fact]
	public void ParsesAllSevenComponents()
	{
		var s = FearGreedClient.ParseResponse(SampleResponse)!;
		Assert.Equal(7, s.Components.Count);
		var keys = s.Components.Select(c => c.Key).ToList();
		Assert.Contains("market_momentum_sp500", keys);
		Assert.Contains("stock_price_strength", keys);
		Assert.Contains("stock_price_breadth", keys);
		Assert.Contains("put_call_options", keys);
		Assert.Contains("market_volatility_vix", keys);
		Assert.Contains("junk_bond_demand", keys);
		Assert.Contains("safe_haven_demand", keys);
	}

	[Fact]
	public void ComponentsCarryRawValueFromDataPoint()
	{
		var s = FearGreedClient.ParseResponse(SampleResponse)!;
		var vix = s.Components.First(c => c.Key == "market_volatility_vix");
		Assert.Equal(17.19m, vix.RawValue);
		var spx = s.Components.First(c => c.Key == "market_momentum_sp500");
		Assert.Equal(7398.93m, spx.RawValue);
	}

	[Fact]
	public void ReturnsNullOnMalformedJson()
	{
		Assert.Null(FearGreedClient.ParseResponse("{not valid"));
		Assert.Null(FearGreedClient.ParseResponse(""));
		Assert.Null(FearGreedClient.ParseResponse("{\"unrelated\":1}"));
	}

	[Fact]
	public void RatingFromScoreMapsBands()
	{
		Assert.Equal("extreme fear", SentimentRating.FromScore(0m));
		Assert.Equal("extreme fear", SentimentRating.FromScore(24m));
		Assert.Equal("fear", SentimentRating.FromScore(25m));
		Assert.Equal("fear", SentimentRating.FromScore(49m));
		Assert.Equal("neutral", SentimentRating.FromScore(50m));
		Assert.Equal("greed", SentimentRating.FromScore(51m));
		Assert.Equal("greed", SentimentRating.FromScore(74m));
		Assert.Equal("extreme greed", SentimentRating.FromScore(75m));
		Assert.Equal("extreme greed", SentimentRating.FromScore(100m));
	}

	[Fact]
	public void IsSettledTrueAfterFivePmNyForToday()
	{
		// 5 May 2026 is a Tuesday. After 5pm NY (= 21 UTC standard / 22 UTC DST) the day is settled.
		var today = new DateTime(2026, 5, 5);
		var afterCutoffUtc = new DateTime(2026, 5, 5, 23, 0, 0, DateTimeKind.Utc); // ~7pm NY
		Assert.True(FearGreedClient.IsSettled(today, afterCutoffUtc));
	}

	[Fact]
	public void IsSettledFalseBeforeFivePmNyForToday()
	{
		// 14:00 UTC on a May day = 10am NY (DST) — well before 5pm cutoff.
		var today = new DateTime(2026, 5, 5);
		var beforeCutoffUtc = new DateTime(2026, 5, 5, 14, 0, 0, DateTimeKind.Utc);
		Assert.False(FearGreedClient.IsSettled(today, beforeCutoffUtc));
	}

	[Fact]
	public void IsSettledTrueForHistoricalDate()
	{
		// Yesterday is always settled regardless of current time-of-day.
		var yesterday = new DateTime(2026, 5, 4);
		var thisMorningUtc = new DateTime(2026, 5, 5, 14, 0, 0, DateTimeKind.Utc);
		Assert.True(FearGreedClient.IsSettled(yesterday, thisMorningUtc));
	}
}
