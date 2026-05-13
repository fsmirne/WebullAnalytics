using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Black-Scholes-priced option quote source for backtests. Underlying spot comes from each ticker's
/// daily close (<see cref="HistoricalBarCache"/>); IV comes from <see cref="BacktestIVProvider"/>.
/// Synthesizes a symmetric bid/ask around the theoretical mid using a per-ticker spread model so
/// the candidate scorer's liquidity/realized-EV checks see friction that resembles the live tape:
/// SPY/QQQ/IWM are penny-pilot tight; everything else gets the wider per-share floor typical of
/// single-stock options (matching e.g. the GME quotes seen in production — bid 0.30 / ask 0.35 on
/// a $0.325 mid).
/// </summary>
internal sealed class BacktestQuoteSource : IQuoteSource
{
	// Penny-pilot index ETFs: minimum half-spread $0.005 ($0.01 total), plus 0.5% of mid.
	private const decimal IndexHalfSpreadFloor = 0.005m;
	private const decimal IndexHalfSpreadPct = 0.005m;
	// Single-stock single-name options: minimum half-spread $0.01 ($0.02 total), plus 5% of mid.
	// This roughly reproduces real GME-style quotes where mid-priced contracts trade one
	// nickel-tick wide and very cheap (sub-$0.10) contracts trade one penny-tick wide.
	private const decimal SingleStockHalfSpreadFloor = 0.01m;
	private const decimal SingleStockHalfSpreadPct = 0.05m;

	private static readonly HashSet<string> PennyPilotIndexEtfs = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPY", "QQQ", "IWM", "DIA"
	};

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

			var halfSpread = HalfSpreadFor(parsed.Root, price);
			var bid = Math.Max(0m, price - halfSpread);
			var ask = price + halfSpread;
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

	private static decimal HalfSpreadFor(string ticker, decimal mid)
	{
		var (floor, pct) = PennyPilotIndexEtfs.Contains(ticker)
			? (IndexHalfSpreadFloor, IndexHalfSpreadPct)
			: (SingleStockHalfSpreadFloor, SingleStockHalfSpreadPct);
		return Math.Max(floor, mid * pct);
	}
}
