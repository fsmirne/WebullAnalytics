using WebullAnalytics.AI.Replay;

namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Synthesizes option quotes for historical replay by pricing via Black-Scholes with IV back-solved
/// from the nearest real fill. When no fill within 30 days exists for a symbol, returns intrinsic-only.
/// </summary>
internal sealed class ReplayQuoteSource : IQuoteSource
{
	private readonly HistoricalPriceCache _priceCache;
	private readonly IVBackSolver _ivSolver;
	private readonly double _riskFreeRate;

	public ReplayQuoteSource(HistoricalPriceCache priceCache, IVBackSolver ivSolver, double riskFreeRate)
	{
		_priceCache = priceCache;
		_ivSolver = ivSolver;
		_riskFreeRate = riskFreeRate;
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation)
	{
		var options = new Dictionary<string, OptionContractQuote>();
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		// Spots for each ticker at asOf.
		foreach (var ticker in tickers)
		{
			var close = await _priceCache.GetCloseAsync(ticker, asOf.Date, cancellation);
			if (close.HasValue) underlyings[ticker] = close.Value;
		}

		// For each option symbol, price via Black-Scholes with back-solved IV.
		foreach (var sym in optionSymbols)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;
			if (!underlyings.TryGetValue(parsed.Root, out var S))
			{
				var close = await _priceCache.GetCloseAsync(parsed.Root, asOf.Date, cancellation);
				if (!close.HasValue) continue;
				S = close.Value;
				underlyings[parsed.Root] = S;
			}

			var iv = _ivSolver.ResolveIV(sym, asOf, parsed.ExpiryDate, parsed.Strike, parsed.CallPut);
			decimal price;
			if (iv.HasValue)
			{
				var dte = Math.Max(1, (parsed.ExpiryDate.Date - asOf.Date).Days);
				var timeYears = dte / 365.0;
				price = OptionMath.BlackScholes(S, parsed.Strike, timeYears, _riskFreeRate, iv.Value, parsed.CallPut);
			}
			else
			{
				// Intrinsic-only fallback.
				price = parsed.CallPut == "C" ? Math.Max(0m, S - parsed.Strike) : Math.Max(0m, parsed.Strike - S);
			}

			// Synthesize a symmetric bid/ask around the theoretical mid (±1% spread).
			var spread = Math.Max(0.01m, price * 0.01m);
			var bid = Math.Max(0m, price - spread);
			var ask = price + spread;
			options[sym] = new OptionContractQuote(
				ContractSymbol: sym,
				LastPrice: price,
				Bid: bid,
				Ask: ask,
				Change: null,
				PercentChange: null,
				Volume: null,
				OpenInterest: null,
				ImpliedVolatility: iv,
				HistoricalVolatility: null,
				ImpliedVolatility5Day: null
			);
		}

		return new QuoteSnapshot(options, underlyings);
	}
}
