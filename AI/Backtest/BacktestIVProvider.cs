namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Resolves an implied-volatility input for Black-Scholes pricing during a backtest. Two paths:
///   - <c>SPY</c>: VIX/100 from <see cref="HistoricalVixCache"/> (real options-implied vol, since
///     VIX is derived from SPX option chain and SPY tracks SPX).
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
	private readonly HistoricalVixCache _vix;
	private readonly HistoricalBarCache _bars;
	private readonly decimal _ivHvPremium;
	private readonly bool _smileEnabled;

	// Index profile: pronounced left skew, mild V-curvature.
	private const decimal IndexLinearSkew = -1.0m;
	private const decimal IndexCurvature = 1.0m;
	// Single-stock equity profile: mild left skew, strong V-curvature lifting both wings.
	private const decimal EquityLinearSkew = -0.5m;
	private const decimal EquityCurvature = 3.0m;
	// Sanity clamps on the smile multiplier.
	private const decimal SmileFloor = -0.30m;
	private const decimal SmileCeiling = 0.50m;

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
	public BacktestIVProvider(HistoricalVixCache vix, HistoricalBarCache bars, decimal ivHvPremium = 1.15m, bool smileEnabled = true)
	{
		_vix = vix;
		_bars = bars;
		_ivHvPremium = ivHvPremium;
		_smileEnabled = smileEnabled;
	}

	public async Task<decimal?> GetIVAsync(string ticker, DateTime asOf, decimal strike, decimal spot, string callPut, CancellationToken cancellation)
	{
		var atm = await GetAtmIVAsync(ticker, asOf, cancellation);
		if (!atm.HasValue) return null;
		return ApplySmile(atm.Value, ticker, strike, spot);
	}

	/// <summary>Synchronous smile application. Splits the async ATM-IV lookup from the per-strike math
	/// so a single ATM read per (ticker, asOf) can be amortized across thousands of per-strike pricings
	/// in parallel — see <see cref="BacktestQuoteSource"/>. Pure function; safe to call concurrently.</summary>
	public decimal? ApplySmile(decimal atm, string ticker, decimal strike, decimal spot)
	{
		if (atm <= 0m || spot <= 0m) return atm;
		if (!_smileEnabled) return atm;

		var (linearSkew, curvature) = GetSmileParams(ticker);
		var m = (strike - spot) / spot;
		var absM = m < 0m ? -m : m;
		var rawSmile = linearSkew * m + curvature * absM;
		var smile = Math.Clamp(rawSmile, SmileFloor, SmileCeiling);
		var iv = atm * (1m + smile);
		// Final clamp: IV can't be negative, and don't let extreme strikes blow up past 300%.
		return Math.Clamp(iv, 0.05m, 3m);
	}

	private static (decimal linearSkew, decimal curvature) GetSmileParams(string ticker)
	{
		// SPX-family, plus the broad-index ETFs QQQ/IWM/NDX, exhibit the classic index smile.
		if (VixDrivenTickers.Contains(ticker)
			|| string.Equals(ticker, "QQQ", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(ticker, "IWM", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(ticker, "NDX", StringComparison.OrdinalIgnoreCase))
			return (IndexLinearSkew, IndexCurvature);
		return (EquityLinearSkew, EquityCurvature);
	}

	internal async Task<decimal?> GetAtmIVAsync(string ticker, DateTime asOf, CancellationToken cancellation)
	{
		if (VixDrivenTickers.Contains(ticker))
		{
			var vix = await _vix.GetVixAsync(asOf, cancellation);
			if (vix.HasValue) return vix.Value / 100m;
			// Fall through to HV if VIX unavailable.
		}

		var closes = await _bars.GetRecentAdjClosesAsync(ticker, HvLookbackDays + 1, asOf, cancellation);
		var hv = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
		if (!hv.HasValue || hv.Value <= 0m) return null;
		return hv.Value * _ivHvPremium;
	}
}
