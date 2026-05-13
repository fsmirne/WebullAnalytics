using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// <see cref="IPositionSource"/> that reflects the in-memory <see cref="SimulatedBook"/>. The book's
/// cash plus the mark-to-market value of all open positions is reported as <c>AccountValue</c>,
/// matching the live <c>LivePositionSource</c> convention (where Webull's account-value field already
/// includes open-position MTM). Without folding MTM into <c>AccountValue</c>, opener risk caps that
/// scale off equity (e.g. <c>MaxRiskPctPerProposal</c>) misfire whenever a credit-received position
/// inflates cash without a corresponding decrease.
/// </summary>
internal sealed class BacktestPositionSource : IPositionSource
{
	private const decimal Multiplier = 100m;

	private readonly SimulatedBook _book;
	private readonly IQuoteSource _quotes;

	public BacktestPositionSource(SimulatedBook book, IQuoteSource quotes)
	{
		_book = book;
		_quotes = quotes;
	}

	public Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		var filtered = _book.OpenPositions
			.Where(kv => tickers.Contains(kv.Value.Ticker))
			.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
		return Task.FromResult<IReadOnlyDictionary<string, OpenPosition>>(filtered);
	}

	public async Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation)
	{
		var mtm = await ComputeMtmAsync(asOf, cancellation);
		return (_book.Cash, _book.Cash + mtm);
	}

	/// <summary>Sum of per-position mark-to-market value at <paramref name="asOf"/>. Each leg's
	/// contribution is <c>±mid × 100 × qty</c> (positive for long, negative for short). Positions whose
	/// legs aren't all priceable are skipped (their MTM stays out of the equity estimate rather than
	/// being treated as zero, which would understate equity and over-tighten the risk cap).</summary>
	private async Task<decimal> ComputeMtmAsync(DateTime asOf, CancellationToken cancellation)
	{
		if (_book.OpenPositions.Count == 0) return 0m;

		var symbols = _book.OpenPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var tickers = _book.OpenPositions.Values.Select(p => p.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var snap = await _quotes.GetQuotesAsync(asOf, symbols, tickers, cancellation);

		decimal total = 0m;
		foreach (var pos in _book.OpenPositions.Values)
		{
			decimal perShare = 0m;
			var allLegsPriced = true;
			foreach (var leg in pos.Legs)
			{
				if (!snap.Options.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
				{
					allLegsPriced = false;
					break;
				}
				var mid = (q.Bid.Value + q.Ask.Value) / 2m;
				perShare += leg.Side == Side.Buy ? mid : -mid;
			}
			if (allLegsPriced) total += perShare * Multiplier * pos.Quantity;
		}
		return total;
	}
}
