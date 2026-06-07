using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI.Backtest;

/// <summary>An <see cref="IQuoteSource"/> the backtest runner can drive. On top of the per-minute
/// <see cref="IQuoteSource.GetQuotesAsync"/> (real marks/fills), it adds the COUNTERFACTUAL reprice the
/// daily-step intraday-trigger pass and the profit projector need: "price these legs as if the underlying
/// were at <paramref name="spot"/>" (the day's bar.High / bar.Low, or a projector's hypothetical spot).
///
/// <para>That query is inherently parametric — no historical feed can say what the NBBO would have been at
/// a price that never printed — so the quotes-only source (<see cref="QuotesQuoteSource"/>) delegates it to
/// a held <see cref="BacktestQuoteSource"/>, while pricing actual marks/fills from real NBBO. Both impls
/// satisfy this interface so the runner is agnostic to which price foundation is active.</para></summary>
internal interface IBacktestQuoteSource : IQuoteSource
{
	/// <summary>Reprice <paramref name="optionSymbols"/> for one ticker at an explicit hypothetical
	/// <paramref name="spot"/> (parametric BS + smile), with a 0DTE time-to-expiry override. Used by the
	/// intraday SL/TP bracket and the profit projector. Empty map if ATM IV is unavailable for the date.</summary>
	Task<IReadOnlyDictionary<string, OptionContractQuote>> GetIntradayQuotesAsync(
		DateTime asOf, string ticker, decimal spot, IEnumerable<string> optionSymbols,
		double zeroDteTimeYears, CancellationToken cancellation);
}
