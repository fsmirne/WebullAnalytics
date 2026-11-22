using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Open.Cascade;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Analyze;
using WebullAnalytics.IO;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal sealed class AIVerifySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Ticker to verify (e.g. SPY, SPXW).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--strategy <TOKEN>")]
	[Description("Strategy token to verify (e.g. DC2, CASCADE). Loads ai-config.<TICKER>.<STRATEGY>.json and its proposal log. Defaults to the ticker's defaultStrategy; an error when neither is set.")]
	public string? Strategy { get; set; }

	[CommandOption("--date <YYYY-MM-DD>")]
	[Description("Single trading date to verify (shorthand for --since D --until D). Defaults to the most recent completed trading session.")]
	public string? Date { get; set; }

	[CommandOption("--since <YYYY-MM-DD>")]
	[Description("Start of verification window (inclusive).")]
	public string? Since { get; set; }

	[CommandOption("--until <YYYY-MM-DD>")]
	[Description("End of verification window (inclusive). Defaults to --since (or most recent completed trading session).")]
	public string? Until { get; set; }

	[CommandOption("--cadence <MINUTES>")]
	[Description("Cascade strategies only: backtest grid cadence in minutes. Default: 1 (every minute, as live reads; matching `wa ai backtest`).")]
	public int Cadence { get; set; } = 1;

	[CommandOption("--quote-db <PATH>")]
	[Description("Override the SQLite quote-store path (default data/quotes.db).")]
	public string? QuoteDb { get; set; }

	[CommandOption("--detail")]
	[Description("Scored strategies: after the summary, a pick ledger — live and backtest rows per day (time, legs, leg mids, spot, net, score, rep IV, live POP/EV, P&L to exit), each backtest cell with its Δ vs live, plus the unpinned backtest on early-open days — and, for days with a pick still open, an `analyze trade --compare` command that marks both picks at current quotes.")]
	public bool Detail { get; set; }

	[CommandOption("--output <FORMAT>")]
	[Description("Output format: console or text. Default: console.")]
	public string Output { get; set; } = "console";

	[CommandOption("--output-path <PATH>")]
	[Description("Path for --output text.")]
	public string? OutputPath { get; set; }

	public override ValidationResult Validate()
	{
		if (string.IsNullOrWhiteSpace(Ticker))
			return ValidationResult.Error("<ticker> is required (e.g. SPY, SPXW)");
		if (Date != null && (Since != null || Until != null))
			return ValidationResult.Error("--date cannot be combined with --since/--until");
		if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--date: must be YYYY-MM-DD, got '{Date}'");
		DateTime sinceDt = default, untilDt = default;
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out sinceDt))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out untilDt))
			return ValidationResult.Error($"--until: must be YYYY-MM-DD, got '{Until}'");
		if (Since != null && Until != null && sinceDt > untilDt)
			return ValidationResult.Error($"--since ({Since}) must be <= --until ({Until})");
		if (Cadence < 1 || Cadence > 390)
			return ValidationResult.Error($"--cadence: must be in [1, 390], got {Cadence}");
		var outFmt = Output.ToLowerInvariant();
		if (outFmt is not ("console" or "text"))
			return ValidationResult.Error($"--output: must be console or text, got '{Output}'");
		return ValidationResult.Success();
	}
}

/// <summary>Verdict comparing a live watch proposal/fill against the same-day backtest. Declared in increasing
/// severity; <see cref="AIVerifyCommand.ExitCodeFor"/> maps each to the process exit code.</summary>
internal enum VerifyVerdict
{
	/// <summary>Same structure and identical legs (or both sides declined to open).</summary>
	Match,
	/// <summary>A score tie the two quote feeds broke differently (opposite-side ATM, adjacent long strike, adjacent long or short expiry). Not a pass, not a defect.</summary>
	NearTie,
	/// <summary>Cascade only: the same setup confirmed on a different minute of the backtest's cadence grid.</summary>
	CadenceOffset,
	/// <summary>Same structure, expiries, sides and call/put on every leg; only strikes differ, beyond the tie tolerance.</summary>
	StrikeShift,
	/// <summary>Same structure and expiries, but a leg changed call/put or buy/sell.</summary>
	LegFlip,
	/// <summary>The day has no quotes in the store yet, so there is no backtest side to compare.</summary>
	NoBackfill,
	/// <summary>Structure or expiry divergence, an open on one side only, or an early backtest open.</summary>
	Mismatch
}

internal sealed record LiveLegQuote(decimal? Bid, decimal? Ask)
{
	public decimal? Mid => Bid.HasValue && Ask.HasValue ? (Bid.Value + Ask.Value) / 2m : Bid ?? Ask;
}

internal sealed record LiveWatchProposal(
	DateTime TimestampEt,
	string Structure,
	int Qty,
	IReadOnlyList<VerifyLeg> Legs,
	decimal Spot,
	decimal NetPerShare,
	decimal? RawScore,
	decimal? FinalScore,
	decimal? RepIv,
	CascadeRegime? CascadeSetup,
	int? CascadeScore,
	IReadOnlyList<decimal> Breakevens,
	IReadOnlyDictionary<string, LiveLegQuote> LegQuotes,
	decimal? Pop = null,
	decimal? Ev = null);

internal sealed record VerifyLeg(string Action, string Symbol, decimal? PricePerShare);

/// <summary>One day of watch activity for the verified strategy: the live opens that count as picks, and every minute
/// the watch evaluated (any record type) — the latter gates the early-open check, since a minute the watch never saw
/// cannot be a divergence.</summary>
internal sealed record WatchDay(List<LiveWatchProposal> Proposals, SortedSet<TimeSpan> EvalMinutes);

internal sealed record VerifyRow(
	DateOnly Date,
	string? SetupLabel,
	LiveWatchProposal? LiveProposal,
	BacktestFill? BacktestOpen,
	BacktestFill? BacktestClose,
	VerifyVerdict Verdict,
	string Note,
	PathPnl? BacktestPickPnl,
	BacktestFill? BacktestEarlyOpen = null,
	PathPnl? LivePickPnl = null,
	string? LivePickNote = null);

/// <summary>One pick's P&L per lot, followed through the backtest's exit rules from its entry until it exits or the last quoted
/// session. <see cref="Open"/> = still held at that session, so <see cref="PerLot"/> includes the final mark.</summary>
internal sealed record PathPnl(decimal PerLot, bool Open, DateTime? ExitEt, string? ExitRule);

/// <summary>Runs the single-day, one-lot backtest the verifier compares against. Holds the shared offline caches so
/// each day (and each pinned/unpinned variant of a day) is one call.</summary>
internal sealed class VerifyBacktestHarness(
	AIConfig config, IBacktestQuoteSource quotes, HistoricalBarCache bars, HistoricalPriceCache closes,
	IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>> dividendsByRoot, IReadOnlyList<(DateTime Date, double Rate)> rateHistory,
	IntradayBarCache intradayBars, int cascadeCadenceMinutes)
{
	// Fixed one-lot sizing ignores cash, so the book's starting cash only has to be positive.
	private const decimal BookCash = 50_000m;

	/// <summary>Backtests <paramref name="day"/>. <paramref name="openAfter"/>, when set, withholds opens until that ET
	/// minute (the backtest's <c>--open-after</c>) so the backtest evaluates the same bar live first opened on.</summary>
	public async Task<BacktestResult> RunDayAsync(DateTime day, TimeSpan? openAfter, CancellationToken cancellation)
	{
		var savedEarliest = config.Opener.EarliestEntryTimeEt;
		if (openAfter.HasValue) config.Opener.EarliestEntryTimeEt = openAfter.Value.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
		try
		{
			return await RunAsync(day, day, replayOpens: null, cancellation);
		}
		finally
		{
			config.Opener.EarliestEntryTimeEt = savedEarliest;
		}
	}

	/// <summary>Books <paramref name="open"/> alone (opener off — the backtest's <c>--replay</c> mode) and manages it with the strategy's
	/// exit rules from its entry day through <paramref name="through"/>, at one lot. A run of its own, so a pick never merges into
	/// another as an add-to-held.</summary>
	public async Task<PathPnl> FollowAsync(ProposalReplayOpen open, DateTime through, CancellationToken cancellation)
	{
		var result = await RunAsync(open.OpenEt.Date, through, [open], cancellation);
		var exit = result.HasOpenAtEnd ? null : result.Fills.LastOrDefault(f => f.Kind is BacktestFillKind.Close or BacktestFillKind.Expire);
		return new PathPnl(result.TotalPnL, result.HasOpenAtEnd, exit?.Date, exit != null ? exit.RuleName ?? exit.Kind.ToString() : null);
	}

	private async Task<BacktestResult> RunAsync(DateTime from, DateTime through, IReadOnlyList<ProposalReplayOpen>? replayOpens, CancellationToken cancellation)
	{
		var book = new SimulatedBook(BookCash, SimulatedBook.DefaultFeePerContractFor(config.Ticker), config.Opener.RealizedExpectancy);
		var positions = new BacktestPositionSource(book, quotes);
		var runner = new BacktestRunner(
			config, book, positions, quotes, bars, closes, topNPerStep: 1, fixedContracts: 1,
			pricingMode: SuggestionPricing.Mid, scanStride: 1,
			dividendsByRoot: dividendsByRoot, replayOpens: replayOpens, rateHistory: rateHistory, intradayBars: intradayBars,
			cascadeCadenceMinutes: cascadeCadenceMinutes, cascadeRecache: false);
		using var deferral = ConsoleDeferral.Begin();
		return await runner.RunAsync(from, through, cancellation);
	}
}

/// <summary>
/// <c>wa ai verify</c> — first-class live-vs-backtest fidelity verifier: how faithfully the backtest reproduces the live watch's
/// PROPOSALS, at the size they proposed, for scored strategies (e.g. <c>SPY.DC2</c>) and cascade strategies (e.g.
/// <c>SPXW.CASCADE</c>) — entry/strike/expiry alignment, cascade cadence offsets, and each pick's P&L to exit. Broker fills and
/// trades placed or closed by hand are deliberately not read: they measure the operator, not the backtest's fidelity.
/// The backtest runs at a fixed one lot so its P&L compares to live per lot regardless of sizing. For scored
/// strategies the backtest is pinned to live's first-open minute; on a late-entry day an unpinned run also
/// checks whether the backtest would have opened earlier (EARLY OPEN = mismatch). Each scored day's two picks — live's, as the
/// backtest's <c>--replay</c> books it, and the backtest's own — are then followed to their exits through the last quoted session on
/// the same engine, quotes and exit rules, so their per-lot P&L gap is what the different pick cost; scaled by the
/// proposal's quantity it reads in the dollars the proposal would have meant.
/// Exit code: the most severe verdict over the window — 0 match, 3 near-tie/cadence, 5 strike-shift,
/// 4 leg-flip, 2 could not run (no backfill / no config / no watch activity), 1 mismatch.
/// </summary>
internal sealed class AIVerifyCommand : AsyncCommand<AIVerifySettings>
{
	private static readonly Regex CascadeRationaleRegex = new(@"cascade\s+(BULL TRAP|BEAR TRAP|BULL CASCADE|BEAR CASCADE|NEUTRAL)\s+(\d+)/100", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex RepIvRegex = new(@"rep IV\s+([0-9.]+)%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>Two finalScores within this fraction of the larger are a tie. Relative only: scores scale with the strategy (DC2's gate is
	/// 0.0007), so an absolute floor would make every comparison a tie. Feed noise alone moves the score of the SAME trade ~8% between
	/// the live vendor and ThetaData (SPY.DC2 2026-11-12: 0.00076 live vs 0.00070 bt on identical legs).</summary>
	private const decimal ScoreTieTolerance = 0.25m;
	/// <summary>Calendar days within which two long expiries count as neighboring listings (monthly-vs-EOM listings can sit 10 days apart).</summary>
	private const int LongExpiryBandDays = 14;
	/// <summary>Calendar days within which two short expiries count as neighboring listings (SPY's M/W/F weeklies sit 1-3 days apart).</summary>
	private const int ShortExpiryBandDays = 3;

	internal const int ExitCouldNotRun = 2;

	protected override async Task<int> ExecuteAsync(CommandContext context, AIVerifySettings settings, CancellationToken cancellation)
	{
		if (string.Equals(settings.Output, "text", StringComparison.OrdinalIgnoreCase))
		{
			var path = Path.GetFullPath(settings.OutputPath ?? $"WebullAnalytics_AIVerify_{DateTime.Now:yyyyMMdd}.txt");
			return await TextFileExporter.CaptureToFileAsync(path, "AI verify output exported to", () => RunAsync(settings, cancellation));
		}
		return await RunAsync(settings, cancellation);
	}

	private static async Task<int> RunAsync(AIVerifySettings settings, CancellationToken cancellation)
	{
		var (since, until) = ResolveDateWindow(settings);
		var ticker = settings.Ticker.Trim().ToUpperInvariant();
		var quoteDbPath = settings.QuoteDb ?? Program.ResolvePath("data/quotes.db");
		if (!File.Exists(quoteDbPath))
		{
			Console.Error.WriteLine($"Error: SQLite quote store not found at '{quoteDbPath}'. Run the daily backfill first.");
			return ExitCouldNotRun;
		}

		// 1. Load merged config for the requested ticker + strategy (--strategy, else the ticker's defaultStrategy).
		var config = LoadMergedConfig(ticker, settings.Strategy);
		if (config == null)
			return ExitCouldNotRun;
		var strategy = config.Strategy;

		// 2. Load watch proposal activity for this strategy in [since, until].
		var propPath = ProposalLog.ResolvedPath(ticker, strategy);
		var watchDays = LoadWatchActivity(propPath, since, until, config.Opener);
		if (watchDays.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No watch proposal activity found for {Markup.Escape(ticker)}.{Markup.Escape(strategy)} in {since:yyyy-MM-dd} → {until:yyyy-MM-dd} ({Markup.Escape(propPath)}).[/]");
			return ExitCouldNotRun;
		}

		// 3. Shared offline caches.
		var bars = new HistoricalBarCache(offline: true);
		var closes = new HistoricalPriceCache(bars);
		var smile = new SmileIndexCache(offline: true);
		var dividends = new HistoricalDividendCache(offline: true);
		var rateHistory = await new HistoricalRateCache(offline: true).GetAsync(cancellation);
		var oiCache = new ChainSnapshotOiCache();
		// Picks are followed to their exit past the verified window, so the store spans through the most recent completed session.
		var followCap = MaxDate(until, MostRecentCompletedSession());
		var quoteStore = new QuoteStoreCache(quoteDbPath, since: since, until: followCap, sameDayExpiryOnly: false);
		var intradayBars = new IntradayBarCache(BacktestRunner.NoopIntradayFetcher);

		var isCascade = config.Opener.Cascade.Enabled;
		var dividendsByRoot = await dividends.BuildScheduleMapAsync(config.TickerSet(), cancellation);
		var ivProvider = new BacktestIVProvider(bars, ivHvPremium: 1.15m, smileEnabled: true, smile: smile);
		var parametric = new BacktestQuoteSource(bars, ivProvider, dividendsByRoot: dividendsByRoot, oiCache: oiCache);
		IBacktestQuoteSource quotes = new QuotesQuoteSource(
			bars, quoteStore, parametric, dividendsByRoot: dividendsByRoot, oiCache: oiCache,
			openerConfig: config.Opener, defaultIv: config.Indicators.IvDefaultPct, strikeStep: config.Indicators.StrikeStep, intradayBars: intradayBars);
		var harness = new VerifyBacktestHarness(config, quotes, bars, closes, dividendsByRoot, rateHistory, intradayBars, settings.Cadence);
		var followThrough = LastQuotedSession(quoteStore, ticker, until, followCap);

		// Live picks as the backtest's --replay books them (the executor's submit price per order group, no entry slippage).
		var (liveReplayOpens, liveReplayWarnings) = isCascade ? ([], []) : ProposalReplayLoader.Load(propPath, since, until, config.Opener);
		var liveReplayByDate = liveReplayOpens.GroupBy(o => DateOnly.FromDateTime(o.OpenEt)).ToDictionary(g => g.Key, g => g.First());

		var allRows = new List<VerifyRow>();
		foreach (var (date, watchDay) in watchDays.OrderBy(kv => kv.Key))
		{
			var dayDt = date.ToDateTime(TimeOnly.MinValue);
			var dayProposals = watchDay.Proposals;

			if (!quoteStore.HasAnyQuoteInWindow(ticker, dayDt, dayDt))
			{
				var firstProp = dayProposals.FirstOrDefault();
				allRows.Add(new VerifyRow(date, firstProp?.CascadeSetup?.ToString(), firstProp, null, null, VerifyVerdict.NoBackfill, "no quotes yet (evening backfill)", null));
				continue;
			}

			if (isCascade)
			{
				var btResult = await harness.RunDayAsync(dayDt, null, cancellation);
				allRows.AddRange(await CompareCascadeDayAsync(date, ticker, dayProposals, btResult, rateHistory, cancellation));
				continue;
			}

			// Scored: pin the backtest to live's first-open minute so both sides evaluate the same bar. The pin can only push the
			// backtest later, so on a late-entry day also run it unpinned to catch a backtest that would have opened earlier.
			var live = dayProposals.FirstOrDefault();
			var liveMinute = live != null ? MinuteOf(live.TimestampEt) : (TimeSpan?)null;
			var pinned = await harness.RunDayAsync(dayDt, liveMinute, cancellation);
			var unpinned = liveMinute > FirstEntryMinute(config.Opener) ? await harness.RunDayAsync(dayDt, null, cancellation) : null;
			var row = CompareScoredDay(date, live, watchDay.EvalMinutes, pinned, unpinned, config.Indicators.StrikeStep);
			allRows.Add(await FollowPicksAsync(row, live, liveReplayByDate, liveReplayWarnings, harness, followThrough, cancellation));
		}

		AIVerifyRenderer.Render(since, until, ticker, strategy, allRows, followThrough, settings.Detail);

		return ExitCodeFor(allRows.Select(r => r.Verdict).DefaultIfEmpty(VerifyVerdict.Match).Max());
	}

	internal static int ExitCodeFor(VerifyVerdict verdict) => verdict switch
	{
		VerifyVerdict.Match => 0,
		VerifyVerdict.NearTie or VerifyVerdict.CadenceOffset => 3,
		VerifyVerdict.StrikeShift => 5,
		VerifyVerdict.LegFlip => 4,
		VerifyVerdict.NoBackfill => ExitCouldNotRun,
		_ => 1
	};

	private static (DateTime Since, DateTime Until) ResolveDateWindow(AIVerifySettings settings)
	{
		if (settings.Date != null)
		{
			var d = DateTime.ParseExact(settings.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
			return (d, d);
		}
		if (settings.Since != null)
		{
			var s = DateTime.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture);
			var u = settings.Until != null
				? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture)
				: s;
			return (s, u);
		}
		var defaultDay = MostRecentCompletedSession();
		return (defaultDay, defaultDay);
	}

	/// <summary>The most recent completed trading session: today from 19:00 ET (after the evening backfill), else the previous session.</summary>
	private static DateTime MostRecentCompletedSession()
	{
		var nowEt = MarketTime.NowEt();
		return nowEt.TimeOfDay >= new TimeSpan(19, 0, 0) && MarketCalendar.IsOpen(nowEt.Date)
			? nowEt.Date
			: MarketCalendar.PreviousOpenOnOrBefore(nowEt.Date.AddDays(-1));
	}

	private sealed class VerifySingleTickerShim : AISingleTickerSubcommandSettings { }

	private static AIConfig? LoadMergedConfig(string ticker, string? strategy)
	{
		var shim = new VerifySingleTickerShim { Ticker = ticker, Strategy = strategy };
		return AIContext.ResolveConfig(shim);
	}

	private static TimeSpan MinuteOf(DateTime t) => new(t.Hour, t.Minute, 0);

	/// <summary>The first minute the strategy may open: the RTH open, or the entry window's start when later.</summary>
	private static TimeSpan FirstEntryMinute(OpenerConfig opener) =>
		ParsingHelpers.TryParseClockTime(opener.EarliestEntryTimeEt, out var earliest) && earliest > Pricing.OptionMath.MarketOpen ? earliest : Pricing.OptionMath.MarketOpen;

	private static Dictionary<DateOnly, WatchDay> LoadWatchActivity(string path, DateTime since, DateTime until, OpenerConfig opener)
	{
		var days = new Dictionary<DateOnly, WatchDay>();
		if (!File.Exists(path)) return days;

		var isCascade = opener.Cascade.Enabled;
		foreach (var line in SharedFileReader.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			JsonDocument doc;
			try { doc = JsonDocument.Parse(line); }
			catch (JsonException) { continue; }

			using (doc)
			{
				var root = doc.RootElement;
				// `wa ai scan` writes to the same log; only watch records reflect what live did.
				if (!string.Equals(Str(root, "mode"), "watch", StringComparison.OrdinalIgnoreCase)) continue;
				var tsEt = ParseEasternTs(Str(root, "ts"));
				if (tsEt == null || tsEt.Value.Date < since.Date || tsEt.Value.Date > until.Date) continue;

				var d = DateOnly.FromDateTime(tsEt.Value);
				if (!days.TryGetValue(d, out var day))
					days[d] = day = new WatchDay([], []);
				day.EvalMinutes.Add(MinuteOf(tsEt.Value));

				var proposal = ParseLiveOpen(root, tsEt.Value, opener);
				if (proposal == null) continue;

				// Cascade: keep the first qualifying open proposal per cascade setup per day.
				if (isCascade && day.Proposals.Any(p => p.CascadeSetup == proposal.CascadeSetup)) continue;
				day.Proposals.Add(proposal);
			}
		}

		// Scored: the live pick is the top-finalScore open at the first evaluation minute (what the opener submits).
		if (!isCascade)
			foreach (var day in days.Values)
				if (PickScoredLive(day.Proposals) is { } pick)
				{
					day.Proposals.Clear();
					day.Proposals.Add(pick);
				}

		return days;
	}

	private static LiveWatchProposal? PickScoredLive(IReadOnlyList<LiveWatchProposal> opens)
	{
		if (opens.Count == 0) return null;
		var firstMinute = opens.Min(p => MinuteOf(p.TimestampEt));
		return opens.Where(p => MinuteOf(p.TimestampEt) == firstMinute).MaxBy(p => p.FinalScore ?? decimal.MinValue);
	}

	/// <summary>Parses one watch record into a live open, or null when it is not one the watch would submit: not an open,
	/// outside RTH or the entry window, informational, cash-reserve blocked, zero qty, or legless.</summary>
	private static LiveWatchProposal? ParseLiveOpen(JsonElement root, DateTime tsEt, OpenerConfig opener)
	{
		var type = Str(root, "type") ?? "open";
		if (!string.Equals(type, "open", StringComparison.OrdinalIgnoreCase)) return null;
		if (tsEt.TimeOfDay < Pricing.OptionMath.MarketOpen || tsEt.TimeOfDay >= Pricing.OptionMath.MarketClose) return null;
		if (!opener.IsWithinEntryWindow(tsEt.TimeOfDay)) return null;
		if (root.TryGetProperty("informational", out var inf) && inf.ValueKind == JsonValueKind.True) return null;
		if (root.TryGetProperty("cashReserveBlocked", out var crb) && crb.ValueKind == JsonValueKind.True) return null;
		var qty = (int)(Dec(root, "qty") ?? 0m);
		if (qty < 1) return null;

		var legQuotes = ParseLegQuotes(root);
		var legs = ParseProposalLegs(root, legQuotes);
		if (legs.Count == 0) return null;

		var netPerShare = (Dec(root, "cashImpactPerContract") ?? 0m) / 100m; // signed: -debit, +credit
		var spot = root.TryGetProperty("diagnostic", out var diag) && diag.ValueKind == JsonValueKind.Object ? Dec(diag, "spotAtEvaluation") ?? 0m : 0m;
		var rationale = Str(root, "rationale") ?? "";
		decimal? repIv = null;
		var ivMatch = RepIvRegex.Match(rationale);
		if (ivMatch.Success && decimal.TryParse(ivMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var ivPct))
			repIv = ivPct / 100m;

		CascadeRegime? cascadeSetup = null;
		int? cascadeScore = null;
		var cm = CascadeRationaleRegex.Match(rationale);
		if (cm.Success)
		{
			cascadeSetup = ParseCascadeRegime(cm.Groups[1].Value);
			if (int.TryParse(cm.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc))
				cascadeScore = sc;
		}

		return new LiveWatchProposal(
			tsEt, Str(root, "structure") ?? "", qty, legs, spot, netPerShare, Dec(root, "rawScore"), Dec(root, "finalScore"), repIv,
			cascadeSetup, cascadeScore, ParseBreakevens(root), legQuotes, Dec(root, "pop"), Dec(root, "ev"));
	}

	private static BacktestFill? FirstOpen(BacktestResult result) => result.Fills.FirstOrDefault(f => f.Kind == BacktestFillKind.Open);

	private static BacktestFill? TerminalFill(BacktestResult result, BacktestFill open) =>
		result.Fills.LastOrDefault(f => f.LineageId == open.LineageId && f.Kind is BacktestFillKind.Close or BacktestFillKind.Expire);

	private static VerifyRow CompareScoredDay(
		DateOnly date, LiveWatchProposal? live, SortedSet<TimeSpan> evalMinutes,
		BacktestResult pinned, BacktestResult? unpinned, decimal strikeStep)
	{
		var btOpen = FirstOpen(pinned);
		var btClose = btOpen != null ? TerminalFill(pinned, btOpen) : null;
		var early = FindEarlyOpen(live, unpinned != null ? FirstOpen(unpinned) : null, evalMinutes);

		var (verdict, detail) = (live, btOpen) switch
		{
			(null, null) => (VerifyVerdict.Match, "no trade either side"),
			(null, _) => (VerifyVerdict.Mismatch, $"bt only {btOpen!.Date:HH:mm}"),
			(_, null) => (VerifyVerdict.Mismatch, $"live only {live.TimestampEt:HH:mm}"),
			_ => ClassifyScoredComparison(live!, btOpen!, strikeStep)
		};

		// The pinned comparison only holds from live's entry minute on; a backtest that would have opened earlier traded a different day.
		if (early != null)
			(verdict, detail) = (VerifyVerdict.Mismatch, $"early: bt {early.Date:HH:mm}, pinned {(verdict == VerifyVerdict.Match ? "match" : detail)}");

		return new VerifyRow(date, null, live, btOpen, btClose, verdict, detail, null, early);
	}

	/// <summary>The unpinned backtest's open when it fired before live's first open AND the live watch evaluated some minute in
	/// between (a watch that started after the backtest's open minute never saw it, which is not a divergence); else null.</summary>
	internal static BacktestFill? FindEarlyOpen(LiveWatchProposal? live, BacktestFill? unpinnedOpen, SortedSet<TimeSpan> evalMinutes)
	{
		if (live == null || unpinnedOpen is not { } open) return null;
		var btMinute = MinuteOf(open.Date);
		var liveMinute = MinuteOf(live.TimestampEt);
		return btMinute < liveMinute && evalMinutes.Any(m => m >= btMinute && m < liveMinute) ? open : null;
	}

	/// <summary>Follows both picks of a scored row to their exits: live's pick as the backtest's --replay books it, and the backtest's own
	/// (pinned) pick as it booked it. Same engine, quotes and exit rules on both, so the P&L gap is what the different pick cost.</summary>
	private static async Task<VerifyRow> FollowPicksAsync(
		VerifyRow row, LiveWatchProposal? live, IReadOnlyDictionary<DateOnly, ProposalReplayOpen> liveReplayByDate, IReadOnlyList<string> liveReplayWarnings,
		VerifyBacktestHarness harness, DateTime through, CancellationToken cancellation)
	{
		PathPnl? livePnl = null;
		string? liveNote = null;
		if (live != null)
		{
			if (liveReplayByDate.TryGetValue(row.Date, out var liveOpen))
				livePnl = await harness.FollowAsync(liveOpen, through, cancellation);
			else
				liveNote = liveReplayWarnings.FirstOrDefault(w => w.Contains(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)) ?? "live pick could not be replayed";
		}
		var btPnl = row.BacktestOpen != null ? await harness.FollowAsync(FillsReplayLoader.FromOpenFill(row.BacktestOpen), through, cancellation) : null;
		return row with { LivePickPnl = livePnl, BacktestPickPnl = btPnl, LivePickNote = liveNote };
	}

	private static DateTime MaxDate(DateTime a, DateTime b) => a > b ? a : b;

	/// <summary>The latest session in [<paramref name="from"/>, <paramref name="to"/>] with quotes in the store — how far a pick can be followed.</summary>
	private static DateTime LastQuotedSession(QuoteStoreCache store, string ticker, DateTime from, DateTime to)
	{
		for (var d = to.Date; d > from.Date; d = d.AddDays(-1))
			if (MarketCalendar.IsOpen(d) && store.HasAnyQuoteInWindow(ticker, d, d))
				return d;
		return from.Date;
	}

	private static async Task<List<VerifyRow>> CompareCascadeDayAsync(
		DateOnly date, string ticker,
		List<LiveWatchProposal> dayProposals,
		BacktestResult btResult, IReadOnlyList<(DateTime Date, double Rate)> rateHistory,
		CancellationToken cancellation)
	{
		var rows = new List<VerifyRow>();
		var btOpens = btResult.Fills.Where(f => f.Kind == BacktestFillKind.Open).OrderBy(f => f.Date).ToList();

		if (dayProposals.Count == 0 && btOpens.Count == 0)
		{
			rows.Add(new VerifyRow(date, "—", null, null, null, VerifyVerdict.Match, "no trigger either side", null));
			return rows;
		}

		// Pair live proposals and backtest opens by inferred setup / structure kind.
		var matchedBt = new HashSet<long>();
		var dayDt = date.ToDateTime(TimeOnly.MinValue);
		double? dayRate = rateHistory.Where(r => r.Date.Date <= dayDt).Select(r => (double?)r.Rate).LastOrDefault();
		CascadeReplaySession? replaySession = null;

		foreach (var live in dayProposals)
		{
			var btOpen = btOpens.FirstOrDefault(b => !matchedBt.Contains(b.LineageId) && string.Equals(b.StrategyKind, live.Structure, StringComparison.OrdinalIgnoreCase));
			if (btOpen != null) matchedBt.Add(btOpen.LineageId);

			var btClose = btOpen != null ? TerminalFill(btResult, btOpen) : null;
			var btPnl = DayPathPnl(btResult, btOpen, btClose);

			if (btOpen != null)
			{
				var sameLegs = SameLegSet(live.Legs, btOpen.Legs);
				var minuteDiff = Math.Abs((btOpen.Date - live.TimestampEt).TotalMinutes);
				VerifyVerdict verdict;
				string detail;
				if (sameLegs && minuteDiff <= 2.0)
				{
					verdict = VerifyVerdict.Match;
					detail = "";
				}
				else if (minuteDiff <= 15.0)
				{
					// Check exact minute via offline CascadeReplaySession to see if the signal itself confirmed at live's minute.
					var exactNote = await CheckExactMinuteCascadeAsync(ticker, dayDt, live, dayRate, () => replaySession, s => replaySession = s, cancellation);
					verdict = VerifyVerdict.CadenceOffset;
					detail = $"grid {(btOpen.Date - live.TimestampEt).TotalMinutes:+0;-0}m{exactNote}";
				}
				else
				{
					// Beyond one cadence step plus slack the signal confirmed at a genuinely different time: a timing divergence, not a leg flip.
					verdict = VerifyVerdict.Mismatch;
					detail = $"{Math.Round(minuteDiff)}m apart";
				}

				rows.Add(new VerifyRow(date, live.CascadeSetup?.ToString() ?? live.Structure, live, btOpen, btClose, verdict, detail, btPnl));
			}
			else
			{
				var exactNote = await CheckExactMinuteCascadeAsync(ticker, dayDt, live, dayRate, () => replaySession, s => replaySession = s, cancellation);
				rows.Add(new VerifyRow(date, live.CascadeSetup?.ToString() ?? live.Structure, live, null, null, VerifyVerdict.Mismatch, $"live only {live.TimestampEt:HH:mm}{exactNote}", null));
			}
		}

		// Any backtest opens not matched to a live proposal.
		foreach (var btOpen in btOpens.Where(b => !matchedBt.Contains(b.LineageId)))
		{
			var btClose = TerminalFill(btResult, btOpen);
			var btPnl = DayPathPnl(btResult, btOpen, btClose);
			rows.Add(new VerifyRow(date, btOpen.StrategyKind, null, btOpen, btClose, VerifyVerdict.Mismatch, $"bt only {btOpen.Date:HH:mm}", btPnl));
		}

		return rows;
	}

	private static async Task<string> CheckExactMinuteCascadeAsync(
		string ticker, DateTime dayDt, LiveWatchProposal live, double? dayRate,
		Func<CascadeReplaySession?> getSession, Action<CascadeReplaySession?> setSession, CancellationToken ct)
	{
		if (live.CascadeSetup == null) return "";
		try
		{
			var session = getSession();
			if (session == null)
			{
				var cache = new CascadeReplayCache(new());
				var (opened, _) = await CascadeReplaySession.OpenAsync(ticker, dayDt, cache, dayRate, ct);
				session = opened;
				setSession(session);
			}
			if (session == null) return "";
			var read = await session.ReadAsync(new TimeSpan(live.TimestampEt.Hour, live.TimestampEt.Minute, 0), ct);
			if (read == null) return "";
			var confirmed = read.Reading.PrimaryRegime == live.CascadeSetup.Value && read.Reading.Active;
			return confirmed ? $", offline confirms {read.Reading.LeadingSetup.Score}" : $", offline {read.Reading.PrimaryRegime}";
		}
		catch
		{
			return "";
		}
	}

	/// <summary>A structure's legs split by direction: option types present, and the (expiry, strike) of each short and
	/// long leg, each list sorted so the two sides pair leg-for-leg.</summary>
	private sealed record LegSkeleton(HashSet<string> Types, List<(DateTime Expiry, decimal Strike)> Shorts, List<(DateTime Expiry, decimal Strike)> Longs)
	{
		public IEnumerable<DateTime> Expiries => Shorts.Concat(Longs).Select(l => l.Expiry).OrderBy(e => e);
	}

	private static LegSkeleton SkeletonOf(IEnumerable<(string Action, string Symbol)> legs)
	{
		var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var shorts = new List<(DateTime, decimal)>();
		var longs = new List<(DateTime, decimal)>();
		foreach (var (action, symbol) in legs)
		{
			var opt = ParsingHelpers.ParseOptionSymbol(symbol);
			if (opt == null) continue;
			types.Add(opt.CallPut);
			(action.Equals("sell", StringComparison.OrdinalIgnoreCase) ? shorts : longs).Add((opt.ExpiryDate.Date, opt.Strike));
		}
		shorts.Sort();
		longs.Sort();
		return new LegSkeleton(types, shorts, longs);
	}

	/// <summary>Every pair is within <paramref name="maxDays"/> calendar days and one strike step (both lists already sorted).</summary>
	private static bool Adjacent(List<(DateTime Expiry, decimal Strike)> a, List<(DateTime Expiry, decimal Strike)> b, int maxDays, decimal step) =>
		a.Count == b.Count && a.Zip(b).All(p => Math.Abs((p.First.Expiry - p.Second.Expiry).Days) <= maxDays && Math.Abs(p.First.Strike - p.Second.Strike) <= step);

	internal static bool ScoresTie(decimal? live, decimal? bt) =>
		live.HasValue && bt.HasValue && Math.Abs(bt.Value - live.Value) <= ScoreTieTolerance * Math.Max(Math.Abs(live.Value), Math.Abs(bt.Value));

	/// <summary>Names the score tie the two quote feeds broke differently, or null. Each requires the same structure (checked
	/// by the caller) and finalScores within the tie tolerance:
	/// (a) opposite-side ATM tie — opposite call/put, same short strike(s)+expiry and long expiry, spot within one step of every short strike;
	/// (b) adjacent long-strike tie — same side and short leg(s), long leg same expiry within one strike;
	/// (c) adjacent long-expiry tie — same side and short leg(s), long leg a neighboring listed expiry within one strike;
	/// (d) adjacent short-expiry tie — same side, short leg a neighboring listed expiry within one strike, long leg equal or within (c).</summary>
	private static string? ClassifyTie(LegSkeleton live, LegSkeleton bt, decimal? spot, decimal step, bool scoresTie)
	{
		if (!scoresTie || live.Types.Count == 0) return null;
		var sameShort = live.Shorts.SequenceEqual(bt.Shorts);
		var sameSide = live.Types.SetEquals(bt.Types);

		var opposite = !live.Types.Overlaps(bt.Types);
		var sameLongExpiries = live.Longs.Select(l => l.Expiry).SequenceEqual(bt.Longs.Select(l => l.Expiry));
		var nearAtm = spot.HasValue && live.Shorts.Concat(bt.Shorts).All(s => Math.Abs(spot.Value - s.Strike) <= step);
		if (opposite && sameShort && sameLongExpiries && nearAtm)
			return "ATM side tie";

		if (!sameSide) return null;
		if (sameShort && Adjacent(live.Longs, bt.Longs, 0, step))
			return "adj. long strike";
		if (sameShort && Adjacent(live.Longs, bt.Longs, LongExpiryBandDays, step))
			return "adj. long expiry";
		var shortExpiryDiffers = !live.Shorts.Select(s => s.Expiry).SequenceEqual(bt.Shorts.Select(s => s.Expiry));
		if (shortExpiryDiffers && Adjacent(live.Shorts, bt.Shorts, ShortExpiryBandDays, step) && Adjacent(live.Longs, bt.Longs, LongExpiryBandDays, step))
			return "adj. short expiry";
		return null;
	}

	/// <summary>The verdict and a short note naming the divergence (empty for a match). Score and spot deltas are not in the note: the
	/// <c>--detail</c> ledger shows them in their own columns.</summary>
	internal static (VerifyVerdict Verdict, string Note) ClassifyScoredComparison(LiveWatchProposal live, BacktestFill btOpen, decimal strikeStep)
	{
		if (!string.Equals(live.Structure, btOpen.StrategyKind, StringComparison.OrdinalIgnoreCase))
			return (VerifyVerdict.Mismatch, $"bt {btOpen.StrategyKind}");

		if (SameLegSet(live.Legs, btOpen.Legs))
			return (VerifyVerdict.Match, "");

		var step = strikeStep > 0m ? strikeStep : 1m;
		var liveLegs = live.Legs.Select(l => (l.Action, l.Symbol)).ToList();
		var btLegs = btOpen.Legs.Select(l => (Action: l.Side == Side.Buy ? "buy" : "sell", l.Symbol)).ToList();
		var liveSkeleton = SkeletonOf(liveLegs);
		var btSkeleton = SkeletonOf(btLegs);
		var tie = ClassifyTie(liveSkeleton, btSkeleton, live.Spot > 0m ? live.Spot : btOpen.Spot, step, ScoresTie(live.FinalScore, btOpen.FinalScore));
		if (tie != null)
			return (VerifyVerdict.NearTie, tie);

		if (!liveSkeleton.Expiries.SequenceEqual(btSkeleton.Expiries))
			return (VerifyVerdict.Mismatch, "expiries differ");

		// Same expiries. When every paired leg keeps its action, expiry and call/put, only strikes moved: a strike shift, not a
		// leg flip (a flip changes a leg's type or side), and not a match either — the two trades sit at different strikes.
		var liveParsed = ParsePaired(liveLegs);
		var btParsed = ParsePaired(btLegs);
		if (!SameLegShape(liveParsed, btParsed))
			return (VerifyVerdict.LegFlip, !liveSkeleton.Types.Overlaps(btSkeleton.Types) ? $"{TypeName(liveSkeleton.Types)}→{TypeName(btSkeleton.Types)}" : "side flip");

		var shifts = liveParsed.Zip(btParsed, (a, b) => (a.Action, Shift: b.Opt.Strike - a.Opt.Strike)).ToList();
		return (VerifyVerdict.StrikeShift, shifts.Select(s => s.Shift).Distinct().Count() == 1
			? $"strike {shifts[0].Shift:+0.##;-0.##;0}"
			: string.Join(", ", shifts.Select(s => $"{s.Action} {s.Shift:+0.##;-0.##;0}")));
	}

	private static string TypeName(IReadOnlySet<string> types) => types.Count != 1 ? "mixed" : types.Contains("C") ? "call" : "put";

	/// <summary>Legs parsed and sorted by action, expiry, call/put, then strike, so two leg sets pair leg-for-leg.</summary>
	private static List<(string Action, OptionParsed Opt)> ParsePaired(IEnumerable<(string Action, string Symbol)> legs) =>
		legs.Select(l => (Action: l.Action.ToLowerInvariant(), Opt: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.Where(x => x.Opt != null)
			.Select(x => (x.Action, Opt: x.Opt!))
			.OrderBy(x => x.Action, StringComparer.Ordinal)
			.ThenBy(x => x.Opt.ExpiryDate.Date)
			.ThenBy(x => x.Opt.CallPut, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Opt.Strike)
			.ToList();

	/// <summary>Both leg sets (from <see cref="ParsePaired"/>) pair leg-for-leg on action, expiry and call/put; strikes may differ.</summary>
	private static bool SameLegShape(List<(string Action, OptionParsed Opt)> a, List<(string Action, OptionParsed Opt)> b) =>
		a.Count == b.Count && a.Zip(b).All(p =>
			p.First.Action == p.Second.Action &&
			p.First.Opt.ExpiryDate.Date == p.Second.Opt.ExpiryDate.Date &&
			string.Equals(p.First.Opt.CallPut, p.Second.Opt.CallPut, StringComparison.OrdinalIgnoreCase));

	internal static bool SameLegSet(IReadOnlyList<VerifyLeg> a, IReadOnlyList<BacktestLegFill> b)
	{
		if (a.Count != b.Count) return false;
		var setA = a.Select(l => $"{l.Action.ToLowerInvariant()}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		var setB = b.Select(l => $"{(l.Side == Side.Buy ? "buy" : "sell")}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		return setA.SequenceEqual(setB);
	}

	/// <summary>A same-day backtest lineage's P&L per lot (the run is one lot), or null when it did not exit that day.</summary>
	private static PathPnl? DayPathPnl(BacktestResult result, BacktestFill? open, BacktestFill? close) =>
		open != null && close != null ? new PathPnl(ComputeBtLineagePnl(result.Fills, open.LineageId), false, close.Date, close.RuleName ?? close.Kind.ToString()) : null;

	private static decimal ComputeBtLineagePnl(IReadOnlyList<BacktestFill> fills, long lineageId)
	{
		decimal sum = 0m;
		foreach (var f in fills)
			if (f.LineageId == lineageId)
				sum += f.NetCashFlow - f.Fees;
		return sum;
	}

	/// <summary>The opener's captured per-leg NBBO (<c>diagnostic.probe.legQuotes</c>), keyed by OCC symbol.</summary>
	private static Dictionary<string, LiveLegQuote> ParseLegQuotes(JsonElement root)
	{
		var quotes = new Dictionary<string, LiveLegQuote>(StringComparer.OrdinalIgnoreCase);
		if (root.TryGetProperty("diagnostic", out var diag) && diag.ValueKind == JsonValueKind.Object
			&& diag.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object
			&& probe.TryGetProperty("legQuotes", out var lq) && lq.ValueKind == JsonValueKind.Array)
		{
			foreach (var q in lq.EnumerateArray())
			{
				var sym = Str(q, "symbol");
				if (sym == null) continue;
				quotes[sym] = new LiveLegQuote(Dec(q, "bid"), Dec(q, "ask"));
			}
		}
		return quotes;
	}

	private static List<VerifyLeg> ParseProposalLegs(JsonElement root, IReadOnlyDictionary<string, LiveLegQuote> legQuotes)
	{
		var legs = new List<VerifyLeg>();
		if (!root.TryGetProperty("legs", out var legsEl) || legsEl.ValueKind != JsonValueKind.Array)
			return legs;

		foreach (var l in legsEl.EnumerateArray())
		{
			var sym = Str(l, "symbol");
			var act = Str(l, "action");
			if (sym == null || act == null) continue;
			var mid = legQuotes.GetValueOrDefault(sym)?.Mid;
			legs.Add(new VerifyLeg(act, sym, mid > 0m ? mid : null));
		}
		return legs;
	}

	private static List<decimal> ParseBreakevens(JsonElement root)
	{
		var bes = new List<decimal>();
		if (root.TryGetProperty("breakevens", out var el) && el.ValueKind == JsonValueKind.Array)
			foreach (var b in el.EnumerateArray())
				if (b.ValueKind == JsonValueKind.Number)
					bes.Add(b.GetDecimal());
		bes.Sort();
		return bes;
	}

	private static CascadeRegime? ParseCascadeRegime(string label) => label.ToUpperInvariant() switch
	{
		"BULL TRAP" => CascadeRegime.BullTrap,
		"BEAR TRAP" => CascadeRegime.BearTrap,
		"BULL CASCADE" => CascadeRegime.BullCascade,
		"BEAR CASCADE" => CascadeRegime.BearCascade,
		"NEUTRAL" => CascadeRegime.Neutral,
		_ => null
	};

	private static DateTime? ParseEasternTs(string? ts) =>
		!string.IsNullOrWhiteSpace(ts) && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
			? MarketTime.ToEt(dto).DateTime
			: null;

	private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	private static decimal? Dec(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}
