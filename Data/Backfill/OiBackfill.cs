using System.Globalization;
using System.Text.Json;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data.Backfill;

/// <param name="MaxDte">Expirations captured per day: those within this many days.</param>
/// <param name="Rate">Risk-free rate for the IV back-solve.</param>
/// <param name="Retries">Extra attempts per month chunk after a failure.</param>
/// <param name="OutDir">The OI store root (data/oi); one sub-directory per ticker.</param>
internal sealed record OiBackfillOptions(IReadOnlyList<string> Tickers, DateOnly Start, DateOnly End, int MaxDte, double Rate, int Retries, string OutDir);

/// <summary>The daily full-chain EOD + open-interest pull into <c>data/oi/&lt;TICKER&gt;/&lt;date&gt;.jsonl</c> — a port of
/// scripts/backfill_thetadata.py's --run mode. Per (ticker, month) chunk: option_history_eod (OHLC + closing NBBO, no OI) joined with
/// option_history_open_interest on (date, expiration, strike, right), the underlying close, and an IV back-solved from each
/// contract's EOD mid (the IV endpoints need a higher tier; <see cref="ChainSnapshotOiCache"/> drops contracts without one). Sealing
/// is per DAY in <c>data/oi/&lt;TICKER&gt;/sealed.json</c>: a past session's settled OI never changes, so once written it is skipped
/// forever; the ET-forming day is never sealed, and a trading day the vendor returned nothing for stays unsealed (and is named) so
/// every run retries it.</summary>
internal sealed class OiBackfill(ThetaDataClient client, OiBackfillOptions options, BackfillLog log)
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private readonly UnderlyingCloses _closes = new(client, log);

	internal async Task RunAsync(CancellationToken cancellation)
	{
		var today = MinuteBackfill.EtToday();
		foreach (var ticker in options.Tickers)
		{
			var dir = Path.Combine(options.OutDir, ticker);
			Directory.CreateDirectory(dir);
			var sealedDays = LoadSealed(dir);
			var pending = MonthChunks(options.Start, options.End).Where(c => !TradingDays(c.From, c.To).All(sealedDays.Contains)).ToList();
			if (pending.Count == 0)
			{
				log.Info($"=== {ticker} -> {dir}  ({sealedDays.Count} day(s) sealed, nothing new in {Iso(options.Start)}..{Iso(options.End)}) ===");
				continue;
			}
			log.Info($"=== {ticker} -> {dir}  (max_dte={options.MaxDte}, {sealedDays.Count} day(s) sealed, pulling {pending.Count} chunk(s) in {Iso(pending[0].From)}..{Iso(options.End)}) ===");
			foreach (var (from, to) in pending)
			{
				log.Info($"  chunk {Iso(from)} .. {Iso(to)}  [{DateTime.Now:HH:mm:ss}]");
				var written = await PullChunkWithRetriesAsync(ticker, dir, from, to, cancellation);
				var newlySealed = written.Where(d => d < today && sealedDays.Add(d)).Order().ToList();
				if (newlySealed.Count > 0)
				{
					SaveSealed(dir, sealedDays);   // checkpoint per chunk so a later failure can't lose it
					log.Info($"  [seal] {ticker} OI day(s): {string.Join(", ", newlySealed.Select(Iso))} ({sealedDays.Count} total)");
				}
				var missing = TradingDays(from, to).Where(d => d < today && !sealedDays.Contains(d)).ToList();
				if (missing.Count > 0)
					log.Info($"  [gap] {ticker} OI: no data for trading day(s) {string.Join(", ", missing.Select(Iso))} — left unsealed; re-pulled next run");
			}
		}
	}

	private async Task<List<DateOnly>> PullChunkWithRetriesAsync(string ticker, string dir, DateOnly from, DateOnly to, CancellationToken cancellation)
	{
		for (var attempt = 1; attempt <= options.Retries + 1; attempt++)
		{
			try
			{
				return await PullChunkAsync(ticker, dir, from, to, cancellation);
			}
			catch (Exception ex) when (ex is not (ThetaSessionException or OperationCanceledException))
			{
				log.Info($"    [error] {ticker} {Iso(from)}..{Iso(to)} attempt {attempt}/{options.Retries + 1}: {ex.GetType().Name}: {ex.Message} — retrying");
			}
		}
		log.Info($"    [GIVE UP] {ticker} {Iso(from)}..{Iso(to)} after {options.Retries + 1} attempts — not sealed; re-run to retry");
		return [];
	}

	/// <summary>Pulls and writes one (ticker, month) chunk; returns the days written.</summary>
	private async Task<List<DateOnly>> PullChunkAsync(string ticker, string dir, DateOnly from, DateOnly to, CancellationToken cancellation)
	{
		var chain = new OptionContract(ticker, Expiration: null);
		var eod = await ThetaRetry.CollectAsync(token => client.OptionHistoryEod(chain, from, to, options.MaxDte, token), cancellation);
		var oi = await ThetaRetry.CollectAsync(token => client.OptionHistoryOpenInterest(chain, from, to, options.MaxDte, token), cancellation);
		if (eod.RowCount == 0)
		{
			log.Info("    (no EOD rows)");
			return [];
		}

		var openInterest = new Dictionary<(DateOnly, DateOnly, double, char), long?>();
		if (oi.RowCount > 0)
		{
			var (dateCol, expCol, strikeCol, rightCol, oiCol) = (ThetaCells.Pick(oi, "timestamp", "date"), ThetaCells.Pick(oi, "expiration", "exp"), oi.Column("strike"), ThetaCells.Pick(oi, "right", "type"), ThetaCells.Pick(oi, "open_interest", "oi"));
			for (var row = 0; row < oi.RowCount; row++)
				openInterest[(ThetaCells.Date(oi, row, dateCol), ThetaCells.Date(oi, row, expCol), oi.Double(row, strikeCol), ThetaCells.Right(oi, row, rightCol))] = Integer(oi.Double(row, oiCol));
		}

		var spots = await _closes.GetAsync(ticker, from, to, cancellation);

		// EOD has no plain date column: `created` is each row's EOD-finalization stamp (~17:15 ET on the session), so its date IS the session.
		var (eDate, eExp, eStrike, eRight) = (ThetaCells.Pick(eod, "created", "date", "quote_date", "bar_date", "timestamp"), ThetaCells.Pick(eod, "expiration", "exp"), eod.Column("strike"), ThetaCells.Pick(eod, "right", "type"));
		var (eBid, eAsk, eClose, eVolume) = (eod.Column("bid"), eod.Column("ask"), ThetaCells.Pick(eod, "close", "last"), eod.Column("volume"));
		var byDay = new SortedDictionary<DateOnly, List<object>>();
		var ivSolved = 0;
		for (var row = 0; row < eod.RowCount; row++)
		{
			var (day, expiration, strike, right) = (ThetaCells.Date(eod, row, eDate), ThetaCells.Date(eod, row, eExp), eod.Double(row, eStrike), ThetaCells.Right(eod, row, eRight));
			var (bid, ask, close) = (eod.Double(row, eBid), eod.Double(row, eAsk), eod.Double(row, eClose));
			var spot = spots.TryGetValue(day, out var s) ? s : double.NaN;
			var iv = ImpliedVol.BackSolve(bid > 0 && ask > 0 ? (bid + ask) / 2.0 : close, spot, strike, (expiration.DayNumber - day.DayNumber) / 365.0, options.Rate, right == 'C');
			if (!double.IsNaN(iv)) ivSolved++;
			(byDay.TryGetValue(day, out var list) ? list : byDay[day] = []).Add(new
			{
				symbol = $"{ticker}{expiration:yyMMdd}{right}{QuoteStoreSchema.StrikeMilli(strike):D8}",
				bid = Nullable(bid),
				ask = Nullable(ask),
				last = Nullable(close),
				volume = Integer(eod.Double(row, eVolume)),
				openInterest = openInterest.GetValueOrDefault((day, expiration, strike, right)),
				iv = Nullable(iv),
				hv = (double?)null,
				iv5 = (double?)null,
			});
		}
		log.Info($"    contracts={eod.RowCount} iv_solved={ivSolved} oi_rows={openInterest.Count} spot_days={spots.Count}");
		if (ivSolved == 0)
			log.Info("    [WARN] no IV solved (missing spot?) — contracts will be DROPPED by ChainSnapshotOiCache.");

		foreach (var (day, contracts) in byDay)
		{
			// One settled EOD record per day, stamped 16:00 ET — overwritten whole (idempotent), including an intraday live-capture
			// snapshot of the same day.
			var close = new DateTime(day.Year, day.Month, day.Day, 16, 0, 0);
			var closeEt = new DateTimeOffset(close, NyTz.GetUtcOffset(close));
			var record = new
			{
				tsUtc = closeEt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
				tsEt = closeEt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
				ticker,
				underlyingPrice = spots.TryGetValue(day, out var spot) ? Nullable(spot) : null,
				options = contracts,
			};
			await File.WriteAllTextAsync(Path.Combine(dir, $"{Iso(day)}.jsonl"), JsonSerializer.Serialize(record) + "\n", cancellation);
		}
		log.Info($"    wrote {byDay.Count} day file(s)");
		return [.. byDay.Keys];
	}

	private static double? Nullable(double value) => double.IsNaN(value) ? null : value;
	private static long? Integer(double value) => double.IsNaN(value) ? null : (long)value;

	/// <summary>The sealed days in sealed.json (<c>{"sealed": ["yyyy-MM-dd", ...]}</c>); unreadable = none (re-pull rather than
	/// silently lose coverage).</summary>
	private static HashSet<DateOnly> LoadSealed(string dir)
	{
		try
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "sealed.json")));
			return [.. doc.RootElement.GetProperty("sealed").EnumerateArray().Select(e => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture))];
		}
		catch (Exception ex) when (ex is IOException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { return []; }
	}

	private static void SaveSealed(string dir, HashSet<DateOnly> sealedDays)
	{
		var (path, tmp) = (Path.Combine(dir, "sealed.json"), Path.Combine(dir, "sealed.json.tmp"));
		File.WriteAllText(tmp, JsonSerializer.Serialize(new { @sealed = sealedDays.Order().Select(Iso) }, new JsonSerializerOptions { WriteIndented = true }));
		File.Move(tmp, path, overwrite: true);   // atomic replace
	}

	/// <summary>&lt;=1-month windows covering [start, end], split at month boundaries.</summary>
	private static IEnumerable<(DateOnly From, DateOnly To)> MonthChunks(DateOnly start, DateOnly end)
	{
		for (var from = start; from <= end; )
		{
			var monthEnd = new DateOnly(from.Year, from.Month, 1).AddMonths(1).AddDays(-1);
			var to = monthEnd < end ? monthEnd : end;
			yield return (from, to);
			from = to.AddDays(1);
		}
	}

	private static IEnumerable<DateOnly> TradingDays(DateOnly from, DateOnly to)
	{
		for (var day = from; day <= to; day = day.AddDays(1))
			if (MarketCalendar.IsOpen(day.ToDateTime(TimeOnly.MinValue))) yield return day;
	}

	private static string Iso(DateOnly date) => ThetaDataClient.Date(date);
}

/// <summary>Black-Scholes implied volatility by bisection, exactly as the Python backfill solved it for the OI snapshots: plain
/// European BS with no dividend and the Abramowitz-Stegun 7.1.26 erf, 60 halvings of [1e-4, 5]. NaN when T &lt;= 0, spot or strike
/// is invalid, or the price is not above intrinsic. This is the stopgap IV the snapshots have always carried (the IV endpoints need a
/// higher tier); dividend-aware pricing happens at consumption.</summary>
internal static class ImpliedVol
{
	internal static double BackSolve(double price, double spot, double strike, double years, double rate, bool isCall)
	{
		var intrinsic = isCall ? Math.Max(spot - strike, 0.0) : Math.Max(strike - spot, 0.0);
		if (!(double.IsFinite(price) && double.IsFinite(spot) && years > 0 && spot > 0 && strike > 0 && price > intrinsic + 1e-6)) return double.NaN;
		var (lo, hi) = (1e-4, 5.0);
		for (var i = 0; i < 60; i++)
		{
			var mid = 0.5 * (lo + hi);
			if (Price(spot, strike, years, rate, mid, isCall) > price) hi = mid;
			else lo = mid;
		}
		return 0.5 * (lo + hi);
	}

	private static double Price(double spot, double strike, double years, double rate, double sigma, bool isCall)
	{
		var sig = Math.Max(sigma, 1e-9);
		var t = Math.Max(years, 1e-9);
		var sqrtT = Math.Sqrt(t);
		var d1 = (Math.Log(spot / strike) + (rate + 0.5 * sig * sig) * t) / (sig * sqrtT);
		var d2 = d1 - sig * sqrtT;
		var call = spot * NormCdf(d1) - strike * Math.Exp(-rate * t) * NormCdf(d2);
		return isCall ? call : call - spot + strike * Math.Exp(-rate * t);   // put via parity
	}

	private static double NormCdf(double x)
	{
		var ax = Math.Abs(x) / Math.Sqrt(2.0);
		var t = 1.0 / (1.0 + 0.3275911 * ax);
		var poly = (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t;
		var erf = Math.Sign(x) * (1.0 - poly * Math.Exp(-ax * ax));
		return 0.5 * (1.0 + erf);
	}
}
