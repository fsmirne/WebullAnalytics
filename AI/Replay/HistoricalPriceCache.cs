using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI.Replay;

/// <summary>
/// Thin facade over <see cref="HistoricalBarCache"/> that returns daily closes. Both this class and
/// the bar cache share the same on-disk OHLC CSV at <c>data/history/{TICKER}.csv</c>, so callers that
/// only need closes (technical-bias inputs, VIX term structure, the position replay underlying spot)
/// can keep using the simpler API without forking the disk format. The previous close-only writer is
/// gone — every refresh now persists full OHLC, which is what <see cref="HistoricalBarCache"/> needs
/// for backtest fills.
/// </summary>
internal sealed class HistoricalPriceCache
{
	private readonly HistoricalBarCache _bars;

	public HistoricalPriceCache(string? cacheDir = null) : this(new HistoricalBarCache(cacheDir)) { }

	public HistoricalPriceCache(HistoricalBarCache bars)
	{
		_bars = bars;
	}

	internal HistoricalPriceCache(string? cacheDir, Func<string, DateTime, DateTime, CancellationToken, Task<Dictionary<DateTime, YahooOptionsClient.HistoricalBar>>> fetch, Func<DateTime>? utcNow = null)
		: this(new HistoricalBarCache(cacheDir, fetch, utcNow))
	{
	}

	public async Task<decimal?> GetCloseAsync(string ticker, DateTime date, CancellationToken cancellation)
	{
		var bar = await _bars.GetBarAsync(ticker, date, cancellation);
		return bar?.Close;
	}

	/// <summary>Returns the last <paramref name="count"/> daily closes strictly before <paramref name="asOf"/>,
	/// oldest-first. Returns fewer than <paramref name="count"/> entries if the cache has less data.
	/// The strict-less-than filter prevents backtest lookahead: at a 09:30 step on day X the model
	/// must not consume day X's close (which the historical cache already knows). In live mode the
	/// cache only has settled days, so the strict filter is a no-op there.</summary>
	public Task<IReadOnlyList<decimal>> GetRecentClosesAsync(string ticker, int count, DateTime asOf, CancellationToken cancellation)
		=> _bars.GetRecentClosesAsync(ticker, count, asOf, cancellation);
}
