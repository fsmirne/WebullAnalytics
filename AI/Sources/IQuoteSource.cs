namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Provides option quotes and underlying spot prices.
/// Implementations: LiveQuoteSource (Webull) and ReplayQuoteSource (Black-Scholes + IV back-solve).
/// </summary>
internal interface IQuoteSource
{
	/// <summary>Fetches quotes for all OCC option symbols in the set, plus the spot price for each unique ticker root.</summary>
	/// <param name="asOf">Logical timestamp. Live implementations ignore this; replay uses it.</param>
	/// <param name="optionSymbols">OCC option symbols.</param>
	/// <param name="tickers">Underlying tickers for spot-price lookup.</param>
	/// <param name="overrides">Per-call overrides for spot prices and 0DTE TTE. Default is empty
	/// (use the implementation's natural values). Backtest's intraday minute loop populates these
	/// per minute so the same BacktestQuoteSource instance can be queried concurrently without
	/// shared mutable state. Live/replay impls accept and ignore.</param>
	Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation, QuoteOverrides overrides = default);
}

/// <summary>Bundle of per-leg quotes and per-ticker spots returned by IQuoteSource.</summary>
internal sealed record QuoteSnapshot(
	IReadOnlyDictionary<string, OptionContractQuote> Options,
	IReadOnlyDictionary<string, decimal> Underlyings
);

/// <summary>Per-call quote-source overrides. <c>Spots</c> replaces the implementation's natural
/// spot lookup per ticker; <c>ZeroDteTimeYears</c> replaces the 0DTE time-to-expiry assumption.
/// Defaults to all-null (use natural values) so callers that don't need overrides can pass
/// <c>default</c>. Used by BacktestRunner's per-minute opener scan to inject the minute's spot +
/// remaining-session TTE without mutating BacktestQuoteSource shared state.</summary>
internal readonly record struct QuoteOverrides(
	IReadOnlyDictionary<string, decimal>? Spots = null,
	double? ZeroDteTimeYears = null
);
