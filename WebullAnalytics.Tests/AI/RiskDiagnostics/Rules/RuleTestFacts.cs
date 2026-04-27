using WebullAnalytics.AI.RiskDiagnostics;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics.Rules;

/// <summary>Shared factory for building RiskDiagnosticFacts with sensible defaults so each rule test
/// only specifies the fields it cares about. Avoids repeating a 20-arg constructor in every test.</summary>
internal static class RuleTestFacts
{
	public static RiskDiagnosticFacts Default(
		string structureLabel = "calendar",
		string directionalBias = "neutral",
		decimal netDelta = 0m,
		decimal netThetaPerDay = 0m,
		decimal netVega = 0m,
		int shortLegDteMin = 3,
		int longLegDteMax = 10,
		int dteGapDays = 7,
		bool hasShortLeg = true,
		bool hasLongLeg = true,
		decimal longPremiumPaid = 1m,
		decimal shortPremiumReceived = 0.5m,
		decimal netCashPerShare = -0.5m,
		decimal? premiumRatio = 2m,
		decimal spot = 25m,
		bool shortLegOtm = true,
		decimal shortLegExtrinsic = 0.2m,
		decimal longLegStrike = 25m,
		decimal shortLegStrike = 25m,
		decimal netDeltaPostShort = 0m,
		TrendSnapshot? trend = null) =>
		new(
			structureLabel, directionalBias,
			netDelta, netThetaPerDay, netVega,
			shortLegDteMin, longLegDteMax, dteGapDays,
			hasShortLeg, hasLongLeg,
			longPremiumPaid, shortPremiumReceived, netCashPerShare, premiumRatio,
			spot, shortLegOtm, shortLegExtrinsic,
			longLegStrike, shortLegStrike, netDeltaPostShort, trend);
}
