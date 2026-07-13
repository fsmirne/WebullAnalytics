using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Black-Scholes-priced option quote source for backtests. Underlying spot comes from each ticker's
/// daily close (<see cref="HistoricalBarCache"/>); IV comes from <see cref="BacktestIVProvider"/>.
/// Synthesizes a symmetric bid/ask around the theoretical mid using a per-ticker spread model so
/// the candidate scorer's liquidity/realized-EV checks see friction that resembles the live tape.
/// SPY uses a mid→spread curve empirically calibrated to real EOD NBBO (see <see cref="SpySpreadKnots"/>);
/// other penny-pilot ETFs + cash-settled index roots get the tight parametric band; everything else gets
/// the wider per-share floor typical of single-stock options (matching e.g. the GME quotes seen in
/// production — bid 0.30 / ask 0.35 on a $0.325 mid).
/// </summary>
internal sealed class BacktestQuoteSource : IBacktestQuoteSource
{
	// Penny-pilot index ETFs: minimum half-spread $0.005 ($0.01 total), plus 0.5% of mid.
	private const decimal IndexHalfSpreadFloor = 0.005m;
	private const decimal IndexHalfSpreadPct = 0.005m;
	// Single-stock single-name options: minimum half-spread $0.01 ($0.02 total), plus 5% of mid.
	// This roughly reproduces real GME-style quotes where mid-priced contracts trade one
	// nickel-tick wide and very cheap (sub-$0.10) contracts trade one penny-tick wide.
	private const decimal SingleStockHalfSpreadFloor = 0.01m;
	private const decimal SingleStockHalfSpreadPct = 0.05m;

	// Penny-pilot ETFs + cash-settled index options (SPX/SPXW/XSP/NDX). Index options aren't formally
	// "penny pilot" but they trade in tight $0.05 ticks under $3 / $0.10 above, which is closer to the
	// SPY/QQQ regime than to single-stock single-name option spreads.
	private static readonly HashSet<string> PennyPilotIndexEtfs = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPY", "QQQ", "IWM", "DIA", "SPX", "SPXW", "XSP", "NDX"
	};

	// One trading day = 6.5h. Used to give 0DTE a non-zero time component so the open captures
	// a full day's theta before the same-step expiry settles at intrinsic. Without this, BlackScholes
	// returns intrinsic at both ends and the position can never collect time premium.
	private const double ZeroDteTimeYears = 6.5 / 24.0 / 365.0;
	// Half-session TTE — used as the approximation for "current moment is the middle of the session"
	// when re-pricing positions at the day's High/Low for intraday stop-loss / take-profit checks.
	// Without minute-bar history we can't know when the extreme actually occurred; mid-session is the
	// expected midpoint and avoids systematically biasing either toward open (rich extrinsic) or close
	// (zero extrinsic). Exposed as a constant so the same value is used by the intraday-trigger pass
	// in <see cref="BacktestRunner"/>.
	internal const double IntradayHalfSessionTimeYears = 3.25 / 24.0 / 365.0;

	private readonly HistoricalBarCache _bars;
	private readonly BacktestIVProvider _iv;
	private readonly double _riskFreeRate;
	private readonly IReadOnlyDictionary<string, decimal>? _spotOverrides;
	private readonly IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? _dividendsByRoot;
	// Per-day per-contract open interest (+ IV) from the scraped chain snapshots. The captured option BARS
	// carry no OI, so the GEX / max-pain factors are inert in the backtest without this. When present, every
	// quote we return for a contract that exists in the day's snapshot gets its real OI (and a snapshot IV for
	// the OI-only ladder markers, which ComputeGex needs to weight gamma). Null → unchanged (OI stays absent).
	private readonly ChainSnapshotOiCache? _oiCache;

	/// <param name="spotOverrides">When supplied for a ticker, replaces the bar.open lookup for that
	/// ticker. Used by <c>ai scan --theoretical</c> to evaluate a hypothetical spot at an asOf for which
	/// no historical bar exists (next-business-day previews) or a stress scenario at any spot level.</param>
	/// <param name="dividendsByRoot">Optional per-root historical dividend schedules (from
	/// <see cref="HistoricalDividendCache"/>). When supplied, every Black-Scholes forward AND inverse below
	/// prices on the dividend-adjusted spot for the leg's expiry window — matching the live dividend-aware
	/// path. This prices synthetic legs on the correct reduced forward. Null (or a root with no schedule)
	/// leaves that root unadjusted (q=0), unchanged behaviour — correct for cash-settled index roots
	/// (SPX/SPXW/XSP).</param>
	public BacktestQuoteSource(HistoricalBarCache bars, BacktestIVProvider iv, double riskFreeRate, IReadOnlyDictionary<string, decimal>? spotOverrides = null, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividendsByRoot = null, ChainSnapshotOiCache? oiCache = null)
	{
		_bars = bars;
		_iv = iv;
		_riskFreeRate = riskFreeRate;
		_spotOverrides = spotOverrides;
		_dividendsByRoot = dividendsByRoot;
		_oiCache = oiCache;
	}

	/// <summary>Spot reduced by the present value of every known dividend going ex within
	/// (<paramref name="asOf"/>, <paramref name="expiryDate"/> close] for <paramref name="root"/>. Returns
	/// <paramref name="spot"/> unchanged when no schedule is known or none falls in the window — so the
	/// no-dividend path is bit-for-bit the old behaviour. Used for BOTH the BS price and the IV back-solve so
	/// the surface and the legs it prices share one consistent forward.</summary>
	private decimal AdjSpot(string root, decimal spot, DateTime asOf, DateTime expiryDate)
	{
		if (_dividendsByRoot == null || !_dividendsByRoot.TryGetValue(root, out var divs) || divs.Count == 0) return spot;
		return OptionMath.DividendAdjustedSpot(spot, divs, asOf, expiryDate.Date + OptionMath.MarketClose, _riskFreeRate);
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation, QuoteOverrides overrides = default)
	{
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		// Use the day's OPEN as the spot price the opener sees: the daily simulator step is
		// conceptually "morning of day X" — strikes get picked relative to the open, the position
		// holds across the session, and SettleExpirationsAsync settles 0DTE expiries at bar.Close.
		// Without this, strike selection and settlement both use Close, so a delta-X% OTM strike
		// is OTM at picking-time AND at settle-time, producing an unrealistic 100%-win-rate backtest.
		foreach (var ticker in tickers)
		{
			// Precedence: per-call overrides.Spots (set by BacktestRunner's intraday opener loop)
			// wins over the construction-time _spotOverrides (--theoretical), which wins over bar.Open.
			if (overrides.Spots != null && overrides.Spots.TryGetValue(ticker, out var minuteSpot))
			{
				underlyings[ticker] = minuteSpot;
				continue;
			}
			if (_spotOverrides != null && _spotOverrides.TryGetValue(ticker, out var spotOverride))
			{
				underlyings[ticker] = spotOverride;
				continue;
			}
			var bar = await _bars.GetBarAsync(ticker, asOf.Date, cancellation);
			if (bar != null) underlyings[ticker] = bar.Open;
		}

		// Parse once, collect all (sym, parsed) pairs, and resolve any root spots not yet in underlyings.
		// Pre-parsing avoids reparsing inside the parallel loop and lets us load all root spots and ATM
		// IVs serially up front — both are async + cache-backed, and parallelizing those would compete
		// for the same in-memory dictionary on first miss.
		var ladderMarkers = new List<string>(); // listed-but-no-bar strikes → cheap OI markers (NOT priced)
		var parsedSymbols = new List<(string Symbol, OptionParsed Parsed)>(optionSymbols.Count);
		foreach (var sym in optionSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p != null) parsedSymbols.Add((sym, p));
		}

		var roots = parsedSymbols.Select(t => t.Parsed.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (var root in roots)
		{
			if (underlyings.ContainsKey(root)) continue;
			if (overrides.Spots != null && overrides.Spots.TryGetValue(root, out var minuteSpot))
			{
				underlyings[root] = minuteSpot;
				continue;
			}
			if (_spotOverrides != null && _spotOverrides.TryGetValue(root, out var spotOverride))
			{
				underlyings[root] = spotOverride;
				continue;
			}
			var bar = await _bars.GetBarAsync(root, asOf.Date, cancellation);
			if (bar != null) underlyings[root] = bar.Open;
		}

		// One smile-scale lookup per (root, asOf), and one ATM IV lookup per (root, dte). ATM IV is now
		// DTE-aware (VIX1D / VIX9D / VIX per term), so a chain that mixes 0DTE and 7DTE legs resolves
		// to different ATM anchors. Pre-collect unique (root, dte) pairs so the per-strike fan-out
		// below doesn't re-hit the VIX/HV caches.
		var smileScaleByRoot = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			smileScaleByRoot[root] = await _iv.GetSmileScaleAsync(root, asOf, cancellation);
		}
		var atmByRootDte = new Dictionary<(string Root, int Dte), decimal?>();
		foreach (var (_, parsed) in parsedSymbols)
		{
			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
			var key = (parsed.Root, dte);
			if (atmByRootDte.ContainsKey(key)) continue;
			atmByRootDte[key] = await _iv.GetAtmIVAsync(parsed.Root, asOf, dte, cancellation);
		}

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, parsed) in parsedSymbols)
		{
			if (!underlyings.TryGetValue(parsed.Root, out var spot)) continue;
			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
			atmByRootDte.TryGetValue((parsed.Root, dte), out var atm);
			smileScaleByRoot.TryGetValue(parsed.Root, out var smileScale);

			// Parametric Black-Scholes price from the VIX-anchored ATM IV + smile. 0DTE TTE is the per-call
			// override (remaining session) for the intraday loop, else the day-step constant. Falls back to
			// raw intrinsic only when no ATM IV is available for the term.
			var timeYears = dte <= 0
				? (overrides.ZeroDteTimeYears ?? ZeroDteTimeYears)
				: dte / 365.0;
			decimal price;
			decimal? iv = null;
			if (atm.HasValue)
				iv = _iv.ApplySmile(atm.Value, parsed.Root, parsed.Strike, spot, smileScale);
			if (iv.HasValue)
				price = OptionMath.BlackScholes(AdjSpot(parsed.Root, spot, asOf, parsed.ExpiryDate), parsed.Strike, timeYears, _riskFreeRate, iv.Value, parsed.CallPut!);
			else
				price = parsed.CallPut == "C" ? Math.Max(0m, spot - parsed.Strike) : Math.Max(0m, parsed.Strike - spot);

			var halfSpread = HalfSpreadFor(parsed.Root, price);
			var bid = Math.Max(0m, price - halfSpread);
			var ask = price + halfSpread;
			// Attach the contract's real open interest from the day's chain snapshot (static intraday) when
			// available — this is what lets the GEX / max-pain factors see a real chain in the backtest. The
			// captured-bar IV stays as priced (back-solved from the bar midpoint — a fresher gamma input than
			// the snapshot's representative-minute IV).
			long? oi = null;
			if (_oiCache != null && _oiCache.ForDay(parsed.Root, asOf.Date).TryGetValue(sym, out var snap)) oi = snap.Oi;
			options[sym] = new OptionContractQuote(
				ContractSymbol: sym,
				LastPrice: price,
				Bid: bid,
				Ask: ask,
				Change: null,
				PercentChange: null,
				Volume: null,
				OpenInterest: oi,
				ImpliedVolatility: iv,
				HistoricalVolatility: null
			);
		}

		// Inject the cheap "listed" markers for chain strikes that had no bar at the scan minute: open-interest
		// only, no bid/ask, no IV. The StrikeLadder treats them as tradeable (so the opener enumerates these
		// strikes), but they're never synthetic-priced here — the few legs the opener actually selects get
		// real pricing on the Phase-B refetch. Never overwrite a real priced quote.
		foreach (var occ in ladderMarkers)
			if (!options.ContainsKey(occ))
			{
				// Carry the chain snapshot's real OI + IV onto the marker when available, so the full near-money
				// chain (not just the priced candidate legs) contributes to the GEX / max-pain sums. Without the
				// IV, ComputeGex can't weight the strike's gamma and would skip it. Falls back to the bare OI=1
				// ladder marker (StrikeLadder visibility only) when the day has no snapshot.
				long markerOi = 1;
				decimal? markerIv = null;
				var mp = ParsingHelpers.ParseOptionSymbol(occ);
				if (_oiCache != null && mp != null && _oiCache.ForDay(mp.Root, asOf.Date).TryGetValue(occ, out var snap))
				{
					markerOi = snap.Oi;
					markerIv = snap.Iv > 0m ? snap.Iv : (decimal?)null;   // 0 = no usable snapshot IV → leave null (ComputeGex skips its gamma; OI still counts for max-pain)
				}
				options[occ] = new OptionContractQuote(occ, null, null, null, null, null, Volume: null, OpenInterest: markerOi, ImpliedVolatility: markerIv);
			}

		return new QuoteSnapshot(options, underlyings);
	}

	/// <summary>Convenience for the intraday-trigger pass: pre-resolves the smile scale + a per-DTE
	/// map of ATM IV for the legs being re-priced, then calls <see cref="PriceAtSpot"/>. Returns an
	/// empty map if no leg's ATM IV is available for the date.</summary>
	public async Task<IReadOnlyDictionary<string, OptionContractQuote>> GetIntradayQuotesAsync(
		DateTime asOf, string ticker, decimal spot, IEnumerable<string> optionSymbols,
		double zeroDteTimeYears, CancellationToken cancellation)
	{
		var parsed = new List<(string Symbol, OptionParsed Parsed)>();
		foreach (var sym in optionSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p != null) parsed.Add((sym, p));
		}
		var atmByDte = new Dictionary<int, decimal>();
		foreach (var (_, p) in parsed)
		{
			var dte = (p.ExpiryDate.Date - asOf.Date).Days;
			if (atmByDte.ContainsKey(dte)) continue;
			var atm = await _iv.GetAtmIVAsync(ticker, asOf, dte, cancellation);
			if (atm.HasValue) atmByDte[dte] = atm.Value;
		}
		if (atmByDte.Count == 0) return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var smileScale = await _iv.GetSmileScaleAsync(ticker, asOf, cancellation);
		return PriceAtSpot(asOf, optionSymbols, spot, atmByDte, smileScale, zeroDteTimeYears);
	}

	/// <summary>Re-prices a set of option legs at an explicit spot for a single ticker, with an
	/// override for 0DTE time-to-expiry. Used by the intraday-trigger pass in
	/// <see cref="BacktestRunner"/> to compute position MTM at the day's bar.High and bar.Low
	/// without disturbing the cache or refetching ATM IV (passed in by the caller). Non-0DTE legs
	/// use the standard <c>dte/365</c> TTE — a few hours of intraday adjustment is negligible at
	/// 7+ DTE. <paramref name="atmByDte"/> maps each leg's DTE to the ATM IV at that term; missing
	/// keys fall back to the longest-DTE entry (defensive — caller should pre-populate).
	///
	/// <para>This is a purely parametric (Black-Scholes + smile) reprice at a hypothetical spot — the
	/// counterfactual question real NBBO can't answer — which is exactly why <see cref="QuotesQuoteSource"/>
	/// delegates its intraday-trigger / profit-projector reprices here.</para></summary>
	internal IReadOnlyDictionary<string, OptionContractQuote> PriceAtSpot(
		DateTime asOf, IEnumerable<string> optionSymbols, decimal spot,
		IReadOnlyDictionary<int, decimal> atmByDte, decimal smileScale, double zeroDteTimeYears)
	{
		if (atmByDte.Count == 0) return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var fallbackAtm = atmByDte.OrderByDescending(kv => kv.Key).First().Value;

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var sym in optionSymbols)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;

			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
			var atmIv = atmByDte.TryGetValue(dte, out var hit) ? hit : fallbackAtm;
			var iv = _iv.ApplySmile(atmIv, parsed.Root, parsed.Strike, spot, smileScale) ?? atmIv;
			var timeYears = dte <= 0 ? zeroDteTimeYears : dte / 365.0;
			var price = OptionMath.BlackScholes(AdjSpot(parsed.Root, spot, asOf, parsed.ExpiryDate), parsed.Strike, timeYears, _riskFreeRate, iv, parsed.CallPut);

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
				HistoricalVolatility: null);
		}
		return options;
	}

	private static decimal HalfSpreadFor(string ticker, decimal mid)
	{
		// SPY has its own empirically-calibrated curve; everything else uses the parametric band.
		if (string.Equals(ticker, "SPY", StringComparison.OrdinalIgnoreCase))
			return SpyHalfSpread(mid);
		var (floor, pct) = PennyPilotIndexEtfs.Contains(ticker)
			? (IndexHalfSpreadFloor, IndexHalfSpreadPct)
			: (SingleStockHalfSpreadFloor, SingleStockHalfSpreadPct);
		return Math.Max(floor, mid * pct);
	}

	// Empirically-calibrated SPY mid→full-spread curve. Each knot is the MEAN bid-ask spread of real EOD
	// NBBO from the 2020–2022 SPY option chain (Kaggle/OptionsDX), restricted to near-the-money (≤5% from
	// spot), actually-traded (volume>0), DTE≥1 contracts. MEAN (not median) because a fill crosses the
	// realized spread on every trade and the distribution is right-skewed. The curve is mid-driven:
	// moneyness, DTE, and vol regime all act through mid (verified — they collapse once conditioned on
	// mid), so no extra inputs are needed. %-of-mid is U-shaped — ~8% at the penny floor, dipping to ~1.4%
	// around $2–10, then widening again on expensive far-dated/high-IV legs. EOD-calibrated, so this is a
	// mild floor for 09:30-open realism (open spreads run wider; no intraday NBBO exists to capture that).
	private static readonly (decimal Mid, decimal Spread)[] SpySpreadKnots =
	{
		(0.134m, 0.0113m), (0.368m, 0.0142m), (0.620m, 0.0179m), (0.872m, 0.0217m),
		(1.245m, 0.0265m), (1.746m, 0.0318m), (2.494m, 0.0394m), (3.991m, 0.0578m),
		(6.232m, 0.0953m), (8.729m, 0.1523m), (12.429m, 0.2551m), (19.396m, 0.5032m),
		(32.363m, 1.3841m),
	};

	/// <summary>Half of the calibrated SPY full-spread at <paramref name="mid"/>: piecewise-linear
	/// interpolation between <see cref="SpySpreadKnots"/> (sorted ascending by mid), flat-extrapolated
	/// beyond either end, and never tighter than half a penny so the total spread is at least one $0.01
	/// tick. Returns the HALF-spread — the model brackets the theoretical mid symmetrically.</summary>
	private static decimal SpyHalfSpread(decimal mid)
	{
		var knots = SpySpreadKnots;
		decimal full;
		if (mid <= knots[0].Mid) full = knots[0].Spread;
		else if (mid >= knots[^1].Mid) full = knots[^1].Spread;
		else
		{
			full = knots[^1].Spread;
			for (var i = 1; i < knots.Length; i++)
			{
				if (mid > knots[i].Mid) continue;
				var (m0, s0) = knots[i - 1];
				var (m1, s1) = knots[i];
				full = s0 + (s1 - s0) * (mid - m0) / (m1 - m0);
				break;
			}
		}
		return Math.Max(0.005m, full / 2m);
	}
}
