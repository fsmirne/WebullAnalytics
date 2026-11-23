using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Events;

/// <summary>The ex-dividend dates an option book is still carrying, for reading its spot off put-call parity: a cash dividend inside an
/// expiry's life puts −PV(D) into C − P, so parity on that expiry reads the spot low by the dividend
/// (<see cref="PremarketSpotOverride.DeriveSpotFromParity"/>'s excludeExpiriesFrom).</summary>
internal static class ExDividendDates
{
	/// <summary>Each root's next ex-dividend date when it falls after the session the quotes were struck in (<see cref="OptionMath.ObservationInstant"/>
	/// — the prior close for a frozen book, now for a live one). Roots with no scheduled dividend (cash indexes, non-payers) are absent.</summary>
	internal static async Task<IReadOnlyDictionary<string, DateTime>> SinceStrikeAsync(IReadOnlyCollection<string> roots, OpenerEventsConfig cfg, DateTime asOf, CancellationToken cancellation)
	{
		var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		if (roots.Count == 0) return result;
		var struckOn = OptionMath.ObservationInstant().Date;
		var calendar = await EventCalendarLoader.LoadAsync(roots, cfg, asOf, cancellation);
		foreach (var root in roots)
			if (calendar.Get(root)?.NextExDividendDate is DateTime ex && ex.Date > struckOn)
				result[root] = ex.Date;
		return result;
	}
}
