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

	/// <summary>Per-call minute spot anchor, settable by <see cref="BacktestRunner"/>'s intraday opener loop.
	/// When set, <see cref="GetQuotesAsync"/> uses this spot per ticker instead of <c>bar.Open</c>, and
	/// 0DTE pricing uses <see cref="MinuteScanZeroDteTimeYears"/> for the remaining time-to-expiry.
	/// Always single-threaded (backtest is serial), so no synchronization required.</summary>
	internal IReadOnlyDictionary<string, decimal>? MinuteScanSpotOverrides { get; set; }
	internal double? MinuteScanZeroDteTimeYears { get; set; }

	/// <param name="spotOverrides">When supplied for a ticker, replaces the bar.open lookup for that
	/// ticker. Used by <c>ai scan --theoretical</c> to evaluate a hypothetical spot at an asOf for which
	/// no historical bar exists (next-business-day previews) or a stress scenario at any spot level.</param>
	public BacktestQuoteSource(HistoricalBarCache bars, BacktestIVProvider iv, double riskFreeRate, IReadOnlyDictionary<string, decimal>? spotOverrides = null)
	{
		_bars = bars;
		_iv = iv;
		_riskFreeRate = riskFreeRate;
		_spotOverrides = spotOverrides;
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		// Use the day's OPEN as the spot price the opener sees: the daily simulator step is
		// conceptually "morning of day X" — strikes get picked relative to the open, the position
		// holds across the session, and SettleExpirationsAsync settles 0DTE expiries at bar.Close.
		// Without this, strike selection and settlement both use Close, so a delta-X% OTM strike
		// is OTM at picking-time AND at settle-time, producing an unrealistic 100%-win-rate backtest.
		foreach (var ticker in tickers)
		{
			// Precedence: minute-scan override (set by BacktestRunner's intraday opener loop) wins
			// over the construction-time spotOverrides (--theoretical), which wins over bar.Open.
			if (MinuteScanSpotOverrides != null && MinuteScanSpotOverrides.TryGetValue(ticker, out var minuteSpot))
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
			if (MinuteScanSpotOverrides != null && MinuteScanSpotOverrides.TryGetValue(root, out var minuteSpot))
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

			decimal price;
			decimal? iv = null;
			if (atm.HasValue)
			{
				iv = _iv.ApplySmile(atm.Value, parsed.Root, parsed.Strike, spot, smileScale);
				// 0DTE TTE: when the intraday opener loop has set MinuteScanZeroDteTimeYears, use it
				// (remaining session from the current minute to 16:00 ET). Otherwise use the day-step
				// constant — conceptually "morning of day X", a full session of theta remains.
				// Settlement (Expire) uses intrinsic so the time component is collected, not double-counted.
				var timeYears = dte <= 0
					? (MinuteScanZeroDteTimeYears ?? ZeroDteTimeYears)
					: dte / 365.0;
				price = OptionMath.BlackScholes(spot, parsed.Strike, timeYears, _riskFreeRate, iv!.Value, parsed.CallPut);
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

	/// <summary>Convenience for the intraday-trigger pass: pre-resolves the smile scale + a per-DTE
	/// map of ATM IV for the legs being re-priced, then calls <see cref="PriceAtSpot"/>. Returns an
	/// empty map if no leg's ATM IV is available for the date.</summary>
	internal async Task<IReadOnlyDictionary<string, OptionContractQuote>> GetIntradayQuotesAsync(
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
	/// keys fall back to the longest-DTE entry (defensive — caller should pre-populate).</summary>
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
			var price = OptionMath.BlackScholes(spot, parsed.Strike, timeYears, _riskFreeRate, iv, parsed.CallPut);

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
				ImpliedVolatility5Day: null);
		}
		return options;
	}

	private static decimal HalfSpreadFor(string ticker, decimal mid)
	{
		var (floor, pct) = PennyPilotIndexEtfs.Contains(ticker)
			? (IndexHalfSpreadFloor, IndexHalfSpreadPct)
			: (SingleStockHalfSpreadFloor, SingleStockHalfSpreadPct);
		return Math.Max(floor, mid * pct);
	}
}
