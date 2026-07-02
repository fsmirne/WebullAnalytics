namespace WebullAnalytics.AI;

/// <summary>
/// Rounds a computed limit price to the minimum tick the exchange will accept. Without this, raw
/// mids like $10.82 are rejected by Webull (OAUTH_OPENAPI_OPTION_PRICE_STEP_GTE).
///
/// Rules (per CBOE Titanium Complex Book Process v1.2.69 + Cboe release notes on net-price
/// increments for complex orders):
///   - XSP (Cboe Mini-SPX): minimum tick is $0.01 for ALL series, single-leg and complex
///     (broker-verified live 2026-07-02 — a $0.05-rounded combo was needlessly coarse and a $0.01
///     close was accepted).
///   - Single-leg, non-penny-pilot: $0.05 below $3.00, $0.10 at/above. Webull's error string is the
///     authoritative confirmation ("Orders placed with a premium of $3 or more must be in
///     increments of 0.10"). SPX/SPXW are non-penny-pilot.
///   - Multi-leg combo on SPX/SPXW: $0.05 net. Boxes/rolls technically allow $0.01 but the helper
///     doesn't classify structure — $0.05 is safe across all SPX/SPXW combos.
///   - Multi-leg combo on everything else: $0.01 net (penny). Routed through CBOE's Complex Book
///     which accepts penny net pricing for all non-SPX classes.
///   - Individual leg execution prints may land at $0.01 regardless — that's fill mechanics, not
///     submission, and is not this helper's concern.
///
/// Penny-pilot equity options (SPY, QQQ, IWM, etc.) technically accept $0.01 below $3 on single-leg
/// orders. This helper over-rounds them to $0.05 — costs at most a few cents of edge per order
/// when the bot eventually trades those single-leg, but never gets rejected. Add a penny-pilot
/// allow-list when that case actually bites.
///
/// Rounds to nearest (MidpointRounding.AwayFromZero) so the submitted limit stays neutral to the
/// model's mid — no buy/sell asymmetry.
/// </summary>
internal static class OptionPriceRounding
{
	public static decimal RoundToTick(decimal price, int legCount, string ticker)
	{
		if (price <= 0m) return 0m;
		var tick = Tick(price, legCount, ticker);
		return Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;
	}

	/// <summary>Smallest positive limit the exchange accepts for this order shape. Used when a
	/// worthless-position close rounds to a $0.00 net, which brokers reject outright.</summary>
	public static decimal MinTick(int legCount, string ticker) => Tick(0m, legCount, ticker);

	private static decimal Tick(decimal price, int legCount, string ticker)
	{
		if (string.Equals(ticker, "XSP", StringComparison.OrdinalIgnoreCase)) return 0.01m;

		var isSpxClass = string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(ticker, "SPXW", StringComparison.OrdinalIgnoreCase);

		if (legCount >= 2)
			return isSpxClass ? 0.05m : 0.01m;
		return price >= 3.00m ? 0.10m : 0.05m;
	}
}
