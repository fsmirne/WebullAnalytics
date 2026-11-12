using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Analyze;
using WebullAnalytics.Api.ThetaData;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Report;

/// <summary>
/// A past evaluation instant (--date, --until, a fill): marks every leg from the REAL NBBO at that instant instead of Black-Scholes-un-decaying
/// today's live quotes. The live-quote path is only honest for now/future instants — for a past one the quote store (the backtest's own price
/// foundation) has the actual bid/ask, while a live after-hours book can carry frozen one-sided quotes whose vendor IVs are unanchored (a dead
/// 0.01-ask residue reporting IV 145% reprices a 1-DTE leg to a multiple of its real value). The books come from <see cref="FillTimeMarket"/>'s
/// loader — the quote store at exactly that minute, else ThetaData when a login is configured — and a leg neither has is an error naming why:
/// real and repriced legs are never mixed. Open interest is the session's own (<see cref="AsOfOpenInterest"/>), never today's. Each root's spot
/// is its 1-minute-bar price at the instant, so grids and break-evens center on the as-of market, not today's. Pins the quote anchor
/// (<see cref="OptionMath.HistoricalQuotesStruckAt"/>) and that session's risk-free rate, so every IV solve and mark prices on the books' clock.
/// </summary>
internal static class HistoricalMarkOverlay
{
	internal sealed record Result(IReadOnlyDictionary<string, OptionContractQuote> Quotes, IReadOnlyDictionary<string, decimal> UnderlyingPrices);

	/// <param name="nearest">A fill's instant (the boundary nearest it) rather than an exact --date minute; see <see cref="FillTimeMarket.Boundaries"/>.</param>
	internal static async Task<(Result? Market, string? Error)> ApplyAsync(DateTime instantEt, bool nearest, IReadOnlyCollection<string> optionSymbols,
		IReadOnlyDictionary<string, OptionContractQuote>? liveQuotes, IReadOnlyDictionary<string, decimal>? liveUnderlyings, CancellationToken cancellation)
	{
		var symbols = optionSymbols.Where(s => ParsingHelpers.ParseOptionSymbol(s) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (!MarketCalendar.IsOpen(instantEt.Date)) return (null, $"{instantEt:yyyy-MM-dd} is not a trading session");
		var apiConfig = OpenCandidateEvaluator.TryLoadApiConfig();
		if (apiConfig == null) return (null, "the instant's spot comes from the Webull minute-bar cache, and api-config.json is missing (run 'wa sniff')");

		using var theta = ThetaFallback.TryCreate();
		var (books, instant, source, booksError) = await FillTimeMarket.LegBooksAsync(FillTimeMarket.OpenStore(instantEt.Date), theta, symbols, FillTimeMarket.Boundaries(instantEt, nearest), cancellation);
		if (books == null) return (null, booksError);

		var openInterest = await AsOfOpenInterest.LoadAsync(symbols, instant.Date, theta, cancellation);
		var quotes = liveQuotes != null ? new Dictionary<string, OptionContractQuote>(liveQuotes, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var (symbol, book) in books)
		{
			// HV carries over from the live quote: it is the sane-IV fallback GetLegIv reaches for when a leg's IV can't be solved from the
			// as-of mid. IV stays null — the pipeline back-solves it from this bid/ask at the instant it was struck.
			OptionContractQuote? live = null;
			liveQuotes?.TryGetValue(symbol, out live);
			quotes[symbol] = FillTimeMarket.ToQuote(symbol, book, openInterest.TryGetValue(symbol, out var oi) ? oi : null) with { HistoricalVolatility = live?.HistoricalVolatility };
		}

		var underlyings = liveUnderlyings != null ? new Dictionary<string, decimal>(liveUnderlyings, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var intradayCache = new IntradayBarCache(WebullIntradayBars.CreateFetcher(apiConfig));
		var roots = symbols.Select(s => ParsingHelpers.ParseOptionSymbol(s)!.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (var root in roots)
		{
			var (spot, _, _, spotError) = await FillTimeMarket.SpotAtAsync(root, instant, intradayCache, cancellation);
			if (spotError != null) return (null, spotError);
			underlyings[root] = spot;
		}

		OptionMath.HistoricalQuotesStruckAt = instant;
		await FillTimeMarket.ApplySessionRateAsync(instant.Date, cancellation);
		Console.WriteLine($"Market as of {instant:yyyy-MM-dd HH:mm} ET: {books.Count} leg(s) from {source}; spot {string.Join(", ", roots.Select(r => $"{r} ${underlyings[r]:F2}"))}.");
		return (new Result(quotes, underlyings), null);
	}
}
