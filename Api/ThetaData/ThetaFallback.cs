using Grpc.Core;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Data.Backfill;

namespace WebullAnalytics.Api.ThetaData;

/// <summary>On-demand ThetaData for analysis paths whose local stores lack what they need: a past instant's option books, a day's open
/// interest, a day's underlying close. ThetaData is optional — <see cref="TryCreate"/> returns null when no login is configured, and the
/// caller keeps its store-only behavior. One session per instance, opened on the first call; a failed sign-in is remembered so later calls
/// report it without signing in again. Every method returns what it found plus, when ThetaData itself failed (session, stall, server
/// error), why — a contract with no data is simply absent, not an error. Dispose to release the session.</summary>
internal sealed class ThetaFallback : IDisposable
{
	private ThetaDataClient? _client;
	private string? _unavailable;

	private ThetaFallback() { }

	/// <summary>A fallback when a ThetaData login is configured, else null.</summary>
	internal static ThetaFallback? TryCreate() => ThetaDataClient.HasCredentials(ThetaDataClient.DefaultCredsPath()) ? new ThetaFallback() : null;

	/// <summary>Each symbol's book at <paramref name="instantEt"/> (option_at_time_quote, a minute boundary), encoded by the quote store's rule
	/// (<see cref="ThetaQuotes.FirstBook"/>) — the same book the backfill stores for that boundary. Symbols without one are absent.</summary>
	internal Task<(Dictionary<string, QuoteStoreCache.QuoteAt> Books, string? Error)> BooksAtAsync(IReadOnlyCollection<string> symbols, DateTime instantEt, CancellationToken cancellation) =>
		RunAsync(async client =>
		{
			var (date, time) = (DateOnly.FromDateTime(instantEt), TimeOnly.FromDateTime(instantEt));
			var tables = await Task.WhenAll(symbols.Select(symbol => ThetaRetry.CollectAsync(token => client.OptionAtTimeQuote(OptionContract.FromOcc(symbol)!, date, date, time, token), cancellation)));
			var books = new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase);
			foreach (var (symbol, table) in symbols.Zip(tables))
				if (ThetaQuotes.FirstBook(table) is { } book) books[symbol] = book;
			return books;
		}, new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase), cancellation);

	/// <summary>Each symbol's open interest reported on <paramref name="date"/> (published that morning for the prior close — the report-date
	/// keying of the OI snapshot store, see <see cref="ChainSnapshotOiCache"/>). Symbols without a report are absent.</summary>
	internal Task<(Dictionary<string, long> OpenInterest, string? Error)> OpenInterestAsync(IReadOnlyCollection<string> symbols, DateTime date, CancellationToken cancellation) =>
		RunAsync(async client =>
		{
			var day = DateOnly.FromDateTime(date);
			var tables = await Task.WhenAll(symbols.Select(symbol => ThetaRetry.CollectAsync(token => client.OptionHistoryOpenInterest(OptionContract.FromOcc(symbol)!, day, day, maxDte: null, token), cancellation)));
			var openInterest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			foreach (var (symbol, table) in symbols.Zip(tables))
			{
				if (table.RowCount == 0) continue;
				var oi = table.Double(0, ThetaCells.Pick(table, "open_interest", "oi"));
				if (!double.IsNaN(oi)) openInterest[symbol] = (long)oi;
			}
			return openInterest;
		}, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), cancellation);

	/// <summary>Every contract's book in one expiry at <paramref name="instantEt"/> — one wildcard (all strikes, both rights) at-time request,
	/// each row encoded like <see cref="BooksAtAsync"/>. Keyed by OCC symbol; contracts without a book are absent.</summary>
	internal Task<(Dictionary<string, QuoteStoreCache.QuoteAt> Books, string? Error)> ExpiryBooksAtAsync(string root, DateTime expiry, DateTime instantEt, CancellationToken cancellation) =>
		RunAsync(async client =>
		{
			var (date, time) = (DateOnly.FromDateTime(instantEt), TimeOnly.FromDateTime(instantEt));
			var table = await ThetaRetry.CollectAsync(token => client.OptionAtTimeQuote(new OptionContract(root, DateOnly.FromDateTime(expiry)), date, date, time, token), cancellation);
			var books = new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase);
			if (table.RowCount == 0) return books;
			var (strikeCol, rightCol) = (table.Column("strike"), ThetaCells.Pick(table, "right", "type"));
			for (var row = 0; row < table.RowCount; row++)
				if (ThetaQuotes.BookAt(table, row) is { } book) books[Occ(root, expiry, table.Double(row, strikeCol), ThetaCells.Right(table, row, rightCol))] = book;
			return books;
		}, new Dictionary<string, QuoteStoreCache.QuoteAt>(StringComparer.OrdinalIgnoreCase), cancellation);

	/// <summary>Every contract's open interest in one expiry as reported on <paramref name="date"/> — one wildcard request. Keyed by OCC
	/// symbol; contracts without a report are absent.</summary>
	internal Task<(Dictionary<string, long> OpenInterest, string? Error)> ExpiryOpenInterestAsync(string root, DateTime expiry, DateTime date, CancellationToken cancellation) =>
		RunAsync(async client =>
		{
			var day = DateOnly.FromDateTime(date);
			var table = await ThetaRetry.CollectAsync(token => client.OptionHistoryOpenInterest(new OptionContract(root, DateOnly.FromDateTime(expiry)), day, day, maxDte: null, token), cancellation);
			var openInterest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			if (table.RowCount == 0) return openInterest;
			var (strikeCol, rightCol, oiCol) = (table.Column("strike"), ThetaCells.Pick(table, "right", "type"), ThetaCells.Pick(table, "open_interest", "oi"));
			for (var row = 0; row < table.RowCount; row++)
			{
				var oi = table.Double(row, oiCol);
				if (!double.IsNaN(oi)) openInterest[Occ(root, expiry, table.Double(row, strikeCol), ThetaCells.Right(table, row, rightCol))] = (long)oi;
			}
			return openInterest;
		}, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), cancellation);

	/// <summary>The app's OCC symbol for one row of an expiry-wide table (the backfill's own encoding).</summary>
	private static string Occ(string root, DateTime expiry, double strike, char right) => $"{root.ToUpperInvariant()}{expiry:yyMMdd}{right}{QuoteStoreSchema.StrikeMilli(strike):D8}";

	/// <summary><paramref name="ticker"/>'s daily close on <paramref name="date"/> (equity EOD, else index EOD — see
	/// <see cref="UnderlyingCloses.FetchEodAsync"/>), null when neither feed has the day.</summary>
	internal async Task<(decimal? Close, string? Error)> DailyCloseAsync(string ticker, DateTime date, CancellationToken cancellation)
	{
		var day = DateOnly.FromDateTime(date);
		var ((closes, feedError), error) = await RunAsync(client => UnderlyingCloses.FetchEodAsync(client, ticker, day, day, cancellation), (new Dictionary<DateOnly, double>(), (string?)null), cancellation);
		return closes.TryGetValue(day, out var close) && !double.IsNaN(close) ? ((decimal)close, null) : (null, error ?? feedError);
	}

	/// <summary>Runs <paramref name="body"/> on the session (signing in first if needed), or returns <paramref name="empty"/> with the reason
	/// when ThetaData can't be reached or failed mid-call.</summary>
	private async Task<(T Result, string? Error)> RunAsync<T>(Func<ThetaDataClient, Task<T>> body, T empty, CancellationToken cancellation)
	{
		if (_unavailable != null) return (empty, _unavailable);
		try
		{
			_client ??= await ThetaDataClient.ConnectAsync(ThetaDataClient.DefaultCredsPath(), maxConcurrent: 2, stallTimeout: TimeSpan.FromMinutes(1), cancellation);
		}
		catch (Exception ex) when (ex is ThetaSessionException or HttpRequestException)
		{
			_unavailable = ex.Message;
			return (empty, _unavailable);
		}
		try
		{
			return (await body(_client), null);
		}
		catch (Exception ex) when (ex is ThetaSessionException or ThetaStallException) { return (empty, ex.Message); }
		catch (RpcException ex) { return (empty, $"{ex.StatusCode}: {ex.Status.Detail}"); }
	}

	public void Dispose() => _client?.Dispose();
}
