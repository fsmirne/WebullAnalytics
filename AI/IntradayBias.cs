namespace WebullAnalytics.AI;

/// <summary>Intraday tape-derived directional signal in [-1, +1]. Composed from three sub-components:
/// overnight gap, open-to-now drift, and VWAP deviation. Computed by
/// <see cref="IntradayTapeIndicators.Compute"/> from a minute-bar series; null when the underlying
/// data is insufficient (cache miss, pre-open, < <c>MinBars</c> bars on the session).
///
/// Score sign convention matches <see cref="TechnicalBias"/>: positive is bullish, negative bearish.
/// Magnitude is intentionally larger than the daily-bar bias — a 1% intraday move maps to ±1 here
/// versus ±0.02 on SmaScore — because intraday is a faster, higher-confidence signal at the 0DTE
/// horizon. The DTE-weighted mix in the consumer is what controls how much influence this carries
/// in the final scoring bias.</summary>
internal sealed record IntradayBias(
	decimal Score,
	decimal GapScore,
	decimal OpenToNowScore,
	decimal VwapDeviationScore,
	int BarCount,
	DateTimeOffset AsOf);
