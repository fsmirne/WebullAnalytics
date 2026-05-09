using System.Globalization;
using WebullAnalytics.Sentiment;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Surfaces a contrarian-regime warning when CNN's Fear &amp; Greed Index is in extreme
/// territory and the position's directional bias is aligned with the crowd, OR when the index has
/// shifted ≥30 points over the past week (regime change). Macro overlay only — single-name
/// catalyst-driven moves can dominate the F&amp;G signal on a given ticker.</summary>
internal sealed class MarketSentimentExtremeRule : IRiskRule
{
	public string Id => "market_sentiment_extreme";

	private const decimal RegimeShiftThreshold = 30m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (!f.MarketSentimentScore.HasValue) return null;
		var score = f.MarketSentimentScore.Value;
		var rating = f.MarketSentimentRating ?? SentimentRating.FromScore(score);
		var delta = f.MarketSentimentDelta1Week ?? 0m;

		var extremeGreed = score >= 75m;
		var extremeFear = score <= 24m;
		var bullish = string.Equals(f.DirectionalBias, "bullish", StringComparison.OrdinalIgnoreCase);
		var bearish = string.Equals(f.DirectionalBias, "bearish", StringComparison.OrdinalIgnoreCase);

		string? message = null;
		if (extremeGreed && bullish)
			message = $"F&G {score.ToString("F0", CultureInfo.InvariantCulture)}/100 ({rating}); bullish position aligned with crowded long — elevated mean-reversion risk on a 1–2 week horizon.";
		else if (extremeFear && bearish)
			message = $"F&G {score.ToString("F0", CultureInfo.InvariantCulture)}/100 ({rating}); bearish position aligned with crowded short — elevated mean-reversion risk on a 1–2 week horizon.";
		else if (Math.Abs(delta) >= RegimeShiftThreshold)
			message = $"F&G shifted {delta.ToString("+0;-0", CultureInfo.InvariantCulture)} pts over the past week (now {score.ToString("F0", CultureInfo.InvariantCulture)}/100 {rating}); regime change — vol/momentum assumptions may be stale.";

		if (message == null) return null;
		message += " Macro overlay; idiosyncratic catalysts (earnings, M&A) can dominate on single-name positions.";

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["score"] = score,
			["delta_1w"] = delta,
		};
		return new RiskRuleHit(Id, message, inputs);
	}
}
