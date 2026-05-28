namespace WebullAnalytics.Sentiment;

/// <summary>Composite Fear & Greed reading plus the seven sub-components CNN publishes. Score is the
/// 0–100 contrarian index (low = fear / contrarian-bullish, high = greed / contrarian-bearish).
/// Previous-period scores let downstream consumers compute deltas without a second fetch.</summary>
internal sealed record SentimentSnapshot(
	decimal Score,
	string Rating,
	DateTime Timestamp,
	decimal? PreviousClose,
	decimal? Previous1Week,
	decimal? Previous1Month,
	decimal? Previous1Year,
	IReadOnlyList<SentimentComponent> Components)
{
	/// <summary>Score change vs the close-of-previous-trading-day reading. Null when the previous close
	/// is unavailable (first publication, weekend pre-market, etc.).</summary>
	public decimal? DeltaPreviousClose => PreviousClose.HasValue ? Score - PreviousClose.Value : null;
	public decimal? Delta1Week => Previous1Week.HasValue ? Score - Previous1Week.Value : null;
	public decimal? Delta1Month => Previous1Month.HasValue ? Score - Previous1Month.Value : null;
	public decimal? Delta1Year => Previous1Year.HasValue ? Score - Previous1Year.Value : null;
}

/// <summary>One sub-indicator of the composite. Score is CNN's 0–100 normalization of the indicator;
/// RawValue is the underlying number CNN starts from (e.g., the actual VIX print, the put/call ratio).
/// CNN normalizes raw value to score with internal bands — RawValue is exposed so the analyze command
/// can show the actual market reading alongside the normalized score.</summary>
internal sealed record SentimentComponent(
	string Key,
	string Label,
	decimal Score,
	string Rating,
	decimal? RawValue,
	DateTime Timestamp);

/// <summary>Maps a numeric sentiment score (0–100) to one of CNN's five rating labels. Used when the
/// API doesn't echo a rating (e.g., previous-period values come back as bare numbers).</summary>
internal static class SentimentRating
{
	public const string ExtremeFear = "extreme fear";
	public const string Fear = "fear";
	public const string Neutral = "neutral";
	public const string Greed = "greed";
	public const string ExtremeGreed = "extreme greed";

	public static string FromScore(decimal score) => score switch
	{
		<= 24m => ExtremeFear,
		< 50m => Fear,
		<= 50m => Neutral,
		< 75m => Greed,
		_ => ExtremeGreed,
	};

	/// <summary>True when the rating is an extreme regime (extreme fear or extreme greed) — the only
	/// values where the contrarian read is well-supported. Used by the risk rule to gate firing.</summary>
	public static bool IsExtreme(string rating) =>
		rating.Equals(ExtremeFear, StringComparison.OrdinalIgnoreCase)
		|| rating.Equals(ExtremeGreed, StringComparison.OrdinalIgnoreCase);
}
