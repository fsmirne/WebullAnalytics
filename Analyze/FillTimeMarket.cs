using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
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
/// pre-open). Every input is taken at that one instant. Nothing is approximated beyond that minute resolution:
/// a leg the store doesn't cover is an error naming why, never a substitute price.
/// </summary>
internal static class FillTimeMarket
{
	/// <param name="Instant">The minute boundary the market is read at (ET) — report its offset from the fill.</param>
	internal sealed record Snapshot(DateTime Instant, IReadOnlyDictionary<string, OptionContractQuote> Quotes, decimal Spot, decimal SessionHigh, decimal SessionLow);

	private static readonly TimeZoneInfo EtZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	public static async Task<(Snapshot? Market, string? Error)> LoadAsync(string ticker, IReadOnlyCollection<string> symbols, DateTime fillEt, IntradayBarCache intradayCache, CancellationToken cancellation)
	{
		var dbPath = Program.ResolvePath("data/quotes.db");
		if (!File.Exists(dbPath)) return (null, $"quote store not found ({dbPath})");

		// maxStaleMinutes 0: only the exact minute's book — a boundary without one falls to the other boundary,
		// never to an older minute.
		var store = new QuoteStoreCache(dbPath, maxStaleMinutes: 0, since: fillEt.Date, until: fillEt.Date);
		var before = fillEt.Date + TimeSpan.FromMinutes(Math.Floor(fillEt.TimeOfDay.TotalMinutes));
		var after = before.AddMinutes(1);
		var boundaries = fillEt - before <= after - fillEt ? new[] { before, after } : new[] { after, before };

		var quotes = (IReadOnlyDictionary<string, OptionContractQuote>?)null;
		var instant = default(DateTime);
		foreach (var boundary in boundaries)
		{
			quotes = BooksAt(store, symbols, boundary);
			if (quotes != null) { instant = boundary; break; }
		}
		if (quotes == null)
			return (null, $"no stored NBBO at {before:HH:mm} or {after:HH:mm} for every leg ({string.Join(", ", symbols)}): {MissingReason(store, symbols, fillEt)}");

		var oi = new ChainSnapshotOiCache().ForDay(ticker, fillEt.Date);
		quotes = quotes.ToDictionary(kv => kv.Key, kv => oi.TryGetValue(kv.Key, out var o) ? kv.Value with { OpenInterest = o.Oi } : kv.Value, StringComparer.OrdinalIgnoreCase);

		// Bars are start-of-bar labeled: those starting before the instant are complete at it, and the last one's
		// close is the price at the instant.
		var openUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fillEt.Date + OptionMath.MarketOpen, EtZone));
		var instantUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(instant, EtZone));
		var bars = (await intradayCache.GetBarsAsync(ticker, openUtc, instantUtc, BarInterval.M1, includeExtended: false, cancellation)).Where(b => b.Timestamp < instantUtc).ToList();
		if (bars.Count == 0) return (null, $"no {ticker} 1-minute bars for {fillEt:yyyy-MM-dd} up to {instant:HH:mm} ET");

		return (new Snapshot(instant, quotes, bars[^1].Close, bars.Max(b => b.High), bars.Min(b => b.Low)), null);
	}

	/// <summary>Every leg's book at minute boundary <paramref name="boundary"/> — the store row labeled one
	/// minute earlier — or null when any leg has none there.</summary>
	private static Dictionary<string, OptionContractQuote>? BooksAt(QuoteStoreCache store, IReadOnlyCollection<string> symbols, DateTime boundary)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var symbol in symbols)
		{
			if (store.NbboAt(symbol, boundary - QuoteStoreCache.BookLag) is not { } nbbo) return null;
			quotes[symbol] = new OptionContractQuote(symbol, LastPrice: nbbo.Mid, Bid: nbbo.Bid, Ask: nbbo.Ask, Change: null, PercentChange: null, Volume: null, OpenInterest: null, ImpliedVolatility: null, HistoricalVolatility: null, BidSize: nbbo.BidSize, AskSize: nbbo.AskSize);
		}
		return quotes;
	}

	/// <summary>Why neither boundary has a book: the session isn't stored yet, the store has a gap there, or the
	/// contracts aren't backfilled at all.</summary>
	private static string MissingReason(QuoteStoreCache store, IReadOnlyCollection<string> symbols, DateTime fillEt)
	{
		if (symbols.All(sym => new[] { 12, 15 }.Any(h => store.NbboAt(sym, fillEt.Date.AddHours(h)) != null)))
			return "the store has a gap in these contracts' quotes at the fill";
		return fillEt.Date >= DateTime.Today
			? "today's quotes reach quotes.db only with the evening ThetaData backfill — re-run after it lands"
			: "the backfill doesn't cover these contracts (root, expiry or strike band)";
	}
}
