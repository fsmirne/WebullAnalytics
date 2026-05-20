namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Resolves an implied-volatility input for Black-Scholes pricing during a backtest. Two paths:
///   - <c>SPY</c>: VIX/100 from the shared <see cref="HistoricalBarCache"/> (real options-implied vol,
///     since VIX is derived from SPX option chain and SPY tracks SPX). Weekend / holiday dates fall
///     back to the nearest prior settled session within 5 days.
///   - everything else: trailing 30-day realized HV from Adj Close × a configurable IV/HV premium
///     (default 1.15, reflects the volatility risk premium that makes options trade above realized).
/// Both paths layer a volatility smile on top using a V-shape model:
///   <c>iv(K) = atm * (1 + linear*m + curvature*|m|)</c>, clamped to ±50% / −30% of ATM,
///   where <c>m = (K-S)/S</c>.
///   - <c>linear</c> &lt; 0 produces left-skew (OTM puts richer than OTM calls).
///   - <c>curvature</c> &gt; 0 produces a V-shape (both wings richer than ATM).
/// A V-shape (linear in |m|) fits real chains better than a quadratic near ATM — quadratic barely
/// moves at 1-2% OTM, which is where narrow-wing structures like IronButterflies live, and that was
/// the bug that made them look like free money in the backtest. Index tickers (SPY/QQQ/IWM) use a
/// mild profile; everything else uses a stronger profile reflecting single-stock wing premia.
/// </summary>
internal sealed class BacktestIVProvider
{
	private readonly HistoricalBarCache _bars;
	private readonly SmileIndexCache? _smile;
	private readonly decimal _ivHvPremium;
	private readonly bool _smileEnabled;

	// Index profile: steep left skew + V-curvature, calibrated against a live SPXW 0DTE snapshot
	// (2026-05-20 spot $7,424, VIX-implied ATM ~16.2%, observed wing IV ~25%, CBOE SMILE = 2462.81).
	// The prior values (linear=-1, curvature=1) produced near-flat IV across the near-OTM strikes
	// the 0DTE opener actually trades, which made BS-priced fills underprice puts by 3-5× and
	// inflated long-premium backtest P&L.
	//
	// These constants are anchored to <see cref="SmileAnchorValue"/>. When a <see cref="SmileIndexCache"/>
	// is provided, the constants are scaled per-day by <c>SMILE(asOf) / SmileAnchorValue</c> so the smile
	// steepness in the backtest tracks the regime — calm days (SMILE ~2,300) produce a flatter smile,
	// stressed days (SMILE ~2,600) produce steeper. Without a smile cache the constants are used as-is
	// (anchored to today, valid for live theoretical scans).
	private const decimal IndexLinearSkew = -8.0m;
	private const decimal IndexCurvature = 20.0m;
	private const decimal SmileAnchorValue = 2462.81m;
	// Single-stock equity profile: mild left skew, strong V-curvature lifting both wings.
	private const decimal EquityLinearSkew = -0.5m;
	private const decimal EquityCurvature = 3.0m;
	// Sanity clamps on the smile multiplier. Ceiling lifted from 0.50 to 1.50 so the steep index
	// profile can reach the observed +60% wing uplift without saturating on the way out.
	private const decimal SmileFloor = -0.30m;
	private const decimal SmileCeiling = 1.50m;

	// VIX is the 30-day implied vol of SPX options, so any ticker that tracks the S&P 500 — SPY (ETF),
	// SPX (full-size index), SPXW (PM-settled weeklies), XSP (mini-SPX index) — should read IV from VIX
	// directly rather than from realized vol.
	private static readonly HashSet<string> VixDrivenTickers = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPY", "SPX", "SPXW", "XSP"
	};
	private const int HvLookbackDays = 30;

	/// <param name="ivHvPremium">Multiplier applied to historical vol to approximate IV (non-SPY tickers). Default 1.15.</param>
	/// <param name="smileEnabled">Apply the per-strike smile on top of ATM IV. Off = flat IV across all strikes.</param>
	/// <param name="smile">Optional CBOE SMILE Index cache. When provided, index-class smile coefficients
	/// scale per-day by <c>SMILE(asOf) / SmileAnchorValue</c>. When null, constants are used as-is.</param>
	public BacktestIVProvider(HistoricalBarCache bars, decimal ivHvPremium = 1.15m, bool smileEnabled = true, SmileIndexCache? smile = null)
	{
		_bars = bars;
		_smile = smile;
		_ivHvPremium = ivHvPremium;
		_smileEnabled = smileEnabled;
	}

	public async Task<decimal?> GetIVAsync(string ticker, DateTime asOf, decimal strike, decimal spot, string callPut, CancellationToken cancellation)
	{
		var atm = await GetAtmIVAsync(ticker, asOf, cancellation);
		if (!atm.HasValue) return null;
		var smileScale = await GetSmileScaleAsync(ticker, asOf, cancellation);
		return ApplySmile(atm.Value, ticker, strike, spot, smileScale);
	}

	/// <summary>Resolves the per-day smile scale factor for <paramref name="ticker"/> at <paramref name="asOf"/>.
	/// Index-class tickers use <c>SMILE(asOf)/SmileAnchorValue</c> when a smile cache is wired up; falls
	/// back to 1 (constants as-anchored) if the cache is absent or has no value for the date. Equity-class
	/// tickers always use 1 — there's no comparable broad-market smile index for single names.</summary>
	internal async Task<decimal> GetSmileScaleAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		if (_smile == null || !IsIndexClass(ticker)) return 1m;
		var v = await _smile.GetValueAsync(asOf, cancellation);
		if (!v.HasValue || v.Value <= 0m) return 1m;
		return v.Value / SmileAnchorValue;
	}

	/// <summary>Synchronous smile application. Splits the async ATM-IV + smile-scale lookup from the
	/// per-strike math so they amortize across thousands of per-strike pricings in parallel — see
	/// <see cref="BacktestQuoteSource"/>. Pure function; safe to call concurrently.</summary>
	public decimal? ApplySmile(decimal atm, string ticker, decimal strike, decimal spot, decimal smileScale = 1m)
	{
		if (atm <= 0m || spot <= 0m) return atm;
		if (!_smileEnabled) return atm;

		var (linearSkew, curvature) = GetSmileParams(ticker);
		linearSkew *= smileScale;
		curvature *= smileScale;
		var m = (strike - spot) / spot;
		var absM = m < 0m ? -m : m;
		var rawSmile = linearSkew * m + curvature * absM;
		var smile = Math.Clamp(rawSmile, SmileFloor, SmileCeiling);
		var iv = atm * (1m + smile);
		// Final clamp: IV can't be negative, and don't let extreme strikes blow up past 300%.
		return Math.Clamp(iv, 0.05m, 3m);
	}

	private static bool IsIndexClass(string ticker) =>
		VixDrivenTickers.Contains(ticker)
		|| string.Equals(ticker, "QQQ", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(ticker, "IWM", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(ticker, "NDX", StringComparison.OrdinalIgnoreCase);

	private static (decimal linearSkew, decimal curvature) GetSmileParams(string ticker) =>
		IsIndexClass(ticker) ? (IndexLinearSkew, IndexCurvature) : (EquityLinearSkew, EquityCurvature);

	internal async Task<decimal?> GetAtmIVAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		if (VixDrivenTickers.Contains(ticker))
		{
			var vix = await GetVixCloseAsync(asOf, cancellation);
			if (vix.HasValue) return vix.Value / 100m;
			// Fall through to HV if VIX unavailable.
		}

		var closes = await _bars.GetRecentAdjClosesAsync(ticker, HvLookbackDays + 1, asOf, cancellation);
		var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
		if (!hv.HasValue || hv.Value <= 0m) return null;
		return hv.Value * _ivHvPremium;
	}

	/// <summary>VIX close at <paramref name="asOf"/>, walking back up to 5 calendar days for
	/// weekends / holidays. Mirrors the prior <c>HistoricalVixCache.GetVixAsync</c> behavior now that
	/// VIX shares the OHLC bar cache with every other ticker.</summary>
	private async Task<decimal?> GetVixCloseAsync(DateTime asOf, CancellationToken cancellation)
	{
		for (var i = 0; i <= 5; i++)
		{
			var bar = await _bars.GetBarAsync("VIX", asOf.Date.AddDays(-i), cancellation);
			if (bar != null) return bar.Close;
		}
		return null;
	}
}
