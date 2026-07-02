using Spectre.Console;
using Spectre.Console.Cli;
using System.Globalization;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Events;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Pricing;
using WebullAnalytics.Sentiment;
using WebullAnalytics.Utils;
using Backtest = WebullAnalytics.AI.Backtest;

namespace WebullAnalytics.Analyze;

/// <summary>
/// <c>wa analyze regime &lt;TICKER&gt;</c> — the directional-trend companion to <c>analyze gex</c> /
/// <c>analyze sentiment</c>. Shows the blended scoring <c>bias</c> the live opener uses (daily technical
/// composite + VIX term structure + intraday tape, directional-agreement calibrated), its decomposition,
/// the live per-family structure ranking, and the bias thresholds at which a long-debit (LongCall /
/// LongPut) structure overtakes the credit verticals — the "when does it flip to long-favorable" readout.
///
/// It drives the same <see cref="OpenCandidateEvaluator"/> the live scanner uses (so the numbers match
/// <c>wa ai scan</c>), then re-scores the best candidate of each directional family across a bias sweep to
/// locate the flip. Live only — needs a sniffed Webull session (or the configured live quote source).
/// </summary>
internal sealed class AnalyzeRegimeSettings : AILiveTickerSubcommandSettings
{
	[CommandOption("--account <ALIAS>")]
	[System.ComponentModel.Description("Account alias or ID from api-config.json. Used only for the live position/account read; defaults to defaultAccount.")]
	public string? Account { get; set; }

	[CommandOption("--date <DATE>")]
	[System.ComponentModel.Description("Offline historical mode (YYYY-MM-DD): score the regime against the captured minute NBBO in data/quotes.db for that past RTH day instead of the live chain. Lets you inspect any prior day (or work off-hours). Pairs with --time.")]
	public string? Date { get; set; }

	[CommandOption("--time <HH:mm>")]
	[System.ComponentModel.Description("ET time-of-day for --date (default 10:00). The intraday-tape and spot are read as of this minute, so you can see the regime at the open vs mid-session.")]
	public string Time { get; set; } = "10:00";

	public override Spectre.Console.ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return Spectre.Console.ValidationResult.Error($"--date: expected YYYY-MM-DD, got '{Date}'");
		if (!TimeSpan.TryParse(Time, CultureInfo.InvariantCulture, out _))
			return Spectre.Console.ValidationResult.Error($"--time: expected HH:mm, got '{Time}'");
		return Spectre.Console.ValidationResult.Success();
	}
}

internal sealed class AnalyzeRegimeCommand : AsyncCommand<AnalyzeRegimeSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeRegimeSettings settings, CancellationToken cancellation)
		=> await AITextOutput.RunAsync(settings, "AIRegime", async () =>
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;
		TerminalHelper.EnsureTerminalWidthFromConfig();

		if (!config.Opener.Enabled)
		{
			AnsiConsole.MarkupLine("[red]opener is disabled in this config — nothing to score the regime against.[/]");
			return 1;
		}

		// Force best-per-structure surfacing (debug emits the best positive candidate of every enabled
		// structure as Informational) so the per-family table is complete, not just the global top-N.
		config.LogLevel = "debug";

		return settings.Date != null
			? await RunAsync(config, settings, await BuildOfflineContextAsync(config, settings, cancellation), cancellation)
			: await RunAsync(config, settings, await BuildLiveContextAsync(config, settings, cancellation), cancellation);
	});

	/// <summary>Everything the regime render needs, assembled by either the live or the offline-historical
	/// setup. <paramref name="Quotes"/> is the chain source the flip-margin re-score reads from.</summary>
	private sealed record RegimeContext(
		EvaluationContext Ctx,
		OpenCandidateEvaluator Evaluator,
		IQuoteSource Quotes,
		HistoricalPriceCache PriceCache,
		QuoteSnapshot Snapshot,
		DateTime AsOf,
		bool Offline);

	private static async Task<RegimeContext> BuildLiveContextAsync(AIConfig config, AnalyzeRegimeSettings settings, CancellationToken cancellation)
	{
		var now = DateTime.Now;
		AIContext.ConfigureRawQuoteDump(settings.Dump);
		var positions = AIContext.BuildLivePositionSource(config, settings.Account);
		// Honor --source / the config's vendor so the numbers actually match `wa ai scan` (this was silently
		// pinned to Webull before). Premarket note: Schwab's chain books are frozen until the bell while
		// Webull's GTH books are live until 09:15 ET — `--source webull` gives the quote-coherent premarket read.
		var source = (settings.Source ?? config.QuoteSource).ToLowerInvariant();
		var quotes = AIContext.BuildLiveQuoteSource(config, source);
		AnsiConsole.MarkupLine($"[dim]quote source: {source}[/]");
		var tickerSet = config.TickerSet();
		var priceCache = new HistoricalPriceCache();

		var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
		var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);
		var snapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, quotes, config, cancellation);
		// Premarket: replace the chain's stale prior-session spot with a live estimate (GTH-quote parity /
		// SPY-converted premarket bar) so the regime read reflects the overnight move, not yesterday's close.
		snapshot = await PremarketSpotOverride.ApplyAsync(snapshot, config, quotes, now, cancellation);
		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, priceCache, config.Indicators.TechnicalFilter, now, cancellation);
		var ctx = new EvaluationContext(now, openPositions, snapshot.Underlyings, snapshot.Options, cash, accountValue, technicalSignals);
		var evaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache);
		return new RegimeContext(ctx, evaluator, quotes, priceCache, snapshot, now, Offline: false);
	}

	/// <summary>Offline-historical setup (--date): score the regime against the captured minute NBBO in
	/// data/quotes.db at the chosen RTH minute, exactly as `ai backtest` does. No broker calls; a nominal
	/// account value is used since regime scoring/components are independent of cash sizing.</summary>
	private static async Task<RegimeContext> BuildOfflineContextAsync(AIConfig config, AnalyzeRegimeSettings settings, CancellationToken cancellation)
	{
		var date = DateTime.ParseExact(settings.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
		var tod = TimeSpan.Parse(settings.Time, CultureInfo.InvariantCulture);
		var asOf = DateTime.SpecifyKind(date.Date + tod, DateTimeKind.Unspecified); // ET-naive, matching backtest ctx.Now
		var tickerSet = config.TickerSet();

		var bars = new Backtest.HistoricalBarCache(offline: true);
		var smile = new Backtest.SmileIndexCache(offline: true);
		var ivProvider = new Backtest.BacktestIVProvider(bars, smile: smile);
		var dividendsByRoot = await new Backtest.HistoricalDividendCache(offline: true).BuildScheduleMapAsync(tickerSet, cancellation);
		var oiCache = new Backtest.ChainSnapshotOiCache();
		var parametric = new Backtest.BacktestQuoteSource(bars, ivProvider, riskFreeRate: 0.036, dividendsByRoot: dividendsByRoot, oiCache: oiCache);

		var quoteDbPath = Program.ResolvePath("data/quotes.db");
		if (!File.Exists(quoteDbPath))
			throw new FileNotFoundException($"SQLite quote store not found at '{quoteDbPath}'. Build it with scripts/import_quotes_sqlite.py (the daily backfill keeps it current).");
		var quoteStore = new Backtest.QuoteStoreCache(quoteDbPath, since: date.Date, until: date.Date, sameDayExpiryOnly: false);
		IQuoteSource quotes = new Backtest.QuotesQuoteSource(bars, quoteStore, parametric, riskFreeRate: 0.036, dividendsByRoot: dividendsByRoot, oiCache: oiCache);
		var priceCache = new HistoricalPriceCache(bars);

		var openPositions = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);
		const decimal nominalCash = 10_000_000m; // sizing-independent; large so nothing is cash-blocked out of the per-family table
		var snapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, asOf, quotes, config, cancellation);
		var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(tickerSet, priceCache, config.Indicators.TechnicalFilter, asOf, cancellation);
		var ctx = new EvaluationContext(asOf, openPositions, snapshot.Underlyings, snapshot.Options, nominalCash, nominalCash, technicalSignals);
		var evaluator = new OpenCandidateEvaluator(config, quotes, settings.Pricing, priceCache, backtestMode: true, dividendsByRoot: dividendsByRoot);
		return new RegimeContext(ctx, evaluator, quotes, priceCache, snapshot, asOf, Offline: true);
	}

	private static async Task<int> RunAsync(AIConfig config, AnalyzeRegimeSettings settings, RegimeContext rc, CancellationToken cancellation)
	{
		var ticker = config.Ticker.ToUpperInvariant();
		var op = config.Opener;
		var asOf = rc.AsOf;

		// The evaluator's [debug] breakdown goes to stderr — capture and discard it so the panel stays clean.
		IReadOnlyList<OpenProposal> proposals;
		var savedErr = Console.Error;
		try { Console.SetError(TextWriter.Null); proposals = await rc.Evaluator.EvaluateAsync(rc.Ctx, cancellation); }
		finally { Console.SetError(savedErr); }

		if (!rc.Evaluator.LastRegimeComponents.TryGetValue(ticker, out var components))
		{
			if (rc.Offline)
				AnsiConsole.MarkupLine($"[red]No regime components for {ticker} at {asOf:yyyy-MM-dd HH:mm} ET.[/] No captured NBBO for that minute in data/quotes.db? Try another --date/--time (RTH), or check the backfill covers that day.");
			else
				AnsiConsole.MarkupLine($"[red]No regime components computed for {ticker}.[/] No spot/chain at {asOf:yyyy-MM-dd HH:mm}? Market is closed — pass [yellow]--date YYYY-MM-DD[/] to inspect a past RTH day from data/quotes.db.");
			return 1;
		}

		var spot = rc.Evaluator.LastUnderlyings.TryGetValue(ticker, out var s) && s > 0m
			? s
			: rc.Snapshot.Underlyings.TryGetValue(ticker, out var s2) ? s2 : 0m;

		// Representative DTE: the top proposal's target DTE (the trade the engine would actually take); the
		// intraday-tape weight — and thus the blended bias — is DTE-aware, so we anchor the headline number
		// on the selected trade's horizon.
		var repDte = proposals.Count > 0 ? proposals[0].DaysToTarget : 0;
		var blended = RegimeAnalyzer.BlendBias(components.MacroBias, components.VixTermScore, op.Weights.VixTermStructure,
			components.Intraday?.Score, op.Weights.IntradayTape, repDte, op.IntradayTapeDteCurve, op.BiasCalibrationLookbackDays, components.MoveSign);
		var classification = RegimeAnalyzer.Classify(blended);

		rc.Ctx.TechnicalSignals.TryGetValue(ticker, out var techBias);

		RenderHeader(ticker, spot, asOf, repDte, blended, classification, rc.Offline);
		AnsiConsole.WriteLine();
		RenderComponents(components, techBias, blended, repDte, op);
		AnsiConsole.WriteLine();

		// Inputs for the per-family re-score sweep. Match what the evaluator fed the scorer so the flip
		// thresholds line up with live selection.
		decimal? hv = null;
		if (op.Weights.VolatilityFit > 0m)
		{
			var closes = await rc.PriceCache.GetRecentClosesAsync(ticker, op.VolatilityLookbackDays + 1, asOf, cancellation);
			var h = CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
			if (h.HasValue && h.Value > 0m) hv = h.Value;
		}
		decimal? sentiment = null;
		if (op.Weights.Sentiment > 0m)
		{
			var snap = await FearGreedClient.FetchAsync(asOf, cancellation, cacheOnly: rc.Offline);
			sentiment = snap?.Score;
		}
		var eventCalendar = await EventCalendarLoader.LoadAsync(new[] { ticker }, config.Indicators.Events, asOf, cancellation, cacheOnly: rc.Offline);
		var events = eventCalendar.Get(ticker);

		var families = BuildFamilyBests(proposals, ticker);
		RenderFamilyRanking(families, proposals);
		AnsiConsole.WriteLine();

		decimal Score(CandidateSkeleton skel, decimal bias)
			=> CandidateScorer.Score(skel, spot, asOf, rc.Snapshot.Options, bias, op, hv, settings.Pricing, applyLiquidityGate: false, sentimentScore: sentiment, events: events)?.FinalScore ?? decimal.MinValue;

		RenderFlipMargin(families, blended, repDte, Score);
		AnsiConsole.WriteLine();
		RenderInterpretation(classification, blended, components, families, sentiment, op);
		return 0;
	}

	private sealed record FamilyBest(OpenStructureKind Kind, CandidateSkeleton Skeleton, decimal LiveScore, int Sign, bool IsCredit);

	/// <summary>Best (highest live FinalScore) candidate per structure kind, with a re-scorable skeleton
	/// rebuilt from its legs. Only single-expiry directional families participate in the flip sweep; the
	/// table still lists everything the engine surfaced.</summary>
	private static List<FamilyBest> BuildFamilyBests(IReadOnlyList<OpenProposal> proposals, string ticker)
	{
		var bests = new List<FamilyBest>();
		foreach (var grp in proposals.GroupBy(p => p.StructureKind))
		{
			var best = grp.Aggregate((a, b) => (a.FinalScore ?? decimal.MinValue) >= (b.FinalScore ?? decimal.MinValue) ? a : b);
			var expiries = best.Legs.Select(l => ParsingHelpers.ParseOptionSymbol(l.Symbol)?.ExpiryDate).Where(e => e.HasValue).Select(e => e!.Value).ToList();
			if (expiries.Count == 0) continue;
			var target = expiries.Min(); // short-leg / nearest expiry, matching CandidateSkeleton.TargetExpiry
			var skel = new CandidateSkeleton(ticker, best.StructureKind, best.Legs, target);
			bests.Add(new FamilyBest(best.StructureKind, skel, best.FinalScore ?? 0m, StructureKindInfo.DirectionalSign(best.StructureKind), StructureKindInfo.IsCreditStructure(best.StructureKind)));
		}
		return bests.OrderByDescending(f => f.LiveScore).ToList();
	}

	private static bool IsLongDebitCall(FamilyBest f) => f.Kind is OpenStructureKind.LongCall or OpenStructureKind.LongCallVertical;
	private static bool IsLongDebitPut(FamilyBest f) => f.Kind is OpenStructureKind.LongPut or OpenStructureKind.LongPutVertical;

	private static void RenderHeader(string ticker, decimal spot, DateTime asOf, int repDte, decimal blended, RegimeAnalyzer.RegimeClassification c, bool offline)
	{
		var color = BiasColor(blended);
		var spotStr = spot > 0m ? $"${spot:F2}" : "[dim]?[/]";
		var mode = offline ? "  [dim](offline / data/quotes.db)[/]" : "";
		AnsiConsole.MarkupLine($"[bold]{ticker}[/] regime  spot [yellow]{spotStr}[/]  asof {asOf:yyyy-MM-dd HH:mm}{mode}  [dim](target {repDte}DTE)[/]");
		AnsiConsole.MarkupLine($"  bias [bold {color}]{blended:+0.00;-0.00}[/]  [italic]{RegimeAnalyzer.StrengthWord(c.Strength)} {RegimeAnalyzer.DirectionWord(c.Direction)}[/]  →  favors {Markup.Escape(c.FavoredSide)}");
		AnsiConsole.MarkupLine("  " + BuildGauge(blended, 41));
	}

	private static void RenderComponents(RegimeAnalyzer.RegimeComponents comp, TechnicalBias? tech, decimal blended, int repDte, OpenerConfig op)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Bias components[/]");
		table.AddColumn(new TableColumn("Signal"));
		table.AddColumn(new TableColumn("Value").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Weight").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Gauge").NoWrap());

		if (tech != null)
		{
			AddRow(table, "  SMA 5/20", tech.SmaScore, null);
			AddRow(table, "  RSI(14)", tech.RsiScore, null);
			AddRow(table, "  momentum", tech.MomentumScore, null);
			if (op.Indicators.TechnicalFilter.Sma200Weight > 0m) AddRow(table, "  SMA200 trend", tech.Sma200Score, null);
		}
		AddRow(table, "Daily technical (macro)", comp.MacroBias, null, bold: true);
		AddRow(table, "VIX term structure", comp.VixTermScore, op.Weights.VixTermStructure);
		var macroAfterVix = RegimeAnalyzer.BlendMacro(comp.MacroBias, comp.VixTermScore, op.Weights.VixTermStructure);
		AddRow(table, "  macro + VIX", macroAfterVix, null);

		if (comp.Intraday is IntradayBias ib)
		{
			var w = op.IntradayTapeDteCurve.WeightForDte(repDte, op.Weights.IntradayTape);
			AddRow(table, $"Intraday tape ({ib.BarCount} bars)", ib.Score, w, bold: true);
			AddRow(table, "  gap", ib.GapScore, null);
			AddRow(table, "  open→now", ib.OpenToNowScore, null);
			AddRow(table, "  VWAP dev", ib.VwapDeviationScore, null);
		}
		else
		{
			AddRow(table, "Intraday tape", null, op.Weights.IntradayTape);
		}

		if (op.BiasCalibrationLookbackDays > 0)
		{
			var moveStr = comp.MoveSign > 0m ? "up" : comp.MoveSign < 0m ? "down" : "flat";
			var agree = comp.MoveSign == 0m ? "n/a" : (Math.Sign(blended) == Math.Sign(comp.MoveSign) ? "agrees → full" : "disagrees → damped");
			table.AddRow($"Calibration ({op.BiasCalibrationLookbackDays}d move {moveStr})", "[dim]—[/]", "[dim]—[/]", $"[dim]{agree}[/]");
		}

		var bc = BiasColor(blended);
		table.AddRow("[bold]Blended bias[/]", $"[bold {bc}]{blended:+0.00;-0.00}[/]", "[dim]—[/]", BuildGauge(blended, 18));
		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]All signals range −1 (bearish) to +1 (bullish). Blended = (macro blended with VIX) blended with the intraday tape at the DTE-aware weight, then scaled by directional-agreement calibration. This is the exact value the opener's BiasAdjust + grid-shift consume.[/]");
	}

	private static void RenderFamilyRanking(List<FamilyBest> families, IReadOnlyList<OpenProposal> proposals)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Live structure ranking[/]");
		table.AddColumn(new TableColumn("Structure"));
		table.AddColumn(new TableColumn("Side").NoWrap());
		table.AddColumn(new TableColumn("Type").NoWrap());
		table.AddColumn(new TableColumn("Final score").RightAligned().NoWrap());

		var topKind = proposals.Count > 0 ? proposals[0].StructureKind : (OpenStructureKind?)null;
		if (families.Count == 0)
		{
			table.AddRow("[dim]none scored positive[/]", "[dim]—[/]", "[dim]—[/]", "[dim]—[/]");
		}
		foreach (var f in families)
		{
			var side = f.Sign > 0 ? "[green]bullish[/]" : f.Sign < 0 ? "[red]bearish[/]" : "[grey]neutral[/]";
			var type = f.IsCredit ? "credit" : "debit";
			var marker = topKind == f.Kind ? "  [bold cyan]← pick[/]" : "";
			table.AddRow($"{f.Kind}", side, type, $"{f.LiveScore:F6}{marker}");
		}
		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Best candidate of each enabled structure (live FinalScore at the current bias). The pick is the engine's top-ranked trade. [green]call[/]/[red]put[/] = directional side; debit = long-premium (LongCall/LongPut), credit = premium-selling vertical.[/]");
	}

	/// <summary>Bias sweep over [-1, +1]: at each step re-score every family's best skeleton and find which
	/// wins, locating the bias at which a long-debit call (and put) structure first overtakes the field. The
	/// flip is non-linear — bias enters both BiasAdjust and the scenario-grid shift — so it's found by
	/// sweeping, not closed form.</summary>
	private static void RenderFlipMargin(List<FamilyBest> families, decimal currentBias, int repDte, Func<CandidateSkeleton, decimal, decimal> score)
	{
		var sweepable = families.Where(f => f.Sign != 0).ToList();
		if (sweepable.Count == 0)
		{
			AnsiConsole.MarkupLine("[dim]Flip margin: no directional structures enabled to sweep.[/]");
			return;
		}

		const decimal lo = -1.0m, hi = 1.0m, step = 0.05m;
		decimal? callFlip = null, putFlip = null;
		// Winner at each grid point, scanning the whole range once.
		var points = new List<(decimal Bias, OpenStructureKind Winner, bool CallDebit, bool PutDebit)>();
		for (var b = lo; b <= hi + 0.0001m; b += step)
		{
			var bias = Math.Round(b, 2);
			var best = sweepable.Select(f => (f, sc: score(f.Skeleton, bias))).OrderByDescending(x => x.sc).First();
			var callDebit = IsLongDebitCall(best.f);
			var putDebit = IsLongDebitPut(best.f);
			points.Add((bias, best.f.Kind, callDebit, putDebit));
		}
		// Upside flip: lowest bias at/above which a call-debit structure is the winner (scanning up).
		foreach (var p in points) { if (p.CallDebit) { callFlip = p.Bias; break; } }
		// Downside flip: highest (least negative) bias at/below which a put-debit structure wins (scanning down).
		foreach (var p in Enumerable.Reverse(points)) { if (p.PutDebit) { putFlip = p.Bias; break; } }

		var currentWinner = sweepable.Select(f => (f, sc: score(f.Skeleton, currentBias))).OrderByDescending(x => x.sc).First().f;

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title($"[bold]Flip margin[/]  [dim](re-scored sweep, {repDte}DTE)[/]");
		table.AddColumn(new TableColumn("Threshold"));
		table.AddColumn(new TableColumn("Bias").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("vs current").RightAligned().NoWrap());

		table.AddRow("Current bias", $"[bold {BiasColor(currentBias)}]{currentBias:+0.00;-0.00}[/]", $"[dim]winner: {currentWinner.Kind}[/]");
		table.AddRow("Long-call favorable (≥)", FlipCell(callFlip, "green"), DeltaCell(callFlip, currentBias, up: true));
		table.AddRow("Long-put favorable (≤)", FlipCell(putFlip, "red"), DeltaCell(putFlip, currentBias, up: false));
		AnsiConsole.Write(table);

		var notes = new List<string>();
		if (callFlip == null) notes.Add("a long-call never tops the field within ±1.00 bias — credit/other structures dominate the call side at every trend strength the bias scale reaches");
		if (putFlip == null) notes.Add("a long-put never tops the field within ±1.00 bias on the downside");
		notes.Add("flip = bias where a long-debit (LongCall/LongPut) becomes the top-scoring structure; depends on today's IV / expected move, so it moves with vol");
		AnsiConsole.MarkupLine($"[dim]{Markup.Escape(string.Join(". ", notes))}.[/]");
	}

	private static void RenderInterpretation(RegimeAnalyzer.RegimeClassification c, decimal blended, RegimeAnalyzer.RegimeComponents comp, List<FamilyBest> families, decimal? sentiment, OpenerConfig op)
	{
		var lines = new List<string>();
		var color = BiasColor(blended);
		lines.Add($"[bold {color}]Regime:[/] {Markup.Escape(c.Headline)}");

		var pick = families.Count > 0 ? families[0] : null;
		if (pick != null)
		{
			var type = pick.IsCredit ? "premium-selling (credit)" : "long-premium (debit)";
			var lean = pick.Sign > 0 ? "bullish" : pick.Sign < 0 ? "bearish" : "neutral";
			lines.Add($"[bold]Engine pick:[/] {pick.Kind} — {type}, {lean} lean. {Markup.Escape(SideExplanation(c, pick))}");
		}

		// Which lever is doing the directional work, in plain English.
		var levers = new List<string>();
		if (Math.Abs(comp.MacroBias) >= 0.05m) levers.Add($"daily trend ({comp.MacroBias:+0.00;-0.00})");
		if (comp.Intraday is IntradayBias ib && Math.Abs(ib.Score) >= 0.05m) levers.Add($"intraday tape ({ib.Score:+0.00;-0.00})");
		if (comp.VixTermScore is decimal v && op.Weights.VixTermStructure > 0m && Math.Abs(v) >= 0.05m) levers.Add($"VIX term ({v:+0.00;-0.00})");
		lines.Add(levers.Count > 0
			? $"[bold]Carried by:[/] {Markup.Escape(string.Join(", ", levers))}."
			: "[bold]Carried by:[/] no single component is decisive — the bias is near flat.");

		if (op.Weights.Sentiment > 0m && sentiment.HasValue)
		{
			var fgDir = sentiment.Value >= 50m ? "greed → with-trend bullish tilt" : "fear → with-trend bearish tilt";
			var mode = string.Equals(op.SentimentMode, "momentum", StringComparison.OrdinalIgnoreCase) ? "momentum" : "contrarian";
			lines.Add($"[bold]F&G overlay ({mode}, weight {op.Weights.Sentiment:F2}):[/] {sentiment.Value:F0}/100 — {fgDir}. This is a separate, larger directional lever than the bias; see [italic]wa analyze sentiment[/].");
		}

		lines.Add("[dim]The bias sets direction (sign) and conviction (magnitude); the debit-vs-credit flip is decided by scoring against today's option pricing, not by the bias alone. Macro overlay — single-name catalysts can override it.[/]");

		var panel = new Panel(string.Join("\n", lines)).Header("[bold]Interpretation[/]").Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey);
		AnsiConsole.Write(panel);
	}

	private static string SideExplanation(RegimeAnalyzer.RegimeClassification c, FamilyBest pick)
	{
		if (pick.IsCredit)
			return "The trend isn't strong enough to make a long-debit out-score the credit vertical's higher capital efficiency — the engine sells premium on the favored side instead.";
		return "Conviction is strong enough that the directional debit's payoff out-scores the credit verticals.";
	}

	private static void AddRow(Table table, string label, decimal? value, decimal? weight, bool bold = false)
	{
		if (!value.HasValue)
		{
			table.AddRow(bold ? $"[bold]{Markup.Escape(label)}[/]" : Markup.Escape(label), "[dim]—[/]", weight.HasValue ? $"[dim]{weight.Value:F2}[/]" : "[dim]off[/]", "[dim]—[/]");
			return;
		}
		var color = BiasColor(value.Value);
		var valStr = $"[{color}]{value.Value:+0.00;-0.00}[/]";
		if (bold) valStr = $"[bold {color}]{value.Value:+0.00;-0.00}[/]";
		var wStr = weight.HasValue ? $"{weight.Value:F2}" : "[dim]—[/]";
		var lbl = bold ? $"[bold]{Markup.Escape(label)}[/]" : Markup.Escape(label);
		table.AddRow(lbl, valStr, wStr, BuildGauge(value.Value, 18));
	}

	private static string FlipCell(decimal? flip, string color) => flip.HasValue ? $"[bold {color}]{flip.Value:+0.00;-0.00}[/]" : "[dim]never (±1.00)[/]";

	private static string DeltaCell(decimal? flip, decimal current, bool up)
	{
		if (!flip.HasValue) return "[dim]—[/]";
		var reached = up ? current >= flip.Value : current <= flip.Value;
		if (reached) return "[bold green]reached[/]";
		var delta = flip.Value - current;
		return $"[yellow]{delta:+0.00;-0.00}[/]";
	}

	// Symmetric red→grey→green scale: −1 red, 0 grey, +1 green.
	private static string BiasColor(decimal bias) => bias switch
	{
		<= -0.45m => "red",
		< -0.05m => "yellow",
		<= 0.05m => "grey85",
		< 0.45m => "green",
		_ => "lime",
	};

	/// <summary>Solid-block bar with a tick marker at the bias position; center (0) is grey, left red,
	/// right green. Width is the bar length in cells. Mirrors the analyze-sentiment gauge idiom.</summary>
	private static string BuildGauge(decimal bias, int width)
	{
		var clamped = (double)Math.Clamp(bias, -1m, 1m);
		var pos = (int)Math.Round(((clamped + 1.0) / 2.0) * (width - 1));
		var sb = new System.Text.StringBuilder();
		for (var i = 0; i < width; i++)
		{
			var cellBias = (decimal)((i / (double)(width - 1)) * 2.0 - 1.0);
			var color = BiasColor(cellBias);
			if (i == pos) sb.Append($"[bold {TerminalHelper.ContrastingForeground(color)} on {color}]│[/]");
			else sb.Append("[" + color + "]█[/]");
		}
		return sb.ToString();
	}
}
