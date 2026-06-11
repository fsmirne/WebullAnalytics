namespace WebullAnalytics.AI.Events;

/// <summary>Earnings/dividend catalysts for a single ticker. All dates are NY-local calendar dates
/// (DateOnly via DateTime with Kind=Unspecified). Null fields mean "unknown/not announced" — callers
/// must treat null as "no veto" (degrade gracefully on data outage).</summary>
/// <param name="Ticker">Root ticker (e.g. "AAPL"). Stored uppercased.</param>
/// <param name="NextEarningsDate">Next scheduled earnings date, NY local. Null when no future date is
/// announced or when the data source is unavailable.</param>
/// <param name="EarningsTime">Reported timing on <see cref="NextEarningsDate"/>: "BMO" (before market
/// open), "AMC" (after market close), or null/unknown. Used only for display today; the veto rule
/// treats any same-day earnings as in-window regardless of BMO/AMC.</param>
/// <param name="NextExDividendDate">Next ex-dividend date, NY local. Null when no future date is
/// announced. Short-call assignment risk peaks the day before ex-div on ITM contracts (early-exercise
/// to capture the dividend). Normalized at construction to the previous open day when the provider's
/// pattern-projected date lands on a weekend/holiday (e.g. SPY's nominal third-Friday 2026-06-19 is
/// Juneteenth; the real ex-date is Thursday 06-18) — an ex-date can never fall on a closed session.</param>
/// <param name="DividendAmount">Per-share cash dividend on <see cref="NextExDividendDate"/>. Null when
/// missing. Surfaced for diagnostic display only.</param>
internal sealed record TickerEvents(
	string Ticker,
	DateTime? NextEarningsDate,
	string? EarningsTime,
	DateTime? NextExDividendDate,
	decimal? DividendAmount
)
{
	public DateTime? NextExDividendDate { get; init; } = NextExDividendDate is DateTime exDate ? MarketCalendar.PreviousOpenOnOrBefore(exDate) : null;
}
