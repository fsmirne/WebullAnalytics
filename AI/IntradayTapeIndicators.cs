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

	// Regular trading hours in ET: [09:30, 16:00). The tape signal is defined strictly on RTH —
	// gap = prior RTH close → today's RTH open; open-to-now = today's RTH open → now; VWAP over RTH
	// bars only. Pre/post-market prints (the intraday CSVs carry 04:00–17:25 ET) must be excluded, or
	// the "open" anchor becomes a pre-market print and every sub-score measures the wrong move.
	private static readonly TimeSpan RthStart = new(9, 30, 0);
	private static readonly TimeSpan RthEnd = new(16, 0, 0);

	private static bool IsRth(DateTimeOffset ts)
	{
		var et = TimeZoneInfo.ConvertTime(ts, NyTz).TimeOfDay;
		return et >= RthStart && et < RthEnd;
	}

	public static IntradayBias? Compute(IReadOnlyList<MinuteBar> bars, decimal? prevClose, DateTimeOffset asOf, IntradayTapeConfig cfg)
	{
		if (bars.Count == 0) return null;
		if (cfg.GapWeight + cfg.OpenToNowWeight + cfg.VwapDeviationWeight <= 0m) return null;

		var asOfNyDate = TimeZoneInfo.ConvertTime(asOf, NyTz).Date;
		// RTH-only (default): today's regular-session bars, excluding pre/post-market, so the "open"
		// anchor is the 09:30 print — not a 04:00 ET pre-market bar — and the gap measures the true
		// prior-RTH-close → today's-RTH-open move. The intraday CSVs carry 04:00–17:25 ET, so this
		// filter is what enforces the documented IncludeExtended=false contract (the bar cache returns
		// every row on disk regardless). IncludeExtended=true keeps the legacy all-session behavior.
		var rthOnly = !cfg.IncludeExtended;
		var todaysBars = bars.Where(b =>
			TimeZoneInfo.ConvertTime(b.Timestamp, NyTz).Date == asOfNyDate && (!rthOnly || IsRth(b.Timestamp))).ToList();
		if (todaysBars.Count < cfg.MinBars) return null;

		var todayOpen = todaysBars[0].Open;
		var nowPrice = todaysBars[^1].Close;

		// Prev-close from this same series (prior session's last bar — RTH close under rthOnly), preferred
		// over any externally-supplied prevClose so every component stays on one scale by construction.
		var barDerivedPrevClose = LastBarBeforeDate(bars, asOfNyDate, rthOnly);
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

	private static decimal? LastBarBeforeDate(IReadOnlyList<MinuteBar> bars, DateTime nyDate, bool rthOnly)
	{
		// Most recent bar on a prior date. Under rthOnly this skips post-market prints so the gap
		// reference is the prior session's ~16:00 ET RTH close, not a 17:25 ET after-hours tick.
		for (int i = bars.Count - 1; i >= 0; i--)
		{
			var bd = TimeZoneInfo.ConvertTime(bars[i].Timestamp, NyTz).Date;
			if (bd < nyDate && (!rthOnly || IsRth(bars[i].Timestamp))) return bars[i].Close;
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

	/// <summary>When false (default), the signal is computed on regular-session bars only (09:30–16:00
	/// ET) — gap, open-to-now and VWAP all anchor on RTH prints. When true, pre/post-market bars are
	/// included (legacy behavior). Mirrors <see cref="OpenerIntradayTapeConfig.IncludeExtended"/>.</summary>
	public bool IncludeExtended { get; set; } = false;
}
