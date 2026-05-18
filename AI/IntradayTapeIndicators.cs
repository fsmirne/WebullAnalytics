namespace WebullAnalytics.AI;

/// <summary>Derives an <see cref="IntradayBias"/> from a minute-bar series. Sub-components:
/// <list type="bullet">
///   <item><description><b>Gap</b> — prev-close vs today's open. Captures overnight news / futures action.
///     Zero when prev-close is unavailable.</description></item>
///   <item><description><b>Open-to-now drift</b> — today's open vs last-bar close. The primary intraday
///     trend signal at the 0DTE horizon.</description></item>
///   <item><description><b>VWAP deviation</b> — last-bar close vs session VWAP. Catches price stretching
///     away from the volume-weighted mean. Falls back to TWAP (equal-weight typical-price average) on
///     bars with no volume — cash indexes like SPX always need this fallback.</description></item>
/// </list>
/// Each sub-score is a percentage-return-style number clamped to [-1, +1] (1% move → ±1). The composite
/// is a weighted average per <paramref name="cfg"/>.</summary>
internal static class IntradayTapeIndicators
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	public static IntradayBias? Compute(IReadOnlyList<MinuteBar> bars, decimal? prevClose, DateTimeOffset asOf, IntradayTapeConfig cfg)
	{
		if (bars.Count == 0) return null;
		if (cfg.GapWeight + cfg.OpenToNowWeight + cfg.VwapDeviationWeight <= 0m) return null;

		var asOfNyDate = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		var todaysBars = bars.Where(b => TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date == asOfNyDate).ToList();
		if (todaysBars.Count < cfg.MinBars) return null;

		var todayOpen = todaysBars[0].Open;
		var nowPrice = todaysBars[^1].Close;

		// Prefer the previous-day's last bar from this same series over any externally-supplied
		// prevClose. The two sources can be denominated differently (caller-supplied prevClose
		// comes from the daily Yahoo cache; bars come from the live intraday source). When the
		// scales disagree, the gap math becomes meaningless — pinning the gap at the clamp floor.
		// Bar-derived prev-close keeps every component of the score on the same scale by construction.
		var barDerivedPrevClose = LastBarBeforeDate(bars, asOfNyDate);
		var effectivePrevClose = barDerivedPrevClose ?? prevClose;

		var gapScore = ComputeGap(effectivePrevClose, todayOpen);
		var openToNowScore = ComputeOpenToNow(todayOpen, nowPrice);
		var vwapDevScore = ComputeVwapDeviation(todaysBars, nowPrice);

		var totalWeight = cfg.GapWeight + cfg.OpenToNowWeight + cfg.VwapDeviationWeight;
		var score = (gapScore * cfg.GapWeight + openToNowScore * cfg.OpenToNowWeight + vwapDevScore * cfg.VwapDeviationWeight) / totalWeight;

		return new IntradayBias(score, gapScore, openToNowScore, vwapDevScore, todaysBars.Count, asOf);
	}

	private static decimal ComputeGap(decimal? prevClose, decimal todayOpen)
	{
		if (!prevClose.HasValue || prevClose.Value <= 0m) return 0m;
		return Math.Clamp((todayOpen - prevClose.Value) / prevClose.Value * 100m, -1m, 1m);
	}

	private static decimal? LastBarBeforeDate(IReadOnlyList<MinuteBar> bars, DateTime nyDate)
	{
		for (int i = bars.Count - 1; i >= 0; i--)
		{
			var bd = TimeZoneInfo.ConvertTime(bars[i].Timestamp, NyTz).Date;
			if (bd < nyDate) return bars[i].Close;
		}
		return null;
	}

	private static decimal ComputeOpenToNow(decimal todayOpen, decimal nowPrice)
	{
		if (todayOpen <= 0m) return 0m;
		return Math.Clamp((nowPrice - todayOpen) / todayOpen * 100m, -1m, 1m);
	}

	private static decimal ComputeVwapDeviation(IReadOnlyList<MinuteBar> bars, decimal nowPrice)
	{
		decimal totalNotional = 0m;
		long totalVolume = 0;
		foreach (var b in bars)
		{
			var tp = (b.High + b.Low + b.Close) / 3m;
			totalNotional += tp * b.Volume;
			totalVolume += b.Volume;
		}

		decimal reference;
		if (totalVolume > 0)
		{
			reference = totalNotional / totalVolume;
		}
		else
		{
			// TWAP fallback for zero-volume bars (cash indexes).
			decimal sum = 0m;
			foreach (var b in bars) sum += (b.High + b.Low + b.Close) / 3m;
			reference = sum / bars.Count;
		}

		if (reference <= 0m) return 0m;
		return Math.Clamp((nowPrice - reference) / reference * 100m, -1m, 1m);
	}
}

/// <summary>Per-component weights and minimum-data thresholds for the intraday tape signal.
/// Defaults emphasize open-to-now drift over the static gap and VWAP-deviation signals.</summary>
internal sealed class IntradayTapeConfig
{
	/// <summary>Minimum number of bars before the intraday signal is considered usable. Earlier than
	/// this returns null and the bias falls back to macro-only.</summary>
	public int MinBars { get; set; } = 5;

	public decimal GapWeight { get; set; } = 1.0m;
	public decimal OpenToNowWeight { get; set; } = 2.0m;
	public decimal VwapDeviationWeight { get; set; } = 1.0m;
}
