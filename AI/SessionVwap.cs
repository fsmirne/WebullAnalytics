namespace WebullAnalytics.AI;

/// <summary>Running session VWAP over minute bars, shared by the intraday tape signal and the 0DTE session gate so
/// the two never disagree. Volume-weights only while EVERY bar so far reports volume; otherwise it is the TWAP
/// (equal-weight typical price). A zero-volume bar in these tapes means the feed didn't report volume, not that
/// nothing traded: SPX-family RTH bars are a cash index (no volume, plus a few stray bars whose "volume" is the
/// price), SPY's tape loses volume on whole stretches of some days, and the SPX-family CSVs splice in SPY-scaled
/// extended-hours bars that DO carry SPY volume. Weighting by whichever subset happens to report volume makes the
/// "VWAP" the average of a handful of bars — on 2026-10-19 it sat at ~7701 vs a ~7708 session TWAP from 13:22 on,
/// and on 2026-10-20 it read 7739 at 11:00 and fired a false bearish gate confirmation.</summary>
internal sealed class SessionVwap
{
	private decimal _notional;
	private long _volume;
	private decimal _typicalSum;
	private int _count;
	private bool _allHaveVolume = true;

	public void Add(MinuteBar bar)
	{
		var typical = (bar.High + bar.Low + bar.Close) / 3m;
		_notional += typical * bar.Volume;
		_volume += bar.Volume;
		_typicalSum += typical;
		_count++;
		if (bar.Volume <= 0) _allHaveVolume = false;
	}

	/// <summary>Current VWAP (or TWAP fallback); 0 before the first bar.</summary>
	public decimal Value => _count == 0 ? 0m : _allHaveVolume ? _notional / _volume : _typicalSum / _count;
}
