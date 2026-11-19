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
using WebullAnalytics.Report;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI;

internal sealed class AIVerifySettings : CommandSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Ticker to verify (e.g. SPY, SPXW).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--strategy <TOKEN>")]
	[Description("Required strategy token to verify (e.g. DC2, CASCADE). Loads ai-config.<TICKER>.<STRATEGY>.json and its proposal log.")]
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

	[CommandOption("--starting-cash <AMOUNT>")]
	[Description("Starting cash for the single-day backtests. Default: 50000.")]
	public decimal StartingCash { get; set; } = 50000m;

	[CommandOption("--cadence <MINUTES>")]
	[Description("Cascade strategies only: backtest grid cadence in minutes. Default: 10 (matching `wa ai backtest`).")]
	public int Cadence { get; set; } = 10;

	[CommandOption("--source <SOURCE>")]
	[Description("Live fills source: 'api' (data/orders.jsonl, default), 'export' (Webull CSV), or 'none' (proposals only).")]
	public string Source { get; set; } = "api";

	[CommandOption("--quote-db <PATH>")]
	[Description("Override the SQLite quote-store path (default data/quotes.db).")]
	public string? QuoteDb { get; set; }

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
		if (string.IsNullOrWhiteSpace(Strategy))
			return ValidationResult.Error("--strategy is required (e.g. --strategy DC2 or --strategy CASCADE)");
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
		if (StartingCash <= 0m)
			return ValidationResult.Error($"--starting-cash: must be > 0, got {StartingCash}");
		if (Cadence < 1 || Cadence > 390)
			return ValidationResult.Error($"--cadence: must be in [1, 390], got {Cadence}");
		var src = Source.ToLowerInvariant();
		if (src is not ("api" or "export" or "none"))
			return ValidationResult.Error($"--source: must be api, export, or none, got '{Source}'");
		var outFmt = Output.ToLowerInvariant();
		if (outFmt is not ("console" or "text"))
			return ValidationResult.Error($"--output: must be console or text, got '{Output}'");
		return ValidationResult.Success();
	}
}

/// <summary>Verdict comparing a live watch proposal/fill against the same-day backtest.</summary>
internal enum VerifyVerdict
{
	Match,
	NearTie,
	LegFlip,
	CadenceOffset,
	Mismatch,
	NoBackfill
}

internal sealed record LiveWatchProposal(
	DateTime TimestampEt,
	string Ticker,
	string Strategy,
	string Structure,
	int Qty,
	IReadOnlyList<VerifyLeg> Legs,
	decimal Spot,
	decimal NetPerShare,
	decimal? RawScore,
	decimal? FinalScore,
	decimal? RepIv,
	CascadeRegime? CascadeSetup,
	int? CascadeScore);

internal sealed record VerifyLeg(string Action, string Symbol, decimal? PricePerShare);

internal sealed record LiveBrokerFill(
	DateTime TimestampEt,
	string Ticker,
	int Qty,
	IReadOnlyList<VerifyLeg> Legs,
	decimal NetPerShare,
	decimal Fees = 0m,
	DateTime? ExitTimeEt = null,
	decimal? ExitNetPerShare = null,
	decimal? RealizedPnl = null);

internal sealed record VerifyRow(
	DateOnly Date,
	string Ticker,
	string Strategy,
	string? SetupLabel,
	LiveWatchProposal? LiveProposal,
	LiveBrokerFill? LiveFill,
	BacktestFill? BacktestOpen,
	BacktestFill? BacktestClose,
	VerifyVerdict Verdict,
	string Detail,
	decimal? FillSlippagePerShare,
	decimal? LiveRealizedPnl,
	decimal? BacktestRealizedPnl);

/// <summary>
/// <c>wa ai verify</c> — first-class live-vs-backtest fidelity verifier. Compares live watch proposals and
/// actual broker fills (<c>data/orders.jsonl</c>) against the offline backtest for both scored strategies
/// (e.g. <c>SPY.DC2</c>) and cascade strategies (e.g. <c>SPXW.CASCADE</c>), reporting entry/strike/expiry
/// alignment, fill slippage vs. quote-store mid, cascade cadence offsets, and realized exit P&amp;L.
/// </summary>
internal sealed class AIVerifyCommand : AsyncCommand<AIVerifySettings>
{
	private static readonly Regex CascadeRationaleRegex = new(@"cascade\s+(BULL TRAP|BEAR TRAP|BULL CASCADE|BEAR CASCADE|NEUTRAL)\s+(\d+)/100", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex RepIvRegex = new(@"rep IV\s+([0-9.]+)%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private const decimal NearTieScoreTolerance = 0.0005m;

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
		var strategy = settings.Strategy!.Trim();
		var quoteDbPath = settings.QuoteDb ?? Program.ResolvePath("data/quotes.db");
		if (!File.Exists(quoteDbPath))
		{
			Console.Error.WriteLine($"Error: SQLite quote store not found at '{quoteDbPath}'. Run the daily backfill first.");
			return 1;
		}

		// 1. Load merged config for the requested ticker + strategy.
		var config = LoadMergedConfig(ticker, strategy);
		if (config == null)
			return 1;

		// 2. Load watch proposal activity for this strategy in [since, until].
		var propPath = ProposalLog.ResolvedPath(ticker, strategy);
		var (watchDates, liveProposalsByDate) = LoadWatchActivity(propPath, since, until, config.Opener);
		if (watchDates.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No watch proposal activity found for {Markup.Escape(ticker)}.{Markup.Escape(strategy)} in {since:yyyy-MM-dd} → {until:yyyy-MM-dd} ({Markup.Escape(propPath)}).[/]");
			return 0;
		}

		// 3. Load actual broker fills for this ticker (data/orders.jsonl) unless --source none.
		var brokerFills = LoadBrokerFills(settings.Source, ticker, since, until);

		// 4. Shared offline caches.
		var bars = new HistoricalBarCache(offline: true);
		var closes = new HistoricalPriceCache(bars);
		var smile = new SmileIndexCache(offline: true);
		var dividends = new HistoricalDividendCache(offline: true);
		var rateHistory = await new HistoricalRateCache(offline: true).GetAsync(cancellation);
		var oiCache = new ChainSnapshotOiCache();
		var quoteStore = new QuoteStoreCache(quoteDbPath, since: since, until: until, sameDayExpiryOnly: false);
		var intradayBars = new IntradayBarCache(BacktestRunner.NoopIntradayFetcher);

		var isCascade = config.Opener.Cascade.Enabled;
		var dividendsByRoot = await dividends.BuildScheduleMapAsync(config.TickerSet(), cancellation);
		var ivProvider = new BacktestIVProvider(bars, ivHvPremium: 1.15m, smileEnabled: true, smile: smile);
		var parametric = new BacktestQuoteSource(bars, ivProvider, dividendsByRoot: dividendsByRoot, oiCache: oiCache);
		IBacktestQuoteSource quotes = new QuotesQuoteSource(
			bars, quoteStore, parametric, dividendsByRoot: dividendsByRoot, oiCache: oiCache,
			openerConfig: config.Opener, defaultIv: config.Indicators.IvDefaultPct, strikeStep: config.Indicators.StrikeStep, intradayBars: intradayBars);

		var feePerContract = SimulatedBook.DefaultFeePerContractFor(ticker);
		var allRows = new List<VerifyRow>();

		foreach (var date in watchDates.OrderBy(d => d))
		{
			var dayDt = date.ToDateTime(TimeOnly.MinValue);
			liveProposalsByDate.TryGetValue(date, out var dayProposals);
			dayProposals ??= [];

			var dayFills = brokerFills
				.Where(f => f.TimestampEt.Date == dayDt)
				.OrderBy(f => f.TimestampEt)
				.ToList();

			if (!quoteStore.HasAnyQuoteInWindow(ticker, dayDt, dayDt))
			{
				var firstProp = dayProposals.FirstOrDefault();
				var firstFill = firstProp != null ? MatchBrokerFill(firstProp, dayFills) : dayFills.FirstOrDefault();
				allRows.Add(new VerifyRow(
					date, ticker, strategy, firstProp?.CascadeSetup?.ToString(),
					firstProp, firstFill, null, null,
					VerifyVerdict.NoBackfill,
					"no quotes in data/quotes.db for this date yet (run evening backfill)",
					null, firstFill?.RealizedPnl, null));
				continue;
			}

			// Run single-day backtest for this strategy on `date`.
			var book = new SimulatedBook(settings.StartingCash, feePerContract, config.Opener.RealizedExpectancy);
			var positions = new BacktestPositionSource(book, quotes);
			var runner = new BacktestRunner(
				config, book, positions, quotes, bars, closes, topNPerStep: 1,
				pricingMode: SuggestionPricing.Mid, scanStride: 1,
				dividendsByRoot: dividendsByRoot, rateHistory: rateHistory, intradayBars: intradayBars,
				cascadeCadenceMinutes: settings.Cadence, cascadeRecache: false);

			BacktestResult btResult;
			using (var deferral = ConsoleDeferral.Begin())
			{
				btResult = await runner.RunAsync(dayDt, dayDt, cancellation);
			}

			if (isCascade)
			{
				var cascadeRows = await CompareCascadeDayAsync(
					date, ticker, strategy, dayProposals, dayFills, btResult,
					rateHistory, cancellation);
				allRows.AddRange(cascadeRows);
			}
			else
			{
				var scoredRow = CompareScoredDay(date, ticker, strategy, dayProposals, dayFills, btResult, config);
				allRows.Add(scoredRow);
			}
		}

		RenderReport(since, until, allRows);
		return allRows.Any(r => r.Verdict == VerifyVerdict.Mismatch) ? 1 : 0;
	}

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
		// Default: most recent completed trading session (yesterday if today is before 19:00 ET, else today).
		var nowEt = MarketTime.NowEt();
		var defaultDay = nowEt.TimeOfDay >= new TimeSpan(19, 0, 0) && MarketCalendar.IsOpen(nowEt.Date)
			? nowEt.Date
			: MarketCalendar.PreviousOpenOnOrBefore(nowEt.Date.AddDays(-1));
		return (defaultDay, defaultDay);
	}

	private sealed class VerifySingleTickerShim : AISingleTickerSubcommandSettings { }

	private static AIConfig? LoadMergedConfig(string ticker, string strategy)
	{
		var shim = new VerifySingleTickerShim { Ticker = ticker, Strategy = strategy };
		return AIContext.ResolveConfig(shim);
	}

	private static (HashSet<DateOnly> WatchDates, Dictionary<DateOnly, List<LiveWatchProposal>> ProposalsByDate) LoadWatchActivity(
		string path, DateTime since, DateTime until, OpenerConfig opener)
	{
		var watchDates = new HashSet<DateOnly>();
		var byDate = new Dictionary<DateOnly, List<LiveWatchProposal>>();
		if (!File.Exists(path)) return (watchDates, byDate);

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
				if (!string.Equals(Str(root, "mode"), "watch", StringComparison.OrdinalIgnoreCase)) continue;
				var tsEt = ParseEasternTs(Str(root, "ts"));
				if (tsEt == null || tsEt.Value.Date < since.Date || tsEt.Value.Date > until.Date) continue;

				var d = DateOnly.FromDateTime(tsEt.Value);
				watchDates.Add(d);

				var type = Str(root, "type") ?? "open";
				if (!string.Equals(type, "open", StringComparison.OrdinalIgnoreCase)) continue;
				if (tsEt.Value.TimeOfDay < Pricing.OptionMath.MarketOpen || tsEt.Value.TimeOfDay >= Pricing.OptionMath.MarketClose) continue;
				if (!opener.IsWithinEntryWindow(tsEt.Value.TimeOfDay)) continue;

				var isInfo = root.TryGetProperty("informational", out var inf) && inf.ValueKind == JsonValueKind.True;
				if (isInfo) continue;
				if (root.TryGetProperty("cashReserveBlocked", out var crb) && crb.ValueKind == JsonValueKind.True) continue;
				var qty = (int)(Dec(root, "qty") ?? 0m);
				if (qty < 1) continue;

				var legs = ParseProposalLegs(root);
				if (legs.Count == 0) continue;

				var ticker = (Str(root, "ticker") ?? ParsingHelpers.ParseOptionSymbol(legs[0].Symbol)?.Root ?? "").ToUpperInvariant();
				var strategy = Str(root, "strategy") ?? "";
				var structure = Str(root, "structure") ?? "";
				var cashImpact = Dec(root, "cashImpactPerContract") ?? 0m;
				var netPerShare = cashImpact / 100m; // signed: -debit, +credit
				var spot = root.TryGetProperty("diagnostic", out var diag) && diag.ValueKind == JsonValueKind.Object
					? Dec(diag, "spotAtEvaluation") ?? 0m
					: 0m;
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

				if (!byDate.TryGetValue(d, out var list))
					byDate[d] = list = [];

				if (isCascade)
				{
					// Keep the first qualifying open proposal per cascade setup per day.
					if (list.Any(p => p.CascadeSetup == cascadeSetup)) continue;
					list.Add(new LiveWatchProposal(tsEt.Value, ticker, strategy, structure, qty, legs, spot, netPerShare, Dec(root, "rawScore"), Dec(root, "finalScore"), repIv, cascadeSetup, cascadeScore));
				}
				else
				{
					// Scored strategy: keep the first qualifying open proposal of the day (rank 1 at first qualifying tick).
					if (list.Count > 0) continue;
					list.Add(new LiveWatchProposal(tsEt.Value, ticker, strategy, structure, qty, legs, spot, netPerShare, Dec(root, "rawScore"), Dec(root, "finalScore"), repIv, null, null));
				}
			}
		}

		return (watchDates, byDate);
	}

	private static List<LiveBrokerFill> LoadBrokerFills(string source, string targetTicker, DateTime since, DateTime until)
	{
		if (string.Equals(source, "none", StringComparison.OrdinalIgnoreCase)) return [];
		var reportSettings = new ReportSettings { Source = source };
		var (trades, feeLookup, err) = ReportCommand.LoadTrades(reportSettings);
		if (err != 0 || trades.Count == 0) return [];

		// Group option trades into order-level opens and matching closes on the same day.
		// Strategy orders share ParentStrategySeq; single-leg orders have ParentStrategySeq == null.
		var optionTrades = trades
			.Where(t => t.Asset == Asset.Option && t.Timestamp.Date >= since.Date && t.Timestamp.Date <= until.Date)
			.OrderBy(t => t.Timestamp)
			.ThenBy(t => t.Seq)
			.ToList();

		var grouped = new List<LiveBrokerFill>();
		var usedSeqs = new HashSet<int>();

		foreach (var t in optionTrades)
		{
			if (usedSeqs.Contains(t.Seq)) continue;

			List<Trade> orderLegs;
			if (t.ParentStrategySeq.HasValue)
			{
				orderLegs = optionTrades.Where(x => x.ParentStrategySeq == t.ParentStrategySeq && !usedSeqs.Contains(x.Seq)).ToList();
			}
			else
			{
				orderLegs = [t];
			}
			foreach (var l in orderLegs) usedSeqs.Add(l.Seq);

			var verifyLegs = new List<VerifyLeg>();
			decimal netPerShare = 0m;
			string ticker = "";
			foreach (var l in orderLegs)
			{
				if (!MatchKeys.TryGetOptionSymbol(l.MatchKey, out var sym)) continue;
				if (string.IsNullOrEmpty(ticker)) ticker = ParsingHelpers.ParseOptionSymbol(sym)?.Root ?? "";
				var act = l.Side == Side.Buy ? "buy" : "sell";
				verifyLegs.Add(new VerifyLeg(act, sym, l.Price));
				netPerShare += l.Side == Side.Sell ? l.Price : -l.Price;
			}
			if (verifyLegs.Count == 0 || !string.Equals(ticker, targetTicker, StringComparison.OrdinalIgnoreCase)) continue;
			var orderFees = feeLookup != null
				? orderLegs.Select(l => (l.Timestamp, l.Side, l.Qty)).Distinct().Sum(k => feeLookup.GetValueOrDefault(k, 0m))
				: 0m;
			grouped.Add(new LiveBrokerFill(t.Timestamp, ticker, orderLegs[0].Qty, verifyLegs, netPerShare, orderFees));
		}

		// Pair same-day closing fills (opposite sides on identical leg symbols) onto the opening fill so 0DTE exits show P&L.
		var result = new List<LiveBrokerFill>();
		var consumedClose = new HashSet<int>();
		for (int i = 0; i < grouped.Count; i++)
		{
			if (consumedClose.Contains(i)) continue;
			var open = grouped[i];
			LiveBrokerFill? close = null;
			for (int j = i + 1; j < grouped.Count; j++)
			{
				if (consumedClose.Contains(j)) continue;
				var cand = grouped[j];
				if (cand.TimestampEt.Date != open.TimestampEt.Date) continue;
				if (!IsOppositeLegSet(open.Legs, cand.Legs)) continue;
				close = cand;
				consumedClose.Add(j);
				break;
			}

			if (close != null)
			{
				var pnl = (open.NetPerShare + close.NetPerShare) * Math.Min(open.Qty, close.Qty) * 100m - open.Fees - close.Fees;
				result.Add(open with { ExitTimeEt = close.TimestampEt, ExitNetPerShare = close.NetPerShare, RealizedPnl = pnl });
			}
			else
			{
				result.Add(open);
			}
		}

		return result;
	}

	private static VerifyRow CompareScoredDay(
		DateOnly date, string ticker, string strategy,
		List<LiveWatchProposal> dayProposals, List<LiveBrokerFill> dayFills,
		BacktestResult btResult, AIConfig config)
	{
		var live = dayProposals.FirstOrDefault();
		var btOpen = btResult.Fills.FirstOrDefault(f => f.Kind == BacktestFillKind.Open);
		var btClose = btOpen != null
			? btResult.Fills.LastOrDefault(f => f.LineageId == btOpen.LineageId && f.Kind is BacktestFillKind.Close or BacktestFillKind.Expire)
			: null;
		var liveFill = live != null ? MatchBrokerFill(live, dayFills) : dayFills.FirstOrDefault();

		if (live == null && btOpen == null)
			return new VerifyRow(date, ticker, strategy, null, null, liveFill, null, null, VerifyVerdict.Match, "both declined to open", null, liveFill?.RealizedPnl, null);

		if (live == null && btOpen != null)
			return new VerifyRow(date, ticker, strategy, null, null, liveFill, btOpen, btClose, VerifyVerdict.Mismatch, $"bt opened {btOpen.StrategyKind} @ {btOpen.Date:HH:mm}, live did not", null, liveFill?.RealizedPnl, ComputeBtLineagePnl(btResult.Fills, btOpen.LineageId));

		if (live != null && btOpen == null)
			return new VerifyRow(date, ticker, strategy, null, live, liveFill, null, null, VerifyVerdict.Mismatch, $"live opened {live.Structure} @ {live.TimestampEt:HH:mm}, bt did not", null, liveFill?.RealizedPnl, null);

		var (verdict, detail) = ClassifyScoredComparison(live!, btOpen!, config.Indicators.StrikeStep);
		decimal? slippage = null;
		if (liveFill != null)
			slippage = liveFill.NetPerShare - live!.NetPerShare; // negative = paid more / collected less than proposal mid

		decimal? btPnl = btClose != null ? ComputeBtLineagePnl(btResult.Fills, btOpen!.LineageId) : null;
		return new VerifyRow(date, ticker, strategy, null, live, liveFill, btOpen, btClose, verdict, detail, slippage, liveFill?.RealizedPnl, btPnl);
	}

	private static async Task<List<VerifyRow>> CompareCascadeDayAsync(
		DateOnly date, string ticker, string strategy,
		List<LiveWatchProposal> dayProposals, List<LiveBrokerFill> dayFills,
		BacktestResult btResult, IReadOnlyList<(DateTime Date, double Rate)> rateHistory,
		CancellationToken cancellation)
	{
		var rows = new List<VerifyRow>();
		var btOpens = btResult.Fills.Where(f => f.Kind == BacktestFillKind.Open).OrderBy(f => f.Date).ToList();

		if (dayProposals.Count == 0 && btOpens.Count == 0)
		{
			rows.Add(new VerifyRow(date, ticker, strategy, "—", null, null, null, null, VerifyVerdict.Match, "no cascade triggers on either side", null, null, null));
			return rows;
		}

		// Pair live proposals and backtest opens by inferred setup / structure kind.
		var matchedBt = new HashSet<long>();
		var dayDt = date.ToDateTime(TimeOnly.MinValue);
		double? dayRate = rateHistory.Where(r => r.Date.Date <= dayDt).Select(r => (double?)r.Rate).LastOrDefault();
		CascadeReplaySession? replaySession = null;

		foreach (var live in dayProposals)
		{
			var liveFill = MatchBrokerFill(live, dayFills);
			var btOpen = btOpens.FirstOrDefault(b => !matchedBt.Contains(b.LineageId) && string.Equals(b.StrategyKind, live.Structure, StringComparison.OrdinalIgnoreCase));
			if (btOpen != null) matchedBt.Add(btOpen.LineageId);

			var btClose = btOpen != null
				? btResult.Fills.LastOrDefault(f => f.LineageId == btOpen.LineageId && f.Kind is BacktestFillKind.Close or BacktestFillKind.Expire)
				: null;

			decimal? slippage = liveFill != null ? liveFill.NetPerShare - live.NetPerShare : null;
			decimal? btPnl = btOpen != null && btClose != null ? ComputeBtLineagePnl(btResult.Fills, btOpen.LineageId) : null;

			if (btOpen != null)
			{
				var sameLegs = SameLegSet(live.Legs, btOpen.Legs);
				var minuteDiff = Math.Abs((btOpen.Date - live.TimestampEt).TotalMinutes);
				VerifyVerdict verdict;
				string detail;
				if (sameLegs && minuteDiff <= 2.0)
				{
					verdict = VerifyVerdict.Match;
					detail = "exact setup + legs + timing";
				}
				else if (minuteDiff <= 15.0)
				{
					// Check exact minute via offline CascadeReplaySession to see if the signal itself confirmed at live's minute.
					var exactNote = await CheckExactMinuteCascadeAsync(ticker, dayDt, live, dayRate, () => replaySession, s => replaySession = s, cancellation);
					verdict = VerifyVerdict.CadenceOffset;
					detail = $"Δ{(btOpen.Date - live.TimestampEt).TotalMinutes:+0;-0}m grid offset{exactNote}";
				}
				else
				{
					verdict = VerifyVerdict.LegFlip;
					detail = $"same setup, {Math.Round(minuteDiff)}m apart";
				}

				rows.Add(new VerifyRow(date, ticker, strategy, live.CascadeSetup?.ToString() ?? live.Structure, live, liveFill, btOpen, btClose, verdict, detail, slippage, liveFill?.RealizedPnl, btPnl));
			}
			else
			{
				var exactNote = await CheckExactMinuteCascadeAsync(ticker, dayDt, live, dayRate, () => replaySession, s => replaySession = s, cancellation);
				rows.Add(new VerifyRow(date, ticker, strategy, live.CascadeSetup?.ToString() ?? live.Structure, live, liveFill, null, null, VerifyVerdict.Mismatch, $"live confirmed @ {live.TimestampEt:HH:mm}, bt did not{exactNote}", slippage, liveFill?.RealizedPnl, null));
			}
		}

		// Any backtest opens not matched to a live proposal.
		foreach (var btOpen in btOpens.Where(b => !matchedBt.Contains(b.LineageId)))
		{
			var btClose = btResult.Fills.LastOrDefault(f => f.LineageId == btOpen.LineageId && f.Kind is BacktestFillKind.Close or BacktestFillKind.Expire);
			decimal? btPnl = btClose != null ? ComputeBtLineagePnl(btResult.Fills, btOpen.LineageId) : null;
			rows.Add(new VerifyRow(date, ticker, strategy, btOpen.StrategyKind, null, null, btOpen, btClose, VerifyVerdict.Mismatch, $"bt opened @ {btOpen.Date:HH:mm}, live did not", null, null, btPnl));
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
			return confirmed ? $" (offline {live.TimestampEt:HH:mm} confirms {read.Reading.LeadingSetup.Score}/100)" : $" (offline {live.TimestampEt:HH:mm}: {read.Reading.PrimaryRegime})";
		}
		catch
		{
			return "";
		}
	}

	internal static (VerifyVerdict Verdict, string Detail) ClassifyScoredComparison(LiveWatchProposal live, BacktestFill btOpen, decimal strikeStep)
	{
		if (!string.Equals(live.Structure, btOpen.StrategyKind, StringComparison.OrdinalIgnoreCase))
			return (VerifyVerdict.Mismatch, $"structure live={live.Structure} vs bt={btOpen.StrategyKind}");

		if (SameLegSet(live.Legs, btOpen.Legs))
			return (VerifyVerdict.Match, "exact structure + legs");

		var liveParsed = live.Legs
			.Select(l => (Action: l.Action.ToLowerInvariant(), Opt: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.Where(x => x.Opt != null)
			.OrderBy(x => x.Action, StringComparer.Ordinal)
			.ThenBy(x => x.Opt!.ExpiryDate.Date)
			.ThenBy(x => x.Opt!.CallPut, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Opt!.Strike)
			.ToList();
		var btParsed = btOpen.Legs
			.Select(l => (Action: l.Side == Side.Buy ? "buy" : "sell", Opt: ParsingHelpers.ParseOptionSymbol(l.Symbol)))
			.Where(x => x.Opt != null)
			.OrderBy(x => x.Action, StringComparer.Ordinal)
			.ThenBy(x => x.Opt!.ExpiryDate.Date)
			.ThenBy(x => x.Opt!.CallPut, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Opt!.Strike)
			.ToList();

		var liveExps = liveParsed.Select(x => x.Opt!.ExpiryDate.Date).OrderBy(d => d).ToList();
		var btExps = btParsed.Select(x => x.Opt!.ExpiryDate.Date).OrderBy(d => d).ToList();
		var sameExpiries = liveExps.SequenceEqual(btExps);

		var scoreDelta = live.FinalScore.HasValue && btOpen.FinalScore.HasValue
			? Math.Abs(btOpen.FinalScore.Value - live.FinalScore.Value)
			: (decimal?)null;

		var spotDelta = live.Spot > 0m ? btOpen.Spot - live.Spot : (decimal?)null;
		var spotNote = spotDelta.HasValue ? $" (spot Δ{spotDelta.Value:+0.00;-0.00})" : "";

		if (sameExpiries && liveParsed.Count == btParsed.Count)
		{
			var step = strikeStep > 0m ? strikeStep : 1m;
			var sameLegShape = liveParsed.Zip(btParsed, (a, b) =>
				a.Action == b.Action &&
				a.Opt!.ExpiryDate.Date == b.Opt!.ExpiryDate.Date &&
				string.Equals(a.Opt!.CallPut, b.Opt!.CallPut, StringComparison.OrdinalIgnoreCase)).All(ok => ok);

			if (sameLegShape)
			{
				var shifts = liveParsed.Zip(btParsed, (a, b) => b.Opt!.Strike - a.Opt!.Strike).ToList();
				var maxStrikeShift = shifts.Max(s => Math.Abs(s));
				var isUniformShift = shifts.Distinct().Count() == 1;

				// Uniform strike translation preserving exact spread width/type/expirations (e.g. spot-tracked offset).
				if (isUniformShift && (Math.Abs(shifts[0]) <= 2m * step || (spotDelta.HasValue && Math.Abs(shifts[0] - spotDelta.Value) <= step)))
					return (VerifyVerdict.Match, $"same struct+exp+shape, strike Δ{shifts[0]:+0.##;-0.##}{spotNote}");

				if (scoreDelta is <= NearTieScoreTolerance && maxStrikeShift <= step)
					return (VerifyVerdict.NearTie, $"adjacent strike tie (Δscore {scoreDelta:0.00000}){spotNote}");

				return (VerifyVerdict.LegFlip, $"same struct+exp, strike shift{spotNote}");
			}

			return (VerifyVerdict.LegFlip, $"same struct+exp, call/put flip{spotNote}");
		}

		if (sameExpiries)
			return (VerifyVerdict.LegFlip, $"same struct+exp, strike/side shift{spotNote}");

		if (scoreDelta is <= NearTieScoreTolerance)
			return (VerifyVerdict.NearTie, $"adjacent expiry tie (Δscore {scoreDelta:0.00000}){spotNote}");

		return (VerifyVerdict.Mismatch, $"different expiries{spotNote}");
	}

	private static LiveBrokerFill? MatchBrokerFill(LiveWatchProposal proposal, List<LiveBrokerFill> dayFills)
	{
		// Prefer exact leg-set match, else closest fill within 5 minutes of the proposal.
		var exact = dayFills.FirstOrDefault(f => SameLegSet(proposal.Legs, f.Legs));
		if (exact != null) return exact;
		return dayFills
			.Where(f => Math.Abs((f.TimestampEt - proposal.TimestampEt).TotalMinutes) <= 5.0)
			.OrderBy(f => Math.Abs((f.TimestampEt - proposal.TimestampEt).TotalSeconds))
			.FirstOrDefault();
	}

	internal static bool SameLegSet(IReadOnlyList<VerifyLeg> a, IReadOnlyList<BacktestLegFill> b)
	{
		if (a.Count != b.Count) return false;
		var setA = a.Select(l => $"{l.Action.ToLowerInvariant()}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		var setB = b.Select(l => $"{(l.Side == Side.Buy ? "buy" : "sell")}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		return setA.SequenceEqual(setB);
	}

	internal static bool SameLegSet(IReadOnlyList<VerifyLeg> a, IReadOnlyList<VerifyLeg> b)
	{
		if (a.Count != b.Count) return false;
		var setA = a.Select(l => $"{l.Action.ToLowerInvariant()}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		var setB = b.Select(l => $"{l.Action.ToLowerInvariant()}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		return setA.SequenceEqual(setB);
	}

	internal static bool IsOppositeLegSet(IReadOnlyList<VerifyLeg> a, IReadOnlyList<VerifyLeg> b)
	{
		if (a.Count != b.Count) return false;
		var setA = a.Select(l => $"{(l.Action.Equals("buy", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy")}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		var setB = b.Select(l => $"{l.Action.ToLowerInvariant()}:{l.Symbol.ToUpperInvariant()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
		return setA.SequenceEqual(setB);
	}

	private static decimal ComputeBtLineagePnl(IReadOnlyList<BacktestFill> fills, long lineageId)
	{
		decimal sum = 0m;
		foreach (var f in fills)
			if (f.LineageId == lineageId)
				sum += f.NetCashFlow - f.Fees;
		return sum;
	}

	private static void RenderReport(DateTime since, DateTime until, List<VerifyRow> rows)
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[bold]AI Live vs Backtest Verification[/]  [dim]{since:yyyy-MM-dd} → {until:yyyy-MM-dd}[/]");
		AnsiConsole.WriteLine();

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn("Date");
		table.AddColumn("Strategy");
		table.AddColumn("Live (Watch / Fill)");
		table.AddColumn("Backtest");
		table.AddColumn(new TableColumn("Slip Δ").RightAligned());
		table.AddColumn(new TableColumn("Live P&L").RightAligned());
		table.AddColumn(new TableColumn("BT P&L").RightAligned());
		table.AddColumn("Verdict");
		table.AddColumn("Detail");

		foreach (var r in rows)
		{
			var stratCol = r.SetupLabel != null
				? $"{Markup.Escape(r.Ticker)}.{Markup.Escape(r.Strategy)} [dim]({Markup.Escape(r.SetupLabel)})[/]"
				: $"{Markup.Escape(r.Ticker)}.{Markup.Escape(r.Strategy)}";

			var liveText = FormatLiveSummary(r.LiveProposal, r.LiveFill);
			var btText = FormatBtSummary(r.BacktestOpen, r.BacktestClose);
			var slipText = r.FillSlippagePerShare.HasValue
				? (r.FillSlippagePerShare.Value >= 0m ? $"[green]+${r.FillSlippagePerShare.Value:0.00}[/]" : $"[yellow]-${Math.Abs(r.FillSlippagePerShare.Value):0.00}[/]")
				: "[dim]—[/]";
			var livePnlText = FormatPnl(r.LiveRealizedPnl);
			var btPnlText = FormatPnl(r.BacktestRealizedPnl);
			var verdictText = r.Verdict switch
			{
				VerifyVerdict.Match => "[bold green]MATCH ✓[/]",
				VerifyVerdict.NearTie => "[cyan]NEAR-TIE[/]",
				VerifyVerdict.LegFlip => "[yellow]LEG-FLIP ⚠[/]",
				VerifyVerdict.CadenceOffset => "[cyan]CADENCE ⏱[/]",
				VerifyVerdict.NoBackfill => "[dim]NO BACKFILL[/]",
				_ => "[bold red]MISMATCH ✘[/]"
			};

			table.AddRow(
				r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				stratCol,
				liveText,
				btText,
				slipText,
				livePnlText,
				btPnlText,
				verdictText,
				Markup.Escape(r.Detail));
		}

		AnsiConsole.Write(table);

		// Summary line
		var matches = rows.Count(r => r.Verdict == VerifyVerdict.Match);
		var ties = rows.Count(r => r.Verdict is VerifyVerdict.NearTie or VerifyVerdict.LegFlip or VerifyVerdict.CadenceOffset);
		var mismatches = rows.Count(r => r.Verdict == VerifyVerdict.Mismatch);
		var noBackfill = rows.Count(r => r.Verdict == VerifyVerdict.NoBackfill);
		var slips = rows.Where(r => r.FillSlippagePerShare.HasValue).Select(r => r.FillSlippagePerShare!.Value).ToList();
		var avgSlip = slips.Count > 0 ? $" | avg fill slip vs mid: {slips.Average():+$0.00;-$0.00;$0.00}/sh" : "";

		AnsiConsole.MarkupLine($"[dim]Summary: {rows.Count} check(s) — [green]{matches} match[/], [yellow]{ties} tie/flip/cadence[/], [red]{mismatches} mismatch[/]{(noBackfill > 0 ? $", {noBackfill} pending backfill" : "")}{Markup.Escape(avgSlip)}[/]");
	}

	private static string FormatLiveSummary(LiveWatchProposal? prop, LiveBrokerFill? fill)
	{
		if (prop == null && fill == null) return "[dim]none[/]";
		if (prop != null && fill != null)
		{
			var strikes = FormatShortLegs(fill.Legs);
			return $"{fill.TimestampEt:HH:mm} {Markup.Escape(prop.Structure)} {Markup.Escape(strikes)} x{fill.Qty} @ {FormatSignedDollars(fill.NetPerShare)}";
		}
		if (prop != null)
		{
			var strikes = FormatShortLegs(prop.Legs);
			return $"{prop.TimestampEt:HH:mm} {Markup.Escape(prop.Structure)} {Markup.Escape(strikes)} x{prop.Qty} @ {FormatSignedDollars(prop.NetPerShare)} [dim](prop)[/]";
		}
		return $"{fill!.TimestampEt:HH:mm} {Markup.Escape(FormatShortLegs(fill.Legs))} x{fill.Qty} @ {FormatSignedDollars(fill.NetPerShare)} [dim](fill)[/]";
	}

	private static string FormatBtSummary(BacktestFill? open, BacktestFill? close)
	{
		if (open == null) return "[dim]none[/]";
		var legs = open.Legs.Select(l => new VerifyLeg(l.Side == Side.Buy ? "buy" : "sell", l.Symbol, l.PricePerShare)).ToList();
		var netPerShare = open.Qty > 0 ? open.NetCashFlow / (open.Qty * 100m) : 0m;
		var closeNote = close != null ? $" [dim]→ {close.RuleName ?? close.Kind.ToString()} {close.Date:HH:mm}[/]" : "";
		return $"{open.Date:HH:mm} {Markup.Escape(open.StrategyKind)} {Markup.Escape(FormatShortLegs(legs))} x{open.Qty} @ {FormatSignedDollars(netPerShare)}{closeNote}";
	}

	private static string FormatShortLegs(IReadOnlyList<VerifyLeg> legs)
	{
		var parts = new List<string>(legs.Count);
		foreach (var l in legs)
		{
			var p = ParsingHelpers.ParseOptionSymbol(l.Symbol);
			if (p == null) { parts.Add(l.Symbol); continue; }
			var sign = l.Action.Equals("buy", StringComparison.OrdinalIgnoreCase) ? "+" : "-";
			parts.Add($"{sign}{p.Strike:0.##}{p.CallPut}({p.ExpiryDate:MM/dd})");
		}
		return string.Join("/", parts);
	}

	private static string FormatSignedDollars(decimal perShare) =>
		perShare >= 0m ? $"+${perShare:0.00}" : $"-${Math.Abs(perShare):0.00}";

	private static string FormatPnl(decimal? pnl) =>
		!pnl.HasValue ? "[dim]—[/]"
		: pnl.Value >= 0m ? $"[green]+${pnl.Value:N2}[/]"
		: $"[red]-${Math.Abs(pnl.Value):N2}[/]";

	private static List<VerifyLeg> ParseProposalLegs(JsonElement root)
	{
		var legs = new List<VerifyLeg>();
		if (!root.TryGetProperty("legs", out var legsEl) || legsEl.ValueKind != JsonValueKind.Array)
			return legs;

		var quoteMids = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		if (root.TryGetProperty("diagnostic", out var diag) && diag.ValueKind == JsonValueKind.Object
			&& diag.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object
			&& probe.TryGetProperty("legQuotes", out var lq) && lq.ValueKind == JsonValueKind.Array)
		{
			foreach (var q in lq.EnumerateArray())
			{
				var sym = Str(q, "symbol");
				var bid = Dec(q, "bid");
				var ask = Dec(q, "ask");
				if (sym != null && (bid ?? ask) != null)
					quoteMids[sym] = bid.HasValue && ask.HasValue ? (bid.Value + ask.Value) / 2m : (bid ?? ask)!.Value;
			}
		}

		foreach (var l in legsEl.EnumerateArray())
		{
			var sym = Str(l, "symbol");
			var act = Str(l, "action");
			if (sym == null || act == null) continue;
			quoteMids.TryGetValue(sym, out var mid);
			legs.Add(new VerifyLeg(act, sym, mid > 0m ? mid : null));
		}
		return legs;
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
