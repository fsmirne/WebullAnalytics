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
	IReadOnlyList<RiskRuleHit> Rules,
	RiskDiagnosticProbe? Probe = null);

internal sealed record RiskDiagnosticProbe(
	decimal? EnumDelta,
	decimal? EnumDeltaMin,
	decimal? EnumDeltaMax,
	bool? EnumDeltaPass,
	IReadOnlyList<RiskDiagnosticLegQuote> LegQuotes,
	RiskDiagnosticOpenerScore? OpenerScore);

internal sealed record RiskDiagnosticLegQuote(
	string Label,
	string Symbol,
	decimal? Bid,
	decimal? Ask,
	decimal? ImpliedVolatility,
	decimal? HistoricalVolatility,
	decimal? ImpliedVolatility5Day,
	long? OpenInterest,
	long? Volume);

internal sealed record RiskDiagnosticOpenerScore(
	string Structure,
	int Qty,
	decimal? DebitOrCreditPerContract,
	decimal? MaxProfitPerContract,
	decimal? MaxLossPerContract,
	decimal? CapitalAtRiskPerContract,
	decimal? ProbabilityOfProfit,
	decimal? ExpectedValuePerContract,
	int? DaysToTarget,
	decimal? RawScore,
	decimal? BiasAdjustedScore,
	string? Rationale,
	decimal? ThetaPerDayPerContract = null,
	decimal? FinalScore = null);

internal sealed record RiskRuleHit(
	string Id,
	string Message,
	IReadOnlyDictionary<string, decimal> Inputs);

internal sealed record TrendSnapshot(
	decimal? ChangePctIntraday,
	decimal ChangePct5Day,
	decimal ChangePct20Day,
	decimal Atr14Pct,
	DateTime AsOf);

internal sealed record DiagnosticLeg(
	string Symbol,
	OptionParsed Parsed,
	bool IsLong,
	int Qty,
	decimal? PricePerShare,
	decimal? CostBasisPerShare);
