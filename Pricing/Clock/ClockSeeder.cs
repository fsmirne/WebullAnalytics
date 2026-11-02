using Microsoft.Data.Sqlite;
using WebullAnalytics.Api.ThetaData;
using WebullAnalytics.Data.Backfill;

namespace WebullAnalytics.Pricing.Clock;

/// <summary>Fills a root's <see cref="ClockStore"/> for completed sessions it lacks: from quotes.db when it holds a usable book for the
/// day (read-only; a seed source only — nothing live ever reads quotes.db for the clock), else from ThetaData (option_at_time_quote,
/// one request per expiration per instant). Half-day closes are skipped (a 16:00 book after a 13:00 close is stale).</summary>
internal sealed class ClockSeeder : IDisposable
{
	/// <summary>quotes.db rows are start-of-minute labeled, so the books at 16:00 and 09:46 are its 15:59 and 09:45 rows.</summary>
	private const int QuotesDbCloseLabel = 15 * 3600 + 59 * 60, QuotesDbOpenLabel = 9 * 3600 + 45 * 60;

	/// <summary>A quotes.db day is used only when each book has at least this many expiries two or more days out (quotes.db holds only
	/// same-day expiries for some roots, which can't calibrate an overnight).</summary>
	private const int MinUsableExpiries = 2;

	/// <summary>Concurrent ThetaData requests — the backfill's default; the plan rejects more with ResourceExhausted.</summary>
	private const int ThetaConcurrency = 2;

	private readonly string? _quotesDbPath;
	private ThetaDataClient? _theta;

	internal ClockSeeder(string? quotesDbPath) => _quotesDbPath = quotesDbPath != null && File.Exists(quotesDbPath) ? quotesDbPath : null;

	internal sealed record SeedResult(int FromQuotesDb, int FromThetaData, int StillMissing, string? ThetaError);

	/// <summary>Seeds every trading day in [<paramref name="from"/>, <paramref name="to"/>] (capped at yesterday) missing from the store.</summary>
	internal async Task<SeedResult> SeedAsync(string root, DateOnly from, DateOnly to, Action<string> progress, CancellationToken cancellation)
	{
		var yesterday = DateOnly.FromDateTime(MarketTime.TodayEt()).AddDays(-1);
		if (to > yesterday) to = yesterday;
		var missing = TradingDays(from, to).Where(d => !ClockStore.Has(root, d)).ToList();
		if (missing.Count == 0) return new SeedResult(0, 0, 0, null);

		var fromDb = 0;
		var stillMissing = new List<DateOnly>();
		foreach (var date in missing)
		{
			if (_quotesDbPath != null && ReadQuotesDbDay(root, date) is { } day && Usable(day))
			{
				ClockStore.Save(root, day);
				fromDb++;
			}
			else stillMissing.Add(date);
		}
		if (fromDb > 0) progress($"{root}: {fromDb} day(s) from quotes.db");
		if (stillMissing.Count == 0) return new SeedResult(fromDb, 0, 0, null);

		if (!ThetaDataClient.HasCredentials(ThetaDataClient.DefaultCredsPath()))
			return new SeedResult(fromDb, 0, stillMissing.Count, "no ThetaData login configured");
		try
		{
			_theta ??= await ThetaDataClient.ConnectAsync(ThetaDataClient.DefaultCredsPath(), maxConcurrent: ThetaConcurrency, stallTimeout: TimeSpan.FromMinutes(3), cancellation);
			var days = await ReadThetaDaysAsync(_theta, root, stillMissing, progress, cancellation);
			var saved = 0;
			foreach (var day in days.Values.Where(d => d.Close.Count > 0 || d.Open.Count > 0))
			{
				ClockStore.Save(root, day);
				saved++;
			}
			return new SeedResult(fromDb, saved, stillMissing.Count - saved, null);
		}
		catch (Exception ex) when (ex is ThetaSessionException or ThetaStallException or Grpc.Core.RpcException)
		{
			return new SeedResult(fromDb, 0, stillMissing.Count, ex.Message);
		}
	}

	public void Dispose() => _theta?.Dispose();

	private static IEnumerable<DateOnly> TradingDays(DateOnly from, DateOnly to)
	{
		for (var d = from; d <= to; d = d.AddDays(1))
			if (MarketCalendar.IsOpen(d.ToDateTime(TimeOnly.MinValue))) yield return d;
	}

	private static bool Usable(ClockDay day) =>
		day.Close.Count(b => b.Expiry.DayNumber - day.Date.DayNumber >= 2) >= MinUsableExpiries && day.Open.Count(b => b.Expiry.DayNumber - day.Date.DayNumber >= 2) >= MinUsableExpiries;

	private static bool IsHalfDay(DateOnly date) => MarketCalendar.IsEarlyClose(date.ToDateTime(TimeOnly.MinValue));

	// --- quotes.db ---

	private ClockDay? ReadQuotesDbDay(string root, DateOnly date)
	{
		using var conn = new SqliteConnection($"Data Source={_quotesDbPath};Mode=ReadOnly");
		conn.Open();
		var close = IsHalfDay(date) ? [] : ReadQuotesDbBooks(conn, root, date, QuotesDbCloseLabel);
		var open = ReadQuotesDbBooks(conn, root, date, QuotesDbOpenLabel);
		return close.Count == 0 && open.Count == 0 ? null : new ClockDay(date, close, open);
	}

	/// <summary>Every expiry book at one minute label. Candidate expiries are the calendar days in [date, date + <see cref="ClockStore.MaxDte"/>]
	/// (a primary-key probe each); a day without that expiry just returns no rows.</summary>
	private static List<ClockExpiryBook> ReadQuotesDbBooks(SqliteConnection conn, string root, DateOnly date, int timeSec)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT strike_milli, right, bid, ask FROM quotes WHERE root=$r AND expiry=$e AND date=$d AND time_sec=$t";
		var (pr, pe, pd, pt) = (cmd.Parameters.Add("$r", SqliteType.Text), cmd.Parameters.Add("$e", SqliteType.Integer), cmd.Parameters.Add("$d", SqliteType.Integer), cmd.Parameters.Add("$t", SqliteType.Integer));
		(pr.Value, pd.Value, pt.Value) = (root.ToUpperInvariant(), Ymd(date), timeSec);
		var books = new List<ClockExpiryBook>();
		for (var expiry = date; expiry.DayNumber - date.DayNumber <= ClockStore.MaxDte; expiry = expiry.AddDays(1))
		{
			if (!MarketCalendar.IsOpen(expiry.ToDateTime(TimeOnly.MinValue))) continue;
			pe.Value = Ymd(expiry);
			var quotes = new List<(decimal, bool, decimal, decimal)>();
			using (var reader = cmd.ExecuteReader())
				while (reader.Read())
					quotes.Add((reader.GetInt64(0) / 1000m, reader.GetString(1) == "C", reader.GetInt64(2) / 10000m, reader.GetInt64(3) / 10000m));
			if (quotes.Count > 0 && ClockStore.BuildExpiryBook(expiry, quotes) is { } book) books.Add(book);
		}
		return books;
	}

	private static int Ymd(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;

	// --- ThetaData ---

	/// <summary>The close and 09:46 books for <paramref name="dates"/>, one at-time request per expiration per instant covering every
	/// wanted date of that expiration's window; each response is cut to per-day banded books as it arrives.</summary>
	private static async Task<Dictionary<DateOnly, ClockDay>> ReadThetaDaysAsync(ThetaDataClient client, string root, IReadOnlyList<DateOnly> dates, Action<string> progress, CancellationToken cancellation)
	{
		var wanted = dates.ToHashSet();
		var (first, last) = (dates.Min(), dates.Max());
		var expTable = await ThetaRetry.CollectAsync(token => client.OptionListExpirations(root, token), cancellation);
		var expCol = expTable.Column("expiration");
		var expirations = Enumerable.Range(0, expTable.RowCount).Select(r => ThetaCells.Date(expTable, r, expCol))
			.Where(e => e >= first && e.DayNumber - last.DayNumber <= ClockStore.MaxDte).Distinct().OrderBy(e => e).ToList();

		var close = new Dictionary<DateOnly, List<ClockExpiryBook>>();
		var open = new Dictionary<DateOnly, List<ClockExpiryBook>>();
		var done = 0;
		await Parallel.ForEachAsync(expirations, new ParallelOptions { MaxDegreeOfParallelism = ThetaConcurrency, CancellationToken = cancellation }, async (expiry, token) =>
		{
			var start = first.DayNumber > expiry.DayNumber - ClockStore.MaxDte ? first : DateOnly.FromDayNumber(expiry.DayNumber - ClockStore.MaxDte);
			var end = last < expiry ? last : expiry;
			if (end < start) return;
			var contract = new OptionContract(root.ToUpperInvariant(), expiry);
			var closeTable = await ThetaRetry.CollectAsync(t => client.OptionAtTimeQuote(contract, start, end, ClockStore.CloseTime, t), token);
			var openTable = await ThetaRetry.CollectAsync(t => client.OptionAtTimeQuote(contract, start, end, ClockStore.OpenTime, t), token);
			var closeBooks = BooksByDate(closeTable, expiry, wanted);
			var openBooks = BooksByDate(openTable, expiry, wanted);
			lock (close)
			{
				foreach (var (date, book) in closeBooks) if (!IsHalfDay(date)) (close.TryGetValue(date, out var l) ? l : close[date] = []).Add(book);
				foreach (var (date, book) in openBooks) (open.TryGetValue(date, out var l) ? l : open[date] = []).Add(book);
				if (++done % 25 == 0) progress($"{root}: {done}/{expirations.Count} expirations from ThetaData");
			}
		});
		return dates.ToDictionary(d => d, d => new ClockDay(d, close.GetValueOrDefault(d) ?? [], open.GetValueOrDefault(d) ?? []));
	}

	/// <summary>Cuts one expiration's at-time table (all strikes, both rights, one row per contract per day) into per-day banded books.</summary>
	private static IEnumerable<(DateOnly Date, ClockExpiryBook Book)> BooksByDate(ThetaTable table, DateOnly expiry, IReadOnlySet<DateOnly> wanted)
	{
		if (table.RowCount == 0) yield break;
		var (strikeCol, rightCol, timeCol, bidCol, askCol) = (table.Column("strike"), table.Column("right"), table.Column("timestamp"), table.Column("bid"), table.Column("ask"));
		var byDate = new Dictionary<DateOnly, List<(decimal, bool, decimal, decimal)>>();
		for (var row = 0; row < table.RowCount; row++)
		{
			var date = DateOnly.FromDateTime(table.Timestamp(row, timeCol).DateTime);
			if (!wanted.Contains(date)) continue;
			var (bid, ask) = (table.Double(row, bidCol), table.Double(row, askCol));
			if (double.IsNaN(bid) || double.IsNaN(ask) || bid <= 0 || ask <= 0) continue;
			(byDate.TryGetValue(date, out var l) ? l : byDate[date] = []).Add(((decimal)table.Double(row, strikeCol), ThetaCells.Right(table, row, rightCol) == 'C', Math.Round((decimal)bid, 4), Math.Round((decimal)ask, 4)));
		}
		foreach (var (date, quotes) in byDate)
			if (ClockStore.BuildExpiryBook(expiry, quotes) is { } book) yield return (date, book);
	}
}
