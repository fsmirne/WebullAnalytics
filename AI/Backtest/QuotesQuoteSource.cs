using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Backtest;

/// <summary>Quotes-only <see cref="IQuoteSource"/>: prices legs from the REAL minute NBBO in
/// <see cref="QuoteStoreCache"/> (ThetaData) instead of massive trade-bars + the synthetic spread model in
/// <see cref="BacktestQuoteSource"/>. For each leg at the scan minute it returns the real bid/ask, sets
/// LastPrice to the mid (for marking — the runner's fill model decides how far to cross), and back-solves
/// IV on demand from the mid on the dividend-adjusted forward (same dividend-aware basis as the live path).
///
/// <para>A leg with no quote within the staleness window is OMITTED from the snapshot — the consumer's
/// missing-quote policy then decides (skip the candidate, widen, etc.). This is the alternate price
/// foundation for the quotes-only pivot; it is selected behind a flag alongside the trade-bar path and
/// validated against it (diagvert / SPY-DC) before the trade-bar path is retired.</para>
///
/// <para>The runner's daily-step intraday-trigger pass and profit projector ask a COUNTERFACTUAL question
/// (<see cref="GetIntradayQuotesAsync"/>: "reprice these legs as if spot were X") that no historical NBBO
/// can answer — so that one call is delegated to a held parametric <see cref="BacktestQuoteSource"/>. Real
/// NBBO drives every actual mark/fill (<see cref="GetQuotesAsync"/>); parametric covers only hypotheticals.</para>
///
/// <para>Fill model: run with <c>--pricing mid</c> (the REAL mid) + the existing slippagePerSharePerOrder.
/// The calibrated mid+slippage already equals the real near-money half-spread crossing, so it does NOT
/// double-count; <c>--pricing bidask</c> (cross the full real spread) on top of slippage WOULD, and the
/// command warns against combining them. Volume is null here (quotes carry sizes, not executed volume).</para></summary>
internal sealed class QuotesQuoteSource : IBacktestQuoteSource
{
	// One trading session (6.5h) as the 0DTE time-to-expiry so the open captures a day's theta before
	// same-day settlement — mirrors BacktestQuoteSource.ZeroDteTimeYears.
	private const double ZeroDteTimeYears = 6.5 / 24.0 / 365.0;

	private readonly HistoricalBarCache _bars;
	private readonly QuoteStoreCache _store;
	private readonly BacktestQuoteSource _parametric;
	private readonly double _riskFreeRate;
	private readonly IReadOnlyDictionary<string, decimal>? _spotOverrides;
	private readonly IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? _dividendsByRoot;
	private readonly ChainSnapshotOiCache? _oiCache;

	/// <param name="parametric">The trade-bar/BS source used ONLY for counterfactual reprices
	/// (<see cref="GetIntradayQuotesAsync"/>) that real NBBO can't answer. All real marks/fills use the store.</param>
	public QuotesQuoteSource(HistoricalBarCache bars, QuoteStoreCache store, BacktestQuoteSource parametric, double riskFreeRate,
		IReadOnlyDictionary<string, decimal>? spotOverrides = null,
		IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividendsByRoot = null,
		ChainSnapshotOiCache? oiCache = null)
	{
		_bars = bars;
		_store = store;
		_parametric = parametric;
		_riskFreeRate = riskFreeRate;
		_spotOverrides = spotOverrides;
		_dividendsByRoot = dividendsByRoot;
		_oiCache = oiCache;
	}

	/// <summary>Counterfactual reprice at a hypothetical spot (bar.High/Low brackets, profit projector) —
	/// delegated to the parametric source, since real NBBO has no quote for a price that never printed.</summary>
	public Task<IReadOnlyDictionary<string, OptionContractQuote>> GetIntradayQuotesAsync(
		DateTime asOf, string ticker, decimal spot, IEnumerable<string> optionSymbols,
		double zeroDteTimeYears, CancellationToken cancellation)
		=> _parametric.GetIntradayQuotesAsync(asOf, ticker, spot, optionSymbols, zeroDteTimeYears, cancellation);

	/// <summary>Spot reduced by PV of dividends going ex in (asOf, expiry close]; unchanged for cash-settled
	/// index roots / no schedule. Shared basis for the IV back-solve, matching the live dividend-aware path.</summary>
	private decimal AdjSpot(string root, decimal spot, DateTime asOf, DateTime expiry)
	{
		if (_dividendsByRoot == null || !_dividendsByRoot.TryGetValue(root, out var divs) || divs.Count == 0) return spot;
		return OptionMath.DividendAdjustedSpot(spot, divs, asOf, expiry.Date + OptionMath.MarketClose, _riskFreeRate);
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols,
		IReadOnlySet<string> tickers, CancellationToken cancellation, QuoteOverrides overrides = default)
	{
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		async Task ResolveSpotAsync(string t)
		{
			if (underlyings.ContainsKey(t)) return;
			if (overrides.Spots != null && overrides.Spots.TryGetValue(t, out var ms)) { underlyings[t] = ms; return; }
			if (_spotOverrides != null && _spotOverrides.TryGetValue(t, out var so)) { underlyings[t] = so; return; }
			var bar = await _bars.GetBarAsync(t, asOf.Date, cancellation);
			if (bar != null) underlyings[t] = bar.Open;   // day's open = the spot the opener sees (see BacktestQuoteSource)
		}

		foreach (var t in tickers) await ResolveSpotAsync(t);

		var parsed = new List<(string Sym, OptionParsed P)>(optionSymbols.Count);
		foreach (var sym in optionSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p != null) parsed.Add((sym, p));
		}
		foreach (var root in parsed.Select(x => x.P.Root).Distinct(StringComparer.OrdinalIgnoreCase))
			await ResolveSpotAsync(root);

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, p) in parsed)
		{
			if (!underlyings.TryGetValue(p.Root, out var spot)) continue;
			var q = _store.NbboAt(sym, asOf);
			if (q == null) continue;                       // no real quote within staleness → omit (missing-quote policy)
			var nbbo = q.Value;
			var mid = nbbo.Mid;

			var dte = (p.ExpiryDate.Date - asOf.Date).Days;
			var timeYears = dte <= 0 ? (overrides.ZeroDteTimeYears ?? ZeroDteTimeYears) : dte / 365.0;
			decimal? iv = null;
			if (p.CallPut != null && timeYears > 0 && mid > 0m)
			{
				var solved = OptionMath.ImpliedVol(AdjSpot(p.Root, spot, asOf, p.ExpiryDate), p.Strike, timeYears, _riskFreeRate, mid, p.CallPut);
				if (solved > 0.011m && solved < 4.99m) iv = solved;   // reject vega-flat back-solver bounds
			}

			long? oi = null;
			if (_oiCache != null && _oiCache.ForDay(p.Root, asOf.Date).TryGetValue(sym, out var snap)) oi = snap.Oi;

			options[sym] = new OptionContractQuote(
				ContractSymbol: sym,
				LastPrice: mid,
				Bid: nbbo.Bid,
				Ask: nbbo.Ask,
				Change: null,
				PercentChange: null,
				Volume: null,                              // quotes carry sizes, not executed volume
				OpenInterest: oi,
				ImpliedVolatility: iv,
				HistoricalVolatility: null,
				ImpliedVolatility5Day: null);
		}

		return new QuoteSnapshot(options, underlyings);
	}
}
