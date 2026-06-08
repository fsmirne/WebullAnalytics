using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace WebullAnalytics.Scraper;

/// <summary>The minute-aligned scrape loop. Sleeps to the next interval boundary AFTER firing, not
/// before — that is the key difference from <c>wa ai watch</c>'s sleep-then-fire pattern, which
/// drifts by the per-tick fetch duration. Here the "next fire" target is advanced by the fixed
/// interval each iteration and the sleep is to that absolute target, so drift accumulates only as
/// the per-tick fetch overshoots one full interval (in which case we skip ahead to the next
/// boundary — drop a tick rather than fire late).</summary>
internal sealed class ScraperLoop
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan MarketOpenEt = new(9, 30, 0);
	private static readonly TimeSpan MarketCloseEt = new(16, 0, 0);

	private readonly string _ticker;
	private readonly ScraperConfig _config;
	private readonly IChainSource _chainSource;
	private readonly string _quotesDir;   // data/quotes/<TICKER> — minute NBBO time-series (one CSV per expiry)
	private readonly string _oiDir;       // data/oi/<TICKER>     — one full-chain OI snapshot per day

	public ScraperLoop(string ticker, ScraperConfig config, IChainSource chainSource)
	{
		_ticker = ticker.ToUpperInvariant();
		_config = config;
		_chainSource = chainSource;
		// The two canonical stores share the same on-disk format as the ThetaData backfill, so a live scrape
		// and a historical pull are interchangeable in the same directory tree.
		_quotesDir = Path.Combine(WebullAnalytics.Program.ResolvePath("data/quotes"), _ticker);
		_oiDir = Path.Combine(WebullAnalytics.Program.ResolvePath("data/oi"), _ticker);
		Directory.CreateDirectory(_quotesDir);
		Directory.CreateDirectory(_oiDir);
	}

	public async Task<int> RunAsync(DateTime startEt, DateTime endEt, CancellationToken cancellation)
	{
		AnsiConsole.MarkupLine($"[bold]wa-scraper[/] ticker={_ticker} interval={_config.IntervalSeconds}s startEt={startEt:HH:mm:ss} endEt={endEt:HH:mm:ss} quotes={Markup.Escape(_quotesDir)} oi={Markup.Escape(_oiDir)}");

		// Sleep until first fire. If startEt is in the past, we begin immediately at the next
		// minute-boundary so the first persisted line has a clean ET wall-clock minute stamp.
		var firstFire = ComputeFirstFire(startEt);
		await DelayUntilLocalAsync(firstFire, cancellation);

		var nextFire = firstFire;
		var ticksRun = 0;
		var failures = 0;
		var endLocal = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endEt, DateTimeKind.Unspecified), NyTz).ToLocalTime();

		while (!cancellation.IsCancellationRequested && DateTime.Now < endLocal)
		{
			if (_config.MarketHoursOnly && !IsMarketOpen(DateTime.Now))
			{
				nextFire = AdvanceToNext(nextFire, _config.IntervalSeconds);
				await DelayUntilLocalAsync(nextFire, cancellation);
				continue;
			}

			var fireWallClock = DateTime.Now;
			try
			{
				var (count, spot) = await TickOnceAsync(fireWallClock, cancellation);
				if (count > 0)
				{
					ticksRun++;
					failures = 0;
					AnsiConsole.MarkupLine($"[dim]{fireWallClock:HH:mm:ss} fired: {count} contracts, spot={spot?.ToString("F2") ?? "?"}[/]");
				}
				else
				{
					// Empty even after in-tick retries. A persistent dataless stretch (e.g. an expired
					// session) would otherwise burn the whole day silently, so it counts toward the same
					// circuit breaker as a hard failure.
					failures++;
					AnsiConsole.MarkupLine($"[yellow]{fireWallClock:HH:mm:ss} no data after {_config.EmptyRetryCount} retries — nothing written ({failures}/5)[/]");
					if (failures >= 5)
					{
						Console.Error.WriteLine("Circuit breaker: 5 consecutive dataless/failed ticks. Exiting.");
						return 3;
					}
				}
			}
			catch (OperationCanceledException) { break; }
			catch (WebullAnalytics.Api.SchwabAuthException ex)
			{
				// Expired/invalid token won't fix itself by retrying — stop now with an actionable message.
				Console.Error.WriteLine($"Schwab auth: {ex.Message}");
				return 4;
			}
			catch (Exception ex)
			{
				failures++;
				AnsiConsole.MarkupLine($"[red]{fireWallClock:HH:mm:ss} tick failed ({failures}/5): {Markup.Escape(ex.Message)}[/]");
				if (failures >= 5)
				{
					Console.Error.WriteLine("Circuit breaker: 5 consecutive dataless/failed ticks. Exiting.");
					return 3;
				}
			}

			nextFire = AdvanceToNext(nextFire, _config.IntervalSeconds);
			await DelayUntilLocalAsync(nextFire, cancellation);
		}

		AnsiConsole.MarkupLine($"[dim]Loop exited. ticks={ticksRun} failures={failures}[/]");
		return 0;
	}

	/// <summary>Pulls the chain and writes it to the two canonical stores that the backtest reads (the same
	/// on-disk shape the ThetaData backfill produces, so live + historical are interchangeable):
	/// <list type="bullet">
	/// <item><c>data/quotes/&lt;TICKER&gt;/&lt;expiry&gt;.csv</c> — one row per kept contract this tick, appended,
	/// grouped into one CSV per expiration. Columns <c>date,time,strike,right,bid,ask,bid_size,ask_size</c>.</item>
	/// <item><c>data/oi/&lt;TICKER&gt;/&lt;date&gt;.jsonl</c> — exactly ONE full-chain snapshot per ET date (OI is
	/// constant intraday), written on the first successful tick of the day and skipped thereafter.</item>
	/// </list>
	/// Keeps contracts expiring from today out to <c>config.MaxDte</c> calendar days, then applies a
	/// <c>±config.StrikeBandFraction</c> moneyness band around the fetched spot (Schwab returns range=ALL, so the
	/// band is enforced here). MaxDte=0 (default) keeps only the same-day 0DTE expiry; a larger MaxDte also
	/// captures the further-dated legs the diagonal/calendar structures use. The live scraper fires at the real
	/// ET wall-clock minute, which is ALREADY start-of-bar (09:30:00 = the 09:30 minute), so it writes the time
	/// as-is — do NOT apply any -60s end-of-bar shift (that shift only exists for ThetaData's end-of-bar stamps).</summary>
	private async Task<(int Count, decimal? Spot)> TickOnceAsync(DateTime fireWallClock, CancellationToken cancellation)
	{
		var fireEt = TimeZoneInfo.ConvertTime(fireWallClock, NyTz);
		var fireUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fireEt, DateTimeKind.Unspecified), NyTz);

		// Retry the chain fetch when Webull returns no contracts for this minute (throttle, dropped
		// session, transient empty response). A missing minute is worse than a marginally delayed one:
		// the backtest replays this file minute-by-minute, so a gap forces interpolation. Retry a few
		// times within the interval; only persist once we actually have contracts, never an empty line.
		List<OptionContractQuote> todayContracts = new();
		decimal? spot = null;
		var fromExpiry = DateOnly.FromDateTime(fireEt.Date);
		var toExpiry = DateOnly.FromDateTime(fireEt.Date.AddDays(_config.MaxDte));
		for (var attempt = 0; attempt <= _config.EmptyRetryCount; attempt++)
		{
			// The source returns the full window already quoted (Schwab inlines NBBO+OI for every expiry;
			// the Webull source does the queryBatch refresh internally when the window spans >1 day).
			var (fetchedSpot, fetched) = await _chainSource.FetchChainAsync(_ticker, fromExpiry, toExpiry, _config.FarStrikeRangeFraction, cancellation);
			spot = fetchedSpot;
			todayContracts = fetched
				.Where(q =>
				{
					var parsed = WebullAnalytics.ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
					if (parsed == null) return false;
					// Querying SPXW's underlying (SPX) returns BOTH roots — SPX (AM-settled monthly) and
					// SPXW (PM weekly). On monthly-expiry dates the SPX series comes back unquoted and
					// shadows the real SPXW strikes (a consumer keying on expiry/strike/right could grab
					// the empty SPX entry). Keep only the requested root.
					if (!string.Equals(parsed.Root, _ticker, StringComparison.OrdinalIgnoreCase)) return false;
					var dte = (parsed.ExpiryDate.Date - fireEt.Date).Days;
					if (dte < 0 || dte > _config.MaxDte) return false;
					// ±band moneyness filter. Schwab returns range=ALL, so the band is enforced post-fetch here.
					// If spot is unknown we can't band — keep the contract.
					if (spot is decimal sp && sp > 0m && Math.Abs(parsed.Strike / sp - 1m) > _config.StrikeBandFraction) return false;
					return true;
				})
				.ToList();
			if (todayContracts.Count > 0) break;
			if (attempt < _config.EmptyRetryCount)
			{
				AnsiConsole.MarkupLine($"[yellow]{fireEt:HH:mm:ss} empty chain — retry {attempt + 1}/{_config.EmptyRetryCount} in {_config.EmptyRetryDelaySeconds}s[/]");
				await Task.Delay(TimeSpan.FromSeconds(_config.EmptyRetryDelaySeconds), cancellation);
			}
		}

		// Still nothing after retries: skip the write so a dataless minute is simply absent rather than
		// a confusing empty record. The caller treats a zero count as a soft miss.
		if (todayContracts.Count == 0) return (0, spot);

		var dateStr = fireEt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		// The fire-time is the real ET wall-clock minute, which is ALREADY start-of-bar (09:30:00 = the 09:30
		// minute) — write it as-is. Do NOT apply the -60s shift the ThetaData pull uses to normalize its
		// end-of-bar stamps (that shift only exists for ThetaData's end-of-bar minute stamps).
		var timeStr = fireEt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

		// --- data/quotes/<TICKER>/<expiry>.csv : minute NBBO time-series, one CSV per expiration ---
		await AppendQuotesAsync(todayContracts, dateStr, timeStr, cancellation);

		// --- data/oi/<TICKER>/<date>.jsonl : ONE full-chain snapshot per day (OI is constant intraday) ---
		WriteOiSnapshotIfFirstOfDay(todayContracts, dateStr, fireUtc, fireEt, spot);

		return (todayContracts.Count, spot);
	}

	/// <summary>Appends one row per contract to its expiration's CSV (one file per expiration), creating the
	/// file with the header line on first write and appending (FileShare.ReadWrite) thereafter. Columns:
	/// <c>date,time,strike,right,bid,ask,bid_size,ask_size</c>. Format matches the ThetaData backfill exactly
	/// (LF line endings, decimal strike text like <c>719.0</c>, NBBO sizes) so live and historical rows in a
	/// per-expiry file are byte-compatible — this is the canonical writer once ThetaData is decommissioned.
	/// bid_size/ask_size are written empty only when the source omits them (the reader treats empty as 0).</summary>
	private async Task AppendQuotesAsync(List<OptionContractQuote> contracts, string dateStr, string timeStr, CancellationToken cancellation)
	{
		const string header = "date,time,strike,right,bid,ask,bid_size,ask_size";
		foreach (var byExpiry in contracts.GroupBy(q => WebullAnalytics.ParsingHelpers.ParseOptionSymbol(q.ContractSymbol)!.ExpiryDate.Date))
		{
			var path = Path.Combine(_quotesDir, $"{byExpiry.Key:yyyy-MM-dd}.csv");
			var needHeader = !File.Exists(path);
			using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
			using var writer = new StreamWriter(stream) { NewLine = "\n" };   // LF to match the ThetaData backfill (pandas to_csv); avoids mixed CRLF/LF in an appended file
			if (needHeader) await writer.WriteLineAsync(header);
			foreach (var q in byExpiry)
			{
				var parsed = WebullAnalytics.ParsingHelpers.ParseOptionSymbol(q.ContractSymbol)!;
				var strike = FormatStrike(parsed.Strike);
				var bid = q.Bid?.ToString(CultureInfo.InvariantCulture) ?? "";
				var ask = q.Ask?.ToString(CultureInfo.InvariantCulture) ?? "";
				var bidSize = q.BidSize?.ToString(CultureInfo.InvariantCulture) ?? "";
				var askSize = q.AskSize?.ToString(CultureInfo.InvariantCulture) ?? "";
				await writer.WriteLineAsync($"{dateStr},{timeStr},{strike},{parsed.CallPut},{bid},{ask},{bidSize},{askSize}");
			}
		}
	}

	/// <summary>Strike text that matches the ThetaData backfill's pandas-float column (719 → <c>719.0</c>,
	/// 719.5 → <c>719.5</c>) so live and historical rows in one per-expiry file share a single representation.
	/// <c>0.0##</c> keeps at least one decimal place and drops superfluous trailing zeros.</summary>
	private static string FormatStrike(decimal strike) => strike.ToString("0.0##", CultureInfo.InvariantCulture);

	/// <summary>Writes exactly ONE full-chain snapshot for the ET date to <c>data/oi/&lt;TICKER&gt;/&lt;date&gt;.jsonl</c>
	/// on the first RTH tick (≥09:30 ET) of that date; OI is constant intraday, so subsequent ticks skip if the
	/// file already exists. Gating to RTH (rather than the first tick, which may be pre-market) means the recorded
	/// <c>underlyingPrice</c> and per-contract bid/ask/iv are regular-hours values, not a thin pre-market print —
	/// the IV in particular feeds the gravity gamma-weighting via <c>ChainSnapshotOiCache</c>. A pre-market-only
	/// run therefore writes no snapshot, which is fine: OI doesn't change before the open.
	/// Record shape matches the ThetaData pull: <c>{tsUtc, tsEt, ticker, underlyingPrice, options:[...]}</c>.</summary>
	private void WriteOiSnapshotIfFirstOfDay(List<OptionContractQuote> contracts, string dateStr, DateTime fireUtc, DateTime fireEt, decimal? spot)
	{
		if (fireEt.TimeOfDay < MarketOpenEt) return;   // wait for the first regular-hours tick so spot/IV aren't pre-market
		var path = Path.Combine(_oiDir, $"{dateStr}.jsonl");
		if (File.Exists(path)) return;

		var record = new
		{
			tsUtc = fireUtc.ToString("o", CultureInfo.InvariantCulture),
			tsEt = fireEt.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
			ticker = _ticker,
			underlyingPrice = spot,
			options = contracts.Select(q => new
			{
				symbol = q.ContractSymbol,
				bid = q.Bid,
				ask = q.Ask,
				last = q.LastPrice,
				volume = q.Volume,
				openInterest = q.OpenInterest,
				iv = q.ImpliedVolatility,
				hv = q.HistoricalVolatility,
				iv5 = q.ImpliedVolatility5Day,
			})
		};

		var line = JsonSerializer.Serialize(record);
		// CreateNew so a race between near-simultaneous "first ticks" can't double-write; if it lost the race
		// (file appeared between the Exists check and here) just skip — the existing snapshot is authoritative.
		try
		{
			using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
			using var writer = new StreamWriter(stream) { NewLine = "\n" };   // LF to match the ThetaData backfill OI writer
			writer.WriteLine(line);
		}
		catch (IOException) { /* already created by a concurrent tick — OI is constant intraday, skip */ }
	}

	/// <summary>First-fire target: the smallest interval-aligned ET time >= <paramref name="startEt"/>
	/// (or >= now ET if startEt has already passed). Aligning to interval boundaries (60-second
	/// floors by default) keeps the per-tick timestamps on clean wall-clock minutes, which is what
	/// downstream consumers join against bar timestamps.</summary>
	private DateTime ComputeFirstFire(DateTime startEt)
	{
		var nowEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		var target = startEt > nowEt ? startEt : nowEt;
		return AlignToBoundary(target, _config.IntervalSeconds, ceilingIfNotAligned: true);
	}

	/// <summary>Advance a fire-target by exactly one interval. If the current `now` already passed
	/// the next-fire target by more than one interval (because the previous fetch overshot), skip
	/// forward to the smallest boundary strictly in the future of `now`. This drops late ticks
	/// rather than firing them late, which is what the user wanted: minute-clean timestamps over
	/// fixed-cadence-but-drifting.</summary>
	private static DateTime AdvanceToNext(DateTime prevFire, int intervalSeconds)
	{
		var nowEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		var next = prevFire.AddSeconds(intervalSeconds);
		while (next <= nowEt) next = next.AddSeconds(intervalSeconds);
		return next;
	}

	private static DateTime AlignToBoundary(DateTime dt, int intervalSeconds, bool ceilingIfNotAligned)
	{
		var secsSinceMidnight = (long)(dt - dt.Date).TotalSeconds;
		var floor = (secsSinceMidnight / intervalSeconds) * intervalSeconds;
		if (floor == secsSinceMidnight) return dt.Date.AddSeconds(floor);
		return dt.Date.AddSeconds(ceilingIfNotAligned ? floor + intervalSeconds : floor);
	}

	/// <summary>Sleeps until the given ET wall-clock time, converting once so the runtime delay is
	/// computed in local-wall-clock terms (so the OS DST handling matches what TZ conversion did).</summary>
	private static async Task DelayUntilLocalAsync(DateTime etTarget, CancellationToken ct)
	{
		var utcTarget = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(etTarget, DateTimeKind.Unspecified), NyTz);
		var localTarget = utcTarget.ToLocalTime();
		var delay = localTarget - DateTime.Now;
		if (delay > TimeSpan.Zero)
			try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
	}

	private static bool IsMarketOpen(DateTime nowLocal)
	{
		var nowEt = TimeZoneInfo.ConvertTime(nowLocal, NyTz);
		if (!MarketCalendar.IsOpen(nowEt.Date)) return false;
		var t = nowEt.TimeOfDay;
		return t >= MarketOpenEt && t <= MarketCloseEt;
	}
}
