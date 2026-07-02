using Spectre.Console;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI;

/// <summary>
/// Premarket (04:00–09:30 ET) spot correction for the live AI pipeline. Webull's chain endpoint reports the underlying's prior-session close until the RTH bell — the SPX-family cash indexes simply are not computed off-hours —
/// so premarket scan/regime reads scored against a spot that ignored the whole overnight move. This helper replaces that stale spot with the freshest premarket estimate available and re-bases the chain's mid-implied IVs against it:
///   1. put-call parity on the ATM straddle of the fetched chain, bid/ask mids only (XSP/SPXW trade a global session until 09:15 ET, so their premarket quotes are live and parity yields the spot the option market is actually pricing);
///   2. fallback: the latest extended-hours minute bar (for the SPX family the fetcher merges SPY premarket bars rescaled to the ticker's level, so this is the SPY-converted spot; for equities it is the ticker's own premarket tape).
/// No-op outside the premarket window, on non-trading days, and when neither source yields a price. Explicit --spot / --premarket flags run after this and win.
/// </summary>
internal static class PremarketSpotOverride
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan PremarketStart = new(4, 0, 0);
	private static readonly TimeSpan RthOpen = new(9, 30, 0);

	// Max relative disagreement between the parity spot and the extended-hours bar before the chain's books
	// are declared frozen. Genuine basis between the two (SPY→SPX conversion ratio drift, minute timing,
	// half-spread parity noise) is a few basis points; a frozen book is off by the whole overnight move.
	private const decimal FrozenBookTolerance = 0.0025m;

	public static bool IsPremarket(DateTime now) => now.TimeOfDay >= PremarketStart && now.TimeOfDay < RthOpen && MarketCalendar.IsOpen(now.Date);

	/// <summary>Returns <paramref name="snapshot"/> with the configured ticker's spot replaced (and its chain IVs re-solved) when running premarket; unchanged otherwise. When the snapshot carries no chain for the ticker
	/// (no open positions), one placeholder fetch pulls it — and the chain is merged into the returned snapshot so the opener's bootstrap probe does not re-fetch the chain and overwrite the corrected spot with the stale one.</summary>
	public static async Task<QuoteSnapshot> ApplyAsync(QuoteSnapshot snapshot, AIConfig config, IQuoteSource quotes, DateTime now, CancellationToken cancellation)
	{
		if (!IsPremarket(now)) return snapshot;
		var ticker = config.Ticker.ToUpperInvariant();

		var options = snapshot.Options;
		var underlyings = new Dictionary<string, decimal>(snapshot.Underlyings, StringComparer.OrdinalIgnoreCase);
		if (!HasChainFor(ticker, options))
		{
			var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MatchKeys.OccSymbol(ticker, now.Date.AddDays(7), 1m, "C") };
			var boot = await quotes.GetQuotesAsync(now, placeholders, config.TickerSet(), cancellation);
			if (boot.Options.Count > 0)
			{
				var combined = new Dictionary<string, OptionContractQuote>(options, StringComparer.OrdinalIgnoreCase);
				foreach (var (k, v) in boot.Options) combined[k] = v;
				options = combined;
			}
			foreach (var (k, v) in boot.Underlyings) if (!underlyings.ContainsKey(k)) underlyings[k] = v;
		}

		underlyings.TryGetValue(ticker, out var chainSpot);

		// Two independent fresh-spot sources, cross-checked. Parity is preferred when the books are live (a
		// spot consistent with the very quotes being scored), but a FROZEN premarket book — Schwab's chain
		// serves the prior session's closing NBBO until the bell; Webull's GTH books freeze at 09:15 ET — is
		// two-sided, passes the strict-mids test, and reproduces YESTERDAY's spot. So parity is trusted only
		// when it agrees with the extended-hours bar; on disagreement the bar wins and the quotes themselves
		// are flagged stale (validated live 2026-07-02: Webull GTH parity 751.92 ≈ bar 752.30 vs Schwab
		// frozen parity 748.13 ≈ prior close 748.32, a 3.6-point overnight move the frozen books missed).
		var parity = DeriveSpotFromParity(ticker, options, riskFreeRate: 0.036, now, allowLastPrice: false);
		var barClose = await LatestExtendedBarCloseAsync(ticker, now, cancellation);

		decimal? fresh = null;
		var source = "";
		if (parity is { Spot: > 0m } p && barClose is decimal bar and > 0m)
		{
			if (Math.Abs(p.Spot - bar) / bar <= FrozenBookTolerance)
			{
				fresh = p.Spot;
				source = $"parity K={p.AtmStrike:0.##}, {p.Dte}d";
			}
			else
			{
				fresh = bar;
				source = "premarket bar";
				AnsiConsole.MarkupLine($"[yellow]premarket: {Markup.Escape(ticker)} chain books look frozen (parity ${p.Spot:N2} vs premarket bar ${bar:N2}) — using the bar spot. The option quotes themselves are likely stale on this source; treat premarket rankings as indicative only.[/]");
			}
		}
		else if (parity is { Spot: > 0m } pOnly)
		{
			fresh = pOnly.Spot;
			source = $"parity K={pOnly.AtmStrike:0.##}, {pOnly.Dte}d (unverified — no premarket bar)";
		}
		else if (barClose is decimal barOnly and > 0m)
		{
			fresh = barOnly;
			source = "premarket bar";
		}

		if (fresh is not decimal spot)
			return ReferenceEquals(options, snapshot.Options) ? snapshot : new QuoteSnapshot(options, underlyings); // nothing fresher than the chain's prior close — keep it, but keep the bootstrapped chain

		underlyings[ticker] = spot;
		options = LiveQuoteSource.RebaseIvForSpot(options, ticker, spot, now);
		AnsiConsole.MarkupLine($"[dim]premarket spot: {Markup.Escape(ticker)} ${spot:N2} ({Markup.Escape(source)}; chain reported ${chainSpot:N2})[/]");
		return new QuoteSnapshot(options, underlyings);
	}

	internal readonly record struct ParityResult(decimal Spot, decimal AtmStrike, int Dte);

	/// <summary>Back-solves the underlying spot from put-call parity on the ATM straddle in the fetched
	/// option chain. Returns null if no expiry has a strike with both a call and put quote.
	/// Parity (European, no dividends — exact for cash-settled SPX/SPXW/NDX/XSP/RUT): S = (C - P) + K * exp(-r*T).
	/// Picks the nearest non-negative DTE expiry, then the strike where |C_mid - P_mid| is minimum (ATM).
	/// Quote preference per leg: bid+ask mid (bid=0, ask>0, ask=bid), else LastPrice if positive. The LastPrice
	/// fallback handles premarket chains where Webull echoes the prior-session close but omits bid/ask; pass
	/// <paramref name="allowLastPrice"/> = false to reject it — required when the result must be FRESH (the
	/// auto premarket override), since a quote-less chain would otherwise echo yesterday's spot back as live.</summary>
	internal static ParityResult? DeriveSpotFromParity(string ticker, IReadOnlyDictionary<string, OptionContractQuote> quotes, double riskFreeRate, DateTime asOf, Action<string>? diag = null, bool allowLastPrice = true)
	{
		// Group by expiry: { expiry → { strike → (call?, put?) } }.
		var byExpiry = new Dictionary<DateTime, Dictionary<decimal, (OptionContractQuote? call, OptionContractQuote? put)>>();
		foreach (var (sym, q) in quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (!byExpiry.TryGetValue(p.ExpiryDate, out var byStrike))
				byExpiry[p.ExpiryDate] = byStrike = new Dictionary<decimal, (OptionContractQuote?, OptionContractQuote?)>();
			byStrike.TryGetValue(p.Strike, out var pair);
			byStrike[p.Strike] = p.CallPut == "C" ? (q, pair.put) : (pair.call, q);
		}

		if (byExpiry.Count == 0)
		{
			diag?.Invoke($"  {ticker}: no contracts for this root in the fetched chain.");
			return null;
		}

		// Pick the nearest non-negative DTE expiry that has at least one strike with both call+put viable mids.
		foreach (var expiry in byExpiry.Keys.Where(d => d.Date >= asOf.Date).OrderBy(d => d))
		{
			var byStrike = byExpiry[expiry];
			decimal? bestStrike = null;
			decimal bestDiff = decimal.MaxValue;
			decimal bestC = 0m, bestP = 0m;
			int bothPresent = 0, callOnly = 0, putOnly = 0, neither = 0;
			foreach (var (k, pair) in byStrike)
			{
				var cMid = Mid(pair.call, allowLastPrice);
				var pMid = Mid(pair.put, allowLastPrice);
				if (cMid != null && pMid != null) bothPresent++;
				else if (cMid != null) callOnly++;
				else if (pMid != null) putOnly++;
				else neither++;
				if (cMid == null || pMid == null) continue;
				var diff = Math.Abs(cMid.Value - pMid.Value);
				if (diff < bestDiff) { bestDiff = diff; bestStrike = k; bestC = cMid.Value; bestP = pMid.Value; }
			}
			diag?.Invoke($"  {ticker} {expiry:yyyy-MM-dd}: {byStrike.Count} strikes (both={bothPresent}, callOnly={callOnly}, putOnly={putOnly}, neither={neither}).");
			if (bestStrike == null) continue;

			var dte = Math.Max(0, (expiry.Date - asOf.Date).Days);
			var discount = (decimal)Math.Exp(-riskFreeRate * dte / 365.0);
			var spot = (bestC - bestP) + bestStrike.Value * discount;
			return new ParityResult(spot, bestStrike.Value, dte);
		}
		return null;

		static decimal? Mid(OptionContractQuote? q, bool allowLastPrice)
		{
			if (q == null) return null;
			if (q.Bid is decimal b && q.Ask is decimal a && b >= 0m && a > 0m && a >= b) return (b + a) / 2m;
			if (allowLastPrice && q.LastPrice is decimal lp && lp > 0m) return lp;
			return null;
		}
	}

	/// <summary>Latest extended-hours minute-bar close at or before <paramref name="now"/> (ET-naive or UTC). For the SPX family the bar fetcher transparently returns SPY premarket bars rescaled to the ticker's level.</summary>
	private static async Task<decimal?> LatestExtendedBarCloseAsync(string ticker, DateTime now, CancellationToken cancellation)
	{
		var apiConfig = OpenCandidateEvaluator.TryLoadApiConfig();
		if (apiConfig == null) return null;
		var nowUtc = now.Kind == DateTimeKind.Utc ? new DateTimeOffset(now, TimeSpan.Zero) : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(now, DateTimeKind.Unspecified), NyTz), TimeSpan.Zero);
		try
		{
			var cache = new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig));
			var bars = await cache.GetBarsAsync(ticker, nowUtc.AddMinutes(-90), nowUtc, BarInterval.M1, includeExtended: true, cancellation);
			return bars.Count > 0 ? bars[^1].Close : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Console.Error.WriteLine($"premarket spot: bar fetch failed for {ticker}: {ex.Message}");
			return null;
		}
	}

	private static bool HasChainFor(string ticker, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		foreach (var k in quotes.Keys)
		{
			var p = ParsingHelpers.ParseOptionSymbol(k);
			if (p != null && string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}
}
