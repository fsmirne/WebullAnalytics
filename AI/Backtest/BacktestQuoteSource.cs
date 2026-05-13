using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Black-Scholes-priced option quote source for backtests. Underlying spot comes from each ticker's
/// daily close (<see cref="HistoricalBarCache"/>); IV comes from <see cref="BacktestIVProvider"/>.
/// Synthesizes a symmetric ±1% bid/ask around the theoretical mid so the candidate scorer's
/// liquidity checks pass on every contract.
/// </summary>
internal sealed class BacktestQuoteSource : IQuoteSource
{
	private readonly HistoricalBarCache _bars;
	private readonly BacktestIVProvider _iv;
	private readonly double _riskFreeRate;

	public BacktestQuoteSource(HistoricalBarCache bars, BacktestIVProvider iv, double riskFreeRate)
	{
		_bars = bars;
		_iv = iv;
		_riskFreeRate = riskFreeRate;
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

		foreach (var ticker in tickers)
		{
			var bar = await _bars.GetBarAsync(ticker, asOf.Date, cancellation);
			if (bar != null) underlyings[ticker] = bar.Close;
		}

		foreach (var sym in optionSymbols)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;

			if (!underlyings.TryGetValue(parsed.Root, out var spot))
			{
				var bar = await _bars.GetBarAsync(parsed.Root, asOf.Date, cancellation);
				if (bar == null) continue;
				spot = bar.Close;
				underlyings[parsed.Root] = spot;
			}

			var iv = await _iv.GetIVAsync(parsed.Root, asOf, parsed.Strike, spot, parsed.CallPut, cancellation);
			decimal price;
			if (iv.HasValue)
			{
				var dte = Math.Max(1, (parsed.ExpiryDate.Date - asOf.Date).Days);
				var timeYears = dte / 365.0;
				price = OptionMath.BlackScholes(spot, parsed.Strike, timeYears, _riskFreeRate, iv.Value, parsed.CallPut);
			}
			else
			{
				price = parsed.CallPut == "C" ? Math.Max(0m, spot - parsed.Strike) : Math.Max(0m, parsed.Strike - spot);
			}

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
