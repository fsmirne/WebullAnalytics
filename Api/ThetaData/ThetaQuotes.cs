using WebullAnalytics.AI.Backtest;

namespace WebullAnalytics.Api.ThetaData;

/// <summary>ThetaData NBBO rows -> the quote store's book shape, by the store's own encoding rule
/// (<see cref="QuoteStoreSchema.EncodeQuote"/>): each side rounded to ten-thousandths, a side the vendor didn't disseminate
/// (NaN) stored as 0, and a row with neither side (or a negative one) is no book at all. A book fetched live is then identical to the one the
/// backfill would have stored for the same instant.</summary>
internal static class ThetaQuotes
{
	/// <summary>The book in an option quote table's first row (an at-time or snapshot response for one contract), or null when the
	/// table is empty or neither side printed.</summary>
	internal static QuoteStoreCache.QuoteAt? FirstBook(ThetaTable table)
	{
		if (table.RowCount == 0) return null;
		var (bid, ask) = (QuoteStoreSchema.Tick(table.Price(0, table.Column("bid"))), QuoteStoreSchema.Tick(table.Price(0, table.Column("ask"))));
		if ((bid == null && ask == null) || bid < 0 || ask < 0) return null;
		return new QuoteStoreCache.QuoteAt((bid ?? 0) / 10000m, (ask ?? 0) / 10000m, (int)table.Number(0, table.Column("bid_size")), (int)table.Number(0, table.Column("ask_size")), AgeMinutes: 0);
	}
}
