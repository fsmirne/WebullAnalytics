using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;

namespace WebullAnalytics.Pricing;

/// <summary>Turns scheduled-catalyst events (<see cref="TickerEvents"/>) into the discrete dividend
/// schedule consumed by <see cref="OptionMath.DividendAdjustedSpot"/>. Source priority per the agreed
/// fallback chain: Yahoo/override ex-date + amount → config-yield-derived amount → no adjustment.
///
/// Only a single upcoming ex-dividend is modeled (Yahoo surfaces just the next one), which is sufficient
/// for the ≤45-DTE structures traded — at most one quarterly dividend ever falls inside a leg's life.
/// A continuous-yield approximation for the "no ex-date at all" case is deliberately NOT implemented:
/// smearing a yield across a leg reintroduces the exact calendar mis-pricing the discrete model fixes,
/// so when no ex-date is known the leg simply prices with no dividend (q=0).</summary>
internal static class DividendScheduleBuilder
{
	/// <summary>Discrete dividend schedule for one ticker, or null when none can be determined (→ no
	/// adjustment). <paramref name="spot"/> is only used to size the config-yield fallback amount.</summary>
	public static IReadOnlyList<DividendEvent>? BuildForTicker(TickerEvents? events, decimal spot, OpenerEventsConfig? cfg)
	{
		if (events?.NextExDividendDate is not DateTime exDate) return null;

		decimal amount;
		if (events.DividendAmount is decimal amt && amt > 0m)
			amount = amt;
		else if (cfg is { DividendYield: > 0m } && spot > 0m)
			amount = spot * cfg.DividendYield / Math.Max(1, cfg.DividendFrequency);
		else
			return null; // ex-date known but no amount and no config yield → can't size the dividend

		return new[] { new DividendEvent(exDate.Date, amount) };
	}

	/// <summary>Assembles the ticker → schedule map used by <see cref="AnalysisOptions.Dividends"/> from a
	/// loaded <see cref="EventCalendar"/> and per-ticker spot prices. Tickers with no determinable
	/// dividend are simply absent (the pricer then leaves their legs unadjusted).</summary>
	public static IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>> Build(EventCalendar calendar, IReadOnlyDictionary<string, decimal> spotsByTicker, OpenerEventsConfig? cfg)
	{
		var result = new Dictionary<string, IReadOnlyList<DividendEvent>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (ticker, spot) in spotsByTicker)
		{
			var schedule = BuildForTicker(calendar.Get(ticker), spot, cfg);
			if (schedule != null)
				result[ticker] = schedule;
		}
		return result;
	}
}
