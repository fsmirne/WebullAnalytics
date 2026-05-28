using System.Globalization;
using WebullAnalytics.Sentiment;

namespace WebullAnalytics.AI.RiskDiagnostics.Rules;

/// <summary>Fires when CNN's headline Fear & Greed composite materially diverges from its
/// <c>junk_bond_demand</c> sub-component — i.e., the equity-driven composite and the credit-driven
/// component are on opposite sides of neutral by at least <see cref="DivergenceThreshold"/> points.
/// Credit spreads have a documented 1–3 week lead over equities at major turning points (2007, 2018,
/// 2020), so this is the part of F&G that carries information the composite alone hides. Fires
/// only when the position is on the side at risk of mean-reversion (greed-composite + fear-credit
/// against a bullish or neutral book; mirror image against a bearish or neutral book) — a contrarian-
/// aligned position is already positioned for the resolution.</summary>
internal sealed class CreditDivergenceRule : IRiskRule
{
	public string Id => "credit_divergence";

	private const decimal DivergenceThreshold = 30m;

	public RiskRuleHit? TryEvaluate(RiskDiagnosticFacts f)
	{
		if (!f.MarketSentimentScore.HasValue || !f.JunkBondDemandScore.HasValue) return null;

		var composite = f.MarketSentimentScore.Value;
		var junk = f.JunkBondDemandScore.Value;
		var divergence = composite - junk;
		if (Math.Abs(divergence) < DivergenceThreshold) return null;

		var bullish = string.Equals(f.DirectionalBias, "bullish", StringComparison.OrdinalIgnoreCase);
		var bearish = string.Equals(f.DirectionalBias, "bearish", StringComparison.OrdinalIgnoreCase);

		var compositeRating = SentimentRating.FromScore(composite);
		var junkRating = SentimentRating.FromScore(junk);
		var compositeStr = composite.ToString("F0", CultureInfo.InvariantCulture);
		var junkStr = junk.ToString("F0", CultureInfo.InvariantCulture);
		var divStr = divergence.ToString("+0;-0", CultureInfo.InvariantCulture);

		string? message = null;
		if (divergence >= DivergenceThreshold && !bearish)
		{
			message = $"F&G composite {compositeStr}/100 ({compositeRating}) but junk-bond-demand sub-score {junkStr}/100 ({junkRating}); credit markets pricing tail risk the equity-driven composite is masking ({divStr} pt divergence). Credit spreads historically lead equity drawdowns by 1–3 weeks at major turning points.";
		}
		else if (divergence <= -DivergenceThreshold && !bullish)
		{
			message = $"F&G composite {compositeStr}/100 ({compositeRating}) but junk-bond-demand sub-score {junkStr}/100 ({junkRating}); credit markets recovering ahead of equities ({divStr} pt divergence). Credit historically leads equity reversals off lows by 1–3 weeks.";
		}

		if (message == null) return null;
		message += " Macro overlay; idiosyncratic catalysts (earnings, M&A) can dominate on single-name positions.";

		var inputs = new Dictionary<string, decimal>(StringComparer.Ordinal)
		{
			["composite_score"] = composite,
			["junk_bond_score"] = junk,
			["divergence"] = divergence,
		};
		return new RiskRuleHit(Id, message, inputs);
	}
}
