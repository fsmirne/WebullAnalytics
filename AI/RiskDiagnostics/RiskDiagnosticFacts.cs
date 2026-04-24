namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Pre-computed fields consumed by IRiskRule implementations. Derived once by RiskDiagnosticBuilder
/// from the legs + spot + IV + trend; rules read this struct rather than recompute.</summary>
internal readonly record struct RiskDiagnosticFacts(
	string StructureLabel,
	string DirectionalBias,
	decimal NetDelta,
	decimal NetThetaPerDay,
	decimal NetVega,
	int ShortLegDteMin,
	int LongLegDteMax,
	int DteGapDays,
	bool HasShortLeg,
	bool HasLongLeg,
	decimal LongPremiumPaid,
	decimal ShortPremiumReceived,
	decimal NetCashPerShare,
	decimal? PremiumRatio,
	decimal Spot,
	bool ShortLegOtm,
	decimal ShortLegExtrinsic,
	decimal LongLegStrike,
	decimal ShortLegStrike,
	decimal NetDeltaPostShort,
	TrendSnapshot? Trend);
