using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using WebullAnalytics.Api.ThetaData;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Analyze;

/// <summary>
/// The market at a past fill, from the same stores the backtest prices from: each leg's real NBBO
/// (quotes.db, ThetaData minute snapshots), that day's causal open interest (the daily OI snapshot), and the
/// underlying's spot plus session high/low from 1-minute bars. The store's resolution is one book per minute:
/// the row labeled M (app standard — 09:30 is the first minute) is the book at the END of minute M, i.e. at
/// M+1:00. So the market is read at the minute boundary NEAREST the fill (either side — this describes a fill
/// that already happened, the fill price itself carries that moment; it is not a decision that must not see the
/// future), falling back to the other boundary when the nearer one has no book (the 09:30:00 boundary is
/// pre-open). Every input is taken at that one instant. Nothing is approximated beyond that minute resolution.
///
/// When the store has no book for every leg at either boundary — today's fills before the evening backfill, roots or strikes
/// the backfill doesn't cover (manual trades on any ticker) — and a ThetaData login is configured, the books come from ThetaData
/// directly: option_at_time_quote at the boundary instant is the same vendor book the backfill stores for that boundary (its
/// END-of-minute stamp T is stored under label T-1), encoded by the store's rule. A leg no source covers is an error naming why,
/// never a substitute price. Open interest likewise falls back to ThetaData (<see cref="AsOfOpenInterest"/>). A past session's
/// spot bars missing from the on-disk tape (Webull's live m1 feed only reaches back ~5 days) are healed first by the tape
/// backfill's deep-history sources (Webull's paginator, massive), the way `wa ai history` fills them.
/// </summary>
internal static class FillTimeMarket
{
	/// <param name="Instant">The minute boundary the market is read at (ET) — report its offset from the fill.</param>
	/// <param name="BookSource">Where the leg books came from (the quote store or ThetaData), for the report header.</param>
	internal sealed record Snapshot(DateTime Instant, IReadOnlyDictionary<string, OptionContractQuote> Quotes, decimal Spot, decimal SessionHigh, decimal SessionLow, string BookSource);

	private static readonly TimeZoneInfo EtZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	public static async Task<(Snapshot? Market, string? Error)> LoadAsync(string ticker, IReadOnlyCollection<string> symbols, DateTime fillEt, IntradayBarCache intradayCache, CancellationToken cancellation)
	{
		var before = fillEt.Date + TimeSpan.FromMinutes(Math.Floor(fillEt.TimeOfDay.TotalMinutes));
		var after = before.AddMinutes(1);
		var boundaries = fillEt - before <= after - fillEt ? new[] { before, after } : new[] { after, before };

		using var theta = ThetaFallback.TryCreate();
		var (books, instant, bookSource, storeMiss) = StoreBooks(symbols, fillEt, boundaries);
		if (books == null)
		{
			if (theta == null)
				return (null, $"no NBBO at {before:HH:mm} or {after:HH:mm} for every leg ({string.Join(", ", symbols)}) — quote store: {storeMiss}; no ThetaData login configured ({ThetaDataClient.DefaultCredsPath()}) to fetch it");
			string? thetaMiss;
			(books, instant, thetaMiss) = await ThetaBooksAsync(theta, symbols, boundaries, cancellation);
			bookSource = "ThetaData at-time NBBO";
			if (books == null)
				return (null, $"no NBBO at {before:HH:mm} or {after:HH:mm} for every leg ({string.Join(", ", symbols)}) — quote store: {storeMiss}; ThetaData: {thetaMiss}");
		}
		var oi = await AsOfOpenInterest.LoadAsync(symbols, fillEt.Date, theta, cancellation);
		var quotes = books.ToDictionary(kv => kv.Key, kv => new OptionContractQuote(kv.Key, LastPrice: kv.Value.Mid, Bid: kv.Value.Bid, Ask: kv.Value.Ask, Change: null, PercentChange: null, Volume: null, OpenInterest: oi.TryGetValue(kv.Key, out var o) ? o : null, ImpliedVolatility: null, HistoricalVolatility: null, BidSize: kv.Value.BidSize, AskSize: kv.Value.AskSize), StringComparer.OrdinalIgnoreCase);

		await HealPastSessionTapeAsync(ticker, fillEt.Date, cancellation);

		// Bars are start-of-bar labeled: those starting before the instant are complete at it, and the last one's
		// close is the price at the instant.
		var openUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fillEt.Date + OptionMath.MarketOpen, EtZone));
		var instantUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(instant, EtZone));
		var bars = (await intradayCache.GetBarsAsync(ticker, openUtc, instantUtc, BarInterval.M1, includeExtended: false, cancellation)).Where(b => b.Timestamp < instantUtc).ToList();
		if (bars.Count == 0) return (null, $"no {ticker} 1-minute bars for {fillEt:yyyy-MM-dd} up to {instant:HH:mm} ET");

		return (new Snapshot(instant, quotes, bars[^1].Close, bars.Max(b => b.High), bars.Min(b => b.Low), bookSource), null);
	}

	/// <summary>Every leg's stored book at the first boundary that has one for all of them, or the reason none does.</summary>
	private static (Dictionary<string, QuoteStoreCache.QuoteAt>? Books, DateTime Instant, string Source, string? Miss) StoreBooks(IReadOnlyCollection<string> symbols, DateTime fillEt, DateTime[] boundaries)
	{
		const string source = "the nearest stored minute book";
		var dbPath = Program.ResolvePath("data/quotes.db");
		if (!File.Exists(dbPath)) return (null, default, source, $"not found ({dbPath})");

		// maxStaleMinutes 0: only the exact minute's book — a boundary without one falls to the other boundary,
		// never to an older minute.
		var store = new QuoteStoreCache(dbPath, maxStaleMinutes: 0, since: fillEt.Date, until: fillEt.Date);
		foreach (var boundary in boundaries)
			if (BooksAt(symbols, symbol => store.NbboAt(symbol, boundary - QuoteStoreCache.BookLag)) is { } books)
				return (books, boundary, source, null);
		return (null, default, source, MissingReason(store, symbols, fillEt));
	}

	/// <summary>Every leg's ThetaData book at the first boundary that has one for all of them, or why none does (no data, or the
	/// session is unavailable — e.g. the nightly backfill holds it).</summary>
	private static async Task<(Dictionary<string, QuoteStoreCache.QuoteAt>? Books, DateTime Instant, string? Miss)> ThetaBooksAsync(ThetaFallback theta, IReadOnlyCollection<string> symbols, DateTime[] boundaries, CancellationToken cancellation)
	{
		foreach (var boundary in boundaries)
		{
			var (found, error) = await theta.BooksAtAsync(symbols, boundary, cancellation);
			if (error != null) return (null, default, error);
			if (BooksAt(symbols, symbol => found.TryGetValue(symbol, out var book) ? book : null) is { } books)
				return (books, boundary, null);
		}
		return (null, default, "no book at either boundary for every leg");
	}

	/// <summary>Re-pulls a past session's spot tape when the on-disk file is missing or partial — before the bar cache reads it, since the cache
	/// never refetches a past day beyond Webull's ~5-day m1 window and remembers a day it found empty. Today's session is the cache's own live
	/// fetch. Best effort: the backfill logs its own failures, and a session still without bars is reported by the caller.</summary>
	private static async Task HealPastSessionTapeAsync(string ticker, DateTime sessionEt, CancellationToken cancellation)
	{
		var todayEt = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EtZone).Date;
		if (sessionEt.Date >= todayEt || IntradayTapeBackfill.AuditIntraday(ticker, sessionEt.Date, sessionEt.Date.AddDays(1)).Complete.Contains(sessionEt.Date)) return;
		await IntradayTapeBackfill.BackfillAsync(ticker, TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(sessionEt.Date.AddHours(12), DateTimeKind.Unspecified), EtZone), DateTime.UtcNow, cancellation, latestNy: sessionEt.Date);
	}

	/// <summary>Every leg's book from <paramref name="bookOf"/>, or null when any leg has none.</summary>
	private static Dictionary<string, QuoteStoreCache.QuoteAt>? BooksAt(IReadOnlyCollection<string> symbols, Func<string, QuoteStoreCache.QuoteAt?> bookOf)
	{
		var books = new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase);
		foreach (var symbol in symbols)
		{
			if (bookOf(symbol) is not { } book) return null;
			books[symbol] = book;
		}
		return books;
	}

	/// <summary>Why neither boundary has a book: the session isn't stored yet, the store has a gap there, or the
	/// contracts aren't backfilled at all.</summary>
	private static string MissingReason(QuoteStoreCache store, IReadOnlyCollection<string> symbols, DateTime fillEt)
	{
		if (symbols.All(sym => new[] { 12, 15 }.Any(h => store.NbboAt(sym, fillEt.Date.AddHours(h)) != null)))
			return "the store has a gap in these contracts' quotes at the fill";
		return fillEt.Date >= DateTime.Today
			? "today's quotes reach quotes.db only with the evening ThetaData backfill"
			: "the backfill doesn't cover these contracts (root, expiry or strike band)";
	}
}
