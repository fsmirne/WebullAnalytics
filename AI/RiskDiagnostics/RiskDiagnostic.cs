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
	RiskDiagnosticProbe? Probe = null,
	decimal? NetMidPerShare = null,
	decimal? TheoreticalValuePerShare = null,
	decimal? MarketLongPremiumPaid = null,
	decimal? MarketShortPremiumReceived = null,
	decimal? MarketNetPremiumPerShare = null,
	decimal? MarketPremiumRatio = null,
	decimal? TheoreticalLongPremiumPaid = null,
	decimal? TheoreticalShortPremiumReceived = null,
	decimal? TheoreticalNetPremiumPerShare = null,
	decimal? TheoreticalPremiumRatio = null,
	decimal? MarketSentimentScore = null,
	string? MarketSentimentRating = null,
	decimal? MarketSentimentDelta1Week = null,
	// True when the quote source is Black-Scholes-synthesized rather than a live chain (backtest or
	// `ai scan --theoretical`). The "Market*" fields in that case hold BS prices too, so the renderer
	// shows a single "theoretical →" line instead of the misleading market-vs-theoretical comparison.
	bool IsTheoretical = false);

internal sealed record RiskDiagnosticProbe(
	decimal? EnumDelta,
	decimal? EnumDeltaMin,
	decimal? EnumDeltaMax,
	bool? EnumDeltaPass,
	IReadOnlyList<RiskDiagnosticLegQuote> LegQuotes,
	RiskDiagnosticOpenerScore? OpenerScore,
	// Non-null when the opener score (EM / PoP / breakevens) could not be computed because no usable
	// per-ticker opener config exists for the position's ticker — carries the human-readable reason
	// (e.g. "no per-ticker config ai-config.USO.json — indicators.strikeStep …") so the display can warn
	// the user what to create instead of silently omitting the block.
	string? ScoreUnavailableReason = null);

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
	decimal? FinalScore = null,
	decimal? MarginPerContract = null);

internal sealed record RiskRuleHit(
	string Id,
	string Message,
	IReadOnlyDictionary<string, decimal> Inputs);

internal sealed record TrendSnapshot(
	decimal? ChangePctIntraday,
	decimal ChangePct5Day,
	decimal ChangePct20Day,
	decimal Atr14Pct,
	DateTime AsOf,
	// Prior completed session's OHLC (second-to-last daily bar, same convention as the intraday
	// change's "yesterday's close"). Feeds the display-only floor-pivot Levels row in the risk
	// diagnostic panel — reference prices many traders watch, NOT a scoring input: a 512-session
	// study found pivot bounce/pinning indistinguishable from density-matched control levels.
	decimal? PriorHigh = null,
	decimal? PriorLow = null,
	decimal? PriorClose = null);

internal sealed record DiagnosticLeg(
	string Symbol,
	OptionParsed Parsed,
	bool IsLong,
	int Qty,
	decimal? PricePerShare,
	decimal? CostBasisPerShare);
