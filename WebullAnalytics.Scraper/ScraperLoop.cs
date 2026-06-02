using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using WebullAnalytics.Api;

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
	private readonly ApiConfig _apiConfig;
	private readonly string _outputDir;

	public ScraperLoop(string ticker, ScraperConfig config, ApiConfig apiConfig)
	{
		_ticker = ticker.ToUpperInvariant();
		_config = config;
		_apiConfig = apiConfig;
		var outputRoot = Path.IsPathRooted(config.OutputPath)
			? config.OutputPath
			: WebullAnalytics.Program.ResolvePath(config.OutputPath);
		_outputDir = Path.Combine(outputRoot, _ticker);
		Directory.CreateDirectory(_outputDir);
	}

	public async Task<int> RunAsync(DateTime startEt, DateTime endEt, CancellationToken cancellation)
	{
		AnsiConsole.MarkupLine($"[bold]wa-scraper[/] ticker={_ticker} interval={_config.IntervalSeconds}s startEt={startEt:HH:mm:ss} endEt={endEt:HH:mm:ss} out={Markup.Escape(_outputDir)}");

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

	/// <summary>Pulls the chain via Webull and appends one JSON line to today's JSONL file, keeping the
	/// contracts that expire from today out to <c>config.MaxDte</c> calendar days. Webull's strategy/list
	/// returns the full chain across every listed expiration (~30k contracts for SPXW), which balloons each
	/// per-minute line; MaxDte=0 (default) keeps only the same-day 0DTE expiry the bot trades, while a larger
	/// MaxDte also captures the further-dated legs the diagonal/calendar structures use — so the synthetic
	/// far-leg pricing can be validated against real quotes via `wa options reprice`. One file per
	/// (ticker, NY date). The fire-time is stamped in both UTC and ET so downstream consumers can re-derive
	/// the minute bucket without re-parsing the timestamp string.</summary>
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
		for (var attempt = 0; attempt <= _config.EmptyRetryCount; attempt++)
		{
			// The chain/list endpoint only quotes the front (0DTE) expiry — the further-dated contracts come
			// back as symbols with null bid/ask/IV. When MaxDte>0 we must queryBatch-refresh those near-the-money
			// far-dated strikes, or the persisted far legs are unpriceable placeholders (the bug this fixes).
			var (quotes, fetchedSpot, _) = _config.MaxDte > 0
				? await WebullOptionsClient.FetchChainWithExpiryRefreshAsync(_apiConfig, _ticker, Enumerable.Range(0, _config.MaxDte + 1).Select(d => fireEt.Date.AddDays(d)).ToList(), _config.FarStrikeRangeFraction, cancellation)
				: await WebullOptionsClient.FetchChainAsync(_apiConfig, _ticker, cancellation);
			spot = fetchedSpot;
			todayContracts = quotes.Values
				.Where(q =>
				{
					var exp = WebullAnalytics.ParsingHelpers.ParseOptionSymbol(q.ContractSymbol)?.ExpiryDate.Date;
					if (exp == null) return false;
					var dte = (exp.Value - fireEt.Date).Days;
					return dte >= 0 && dte <= _config.MaxDte;
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
		var path = Path.Combine(_outputDir, $"{dateStr}.jsonl");

		var record = new
		{
			tsUtc = fireUtc.ToString("o", CultureInfo.InvariantCulture),
			tsEt = fireEt.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
			ticker = _ticker,
			underlyingPrice = spot,
			options = todayContracts.Select(q => new
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
		using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
		using var writer = new StreamWriter(stream);
		await writer.WriteLineAsync(line);
		return (todayContracts.Count, spot);
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
