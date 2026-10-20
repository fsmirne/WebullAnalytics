using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data.Backfill;

/// <summary>Which minute dataset a pull fills: the NBBO book (<c>quotes</c>) or the trade tape (<c>ohlcv</c>). They share every pull
/// mechanic but certify different completeness, so each has its own data, staging, seal and proven-hole tables.</summary>
internal sealed record MinuteDataset(string Name, string Table, string StagingTable, string SealedTable, string SealedSql, string HolesTable, string HolesSql)
{
	internal static readonly MinuteDataset Quotes = new("quotes", "quotes", "quotes_staging", "sealed", QuoteStoreSchema.SealedSql, "known_holes", QuoteStoreSchema.KnownHolesSql);
	internal static readonly MinuteDataset Ohlcv = new("ohlcv", "ohlcv", "ohlcv_staging", "ohlcv_sealed", QuoteStoreSchema.OhlcvSealedSql, "ohlcv_known_holes", QuoteStoreSchema.OhlcvKnownHolesSql);
}

/// <param name="Tickers">Roots with their per-root DTE window.</param>
/// <param name="ChunkDays">Calendar days per request window — small windows keep a request from stalling at the server and a true stall cheap to retry.</param>
/// <param name="Band">Strike band as a fraction of each day's spot close.</param>
/// <param name="Supplement">Additive patch mode (quotes only): pull sealed expiries too, INSERT OR IGNORE straight into the live table, never delete or (re)seal.</param>
/// <param name="StrikeRange">ohlcv only: server-side ATM strike window (listed strikes either side) trimming the dense zero-volume grid before transfer; null = full chain.</param>
internal sealed record MinuteBackfillOptions(IReadOnlyList<(string Ticker, int Dte)> Tickers, DateOnly Start, DateOnly End, int ChunkDays, int Concurrency, double Band, bool Supplement, int? StrikeRange);

/// <summary>The minute pull into <c>data/quotes.db</c> — a port of scripts/backfill_thetadata.py's --quotes / --ohlcv modes, rule for
/// rule. The unit of work is one expiration (ThetaData's minute endpoints take a single expiration per multi-day request) across its
/// DTE window, pulled in small windows into the staging table and promoted into the live table in one transaction once complete, so
/// an abort never leaves an expiry half-rebuilt. Resume is seal-driven: sealed (final) expirations are skipped, unsealed ones —
/// including live and future ones near the window end — are re-pulled every run so the frontier keeps filling. A seal is a
/// completeness certificate, not just "the pull didn't crash": an expiry with an interior trading day missing (or, for quotes, a
/// frontier session missing in-band strikes earlier sessions quoted) fails and stays unsealed unless the gap is recorded as proven
/// in the dataset's known-holes table. Two passes per run: the second retries only what failed in the first.</summary>
internal sealed class MinuteBackfill(ThetaDataClient client, SqliteConnection db, MinuteDataset dataset, MinuteBackfillOptions options, BackfillLog log)
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	// SQLite is single-writer and the connection isn't thread-safe; the network pulls overlap, the store reads and writes queue here.
	private readonly SemaphoreSlim _dbGate = new(1, 1);
	private readonly UnderlyingCloses _closes = new(client, log);
	// Expirations completed earlier in this run: the retry pass skips them (frontier ones are never sealed, so the seal table alone
	// would re-pull them).
	private readonly ConcurrentDictionary<string, byte> _done = new();

	/// <summary>Pulls everything due. Returns the number of sealable expirations still unsealed after the retry pass (0 = certified complete).</summary>
	internal async Task<int> RunAsync(CancellationToken cancellation)
	{
		if (options.Supplement)
		{
			await PassAsync(cancellation);
			return 0;   // supplements seal nothing by design: the seal-driven retry pass is meaningless here
		}
		List<(string Ticker, DateOnly Expiration)> failed = [];
		for (var attempt = 1; attempt <= 2; attempt++)
		{
			await PassAsync(cancellation);
			failed = await UnsealedSealableAsync(cancellation);
			if (failed.Count == 0)
			{
				if (attempt == 2) log.Info("[retry-pass] clean — every previously-failed unit sealed");
				return 0;
			}
			if (attempt == 1) log.Info($"[retry-pass] {failed.Count} sealable unit(s) unsealed after pass 1 ({Names(failed)}) — retrying just those");
		}
		log.Info($"[FAILED] {failed.Count} sealable unit(s) STILL unsealed after the retry pass: {Names(failed)} — store INCOMPLETE for these; re-run to retry, or record proven vendor gaps in the {dataset.HolesTable} table");
		return failed.Count;
	}

	private static string Names(List<(string Ticker, DateOnly Expiration)> units) => string.Join(", ", units.Take(8).Select(u => $"{u.Ticker} {Iso(u.Expiration)}")) + (units.Count > 8 ? "..." : "");

	/// <summary>One pass over every due expiration, <see cref="MinuteBackfillOptions.Concurrency"/> in flight on the one session. A
	/// failed expiration is logged and left unsealed (the retry pass or a later run takes it); a lost session ends the run.</summary>
	private async Task PassAsync(CancellationToken cancellation)
	{
		var work = await BuildWorkAsync(cancellation);
		if (work.Count == 0) return;
		log.Info($"--- pull: {work.Count} expirations, {options.Concurrency} concurrent on one session ---");
		var numbered = work.Select((unit, i) => (Index: i + 1, unit.Ticker, unit.Expiration, unit.Dte));
		await Parallel.ForEachAsync(numbered, new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellation }, async (unit, token) =>
		{
			log.Info($"  exp {unit.Ticker} {Iso(unit.Expiration)}");
			try
			{
				var progress = $"{unit.Index}/{work.Count}: ";
				if (dataset == MinuteDataset.Ohlcv) await PullOhlcvAsync(unit.Ticker, unit.Expiration, unit.Dte, progress, token);
				else await PullQuotesAsync(unit.Ticker, unit.Expiration, unit.Dte, progress, token);
				if (!options.Supplement) await SealAsync(unit.Ticker, unit.Expiration, token);
				_done[$"{unit.Ticker}|{Iso(unit.Expiration)}"] = 0;
			}
			catch (Exception ex) when (ex is not (ThetaSessionException or OperationCanceledException))
			{
				log.Info($"  [error] {unit.Ticker} exp {Iso(unit.Expiration)}: {ex.GetType().Name}: {ex.Message}");
			}
		});
	}

	/// <summary>Every listed expiration in [start, end + DTE] per ticker that isn't sealed (unless supplementing) and wasn't completed
	/// earlier in this run.</summary>
	private async Task<List<(string Ticker, DateOnly Expiration, int Dte)>> BuildWorkAsync(CancellationToken cancellation)
	{
		var work = new List<(string, DateOnly, int)>();
		foreach (var (ticker, dte) in options.Tickers)
		{
			var sealedSet = await SealedAsync(ticker, cancellation);
			List<DateOnly> expirations;
			try { expirations = await ExpirationsAsync(ticker, cancellation); }
			catch (Exception ex) when (ex is not (ThetaSessionException or OperationCanceledException))
			{
				log.Info($"  [error] list expirations {ticker}: {ex.Message} — skipping ticker");
				continue;
			}
			var hi = options.End.AddDays(dte);
			var relevant = expirations.Where(e => e >= options.Start && e <= hi).Order().ToList();
			var sealedCount = relevant.Count(sealedSet.Contains);
			var todo = relevant.Where(e => (options.Supplement || !sealedSet.Contains(e)) && !_done.ContainsKey($"{ticker}|{Iso(e)}")).ToList();
			var doneThisRun = relevant.Count - sealedCount - todo.Count;
			log.Info($"=== {ticker} (dte={dte}): {relevant.Count} expirations, {sealedCount} sealed, {todo.Count} to pull{(doneThisRun > 0 ? $", {doneThisRun} done this run" : "")} ===");
			work.AddRange(todo.Select(e => (ticker, e, dte)));
		}
		return work;
	}

	/// <summary>Expirations that should be sealed after a pass (elapsed and fully in range) but aren't — this run's genuine failures.
	/// A listing failure here can't enumerate them; it is logged and treated as none, as the Python pull did.</summary>
	private async Task<List<(string Ticker, DateOnly Expiration)>> UnsealedSealableAsync(CancellationToken cancellation)
	{
		var failed = new List<(string, DateOnly)>();
		try
		{
			foreach (var (ticker, _) in options.Tickers)
			{
				var sealedSet = await SealedAsync(ticker, cancellation);
				failed.AddRange((await ExpirationsAsync(ticker, cancellation)).Where(e => e >= options.Start && e <= options.End && e < EtToday() && !sealedSet.Contains(e)).Select(e => (ticker, e)));
			}
		}
		catch (Exception ex) when (ex is not (ThetaSessionException or OperationCanceledException))
		{
			log.Info($"  [warn] post-pass seal verification failed ({ex.Message}) — cannot enumerate unsealed units this pass");
			return [];
		}
		return failed;
	}

	private async Task<List<DateOnly>> ExpirationsAsync(string ticker, CancellationToken cancellation)
	{
		var table = await ThetaRetry.CollectAsync(token => client.OptionListExpirations(ticker, token), cancellation);
		if (table.RowCount == 0) return [];
		var column = table.ColumnOrNull("expiration") ?? table.ColumnOrNull("date") ?? table.ColumnOrNull("expirations") ?? 0;
		return [.. Enumerable.Range(0, table.RowCount).Select(row => ThetaCells.Date(table, row, column))];
	}

	// ----- one expiration: quotes -----------------------------------------------------------------------------------------------------

	/// <summary>One window's kept quote rows plus what the seal gates need from it. <see cref="AnyRows"/> = the vendor returned
	/// anything at all (before the band filter).</summary>
	private sealed record QuoteWindow(bool AnyRows, List<QuoteRow> Rows, HashSet<DateOnly> Days, Dictionary<DateOnly, HashSet<double>> StrikesByDay, HashSet<DateOnly> NoSpotDays);

	private async Task PullQuotesAsync(string ticker, DateOnly expiration, int dte, string progress, CancellationToken cancellation)
	{
		var (start, end) = (Max(options.Start, expiration.AddDays(-dte)), Min(options.End, expiration));
		if (start > end) return;

		var spots = await _closes.GetAsync(ticker, start, end, cancellation);
		var contract = new OptionContract(ticker, expiration);
		var expInt = QuoteStoreSchema.YmdInt(expiration);
		var (totalRows, sawQuotes, firstWrite) = (0L, false, true);
		var days = new HashSet<DateOnly>();
		var noSpotDays = new HashSet<DateOnly>();
		var strikesByDay = new Dictionary<DateOnly, HashSet<double>>();
		foreach (var (from, to) in Windows(start, end))
		{
			var window = await FetchQuoteWindowAsync(contract, from, to, ticker, expInt, spots, cancellation);
			if (!window.AnyRows)
			{
				// Pre-listing windows are legitimately empty. An empty window AFTER data began, over a real session, is a hole
				// candidate (a transient blip used to be accepted silently and sealed): retry once, then warn loudly. A window with
				// no session in it (a weekend/holiday tail) is expected-empty — judged against the same spot calendar the seal gate uses.
				if (!sawQuotes || !spots.Keys.Any(d => d >= from && d <= to)) continue;
				window = await FetchQuoteWindowAsync(contract, from, to, ticker, expInt, spots, cancellation);
				if (!window.AnyRows)
				{
					log.Info($"    [warn] {ticker} {Iso(expiration)}: chunk {Iso(from)}..{Iso(to)} EMPTY after data began — possible internal hole (transient server blip?); sessions left unfilled, audit/H7 will flag");
					continue;
				}
			}
			sawQuotes = true;
			if (spots.Count == 0) continue;   // quotes but no spot to band-filter — raised after the loop
			noSpotDays.UnionWith(window.NoSpotDays);
			if (window.Rows.Count == 0) continue;
			await WriteWindowAsync(ticker, expInt, window.Rows, firstWrite, cancellation);
			firstWrite = false;
			totalRows += window.Rows.Count;
			days.UnionWith(window.Days);
			foreach (var (day, strikes) in window.StrikesByDay)
				(strikesByDay.TryGetValue(day, out var set) ? set : strikesByDay[day] = []).UnionWith(strikes);
		}
		if (totalRows > 0 && !options.Supplement)
			await PromoteAsync(ticker, expInt, cancellation);

		// Quotes but no spot -> can't band-filter: treat as transient (fail -> retry). No quotes at all -> a non-trading window or an
		// expiry with no in-band contracts: succeed empty so it seals instead of being re-attempted forever.
		if (sawQuotes && spots.Count == 0)
			throw new InvalidOperationException($"quotes present but no underlying spot for {ticker} {Iso(start)}..{Iso(end)} (transient underlying feed?)");
		if (noSpotDays.Count > 0)
			log.Info($"    [warn] {ticker} {Iso(expiration)}: DROPPED {noSpotDays.Count} quote day(s) with no underlying close ({Iso(noSpotDays.Min())}..{Iso(noSpotDays.Max())}) — spot series ends {(spots.Count > 0 ? Iso(spots.Keys.Max()) : "never")}; those sessions are missing from the store until a re-pull with spot coverage");
		if (days.Count > 0 && !options.Supplement)
		{
			var known = await KnownHolesAsync(ticker, expInt, cancellation);
			RequireNoMissingDays(ticker, expiration, end, spots, days, known, "", "interior trading day(s) absent");
			await RequireFullFrontierLaddersAsync(ticker, expiration, expInt, spots, strikesByDay, known, cancellation);
		}
		if (totalRows > 0)
			log.Info($"    {progress}exp {ticker} {Iso(expiration)} rows={totalRows} days={days.Count} spot_days={spots.Count}");
	}

	/// <summary>One window's minute NBBO, banded and encoded (retried from scratch on a transient failure). ThetaData stamps each
	/// minute at its END; the store labels minutes by their START (09:30 = the first RTH minute), so every stamp is shifted back 60 s
	/// at ingest — the same normalization WebullChartsClient.WebullBarShift applies to Webull bars — and the store never shifts on read.</summary>
	private Task<QuoteWindow> FetchQuoteWindowAsync(OptionContract contract, DateOnly from, DateOnly to, string ticker, int expInt, Dictionary<DateOnly, double> spots, CancellationToken cancellation) =>
		ThetaRetry.RunAsync(async token =>
		{
			var window = new QuoteWindow(false, [], [], [], []);
			var anyRows = false;
			await foreach (var table in client.OptionHistoryQuote(contract, "1m", from, to, token))
			{
				if (table.RowCount == 0) continue;
				anyRows = true;
				var (timeCol, strikeCol, rightCol) = (ThetaCells.Pick(table, "ms_of_day", "time", "timestamp", "datetime"), table.Column("strike"), ThetaCells.Pick(table, "right", "type"));
				var (bidCol, askCol, bidSizeCol, askSizeCol) = (table.Column("bid"), table.Column("ask"), table.ColumnOrNull("bid_size"), table.ColumnOrNull("ask_size"));
				for (var row = 0; row < table.RowCount; row++)
				{
					var stamp = table.Timestamp(row, timeCol).AddSeconds(-60);
					var day = DateOnly.FromDateTime(stamp.DateTime);
					if (!spots.TryGetValue(day, out var spot) || double.IsNaN(spot)) { window.NoSpotDays.Add(day); continue; }
					var (strike, bid, ask) = (table.Double(row, strikeCol), table.Double(row, bidCol), table.Double(row, askCol));
					if (double.IsNaN(strike) || !(Math.Abs(strike / spot - 1.0) <= options.Band) || (double.IsNaN(bid) && double.IsNaN(ask))) continue;
					var right = ThetaCells.Right(table, row, rightCol);
					var timeSec = stamp.Hour * 3600 + stamp.Minute * 60 + stamp.Second;
					long Size(int? column) => column is int c ? ThetaCells.Count(table, row, c) : 0;
					if (QuoteStoreSchema.EncodeQuote(ticker, expInt, QuoteStoreSchema.YmdInt(day), timeSec, strike, right, bid, ask, Size(bidSizeCol), Size(askSizeCol)) is { } encoded)
						window.Rows.Add(encoded);
					window.Days.Add(day);
					(window.StrikesByDay.TryGetValue(day, out var strikes) ? strikes : window.StrikesByDay[day] = []).Add(Math.Round(strike, 3));
				}
			}
			return window with { AnyRows = anyRows };
		}, cancellation);

	/// <summary>Seal-time STRIKE-completeness gate: a session can be present yet missing near-money strikes the vendor served on
	/// neighboring sessions (SPY 07-29 expiry on 07-28 omitted 738P and neighbors while 07-27 had the full ladder, silently corrupting a
	/// backtest's window-end mark). A session is thin only if it lacks strikes an EARLIER session quoted inside the session's own band —
	/// comparing against earlier sessions only separates real vendor gaps from legitimate ladder growth (new $1 fill-ins listed after a
	/// sharp move, a weekly's coarse listing-day grid, expiry-day band drift into a sparser grid). Checked on the last few sessions,
	/// where the vendor's just-settled lag strikes. A clear shortfall (>= 5 strikes AND < 90% of the earlier-quoted in-band ladder)
	/// withholds the seal like a missing day; proven permanent gaps go in known_strike_holes.</summary>
	private async Task RequireFullFrontierLaddersAsync(string ticker, DateOnly expiration, int expInt, Dictionary<DateOnly, double> spots, Dictionary<DateOnly, HashSet<double>> strikesByDay, HashSet<DateOnly> knownDays, CancellationToken cancellation)
	{
		var allDays = strikesByDay.Keys.Order().ToList();
		var recent = allDays.TakeLast(6).ToList();
		if (recent.Count < 3) return;
		var certified = await KnownStrikeHolesAsync(ticker, expInt, cancellation);
		var thin = new List<(DateOnly Day, List<double> Missing)>();
		foreach (var day in recent)
		{
			if (knownDays.Contains(day) || !spots.TryGetValue(day, out var spot) || double.IsNaN(spot)) continue;
			var prior = allDays.Where(e => e < day).SelectMany(e => strikesByDay[e]).Where(k => Math.Abs(k / spot - 1.0) <= options.Band).ToHashSet();
			prior.ExceptWith(strikesByDay[day]);
			if (certified.TryGetValue(day, out var absent)) prior.ExceptWith(absent);
			var missing = prior.Order().ToList();
			if (missing.Count >= 5 && strikesByDay[day].Count < 0.90 * (strikesByDay[day].Count + missing.Count))
				thin.Add((day, missing));
		}
		if (thin.Count > 0)
		{
			var shown = string.Join(", ", thin.Select(t => $"{Iso(t.Day)}(has {strikesByDay[t.Day].Count}, lacks {t.Missing.Count}: {string.Join(", ", t.Missing.Take(4).Select(k => k.ToString(CultureInfo.InvariantCulture)))}{(t.Missing.Count > 4 ? "..." : "")})"));
			throw new InvalidOperationException($"THIN-LADDER {ticker} {Iso(expiration)}: {thin.Count} frontier session(s) missing in-band strikes earlier sessions quoted ({shown}) — seal withheld; a transient partial vendor response heals on re-pull, a proven permanent gap belongs in the known_strike_holes table");
		}
	}

	// ----- one expiration: ohlcv ------------------------------------------------------------------------------------------------------

	private sealed record OhlcvWindow(bool AnyRows, List<OhlcvRow> Rows, HashSet<DateOnly> Days);

	/// <summary>Minute trade bars for one expiration: traded bars only, START-of-bar labels as served (validated 2026-08-18: the 09:30
	/// bar carries the opening trades), so no relabel. Completeness is interior-day only — sparse volume ladders are the norm, so
	/// there's deliberately no strike-ladder gate.</summary>
	private async Task PullOhlcvAsync(string ticker, DateOnly expiration, int dte, string progress, CancellationToken cancellation)
	{
		var (start, end) = (Max(options.Start, expiration.AddDays(-dte)), Min(options.End, expiration));
		if (start > end) return;

		var spots = await _closes.GetAsync(ticker, start, end, cancellation);
		var contract = new OptionContract(ticker, expiration);
		var expInt = QuoteStoreSchema.YmdInt(expiration);
		var (totalRows, sawBars, firstWrite) = (0L, false, true);
		var days = new HashSet<DateOnly>();
		foreach (var (from, to) in Windows(start, end))
		{
			var window = await FetchOhlcvWindowAsync(contract, from, to, ticker, expInt, spots, cancellation);
			if (!window.AnyRows)
			{
				if (!sawBars || !spots.Keys.Any(d => d >= from && d <= to)) continue;
				window = await FetchOhlcvWindowAsync(contract, from, to, ticker, expInt, spots, cancellation);
				if (!window.AnyRows)
				{
					log.Info($"    [warn] {ticker} {Iso(expiration)}: ohlcv chunk {Iso(from)}..{Iso(to)} EMPTY after data began — possible internal hole; sessions left unfilled");
					continue;
				}
			}
			sawBars = true;
			if (spots.Count == 0 || window.Rows.Count == 0) continue;
			await WriteWindowAsync(ticker, expInt, window.Rows, firstWrite, cancellation);
			firstWrite = false;
			totalRows += window.Rows.Count;
			days.UnionWith(window.Days);
		}
		if (totalRows > 0)
			await PromoteAsync(ticker, expInt, cancellation);

		if (sawBars && spots.Count == 0)
			throw new InvalidOperationException($"ohlcv bars present but no underlying spot for {ticker} {Iso(start)}..{Iso(end)} (transient underlying feed?)");
		if (totalRows == 0 && spots.Count > 0)
		{
			// Zero traded bars over real sessions: for an ELAPSED, sealable expiry that's the vendor's bogus-NoData signature — sealing
			// would certify an empty expiry forever. A FUTURE expiry is different: dailies list only days ahead (SPY/QQQ 09-01 and 09-02
			// had zero rows through 08-17 while the 09-04 weekly had 1.78M), so an empty window there is pre-listing.
			if (ShouldSeal(expiration))
				throw new InvalidOperationException($"NO BARS AT ALL for {ticker} {Iso(expiration)} across {spots.Count} trading session(s) — refusing to seal-empty (bogus vendor NoData?); re-pull later, or record a proven gap in ohlcv_known_holes");
			log.Info($"    {progress}ohlcv {ticker} {Iso(expiration)}: no bars in {Iso(start)}..{Iso(end)} — future expiry not yet listed in the window; fills as sessions elapse");
			return;
		}
		if (days.Count > 0)
			RequireNoMissingDays(ticker, expiration, end, spots, days, await KnownHolesAsync(ticker, expInt, cancellation), "ohlcv ", "interior trading day(s) with no traded bar");
		if (totalRows > 0)
			log.Info($"    {progress}ohlcv {ticker} {Iso(expiration)} rows={totalRows} days={days.Count} spot_days={spots.Count}");
	}

	private Task<OhlcvWindow> FetchOhlcvWindowAsync(OptionContract contract, DateOnly from, DateOnly to, string ticker, int expInt, Dictionary<DateOnly, double> spots, CancellationToken cancellation) =>
		ThetaRetry.RunAsync(async token =>
		{
			var window = new OhlcvWindow(false, [], []);
			var anyRows = false;
			await foreach (var table in client.OptionHistoryOhlc(contract, "1m", from, to, options.StrikeRange, token))
			{
				if (table.RowCount == 0) continue;
				anyRows = true;
				var (timeCol, strikeCol, rightCol) = (ThetaCells.Pick(table, "ms_of_day", "time", "timestamp", "datetime"), table.Column("strike"), ThetaCells.Pick(table, "right", "type"));
				var (openCol, highCol, lowCol, closeCol, volumeCol) = (table.Column("open"), table.Column("high"), table.Column("low"), table.Column("close"), table.Column("volume"));
				var (countCol, vwapCol) = (table.ColumnOrNull("count"), table.ColumnOrNull("vwap"));
				for (var row = 0; row < table.RowCount; row++)
				{
					var volume = table.Double(row, volumeCol);   // filtered as read (NaN = 0), truncated only by the encoder
					var stamp = table.Timestamp(row, timeCol);
					var day = DateOnly.FromDateTime(stamp.DateTime);
					var strike = table.Double(row, strikeCol);
					if (!(volume > 0) || !spots.TryGetValue(day, out var spot) || double.IsNaN(spot) || double.IsNaN(strike) || !(Math.Abs(strike / spot - 1.0) <= options.Band)) continue;
					var right = ThetaCells.Right(table, row, rightCol);
					var trades = countCol is int c ? ThetaCells.Count(table, row, c) : 0;
					var vwap = vwapCol is int v ? table.Double(row, v) : double.NaN;
					if (QuoteStoreSchema.EncodeOhlcv(ticker, expInt, QuoteStoreSchema.YmdInt(day), stamp.Hour * 3600 + stamp.Minute * 60 + stamp.Second, strike, right, table.Double(row, openCol), table.Double(row, highCol), table.Double(row, lowCol), table.Double(row, closeCol), (long)volume, trades, vwap) is { } encoded)
						window.Rows.Add(encoded);
					window.Days.Add(day);
				}
			}
			return window with { AnyRows = anyRows };
		}, cancellation);

	// ----- shared gates and store access ----------------------------------------------------------------------------------------------

	/// <summary>Seal-time completeness: from the first session this expiry printed through the window end, every trading day (the spot
	/// calendar) must be present. A missing interior day — a partial vendor response, empty chunk or not — withholds the seal so the
	/// retry pass re-pulls it; days proven absent at the vendor are accepted from the known-holes table.</summary>
	private void RequireNoMissingDays(string ticker, DateOnly expiration, DateOnly end, Dictionary<DateOnly, double> spots, HashSet<DateOnly> days, HashSet<DateOnly> known, string label, string what)
	{
		var first = days.Min();
		var missing = spots.Keys.Where(d => d >= first && d <= end && !days.Contains(d) && !known.Contains(d)).Order().ToList();
		if (missing.Count == 0) return;
		var shown = string.Join(", ", missing.Take(5).Select(Iso)) + (missing.Count > 5 ? "..." : "");
		throw new InvalidOperationException($"INCOMPLETE {label}{ticker} {Iso(expiration)}: {missing.Count} {what} ({shown}) — seal withheld; transient partial responses heal on the retry pass, proven vendor gaps belong in the {dataset.HolesTable} table (INSERT INTO {dataset.HolesTable} VALUES('{ticker}', {QuoteStoreSchema.YmdInt(expiration)}, {QuoteStoreSchema.YmdInt(missing[0])});)");
	}

	/// <summary>Writes one window's rows in primary-key order: into staging (cleared of a prior aborted pull's leftovers on the first
	/// write), or — supplementing — straight into the live table with INSERT OR IGNORE. Each window commits, so a long expiry shows
	/// progress and an abort loses at most the window in flight.</summary>
	private Task WriteWindowAsync<TRow>(string ticker, int expInt, List<TRow> rows, bool firstWrite, CancellationToken cancellation) => WithDbAsync(() =>
	{
		var target = options.Supplement ? dataset.Table : dataset.StagingTable;
		using var tx = db.BeginTransaction();
		if (firstWrite && !options.Supplement)
			Execute(tx, $"DELETE FROM {dataset.StagingTable} WHERE root=$root AND expiry=$exp", ("$root", ticker), ("$exp", expInt));
		switch (rows)
		{
			case List<QuoteRow> quotes: InsertQuotes(tx, target, quotes); break;
			case List<OhlcvRow> bars: InsertOhlcv(tx, target, bars); break;
		}
		tx.Commit();
	}, cancellation);

	/// <summary>Replaces the live rows of a fully-pulled expiry with the staged pull and clears the staging rows, in one transaction.
	/// Runs before the seal gates on purpose: a seal-withheld pull still lands its data; the gates only govern sealing.</summary>
	private Task PromoteAsync(string ticker, int expInt, CancellationToken cancellation) => WithDbAsync(() =>
	{
		using var tx = db.BeginTransaction();
		var key = new[] { ("$root", (object)ticker), ("$exp", expInt) };
		Execute(tx, $"DELETE FROM {dataset.Table} WHERE root=$root AND expiry=$exp", key);
		Execute(tx, $"INSERT INTO {dataset.Table} SELECT * FROM {dataset.StagingTable} WHERE root=$root AND expiry=$exp", key);
		Execute(tx, $"DELETE FROM {dataset.StagingTable} WHERE root=$root AND expiry=$exp", key);
		tx.Commit();
	}, cancellation);

	/// <summary>Seals an expiration if it is final: elapsed (no more live quotes can accrue — judged on the ET calendar) and fully in
	/// range (its window wasn't cut short by the end date).</summary>
	private Task SealAsync(string ticker, DateOnly expiration, CancellationToken cancellation) => !ShouldSeal(expiration) ? Task.CompletedTask : WithDbAsync(() =>
	{
		QuoteStoreSchema.Exec(db, dataset.SealedSql);
		using var cmd = db.CreateCommand();
		cmd.CommandText = $"INSERT OR IGNORE INTO {dataset.SealedTable} VALUES ($root, $exp)";
		cmd.Parameters.AddWithValue("$root", ticker);
		cmd.Parameters.AddWithValue("$exp", QuoteStoreSchema.YmdInt(expiration));
		if (cmd.ExecuteNonQuery() > 0) log.Info($"  [seal:{dataset.Name}] {ticker} exp {Iso(expiration)}");
	}, cancellation);

	private bool ShouldSeal(DateOnly expiration) => expiration < EtToday() && expiration <= options.End;

	private Task<HashSet<DateOnly>> SealedAsync(string ticker, CancellationToken cancellation) =>
		DatesAsync(dataset.SealedSql, $"SELECT expiry FROM {dataset.SealedTable} WHERE root=$root", ticker, null, cancellation);

	private Task<HashSet<DateOnly>> KnownHolesAsync(string ticker, int expInt, CancellationToken cancellation) =>
		DatesAsync(dataset.HolesSql, $"SELECT date FROM {dataset.HolesTable} WHERE root=$root AND expiry=$exp", ticker, expInt, cancellation);

	private Task<HashSet<DateOnly>> DatesAsync(string createSql, string query, string ticker, int? expInt, CancellationToken cancellation) => WithDbAsync(() =>
	{
		QuoteStoreSchema.Exec(db, createSql);
		using var cmd = db.CreateCommand();
		cmd.CommandText = query;
		cmd.Parameters.AddWithValue("$root", ticker);
		if (expInt is int e) cmd.Parameters.AddWithValue("$exp", e);
		var dates = new HashSet<DateOnly>();
		using var reader = cmd.ExecuteReader();
		while (reader.Read()) dates.Add(QuoteStoreSchema.FromYmdInt(reader.GetInt64(0)));
		return dates;
	}, cancellation);

	/// <summary>Per-session strikes (dollars) proven absent at the vendor for this expiry — not new gaps to the ladder gate.</summary>
	private Task<Dictionary<DateOnly, HashSet<double>>> KnownStrikeHolesAsync(string ticker, int expInt, CancellationToken cancellation) => WithDbAsync(() =>
	{
		QuoteStoreSchema.Exec(db, QuoteStoreSchema.KnownStrikeHolesSql);
		using var cmd = db.CreateCommand();
		cmd.CommandText = "SELECT date, strike_milli FROM known_strike_holes WHERE root=$root AND expiry=$exp";
		cmd.Parameters.AddWithValue("$root", ticker);
		cmd.Parameters.AddWithValue("$exp", expInt);
		var holes = new Dictionary<DateOnly, HashSet<double>>();
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			var day = QuoteStoreSchema.FromYmdInt(reader.GetInt64(0));
			(holes.TryGetValue(day, out var set) ? set : holes[day] = []).Add(Math.Round(reader.GetInt64(1) / 1000.0, 3));
		}
		return holes;
	}, cancellation);

	private static void InsertQuotes(SqliteTransaction tx, string table, List<QuoteRow> rows)
	{
		using var cmd = tx.Connection!.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = $"INSERT OR IGNORE INTO {table} VALUES ($root,$exp,$date,$sec,$strike,$right,$bid,$ask,$bsz,$asz)";
		var p = new[] { "$root", "$exp", "$date", "$sec", "$strike", "$right", "$bid", "$ask", "$bsz", "$asz" }.Select(name => cmd.Parameters.Add(name, SqliteType.Integer)).ToArray();
		p[0].SqliteType = p[5].SqliteType = SqliteType.Text;
		// Primary-key order, stable: if the vendor ever repeats a key, the first row in its original order is the one kept — as in the Python writer.
		foreach (var r in rows.OrderBy(r => r.Date).ThenBy(r => r.StrikeMilli).ThenBy(r => r.Right).ThenBy(r => r.TimeSec))
		{
			(p[0].Value, p[1].Value, p[2].Value, p[3].Value, p[4].Value, p[5].Value) = (r.Root, r.Expiry, r.Date, r.TimeSec, r.StrikeMilli, r.Right.ToString());
			(p[6].Value, p[7].Value, p[8].Value, p[9].Value) = (r.Bid, r.Ask, r.BidSize, r.AskSize);
			cmd.ExecuteNonQuery();
		}
	}

	private static void InsertOhlcv(SqliteTransaction tx, string table, List<OhlcvRow> rows)
	{
		using var cmd = tx.Connection!.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = $"INSERT OR IGNORE INTO {table} VALUES ($root,$exp,$date,$sec,$strike,$right,$o,$h,$l,$c,$vol,$trades,$vwap)";
		var p = new[] { "$root", "$exp", "$date", "$sec", "$strike", "$right", "$o", "$h", "$l", "$c", "$vol", "$trades", "$vwap" }.Select(name => cmd.Parameters.Add(name, SqliteType.Integer)).ToArray();
		p[0].SqliteType = p[5].SqliteType = SqliteType.Text;
		foreach (var r in rows.OrderBy(r => r.Date).ThenBy(r => r.StrikeMilli).ThenBy(r => r.Right).ThenBy(r => r.TimeSec))
		{
			(p[0].Value, p[1].Value, p[2].Value, p[3].Value, p[4].Value, p[5].Value) = (r.Root, r.Expiry, r.Date, r.TimeSec, r.StrikeMilli, r.Right.ToString());
			(p[6].Value, p[7].Value, p[8].Value, p[9].Value, p[10].Value, p[11].Value, p[12].Value) = (r.Open, r.High, r.Low, r.Close, r.Volume, r.Trades, r.Vwap);
			cmd.ExecuteNonQuery();
		}
	}

	private static void Execute(SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
	{
		using var cmd = tx.Connection!.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = sql;
		foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
		cmd.ExecuteNonQuery();
	}

	private async Task WithDbAsync(Action action, CancellationToken cancellation) => await WithDbAsync(() => { action(); return 0; }, cancellation);

	private async Task<T> WithDbAsync<T>(Func<T> action, CancellationToken cancellation)
	{
		await _dbGate.WaitAsync(cancellation);
		try { return action(); }
		finally { _dbGate.Release(); }
	}

	/// <summary>&lt;=ChunkDays-wide calendar windows covering [start, end].</summary>
	private IEnumerable<(DateOnly From, DateOnly To)> Windows(DateOnly start, DateOnly end)
	{
		var step = Math.Max(1, options.ChunkDays);
		for (var from = start; from <= end; )
		{
			var to = Min(end, from.AddDays(step - 1));
			yield return (from, to);
			from = to.AddDays(1);
		}
	}

	/// <summary>The trading calendar's today: sessions, expirations and seals are ET-dated, never machine-local.</summary>
	internal static DateOnly EtToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, NyTz));

	private static string Iso(DateOnly date) => ThetaDataClient.Date(date);
	private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
	private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
