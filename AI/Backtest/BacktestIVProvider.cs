namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Resolves an implied-volatility input for Black-Scholes pricing during a backtest. Two paths:
///   - SPX-family tickers (SPY, SPX, SPXW, XSP): ATM IV is anchored to the VIX-family term matching
///     the option's days-to-expiry — VIX1D for 0–1 DTE, VIX9D for 2–9 DTE, VIX (30-day) for 10+ DTE.
///     This closes the systematic underpricing of short-dated options where 30-day VIX undershoots
///     true 0DTE IV by 8–10 percentage points (gamma risk premium, pin / event risk). Weekend /
///     holiday dates fall back to the nearest prior settled session within 5 days. If VIX1D / VIX9D
///     is unavailable (pre-2023 dates, or missing from cache), it falls through to the next-longer
///     term so historical backtests pre-VIX1D launch (2023-04-24) still produce a value.
///   - everything else: trailing 30-day realized HV from Adj Close × a configurable IV/HV premium
///     (default 1.15, reflects the volatility risk premium that makes options trade above realized).
/// Both paths layer a volatility smile on top:
///   <c>iv(K) = atm * (1 + linear*m + curvature*|m|)</c>, clamped to ±50% / −30% of ATM,
///   where <c>m = (K-S)/S</c>.
///   - <c>linear</c> &lt; 0 + <c>curvature</c> &gt; 0 on the put wing (m &lt; 0) produces the steep
///     OTM-put lift typical of SPX (crash insurance premium).
///   - Index tickers go flat on the call wing (m ≥ 0): linear = curvature = 0. This matches the
///     observed SPX "reverse skew" / smirk where OTM call IV sits at or just below ATM.
/// Single-stock tickers retain a symmetric V-shape on both sides — single-name flow doesn't show
/// the same put-side risk premium and the V-shape is a reasonable approximation either way.
/// </summary>
internal sealed class BacktestIVProvider
{
	private readonly HistoricalBarCache _bars;
	private readonly SmileIndexCache? _smile;
	private readonly decimal _ivHvPremium;
	private readonly bool _smileEnabled;

	// Index profile: asymmetric "reverse skew" / SPX smirk. The put wing is lifted steeply (high IV
	// on OTM puts; classic crash-insurance premium); the call wing is essentially flat (OTM call IV
	// is at or slightly below ATM). Calibrated against a live SPXW 0DTE snapshot 2026-05-21 09:30
	// (spot $7,403, VIX1D-anchored ATM ~25.8%, OTM-call IVs flat at 25.78–25.80% from m=0.005 out
	// to m=0.009, near-ATM call IVs 26.14% at m=0.0037 → IV declines toward flat as m increases).
	// The prior values (Put / Call shared `linear=-8, curvature=20`) produced a symmetric V-shape
	// that artificially lifted OTM call IV to 27% at m=0.009, mispricing wide LongCallVerticals.
	//
	// These constants are anchored to <see cref="SmileAnchorValue"/>. When a <see cref="SmileIndexCache"/>
	// is provided, the constants are scaled per-day by <c>SMILE(asOf) / SmileAnchorValue</c> so the smile
	// steepness in the backtest tracks the regime — calm days (SMILE ~2,300) produce a flatter smile,
	// stressed days (SMILE ~2,600) produce steeper. Without a smile cache the constants are used as-is
	// (anchored to today, valid for live theoretical scans).
	private const decimal IndexLinearSkewPut = -8.0m;
	private const decimal IndexCurvaturePut = 20.0m;
	private const decimal IndexLinearSkewCall = 0.0m;
	private const decimal IndexCurvatureCall = 0.0m;
	private const decimal SmileAnchorValue = 2462.81m;
	// Single-stock equity profile: mild left skew, strong V-curvature lifting both wings. Single-name
	// smiles are flatter and more symmetric than index smiles, so the V-shape is retained.
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

	public async Task<decimal?> GetIVAsync(string ticker, DateTime asOf, decimal strike, decimal spot, string callPut, int dteDays, CancellationToken cancellation)
	{
		var atm = await GetAtmIVAsync(ticker, asOf, dteDays, cancellation);
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

		var m = (strike - spot) / spot;
		var (linearSkew, curvature) = GetSmileParams(ticker, isCallSide: m >= 0m);
		linearSkew *= smileScale;
		curvature *= smileScale;
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

	/// <summary>Picks the smile coefficients for <paramref name="ticker"/> on the side of the chain
	/// being priced. Index tickers use an asymmetric profile: steep V-shape on the put wing (m &lt; 0,
	/// <paramref name="isCallSide"/>=false), flat on the call wing (m &gt;= 0). Single-stock tickers
	/// use the symmetric V-shape for both sides — single-name smiles don't show the same SPX-style
	/// reverse skew, and equity-class flow is symmetric enough that one profile fits both wings.</summary>
	private static (decimal linearSkew, decimal curvature) GetSmileParams(string ticker, bool isCallSide)
	{
		if (IsIndexClass(ticker))
		{
			return isCallSide
				? (IndexLinearSkewCall, IndexCurvatureCall)
				: (IndexLinearSkewPut, IndexCurvaturePut);
		}
		return (EquityLinearSkew, EquityCurvature);
	}

	/// <summary>ATM IV for <paramref name="ticker"/> at <paramref name="asOf"/>, anchored to the
	/// VIX-family term matching the option's <paramref name="dteDays"/> for SPX-family tickers.
	/// Non-SPX tickers ignore <paramref name="dteDays"/> and use HV × premium.</summary>
	internal async Task<decimal?> GetAtmIVAsync(string ticker, DateTime asOf, int dteDays, CancellationToken cancellation)
	{
		if (VixDrivenTickers.Contains(ticker))
		{
			// Pick the VIX term that matches the option's life. 0–1 DTE wants the 1-day vol surface
			// (gamma / event risk premium that 30-day VIX does not see); 2–9 DTE wants VIX9D; longer
			// dated options use 30-day VIX. Each term falls back to the next-longer term if missing
			// (e.g., VIX1D unavailable pre-2023-04-24, weekend / holiday gaps within 5 days).
			if (dteDays <= 1)
			{
				var vix1d = await GetVixSeriesCloseAsync("VIX1D", asOf, cancellation);
				if (vix1d.HasValue) return vix1d.Value / 100m;
			}
			if (dteDays <= 9)
			{
				var vix9d = await GetVixSeriesCloseAsync("VIX9D", asOf, cancellation);
				if (vix9d.HasValue) return vix9d.Value / 100m;
			}
			var vix = await GetVixSeriesCloseAsync("VIX", asOf, cancellation);
			if (vix.HasValue) return vix.Value / 100m;
			// Fall through to HV if all VIX series unavailable.
		}

		var closes = await _bars.GetRecentAdjClosesAsync(ticker, HvLookbackDays + 1, asOf, cancellation);
		var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
		if (!hv.HasValue || hv.Value <= 0m) return null;
		return hv.Value * _ivHvPremium;
	}

	/// <summary>Most recent settled close for a VIX-family series (<c>VIX</c>, <c>VIX1D</c>,
	/// <c>VIX9D</c>) strictly before <paramref name="asOf"/>. The close for <c>asOf.Date</c> itself is
	/// published at 16:00 ET on that date and is not available to the 09:30 backtest decision; using it
	/// would be lookahead. Walks back up to 5 calendar days to skip weekends / holidays. Returns null
	/// if no bar exists in that window — caller falls back to a longer-term series.</summary>
	private async Task<decimal?> GetVixSeriesCloseAsync(string series, DateTime asOf, CancellationToken cancellation)
	{
		for (var i = 1; i <= 5; i++)
		{
			var bar = await _bars.GetBarAsync(series, asOf.Date.AddDays(-i), cancellation);
			if (bar != null) return bar.Close;
		}
		return null;
	}
}
