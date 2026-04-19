namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Provides option quotes and underlying spot prices.
/// Implementations: LiveQuoteSource (Yahoo / Webull) and ReplayQuoteSource (Black-Scholes + IV back-solve).
/// </summary>
internal interface IQuoteSource
{
	/// <summary>Fetches quotes for all OCC option symbols in the set, plus the spot price for each unique ticker root.</summary>
	/// <param name="asOf">Logical timestamp. Live implementations ignore this; replay uses it.</param>
	/// <param name="optionSymbols">OCC option symbols.</param>
	/// <param name="tickers">Underlying tickers for spot-price lookup.</param>
	Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation);
}

/// <summary>Bundle of per-leg quotes and per-ticker spots returned by IQuoteSource.</summary>
internal sealed record QuoteSnapshot(
	IReadOnlyDictionary<string, OptionContractQuote> Options,
	IReadOnlyDictionary<string, decimal> Underlyings
);
