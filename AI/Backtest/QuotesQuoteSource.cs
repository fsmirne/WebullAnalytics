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

		// Price one OCC symbol off the real NBBO store (mid + back-solved IV + snapshot OI). Null when the
		// store has no two-sided quote within staleness for that contract at this minute.
		OptionContractQuote? BuildQuote(string sym, OptionParsed p)
		{
			if (!underlyings.TryGetValue(p.Root, out var spot)) return null;
			var q = _store.NbboAt(sym, asOf);
			if (q == null) return null;
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
			return new OptionContractQuote(sym, mid, nbbo.Bid, nbbo.Ask, null, null, Volume: null, OpenInterest: oi, ImpliedVolatility: iv);
		}

		foreach (var (sym, p) in parsed)
			if (BuildQuote(sym, p) is { } qo) options[sym] = qo;

		// Chain expansion: the live broker returns the whole chain for any one symbol, but this store returns
		// only exact matches — so the opener's per-band-expiry placeholder probe would surface no contracts and
		// enumerate nothing. Expand each requested expiry into the store's real near-money chain (±15% strikes)
		// so the opener sees a full chain to build calendars/diagonals from, exactly like live.
		foreach (var grp in parsed.GroupBy(x => (x.P.Root, Exp: x.P.ExpiryDate.Date)))
		{
			if (!underlyings.TryGetValue(grp.Key.Root, out var spot) || spot <= 0m) continue;
			var oiDay = _oiCache?.ForDay(grp.Key.Root, asOf.Date);   // one memoized lookup per (root, day), reused across strikes
			foreach (var occ in _store.ContractsOn(grp.Key.Root, grp.Key.Exp, asOf.Date, spot * (1m - ChainExpandPct), spot * (1m + ChainExpandPct)))
			{
				if (options.ContainsKey(occ)) continue;
				var q = _store.NbboAt(occ, asOf);
				if (q == null) continue;
				// Cheap surface: real bid/ask (binary search) + snapshot OI/IV. Deliberately NOT back-solving IV
				// for the whole chain — that Newton solve per strike per minute was the slowdown. The scorer
				// re-solves market-implied IV for the few legs it actually selects; snapshot IV here is enough
				// for the enumerator's delta-band strike picking (mirrors the old OI-marker chain expansion).
				long? oi = null; decimal? iv = null;
				if (oiDay != null && oiDay.TryGetValue(occ, out var snap)) { oi = snap.Oi; if (snap.Iv > 0m) iv = snap.Iv; }
				// When the snapshot carries no usable IV — always the case for 0DTE, whose same-day-expiry contracts
				// have null IV in the EOD chain snapshot — back-solve it from the real NBBO mid just fetched. Without
				// this the whole expanded near-money chain is IV-less on 0DTE, so ComputeGex skips every strike's
				// gamma and GEX gravity / NetGexFraction (and the gammaRegime + gexBiasPull factors) go silently
				// inert, even though they are live in production. Gated on iv==null so the non-0DTE fast path
				// (snapshot IV present) keeps its no-Newton-per-strike behaviour.
				if (iv == null && q.Value.Mid > 0m && ParsingHelpers.ParseOptionSymbol(occ) is OptionParsed pe && pe.CallPut != null)
				{
					var dte = (grp.Key.Exp - asOf.Date).Days;
					var timeYears = dte <= 0 ? (overrides.ZeroDteTimeYears ?? ZeroDteTimeYears) : dte / 365.0;
					if (timeYears > 0)
					{
						var solved = OptionMath.ImpliedVol(AdjSpot(pe.Root, spot, asOf, pe.ExpiryDate), pe.Strike, timeYears, _riskFreeRate, q.Value.Mid, pe.CallPut);
						if (solved > 0.011m && solved < 4.99m) iv = solved;   // reject vega-flat back-solver bounds
					}
				}
				options[occ] = new OptionContractQuote(occ, q.Value.Mid, q.Value.Bid, q.Value.Ask, null, null, Volume: null, OpenInterest: oi, ImpliedVolatility: iv);
			}
		}

		return new QuoteSnapshot(options, underlyings);
	}

	/// <summary>Strike half-window for the probe→chain expansion above, as a fraction of spot. Wide enough to
	/// cover calendar/diagonal long legs (which can sit a few % OTM) without pricing the whole deep-wing tail.</summary>
	private const decimal ChainExpandPct = 0.06m;
}
