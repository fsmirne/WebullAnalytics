using Spectre.Console;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>What kind of quote-integrity problem the live guard found.</summary>
internal enum QuoteIssueKind { StaleFeed, TornNbbo }

/// <summary>One quote-integrity finding. <see cref="Symbol"/> is empty for the feed-level staleness
/// finding (it describes the whole book, not a single leg).</summary>
internal readonly record struct QuoteIssue(QuoteIssueKind Kind, string Symbol, string Detail);

/// <summary>One expiry whose book fails put-call parity against the live spot: <see cref="ImpliedSpot"/> is the
/// dividend-adjusted spot the book's near-the-money call/put pairs price, <see cref="ExpectedSpot"/> the
/// dividend-adjusted live spot, <see cref="Deviation"/> their signed gap as a fraction of spot.</summary>
internal readonly record struct ParityGap(DateTime Expiry, decimal ImpliedSpot, decimal ExpectedSpot, decimal Deviation);

/// <summary>LIVE-only quote-integrity checks (pure functions). A logged-off or degraded vendor feed returns
/// quotes that are STALE (delayed by minutes) or TORN (a bid and ask stitched from different moments, e.g.
/// bid 10.36 / ask 20.36) — either corrupts the mid the opener prices and the <c>--limit</c> it proposes.
/// The backtest prices from a clean historical NBBO store and never runs these. Thresholds live in
/// <see cref="OpenerQuoteGuardConfig"/>.</summary>
internal static class QuoteSanity
{
	/// <summary>Age (seconds) of the FRESHEST two-sided quote in <paramref name="book"/> relative to
	/// <paramref name="now"/> — the feed-staleness signal. On a live feed at least one near-the-money strike
	/// re-quotes every few seconds, so the freshest age stays small; a logged-off/delayed session leaves even
	/// the freshest quote minutes old. Uses the freshest (not a per-strike age) precisely so an individually
	/// quiet strike can't false-trip it. Returns null when NO quote carries a timestamp — staleness is then
	/// UNVERIFIABLE (e.g. a vendor that doesn't stamp quotes), which the caller must not treat as "fresh".</summary>
	public static double? FreshestQuoteAgeSeconds(IReadOnlyDictionary<string, OptionContractQuote> book, DateTimeOffset now)
	{
		double? freshest = null;
		foreach (var q in book.Values)
		{
			if (q.QuoteTime is not { } t) continue;
			if (q.Bid is not > 0m || q.Ask is not > 0m) continue;
			var age = (now - t).TotalSeconds;
			if (freshest is null || age < freshest.Value) freshest = age;
		}
		return freshest;
	}

	/// <summary>True when the feed is stale: a timestamp exists and even the freshest quote exceeds
	/// <paramref name="maxAgeSeconds"/>. Also returns the observed <paramref name="ageSeconds"/> (null when
	/// unverifiable). Disabled when <paramref name="maxAgeSeconds"/> &lt;= 0.</summary>
	public static bool IsFeedStale(IReadOnlyDictionary<string, OptionContractQuote> book, DateTimeOffset now, int maxAgeSeconds, out double? ageSeconds)
	{
		ageSeconds = FreshestQuoteAgeSeconds(book, now);
		return maxAgeSeconds > 0 && ageSeconds is { } a && a > maxAgeSeconds;
	}

	/// <summary>Reason string when the leg's two-sided quote is TORN, else null. A crossed quote (bid &gt;= ask)
	/// is always torn. A wide quote is torn only when its spread clears BOTH <paramref name="maxSpreadPctOfMid"/>
	/// AND <paramref name="minAbsSpread"/> — the AND spares genuinely cheap options (wide in percent but pennies
	/// in absolute terms). One-sided or absent quotes return null: that is the liquidity gate's concern, not
	/// integrity. The percent check is disabled when <paramref name="maxSpreadPctOfMid"/> &lt;= 0 (crossed still fires).</summary>
	public static string? TornReason(OptionContractQuote q, decimal maxSpreadPctOfMid, decimal minAbsSpread)
	{
		if (q.Bid is not { } bid || q.Ask is not { } ask) return null;
		if (bid <= 0m || ask <= 0m) return null;
		if (bid >= ask) return $"crossed bid {bid} >= ask {ask}";
		var spread = ask - bid;
		var mid = (bid + ask) / 2m;
		if (maxSpreadPctOfMid > 0m && mid > 0m && spread > minAbsSpread && spread > maxSpreadPctOfMid * mid)
			return $"wide {bid}/{ask} spread ${spread:0.00} = {spread / mid:P0} of mid";
		return null;
	}

	/// <summary>Max |parity-implied spot − live spot| / spot before an expiry's book is declared frozen. Healthy books sit within a
	/// few basis points (live 2026-10-19 Webull SPY 10-27: 767.37 implied vs 767.40 spot), and the SPX-family composed-open lag
	/// measured 12 bp (see <see cref="ParitySpotGuard"/>); the frozen Schwab SPY book the same morning was 59 bp off.</summary>
	internal const decimal MaxParityDeviation = 0.0025m;

	/// <summary>Expiries are parity-checked only this far out: <see cref="DividendScheduleBuilder"/> models a single upcoming
	/// ex-dividend, so a longer-dated expiry spanning a second one would read the unmodeled dividend as a parity gap.</summary>
	private const int ParityHorizonDays = 90;

	/// <summary>Near-the-money strikes per expiry whose parity spots are medianed — one torn strike can't trip (or mask) the check.</summary>
	private const int ParityStrikes = 3;

	/// <summary>Expiries of <paramref name="ticker"/> in <paramref name="book"/> whose two-sided call/put mids imply a spot more than
	/// <see cref="MaxParityDeviation"/> away from <paramref name="spot"/>. A FROZEN book — the vendor keeps serving an old NBBO
	/// while stamping it fresh and reporting today's trades (Schwab SPY 2026-10-19 after the open: 767P 10-27 bid/ask 8.14/8.70
	/// with a 5.00 last print, every expiry implying 762.9 against a 767.4 spot) — passes the staleness and torn-NBBO checks yet
	/// misprices every leg by the whole move since it froze. Parity is model-free, so no IV assumption can hide it:
	/// S − PV(div) = C − P + K·e^(−rT), using the three strikes nearest spot (American early exercise is negligible there).
	/// Expiries with no near-the-money call/put pair are unassessable and pass.</summary>
	public static IReadOnlyList<ParityGap> ParityGaps(string ticker, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> book, DateTime now, IReadOnlyList<DividendEvent>? dividends, double riskFreeRate)
	{
		var gaps = new List<ParityGap>();
		if (spot <= 0m) return gaps;

		var byExpiry = new Dictionary<DateTime, Dictionary<decimal, (decimal? Call, decimal? Put)>>();
		foreach (var (sym, q) in book)
		{
			if (q.Bid is not > 0m || q.Ask is not { } ask || ask < q.Bid.Value) continue;
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || p.CallPut is not ("C" or "P") || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, p.ExpiryDate)) continue;
			var expiry = p.ExpiryDate.Date;
			if (expiry < now.Date || (expiry - now.Date).Days > ParityHorizonDays) continue;
			if (!byExpiry.TryGetValue(expiry, out var byStrike)) byExpiry[expiry] = byStrike = new Dictionary<decimal, (decimal?, decimal?)>();
			byStrike.TryGetValue(p.Strike, out var pair);
			var mid = (q.Bid.Value + ask) / 2m;
			byStrike[p.Strike] = p.CallPut == "C" ? (mid, pair.Put) : (pair.Call, mid);
		}

		foreach (var (expiry, byStrike) in byExpiry.OrderBy(e => e.Key))
		{
			var close = expiry + OptionMath.MarketClose;
			var discount = (decimal)Math.Exp(-riskFreeRate * Math.Max(0.0, (close - now).TotalDays / 365.0));
			var implied = byStrike.Where(kv => kv.Value.Call != null && kv.Value.Put != null)
				.OrderBy(kv => Math.Abs(kv.Key - spot))
				.Take(ParityStrikes)
				.Select(kv => kv.Value.Call!.Value - kv.Value.Put!.Value + kv.Key * discount)
				.ToList();
			if (implied.Count == 0) continue;
			var impliedSpot = Median(implied);
			var expected = OptionMath.DividendAdjustedSpot(spot, dividends, now, close, riskFreeRate);
			var deviation = (impliedSpot - expected) / spot;
			if (Math.Abs(deviation) > MaxParityDeviation) gaps.Add(new ParityGap(expiry, impliedSpot, expected, deviation));
		}
		return gaps;
	}

	/// <summary>True when any of <paramref name="legs"/> expires on one of <paramref name="expiries"/>.</summary>
	public static bool TouchesExpiry(IEnumerable<ProposalLeg> legs, IReadOnlySet<DateTime> expiries) =>
		expiries.Count > 0 && legs.Any(l => ParsingHelpers.ParseOptionSymbol(l.Symbol) is { } p && expiries.Contains(p.ExpiryDate.Date));

	private static decimal Median(List<decimal> values)
	{
		values.Sort();
		var n = values.Count;
		return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2m;
	}

	/// <summary>Torn-NBBO findings for the given <paramref name="legs"/>. Empty when all legs quote cleanly or
	/// the guard is disabled. Feed staleness is checked separately (once per book) via <see cref="IsFeedStale"/>.</summary>
	public static IReadOnlyList<QuoteIssue> TornLegs(IEnumerable<ProposalLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> book, OpenerQuoteGuardConfig cfg)
	{
		var issues = new List<QuoteIssue>();
		if (cfg is null || !cfg.Enabled) return issues;
		foreach (var leg in legs)
			if (book.TryGetValue(leg.Symbol, out var q) && TornReason(q, cfg.MaxSpreadPctOfMid, cfg.MinAbsSpreadDollars) is { } r)
				issues.Add(new QuoteIssue(QuoteIssueKind.TornNbbo, leg.Symbol, r));
		return issues;
	}
}

/// <summary>Console-facing wrapper shared by <c>wa ai watch</c> and <c>wa ai scan</c>: runs the pure
/// <see cref="QuoteSanity"/> checks over one evaluation's quote book + open proposals, emits loud warnings
/// (so they stand out amid a fast-scrolling watch), and reports which opens to withhold from
/// auto-execution. Kept separate from <see cref="QuoteSanity"/> so that class stays pure/unit-testable.</summary>
internal static class LiveQuoteGuard
{
	/// <summary>Expiries of <paramref name="ticker"/> whose book in <paramref name="book"/> is frozen (see <see cref="QuoteSanity.ParityGaps"/>),
	/// with one loud warning naming them and what is being held (<paramref name="holding"/>). RTH-only, like staleness: after the close
	/// the ETF/stock keeps trading against books that legitimately stopped at 16:00, and the premarket window belongs to
	/// <see cref="PremarketSpotOverride"/>. Empty when the guard is disabled or <paramref name="spot"/> is unknown.</summary>
	public static IReadOnlySet<DateTime> FrozenExpiries(IReadOnlyDictionary<string, OptionContractQuote> book, string ticker, decimal? spot, DateTime now, bool marketOpen, string vendorName, IReadOnlyList<DividendEvent>? dividends, OpenerQuoteGuardConfig cfg, string holding)
	{
		if (cfg is null || !cfg.Enabled || !marketOpen || spot is not > 0m) return new HashSet<DateTime>();
		var gaps = QuoteSanity.ParityGaps(ticker, spot.Value, book, now, dividends, OptionMath.RiskFreeRate);
		if (gaps.Count == 0) return new HashSet<DateTime>();
		var worst = gaps.MaxBy(g => Math.Abs(g.Deviation));
		AnsiConsole.MarkupLine($"[red bold]⚠ QUOTE WARNING:[/] {Markup.Escape(vendorName)} {Markup.Escape(ticker)} book looks FROZEN — put-call parity implies ${worst.ImpliedSpot:N2} vs spot ${worst.ExpectedSpot:N2} ({worst.Deviation * 10000m:+0;-0} bp) on {Markup.Escape(string.Join(", ", gaps.Select(g => g.Expiry.ToString("yyyy-MM-dd"))))}. Holding {Markup.Escape(holding)} on those expiries; mids/--limit unreliable.");
		return gaps.Select(g => g.Expiry).ToHashSet();
	}

	/// <summary>Warns on feed staleness and torn proposal legs, and returns what to hold: <c>FeedStale</c>
	/// (poisons every quote → hold ALL opens this evaluation) and <c>Suspect</c> (proposal index → reason, for
	/// proposals with a torn leg or a leg on a <paramref name="frozenExpiries"/> expiry → hold just those).
	/// <paramref name="staleUnverifiableNoted"/> gates the one-time "no timestamp" note across watch ticks;
	/// scan passes a throwaway. No-op when the guard is disabled.</summary>
	public static (bool FeedStale, Dictionary<int, string> Suspect) Inspect(IReadOnlyDictionary<string, OptionContractQuote> book, DateTimeOffset now, bool marketOpen, string vendorName, OpenerQuoteGuardConfig cfg, IReadOnlyList<OpenProposal> proposals, IReadOnlySet<DateTime> frozenExpiries, ref bool staleUnverifiableNoted)
	{
		var suspect = new Dictionary<int, string>();
		if (cfg is null || !cfg.Enabled) return (false, suspect);

		// Staleness is RTH-only: after the close every quote is legitimately minutes-to-hours old, so checking
		// off-hours would cry wolf on every research scan and train the eye to ignore the real warning. Torn-NBBO
		// runs regardless (a crossed/absurd spread is wrong at any hour).
		var feedStale = false;
		if (marketOpen)
		{
			feedStale = QuoteSanity.IsFeedStale(book, now, cfg.MaxQuoteAgeSeconds, out var freshestAge);
			if (feedStale)
				AnsiConsole.MarkupLine($"[red bold]⚠ QUOTE WARNING:[/] {Markup.Escape(vendorName)} feed looks STALE — freshest quote {freshestAge:0}s old (> {cfg.MaxQuoteAgeSeconds}s). Holding opens; mid/--limit unreliable.");
			else if (freshestAge is null && !staleUnverifiableNoted)
			{
				staleUnverifiableNoted = true;
				AnsiConsole.MarkupLine($"[yellow]note:[/] staleness unverifiable on {Markup.Escape(vendorName)} (quotes carry no timestamp this tick); torn-NBBO guard still active.");
			}
		}

		for (var i = 0; i < proposals.Count; i++)
		{
			var torn = QuoteSanity.TornLegs(proposals[i].Legs, book, cfg);
			if (torn.Count > 0)
			{
				suspect[i] = "torn NBBO on a leg";
				AnsiConsole.MarkupLine($"[red bold]⚠ QUOTE WARNING:[/] rank {i + 1} {proposals[i].StructureKind} has TORN quotes — {Markup.Escape(string.Join("; ", torn.Select(t => $"{t.Symbol} {t.Detail}")))}. Not auto-executed.");
			}
			else if (QuoteSanity.TouchesExpiry(proposals[i].Legs, frozenExpiries))
				suspect[i] = "frozen book on a leg's expiry";
		}
		return (feedStale, suspect);
	}
}
