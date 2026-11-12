using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>
/// Real-world volatility forecast for the scoring distribution, on a trading-session clock. Options are priced at
/// implied vol, but the outcomes a trade actually experiences follow realized vol; scoring EV/POP on the implied
/// distribution makes every fairly priced structure EV ≈ 0 − friction and hides the volatility risk premium. This
/// model splits realized variance into an overnight gap (prior close → open) and a session move (open → close),
/// each a per-session zero-mean variance from trailing daily bars, so the variance to any target close — including
/// the remaining part of today's session for 0DTE — is the sum of the pieces actually left to happen.
///
/// <see cref="TodayScale"/> and <see cref="ForwardScale"/> rescale the per-session variances for the forecast day itself and for every later
/// session (<see cref="ImpliedVolForecast"/> calibrates them against VIX1D / VIX9D when <c>opener.impliedVolForecast</c> is on); both
/// default to 1, the trailing-EWMA forecast alone.
/// </summary>
internal sealed record RealizedVolModel(double OvernightVariance, double SessionVariance, double TodayScale = 1.0, double ForwardScale = 1.0)
{
	/// <summary>EWMA half-life, in sessions. Calibrated 2026-10-14 by QLIKE on next-1 and next-5-session realized variance
	/// (overnight + session), SPY / SPXW / QQQ 2022-01..2026-10: half-life 5–10 beat simple 10/20/40/60/120-day means on every
	/// ticker and horizon (SPY 5-session 0.366 vs 0.401 for the 20-day mean); 10 is best or within 0.01 of best throughout.</summary>
	internal const double HalfLifeSessions = 10.0;

	/// <summary>Sessions of history loaded: six half-lives, beyond which the remaining weight is under 2%.</summary>
	internal const int HistorySessions = 60;

	/// <summary>Builds the model from daily bars, oldest-first, strictly before the scoring instant (no lookahead): an
	/// exponentially weighted (<see cref="HalfLifeSessions"/>) mean of each squared overnight and session log return.
	/// Null when fewer than two bars or a non-positive price makes a return undefined.</summary>
	internal static RealizedVolModel? FromBars(IReadOnlyList<YahooOptionsClient.HistoricalBar> bars)
	{
		if (bars.Count < 2) return null;
		var decay = Math.Pow(0.5, 1.0 / HalfLifeSessions);
		double overnight = 0, session = 0, totalWeight = 0, weight = 1;
		for (var i = bars.Count - 1; i >= 1; i--)
		{
			var priorClose = (double)bars[i - 1].Close;
			var open = (double)bars[i].Open;
			var close = (double)bars[i].Close;
			if (priorClose <= 0 || open <= 0 || close <= 0) return null;
			overnight += weight * Math.Pow(Math.Log(open / priorClose), 2);
			session += weight * Math.Pow(Math.Log(close / open), 2);
			totalWeight += weight;
			weight *= decay;
		}
		return new RealizedVolModel(overnight / totalWeight, session / totalWeight);
	}

	/// <summary>Loads the trailing <see cref="HistorySessions"/> sessions strictly before <paramref name="asOf"/> and builds the
	/// model; null when the history is too short. <paramref name="impliedForecast"/> (opener.impliedVolForecast) rescales it
	/// against the market's short-dated implied vol (<see cref="ImpliedVolForecast"/>).</summary>
	internal static async Task<RealizedVolModel?> LoadAsync(HistoricalPriceCache priceCache, string ticker, DateTime asOf, bool impliedForecast, CancellationToken cancellation)
	{
		var model = FromBars(await priceCache.GetRecentBarsAsync(ticker, HistorySessions + 1, asOf, cancellation));
		return model != null && impliedForecast ? await ImpliedVolForecast.ApplyAsync(model, priceCache, ticker, asOf, cancellation) : model;
	}

	/// <summary>The overnight gap and full-session variance of trading day <paramref name="day"/>, as forecast from <paramref name="asOf"/>.</summary>
	private (double Overnight, double Session) VariancesOn(DateTime day, DateTime asOf)
	{
		var scale = day.Date == asOf.Date ? TodayScale : ForwardScale;
		return (OvernightVariance * scale, SessionVariance * scale);
	}

	/// <summary>Realized log-variance expected between <paramref name="asOf"/> (ET) and the close of
	/// <paramref name="targetExpiry"/>: the unelapsed fraction of today's session (plus today's gap if before the
	/// open) and a full gap + session for every later trading day through the target.</summary>
	internal double VarianceTo(DateTime asOf, DateTime targetExpiry)
	{
		var variance = 0.0;
		for (var day = asOf.Date; day <= targetExpiry.Date; day = day.AddDays(1))
		{
			if (!MarketCalendar.IsOpen(day)) continue;
			var open = day + OptionMath.MarketOpen;
			var close = day + (MarketCalendar.IsEarlyClose(day) ? new TimeSpan(13, 0, 0) : OptionMath.MarketClose);
			if (asOf >= close) continue;
			var (overnight, session) = VariancesOn(day, asOf);
			if (asOf < open) variance += overnight;
			var start = asOf > open ? asOf : open;
			variance += session * (close - start).TotalMinutes / (close - open).TotalMinutes;
		}
		return variance;
	}

	/// <summary>Sessions' worth of time between <paramref name="asOf"/> and the target close — <see cref="VarianceTo"/> in units of one
	/// unscaled session (gap + full session), so a level rescale never changes how many path steps a horizon gets.</summary>
	internal double SessionsTo(DateTime asOf, DateTime targetExpiry)
	{
		var unit = OvernightVariance + SessionVariance;
		return unit > 0 ? (this with { TodayScale = 1.0, ForwardScale = 1.0 }).VarianceTo(asOf, targetExpiry) / unit : 0.0;
	}

	/// <summary>Inverse of <see cref="VarianceTo"/>: the clock time by which <paramref name="fraction"/> of the variance
	/// left until the target close has accrued. An overnight gap accrues all at once at the open, so a fraction that lands
	/// inside a gap maps to that open; inside a session it maps proportionally through the session's minutes.</summary>
	internal DateTime TimeAtVarianceFraction(DateTime asOf, DateTime targetExpiry, double fraction)
	{
		var remaining = VarianceTo(asOf, targetExpiry) * Math.Clamp(fraction, 0.0, 1.0);
		DateTime last = asOf;
		for (var day = asOf.Date; day <= targetExpiry.Date; day = day.AddDays(1))
		{
			if (!MarketCalendar.IsOpen(day)) continue;
			var open = day + OptionMath.MarketOpen;
			var close = day + (MarketCalendar.IsEarlyClose(day) ? new TimeSpan(13, 0, 0) : OptionMath.MarketClose);
			if (asOf >= close) continue;
			var (overnight, session) = VariancesOn(day, asOf);
			if (asOf < open)
			{
				if (remaining <= overnight) return open;
				remaining -= overnight;
			}
			var start = asOf > open ? asOf : open;
			var sessionLeft = session * (close - start).TotalMinutes / (close - open).TotalMinutes;
			if (remaining <= sessionLeft)
				return session > 0 ? start.AddMinutes(remaining / session * (close - open).TotalMinutes) : close;
			remaining -= sessionLeft;
			last = close;
		}
		return last;
	}

	/// <summary>The forecast expressed as an annualized vol on the same clock as <paramref name="years"/> (the
	/// IV back-solve's time-to-target), so it drops into the scenario grid / POP / expected move in place of IV:
	/// σ_scenario·√years = √VarianceTo. Null when there's no time or variance left.</summary>
	internal decimal? ScenarioVol(DateTime asOf, DateTime targetExpiry, OptionTime years)
	{
		if (years.IsExpired) return null;
		var variance = VarianceTo(asOf, targetExpiry);
		return variance > 0 ? (decimal)Math.Sqrt(variance / years.VarianceYears) : null;
	}
}
