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
	/// captures the further-dated legs the diagonal/calendar structures use.
	///
	/// Row label = fire minute MINUS ONE: validated 2026-06-11 (1.4M-row Schwab-vs-ThetaData join), the
	/// store's ThetaData row labeled T is the NBBO at the (T+1):00 boundary — its raw end-of-bar stamps are
	/// relabeled -60s at ingest. The scraper firing at T:00+ε samples that same boundary, so writing label
	/// T-1 makes live and historical rows mean the same instant (same-label join: median $0.00; fire-minute
	/// labels disagreed by the full intra-minute drift, ~$0.07 median on SPY). The first capture of the day
	/// is the 09:30 fire labeled 09:29 — the auction-boundary row, exactly like ThetaData's.</summary>
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

		// Label = fire minute - 1 (see the method doc): the T:00+ε fetch IS the end-of-bar sample for
		// minute T-1, which is what a store row labeled T-1 means under the ThetaData ingest convention.
		// Floor to the minute first so a retry-delayed fire within the minute can't smear the label.
		var labelEt = new DateTime(fireEt.Year, fireEt.Month, fireEt.Day, fireEt.Hour, fireEt.Minute, 0).AddMinutes(-1);
		var dateStr = labelEt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var timeStr = labelEt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

		// Pre-open gate on FIRE time (not the label): Schwab serves a frozen indicative book before 09:30
		// (verified 2026-06-10), so fires before the open are skipped; the 09:30:00 fire is the first real
		// sample and lands as the 09:29-labeled auction-boundary row.
		if (fireEt.TimeOfDay >= MarketOpenEt)
			await AppendQuotesAsync(todayContracts, dateStr, timeStr, cancellation);

		// --- data/oi/<TICKER>/<date>.jsonl : ONE full-chain snapshot per day (OI is constant intraday).
		// OI is keyed by the calendar session, not a bar boundary — stamp the FIRE date (the label date
		// would be the prior day for a hypothetical midnight fire; for the 09:30 fire they coincide).
		WriteOiSnapshotIfFirstOfDay(todayContracts, fireEt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), fireUtc, fireEt, spot);

		return (todayContracts.Count, spot);
	}

	/// <summary>Appends one row per contract to its expiration's CSV (one file per expiration), creating the
	/// file with the header line on first write and appending (FileShare.ReadWrite) thereafter. Columns:
	/// <c>date,time,strike,right,bid,ask,bid_size,ask_size</c>. Format matches the ThetaData backfill exactly
	/// (LF line endings, decimal strike text like <c>719.0</c>, NBBO sizes) so live and historical rows in a
	/// per-expiry file are byte-compatible — this is the canonical writer once ThetaData is decommissioned.
	/// That includes missing sides: a null bid/ask is written as <c>0.0</c> and a null size as <c>0</c>
	/// (the ThetaData "no quote on that side" encoding), never as an empty field — one canonical encoding
	/// so consumers can't tell the sources apart. Blank fields broke ad-hoc scripts, and QuoteStoreCache
	/// only skipped them by the accident of TryParse failing.</summary>
	private async Task AppendQuotesAsync(List<OptionContractQuote> contracts, string dateStr, string timeStr, CancellationToken cancellation)
	{
		// Pre-open gating (Schwab serves a FROZEN indicative book before 09:30, verified 2026-06-10:
		// ATM SPY rows 09:25–09:29 identical at 17.03×17.10 with 0×0 sizes, then the real auction quote
		// at 09:30 was 15.73×15.81 — $1.30 away) is the CALLER's job, on FIRE time — the timeStr here is
		// the end-of-bar label (fire minute - 1), so a time check against it would also drop the
		// legitimate 09:29-labeled auction-boundary row.
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
				var bid = q.Bid?.ToString(CultureInfo.InvariantCulture) ?? "0.0";
				var ask = q.Ask?.ToString(CultureInfo.InvariantCulture) ?? "0.0";
				var bidSize = q.BidSize?.ToString(CultureInfo.InvariantCulture) ?? "0";
				var askSize = q.AskSize?.ToString(CultureInfo.InvariantCulture) ?? "0";
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
