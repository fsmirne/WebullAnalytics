using WebullAnalytics.Positions;

namespace WebullAnalytics.Pricing;

/// <summary>
/// Pure math and pricing utilities for options: Black-Scholes, intrinsic value, P&L,
/// implied volatility lookup, and price ladder/chart construction.
/// </summary>
internal static class OptionMath
{
	internal static double RiskFreeRate = 0.043; // default; updated at runtime from ^IRX when available
	internal static readonly TimeSpan MarketOpen = new(9, 30, 0);
	// 16:00 ET — equity options cease trading and PM-settled index options settle on the 4:00 close print.
	internal static readonly TimeSpan MarketClose = new(16, 0, 0);

	/// <summary>The instant a mid→IV back-solve (and the time-decay grid's leftmost "current value" column)
	/// should anchor on: the moment the loaded quotes were actually struck. Inverting Black-Scholes at this
	/// instant recovers the IV consistent with how the mid was priced.
	/// <list type="bullet">
	/// <item>Live during RTH → wall-clock now.</item>
	/// <item>Live after today's close → today's 16:00 (the day's last quotes; no further decay was priced).</item>
	/// <item>Live pre-open / weekend / holiday → the most recent trading session's 16:00 close (the last real
	/// quotes — e.g. Friday's close when run on a Saturday), so an off-hours report shows the position's value
	/// as of the last close rather than a phantom value decayed against a dead clock.</item>
	/// <item>Historical --date run → that date's session open (wall-clock "now" is meaningless).</item>
	/// </list></summary>
	internal static DateTime ObservationInstant()
	{
		if (EvaluationDate.IsOverridden) return EvaluationDate.Today + MarketOpen;
		var now = DateTime.Now;
		var todayTrades = MarketCalendar.IsOpen(now.Date);
		if (todayTrades && now >= now.Date + MarketOpen && now <= now.Date + MarketClose) return now;   // live RTH
		if (todayTrades && now > now.Date + MarketClose) return now.Date + MarketClose;                  // after today's close
		return MarketCalendar.PreviousOpenOnOrBefore(now.Date.AddDays(-1)) + MarketClose;                // pre-open / weekend / holiday
	}

	// --- Black-Scholes ---

	/// <summary>
	/// Computes the Black-Scholes theoretical price for a European option.
	/// </summary>
	internal static decimal BlackScholes(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv, string callPut)
	{
		if (timeYears <= 0)
			return Intrinsic(spot, strike, callPut);

		double s = (double)spot, k = (double)strike, sigma = (double)iv, t = timeYears, r = riskFreeRate;
		double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2.0) * t) / (sigma * Math.Sqrt(t));
		double d2 = d1 - sigma * Math.Sqrt(t);

		double price = callPut == "C"
			? s * NormalCdf(d1) - k * Math.Exp(-r * t) * NormalCdf(d2)
			: k * Math.Exp(-r * t) * NormalCdf(-d2) - s * NormalCdf(-d1);

		return (decimal)Math.Max(0, price);
	}

	/// <summary>
	/// Escrowed-dividend adjustment to spot for pricing a leg that may trade through an ex-dividend date.
	/// Subtracts the present value of every cash dividend whose ex-date falls in (<paramref name="evalDate"/>,
	/// <paramref name="expiry"/>] from <paramref name="spot"/>. Pricing the leg on this adjusted spot lowers
	/// the Black-Scholes forward by FV(divs) — the correct treatment for, e.g., the long leg of a calendar
	/// straddling an ex-date (the short leg, expiring before the ex-date, has nothing in its window and is
	/// unchanged). Returns <paramref name="spot"/> unchanged when no dividends fall in the window, so
	/// non-payers and missing data behave exactly as before. A future ex-date already drops out of the
	/// window once <paramref name="evalDate"/> passes it, so the same call is correct for both current-spot
	/// and future-dated scenario pricing.
	/// </summary>
	internal static decimal DividendAdjustedSpot(decimal spot, IReadOnlyList<DividendEvent>? dividends, DateTime evalDate, DateTime expiry, double riskFreeRate)
	{
		if (dividends == null || dividends.Count == 0) return spot;
		decimal pv = 0m;
		foreach (var d in dividends)
		{
			if (d.Amount <= 0m || d.ExDate <= evalDate || d.ExDate > expiry) continue;
			var years = (d.ExDate - evalDate).TotalDays / 365.0;
			pv += d.Amount * (decimal)Math.Exp(-riskFreeRate * years);
		}
		var adjusted = spot - pv;
		// Guard against pathological over-subtraction (divs ≥ spot): a non-positive spot would blow up
		// log(S/K) in Black-Scholes. Such inputs are non-physical, so fall back to the unadjusted spot.
		return adjusted > 0m ? adjusted : spot;
	}

	/// <summary>Computes the Black-Scholes delta for a European option. Returns signed delta: positive for calls, negative for puts.</summary>
	internal static decimal Delta(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv, string callPut)
	{
		if (timeYears <= 0)
			return callPut == "C" ? (spot > strike ? 1m : 0m) : (spot < strike ? -1m : 0m);
		double s = (double)spot, k = (double)strike, sigma = (double)iv, t = timeYears, r = riskFreeRate;
		double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2.0) * t) / (sigma * Math.Sqrt(t));
		return callPut == "C" ? (decimal)NormalCdf(d1) : (decimal)(NormalCdf(d1) - 1.0);
	}

	/// <summary>
	/// Cumulative distribution function of the standard normal distribution.
	/// Uses the Abramowitz & Stegun approximation (accuracy ~1.5e-7).
	/// </summary>
	internal static double NormalCdf(double x)
	{
		const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
		int sign = x < 0 ? -1 : 1;
		x = Math.Abs(x) / Math.Sqrt(2.0);
		double t = 1.0 / (1.0 + p * x);
		double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
		return 0.5 * (1.0 + sign * y);
	}

	/// <summary>Computes intrinsic value of an option at a given underlying price.</summary>
	internal static decimal Intrinsic(decimal underlyingPrice, decimal strike, string callPut) =>
		callPut == "C" ? Math.Max(0, underlyingPrice - strike) : Math.Max(0, strike - underlyingPrice);

	// --- P&L ---

	/// <summary>Calculates P&L for a single option leg at expiration (intrinsic only).</summary>
	internal static decimal OptionPnLAtExpiration(decimal underlyingPrice, decimal strike, string callPut, Side side, int qty, decimal premium)
	{
		var pnlPerContract = side == Side.Buy ? Intrinsic(underlyingPrice, strike, callPut) - premium : premium - Intrinsic(underlyingPrice, strike, callPut);
		return pnlPerContract * qty * 100;
	}

	/// <summary>
	/// Computes P&L for a single leg. Uses Black-Scholes if the leg has time remaining
	/// past the evaluation date; otherwise uses intrinsic value.
	/// </summary>
	internal static decimal LegPnLWithBs(decimal underlyingPrice, OptionParsed parsed, string symbol, Side side, int qty, decimal premium, DateTime evaluationDate, AnalysisOptions opts)
	{
		var legValue = LegContractValueWithBs(underlyingPrice, parsed, symbol, side, evaluationDate, opts);
		var pnlPerContract = side == Side.Buy ? legValue - premium : premium - legValue;
		return pnlPerContract * qty * 100;
	}

	/// <summary>Black-Scholes call signature with the (constant) risk-free rate omitted, so an implementation
	/// can memoize on just the varying inputs. Used by <see cref="LegValueAt"/> to let the opener scorer
	/// inject its per-tick long-leg cache without changing the shared pricing math.</summary>
	internal delegate decimal BlackScholesEvaluator(decimal spot, decimal strike, double timeYears, decimal iv, string callPut);

	/// <summary>
	/// Canonical per-leg contract value at underlying <paramref name="spot"/> and <paramref name="evalDate"/>:
	/// Black-Scholes when the leg still has time past the eval date and an IV is known, intrinsic otherwise.
	/// Dividend-adjusts the spot for any ex-dates the leg trades through (see <see cref="DividendAdjustedSpot"/>).
	/// This is the SINGLE per-leg pricer shared by the position analyzer (report / analyze break-evens and the
	/// time-decay grid) and the opener scorer (calendar/diagonal long-leg value, multi-leg terminal value), so
	/// both engines price an identical leg to the same number.
	/// <paramref name="blackScholes"/> optionally injects a memoized Black-Scholes (the scorer's per-tick
	/// long-leg cache); null uses the direct <see cref="BlackScholes"/>. The evaluator omits the constant
	/// risk-free rate so its cache key matches the inputs that actually vary.
	/// </summary>
	internal static decimal LegValueAt(decimal spot, DateTime evalDate, OptionParsed parsed, decimal? iv, IReadOnlyList<DividendEvent>? dividends, BlackScholesEvaluator? blackScholes = null)
	{
		var expirationTime = parsed.ExpiryDate.Date + MarketClose;
		if (!iv.HasValue || evalDate >= expirationTime)
			return Intrinsic(spot, parsed.Strike, parsed.CallPut);

		var timeYears = (expirationTime - evalDate).TotalDays / 365.0;
		var adjustedSpot = DividendAdjustedSpot(spot, dividends, evalDate, expirationTime, RiskFreeRate);
		return blackScholes is null
			? BlackScholes(adjustedSpot, parsed.Strike, timeYears, RiskFreeRate, iv.Value, parsed.CallPut)
			: blackScholes(adjustedSpot, parsed.Strike, timeYears, iv.Value, parsed.CallPut);
	}

	/// <summary>
	/// Per-share contract value for a position leg, resolving IV (override / calibrated / broker) and the
	/// leg's dividend schedule from <paramref name="opts"/>, then delegating to <see cref="LegValueAt"/>.
	/// </summary>
	internal static decimal LegContractValueWithBs(decimal underlyingPrice, OptionParsed parsed, string symbol, Side side, DateTime evaluationDate, AnalysisOptions opts)
		=> LegValueAt(underlyingPrice, evaluationDate, parsed, GetLegIv(side, symbol, opts), GetLegDividends(parsed.Root, opts));

	/// <summary>Computes total P&L at expiration for all legs (intrinsic value only).</summary>
	internal static decimal StrategyPnLAtExpiration(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty) =>
		legs.Sum(l => OptionPnLAtExpiration(underlyingPrice, l.parsed.Strike, l.parsed.CallPut, l.row.Side, qty, GetPremium(l.row)));

	/// <summary>Computes total P&L using Black-Scholes for legs with remaining time.</summary>
	internal static decimal StrategyPnLWithBs(decimal underlyingPrice, List<(PositionRow row, OptionParsed parsed, string symbol)> legs, int qty, DateTime evaluationDate, AnalysisOptions opts) =>
		legs.Sum(l => LegPnLWithBs(underlyingPrice, l.parsed, l.symbol, l.row.Side, qty, GetPremium(l.row), evaluationDate, opts));

	/// <summary>
	/// Computes total P&L across merged legs where each leg has its own net qty.
	/// Used by the combined break-even analyzer. Unlike <see cref="StrategyPnLWithBs"/>,
	/// this does not assume a uniform per-leg quantity.
	/// </summary>
	internal static decimal StrategyPnLWithBsMixed(decimal underlyingPrice, List<MergedLeg> legs, DateTime evaluationDate, AnalysisOptions opts)
	{
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (leg.IsStock) continue;
			total += LegPnLWithBs(underlyingPrice, leg.Parsed!, leg.Symbol, leg.Side, leg.Qty, leg.Price, evaluationDate, opts);
		}
		return total;
	}

	// --- IV Lookup ---

	internal static decimal? GetLegIv(Side side, string symbol, AnalysisOptions opts)
	{
		if (opts.IvOverrides != null && opts.IvOverrides.TryGetValue(symbol, out var perLegIv))
			return perLegIv;
		// Calibrated IV (back-solved from the leg's live mid) takes precedence over the broker's reported
		// IV so the today column reproduces market mid and future columns decay on the same, mid-consistent
		// surface. A user --iv override still wins; legs that couldn't be calibrated fall through to broker IV.
		if (opts.CalibratedIv != null && opts.CalibratedIv.TryGetValue(symbol, out var calibrated))
			return calibrated;
		if (opts.OptionQuotes != null && opts.OptionQuotes.TryGetValue(symbol, out var quote) && quote.ImpliedVolatility.HasValue && quote.ImpliedVolatility.Value > 0)
			return quote.ImpliedVolatility.Value;
		return null;
	}

	/// <summary>Back-solves the IV that reproduces a leg's market mid `(bid + ask) / 2` at the given
	/// <paramref name="spot"/> (pass the dividend-adjusted spot to keep calibration consistent with how
	/// <see cref="LegContractValueWithBs"/> prices and avoid double-counting the dividend). Returns null —
	/// rather than a fallback IV — when the leg has no two-sided quote, mid ≤ intrinsic, the option has
	/// expired, or the solver fails to converge, so callers can cleanly fall back to broker IV / intrinsic.</summary>
	internal static decimal? TryMarketImpliedIv(string symbol, OptionParsed parsed, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		if (!quotes.TryGetValue(symbol, out var q)) return null;
		if (!q.Bid.HasValue || !q.Ask.HasValue) return null;
		if (q.Bid.Value < 0m || q.Ask.Value <= 0m) return null;
		var mid = (q.Bid.Value + q.Ask.Value) / 2m;
		if (mid <= 0m) return null;
		// Require the *bid* itself to exceed intrinsic, not just the mid. A mid only marginally above
		// intrinsic carries time value smaller than the bid/ask half-spread (bid < intrinsic ⇔
		// extrinsic < half-spread), so the back-solved IV is dominated by quote noise rather than real
		// vol — deep-ITM legs in a wide or stale (off-hours) book collapse to a near-zero IV that then
		// craters POP / expected-move math downstream. Such legs keep the broker/vendor IV instead.
		if (q.Bid.Value <= Intrinsic(spot, parsed.Strike, parsed.CallPut)) return null;
		var t = (parsed.ExpiryDate.Date + MarketClose - asOf).TotalDays / 365.0;
		if (t <= 0) return null;
		try
		{
			var iv = ImpliedVol(spot, parsed.Strike, t, RiskFreeRate, mid, parsed.CallPut);
			if (iv > 0m && iv < 5m) return iv;
		}
		catch { }
		return null;
	}

	/// <summary>Dividend schedule for a leg's underlying root, or null when none is known (no adjustment).</summary>
	internal static IReadOnlyList<DividendEvent>? GetLegDividends(string root, AnalysisOptions opts) =>
		opts.Dividends != null && opts.Dividends.TryGetValue(root, out var divs) ? divs : null;

	// --- Shared helpers ---

	internal static decimal GetPremium(PositionRow row) => row.AdjustedAvgPrice ?? row.AvgPrice;

	// --- Price Ladder / Chart ---

	internal static decimal GetPriceStep(decimal referencePrice) => referencePrice switch
	{
		< 10m => 0.50m,
		< 25m => 1m,
		< 100m => 2.50m,
		< 250m => 5m,
		_ => 10m
	};

	/// <summary>Generates a price ladder of ~10 price points centered around notable prices.</summary>
	internal static List<PricePnL> BuildPriceLadder(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt, Func<decimal, decimal, decimal?> valueAt)
	{
		ComputePriceRange(notablePrices, step, out var min, out var max);

		const int maxExtensions = 50;
		for (int i = 0; i < maxExtensions && Math.Round(pnlAt(max), 2) > 0; i++)
			max += step;
		for (int i = 0; i < maxExtensions && min > 0 && Math.Round(pnlAt(min), 2) > 0; i++)
			min = Math.Max(0, min - step);

		var prices = new SortedSet<decimal>();
		for (var p = min; p <= max + step / 2; p += step)
			prices.Add(Math.Round(p, 2));
		prices.Add(Math.Round(max, 2));
		foreach (var p in notablePrices.Where(np => np >= 0))
			prices.Add(Math.Round(p, 2));

		return prices.Select(p =>
		{
			var pnl = Math.Round(pnlAt(p), 2);
			return new PricePnL(p, pnl, valueAt(p, pnl));
		}).ToList();
	}

	/// <summary>Computes the price range [min, max] for a price ladder or chart.</summary>
	internal static void ComputePriceRange(List<decimal> notablePrices, decimal step, out decimal min, out decimal max)
	{
		min = notablePrices.Min() - 2 * step;
		max = notablePrices.Max() + 2 * step;
		if (min < 0) min = 0;
		while ((max - min) / step + 1 < 8)
		{
			min = Math.Max(0, min - step);
			max += step;
		}
	}

	/// <summary>Generates ~100 evenly-spaced data points for smooth chart rendering.</summary>
	internal static List<PricePnL> BuildChartData(List<decimal> notablePrices, decimal step, Func<decimal, decimal> pnlAt, Func<decimal, decimal, decimal?> valueAt)
	{
		ComputePriceRange(notablePrices, step, out var min, out var max);
		const int pointCount = 100;
		var chartStep = (max - min) / (pointCount - 1);
		var points = new List<PricePnL>(pointCount);
		for (int i = 0; i < pointCount; i++)
		{
			var price = Math.Round(min + chartStep * i, 4);
			var pnl = Math.Round(pnlAt(price), 2);
			points.Add(new PricePnL(price, pnl, valueAt(price, pnl)));
		}
		return points;
	}

	/// <summary>Finds all prices where P&L crosses zero using linear interpolation.</summary>
	/// <summary>
	/// Returns the ladder price whose P&L equals <paramref name="targetPnL"/>. When the target is reached
	/// across a flat plateau (e.g., max profit at every S ≥ strike), prefers boundary points where the
	/// adjacent ladder price is not at the target, then breaks ties by closeness to <paramref name="spot"/>.
	/// </summary>
	internal static decimal? FindPriceAtPnL(List<PricePnL> ladder, decimal targetPnL, decimal? spot)
	{
		if (ladder == null || ladder.Count == 0) return null;
		const decimal tolerance = 0.01m;

		var sorted = ladder.OrderBy(p => p.UnderlyingPrice).ToList();
		bool IsTarget(int i) => Math.Abs(sorted[i].PnL - targetPnL) <= tolerance;

		var boundaries = new List<decimal>();
		for (int i = 0; i < sorted.Count; i++)
		{
			if (!IsTarget(i)) continue;
			var leftEdge = i == 0 || !IsTarget(i - 1);
			var rightEdge = i == sorted.Count - 1 || !IsTarget(i + 1);
			if (leftEdge || rightEdge) boundaries.Add(sorted[i].UnderlyingPrice);
		}

		var candidates = boundaries.Count > 0 ? boundaries : sorted.Where((_, i) => IsTarget(i)).Select(p => p.UnderlyingPrice).ToList();
		if (candidates.Count == 0) return null;
		if (spot.HasValue) return candidates.OrderBy(p => Math.Abs(p - spot.Value)).First();
		return candidates.First();
	}

	internal static List<decimal> FindBreakEvensNumerically(List<PricePnL> ladder, Func<decimal, decimal>? pnlFunc = null)
	{
		var results = new List<decimal>();
		for (var i = 0; i < ladder.Count - 1; i++)
		{
			var curr = ladder[i];
			var next = ladder[i + 1];
			if (curr.PnL == 0)
			{
				results.Add(curr.UnderlyingPrice);
				continue;
			}
			// When a continuous pnlFunc is available, use it directly for sign-change detection so
			// that 2-decimal rounding in the ladder (from BuildPriceLadder) does not shift the bracket
			// and produce a different break-even than CandidateScorer, which scans the unrounded curve.
			var pCurr = pnlFunc != null ? pnlFunc(curr.UnderlyingPrice) : curr.PnL;
			var pNext = pnlFunc != null ? pnlFunc(next.UnderlyingPrice) : next.PnL;
			if ((pCurr > 0 && pNext < 0) || (pCurr < 0 && pNext > 0))
			{
				if (pnlFunc != null)
				{
					var (nA, nB) = NarrowBracket(pnlFunc, curr.UnderlyingPrice, next.UnderlyingPrice);
					var beVal = BisectBreakEven(pnlFunc, nA, nB);
					results.Add(beVal);
				}
				else
				{
					var fraction = Math.Abs(pCurr) / (Math.Abs(pCurr) + Math.Abs(pNext));
					results.Add(Math.Round(curr.UnderlyingPrice + fraction * (next.UnderlyingPrice - curr.UnderlyingPrice), 2));
				}
			}
		}
		if (ladder.Count > 0 && ladder[^1].PnL == 0 && (results.Count == 0 || results[^1] != ladder[^1].UnderlyingPrice))
			results.Add(ladder[^1].UnderlyingPrice);
		return results;
	}

	/// <summary>Canonical breakeven bisection on a continuous P&L curve known to have a single sign change in
	/// [<paramref name="a"/>, <paramref name="b"/>] (with <paramref name="a"/> < <paramref name="b"/>). Stops when
	/// the interval is ≤ 0.005 (sub-cent on a $25 underlying), capped at 60 iterations. Returns the unrounded
	/// midpoint — callers round only at display time. Shared by the opener scorer's breakeven search and the
	/// analyzer/rule numerical break-even finders so both engines agree to the same precision.</summary>
	internal static decimal BisectBreakEven(Func<decimal, decimal> pnl, decimal a, decimal b)
	{
		var fa = pnl(a);
		for (var i = 0; i < 60; i++)
		{
			var mid = (a + b) / 2m;
			if (b - a <= 0.0001m) return mid;
			var fm = pnl(mid);
			if ((fa < 0m && fm < 0m) || (fa >= 0m && fm >= 0m)) { a = mid; fa = fm; }
			else { b = mid; }
		}
		return (a + b) / 2m;
	}

	/// <summary>Narrows a coarse bracket [a, b] to a ≤$0.01 interval by scanning fn at $0.01 steps so that
	/// both break-even engines start bisection from the same bracket and produce an identical display value.
	/// Without this, the scorer (120-step ±60% scan) and the analyzer (strike-step ladder) find different coarse
	/// brackets; bisecting from different brackets on the same continuous curve gives results that differ by
	/// ~$0.0001–$0.0002 — invisible in absolute terms but enough to straddle a $0.005 display boundary and show
	/// $22.21 vs $22.22. Returns (a, b) unchanged when the bracket is already ≤$0.01.</summary>
	internal static (decimal a, decimal b) NarrowBracket(Func<decimal, decimal> fn, decimal a, decimal b)
	{
		if (b - a <= 0.01m) return (a, b);
		var pA = fn(a);
		for (var x = a + 0.01m; x < b; x += 0.01m)
		{
			var pX = fn(x);
			if ((pA > 0m && pX < 0m) || (pA < 0m && pX > 0m)) return (x - 0.01m, x);
			pA = pX;
		}
		return (a, b);
	}

	// --- Implied Volatility ---

	/// <summary>Back-solves implied volatility on the BlackScholes pricing function. Returns a vol in
	/// [0.01, 5.0]. Newton-Raphson is the fast path; for deep OTM/ITM legs where Newton overshoots or
	/// stalls (tiny vega), falls back to bisection on the monotone price→vol curve. Converges to within
	/// $0.005 of marketPrice or returns the nearest bound when the price is outside the [0.01, 5.0] range.</summary>
	internal static decimal ImpliedVol(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal marketPrice, string callPut)
	{
		decimal vol = 0.3m;
		for (var i = 0; i < 50; i++)
		{
			var price = BlackScholes(spot, strike, timeYears, riskFreeRate, vol, callPut);
			var vega = Vega(spot, strike, timeYears, riskFreeRate, vol);
			var diff = price - marketPrice;
			if (Math.Abs(diff) < 0.005m) return vol;
			// Vega is non-negative; for a deep OTM/ITM leg it's tiny-but-nonzero, where the price is
			// effectively vol-insensitive and Newton overshoots (a single `diff / vega` step jumps the
			// vol past the [0.01, 5.0] range) or divides by ~1e-30 and overflows the decimal range. In
			// either case Newton can't be trusted here — hand off to bisection, which is robust because
			// the BlackScholes price is monotone increasing in vol.
			if (vega <= 1e-8m) break;
			vol -= diff / vega;
			if (vol <= 0.01m || vol >= 5m) break;
		}

		// Bisection fallback. The price is monotone in vol, so f(vol) = price(vol) - marketPrice has at
		// most one sign change in [0.01, 5.0]; clamp to the bound when the market price sits outside it.
		decimal Excess(decimal v) => BlackScholes(spot, strike, timeYears, riskFreeRate, v, callPut) - marketPrice;
		if (Excess(0.01m) >= 0m) return 0.01m;
		if (Excess(5m) <= 0m) return 5m;
		return BisectBreakEven(Excess, 0.01m, 5m);
	}

	/// <summary>Vega of a European option under Black-Scholes: partial derivative of price w.r.t. vol.</summary>
	internal static decimal Vega(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv)
	{
		if (timeYears <= 0) return 0m;
		var S = (double)spot;
		var K = (double)strike;
		var T = timeYears;
		var r = riskFreeRate;
		var sigma = (double)iv;
		if (sigma <= 0) return 0m;
		var d1 = (Math.Log(S / K) + (r + 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
		var pdf = Math.Exp(-0.5 * d1 * d1) / Math.Sqrt(2 * Math.PI);
		return (decimal)(S * pdf * Math.Sqrt(T));
	}

	/// <summary>Gamma of a European option under Black-Scholes: second derivative of price w.r.t. spot. Same formula for calls and puts.</summary>
	internal static decimal Gamma(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv)
	{
		if (timeYears <= 0 || iv <= 0m) return 0m;
		double s = (double)spot, k = (double)strike, sigma = (double)iv, t = timeYears, r = riskFreeRate;
		var d1 = (Math.Log(s / k) + (r + 0.5 * sigma * sigma) * t) / (sigma * Math.Sqrt(t));
		var pdf = Math.Exp(-0.5 * d1 * d1) / Math.Sqrt(2.0 * Math.PI);
		return (decimal)(pdf / (s * sigma * Math.Sqrt(t)));
	}
}
