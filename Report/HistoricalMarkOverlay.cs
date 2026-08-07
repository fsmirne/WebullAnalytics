using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Report;

/// <summary>
/// Past --date evaluation: marks legs from the quote store's REAL minute NBBO at that date's close instead of
/// Black-Scholes-un-decaying today's live quotes. The live-quote path is only honest for today/future dates —
/// for a past date the store (the backtest's own price foundation) has the actual bid/ask as of that session,
/// while a live after-hours book can carry frozen one-sided quotes whose vendor IVs are unanchored (a dead
/// 0.01-ask residue reporting IV 145% reprices a 1-DTE leg to a multiple of its real value). Per-leg: a symbol
/// with no stored NBBO for the date (evening backfill not landed yet, contract outside the captured ladder)
/// keeps its live quote and falls back to the guarded BS reprice; every substitution and miss is printed so a
/// store-marked and a live-repriced leg are never silently mixed. Underlying spots are likewise replaced with
/// the date's daily close (a --spot override still wins downstream), so grids and break-evens center on the
/// as-of market, not today's.
/// </summary>
internal static class HistoricalMarkOverlay
{
	/// <param name="AllLegsCovered">True when EVERY requested leg was marked from stored NBBO — the signal that
	/// quote mids ARE as-of-the-eval-date marks, letting the current-P&L/market-value paths drop their
	/// past-date guard (see <see cref="AnalysisOptions.MarksAsOfEvalDate"/>).</param>
	internal sealed record Result(
		IReadOnlyDictionary<string, OptionContractQuote>? Quotes,
		IReadOnlyDictionary<string, decimal>? UnderlyingPrices,
		bool AllLegsCovered);

	// The close mark accepts the last two-sided NBBO within this many minutes before 16:00. Minute NBBO is
	// dense for liquid near-money contracts; thin far-dated legs can gap a few minutes near the close.
	private const int CloseStalenessMinutes = 15;

	internal static async Task<Result> ApplyAsync(DateTime evalDate, IReadOnlyCollection<string> optionSymbols,
		IReadOnlyDictionary<string, OptionContractQuote>? liveQuotes, IReadOnlyDictionary<string, decimal>? liveUnderlyings,
		CancellationToken cancellation)
	{
		var dbPath = Program.ResolvePath("data/quotes.db");
		if (!File.Exists(dbPath))
		{
			Console.WriteLine($"Warning: quote store not found ({dbPath}) — legs are BS-repriced from live quotes to {evalDate:yyyy-MM-dd}.");
			return new Result(liveQuotes, liveUnderlyings, false);
		}

		var store = new QuoteStoreCache(dbPath, maxStaleMinutes: CloseStalenessMinutes, since: evalDate, until: evalDate);
		var closeEt = evalDate.Date + OptionMath.MarketClose;
		var quotes = liveQuotes != null
			? new Dictionary<string, OptionContractQuote>(liveQuotes, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var covered = 0;
		var misses = new List<string>();

		foreach (var symbol in optionSymbols)
		{
			if (ParsingHelpers.ParseOptionSymbol(symbol) == null) continue;
			var nbbo = store.NbboAt(symbol, closeEt);
			if (nbbo == null) { misses.Add(symbol); continue; }
			covered++;

			// Carry OI/HV over from the live quote where available: OI is display-only, and HV is the sane-IV
			// fallback GetLegIv reaches for when a leg's IV can't be solved from the stored mid. IV is left null
			// on purpose — the pipeline auto-calibrates a mid-implied IV from this bid/ask, which is the surface
			// the grid then decays on; a vendor IV struck today has no business pricing an as-of-then mark.
			OptionContractQuote? live = null;
			liveQuotes?.TryGetValue(symbol, out live);
			quotes[symbol] = new OptionContractQuote(symbol, LastPrice: nbbo.Value.Mid, Bid: nbbo.Value.Bid, Ask: nbbo.Value.Ask,
				Change: null, PercentChange: null, Volume: null, OpenInterest: live?.OpenInterest,
				ImpliedVolatility: null, HistoricalVolatility: live?.HistoricalVolatility,
				BidSize: nbbo.Value.BidSize, AskSize: nbbo.Value.AskSize);
		}

		var underlyings = liveUnderlyings != null
			? new Dictionary<string, decimal>(liveUnderlyings, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var priceCache = new HistoricalPriceCache();
		var roots = optionSymbols.Select(s => ParsingHelpers.ParseOptionSymbol(s)?.Root).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			var close = await priceCache.GetCloseAsync(root!, evalDate, cancellation);
			if (close.HasValue) underlyings[root!] = close.Value;
		}

		Console.WriteLine($"Marks as of {evalDate:yyyy-MM-dd} close: {covered}/{covered + misses.Count} leg(s) priced from stored real NBBO.");
		foreach (var symbol in misses)
			Console.WriteLine($"⚠ {symbol}: no stored NBBO for {evalDate:yyyy-MM-dd} — leg is BS-repriced from its live quote (if the date is recent, re-run after the evening backfill lands).");

		return new Result(quotes.Count > 0 ? quotes : liveQuotes, underlyings.Count > 0 ? underlyings : liveUnderlyings,
			covered > 0 && misses.Count == 0);
	}
}
