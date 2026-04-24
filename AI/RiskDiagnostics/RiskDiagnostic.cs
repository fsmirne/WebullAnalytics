namespace WebullAnalytics.AI.RiskDiagnostics;

internal sealed record RiskDiagnostic(
	string StructureLabel,
	string DirectionalBias,
	decimal NetDelta,
	decimal NetThetaPerDay,
	decimal NetVega,
	int ShortLegDteMin,
	int LongLegDteMax,
	int DteGapDays,
	decimal LongPremiumPaid,
	decimal ShortPremiumReceived,
	decimal NetCashPerShare,
	decimal? PremiumRatio,
	decimal SpotAtEvaluation,
	decimal? BreakevenDistancePct,
	bool ShortLegOtm,
	decimal ShortLegExtrinsic,
	TrendSnapshot? Trend,
	decimal? CostBasisPerShare,
	decimal? CurrentValuePerShare,
	decimal? UnrealizedPnlPerShare,
	IReadOnlyList<RiskRuleHit> Rules);

internal sealed record RiskRuleHit(
	string Id,
	string Message,
	IReadOnlyDictionary<string, decimal> Inputs);

internal sealed record TrendSnapshot(
	decimal? ChangePctIntraday,
	decimal ChangePct5Day,
	decimal ChangePct20Day,
	decimal Spot20DayAtrPct,
	DateTime AsOf);

internal sealed record DiagnosticLeg(
	string Symbol,
	OptionParsed Parsed,
	bool IsLong,
	int Qty,
	decimal? PricePerShare,
	decimal? CostBasisPerShare);
