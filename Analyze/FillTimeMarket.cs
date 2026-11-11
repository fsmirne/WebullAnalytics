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
///
/// The snapshot also carries each leg expiry's chain within ±<see cref="ChainBand"/> of spot at the same instant (the store's minute
/// books, else one expiry-wide ThetaData request), with that expiry's open interest for the day.
/// The diagnostic's chain-wide reads — GEX gravity, net gamma, max pain, walls, relative OI — need the expiry's whole book: built from the
/// legs alone they collapse onto the legs' own strikes (a lone short call read as max pain and gravity at its strike, net gamma +1.00).
/// An expiry whose chain neither source has is an error naming why, never a legs-only stand-in. A caller can ask for more expiries'
/// chains (scenario roll targets) through the extra-symbols hook; those are context, so a miss there is listed in
/// <see cref="Snapshot.ChainMisses"/> rather than failing the load.
/// </summary>
internal static class FillTimeMarket
{
	/// <param name="Instant">The minute boundary the market is read at (ET) — report its offset from the fill.</param>
	/// <param name="BookSource">Where the leg books came from (the quote store or ThetaData), for the report header.</param>
	/// <param name="ChainMisses">Extra expiries whose chain no source had, each with why (empty when every one loaded).</param>
	internal sealed record Snapshot(DateTime Instant, IReadOnlyDictionary<string, OptionContractQuote> Quotes, decimal Spot, decimal SessionHigh, decimal SessionLow, string BookSource, IReadOnlyList<string> ChainMisses);


	/// <summary>Strike window of each leg expiry's chain, as a fraction of spot — the GEX/max-pain window the live chain paths use.</summary>
	private const decimal ChainBand = 0.20m;

	/// <param name="extraSymbols">Given the instant's spot, more contracts whose expiries' chains to load as context (non-fatal).</param>
	public static async Task<(Snapshot? Market, string? Error)> LoadAsync(string ticker, IReadOnlyCollection<string> symbols, DateTime fillEt, IntradayBarCache intradayCache, CancellationToken cancellation, Func<decimal, IEnumerable<string>>? extraSymbols = null)
	{
		var before = fillEt.Date + TimeSpan.FromMinutes(Math.Floor(fillEt.TimeOfDay.TotalMinutes));
		var after = before.AddMinutes(1);
		var boundaries = fillEt - before <= after - fillEt ? new[] { before, after } : new[] { after, before };

		using var theta = ThetaFallback.TryCreate();
		var store = OpenStore(fillEt);
		var (books, instant, bookSource, storeMiss) = StoreBooks(store, symbols, fillEt, boundaries);
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
		await HealPastSessionTapeAsync(ticker, fillEt.Date, cancellation);

		// Bars are start-of-bar labeled: those starting before the instant are complete at it, and the last one's
		// close is the price at the instant.
		var openUtc = MarketTime.EtToInstant(fillEt.Date + OptionMath.MarketOpen);
		var instantUtc = MarketTime.EtToInstant(instant);
		var bars = (await intradayCache.GetBarsAsync(ticker, openUtc, instantUtc, BarInterval.M1, includeExtended: false, cancellation)).Where(b => b.Timestamp < instantUtc).ToList();
		if (bars.Count == 0) return (null, $"no {ticker} 1-minute bars for {fillEt:yyyy-MM-dd} up to {instant:HH:mm} ET");
		var spot = bars[^1].Close;

		// Each leg expiry's chain at the instant plus that expiry's open interest for the day (required), then any extra expiries (context).
		var chainBooks = new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase);
		var oi = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var legExpiries = Expiries(symbols).ToList();
		foreach (var (root, expiry) in legExpiries)
		{
			var miss = await AddChainAsync(store, theta, root, expiry, instant, spot, chainBooks, oi, cancellation);
			if (miss != null) return (null, $"{miss} — the chain-wide reads (GEX, max pain, relative OI) need it");
		}
		var chainMisses = new List<string>();
		var extraExpiries = Expiries(extraSymbols?.Invoke(spot) ?? []).Except(legExpiries).ToList();
		foreach (var (root, expiry) in extraExpiries)
			if (await AddChainAsync(store, theta, root, expiry, instant, spot, chainBooks, oi, cancellation) is { } miss) chainMisses.Add(miss);
		foreach (var (sym, book) in books) chainBooks[sym] = book;

		var quotes = chainBooks.ToDictionary(kv => kv.Key, kv => new OptionContractQuote(kv.Key, LastPrice: kv.Value.Mid, Bid: kv.Value.Bid, Ask: kv.Value.Ask, Change: null, PercentChange: null, Volume: null, OpenInterest: oi.TryGetValue(kv.Key, out var o) ? o : null, ImpliedVolatility: null, HistoricalVolatility: null, BidSize: kv.Value.BidSize, AskSize: kv.Value.AskSize), StringComparer.OrdinalIgnoreCase);
		return (new Snapshot(instant, quotes, spot, bars.Max(b => b.High), bars.Min(b => b.Low), $"{bookSource} ({quotes.Count} contracts across {legExpiries.Count + extraExpiries.Count - chainMisses.Count} expiries)", chainMisses), null);
	}

	private static IEnumerable<(string Root, DateTime Expiry)> Expiries(IEnumerable<string> symbols) =>
		symbols.Select(ParsingHelpers.ParseOptionSymbol).Where(p => p != null).Select(p => (p!.Root.ToUpperInvariant(), p.ExpiryDate.Date)).Distinct();

	/// <summary>Adds one expiry's chain at <paramref name="instant"/> (store first, else ThetaData) and its open interest for the day; returns why
	/// when either is unavailable (nothing is added then).</summary>
	private static async Task<string?> AddChainAsync(QuoteStoreCache? store, ThetaFallback? theta, string root, DateTime expiry, DateTime instant, decimal spot, Dictionary<string, QuoteStoreCache.QuoteAt> chainBooks, Dictionary<string, long> oi, CancellationToken cancellation)
	{
		var (expiryBooks, storeMiss) = store != null ? StoreChain(store, root, expiry, instant, spot) : (null, "no quote store");
		string? thetaMiss = null;
		if (expiryBooks == null && theta != null) (expiryBooks, thetaMiss) = await ThetaChainAsync(theta, root, expiry, instant, spot, cancellation);
		if (expiryBooks == null) return $"no {root} {expiry:yyyy-MM-dd} chain at {instant:HH:mm} ET ({storeMiss}; {thetaMiss ?? "no ThetaData login configured"})";
		var (expiryOi, oiMiss) = await AsOfOpenInterest.LoadExpiryAsync(root, expiry, instant.Date, theta, cancellation);
		if (oiMiss != null) return $"no {root} {expiry:yyyy-MM-dd} open interest for {instant:yyyy-MM-dd} ({oiMiss})";
		foreach (var (sym, book) in expiryBooks) chainBooks[sym] = book;
		foreach (var (sym, o) in expiryOi) oi[sym] = o;
		return null;
	}

	/// <summary>The fill day's quote store (exact-minute books only: a boundary without one falls to the other boundary, never to an older
	/// minute), or null when quotes.db doesn't exist.</summary>
	private static QuoteStoreCache? OpenStore(DateTime fillEt)
	{
		var dbPath = Program.ResolvePath("data/quotes.db");
		return File.Exists(dbPath) ? new QuoteStoreCache(dbPath, maxStaleMinutes: 0, since: fillEt.Date, until: fillEt.Date) : null;
	}

	/// <summary>One expiry's stored books at <paramref name="instant"/> within the chain band, or null with why when the store has none.</summary>
	private static (Dictionary<string, QuoteStoreCache.QuoteAt>? Books, string? Miss) StoreChain(QuoteStoreCache store, string root, DateTime expiry, DateTime instant, decimal spot)
	{
		var books = new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase);
		foreach (var sym in store.ContractsOn(root, expiry, instant.Date, spot * (1m - ChainBand), spot * (1m + ChainBand)))
			if (store.NbboAt(sym, instant - QuoteStoreCache.BookLag) is { } book) books[sym] = book;
		return books.Count > 0 ? (books, null) : (null, "the quote store has no books for that expiry at that minute");
	}

	/// <summary>One expiry's ThetaData books at <paramref name="instant"/> within the chain band (one expiry-wide request), or null with why.</summary>
	private static async Task<(Dictionary<string, QuoteStoreCache.QuoteAt>? Books, string? Miss)> ThetaChainAsync(ThetaFallback theta, string root, DateTime expiry, DateTime instant, decimal spot, CancellationToken cancellation)
	{
		var (all, error) = await theta.ExpiryBooksAtAsync(root, expiry, instant, cancellation);
		var books = all.Where(kv => ParsingHelpers.ParseOptionSymbol(kv.Key) is { } p && Math.Abs(p.Strike - spot) <= spot * ChainBand).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
		return books.Count > 0 ? (books, null) : (null, $"ThetaData: {error ?? "no books for that expiry at that instant"}");
	}

	/// <summary>Every leg's stored book at the first boundary that has one for all of them, or the reason none does.</summary>
	private static (Dictionary<string, QuoteStoreCache.QuoteAt>? Books, DateTime Instant, string Source, string? Miss) StoreBooks(QuoteStoreCache? store, IReadOnlyCollection<string> symbols, DateTime fillEt, DateTime[] boundaries)
	{
		const string source = "the nearest stored minute book";
		if (store == null) return (null, default, source, $"not found ({Program.ResolvePath("data/quotes.db")})");
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
		var todayEt = MarketTime.TodayEt();
		if (sessionEt.Date >= todayEt || IntradayTapeBackfill.AuditIntraday(ticker, sessionEt.Date, sessionEt.Date.AddDays(1)).Complete.Contains(sessionEt.Date)) return;
		await IntradayTapeBackfill.BackfillAsync(ticker, MarketTime.EtToUtc(sessionEt.Date.AddHours(12)), DateTime.UtcNow, cancellation, latestNy: sessionEt.Date);
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
