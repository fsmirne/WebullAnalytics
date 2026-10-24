using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api.ThetaData;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Report;

/// <summary>
/// Past --date evaluation: marks legs from the REAL NBBO at that date's close instead of Black-Scholes-un-decaying
/// today's live quotes. The live-quote path is only honest for today/future dates — for a past date the quote store
/// (the backtest's own price foundation) has the actual bid/ask as of that session, while a live after-hours book can
/// carry frozen one-sided quotes whose vendor IVs are unanchored (a dead 0.01-ask residue reporting IV 145% reprices a
/// 1-DTE leg to a multiple of its real value). Per-leg: a symbol with no stored NBBO for the date (evening backfill not
/// landed yet, contract outside the captured ladder) is fetched from ThetaData at the same close instant when a
/// ThetaData login is configured; one neither covers keeps its live quote and falls back to the guarded BS reprice.
/// Every substitution and miss is printed so a real-marked and a live-repriced leg are never silently mixed. Open
/// interest is the date's own (<see cref="AsOfOpenInterest"/>), never today's. Underlying spots are likewise replaced
/// with the date's daily close (local history, else ThetaData's EOD; a --spot override still wins downstream), so grids
/// and break-evens center on the as-of market, not today's.
/// </summary>
internal static class HistoricalMarkOverlay
{
	/// <param name="AllLegsCovered">True when EVERY requested leg was marked from real as-of NBBO — the signal that
	/// quote mids ARE as-of-the-eval-date marks, letting the current-P&L/market-value paths drop their
	/// past-date guard (see <see cref="AnalysisOptions.MarksAsOfEvalDate"/>).</param>
	internal sealed record Result(
		IReadOnlyDictionary<string, OptionContractQuote>? Quotes,
		IReadOnlyDictionary<string, decimal>? UnderlyingPrices,
		bool AllLegsCovered);

	// The close mark accepts the last two-sided NBBO within this many minutes before the close. Minute NBBO is
	// dense for liquid near-money contracts; thin far-dated legs can gap a few minutes near the close.
	private const int CloseStalenessMinutes = 15;

	internal static async Task<Result> ApplyAsync(DateTime evalDate, IReadOnlyCollection<string> optionSymbols,
		IReadOnlyDictionary<string, OptionContractQuote>? liveQuotes, IReadOnlyDictionary<string, decimal>? liveUnderlyings,
		CancellationToken cancellation)
	{
		var symbols = optionSymbols.Where(s => ParsingHelpers.ParseOptionSymbol(s) != null).ToList();
		// The session's own close (13:00 on NYSE half-days), and the store label whose book is struck at it.
		var closeEt = evalDate.Date + MarketCalendar.CloseTimeEt(evalDate);
		using var theta = ThetaFallback.TryCreate();

		var dbPath = Program.ResolvePath("data/quotes.db");
		var store = File.Exists(dbPath) ? new QuoteStoreCache(dbPath, maxStaleMinutes: CloseStalenessMinutes, since: evalDate, until: evalDate) : null;
		if (store == null) Console.WriteLine($"Warning: quote store not found ({dbPath}).");
		var books = new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase);
		foreach (var symbol in symbols)
			if (store?.NbboAt(symbol, closeEt - QuoteStoreCache.BookLag) is { } nbbo) books[symbol] = nbbo;
		var storeCovered = books.Count;

		var unstored = symbols.Where(s => !books.ContainsKey(s)).ToList();
		string? thetaError = null;
		if (unstored.Count > 0 && theta != null)
		{
			(var fetched, thetaError) = await theta.BooksAtAsync(unstored, closeEt, cancellation);
			foreach (var (symbol, book) in fetched) books[symbol] = book;
		}

		var openInterest = await AsOfOpenInterest.LoadAsync(symbols, evalDate, theta, cancellation);
		var quotes = liveQuotes != null
			? new Dictionary<string, OptionContractQuote>(liveQuotes, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (symbol, book) in books)
		{
			// HV carries over from the live quote: it is the sane-IV fallback GetLegIv reaches for when a leg's IV can't be
			// solved from the as-of mid. IV is left null on purpose — the pipeline auto-calibrates a mid-implied IV from this
			// bid/ask, which is the surface the grid then decays on; a vendor IV struck today has no business pricing an
			// as-of-then mark.
			OptionContractQuote? live = null;
			liveQuotes?.TryGetValue(symbol, out live);
			quotes[symbol] = new OptionContractQuote(symbol, LastPrice: book.Mid, Bid: book.Bid, Ask: book.Ask,
				Change: null, PercentChange: null, Volume: null, OpenInterest: openInterest.TryGetValue(symbol, out var oi) ? oi : null,
				ImpliedVolatility: null, HistoricalVolatility: live?.HistoricalVolatility,
				BidSize: book.BidSize, AskSize: book.AskSize);
		}

		var underlyings = liveUnderlyings != null
			? new Dictionary<string, decimal>(liveUnderlyings, StringComparer.OrdinalIgnoreCase)
			: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var priceCache = new HistoricalPriceCache();
		foreach (var root in symbols.Select(s => ParsingHelpers.ParseOptionSymbol(s)!.Root).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var close = await priceCache.GetCloseAsync(root, evalDate, cancellation);
			if (close == null && theta != null)
			{
				(close, var closeError) = await theta.DailyCloseAsync(root, evalDate, cancellation);
				if (close != null) Console.WriteLine($"{root} close for {evalDate:yyyy-MM-dd} from ThetaData EOD (not in local history).");
				else Console.WriteLine($"⚠ {root}: no {evalDate:yyyy-MM-dd} close in local history or ThetaData{(closeError != null ? $" ({closeError})" : "")} — spot stays the live quote.");
			}
			if (close.HasValue) underlyings[root] = close.Value;
		}

		var misses = symbols.Where(s => !books.ContainsKey(s)).ToList();
		var thetaCovered = books.Count - storeCovered;
		Console.WriteLine($"Marks as of {evalDate:yyyy-MM-dd} close: {books.Count}/{symbols.Count} leg(s) priced from real NBBO ({storeCovered} stored{(thetaCovered > 0 ? $", {thetaCovered} from ThetaData" : "")}).");
		var missReason = theta == null ? "not in the quote store and no ThetaData login is configured (if the date is recent, re-run after the evening backfill lands)"
			: thetaError != null ? $"not in the quote store; ThetaData failed ({thetaError})"
			: "neither the quote store nor ThetaData has a book at the close";
		foreach (var symbol in misses)
			Console.WriteLine($"⚠ {symbol}: {missReason} — leg is BS-repriced from its live quote.");

		return new Result(quotes.Count > 0 ? quotes : liveQuotes, underlyings.Count > 0 ? underlyings : liveUnderlyings,
			books.Count > 0 && misses.Count == 0);
	}
}
