using Spectre.Console;

namespace WebullAnalytics.AI;

/// <summary>What kind of quote-integrity problem the live guard found.</summary>
internal enum QuoteIssueKind { StaleFeed, TornNbbo }

/// <summary>One quote-integrity finding. <see cref="Symbol"/> is empty for the feed-level staleness
/// finding (it describes the whole book, not a single leg).</summary>
internal readonly record struct QuoteIssue(QuoteIssueKind Kind, string Symbol, string Detail);

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
	/// <summary>Warns on feed staleness and torn proposal legs, and returns what to hold: <c>FeedStale</c>
	/// (poisons every quote → hold ALL opens this evaluation) and <c>Suspect</c> (indices of proposals with
	/// torn legs → hold just those). <paramref name="staleUnverifiableNoted"/> gates the one-time
	/// "no timestamp" note across watch ticks; scan passes a throwaway. No-op when the guard is disabled.</summary>
	public static (bool FeedStale, HashSet<int> Suspect) Inspect(IReadOnlyDictionary<string, OptionContractQuote> book, DateTimeOffset now, string vendorName, OpenerQuoteGuardConfig cfg, IReadOnlyList<OpenProposal> proposals, ref bool staleUnverifiableNoted)
	{
		var suspect = new HashSet<int>();
		if (cfg is null || !cfg.Enabled) return (false, suspect);

		var feedStale = QuoteSanity.IsFeedStale(book, now, cfg.MaxQuoteAgeSeconds, out var freshestAge);
		if (feedStale)
			AnsiConsole.MarkupLine($"[red bold]⚠ QUOTE WARNING:[/] {Markup.Escape(vendorName)} feed looks STALE — freshest quote {freshestAge:0}s old (> {cfg.MaxQuoteAgeSeconds}s). Holding opens; mid/--limit unreliable.");
		else if (freshestAge is null && !staleUnverifiableNoted)
		{
			staleUnverifiableNoted = true;
			AnsiConsole.MarkupLine($"[yellow]note:[/] staleness unverifiable on {Markup.Escape(vendorName)} (quotes carry no timestamp this tick); torn-NBBO guard still active.");
		}

		for (var i = 0; i < proposals.Count; i++)
		{
			var torn = QuoteSanity.TornLegs(proposals[i].Legs, book, cfg);
			if (torn.Count > 0)
			{
				suspect.Add(i);
				AnsiConsole.MarkupLine($"[red bold]⚠ QUOTE WARNING:[/] rank {i + 1} {proposals[i].StructureKind} has TORN quotes — {Markup.Escape(string.Join("; ", torn.Select(t => $"{t.Symbol} {t.Detail}")))}. Not auto-executed.");
			}
		}
		return (feedStale, suspect);
	}
}
