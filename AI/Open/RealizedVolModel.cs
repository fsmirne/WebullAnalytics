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
/// </summary>
internal sealed record RealizedVolModel(double OvernightVariance, double SessionVariance)
{
	/// <summary>Builds the model from daily bars, oldest-first, strictly before the scoring instant (no lookahead).
	/// Null when fewer than two bars or a non-positive price makes a return undefined.</summary>
	internal static RealizedVolModel? FromBars(IReadOnlyList<YahooOptionsClient.HistoricalBar> bars)
	{
		if (bars.Count < 2) return null;
		double overnight = 0, session = 0;
		for (var i = 1; i < bars.Count; i++)
		{
			var priorClose = (double)bars[i - 1].Close;
			var open = (double)bars[i].Open;
			var close = (double)bars[i].Close;
			if (priorClose <= 0 || open <= 0 || close <= 0) return null;
			overnight += Math.Pow(Math.Log(open / priorClose), 2);
			session += Math.Pow(Math.Log(close / open), 2);
		}
		var n = bars.Count - 1;
		return new RealizedVolModel(overnight / n, session / n);
	}

	/// <summary>Loads the trailing <paramref name="lookbackDays"/> sessions strictly before <paramref name="asOf"/> and builds
	/// the model; null when the history is too short.</summary>
	internal static async Task<RealizedVolModel?> LoadAsync(HistoricalPriceCache priceCache, string ticker, int lookbackDays, DateTime asOf, CancellationToken cancellation)
		=> FromBars(await priceCache.GetRecentBarsAsync(ticker, lookbackDays + 1, asOf, cancellation));

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
			if (asOf < open) variance += OvernightVariance;
			var start = asOf > open ? asOf : open;
			variance += SessionVariance * (close - start).TotalMinutes / (close - open).TotalMinutes;
		}
		return variance;
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
			if (asOf < open)
			{
				if (remaining <= OvernightVariance) return open;
				remaining -= OvernightVariance;
			}
			var start = asOf > open ? asOf : open;
			var sessionLeft = SessionVariance * (close - start).TotalMinutes / (close - open).TotalMinutes;
			if (remaining <= sessionLeft)
				return SessionVariance > 0 ? start.AddMinutes(remaining / SessionVariance * (close - open).TotalMinutes) : close;
			remaining -= sessionLeft;
			last = close;
		}
		return last;
	}

	/// <summary>The forecast expressed as an annualized vol on the same clock as <paramref name="years"/> (the
	/// IV back-solve's time-to-target), so it drops into the scenario grid / POP / expected move in place of IV:
	/// σ_scenario·√years = √VarianceTo. Null when there's no time or variance left.</summary>
	internal decimal? ScenarioVol(DateTime asOf, DateTime targetExpiry, double years)
	{
		if (years <= 0) return null;
		var variance = VarianceTo(asOf, targetExpiry);
		return variance > 0 ? (decimal)Math.Sqrt(variance / years) : null;
	}
}
