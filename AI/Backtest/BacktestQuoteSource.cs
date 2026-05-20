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
			if (_spotOverrides != null && _spotOverrides.TryGetValue(root, out var spotOverride))
			{
				underlyings[root] = spotOverride;
				continue;
			}
			var bar = await _bars.GetBarAsync(root, asOf.Date, cancellation);
			if (bar != null) underlyings[root] = bar.Open;
		}

		// One ATM IV + one smile-scale lookup per (root, asOf). Both are pure functions of (atm, strike,
		// spot, scale, ticker) once resolved, so the per-strike work fans out below without re-touching
		// the VIX/HV/SMILE caches.
		var atmByRoot = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
		var smileScaleByRoot = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			atmByRoot[root] = await _iv.GetAtmIVAsync(root, asOf, cancellation);
			smileScaleByRoot[root] = await _iv.GetSmileScaleAsync(root, asOf, cancellation);
		}

		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, parsed) in parsedSymbols)
		{
			if (!underlyings.TryGetValue(parsed.Root, out var spot)) continue;
			atmByRoot.TryGetValue(parsed.Root, out var atm);
			smileScaleByRoot.TryGetValue(parsed.Root, out var smileScale);

			decimal price;
			decimal? iv = null;
			if (atm.HasValue)
			{
				iv = _iv.ApplySmile(atm.Value, parsed.Root, parsed.Strike, spot, smileScale);
				var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
				// 0DTE: at the daily step we're conceptually at the open of the trading day, not at 15:45 —
				// price as if a full session of theta remains. Settlement (Expire) uses intrinsic so the
				// time component is collected, not double-counted.
				var timeYears = dte <= 0 ? ZeroDteTimeYears : dte / 365.0;
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

	/// <summary>Convenience for the intraday-trigger pass: looks up ATM IV + smile scale for the
	/// ticker once, then re-prices every leg at the supplied <paramref name="spot"/> using
	/// <see cref="PriceAtSpot"/>. Returns an empty map if ATM IV is unavailable for the date.</summary>
	internal async Task<IReadOnlyDictionary<string, OptionContractQuote>> GetIntradayQuotesAsync(
		DateTime asOf, string ticker, decimal spot, IEnumerable<string> optionSymbols,
		double zeroDteTimeYears, CancellationToken cancellation)
	{
		var atm = await _iv.GetAtmIVAsync(ticker, asOf, cancellation);
		if (!atm.HasValue) return new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var smileScale = await _iv.GetSmileScaleAsync(ticker, asOf, cancellation);
		return PriceAtSpot(asOf, optionSymbols, spot, atm.Value, smileScale, zeroDteTimeYears);
	}

	/// <summary>Re-prices a set of option legs at an explicit spot for a single ticker, with an
	/// override for 0DTE time-to-expiry. Used by the intraday-trigger pass in
	/// <see cref="BacktestRunner"/> to compute position MTM at the day's bar.High and bar.Low
	/// without disturbing the cache or refetching ATM IV (passed in by the caller). Non-0DTE legs
	/// use the standard <c>dte/365</c> TTE — a few hours of intraday adjustment is negligible at
	/// 7+ DTE.</summary>
	internal IReadOnlyDictionary<string, OptionContractQuote> PriceAtSpot(
		DateTime asOf, IEnumerable<string> optionSymbols, decimal spot,
		decimal atmIv, decimal smileScale, double zeroDteTimeYears)
	{
		var options = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var sym in optionSymbols)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;

			var iv = _iv.ApplySmile(atmIv, parsed.Root, parsed.Strike, spot, smileScale) ?? atmIv;
			var dte = (parsed.ExpiryDate.Date - asOf.Date).Days;
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
