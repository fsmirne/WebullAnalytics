using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// `wa analyze gex <TICKER>` — Renders a 2D GEX heatmap over the option chain
/// (strikes × expirations), a per-expiration summary (gravity / gamma flip / max pain), a chain
/// totals panel, and call/put walls. Pulls the chain live from Webull (default; api-config.json must
/// already be sniffed) or Schwab (--vendor schwab; `wa schwab login`) — the two vendors' chain IVs
/// disagree materially on gravity, so the source is logged with every data/gex record. Yahoo isn't
/// supported because chain-level analytics need full OI + IV across every strike/expiry.
/// </summary>
internal sealed class AnalyzeGexSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., GME, SPY).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--greek <NAME>")]
	[DefaultValue("both")]
	[Description("Which exposure to map: both (default — the gamma and vanna heatmaps side by side, trimmed to the front expiries that fit the terminal), gamma/gex (the classic GEX alone), or vanna/vex (dollars of dealer delta per vol point of IV change, alone). Vanna is signed and flips just below the money, so it maps a hedging FLOW under an IV move, not a price magnet: the gamma-only anchors (gravity, gamma flip, call/put walls, max pain) are replaced by the per-expiry net vanna and its implied flow.")]
	public string Greek { get; set; } = "both";

	[CommandOption("--expiry <DATE>")]
	[Description("Pin the view to a single expiration date (YYYY-MM-DD). Default: show all expirations in the chain. Pinning one expiry frees the column axis for TIME, so it changes what the heatmap plots: alone it becomes the build-up view (columns = trading sessions, see --since), and with --intraday it becomes that expiry's intraday migration on --date rather than the same-day 0DTE. The per-expiration, chain-totals and wall tables are unaffected.")]
	public string? Expiry { get; set; }

	[CommandOption("--since <DATE>")]
	[Description("Build-up view (--expiry without --intraday) only: first session shown (YYYY-MM-DD). Default: the Monday of --date's week, so a weekly expiry shows its own Mon-to-date build. Each column is that session's OWN captured chain from data/oi — its real OI, IVs and close spot at the correct DTE — so the panel shows the pinned expiry's book filling in day by day.")]
	public string? Since { get; set; }

	[CommandOption("--strike-range <PCT>")]
	[DefaultValue(20)]
	[Description("Strike window as ± percent of spot. Default: 20.")]
	public int StrikeRangePct { get; set; } = 20;

	[CommandOption("--max-strikes <N>")]
	[DefaultValue(200)]
	[Description("Max strike rows the raw (pre-grouping) ladder keeps, picking the N strikes closest to spot within --strike-range. Display only — the Schwab fetch is always the full chain (strikeCount: 0), so this never starves Gravity/Centroid/NetPull/gamma-flip/max-pain of data; lowering it only trims the ladder those analytics themselves draw from before --strike-range and the display grouping shrink it further to fit the terminal. Default: 200 (the max), since a thin book's real gravity strike can sit well outside a small window (verified live on XSP).")]
	public int MaxStrikes { get; set; } = 200;

	[CommandOption("--dte <N>")]
	[DefaultValue(14)]
	[Description("Days-to-expiry cap: include every expiry from 0DTE through N days out (the daily grid). 0 = today's 0DTE only, 14 (default) = the next two weeks. Ignored when --expiry pins a single expiry.")]
	public int Dte { get; set; } = 14;

	[CommandOption("--top-walls <N>")]
	[DefaultValue(5)]
	[Description("Number of top call/put walls to list. Default: 5.")]
	public int TopWalls { get; set; } = 5;

	[CommandOption("--dump")]
	[Description("Also append every in-window contract from this live fetch (expiry, strike, right, bid/ask, vendor IV, OI, spot) to data/iv/<TICKER>/<ET-date>.csv, source-tagged. The raw per-strike inputs behind the displayed gex values — capture them from both --vendor sources to measure cross-vendor IV gaps. Live fetch only.")]
	public bool Dump { get; set; }

	[CommandOption("--intraday")]
	[Description("Intraday GEX heatmap: rows = strikes, columns = RTH time buckets (--interval), recomputing per-strike GEX at each bucket's spot (from data/intraday) against the day's fixed OI. Shows the gravity migrating as price moves. Maps --date's own 0DTE by default; pass --expiry to watch a LATER expiry's gravity move through --date instead (the only option on roots with no daily expirations). Without --date (or with --date today) this is the RUNNING-DAY view: the columns run 09:30 through the current bucket, and each call captures its chain to data/iv and its anchors to data/gex — a bucket near a capture is rebuilt from that capture's surface (as-displayed-then, static across re-runs, matching the \"·live\" footer rows), while a bucket with no capture is provisionally priced with the current fetch's IVs until tomorrow's quote backfill enables the full ex-ante replay. A past --date replays that day offline from its data/oi snapshot, with per-bucket IVs back-solved from the minute-quote store (data/quotes/<TICKER>/<expiry>.csv) when it covers the day, else frozen from the snapshot. Renders the gamma migration beside the session's VOLUME TAPE by default (per-bucket Δ of the captures' cumulative day volume, colored by call/put mix — the tape landing on the book; the panel self-skips on days whose captures predate the volume column); --vanna adds the VEX migration as an optional third panel. Skips the chain-totals and per-expiry tables; the walls appear as per-bucket Wall·C/Wall·P footer rows instead of their own table.")]
	public bool Intraday { get; set; }

	[CommandOption("--interval <MIN>")]
	[Description("--intraday time-bucket size in minutes, 1-120. Default: auto — the finest standard size whose ACTIVE panel set fits the terminal width side by side (gamma alone; plus the volume tape when capture volume exists; plus VEX under --vanna). Narrow the window with --start/--end to keep a fine interval readable: minute buckets over a 30-minute slice pin exactly when the gravity flips.")]
	public int? IntervalMin { get; set; }

	[CommandOption("--start <HH:MM>")]
	[Description("--intraday only: first column's ET time (default 09:30). Bounds the migration to a slice of the session, so a fine --interval stays readable instead of overflowing the terminal.")]
	public string? Start { get; set; }

	[CommandOption("--end <HH:MM>")]
	[Description("--intraday only: last column's ET time (default 16:00). Pairs with --start.")]
	public string? End { get; set; }

	[CommandOption("--time <HH:MM>")]
	[Description("--intraday only: narrow the VEX side panel to the single bucket nearest this ET time (default: a full VEX migration panel, one column per bucket like the gamma table) — e.g. --time 10:00 focuses on what the mapped expiry's vanna book looked like at 10am. The gamma migration columns are unaffected.")]
	public string? Time { get; set; }

	[CommandOption("--exante")]
	[Description("--intraday only: price the mapped expiry's gamma with the PRIOR trading day's snapshot IVs (falling back to a back-solve from the prior day's mids at the prior day's spot) instead of back-solving from this day's EOD mids. The default solve leaks the session's outcome into every column — a put that finished ITM has a fat EOD mid, back-solves to an inflated IV, and its strike re-brightens/dims by where the day CLOSED; ex-ante IVs show what was actually hedgeable at each bucket. Contracts absent from the prior snapshot are dropped.")]
	public bool Exante { get; set; }

	[CommandOption("--vanna")]
	[Description("--intraday only: add the VEX (vanna) migration panel as a third panel beside the gamma map and the volume tape. It was the default second panel before the tape took that seat; the flow study's null result (vanna adds nothing over ΔIV) demoted it to opt-in. The normal (non---intraday) view is unaffected — --greek both still renders the gamma and vanna heatmaps there.")]
	public bool Vanna { get; set; }

	[CommandOption("--lookback <DAYS>")]
	[DefaultValue(5)]
	[Description("How many prior sessions the expiring-contracts activity panel scans (0 disables it). The panel reads the last N data/oi snapshots and lists contracts expiring on the analysis day (or the pinned --expiry) that showed unusual activity while they still had days to run: day volume ≥ 2× that day's OI (guaranteed opening flow, same rule as the unusual-opening-activity screen), or an OI jump ≥ 2× the prior standing OI between consecutive covered snapshots (catches positioning built on a day whose snapshot missed the expiry — the live scraper's files carry only the 0DTE chain until the nightly backfill supplements them). Default: 5.")]
	public int Lookback { get; set; } = 5;

	[CommandOption("--captured")]
	[Description("--intraday only: price every bucket from the VENDOR-reported IVs/OI archived in data/iv (written by running-day analyze calls and wa-scraper's per-minute ivCapture) instead of the default source (minute-NBBO back-solve when the store covers the day, else the live chain). Buckets with no capture within half an interval are DROPPED, so the panel is purely vendor-surfaced — run the same command with and without the flag to compare NBBO-solved vs vendor-reported gamma terrain. Captures are matched to the --vendor in effect.")]
	public bool Captured { get; set; }

	[CommandOption("--view <MODE>")]
	[DefaultValue("detailed")]
	[Description("Display mode: 'detailed' (default — unusual-activity screens, heatmap, per-expiration table, chain totals, and walls) or 'simplified' (only the per-expiration table). Config.json's analyze.view sets the default.")]
	public string View { get; set; } = "detailed";

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("<ticker> is required");
		if (!BothGreeks && !TryParseGreek(Greek, out _)) return ValidationResult.Error($"--greek: expected 'gamma', 'vanna' or 'both', got '{Greek}'");
		if (Expiry != null && !DateTime.TryParseExact(Expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--expiry: expected YYYY-MM-DD, got '{Expiry}'");
		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: expected YYYY-MM-DD, got '{Since}'");
		if (Since != null && Expiry == null) return ValidationResult.Error("--since sets the first column of the build-up view, which needs a pinned expiry; add --expiry YYYY-MM-DD");
		if (Since != null && Intraday) return ValidationResult.Error("--since spans sessions and --intraday spans one session's time buckets; use one or the other");
		if (StrikeRangePct <= 0 || StrikeRangePct > 200) return ValidationResult.Error($"--strike-range: must be in (0, 200], got {StrikeRangePct}");
		if (MaxStrikes < 1 || MaxStrikes > 200) return ValidationResult.Error($"--max-strikes: must be in [1, 200], got {MaxStrikes}");
		if (Dte < 0 || Dte > 60) return ValidationResult.Error($"--dte: must be in [0, 60], got {Dte}");
		if (IntervalMin.HasValue && (IntervalMin.Value < 1 || IntervalMin.Value > 120)) return ValidationResult.Error($"--interval: must be in [1, 120] minutes, got {IntervalMin}");
		if (Time != null && !Intraday) return ValidationResult.Error("--time anchors the --intraday VEX column; add --intraday");
		if (Time != null && !Vanna) return ValidationResult.Error("--time narrows the VEX panel, which is opt-in now; add --vanna");
		if (Vanna && !Intraday) return ValidationResult.Error("--vanna adds the --intraday VEX migration panel; the normal view gets vanna via --greek both/vanna");
		if (Start != null && !Intraday) return ValidationResult.Error("--start bounds the --intraday columns; add --intraday");
		if (End != null && !Intraday) return ValidationResult.Error("--end bounds the --intraday columns; add --intraday");
		if (RthTimeError(Time, "--time") is { } timeErr) return ValidationResult.Error(timeErr);
		if (RthTimeError(Start, "--start") is { } startErr) return ValidationResult.Error(startErr);
		if (RthTimeError(End, "--end") is { } endErr) return ValidationResult.Error(endErr);
		if (ParseEtTime(Start) is { } s && ParseEtTime(End) is { } e && s >= e) return ValidationResult.Error($"--start {Start} must be before --end {End}");
		if (Exante && !Intraday) return ValidationResult.Error("--exante only applies to the --intraday heatmap");
		if (Captured && !Intraday) return ValidationResult.Error("--captured only applies to the --intraday heatmap");
		if (Captured && Exante) return ValidationResult.Error("--exante replays prior-day IVs and --captured replays this day's captured vendor IVs; use one or the other");
		// The intraday view's whole subject is the gravity strike migrating as spot moves, and it reads its Gravity row
		// back out of the gamma-only data/gex log. Vanna has no gravity, so an explicit vanna request is refused
		// outright; the 'both' default quietly resolves to the gamma view so plain --intraday keeps working.
		if (Intraday && !BothGreeks && TryParseGreek(Greek, out var g) && g != GreekKind.Gamma) return ValidationResult.Error("--intraday maps the gamma gravity migration and has no vanna equivalent; drop --greek vanna");
		if (TopWalls < 1 || TopWalls > 25) return ValidationResult.Error($"--top-walls: must be in [1, 25], got {TopWalls}");
		if (Lookback < 0 || Lookback > 30) return ValidationResult.Error($"--lookback: must be in [0, 30] sessions, got {Lookback}");
		if (Dump && EvaluationDateOverride.HasValue) return ValidationResult.Error("--dump applies to live fetches only (no --date)");
		if (View?.Trim().ToLowerInvariant() is not ("detailed" or "simplified")) return ValidationResult.Error($"--view: expected 'detailed' or 'simplified', got '{View}'");
		return ValidationResult.Success();
	}

	/// <summary>'both' = render the gamma AND vanna maps in one run, side by side when the terminal is wide enough.</summary>
	internal bool BothGreeks => string.Equals(Greek?.Trim(), "both", StringComparison.OrdinalIgnoreCase);

	internal bool Simplified => string.Equals(View?.Trim(), "simplified", StringComparison.OrdinalIgnoreCase);

	/// <summary>Applies the `analyze` config.json section's `view` key — kept separate from
	/// AnalyzeBaseSettings.ApplyConfig's `report` section since detailed/simplified is specific to
	/// `analyze gex`'s panel set, not the report renderer.</summary>
	internal void ApplyViewConfig(Dictionary<string, JsonElement> cfg)
	{
		if (!Program.HasCliOption("view") && cfg.TryGetString("view", out var view)) View = view;
	}

	internal static readonly TimeSpan RthOpen = new(9, 30, 0);
	internal static readonly TimeSpan RthClose = new(16, 0, 0);
	// "9:40" is what anyone actually types; requiring the leading zero is a papercut, so both forms parse.
	private static readonly string[] EtTimeFormats = { @"hh\:mm", @"h\:mm" };

	/// <summary>An ET HH:MM flag value, or null when the flag was absent. Validation has already rejected malformed
	/// and out-of-RTH values, so callers can treat this as a plain parse.</summary>
	internal static TimeSpan? ParseEtTime(string? value) => value != null && TimeSpan.TryParseExact(value, EtTimeFormats, CultureInfo.InvariantCulture, out var t) ? t : null;

	/// <summary>Validation message for an ET time flag, or null when it is absent or valid.</summary>
	private static string? RthTimeError(string? value, string flag)
	{
		if (value == null) return null;
		if (ParseEtTime(value) is not { } t) return $"{flag}: expected HH:MM (ET), got '{value}'";
		return t < RthOpen || t > RthClose ? $"{flag}: must be within RTH 09:30-16:00 ET, got '{value}'" : null;
	}

	internal static bool TryParseGreek(string? name, out GreekKind greek)
	{
		greek = GreekKind.Gamma;
		switch ((name ?? "").Trim().ToLowerInvariant())
		{
			case "" or "gamma" or "gex": return true;
			case "vanna" or "vex": greek = GreekKind.Vanna; return true;
			default: return false;
		}
	}
}

internal sealed class AnalyzeGexCommand : AsyncCommand<AnalyzeGexSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeGexSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);
		var analyzeConfig = Program.LoadAppConfig("analyze");
		if (analyzeConfig != null) settings.ApplyViewConfig(analyzeConfig);

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var ticker = settings.Ticker.ToUpperInvariant();
		var asOf = settings.EvaluationDateOverride ?? DateTime.Now;
		DateTime? expiryFilter = settings.Expiry != null
			? DateTime.ParseExact(settings.Expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture)
			: null;

		Dictionary<string, OptionContractQuote> quotes;
		decimal? spot;
		var isOfflineHistorical = false;
		ApiConfig? apiConfig = null;   // set on the live-fetch path; the running-day --intraday tape refresh reuses it

		// Historical/offline: for an explicit --date (today included) with a captured chain in data/oi (ThetaData
		// backfill or the live scraper), read that day's snapshot — OI + IV + spot are inlined for the full chain —
		// instead of the live Webull fetch. Lets `analyze gex SPY --date 2026-06-03` show THAT day's real magnet,
		// not today's. With --date <today> this trades the live fetch for the morning snapshot's IV/spot.
		var oiPath = Program.ResolvePath($"data/oi/{ticker}/{asOf:yyyy-MM-dd}.jsonl");
		if (settings.EvaluationDateOverride.HasValue && asOf.Date <= DateTime.Today && File.Exists(oiPath))
		{
			var (snapSpot, snapQuotes) = LoadOiSnapshot(oiPath);
			if (snapQuotes.Count == 0)
			{
				AnsiConsole.MarkupLine($"[red]Empty data/oi snapshot for {ticker} {asOf:yyyy-MM-dd}.[/]");
				return 1;
			}
			quotes = snapQuotes;
			spot = ResolveSpotOverride(settings.Spot, ticker) ?? snapSpot;
			AnsiConsole.MarkupLine($"[dim]Historical GEX from {Markup.Escape(oiPath)} ({quotes.Count} contracts; offline — no live fetch).[/]");
			isOfflineHistorical = true;
		}
		else if (settings.EvaluationDateOverride.HasValue && asOf.Date < DateTime.Today)
		{
			// A PAST --date with no captured snapshot can never be served by a live fetch (the chain would be
			// today's book wearing yesterday's label — and roots only the scraper ever covered, like SPCX, just
			// produce a confusing vendor 400). Fail fast and say what IS available instead.
			var dir = Program.ResolvePath($"data/oi/{ticker}");
			var available = Directory.Exists(dir)
				? Directory.EnumerateFiles(dir, "????-??-??.jsonl").Select(Path.GetFileNameWithoutExtension).OrderBy(d => d).ToList()
				: new List<string?>();
			AnsiConsole.MarkupLine($"[red]No data/oi snapshot for {Markup.Escape(ticker)} on {asOf:yyyy-MM-dd} — a past --date renders only from a captured chain (no live fallback).[/]");
			AnsiConsole.MarkupLine(available.Count > 0
				? $"[dim]Available {Markup.Escape(ticker)} snapshot dates: {Markup.Escape(available.Count > 12 ? string.Join(", ", available.Take(3)) + $", … ({available.Count - 6} more) …, " + string.Join(", ", available.TakeLast(3)) : string.Join(", ", available))}.[/]"
				: $"[dim]No snapshots exist under data/oi/{Markup.Escape(ticker)}/ at all — the ThetaData backfill and the live scraper are the writers.[/]");
			return 1;
		}
		else
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath))
			{
				AnsiConsole.MarkupLine("[red]Error: api-config.json not found. Run 'sniff' first (or pass a past --date with a data/oi snapshot).[/]");
				return 1;
			}
			apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
			if (settings.VendorName == "schwab")
			{
				if (apiConfig?.Schwab == null)
				{
					AnsiConsole.MarkupLine("[red]Error: schwab vendor needs Schwab credentials in api-config.json. Run 'wa schwab login' first.[/]");
					return 1;
				}
				IReadOnlyList<OptionContractQuote> schwabQuotes;
				decimal? schwabSpot;
				try
				{
					var token = await SchwabAuthClient.GetAccessTokenAsync(apiConfig.Schwab, configPath, cancellation);
					var fromExpiry = expiryFilter.HasValue ? DateOnly.FromDateTime(expiryFilter.Value) : DateOnly.FromDateTime(asOf.Date);
					var toExpiry = expiryFilter.HasValue ? DateOnly.FromDateTime(expiryFilter.Value) : DateOnly.FromDateTime(asOf.Date).AddDays(settings.Dte);
					// Full ladder (strikeCount: 0), NOT bounded by --max-strikes: that's a DISPLAY cap now (GravityByExpiry/
					// Centroid/NetPull/MaxPainContributors all read the complete fetched set — see GexMatrix.Build), and a
					// vendor-side strike cap here would starve them of data no in-memory fix can recover. Also the only way
					// this stays consistent with `wa ai scan`'s own live fetch (LiveQuoteSource), which has always been
					// unbounded — verified live: capping this to 200 here left Max Pain reading $7,820 against ai scan's
					// $7,420 for the same SPXW 10/16 book. The client still date-splits on an oversized response as a
					// safety net, so this doesn't risk the 502 the original cap was partly guarding against.
					(schwabSpot, schwabQuotes) = await SchwabOptionsClient.FetchChainAsync(token, ticker, fromExpiry, toExpiry, cancellation, strikeCount: 0);
				}
				catch (SchwabAuthException ex)
				{
					AnsiConsole.MarkupLine($"[red]Schwab auth failed: {Markup.Escape(ex.Message)} Re-run 'wa schwab login'.[/]");
					return 1;
				}
				// A $SPX chains request returns BOTH the SPX and SPXW roots. Keep both — SPXW is the requested
				// root on every date, but on a standard monthly (3rd Friday) real open interest is split
				// across both, and downstream per-expiry aggregation (ParsingHelpers.RootsMatchForAggregation)
				// merges them correctly for that date while still isolating them on every other date.
				quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
				foreach (var q in schwabQuotes)
					quotes[q.ContractSymbol] = q;
				if (quotes.Count == 0)
				{
					AnsiConsole.MarkupLine($"[red]No option chain data returned for {ticker} from Schwab.[/]");
					return 1;
				}
				spot = ResolveSpotOverride(settings.Spot, ticker) ?? schwabSpot;
			}
			else
			{
				if (apiConfig == null || apiConfig.Webull.Headers.Count == 0)
				{
					AnsiConsole.MarkupLine("[red]Error: api-config.json is empty or missing headers. Run 'sniff' first.[/]");
					return 1;
				}
				var (initialQuotes, fetchedSpot, derivativeIds) = await WebullOptionsClient.FetchChainAsync(apiConfig, ticker, cancellation);
				if (initialQuotes.Count == 0)
				{
					AnsiConsole.MarkupLine($"[red]No option chain data returned for {ticker}.[/]");
					return 1;
				}
				spot = ResolveSpotOverride(settings.Spot, ticker) ?? fetchedSpot;
				quotes = new Dictionary<string, OptionContractQuote>(initialQuotes, StringComparer.OrdinalIgnoreCase);
				// Webull's strategy/list only inlines OI/IV for the front-most expiration; re-pull in-window contracts
				// via queryBatch to populate the heatmap. (Offline data/oi snapshots already carry the full chain.)
				if (spot.HasValue && spot.Value > 0m)
				{
					var refreshed = await RefreshInWindowContractsAsync(apiConfig, quotes, derivativeIds, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.Dte, settings.MaxStrikes, cancellation);
					if (refreshed > 0) Log.Debug($"Refreshed {refreshed} in-window contract(s) via queryBatch.");
				}
			}
		}

		if (!spot.HasValue || spot.Value <= 0m)
		{
			AnsiConsole.MarkupLine($"[red]No spot price available for {ticker}. Pass --spot {ticker}:PRICE to override.[/]");
			return 1;
		}

		// Chain-wide OI absence = vendor OI blackout (Schwab zeroes openInterest overnight until the next OCC
		// update posts), not "every contract is a fresh listing" — every OI-dependent panel must know the field
		// is unusable rather than treat 0 as a real standing-interest reading.
		var chainHasOi = quotes.Values.Any(q => q.OpenInterest is > 0);

		// Unusual opening activity: strikes trading a multiple of their standing OI — arithmetically
		// guaranteed opening flow, no print signing needed. Rendered in both the normal and --intraday views;
		// --view simplified drops it, since that view's whole point is JUST the per-expiration table.
		if (!settings.Simplified)
			RenderUnusualActivity(ticker, quotes, asOf, isOfflineHistorical, chainHasOi);

		// The companion look BACK: the screen above excludes contracts expiring on the analysis day (0DTE churn),
		// but a contract dying TODAY that printed unusual volume on a PRIOR session — when it still had days to
		// run — is positioning that matured into today's OI, i.e. today's gamma terrain. Rendered in both views.
		if (!settings.Simplified)
			RenderExpiryLookback(ticker, quotes, asOf, expiryFilter?.Date ?? asOf.Date, settings.Lookback, chainHasOi);

		// --intraday: 0DTE strikes × RTH-hours gravity-migration heatmap. Offline-historical only (needs an explicit
		// --date with both a data/oi snapshot and a data/intraday spot file). Replaces the normal tables.
		if (settings.Intraday)
		{
			if (!isOfflineHistorical && asOf.Date != DateTime.Today)
			{
				AnsiConsole.MarkupLine("[red]--intraday for a past day requires a data/oi snapshot for that --date (offline-historical mode); none was loaded. The running day needs no snapshot — omit --date.[/]");
				return 1;
			}
			// Which expiry's gamma the columns map. Default = --date's own 0DTE; --expiry moves it to a later series,
			// which is the ONLY way to see a gravity migration on a root that lists no same-day expiration.
			var intradayExpiry = expiryFilter?.Date ?? asOf.Date;
			if (intradayExpiry < asOf.Date)
			{
				AnsiConsole.MarkupLine($"[red]--expiry {intradayExpiry:yyyy-MM-dd} precedes --date {asOf.Date:yyyy-MM-dd} — the intraday map replays a session against an expiry that is still listed on it.[/]");
				return 1;
			}
			if (settings.Exante && !ApplyExanteIvs(ticker, asOf.Date, intradayExpiry, quotes))
				return 1;
			if (!isOfflineHistorical)
			{
				// Running-day mode: the chain just fetched live carries today's real OI (constant intraday, published
				// pre-open) and current IVs, so the 09:30 -> now migration is renderable without waiting for tomorrow's
				// backfill snapshot. Refresh the intraday tape first so the spot columns extend to the current minute
				// instead of ending wherever the last tape-touching command left the file; columns after "now" have no
				// spot within tolerance and drop out naturally.
				await RefreshIntradayTapeAsync(ticker, apiConfig, cancellation);
				AnsiConsole.MarkupLine($"[dim]Running-day heatmap from the live {Markup.Escape(settings.VendorName)} chain ({quotes.Count} contracts); columns end at the current bucket.[/]");
				// A running-day --intraday call is often the only gex command run all session, so it must feed both live
				// stores itself: the data/gex anchor log (the "·live" footer rows) AND a data/iv chain capture — the
				// per-strike bid/ask/vendor-IV/OI behind THIS run's numbers, which the heatmap reads back so buckets
				// near a capture are rebuilt from what was actually visible then instead of being silently repriced
				// with whatever chain the latest call fetched. --exante swaps the chain's IVs for prior-day values,
				// which must not enter either store as live observations.
				if (!settings.Exante)
				{
					var liveMatrix = GexMatrix.Build(quotes, ticker, spot.Value, asOf, expiryFilter: intradayExpiry, settings.StrikeRangePct / 100m, maxDteDays: Math.Max(0, (intradayExpiry - asOf.Date).Days), settings.MaxStrikes);
					if (liveMatrix.Expiries.Count > 0) AppendGexLog(ticker, spot.Value, liveMatrix, settings);
					AppendIvDump(ticker, spot.Value, quotes, settings, asOf, intradayExpiry);
				}
			}
			RenderIntradayGexHeatmap(ticker, asOf.Date, intradayExpiry, quotes, settings.StrikeRangePct / 100m, settings.MaxStrikes, settings.IntervalMin, settings.Exante, settings.VendorName, liveChain: !isOfflineHistorical, withVexNow: settings.Vanna, vendorIvs: settings.Captured,
				vexAt: AnalyzeGexSettings.ParseEtTime(settings.Time),
				windowStart: AnalyzeGexSettings.ParseEtTime(settings.Start) ?? AnalyzeGexSettings.RthOpen,
				windowEnd: AnalyzeGexSettings.ParseEtTime(settings.End) ?? AnalyzeGexSettings.RthClose);
			return 0;
		}

		// --greek both: TryParseGreek leaves greek at Gamma, so `matrix` below IS the gamma matrix — the data/gex
		// log condition and every gamma-only panel stay correct; the vanna matrix rides alongside for rendering.
		AnalyzeGexSettings.TryParseGreek(settings.Greek, out var greek);
		var both = settings.BothGreeks;
		var matrix = GexMatrix.Build(quotes, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.Dte, settings.MaxStrikes, greek);
		var vannaMatrix = both ? GexMatrix.Build(quotes, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.Dte, settings.MaxStrikes, GreekKind.Vanna) : null;
		if (matrix.Strikes.Count == 0 || matrix.Expiries.Count == 0)
		{
			AnsiConsole.MarkupLine(chainHasOi
				? $"[yellow]No strikes match within ±{settings.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]"
				: $"[yellow]The vendor returned no open interest for any of the {quotes.Count} fetched contracts (overnight OI blackout — OI is zeroed until the next morning's OCC update) and GEX is built from OI, so there is nothing to compute. Re-run during market hours, or use --date with a captured data/oi snapshot.[/]");
			return 1;
		}

		// Live runs log what THIS computation showed (gravity/walls/flip/max-pain per expiry) to data/gex —
		// the vendor-reported IVs these values are built on are never persisted intraday, so the displayed
		// numbers are otherwise irreproducible; the --intraday heatmap reads this log back as its "Gravity" row.
		// data/gex carries gamma anchors (gravity/walls/flip/max-pain) and is read back by --intraday; a vanna run
		// has none of those, so it stays out of the log rather than writing rows the readers would misinterpret.
		if (!settings.EvaluationDateOverride.HasValue)
		{
			if (greek == GreekKind.Gamma) AppendGexLog(ticker, spot.Value, matrix, settings);
			if (settings.Dump) AppendIvDump(ticker, spot.Value, quotes, settings, asOf, expiryFilter);
		}

		// --view simplified renders ONLY the per-expiration table below — header, heatmap, chain totals and the
		// standalone walls panel are all skipped.
		if (!settings.Simplified)
		{
			RenderHeader(ticker, spot.Value, asOf, expiryFilter, matrix);
			AnsiConsole.WriteLine();
			// A pinned --expiry collapses the heatmap's column axis to one expiration, which is a whole panel spent on a
			// single column. Spend it on TIME instead: the same expiry re-read from each prior session's own snapshot, so
			// the book is seen filling in. The rightmost column is exactly what the one-column heatmap used to show.
			if (expiryFilter.HasValue)
				RenderExpiryBuildup(ticker, expiryFilter.Value.Date, asOf.Date, ResolveBuildupStart(settings.Since, asOf.Date), quotes, spot.Value, settings, greek, vannaMatrix != null);
			else if (vannaMatrix != null)
				RenderHeatmapsSideBySide(matrix, vannaMatrix, spot.Value);
			else
				RenderHeatmap(matrix, spot.Value, greek);
			AnsiConsole.WriteLine();
		}
		if (greek == GreekKind.Vanna)
			RenderPerExpiryVanna(matrix, asOf);
		else
			RenderPerExpirySummary(matrix, spot.Value, asOf);
		if (settings.Simplified) return 0;
		if (vannaMatrix != null)
		{
			AnsiConsole.WriteLine();
			RenderPerExpiryVanna(vannaMatrix, asOf);
		}
		AnsiConsole.WriteLine();
		if (vannaMatrix != null)
			RenderTotalsSideBySide(matrix, vannaMatrix, spot.Value);
		else
			RenderTotals(matrix, spot.Value, greek);
		AnsiConsole.WriteLine();
		// Walls are max-gamma-per-side strikes read as support/resistance. There is no vanna analogue — the largest
		// |vanna| strike is just where OI meets d2 ≈ ±1 — so the panel is gamma-only rather than relabelled.
		if (greek == GreekKind.Gamma) RenderWalls(matrix, settings.TopWalls);
		return 0;
	}

	/// <summary>Loads a historical day's chain from a data/oi snapshot (the per-day full-chain JSONL written by
	/// the ThetaData backfill / live scraper) into (spot, quotes) — OI + IV inlined for every contract, so the
	/// GEX heatmap computes off real captured data with no live fetch. Picks the first RTH (≥09:30 ET) record,
	/// else the first line.</summary>
	private static (decimal? Spot, Dictionary<string, OptionContractQuote> Quotes) LoadOiSnapshot(string path)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		string? chosen = null, firstAny = null;
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			firstAny ??= line;
			using var probe = JsonDocument.Parse(line);
			if (probe.RootElement.TryGetProperty("tsEt", out var ts) && DateTime.TryParse(ts.GetString(), out var et)
				&& et.TimeOfDay >= new TimeSpan(9, 30, 0)) { chosen = line; break; }
		}
		chosen ??= firstAny;
		if (chosen == null) return (null, quotes);

		using var doc = JsonDocument.Parse(chosen);
		var root = doc.RootElement;
		decimal? spot = root.TryGetProperty("underlyingPrice", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetDecimal() : null;
		if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
			foreach (var o in opts.EnumerateArray())
			{
				if (!o.TryGetProperty("symbol", out var symEl) || symEl.GetString() is not string sym || sym.Length == 0) continue;
				decimal? Dec(string k) => o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : null;
				long? Lng(string k) => o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;
				quotes[sym] = new OptionContractQuote(
					ContractSymbol: sym, LastPrice: Dec("last"), Bid: Dec("bid"), Ask: Dec("ask"),
					Change: null, PercentChange: null, Volume: Lng("volume"), OpenInterest: Lng("openInterest"),
					ImpliedVolatility: Dec("iv"), HistoricalVolatility: Dec("hv"));
			}
		return (spot, quotes);
	}

	/// <summary>--exante: replaces every mapped-expiry contract's IV with the prior trading day's snapshot value (falling back
	/// to a back-solve from the prior day's captured mid at the prior day's spot). The data/oi EOD snapshot stores
	/// iv = null for the own-day expiry, so GexMatrix.Build otherwise back-solves 0DTE IVs from POST-session mids at
	/// each bucket's historical spot — which leaks the day's outcome into every column (a put that finished ITM has a
	/// fat EOD mid, back-solves to an inflated IV, and its gamma re-shapes by where the day closed). Contracts with no
	/// usable prior-day IV or mid are removed so Build cannot fall back to the leaky same-day solve.</summary>
	private static bool ApplyExanteIvs(string ticker, DateTime date, DateTime expiry, Dictionary<string, OptionContractQuote> quotes)
	{
		var dir = Program.ResolvePath($"data/oi/{ticker}");
		DateTime? priorDate = null;
		if (Directory.Exists(dir))
			foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
				if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Date < date && (date - d.Date).Days <= 5 && (priorDate == null || d.Date > priorDate.Value))
					priorDate = d.Date;
		if (priorDate == null)
		{
			AnsiConsole.MarkupLine($"[red]--exante: no prior data/oi snapshot for {ticker} within 5 days before {date:yyyy-MM-dd}.[/]");
			return false;
		}

		var priorPath = Program.ResolvePath($"data/oi/{ticker}/{priorDate:yyyy-MM-dd}.jsonl");
		var (priorSpot, priorQuotes) = LoadOiSnapshot(priorPath);
		if (priorQuotes.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]--exante: empty prior data/oi snapshot {Markup.Escape(priorPath)}.[/]");
			return false;
		}

		// The solve is priced as of the PRIOR day (its spot, its EOD mids, both stamped near that day's close), so
		// time-to-expiry runs close-to-close: whole calendar days, which IS the true clock here — matching the
		// clock-accurate GexMatrix.TimeYears convention without needing an intraday instant. (Scraper-written
		// snapshots are morning captures, overstating this by ~⅔ of a day; accepted, the solve stays two-sided.)
		var timeYears = Math.Max(1, (expiry.Date - priorDate.Value).Days) / 365.0;
		int applied = 0, solved = 0, dropped = 0;
		foreach (var sym in quotes.Keys.ToList())
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null || !ParsingHelpers.RootsMatchForAggregation(parsed.Root, ticker, expiry) || parsed.ExpiryDate.Date != expiry.Date) continue; // only the mapped expiry is rendered
			var iv = 0m;
			if (priorQuotes.TryGetValue(sym, out var prior))
			{
				iv = prior.ImpliedVolatility ?? 0m;
				if (iv > 0m)
					applied++;
				else if (priorSpot.HasValue && priorSpot.Value > 0m && !string.IsNullOrEmpty(parsed.CallPut))
				{
					var mid = prior.Bid.HasValue && prior.Ask.HasValue && prior.Bid.Value > 0m && prior.Ask.Value > 0m ? (prior.Bid.Value + prior.Ask.Value) / 2m : prior.LastPrice ?? 0m;
					if (mid > 0m)
					{
						var s = OptionMath.ImpliedVol(priorSpot.Value, parsed.Strike, timeYears, OptionMath.RiskFreeRate, mid, parsed.CallPut);
						if (s > 0.011m && s < 4.99m) { iv = s; solved++; }
					}
				}
			}
			if (iv > 0m)
				quotes[sym] = quotes[sym] with { ImpliedVolatility = iv };
			else
			{
				quotes.Remove(sym);
				dropped++;
			}
		}
		AnsiConsole.MarkupLine($"[dim]--exante: {expiry:yyyy-MM-dd} IVs from {Markup.Escape(priorPath)} ({applied} snapshot IVs, {solved} back-solved from prior-day mids, {dropped} contract(s) dropped).[/]");
		return true;
	}

	/// <summary>Identifies chain symbols within the heatmap window (strike range × selected expiries) that
	/// came back from strategy/list with missing OI or IV, then refreshes them via Webull's queryBatch.
	/// Pre-filters expiries to those within <paramref name="maxDteDays"/> days-to-expiry when no explicit
	/// --expiry is set so we don't waste batches on far-dated stub contracts the user won't see.</summary>
	private static async Task<int> RefreshInWindowContractsAsync(
		ApiConfig apiConfig,
		IDictionary<string, OptionContractQuote> chain,
		IReadOnlyDictionary<string, long> derivativeIds,
		string ticker,
		decimal spot,
		DateTime asOf,
		DateTime? expiryFilter,
		decimal strikeRangeFraction,
		int maxDteDays,
		int maxStrikes,
		CancellationToken cancellation)
	{
		var minStrike = spot * (1m - strikeRangeFraction);
		var maxStrike = spot * (1m + strikeRangeFraction);
		var asOfDate = asOf.Date;

		// First pass: find which expiries we'll actually keep, so we only refresh contracts in those buckets.
		var inScopeExpiries = new HashSet<DateTime>();
		var candidateStrikes = new HashSet<decimal>();
		foreach (var sym in chain.Keys)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, p.ExpiryDate)) continue;
			if (p.ExpiryDate.Date < asOfDate) continue;
			if (expiryFilter.HasValue && p.ExpiryDate.Date != expiryFilter.Value.Date) continue;
			inScopeExpiries.Add(p.ExpiryDate.Date);
			if (p.Strike >= minStrike && p.Strike <= maxStrike) candidateStrikes.Add(p.Strike);
		}
		var keptExpiries = expiryFilter.HasValue
			? inScopeExpiries
			: inScopeExpiries.Where(e => (e - asOfDate).Days <= maxDteDays).ToHashSet();

		// Cap rows: pick the maxStrikes strikes closest to spot. High-priced underlyings (e.g. SPY with
		// $1-wide strikes) otherwise pull hundreds of strikes into the heatmap and refresh thousands of
		// contracts, which is slow and hard to read.
		var keptStrikes = candidateStrikes.OrderBy(s => Math.Abs(s - spot)).Take(maxStrikes).ToHashSet();

		var symbolsToRefresh = new List<string>();
		foreach (var (sym, q) in chain)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, p.ExpiryDate)) continue;
			if (!keptExpiries.Contains(p.ExpiryDate.Date)) continue;
			if (!keptStrikes.Contains(p.Strike)) continue;
			var hasOi = q.OpenInterest.HasValue && q.OpenInterest.Value > 0;
			var hasIv = q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m;
			if (hasOi && hasIv) continue;
			symbolsToRefresh.Add(sym);
		}

		if (symbolsToRefresh.Count == 0) return 0;
		Log.Debug($"Refreshing {symbolsToRefresh.Count} non-front-month contract(s) via queryBatch...");
		return await WebullOptionsClient.RefreshContractsAsync(apiConfig, chain, symbolsToRefresh, derivativeIds, cancellation);
	}


	private static decimal? ResolveSpotOverride(string? spotSpec, string ticker)
	{
		if (string.IsNullOrWhiteSpace(spotSpec)) return null;
		foreach (var pair in spotSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = pair.Split(':', 2);
			if (parts.Length == 2 && string.Equals(parts[0].Trim(), ticker, StringComparison.OrdinalIgnoreCase) && decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
				return p;
		}
		return null;
	}

	/// <summary>Unsigned opening-activity screen (the free tier of the flow idea, campaign gex_layers):
	/// a contract whose day volume is a multiple of its standing OI is GUARANTEED to contain opening trades
	/// — total position count can't turn over more than the open interest without someone opening. No print
	/// signing needed; direction stays unknown ("who" and "which way" need signed prints). 0DTE contracts
	/// are excluded (day-trading churn makes volume≫OI the norm there, not a signal). For an offline --date,
	/// the NEXT session's snapshot (when captured) supplies the ΔOI confirmation — the CELH-style overnight
	/// jump, visible the day it was being built. Thresholds fixed on purpose: vol ≥ 2× max(OI,1) and
	/// vol ≥ 250 (the OI floor keeps fresh listings from flooding the list), top 12 by volume, listed by
	/// expiry then strike-descending (matching the heatmap ladder) then volume. When the vendor is in its
	/// overnight OI blackout (chainHasOi false — no contract anywhere in the fetch carries OI), the screen is
	/// skipped with a note instead of rendered: every ratio would be vol/1, turning the panel into a plain
	/// volume leaderboard wearing an "unusual" label.</summary>
	// Thresholds shared by both unusual-activity screens: 2× the standing interest, with an absolute floor that
	// keeps fresh listings and odd lots out of the list.
	private const decimal UnusualMinRatio = 2m;
	private const long UnusualMinFloor = 250;

	private static void RenderUnusualActivity(string ticker, Dictionary<string, OptionContractQuote> quotes, DateTime asOf, bool isOfflineHistorical, bool chainHasOi)
	{
		if (!chainHasOi)
		{
			AnsiConsole.MarkupLine("[dim]Unusual-activity screen skipped: the vendor returned no open interest for any fetched contract (overnight OI blackout), so volume/OI ratios are meaningless right now. Re-run during market hours.[/]");
			AnsiConsole.WriteLine();
			return;
		}
		var hits = new List<(string Sym, OptionParsed P, long Vol, long Oi, decimal Ratio)>();
		foreach (var (sym, q) in quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, p.ExpiryDate)) continue;
			if (p.ExpiryDate.Date <= asOf.Date) continue;   // 0DTE churn is not opening-activity signal
			if (q.Volume is not { } vol || vol < UnusualMinFloor) continue;
			var oi = Math.Max(q.OpenInterest ?? 0, 1);
			var ratio = (decimal)vol / oi;
			if (ratio < UnusualMinRatio) continue;
			hits.Add((sym, p, vol, q.OpenInterest ?? 0, ratio));
		}
		if (hits.Count == 0) return;

		// ΔOI confirmation from the next captured session's snapshot (offline replays only — live/today
		// confirms in tomorrow's file).
		Dictionary<string, OptionContractQuote>? next = null;
		string? nextDate = null;
		if (isOfflineHistorical)
		{
			for (var d = asOf.Date.AddDays(1); d <= asOf.Date.AddDays(5); d = d.AddDays(1))
			{
				var path = Program.ResolvePath($"data/oi/{ticker}/{d:yyyy-MM-dd}.jsonl");
				if (!File.Exists(path)) continue;
				(_, next) = LoadOiSnapshot(path);
				nextDate = d.ToString("yyyy-MM-dd");
				break;
			}
		}

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Unusual opening activity (volume ≥ 2× OI)[/]");
		table.AddColumn(new TableColumn("[bold]Contract[/]").NoWrap());
		table.AddColumn(new TableColumn("[bold]Volume[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]OI[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Vol/OI[/]").RightAligned().NoWrap());
		if (next != null) table.AddColumn(new TableColumn($"[bold]ΔOI → {nextDate}[/]").RightAligned().NoWrap());

		// Selection stays top-12 by volume (the screen's criterion); display order groups by expiry date, then
		// strike-descending within it (matching the heatmap ladder), then volume for same-strike C/P pairs.
		foreach (var h in hits.OrderByDescending(h => h.Vol).Take(12).OrderBy(h => h.P.ExpiryDate).ThenByDescending(h => h.P.Strike).ThenByDescending(h => h.Vol))
		{
			var cells = new List<string>
			{
				$"{h.P.ExpiryDate:M/d} {StrikeLabel(h.P.Strike, wholeGrid: h.P.Strike == Math.Truncate(h.P.Strike))}{h.P.CallPut}",
				h.Vol.ToString("N0"),
				h.Oi.ToString("N0"),
				$"[bold]{h.Ratio:F1}×[/]"
			};
			if (next != null)
			{
				long? nextOi = next.TryGetValue(h.Sym, out var nq) ? nq.OpenInterest : null;
				var delta = nextOi.HasValue ? nextOi.Value - h.Oi : (long?)null;
				cells.Add(delta.HasValue
					? delta.Value > 0 ? $"[green]+{delta.Value:N0}[/]" : delta.Value < 0 ? $"[red]{delta.Value:N0}[/]" : "[dim]0[/]"
					: "[dim]n/a[/]");
			}
			table.AddRow(cells.ToArray());
		}
		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Volume ≥ 2× standing OI guarantees opening trades (positions can't turn over past the open interest without new ones) — DIRECTION unknown without signed prints. 0DTE excluded (churn, not positioning). " + (next != null ? "ΔOI = next captured session's OI change: the overnight jump this activity was building." : "Confirm in tomorrow's snapshot (ΔOI).") + "[/]");
		AnsiConsole.WriteLine();
	}

	/// <summary>The companion screen looking BACKWARD at the analysis day's own expiry: contracts dying today whose
	/// unusual trading happened on a PRIOR session, when they still had ≥1 day to run — the sibling screen's 0DTE-churn
	/// exclusion never shows them, yet that activity is exactly what matured into today's OI and therefore today's
	/// gamma terrain. Two triggers per (session, contract), thresholds shared with the sibling: day volume ≥ 2× that
	/// day's standing OI (arithmetically guaranteed opening flow), or ΔOI ≥ 2× prior OI into the next COVERED snapshot
	/// (the settled evidence — it survives a trade day whose snapshot missed the expiry entirely: the live scraper
	/// writes 0DTE-only files until the nightly ThetaData backfill supplements them, so ΔOI is measured between
	/// consecutive snapshots that actually list the expiry and the cell names the session it landed on). The newest
	/// covered session settles against the chain this command is already looking at — skipped during the vendor's
	/// overnight OI blackout, when live OI reads 0 chain-wide and every delta would be a fake collapse.</summary>
	private static void RenderExpiryLookback(string ticker, Dictionary<string, OptionContractQuote> quotes, DateTime asOf, DateTime targetExpiry, int lookbackDays, bool chainHasOi)
	{
		if (lookbackDays <= 0 || targetExpiry.Date < asOf.Date) return;
		var dir = Program.ResolvePath($"data/oi/{ticker}");
		if (!Directory.Exists(dir)) return;
		var sessionDates = Directory.EnumerateFiles(dir, "????-??-??.jsonl")
			.Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : (DateTime?)null)
			.Where(d => d.HasValue && d.Value < asOf.Date)
			.Select(d => d!.Value)
			.OrderByDescending(d => d).Take(lookbackDays).OrderBy(d => d).ToList();
		if (sessionDates.Count == 0) return;

		// Per session: the target expiry's (volume, OI) book. A snapshot listing no contract for the expiry (the
		// scraper's 0DTE-only files) is "uncovered": it contributes no volume rows and ΔOI spans across it.
		var books = new List<(DateTime Day, Dictionary<string, (long Vol, long Oi)> Book)>();
		var uncovered = new List<DateTime>();
		foreach (var day in sessionDates)
		{
			var (_, snap) = LoadOiSnapshot(Path.Combine(dir, $"{day:yyyy-MM-dd}.jsonl"));
			var book = new Dictionary<string, (long Vol, long Oi)>(StringComparer.OrdinalIgnoreCase);
			foreach (var (sym, q) in snap)
			{
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, targetExpiry) || p.ExpiryDate.Date != targetExpiry.Date) continue;
				book[sym] = (q.Volume ?? 0, q.OpenInterest ?? 0);
			}
			if (book.Count > 0) books.Add((day, book)); else uncovered.Add(day);
		}
		if (books.Count == 0) return;

		// Terminal OI — what the flagged activity matured into. The analysis day's own data/oi snapshot goes in
		// first (the scraper's morning capture spans the full ladder), then the chain the command is already
		// rendering overlays it: a live fetch covers only ~maxStrikes around spot, so without the snapshot the
		// far strikes — where unusual builds live — settle to n/a. OI is static intraday, so the two agree where
		// they overlap; on the offline path quotes IS that snapshot and the overlay is a no-op.
		var terminalOi = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var todayPath = Program.ResolvePath($"data/oi/{ticker}/{asOf:yyyy-MM-dd}.jsonl");
		if (File.Exists(todayPath))
			foreach (var (sym, q) in LoadOiSnapshot(todayPath).Quotes)
			{
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p != null && ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, targetExpiry) && p.ExpiryDate.Date == targetExpiry.Date && q.OpenInterest is { } snapOi)
					terminalOi[sym] = snapOi;
			}
		if (chainHasOi)
			foreach (var (sym, q) in quotes)
			{
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p != null && ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, targetExpiry) && p.ExpiryDate.Date == targetExpiry.Date && q.OpenInterest is { } oi)
					terminalOi[sym] = oi;
			}

		var hits = new List<(DateTime Day, string Sym, OptionParsed P, long Vol, long Oi, long? Delta, DateTime? DeltaTo, bool VolHit, DateTime SortDay, string SessionLabel)>();
		for (var i = 0; i < books.Count; i++)
		{
			var (day, book) = books[i];
			foreach (var (sym, (vol, oi)) in book)
			{
				long? nextOi = null;
				DateTime? nextDay = null;
				for (var j = i + 1; j < books.Count && !nextOi.HasValue; j++)
					if (books[j].Book.TryGetValue(sym, out var later)) { nextOi = later.Oi; nextDay = books[j].Day; }
				if (!nextOi.HasValue && terminalOi.TryGetValue(sym, out var now)) { nextOi = now; nextDay = asOf.Date; }

				var delta = nextOi.HasValue ? nextOi.Value - oi : (long?)null;
				var volHit = vol >= UnusualMinFloor && vol >= UnusualMinRatio * Math.Max(oi, 1);
				var oiHit = delta is { } d && d >= UnusualMinFloor && d >= UnusualMinRatio * Math.Max(oi, 1);
				if (!volHit && !oiHit) continue;
				// Attribute an OI build with no same-day volume evidence to the session(s) it must have traded in:
				// the trading days between this snapshot and the next covered one (the gap the scraper's 0DTE-only
				// files leave). A 8/14-snapshot row whose OI lands on 8/18 traded on Monday 8/17, and should read —
				// and sort — as 8/17 activity, not as an 8/14 row with a mystery delta.
				var gap = new List<DateTime>();
				if (!volHit && nextDay.HasValue)
					for (var g = day.AddDays(1); g < nextDay.Value; g = g.AddDays(1))
						if (MarketCalendar.IsOpen(g)) gap.Add(g);
				var sortDay = gap.Count > 0 ? gap[0] : day;
				var label = gap.Count == 0 ? $"{day:M/d}" : gap.Count == 1 ? $"~{gap[0]:M/d}" : $"~{gap[0]:M/d}–{gap[^1]:M/d}";
				hits.Add((day, sym, ParsingHelpers.ParseOptionSymbol(sym)!, vol, oi, delta, nextDay, volHit, sortDay, label));
			}
		}
		if (hits.Count == 0) return;

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title($"[bold]Unusual activity into the {targetExpiry:M/d} expiry (last {books.Count} covered session(s))[/]");
		table.AddColumn(new TableColumn("[bold]Session[/]").NoWrap());
		table.AddColumn(new TableColumn("[bold]Contract[/]").NoWrap());
		table.AddColumn(new TableColumn("[bold]Volume[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]OI[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Vol/OI[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]ΔOI[/]").RightAligned().NoWrap());

		// Same selection cap as the sibling screen (top 12, by the larger trigger magnitude); same display order too:
		// session ascending (the attributed trade session for gap rows), then strike-descending to match the heatmap
		// ladder, then volume.
		foreach (var h in hits.OrderByDescending(h => Math.Max(h.Vol, h.Delta ?? 0)).Take(12).OrderBy(h => h.SortDay).ThenByDescending(h => h.P.Strike).ThenByDescending(h => h.Vol))
		{
			// A gap-attributed row has NO captured volume for its trade session — the snapshot that day was
			// 0DTE-only. But opening ΔOI new contracts requires at least ΔOI contracts to trade, so the Volume
			// and Vol/OI cells carry that inferred floor rather than the prior covered session's stale (and
			// misleading) numbers.
			var gapped = h.SortDay != h.Day;
			var ratio = (decimal)h.Vol / Math.Max(h.Oi, 1);
			var oiMultiple = h.Delta is { } dm && dm > 0 ? (decimal)dm / Math.Max(h.Oi, 1) : 0m;
			table.AddRow(
				h.SessionLabel,
				$"{StrikeLabel(h.P.Strike, wholeGrid: h.P.Strike == Math.Truncate(h.P.Strike))}{h.P.CallPut}",
				gapped ? $"≥{h.Delta:N0}" : h.VolHit ? h.Vol.ToString("N0") : $"[dim]{h.Vol:N0}[/]",
				h.Oi.ToString("N0"),
				gapped ? $"[bold]≥{oiMultiple:F0}×[/]" : ratio >= UnusualMinRatio ? $"[bold]{ratio:F1}×[/]" : $"[dim]{ratio:F1}×[/]",
				h.Delta is { } dd
					? (dd > 0 ? $"[green]+{dd:N0}[/]" : dd < 0 ? $"[red]{dd:N0}[/]" : "[dim]0[/]") + (oiMultiple > 0m && !gapped ? oiMultiple >= UnusualMinRatio ? $" [bold]{oiMultiple:F0}×[/]" : $" [dim]{oiMultiple:F1}×[/]" : "") + $" [dim]→{h.DeltaTo:M/d}[/]"
					: "[dim]n/a[/]");
		}
		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine($"[dim]Contracts expiring {targetExpiry:M/d} that were unusually active while they still had days to run — this activity matured into today's OI, i.e. today's gamma terrain. Bold Vol/OI ≥ 2× = guaranteed opening flow that session. ΔOI = standing-interest change into the next covered snapshot (→date). A ~session row is a build attributed to the snapshot gap it traded in — that day's own chain was never captured, so its Volume and Vol/OI are inferred floors: opening ΔOI new interest takes at least ΔOI contracts traded. DIRECTION unknown without signed prints.{(uncovered.Count > 0 ? $" No {targetExpiry:M/d} coverage in: {string.Join(", ", uncovered.Select(u => u.ToString("M/d")))} (0DTE-only scraper file, pending backfill)." : "")}[/]");
		AnsiConsole.WriteLine();
	}

	private static void RenderHeader(string ticker, decimal spot, DateTime asOf, DateTime? expiryFilter, GexMatrix matrix)
	{
		var scope = expiryFilter.HasValue ? $"expiry {expiryFilter.Value:yyyy-MM-dd}" : $"{matrix.Expiries.Count} expiration(s)";
		AnsiConsole.MarkupLine($"[bold]{ticker}[/]  spot [yellow]${spot:F2}[/]  asof {asOf:yyyy-MM-dd HH:mm}  {scope}, {matrix.Strikes.Count} strikes");
	}

	/// <summary>
	/// Renders the 2D heatmap: rows = strikes (descending so higher prices appear on top),
	/// columns = expirations (ascending). Cell hue encodes net polarity (green = call-dominated,
	/// red = put-dominated); cell brightness encodes |net GEX| relative to the chain max.
	/// Numeric label inside each cell is the net GEX (compact: "+1.2M", "-450k").
	/// </summary>
	private static void RenderHeatmap(GexMatrix matrix, decimal spot, GreekKind greek)
	{
		AnsiConsole.Write(BuildHeatmapTable(matrix, spot, greek));
		AnsiConsole.MarkupLine(HeatmapLegend(greek));
	}

	/// <summary>--greek both: the gamma and vanna heatmaps in one view. The strike ladders are identical by
	/// construction (strike selection is closest-to-spot, independent of the greek), so the rows line up 1:1 for
	/// left-right comparison. To keep the pair side by side instead of wrapping, the heatmaps are trimmed to the
	/// FRONT expiries that fit the terminal width (per-expiry and chain-totals tables still cover the full set);
	/// Columns then wraps to stacked only if even one expiry per panel cannot fit. Each map keeps its OWN
	/// brightness normalization (gamma and vanna magnitudes are not comparable); both legends print below.</summary>
	private static void RenderHeatmapsSideBySide(GexMatrix gammaMatrix, GexMatrix vannaMatrix, decimal spot)
	{
		// Rendered widths, from the fixed cell geometry: cell content 7 ("+192.8M") + 2 padding + 1 border = 10
		// per expiry column; strike column = label + 2 padding + 1 border; +1 closing border per panel; ~2 for
		// the Columns gutter. Solve for the expiry count that keeps BOTH panels inside the console width.
		var strikeW = gammaMatrix.Strikes.Count > 0 ? gammaMatrix.Strikes.Max(k => StrikeLabel(k, IsWholeGrid(gammaMatrix.Strikes)).Length) : 7;
		var perExpiry = 10;
		var fixedPerPanel = 1 + strikeW + 3;
		var budget = AnsiConsole.Profile.Width - 2 - 2 * fixedPerPanel;
		var maxExpiries = Math.Max(1, budget / (2 * perExpiry));
		var shown = gammaMatrix.Expiries.Take(maxExpiries).ToList();

		var g = BuildHeatmapTable(gammaMatrix, spot, GreekKind.Gamma, shown).Title("[bold]GEX (gamma)[/]");
		var v = BuildHeatmapTable(vannaMatrix, spot, GreekKind.Vanna, shown).Title("[bold]VEX (vanna)[/]");
		AnsiConsole.Write(new Columns(g, v) { Expand = false });
		if (shown.Count < gammaMatrix.Expiries.Count)
			AnsiConsole.MarkupLine($"[dim]Heatmaps show the first {shown.Count} of {gammaMatrix.Expiries.Count} expiries so both panels fit {AnsiConsole.Profile.Width} columns side by side — the per-expiration and chain-totals tables below cover all of them (narrow with --dte/--expiry, or widen the terminal, to see more).[/]");
		AnsiConsole.MarkupLine(HeatmapLegend(GreekKind.Gamma));
		AnsiConsole.MarkupLine(HeatmapLegend(GreekKind.Vanna));
	}

	private static Table BuildHeatmapTable(GexMatrix matrix, decimal spot, GreekKind greek, IReadOnlyList<DateTime>? expiries = null)
	{
		var shownExpiries = expiries ?? matrix.Expiries;
		var wholeGrid = IsWholeGrid(matrix.Strikes);
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn(new TableColumn("[bold]Strike[/]").RightAligned().NoWrap());
		foreach (var exp in shownExpiries)
			table.AddColumn(new TableColumn($"[bold]{exp:M/d}[/]").Centered().NoWrap());

		// Strike closest to spot — gets the bold yellow ATM marker.
		var atmStrike = matrix.Strikes.OrderBy(s => Math.Abs(s - spot)).FirstOrDefault();
		var maxAbsNet = Math.Max(matrix.MaxAbsNet, 1m);

		foreach (var bucket in GroupStrikesForDisplay(matrix.Strikes, spot))
		{
			var isAtm = bucket.Contains(atmStrike);
			var strikeMarkup = isAtm ? $"[bold yellow]{BucketLabel(bucket, wholeGrid)}[/]" : BucketLabel(bucket, wholeGrid);

			var cells = new List<string> { strikeMarkup };
			foreach (var exp in shownExpiries)
			{
				var cell = AggregateBucketCell(bucket, s => matrix.Cells.TryGetValue((exp, s), out var c) ? c : null);
				// The gravity marker is a max-gross-GAMMA anchor. Under vanna the same strike would just be the biggest
				// |vanna|×OI pile, which is precisely the "level" reading this greek does not support — so it is not drawn.
				var isGravity = greek == GreekKind.Gamma && matrix.GravityByExpiry.TryGetValue(exp, out var grav) && grav.HasValue && bucket.Contains(grav.Value);
				cells.Add(BuildHeatmapCellMarkup(cell, maxAbsNet, isGravity));
			}
			table.AddRow(cells.ToArray());
		}

		return table;
	}

	/// <summary>One column of a TIME-axis heatmap — an --intraday bucket or a build-up session. Two header lines (the
	/// mark and its spot), the per-strike cells, and the gravity strike to mark, which is null under vanna since vanna
	/// has no gravity.</summary>
	private sealed record HeatColumn(string Header, string SubHeader, Dictionary<decimal, GexCell> Cells, decimal? Gravity);

	/// <summary>Rows = <paramref name="strikes"/> in the order given, columns = <paramref name="columns"/>. Shared by
	/// the --intraday migration panels and the --expiry build-up panel so a gamma table and a vanna table built off one
	/// strike ladder align row-for-row when placed side by side.</summary>
	private static Table BuildColumnHeatmapTable(string title, IReadOnlyList<HeatColumn> columns, IReadOnlyList<decimal> strikes, bool wholeGrid, decimal maxAbsNet, decimal spot)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title(title);
		table.AddColumn(new TableColumn("[bold]Strike[/]").RightAligned().NoWrap());
		foreach (var c in columns)
			table.AddColumn(new TableColumn($"[bold]{c.Header}[/]\n[dim]{c.SubHeader}[/]").Centered().NoWrap());

		foreach (var bucket in GroupStrikesForDisplay(strikes, spot))
		{
			var row = new List<string> { BucketLabel(bucket, wholeGrid) };
			foreach (var c in columns)
			{
				var cell = AggregateBucketCell(bucket, s => c.Cells.TryGetValue(s, out var v) ? v : null);
				var isGravity = c.Gravity.HasValue && bucket.Contains(c.Gravity.Value);
				row.Add(BuildHeatmapCellMarkup(cell, maxAbsNet, isGravity));
			}
			table.AddRow(row.ToArray());
		}
		return table;
	}

	/// <summary>Resolves the build-up view's first column: --since when given, else the Monday of <paramref name="asOf"/>'s
	/// week — for a weekly expiry that is its own Monday-to-date build, which is the read this view exists for.</summary>
	private static DateTime ResolveBuildupStart(string? since, DateTime asOf) => since != null
		? DateTime.ParseExact(since, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date
		: asOf.Date.AddDays(-(((int)asOf.Date.DayOfWeek + 6) % 7));

	/// <summary>Renders the --expiry build-up heatmap: rows = strikes, columns = trading SESSIONS from
	/// <paramref name="since"/> through <paramref name="asOf"/>, every one of them showing the same pinned
	/// <paramref name="expiry"/>. Each prior column is rebuilt from that session's own data/oi snapshot — the OI, IVs and
	/// close spot actually captured that day, at the DTE the expiry actually had — so the panel shows one series' book
	/// filling in over the week instead of a single day re-read N times. The last column is the chain this run already
	/// loaded for asOf (live fetch or its own snapshot), so it agrees with the per-expiry and totals tables below it.
	/// Sessions whose snapshot does not carry the expiry are named in a footnote rather than drawn as blank columns.</summary>
	private static void RenderExpiryBuildup(string ticker, DateTime expiry, DateTime asOf, DateTime since, Dictionary<string, OptionContractQuote> quotes, decimal spot, AnalyzeGexSettings settings, GreekKind greek, bool withVex)
	{
		var strikeRange = settings.StrikeRangePct / 100m;
		var sessions = new List<(DateTime Date, decimal Spot, Dictionary<string, OptionContractQuote> Quotes)>();
		var dir = Program.ResolvePath($"data/oi/{ticker}");
		if (Directory.Exists(dir))
			foreach (var file in Directory.EnumerateFiles(dir, "????-??-??.jsonl").OrderBy(f => f, StringComparer.Ordinal))
			{
				if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
				if (d.Date < since.Date || d.Date >= asOf.Date) continue;   // asOf gets the already-loaded chain below, not a re-read
				var (snapSpot, snapQuotes) = LoadOiSnapshot(file);
				if (!snapSpot.HasValue || snapSpot.Value <= 0m || snapQuotes.Count == 0) continue;
				sessions.Add((d.Date, snapSpot.Value, snapQuotes));
			}
		sessions.Add((asOf.Date, spot, quotes));

		var gamma = new List<HeatColumn>();
		var vanna = new List<HeatColumn>();
		var union = new HashSet<decimal>();
		var absent = new List<DateTime>();
		foreach (var (d, s, q) in sessions)
		{
			var dte = Math.Max(0, (expiry - d).Days);
			var m = GexMatrix.Build(q, ticker, s, d, expiryFilter: expiry, strikeRange, maxDteDays: dte, settings.MaxStrikes, greek);
			var cells = new Dictionary<decimal, GexCell>();
			foreach (var k in m.Strikes)
				if (m.Cells.TryGetValue((expiry, k), out var c)) cells[k] = c;
			if (cells.Count == 0) { absent.Add(d); continue; }
			m.GravityByExpiry.TryGetValue(expiry, out var grav);
			gamma.Add(new HeatColumn($"{d:M/d}", $"{s:F2}", cells, greek == GreekKind.Gamma ? grav : null));
			foreach (var k in cells.Keys) union.Add(k);
			// Appended in the SAME branch, empty or not, so the two lists stay index-aligned for the width trim below.
			if (withVex)
			{
				var vm = GexMatrix.Build(q, ticker, s, d, expiryFilter: expiry, strikeRange, maxDteDays: dte, settings.MaxStrikes, GreekKind.Vanna);
				var vCells = new Dictionary<decimal, GexCell>();
				foreach (var k in vm.Strikes)
					if (vm.Cells.TryGetValue((expiry, k), out var vc)) vCells[k] = vc;
				vanna.Add(new HeatColumn($"{d:M/d}", $"{s:F2}", vCells, Gravity: null));
			}
		}

		if (gamma.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No {expiry:yyyy-MM-dd} cells in any session from {since:yyyy-MM-dd} to {asOf:yyyy-MM-dd} — no captured snapshot in that span lists the expiry with OI inside ±{settings.StrikeRangePct}% of spot.[/]");
			return;
		}

		// Each session banded its own strikes around its own spot, so the union can span far more rows than one day's
		// window. Cap it around the LAST column's spot — that is the ladder the reader is actually anchored on.
		var strikeSet = union.OrderBy(k => Math.Abs(k - spot)).Take(settings.MaxStrikes).ToHashSet();
		var ladder = strikeSet.OrderByDescending(k => k).ToList();
		var wholeGrid = IsWholeGrid(ladder);
		decimal MaxNet(List<HeatColumn> cols) => Math.Max(1m, cols.SelectMany(c => c.Cells).Where(kv => strikeSet.Contains(kv.Key)).Select(kv => Math.Abs(kv.Value.Net)).DefaultIfEmpty(0m).Max());

		// Same fixed cell geometry as the other paired heatmaps. Oldest columns are dropped first: the near-expiry
		// sessions carry the positioning that still matters.
		var panels = withVex ? 2 : 1;
		var strikeW = ladder.Max(k => StrikeLabel(k, wholeGrid).Length);
		var colW = 3 + Math.Max(7, gamma.Max(c => c.SubHeader.Length));
		var fit = Math.Max(1, (AnsiConsole.Profile.Width - 2 - panels * (1 + strikeW + 3)) / (panels * colW));
		var dropped = Math.Max(0, gamma.Count - fit);
		if (dropped > 0)
		{
			gamma = gamma.Skip(dropped).ToList();
			if (withVex) vanna = vanna.Skip(dropped).ToList();
		}

		AnsiConsole.MarkupLine($"[bold]{ticker}[/] {expiry:yyyy-MM-dd} expiry — build-up over {gamma.Count} session(s), {gamma[0].Header} → {gamma[^1].Header}");
		AnsiConsole.MarkupLine($"[dim]Each column is that session's own data/oi snapshot — the OI and IVs captured that day, priced at that day's close spot and its real DTE. Column header = session, subhead = its spot.{(dropped > 0 ? $" Oldest {dropped} session(s) dropped to fit {AnsiConsole.Profile.Width} columns; narrow with --since." : "")}[/]");

		var table = BuildColumnHeatmapTable(greek == GreekKind.Vanna ? "[bold]VEX (vanna)[/]" : "[bold]GEX (gamma)[/]", gamma, ladder, wholeGrid, MaxNet(gamma), spot);
		if (withVex)
			AnsiConsole.Write(new Columns(table, BuildColumnHeatmapTable("[bold]VEX (vanna)[/]", vanna, ladder, wholeGrid, MaxNet(vanna), spot)) { Expand = false });
		else
			AnsiConsole.Write(table);

		AnsiConsole.MarkupLine(HeatmapLegend(greek));
		if (withVex) AnsiConsole.MarkupLine(HeatmapLegend(GreekKind.Vanna));
		if (absent.Count > 0)
			AnsiConsole.MarkupLine($"[dim]No {expiry:yyyy-MM-dd} cells on {string.Join(", ", absent.Select(d => d.ToString("M/d")))} — the series was unlisted, carried no OI, or sat outside ±{settings.StrikeRangePct}% of that session's spot.[/]");
	}

	private static string HeatmapLegend(GreekKind greek) => greek == GreekKind.Vanna
		? "[dim]Cell = net VEX ($call vanna×OI − $put vanna×OI), in dollars of dealer delta per ONE vol point of IV change. [green]Green[/] = dealers gain delta when IV rises (they sell underlying to re-hedge), [red]red[/] = they lose delta (they buy); brightness ∝ |net|. Multiply by the vol-point move you expect: the sign of net × ΔIV is the hedging flow. Note the polarity flip near the money — that is vanna's d2 sign change, not a level.[/]"
		: "[dim]Cell = net GEX ($call gamma×OI − $put gamma×OI). [green]Green[/] = call-dominated, [red]red[/] = put-dominated, brightness ∝ |net|. Bold + underlined cell (e.g. [bold underline]+1.2M[/]) = per-expiry gravity strike (max gross gamma).[/]";

	/// <summary>Strike label for the heatmap ladders. Cents are dropped only when the WHOLE displayed ladder
	/// is integer ("$7,885" on the SPY/SPXW dollar grids) — a ladder with any fractional strike (GME's $0.50
	/// steps, sub-dollar grids) keeps two decimals on every row so the column reads uniformly instead of
	/// mixing "$27" with "$26.50".</summary>
	private static string StrikeLabel(decimal strike, bool wholeGrid) => wholeGrid ? $"${strike:N0}" : $"${strike:N2}";

	private static bool IsWholeGrid(IEnumerable<decimal> strikes) => strikes.All(s => s == Math.Truncate(s));

	/// <summary>Buckets a strike ladder into display rows: single-strike resolution for the <paramref name="coreRadius"/>
	/// strikes nearest spot on each side, then geometrically wider buckets (2, 2, 4, 4, 8, 8, ... capped at
	/// <paramref name="maxBucketSize"/>) moving further out-of-the-money. Row count grows with log(strikes) instead of
	/// strikes itself, so raising --max-strikes to fetch a fuller book (needed for Gravity/analytics correctness — see
	/// <see cref="GexMatrix.Build"/>) doesn't force scrolling through hundreds of rows to read the table: the near-money
	/// detail that matters for a pin read stays exact, and the far tail — mostly noise or a single legacy block anyway —
	/// collapses into a handful of summary rows. Call and put sides are bucketed independently so the grouping stays
	/// symmetric around spot regardless of how lopsided the raw ladder is (a thin book can have far more strikes listed
	/// on one side than the other). Returns groups ordered highest-strike-first, matching the table's display order.</summary>
	private static List<List<decimal>> GroupStrikesForDisplay(IReadOnlyList<decimal> strikes, decimal spot, int coreRadius = 15, int maxBucketSize = 25)
	{
		List<List<decimal>> GroupOneSide(List<decimal> nearestToFarthest)
		{
			var groups = new List<List<decimal>>();
			int i = 0, n = 0, step = 1;
			while (i < nearestToFarthest.Count)
			{
				var size = Math.Min(n < coreRadius ? 1 : step, nearestToFarthest.Count - i);
				groups.Add(nearestToFarthest.GetRange(i, size));
				i += size;
				n++;
				if (n >= coreRadius) step = Math.Min(step * 2, maxBucketSize);
			}
			return groups;
		}

		var above = GroupOneSide(strikes.Where(s => s > spot).OrderBy(s => s).ToList());   // ascending = nearest spot first
		var atSpot = strikes.Where(s => s == spot).ToList();
		var below = GroupOneSide(strikes.Where(s => s < spot).OrderByDescending(s => s).ToList()); // descending = nearest spot first

		var rows = new List<List<decimal>>();
		for (var i = above.Count - 1; i >= 0; i--) rows.Add(above[i]); // farthest-above first (highest price at top)
		if (atSpot.Count > 0) rows.Add(atSpot);
		rows.AddRange(below); // nearest-below first, working down to farthest
		return rows;
	}

	/// <summary>Row label for a <see cref="GroupStrikesForDisplay"/> bucket: a single strike unchanged, a multi-strike
	/// bucket as "lo–hi".</summary>
	private static string BucketLabel(IReadOnlyList<decimal> bucket, bool wholeGrid) => bucket.Count == 1
		? StrikeLabel(bucket[0], wholeGrid)
		: $"{StrikeLabel(bucket.Min(), wholeGrid)}–{StrikeLabel(bucket.Max(), wholeGrid).TrimStart('$')}";

	/// <summary>Sums the cells of a <see cref="GroupStrikesForDisplay"/> bucket via <paramref name="lookup"/>
	/// (per-side dollar exposures add linearly), or null when no strike in the bucket has a cell.</summary>
	private static GexCell? AggregateBucketCell(IReadOnlyList<decimal> bucket, Func<decimal, GexCell?> lookup)
	{
		decimal call = 0m, put = 0m;
		var any = false;
		foreach (var s in bucket)
		{
			if (lookup(s) is not { } cell) continue;
			call += cell.CallGex;
			put += cell.PutGex;
			any = true;
		}
		return any ? new GexCell(call, put) : null;
	}

	private static string BuildHeatmapCellMarkup(GexCell? cell, decimal maxAbsNet, bool isGravity)
	{
		if (cell == null || cell.Gross == 0m)
			return "[grey15]   ·   [/]";

		var net = cell.Net;
		var intensity = Math.Min(1.0, (double)(Math.Abs(net) / maxAbsNet));
		// gamma-correct so small but nonzero cells stay visible against a dark cell baseline
		var shaped = Math.Pow(intensity, 0.55);
		var brightness = (int)Math.Round(35 + 200 * shaped);

		string bg, fg;
		if (net >= 0m)
		{
			bg = $"rgb(0,{brightness},0)";
			fg = brightness > 140 ? "black" : "grey85";
		}
		else
		{
			bg = $"rgb({brightness},0,0)";
			fg = brightness > 140 ? "black" : "grey85";
		}

		var label = FormatCompact(net).PadLeft(6);
		var content = isGravity ? $"[bold underline {fg} on {bg}]{Markup.Escape(label)}[/]" : $"[{fg} on {bg}]{Markup.Escape(label)}[/]";
		return content;
	}

	private static void RenderTotals(GexMatrix matrix, decimal spot, GreekKind greek)
	{
		AnsiConsole.Write(BuildTotalsTable(matrix, spot, greek));
		if (greek == GreekKind.Vanna)
			AnsiConsole.MarkupLine(VannaTotalsFootnote);
	}

	/// <summary>--greek both: the two chain-totals tables in one view, side by side when the width allows.</summary>
	private static void RenderTotalsSideBySide(GexMatrix gammaMatrix, GexMatrix vannaMatrix, decimal spot)
	{
		var g = BuildTotalsTable(gammaMatrix, spot, GreekKind.Gamma).Title("[bold]Chain totals — GEX[/]");
		var v = BuildTotalsTable(vannaMatrix, spot, GreekKind.Vanna).Title("[bold]Chain totals — VEX[/]");
		AnsiConsole.Write(new Columns(g, v) { Expand = false });
		AnsiConsole.MarkupLine(VannaTotalsFootnote);
	}

	private const string VannaTotalsFootnote = "[dim]Net VEX = dollars of dealer delta gained per vol point of IV RISE. The flow row reads it the way an IV move actually arrives: dealers re-hedge by trading the OPPOSITE sign of the delta they pick up, so net × ΔIV > 0 means they sell the underlying and < 0 means they buy. This is a flow estimate, not a target price.[/]";

	private static Table BuildTotalsTable(GexMatrix matrix, decimal spot, GreekKind greek)
	{
		var isVanna = greek == GreekKind.Vanna;
		var label = isVanna ? "VEX" : "GEX";
		var totalAbs = Math.Abs(matrix.TotalCallGex) + Math.Abs(matrix.TotalPutGex);
		var net = matrix.TotalCallGex - matrix.TotalPutGex;
		var netFrac = totalAbs > 0m ? net / totalAbs : 0m;
		var netSign = net >= 0m ? "+" : "−";
		var netColor = net >= 0m ? "green" : "red";

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Chain totals[/]");
		table.AddColumn("Metric");
		table.AddColumn(new TableColumn("Value").RightAligned().NoWrap());
		table.AddRow($"Total call {label}", FormatSignedDollars(matrix.TotalCallGex));
		table.AddRow($"Total put {label}", FormatSignedDollars(matrix.TotalPutGex));
		table.AddRow("Total absolute (gross)", FormatCompactDollars(totalAbs));
		table.AddRow("Net (call − put)", $"[bold {netColor}]{netSign}{FormatCompactDollars(Math.Abs(net))}[/]");
		if (isVanna)
		{
			// No net-fraction row: it reads |net| against gross to say "how call- vs put-dominated", which is a
			// gamma statement. Call and put vanna carry OPPOSITE signs under this convention (puts sit below the d2
			// flip), so call − put adds their magnitudes and the ratio pins to ±1.00 on every chain — pure noise.
			table.AddRow("Flow @ IV −5 pts", FormatVannaFlow(net, volPoints: -5m, spot));
		}
		else
		{
			table.AddRow("Net fraction", $"[bold {netColor}]{netFrac:+0.00;-0.00}[/]  [dim](+1 = pure call, −1 = pure put)[/]");
			table.AddRow("Gamma flip", FormatGammaFlipDisplay(matrix.FindGammaFlip(spot), spot));
		}
		return table;
	}

	/// <summary>Renders "$1.2M" green for a positive exposure and "−$1.2M" red for a negative one. Gamma sides are
	/// always non-negative; vanna sides carry a real sign, so the total cannot be prefixed unconditionally.</summary>
	private static string FormatSignedDollars(decimal value)
	{
		if (value == 0m) return "[dim]$0[/]";
		return value > 0m ? $"[green]+{FormatCompactDollars(value)}[/]" : $"[red]−{FormatCompactDollars(Math.Abs(value))}[/]";
	}

	/// <summary>Translates net vanna into the underlying trade it implies for the given IV move: dealers pick up
	/// (net × volPoints) dollars of delta and hedge it away by trading the opposite sign, so a vol crush against a
	/// positive net vanna book is dealer BUYING. Shown in both notional and shares (notional ÷ spot).</summary>
	private static string FormatVannaFlow(decimal netVanna, decimal volPoints, decimal spot)
	{
		var deltaGained = netVanna * volPoints;
		var hedgeNotional = -deltaGained;
		if (hedgeNotional == 0m || spot <= 0m) return "[dim]—[/]";
		var buying = hedgeNotional > 0m;
		var shares = Math.Abs(hedgeNotional) / spot;
		var verb = buying ? "BUY" : "SELL";
		var color = buying ? "green" : "red";
		return $"[bold {color}]dealers {verb} {FormatCompactDollars(Math.Abs(hedgeNotional))}[/]  [dim](≈{shares:N0} shares)[/]";
	}

	/// <summary>Vanna's per-expiry panel. Deliberately NOT the gamma one: gravity, call/put walls, gamma flip and max
	/// pain are all strike-anchored gamma concepts, and reprinting them over a vanna matrix would assert exactly the
	/// price-magnet reading vanna does not support. What vanna has to say is per-expiry: how much dealer delta each
	/// maturity's book gains per vol point, and which way that forces them to trade when IV moves.</summary>
	private static void RenderPerExpiryVanna(GexMatrix matrix, DateTime asOf)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Per-expiration (vanna)[/]");
		table.AddColumn(new TableColumn("[bold]Expiry[/]").NoWrap());
		table.AddColumn(new TableColumn("[bold]DTE[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Net VEX[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Gross[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Share of gross[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Hedge @ IV −5 pts[/]").RightAligned().NoWrap());

		var chainGross = matrix.Expiries.Sum(e => ExpiryTotals(matrix, e).Gross);
		foreach (var exp in matrix.Expiries)
		{
			var (net, gross) = ExpiryTotals(matrix, exp);
			var dte = Math.Max(0, (exp.Date - asOf.Date).Days);
			var share = chainGross > 0m ? gross / chainGross : 0m;
			var hedge = -net * -5m;
			var hedgeCell = hedge == 0m ? "[dim]—[/]" : hedge > 0m ? $"[green]buy {FormatCompactDollars(hedge)}[/]" : $"[red]sell {FormatCompactDollars(Math.Abs(hedge))}[/]";
			table.AddRow($"{exp:yyyy-MM-dd}", dte.ToString(), FormatSignedDollars(net), FormatCompactDollars(gross), $"{share:P0}", hedgeCell);
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Net VEX = $ of dealer delta per vol point of IV rise for that maturity; Gross = |call| + |put| (how much vanna the maturity carries at all, regardless of netting). Front expiries dominate the gross on an IV shock because their IV moves most — read the hedge column as a direction and rough size, never as a target strike.[/]");
	}

	/// <summary>(net, gross) VEX summed across every displayed strike of one expiry.</summary>
	private static (decimal Net, decimal Gross) ExpiryTotals(GexMatrix matrix, DateTime expiry)
	{
		decimal net = 0m, gross = 0m;
		foreach (var strike in matrix.Strikes)
			if (matrix.Cells.TryGetValue((expiry, strike), out var cell))
			{
				net += cell.Net;
				gross += cell.Gross;
			}
		return (net, gross);
	}

	/// <summary>Formats the gamma flip cell: price + % distance from spot + regime label, colored by regime.
	/// Spot above flip → positive gamma regime (dealers dampen vol); spot below → negative gamma regime (dealers amplify vol).</summary>
	private static string FormatGammaFlipDisplay(decimal? flip, decimal spot)
	{
		if (!flip.HasValue) return "[dim]not in window[/]  [dim](widen --strike-range)[/]";
		var deltaPct = (flip.Value / spot - 1m) * 100m;
		var sign = deltaPct >= 0m ? "+" : "−";
		var positive = spot >= flip.Value;
		var color = positive ? "green" : "red";
		var regime = positive ? "positive gamma" : "negative gamma";
		return $"[bold {color}]${flip.Value:N2}[/]  [dim]({sign}{Math.Abs(deltaPct):F1}% vs spot, {regime} regime)[/]";
	}

	/// <summary>Renders one row per expiration with the strike-anchored signals that aren't visible from the heatmap alone:
	/// gravity (max gross gamma), top call wall (resistance) and put wall (support), gamma flip (where dealer net dollar-gamma
	/// crosses zero for that expiry only), and max pain (strike minimizing total ITM payout). Lets the reader compare how the
	/// per-expiry anchors line up against each other and against spot.</summary>
	private static void RenderPerExpirySummary(GexMatrix matrix, decimal spot, DateTime asOf)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Per-expiration[/]");
		table.AddColumn(new TableColumn("[bold]Expiry[/]").NoWrap());
		table.AddColumn(new TableColumn("[bold]DTE[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Gravity[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Gross γ[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold green]Call wall[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold red]Put wall[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold green]OI wall[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold red]OI wall[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold green]Vol wall[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold red]Vol wall[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Gamma flip[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Max pain[/]").RightAligned().NoWrap());

		foreach (var exp in matrix.Expiries)
		{
			var dte = Math.Max(0, (exp.Date - asOf.Date).Days);
			matrix.GravityByExpiry.TryGetValue(exp, out var gravity);
			var (callWall, putWall) = matrix.FindWalls(exp);
			var (oiCallWall, oiPutWall) = matrix.FindOiWalls(exp);
			var (volCallWall, volPutWall) = matrix.FindVolumeWalls(exp);
			var flip = matrix.FindGammaFlip(spot, exp);
			var maxPain = matrix.FindMaxPain(exp, spot);

			var gravityCell = gravity.HasValue ? $"${gravity.Value:N2}" : "[dim]—[/]";
			var grossCell = gravity.HasValue && matrix.Cells.TryGetValue((exp, gravity.Value), out var gc) ? FormatCompactDollars(gc.Gross) : "[dim]—[/]";
			table.AddRow($"{exp:yyyy-MM-dd}", dte.ToString(), gravityCell, grossCell,
				FormatWallStrike(callWall, "green"), FormatWallStrike(putWall, "red"),
				FormatWallStrike(oiCallWall, "green"), FormatWallStrike(oiPutWall, "red"),
				FormatWallStrike(volCallWall, "green"), FormatWallStrike(volPutWall, "red"),
				FormatPriceVsSpotCompact(flip, spot, regimeColor: true), FormatPriceVsSpotCompact(maxPain, spot, regimeColor: false));
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Gravity = strike with max gross gamma; Gross γ = the gross GEX ($call γ×OI + $put γ×OI) at that strike (the value underlined in the heatmap above). [green]Call wall[/] / [red]put wall[/] = strike with the largest call / put GEX for that expiry (resistance / support, the GEX cluster). OI wall = strike with the largest standing call / put open interest for that expiry (the OI cluster). Vol wall = strike with the largest call / put day volume for that expiry (the volume cluster). Gamma flip = where dealer net dollar-gamma crosses 0 ([green]green[/] = spot in positive-γ regime, [red]red[/] = negative-γ). Max pain = strike minimizing total ITM payout (where most contracts expire worthless).[/]");
	}

	private static string FormatWallStrike(decimal? strike, string color) => strike.HasValue ? $"[bold {color}]${strike.Value:N2}[/]" : "[dim]—[/]";

	/// <summary>Compact "$price (±x.x%)" cell. When <paramref name="regimeColor"/> is true (gamma flip), the price is
	/// green if spot ≥ price (positive-γ regime) and red otherwise. When false (max pain), the price is bold but neutral —
	/// max-pain pinning isn't directionally signed the way gamma regime is.</summary>
	private static string FormatPriceVsSpotCompact(decimal? price, decimal spot, bool regimeColor)
	{
		if (!price.HasValue) return "[dim]—[/]";
		var deltaPct = (price.Value / spot - 1m) * 100m;
		var sign = deltaPct >= 0m ? "+" : "−";
		if (regimeColor)
		{
			var color = spot >= price.Value ? "green" : "red";
			return $"[bold {color}]${price.Value:N2}[/] [dim]({sign}{Math.Abs(deltaPct):F1}%)[/]";
		}
		return $"[bold]${price.Value:N2}[/] [dim]({sign}{Math.Abs(deltaPct):F1}%)[/]";
	}

	/// <summary>Renders the top N call walls and top N put walls. A "wall" is a strike with an outsized
	/// concentration of dealer-hedging exposure on one side; call walls cap the upside (resistance) and
	/// put walls cushion drawdowns (support). Ranks across the entire selected window (all strikes × expiries).</summary>
	private static void RenderWalls(GexMatrix matrix, int topN)
	{
		var perStrikeCall = matrix.Cells.GroupBy(kv => kv.Key.Strike).Select(g => (Strike: g.Key, CallGex: g.Sum(x => x.Value.CallGex))).ToList();
		var perStrikePut  = matrix.Cells.GroupBy(kv => kv.Key.Strike).Select(g => (Strike: g.Key, PutGex:  g.Sum(x => x.Value.PutGex))).ToList();

		var topCalls = perStrikeCall.Where(x => x.CallGex > 0).OrderByDescending(x => x.CallGex).Take(topN).ToList();
		var topPuts  = perStrikePut.Where(x  => x.PutGex  > 0).OrderByDescending(x => x.PutGex).Take(topN).ToList();

		// Pad the column edges so each table renders wide enough to fit its full parenthetical title
		// without Spectre wrapping it. Default column padding is (1,1) → ~21 cols total; (3,3) takes
		// the table to ~29 cols, comfortably wider than "Call walls (resistance)" (23 chars).
		var callTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green).Title("[bold green]Call walls (resistance)[/]");
		callTable.AddColumn(new TableColumn("Strike").RightAligned().NoWrap().Padding(3, 3));
		callTable.AddColumn(new TableColumn("Call GEX").RightAligned().NoWrap().Padding(3, 3));
		foreach (var (strike, gex) in topCalls)
			callTable.AddRow($"${strike:N2}", $"[green]{FormatCompactDollars(gex)}[/]");
		if (topCalls.Count == 0) callTable.AddRow("[dim]none[/]", "[dim]—[/]");

		var putTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Red).Title("[bold red]Put walls (support)[/]");
		putTable.AddColumn(new TableColumn("Strike").RightAligned().NoWrap().Padding(3, 3));
		putTable.AddColumn(new TableColumn("Put GEX").RightAligned().NoWrap().Padding(3, 3));
		foreach (var (strike, gex) in topPuts)
			putTable.AddRow($"${strike:N2}", $"[red]{FormatCompactDollars(gex)}[/]");
		if (topPuts.Count == 0) putTable.AddRow("[dim]none[/]", "[dim]—[/]");

		// 4-space gap column between the two panels so they don't visually merge.
		var grid = new Grid();
		grid.AddColumn();
		grid.AddColumn(new GridColumn().Width(4));
		grid.AddColumn();
		grid.AddRow(callTable, new Markup(""), putTable);
		AnsiConsole.Write(grid);
	}

	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	/// <summary>Reads data/intraday/<TICKER>/<date>.csv (header timestamp_utc,open,high,low,close,volume; UTC ISO
	/// timestamps), converts each row to ET, keeps the RTH window (09:30–16:00 ET), and returns ET-time-of-day → close.
	/// Returns empty when the file is absent.</summary>
	/// <summary>Refreshes today's <c>data/intraday/&lt;TICKER&gt;/&lt;date&gt;.csv</c> through the same Webull chart path that normally maintains it (<see cref="WebullAnalytics.AI.Replay.IntradayBarCache"/>
	/// persists fetched bars as its side effect), so the running-day heatmap's spot columns reach the current minute. Best-effort: on any failure the heatmap still renders from whatever the tape file
	/// already holds — a warning notes that the columns stop at the last cached bar.</summary>
	private static async Task RefreshIntradayTapeAsync(string ticker, ApiConfig? apiConfig, CancellationToken cancellation)
	{
		if (apiConfig == null) return;
		try
		{
			var cache = new WebullAnalytics.AI.Replay.IntradayBarCache(WebullAnalytics.AI.Sources.WebullIntradayBars.CreateFetcher(apiConfig));
			var nowEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
			var openUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nowEt.Date.AddHours(9).AddMinutes(30), NyTz));
			await cache.GetBarsAsync(ticker, openUtc, DateTimeOffset.UtcNow, BarInterval.M1, includeExtended: true, cancellation);
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Intraday tape refresh failed ({Markup.Escape(ex.Message)}); heatmap columns end at the tape's last cached bar.[/]");
		}
	}

	private static SortedDictionary<TimeSpan, decimal> LoadIntradaySpots(string ticker, DateTime date)
	{
		var spots = new SortedDictionary<TimeSpan, decimal>();
		var path = Program.ResolvePath($"data/intraday/{ticker}/{date:yyyy-MM-dd}.csv");
		if (!File.Exists(path)) return spots;

		var rthOpen = new TimeSpan(9, 30, 0);
		var rthClose = new TimeSpan(16, 0, 0);
		var first = true;
		foreach (var line in File.ReadLines(path))
		{
			if (first) { first = false; continue; } // header
			if (string.IsNullOrWhiteSpace(line)) continue;
			var parts = line.Split(',');
			if (parts.Length < 5) continue;
			if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			var et = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, NyTz);
			var tod = et.TimeOfDay;
			if (tod < rthOpen || tod > rthClose) continue;
			spots[tod] = close;
		}
		return spots;
	}

	/// <summary>Renders the intraday GEX gravity-migration heatmap for ONE expiry: rows = strikes (descending),
	/// columns = RTH hour marks. At each hour the per-strike GEX is recomputed at that hour's intraday spot against the
	/// day's fixed OI, so the gravity strike (bold-underlined) is seen migrating as price moves. Brightness ∝ |net|
	/// across all hours; green = call-dominated, red = put-dominated. <paramref name="expiry"/> is usually
	/// <paramref name="date"/>'s own 0DTE, but any later listed expiry works — on a root with no daily expirations
	/// (SPCX and friends list Fridays only) that is the sole way to get this panel at all, and it reads as "where was
	/// the 8/7 book's magnet sitting as Thursday traded". When the minute-quote store (data/quotes, written by the
	/// wa-scraper / ThetaData sync) covers this expiry on this day, each bucket's IVs are back-solved from THAT
	/// minute's NBBO mids instead of the morning snapshot's frozen values — the snapshot IVs age badly through a 0DTE
	/// session (IV collapses intraday, sharpening gamma toward ATM), which is why a frozen-IV replay disagrees with
	/// what the live command showed. On the RUNNING day (no minute store yet) the same time-matching runs off this
	/// session's own data/iv chain captures instead, so already-watched buckets stop migrating between calls. A
	/// data/gex live log (when present) is rendered as "·live" footer rows (gravity + walls).</summary>
	private static TimeSpan AbsSpan(TimeSpan t) => t < TimeSpan.Zero ? -t : t;

	private static void RenderIntradayGexHeatmap(string ticker, DateTime date, DateTime targetExpiry, Dictionary<string, OptionContractQuote> quotes, decimal strikeRangeFraction, int maxStrikes, int? intervalMin, bool exante, string source, bool liveChain = false, bool withVexNow = false, bool vendorIvs = false, TimeSpan? vexAt = null, TimeSpan? windowStart = null, TimeSpan? windowEnd = null)
	{
		var expiry = targetExpiry.Date;
		var dte = Math.Max(0, (expiry - date.Date).Days);
		var intradaySpots = LoadIntradaySpots(ticker, date);
		if (intradaySpots.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]No intraday spots in data/intraday/{ticker}/{date:yyyy-MM-dd}.csv (file absent or no RTH rows).[/]");
			return;
		}

		// --exante deliberately pins prior-day IVs, so the time-matched minute quotes would defeat its purpose.

		// This session's own chain captures (appended by every prior --intraday call and by wa-scraper's per-minute
		// ivCapture). Loaded up front — before the interval fit — because they answer two independent questions:
		// whether buckets can be rebuilt from as-seen vendor surfaces (running day / --captured, gated below), and
		// whether the volume tape has data (captures carrying the volume column exist for the mapped expiry). The
		// load itself is interval-independent; only the matching tolerance below depends on the bucket size.
		var captureSlices = !exante ? LoadCaptureSlices(ticker, date, expiry, source, quotes) : null;
		if (captureSlices != null && captureSlices.Count == 0) captureSlices = null;
		var hasTape = captureSlices != null && captureSlices.Any(s => s.Quotes.Values.Any(q => q.Volume.HasValue));
		// Historical tape source: the canonical store's ohlcv table (minute trade bars, written by the nightly
		// --ohlcv backfill step) serves days whose captures carry no volume — offline replays and pre-column
		// sessions. The running day prefers the captures (they exist hours before any nightly pull can).
		var storeBars = !exante && !hasTape ? LoadStoreTape(ticker, date.Date, expiry) : null;
		var hasStoreTape = storeBars is { Count: > 0 };

		// Resolve the bucket size: an explicit --interval is honored as-is; the default picks the finest of the
		// standard sizes whose panel set fits the terminal side by side (gamma alone, plus the volume tape when
		// capture volume exists, plus the VEX panel under --vanna), using the same fixed cell geometry as the
		// rendering below. Strike labels track the spot magnitude, so a tape sample prices the column widths
		// before any matrix is built.
		var panels = 1 + (hasTape || hasStoreTape ? 1 : 0) + (withVexNow ? 1 : 0);
		var open = windowStart ?? AnalyzeGexSettings.RthOpen;
		var close = windowEnd ?? AnalyzeGexSettings.RthClose;
		var sampleSpot = intradaySpots.Values.First();
		var estStrikeW = sampleSpot < 100m ? $"${sampleSpot:N2}".Length : StrikeLabel(Math.Round(sampleSpot), wholeGrid: true).Length;
		var estColW = 3 + Math.Max(7, $"{sampleSpot:F2}".Length);
		var fixedW = panels * (1 + estStrikeW + 3) + (panels - 1) * 2;
		int BucketsFor(int min) { var n = 0; for (var t = open; t < close; t += TimeSpan.FromMinutes(min)) n++; return n + 1; }
		// Candidates run down to single minutes: over a --start/--end slice the fine sizes fit, and pinning the
		// exact minute a gravity flips is the reason to narrow the window in the first place.
		var interval = intervalMin ?? new[] { 1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120 }.FirstOrDefault(c => fixedW + panels * BucketsFor(c) * estColW <= AnsiConsole.Profile.Width, 120);
		var minuteQuotes = exante || vendorIvs ? null : IntradayQuoteSlice.Open(ticker, date, expiry, quotes);

		// Column marks: --start stepping by --interval to --end (the end mark is always included).
		var hourMarks = new List<TimeSpan>();
		for (var t = open; t < close; t += TimeSpan.FromMinutes(interval)) hourMarks.Add(t);
		hourMarks.Add(close);
		// Match the nearest intraday minute within half a bucket so adjacent columns never share a spot.
		var tolerance = TimeSpan.FromMinutes(Math.Max(1, interval / 2));
		// Sparse capture series (data/iv chain captures, data/gex ·live anchors) match on a TRUE half-interval: the
		// integer tolerance above leaves dead zones on odd intervals (a 13:02:11 capture missed its 13:00 mark by 11
		// seconds at --interval 5) — harmless against the dense per-minute spot tape, but it drops real captures.
		var captureTolerance = TimeSpan.FromSeconds(Math.Max(60, interval * 30));
		// Bucket REBUILD from captures stays gated to the running day / --captured (an offline past-date replay
		// keeps its store-or-drop discipline); the volume tape below reads the slices on any date.
		var rebuildFromCaptures = (liveChain || vendorIvs) && captureSlices != null;
		if (vendorIvs && captureSlices == null)
		{
			AnsiConsole.MarkupLine($"[red]--captured: no data/iv/{ticker}/{date:yyyy-MM-dd}.csv capture rows from source '{Markup.Escape(source)}' cover the {expiry:yyyy-MM-dd} expiry. The archive is written by running-day `analyze gex` calls and wa-scraper's ivCapture.[/]");
			return;
		}
		var capturedMarks = new HashSet<TimeSpan>();
		var storeMarks = new HashSet<TimeSpan>();

		// Per kept hour: the spot, the per-strike GexCells for the mapped expiry, and that hour's anchor strikes.
		var hours = new List<(TimeSpan Mark, decimal Spot, Dictionary<decimal, GexCell> Cells, decimal? Gravity, decimal? Centroid, decimal? Pull, decimal? CallWall, decimal? PutWall)>();
		var skipped = new List<TimeSpan>();
		// Per-bucket VEX cells (same time-matched quotes as the gamma bucket) — the vanna MIGRATION panel
		// rendered beside the gamma one; --time narrows the panel to the single bucket nearest that ET time.
		var vannaHours = new List<(TimeSpan Mark, decimal Spot, Dictionary<decimal, GexCell> Cells)>();
		foreach (var mark in hourMarks)
		{
			decimal? spot = null;
			var bestDiff = tolerance;
			foreach (var kv in intradaySpots)
			{
				var diff = kv.Key >= mark ? kv.Key - mark : mark - kv.Key;
				if (diff <= bestDiff) { bestDiff = diff; spot = kv.Value; }
			}
			if (!spot.HasValue || spot.Value <= 0m) { skipped.Add(mark); continue; }

			var bucketQuotes = quotes;
			var storeServed = false;
			if (minuteQuotes != null)
			{
				// Strictly at-or-before the mark, within the store's staleness window — never a later print. The
				// whole point of this panel is what was visible AT the bucket, so a forward reach would be a leak.
				var atMark = minuteQuotes.At(date.Date + mark);
				if (atMark.Count > 0) { bucketQuotes = atMark; storeServed = true; storeMarks.Add(mark); }
				// A bucket the store can't serve: on a PAST-date replay it drops (a frozen-IV cell among
				// time-matched ones would mislead), but on the RUNNING day a partial store is the NORM — a
				// mid-session scraper start would otherwise erase every already-watched morning column — so
				// the bucket falls through to the capture slices, then stays provisional on the live fetch.
				else if (!liveChain) { skipped.Add(mark); continue; }
			}
			if (!storeServed && rebuildFromCaptures)
			{
				// Nearest capture within half a bucket (same matching as the ·live rows, so the two agree column for
				// column). No capture near the mark → the current fetch stands in and the column stays provisional.
				Dictionary<string, OptionContractQuote>? nearestCapture = null;
				var bestCapDiff = captureTolerance;
				foreach (var (ts, slice) in captureSlices!)
				{
					var capDiff = ts >= mark ? ts - mark : mark - ts;
					if (capDiff <= bestCapDiff) { bestCapDiff = capDiff; nearestCapture = slice; }
				}
				if (nearestCapture != null) { bucketQuotes = nearestCapture; capturedMarks.Add(mark); }
				else if (vendorIvs) { skipped.Add(mark); continue; }   // a comparison panel must be purely vendor-surfaced — no silent fallback columns
			}

			// asOf is the OBSERVATION instant (the bucket on --date), not the expiry — that is what gives Build the
			// right time-to-expiry once the mapped expiry is allowed to sit days ahead of the session being replayed.
			var m = GexMatrix.Build(bucketQuotes, ticker, spot.Value, date.Date + mark, expiryFilter: expiry, strikeRangeFraction, maxDteDays: dte, maxStrikes);
			var cells = new Dictionary<decimal, GexCell>();
			foreach (var strike in m.Strikes)
				if (m.Cells.TryGetValue((expiry, strike), out var c)) cells[strike] = c;
			if (cells.Count == 0) continue;
			m.GravityByExpiry.TryGetValue(expiry, out var grav);
			m.GrossCentroidByExpiry.TryGetValue(expiry, out var cent);
			m.NetPullByExpiry.TryGetValue(expiry, out var pull);
			var (callWall, putWall) = m.FindWalls(expiry);
			hours.Add((mark, spot.Value, cells, grav, cent, pull, callWall, putWall));
			if (withVexNow)
			{
				var vmB = GexMatrix.Build(bucketQuotes, ticker, spot.Value, date.Date + mark, expiryFilter: expiry, strikeRangeFraction, maxDteDays: dte, maxStrikes, GreekKind.Vanna);
				var vCells = new Dictionary<decimal, GexCell>();
				foreach (var strike in vmB.Strikes)
					if (vmB.Cells.TryGetValue((expiry, strike), out var vc)) vCells[strike] = vc;
				vannaHours.Add((mark, spot.Value, vCells));
			}
		}

		if (hours.Count == 0)
		{
			// Two very different causes, and the ±window one is the rarer: far more often the requested expiry simply
			// is not listed on this date (a Thursday --date on a Friday-only root), so name the expiries that ARE.
			var listed = quotes.Keys
				.Select(ParsingHelpers.ParseOptionSymbol)
				.Where(p => p != null && string.Equals(p!.Root, ticker, StringComparison.OrdinalIgnoreCase) && p.ExpiryDate.Date >= date.Date)
				.Select(p => p!.ExpiryDate.Date).Distinct().OrderBy(d => d).ToList();
			if (!listed.Contains(expiry))
				AnsiConsole.MarkupLine(listed.Count > 0
					? $"[yellow]{ticker} lists no {expiry:yyyy-MM-dd} expiry on {date:yyyy-MM-dd} — this root has no daily expirations. Nearest listed: {string.Join(", ", listed.Take(4).Select(d => d.ToString("yyyy-MM-dd")))}. Re-run with --expiry {listed[0]:yyyy-MM-dd}.[/]"
					: $"[yellow]{ticker} has no expiry at or after {date:yyyy-MM-dd} in the {date:yyyy-MM-dd} chain.[/]");
			else
				AnsiConsole.MarkupLine($"[yellow]No {expiry:yyyy-MM-dd} GEX cells at any hour for {ticker} on {date:yyyy-MM-dd} — the expiry is listed but no strike carries OI within ±{strikeRangeFraction * 100m:F0}% of spot. Widen with --strike-range.[/]");
			return;
		}

		var maxAbsNet = Math.Max(1m, hours.SelectMany(h => h.Cells.Values).Select(c => Math.Abs(c.Net)).DefaultIfEmpty(0m).Max());
		var allStrikes = hours.SelectMany(h => h.Cells.Keys).Distinct().OrderByDescending(s => s).ToList();
		var wholeGrid = IsWholeGrid(allStrikes);

		// Volume tape: the session's TAPE laid on the same ladder as the BOOK. Each column is the Δ of the
		// captures' cumulative day volume between this mark's capture and the previous one that carried volume
		// (the first such column is volume since the open — the baseline is zero, not an earlier capture). Cells
		// reuse GexCell with call/put Δvolume, so the color language matches the gamma map: green = CALL-dominated
		// flow, red = PUT-dominated — which side TRADED, not which way (open/close and buy/sell are unknowable
		// without signed prints). A mark whose nearest volume-bearing capture is the same one the previous column
		// consumed contributes no new information and is skipped rather than rendered as a fake zero column.
		var tapeHours = new List<(TimeSpan Mark, decimal Spot, Dictionary<decimal, GexCell> Cells, long Total)>();
		if (hasStoreTape)
		{
			// Store bars are per-minute INTERVAL volumes with start-of-bar labels, so a bucket is the sum of the
			// bar minutes in [prev mark, mark) — the same trailing window the capture diff produces; the first
			// mark has no trailing window and stays empty by construction.
			for (var i = 1; i < hours.Count; i++)
			{
				var (prev, mark) = (hours[i - 1].Mark, hours[i].Mark);
				var byStrike = new Dictionary<decimal, (decimal C, decimal P)>();
				long total = 0;
				foreach (var (ts, strikes) in storeBars!)
				{
					if (ts < prev || ts >= mark) continue;
					foreach (var (k, v) in strikes)
					{
						byStrike.TryGetValue(k, out var e);
						byStrike[k] = (e.C + v.C, e.P + v.P);
						total += (long)(v.C + v.P);
					}
				}
				if (byStrike.Count == 0) continue;
				tapeHours.Add((mark, hours[i].Spot, byStrike.ToDictionary(kv => kv.Key, kv => new GexCell(kv.Value.C, kv.Value.P)), total));
			}
		}
		else if (hasTape)
		{
			var lastCum = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			TimeSpan? lastCapTs = null;
			foreach (var h in hours)
			{
				Dictionary<string, OptionContractQuote>? cap = null;
				TimeSpan? capTs = null;
				var best = captureTolerance;
				foreach (var (ts, slice) in captureSlices!)
				{
					var diff = ts >= h.Mark ? ts - h.Mark : h.Mark - ts;
					if (diff <= best && slice.Values.Any(q => q.Volume.HasValue)) { best = diff; cap = slice; capTs = ts; }
				}
				if (cap == null || capTs == lastCapTs) continue;
				lastCapTs = capTs;
				var byStrike = new Dictionary<decimal, (decimal C, decimal P)>();
				long total = 0;
				foreach (var (sym, q) in cap)
				{
					if (q.Volume is not { } cum) continue;
					var p = ParsingHelpers.ParseOptionSymbol(sym);
					if (p == null || string.IsNullOrEmpty(p.CallPut)) continue;
					var d = Math.Max(0, cum - lastCum.GetValueOrDefault(sym));   // cumulative never falls; a vendor reset clamps to 0
					lastCum[sym] = cum;
					if (d == 0) continue;
					total += d;
					byStrike.TryGetValue(p.Strike, out var e);
					byStrike[p.Strike] = p.CallPut == "C" ? (e.C + d, e.P) : (e.C, e.P + d);
				}
				tapeHours.Add((h.Mark, h.Spot, byStrike.ToDictionary(kv => kv.Key, kv => new GexCell(kv.Value.C, kv.Value.P)), total));
			}
		}

		AnsiConsole.MarkupLine(dte == 0
			? $"[bold]{ticker}[/] 0DTE {date:yyyy-MM-dd} — intraday GEX gravity migration"
			: $"[bold]{ticker}[/] {expiry:yyyy-MM-dd} expiry ({dte}DTE) as it traded on {date:yyyy-MM-dd} — intraday GEX gravity migration");
		AnsiConsole.MarkupLine(minuteQuotes != null && !liveChain
			? $"[dim]IVs: back-solved per bucket from REAL minute NBBO at or before each mark (data/quotes.db, {ticker} {expiry:yyyy-MM-dd} expiry on {date:yyyy-MM-dd}); OI fixed from the day's snapshot.[/]"
			: exante ? "[dim]IVs: frozen from the prior-day --exante values.[/]"
			: vendorIvs ? $"[dim]IVs/OI: VENDOR-reported per bucket from the data/iv captures ({Markup.Escape(source)}); buckets without a capture within half an interval are dropped. Run without --captured for the NBBO-solved panel to compare against.[/]"
			: liveChain ? $"[dim]IVs/OI running day: {storeMarks.Count} bucket(s) time-matched from minute NBBO (data/quotes.db, wa-scraper), {capturedMarks.Count} from this session's data/iv chain captures, {hours.Count - storeMarks.Count - capturedMarks.Count} provisional on the current live fetch (those reprice each call until coverage lands near them).[/]"
			: $"[yellow]IVs: back-solved from the day's OI-snapshot mids — that snapshot is stamped at the CLOSE, so every column is priced off the session's OUTCOME and the early ones are not what was visible then. data/quotes.db has no {ticker} {expiry:yyyy-MM-dd} rows for {date:yyyy-MM-dd}; backfill it, or use --exante for prior-day IVs.[/]");

		var table = BuildColumnHeatmapTable("[bold]GEX (gamma)[/]", hours.Select(h => new HeatColumn($"{h.Mark:hh\\:mm}", $"{h.Spot:F2}", h.Cells, h.Gravity)).ToList(), allStrikes, wholeGrid, maxAbsNet, hours[^1].Spot);

		// Computed-gravity footer row. The gravity cell is bold+underlined in the matrix, but styling is dropped
		// whenever output is not an interactive terminal (piped, captured, pasted into notes) — which is exactly
		// when someone is reading the migration carefully. Spell the strike out.
		var gravityCells = new List<string> { "[bold]Gravity[/]" };
		foreach (var h in hours)
			gravityCells.Add(h.Gravity.HasValue ? $"[bold]${h.Gravity.Value:N0}[/]" : "[dim]·[/]");
		table.AddRow(gravityCells.ToArray());

		// Whole-ladder rows. Gravity is one strike chosen by argmax; these two read the WHOLE book, which is the
		// question a "sea of red below, sea of green above" eyeball is really asking. Centroid is where the gross
		// gamma mass sits; Pull is how far the NET exposure leans from spot, signed, in expected-move units.
		var centroidCells = new List<string> { "[bold]Centroid[/]" };
		var pullCells = new List<string> { "[bold]Pull[/]" };
		foreach (var h in hours)
		{
			centroidCells.Add(h.Centroid.HasValue ? $"[bold]${h.Centroid.Value:N0}[/]" : "[dim]·[/]");
			pullCells.Add(h.Pull.HasValue ? $"[bold {(h.Pull.Value >= 0m ? "green" : "red")}]{(h.Pull.Value >= 0m ? "+" : "−")}{Math.Abs(h.Pull.Value):F2}σ[/]" : "[dim]·[/]");
		}
		table.AddRow(centroidCells.ToArray());
		table.AddRow(pullCells.ToArray());

		// Per-side wall rows: the strike carrying the largest CALL GEX and the largest PUT GEX that bucket. The net
		// color map cannot substitute for these — a strike with a huge call AND put book nets toward zero and renders
		// dim, yet can be either wall (or both) — so they get their own rows, migrating per bucket like the gravity.
		var wallCCells = new List<string> { "[bold green]Wall·C[/]" };
		var wallPCells = new List<string> { "[bold red]Wall·P[/]" };
		foreach (var h in hours)
		{
			wallCCells.Add(h.CallWall.HasValue ? $"[green]${h.CallWall.Value:N0}[/]" : "[dim]·[/]");
			wallPCells.Add(h.PutWall.HasValue ? $"[red]${h.PutWall.Value:N0}[/]" : "[dim]·[/]");
		}
		table.AddRow(wallCCells.ToArray());
		table.AddRow(wallPCells.ToArray());

		// "·live" footer rows: per bucket, the gravity and walls the live `analyze gex` runs actually displayed
		// (data/gex log) nearest the mark. The live values come from vendor-reported IVs that are never persisted, so
		// these rows are the only ground truth a replay can be compared against.
		var liveLog = LoadLiveGexLog(ticker, date, expiry, source);
		if (liveLog.Count > 0)
		{
			var liveGravityCells = new List<string> { "[bold cyan]Gravity·live[/]" };
			var liveWallCCells = new List<string> { "[bold cyan]Wall·C·live[/]" };
			var liveWallPCells = new List<string> { "[bold cyan]Wall·P·live[/]" };
			foreach (var h in hours)
			{
				LiveGexAnchors? a = null;
				var bestDiff = captureTolerance;
				foreach (var kv in liveLog)
				{
					var diff = kv.Key >= h.Mark ? kv.Key - h.Mark : h.Mark - kv.Key;
					if (diff <= bestDiff) { bestDiff = diff; a = kv.Value; }
				}
				liveGravityCells.Add(a?.Gravity != null ? $"[bold cyan]${a.Gravity.Value:N2}[/]" : "[dim]·[/]");
				liveWallCCells.Add(a?.CallWall != null ? $"[cyan]${a.CallWall.Value:N2}[/]" : "[dim]·[/]");
				liveWallPCells.Add(a?.PutWall != null ? $"[cyan]${a.PutWall.Value:N2}[/]" : "[dim]·[/]");
			}
			table.AddRow(liveGravityCells.ToArray());
			table.AddRow(liveWallCCells.ToArray());
			table.AddRow(liveWallPCells.ToArray());
		}

		// Companion panels beside the gamma map, all sharing the exact strike rows (headers are two lines in every
		// table, none has a title row, and only the gamma table carries footer rows BELOW the ladder — so the
		// ladders align 1:1): the volume tape (default whenever capture volume exists) and the VEX migration panel
		// (--vanna; --time narrows it to the single bucket nearest that ET time). Columns wraps panels below when
		// the set exceeds the terminal; the hint suggests the interval that would fit side by side.
		var shownTables = new List<Table> { table };
		var shownColumnCounts = new List<int> { hours.Count };
		if (tapeHours.Count > 0)
		{
			var tMax = Math.Max(1m, tapeHours.SelectMany(t => t.Cells.Values).Select(c => Math.Abs(c.Net)).DefaultIfEmpty(0m).Max());
			// Same spot anchor as the gamma table above (hours[^1].Spot, not this panel's own tapeHours[^1].Spot) —
			// the panels are meant to line up row-for-row, which only holds if every BuildColumnHeatmapTable call
			// here buckets the shared `allStrikes` ladder around the same spot.
			var tape = BuildColumnHeatmapTable("[bold]Volume (Δ tape)[/]", tapeHours.Select(t => new HeatColumn($"{t.Mark:hh\\:mm}", $"{t.Spot:F2}", t.Cells, Gravity: null)).ToList(), allStrikes, wholeGrid, tMax, hours[^1].Spot);
			var sumCells = new List<string> { "[bold]Σvol[/]" };
			foreach (var t in tapeHours) sumCells.Add(t.Total > 0 ? $"[bold]{FormatCompact(t.Total).TrimStart('+')}[/]" : "[dim]0[/]");
			tape.AddRow(sumCells.ToArray());
			shownTables.Add(tape);
			shownColumnCounts.Add(tapeHours.Count);
		}
		if (withVexNow && vannaHours.Count > 0)
		{
			var shownVanna = vexAt == null ? vannaHours : new List<(TimeSpan Mark, decimal Spot, Dictionary<decimal, GexCell> Cells)> { vannaHours.OrderBy(v => AbsSpan(v.Mark - vexAt.Value)).First() };
			var vMax = Math.Max(1m, shownVanna.SelectMany(h => h.Cells.Values).Select(c => Math.Abs(c.Net)).DefaultIfEmpty(0m).Max());
			shownTables.Add(BuildColumnHeatmapTable("[bold]VEX (vanna)[/]", shownVanna.Select(h => new HeatColumn($"{h.Mark:hh\\:mm}", $"{h.Spot:F2}", h.Cells, Gravity: null)).ToList(), allStrikes, wholeGrid, vMax, hours[^1].Spot));
			shownColumnCounts.Add(shownVanna.Count);
		}
		if (shownTables.Count > 1)
		{
			AnsiConsole.Write(new Columns(shownTables) { Expand = false });

			// Same fixed cell geometry as the side-by-side expiry heatmaps: content max(7, spot label) + 2 padding
			// + 1 border per bucket column; strike column + 2 padding + 1 border; +1 closing border per panel.
			var strikeW = allStrikes.Count > 0 ? allStrikes.Max(k => StrikeLabel(k, wholeGrid).Length) : 7;
			var colW = 3 + Math.Max(7, hours.Max(h => $"{h.Spot:F2}".Length));
			var setWidth = shownTables.Count * (1 + strikeW + 3 + 1) + shownColumnCounts.Sum() * colW;
			if (setWidth > AnsiConsole.Profile.Width && vexAt == null)
			{
				var fitBuckets = Math.Max(2, (AnsiConsole.Profile.Width - shownTables.Count * (1 + strikeW + 3 + 1)) / (shownTables.Count * colW));
				var fitInterval = (int)Math.Ceiling(390.0 / (fitBuckets - 1) / 30) * 30;
				AnsiConsole.MarkupLine($"[dim]Panels are stacked — together they need {setWidth} columns but the terminal has {AnsiConsole.Profile.Width}. Raise --interval to ~{fitInterval} (or drop --interval for the auto fit{(withVexNow ? ", or use --time HH:MM for a single VEX bucket" : "")}) to fit them side by side.[/]");
			}
		}
		else
			AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Cell = net GEX recomputed at each bucket's spot against the day's fixed OI. [green]Green[/] = call-dominated, [red]red[/] = put-dominated, brightness ∝ |net|. Bold + underlined cell = that bucket's gravity strike (max gross gamma), also spelled out in the [bold]Gravity[/] row. [bold]Centroid[/] = gross-gamma-weighted mean strike (gravity without the argmax flicker); [bold]Pull[/] = net-GEX-weighted lean from spot in ATM-expected-move units (raw points ÷ spot·σ_atm·√T for the remaining life — a static book holds roughly flat as the clock decays instead of bleeding to zero), [green]+[/] = the ladder's net exposure sits ABOVE spot, [red]−[/] = below. Both aggregate every in-range strike, not just the displayed rows. [green]Wall·C[/]/[red]Wall·P[/] = the strike carrying the largest call/put GEX that bucket (per-side argmax — a big two-sided strike can be a wall yet net toward dim in the map)." + (liveLog.Count > 0 ? " [cyan]·live[/] rows = the gravity/walls logged in real time by live `analyze gex` runs (data/gex) nearest each bucket." : "") + "[/]");
		if (tapeHours.Count > 0)
			AnsiConsole.MarkupLine($"[dim]Volume tape = contracts TRADED per bucket ({(hasStoreTape ? "summed from the store's ohlcv minute bars (data/quotes.db, ThetaData); the first mark has no trailing window and stays empty" : "Δ of the captures' cumulative day volume; the first column is volume since the open")}), cell = net call−put Δvolume on the same color language as the gamma map — [green]green[/] = call-side flow, [red]red[/] = put-side. Which side traded, NOT which way: open/close and buy/sell need signed prints. [bold]Σvol[/] = total contracts that bucket across the window. The book is static intraday (OI settles overnight); this tape is the only live axis.[/]");
		else if (liveChain && !hasTape)
			AnsiConsole.MarkupLine("[dim]Volume tape unavailable: today's data/iv captures carry no volume column yet (added 2026-08-18 — restart wa-scraper to start recording; the tape appears from that minute on).[/]");
		if (withVexNow && vannaHours.Count > 0)
			AnsiConsole.MarkupLine($"[dim]VEX panel = the mapped expiry's net vanna ($ dealer delta per vol point) recomputed at each bucket's spot, same quotes as the gamma columns{(vexAt != null ? $" (narrowed to the bucket nearest --time {vexAt:hh\\:mm})" : "")}. No gravity marker — vanna maps a hedging flow under an IV move, not a level. Full multi-expiry VEX: `analyze gex {Markup.Escape(ticker)}` without --intraday.[/]");
		if (skipped.Count > 0 && vendorIvs)
			AnsiConsole.MarkupLine($"[dim]{skipped.Count} bucket(s) without a vendor capture within {captureTolerance.TotalMinutes:F1} min dropped: {string.Join(", ", skipped.Select(s => s.ToString(@"hh\:mm")))} — denser coverage comes from wa-scraper's per-minute ivCapture or more frequent analyze runs.[/]");
		else if (skipped.Count > 0 && liveChain)
			AnsiConsole.MarkupLine($"[dim]{skipped.Count} bucket(s) still ahead ({string.Join(", ", skipped.Select(s => s.ToString(@"hh\:mm")))}) — the session tape ends at {intradaySpots.Keys.Last():hh\\:mm} ET; re-run to extend the columns as the day unfolds.[/]");
		else if (skipped.Count > 0)
			AnsiConsole.MarkupLine($"[yellow]Dropped {skipped.Count} bucket(s) with no spot/quote within {tolerance.TotalMinutes:F0} min: {string.Join(", ", skipped.Select(s => s.ToString(@"hh\:mm")))} — the spot tape ends at {intradaySpots.Keys.Last():hh\\:mm} ET{(date.Date == DateTime.Today ? $". Refresh today's tape with `wa ai history {ticker} --partial`" : "")}.[/]");
	}

	/// <summary>Time-matched real NBBO for one expiry's contracts, out of the canonical ThetaData minute store
	/// <c>data/quotes.db</c> — the same source the backtest prices off, read through the same
	/// <see cref="QuoteStoreCache"/> so the price scaling, the row-label convention and the staleness policy have
	/// exactly one implementation. (The old per-ticker <c>data/quotes/*.csv</c> files this used to read were retired
	/// by the quotes-only pivot; reading them meant silently falling back to close-stamped snapshot IVs on every
	/// historical replay.)
	///
	/// <para>Each bucket gets the latest two-sided book at or BEFORE its mark, never a later one: the panel's whole
	/// claim is what a strike looked like at that moment, so reaching forward would leak the outcome that the
	/// snapshot-IV fallback already leaks. Contracts keep the snapshot's OI (constant intraday, published pre-open)
	/// but carry that minute's bid/ask with the IV nulled, so <see cref="GexMatrix.Build"/> back-solves each bucket's
	/// IVs from time-matched mids.</para></summary>
	private sealed class IntradayQuoteSlice
	{
		// Matches the store's own default: minute NBBO is dense for near-money contracts, so a short window keeps
		// "this is what it looked like then" honest rather than papering a gap with a five-bucket-old print.
		private const int MaxStaleMinutes = 5;

		private readonly QuoteStoreCache _cache;
		private readonly List<OptionContractQuote> _contracts;

		private IntradayQuoteSlice(QuoteStoreCache cache, List<OptionContractQuote> contracts)
		{
			_cache = cache;
			_contracts = contracts;
		}

		/// <summary>Null when the store cannot serve this (ticker, expiry, date) — no DB, no snapshot contracts for
		/// the expiry, or no captured rows for the session. The caller then says so instead of quietly pricing off
		/// the close.</summary>
		public static IntradayQuoteSlice? Open(string ticker, DateTime date, DateTime expiry, IReadOnlyDictionary<string, OptionContractQuote> snapshot)
		{
			var dbPath = Program.ResolvePath("data/quotes.db");
			if (!File.Exists(dbPath)) return null;

			var contracts = new List<OptionContractQuote>();
			foreach (var (sym, q) in snapshot)
			{
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, expiry) || p.ExpiryDate.Date != expiry.Date || string.IsNullOrEmpty(p.CallPut)) continue;
				if (!q.OpenInterest.HasValue || q.OpenInterest.Value <= 0) continue;   // no OI = no exposure to plot
				contracts.Add(q);
			}
			if (contracts.Count == 0) return null;

			// since/until pin the load to this one session, so an expiry slice carrying weeks of longer-dated rows
			// parses only the day being replayed.
			var cache = new QuoteStoreCache(dbPath, MaxStaleMinutes, since: date.Date, until: date.Date, sameDayExpiryOnly: expiry.Date == date.Date);
			return cache.HasAnyQuoteInWindow(ticker, date.Date, date.Date) ? new IntradayQuoteSlice(cache, contracts) : null;
		}

		/// <summary>The mapped expiry's contracts as of <paramref name="instantEt"/>, carrying real two-sided books
		/// only. Empty when the store has nothing within the staleness window — the caller drops that bucket rather
		/// than mixing a frozen-IV column in among time-matched ones.</summary>
		public Dictionary<string, OptionContractQuote> At(DateTime instantEt)
		{
			var set = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			foreach (var c in _contracts)
			{
				if (_cache.NbboAt(c.ContractSymbol, instantEt) is not { } nbbo || nbbo.Bid <= 0m || nbbo.Ask <= 0m) continue;
				set[c.ContractSymbol] = c with { Bid = nbbo.Bid, Ask = nbbo.Ask, ImpliedVolatility = null };
			}
			return set;
		}
	}

	/// <summary>Per-minute traded volume for one (ticker, date, expiry) from the canonical store's `ohlcv` table
	/// (ThetaData minute trade bars, START-of-bar labels, traded minutes only — written by the --ohlcv backfill
	/// step). The volume tape's HISTORICAL source; running days read the data/iv captures instead, which exist
	/// hours before any nightly pull can. Returns minute → strike → (call volume, put volume); empty when the
	/// store, the table, or the day's coverage is absent — the tape panel then simply doesn't render.</summary>
	private static SortedDictionary<TimeSpan, Dictionary<decimal, (decimal C, decimal P)>> LoadStoreTape(string ticker, DateTime date, DateTime expiry)
	{
		var result = new SortedDictionary<TimeSpan, Dictionary<decimal, (decimal C, decimal P)>>();
		var dbPath = Program.ResolvePath("data/quotes.db");
		if (!File.Exists(dbPath)) return result;
		try
		{
			using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
			conn.Open();
			using (var probe = conn.CreateCommand())
			{
				probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ohlcv'";
				if (probe.ExecuteScalar() == null) return result;   // store predates the ohlcv feature
			}
			// On a standard-monthly expiry, real OI/volume splits across the legacy SPX and SPXW roots (see
			// ParsingHelpers.AggregationRoots) — query every root that book could be under, not just the one asked for.
			var roots = ParsingHelpers.AggregationRoots(ticker, expiry);
			using var cmd = conn.CreateCommand();
			cmd.CommandText = $"SELECT time_sec, strike_milli, right, volume FROM ohlcv WHERE root IN ({string.Join(",", roots.Select((_, i) => $"$root{i}"))}) AND expiry=$exp AND date=$date";
			for (var i = 0; i < roots.Count; i++) cmd.Parameters.AddWithValue($"$root{i}", roots[i]);
			cmd.Parameters.AddWithValue("$exp", expiry.Year * 10000 + expiry.Month * 100 + expiry.Day);
			cmd.Parameters.AddWithValue("$date", date.Year * 10000 + date.Month * 100 + date.Day);
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
			{
				var ts = TimeSpan.FromSeconds(reader.GetInt64(0));
				var strike = reader.GetInt64(1) / 1000m;
				var isCall = reader.GetString(2) == "C";
				var vol = (decimal)reader.GetInt64(3);
				if (!result.TryGetValue(ts, out var byStrike)) result[ts] = byStrike = new Dictionary<decimal, (decimal C, decimal P)>();
				byStrike.TryGetValue(strike, out var e);
				byStrike[strike] = isCall ? (e.C + vol, e.P) : (e.C, e.P + vol);
			}
		}
		catch (Exception ex) { Log.Debug($"ohlcv tape read failed: {ex.Message}"); }
		return result;
	}

	/// <summary>Today's captured chain surfaces for one expiry, read back from the data/iv dump every running-day
	/// --intraday call appends: ET capture time → the mapped expiry's contracts carrying that capture's bid/ask,
	/// vendor IV and OI, rejoined to the live chain's contract symbols by (strike, right). Each capture is exactly
	/// the per-strike input set behind a displayed run, so a bucket rebuilt from one reproduces what that run showed
	/// instead of repricing history with the current fetch. Source-filtered like the data/gex reader; malformed lines
	/// are skipped. Rows whose contract is absent from the current fetch drop out — with the fetch window spanning
	/// hundreds of points around spot, intraday drift does not reach that edge in practice.</summary>
	private static List<(TimeSpan Ts, Dictionary<string, OptionContractQuote> Quotes)> LoadCaptureSlices(string ticker, DateTime date, DateTime expiry, string source, Dictionary<string, OptionContractQuote> chain)
	{
		var result = new List<(TimeSpan, Dictionary<string, OptionContractQuote>)>();
		var path = Program.ResolvePath($"data/iv/{ticker}/{date:yyyy-MM-dd}.csv");
		if (!File.Exists(path)) return result;
		var bySpec = new Dictionary<(decimal Strike, string Right), OptionContractQuote>();
		foreach (var (sym, q) in chain)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p != null && ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, expiry) && p.ExpiryDate.Date == expiry.Date && !string.IsNullOrEmpty(p.CallPut)) bySpec[(p.Strike, p.CallPut)] = q;
		}
		if (bySpec.Count == 0) return result;
		var expiryStr = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var byTs = new SortedDictionary<TimeSpan, Dictionary<string, OptionContractQuote>>();
		foreach (var line in File.ReadLines(path).Skip(1))
		{
			var f = line.Split(',');
			if (f.Length < 11 || !string.Equals(f[2], source, StringComparison.OrdinalIgnoreCase) || f[3] != expiryStr) continue;
			if (!TimeSpan.TryParseExact(f[1], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var ts)) continue;
			if (!decimal.TryParse(f[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var strike) || !bySpec.TryGetValue((strike, f[5]), out var baseQuote)) continue;
			decimal? Num(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
			if (!byTs.TryGetValue(ts, out var slice)) byTs[ts] = slice = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			// LastPrice is nulled: the dump doesn't persist it, and letting the CURRENT fetch's last price serve as a
			// mid fallback inside a time-matched column would quietly re-import the very leak this slice removes.
			// Volume likewise: rows written before the column existed (pre-2026-08-18 files, or a writer not yet
			// restarted) have 11 fields — their volume is UNKNOWN, and inheriting the current fetch's cumulative
			// count would hand the tape a same-instant total wearing an old timestamp.
			slice[baseQuote.ContractSymbol] = baseQuote with { Bid = Num(f[6]), Ask = Num(f[7]), ImpliedVolatility = Num(f[8]), OpenInterest = long.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var oi) ? oi : null, LastPrice = null, Volume = f.Length >= 12 && long.TryParse(f[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol) ? vol : null };
		}
		foreach (var kv in byTs) result.Add((kv.Key, kv.Value));
		return result;
	}

	/// <summary>The gamma anchors one live `analyze gex` run displayed for one expiry, as read back from the data/gex
	/// log. Fields are independently nullable — a record can carry a gravity with no walls (or vice versa) when the
	/// in-window cell set was one-sided.</summary>
	private sealed record LiveGexAnchors(decimal? Gravity, decimal? CallWall, decimal? PutWall);

	/// <summary>Reads the live `analyze gex` log at <c>data/gex/<TICKER>/<date>.jsonl</c> and returns
	/// ET time-of-day → the gravity and wall strikes that run displayed for the <paramref name="expiry"/> series. The
	/// log file is per SESSION and each record carries every expiry it rendered, so an off-expiry map still finds its
	/// own row. Only records from <paramref name="source"/> are kept (records without a source field predate the
	/// --source option and count as webull). Corrupt lines are skipped — a torn concurrent append must not
	/// take down the heatmap.</summary>
	private static SortedDictionary<TimeSpan, LiveGexAnchors> LoadLiveGexLog(string ticker, DateTime date, DateTime expiry, string source)
	{
		var result = new SortedDictionary<TimeSpan, LiveGexAnchors>();
		var path = Program.ResolvePath($"data/gex/{ticker}/{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(path)) return result;
		var expiryStr = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;
				var recSource = root.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String ? srcEl.GetString() : "webull";
				if (!string.Equals(recSource, source, StringComparison.OrdinalIgnoreCase)) continue;
				if (!root.TryGetProperty("tsEt", out var tsEl) || !DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) continue;
				if (!root.TryGetProperty("expiries", out var exps) || exps.ValueKind != JsonValueKind.Array) continue;
				foreach (var e in exps.EnumerateArray())
				{
					if (!e.TryGetProperty("expiry", out var ex) || ex.GetString() != expiryStr) continue;
					decimal? Num(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
					var anchors = new LiveGexAnchors(Num("gravity"), Num("callWall"), Num("putWall"));
					if (anchors.Gravity.HasValue || anchors.CallWall.HasValue || anchors.PutWall.HasValue) result[ts.TimeOfDay] = anchors;
				}
			}
			catch (JsonException) { }
		}
		return result;
	}

	private sealed record GexLogExpiry(
		[property: JsonPropertyName("expiry")] string Expiry,
		[property: JsonPropertyName("gravity")] decimal? Gravity,
		[property: JsonPropertyName("gross")] decimal? Gross,
		[property: JsonPropertyName("callWall")] decimal? CallWall,
		[property: JsonPropertyName("putWall")] decimal? PutWall,
		[property: JsonPropertyName("gammaFlip")] decimal? GammaFlip,
		[property: JsonPropertyName("maxPain")] decimal? MaxPain);

	private sealed record GexLogRecord(
		[property: JsonPropertyName("tsEt")] string TsEt,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("spot")] decimal Spot,
		[property: JsonPropertyName("strikeRangePct")] int StrikeRangePct,
		[property: JsonPropertyName("maxStrikes")] int MaxStrikes,
		[property: JsonPropertyName("dte")] int Dte,
		[property: JsonPropertyName("expiries")] List<GexLogExpiry> Expiries);

	/// <summary>Appends one record per LIVE run to <c>data/gex/<TICKER>/<ET date>.jsonl</c>: timestamp, source,
	/// spot, the window parameters the values depend on, and the per-expiry signals exactly as displayed (gravity + its
	/// gross, walls, gamma flip, max pain). The live numbers are built on the chain vendor's reported IVs, which exist
	/// nowhere on disk after the fact — without this log a historical replay has nothing to be validated against. The
	/// source field keeps interleaved webull/schwab runs separable in the same day file.</summary>
	private static void AppendGexLog(string ticker, decimal spot, GexMatrix matrix, AnalyzeGexSettings settings)
	{
		var nowEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		var rows = new List<GexLogExpiry>();
		foreach (var exp in matrix.Expiries)
		{
			matrix.GravityByExpiry.TryGetValue(exp, out var gravity);
			decimal? gross = gravity.HasValue && matrix.Cells.TryGetValue((exp, gravity.Value), out var gc) ? Math.Round(gc.Gross) : null;
			var (callWall, putWall) = matrix.FindWalls(exp);
			rows.Add(new GexLogExpiry(exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), gravity, gross, callWall, putWall, matrix.FindGammaFlip(spot, exp), matrix.FindMaxPain(exp, spot)));
		}
		var record = new GexLogRecord(nowEt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), settings.VendorName, spot, settings.StrikeRangePct, settings.MaxStrikes, settings.Dte, rows);
		var path = Program.ResolvePath($"data/gex/{ticker}/{nowEt:yyyy-MM-dd}.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
		AnsiConsole.MarkupLine($"[dim]Logged to data/gex/{ticker}/{nowEt:yyyy-MM-dd}.jsonl (the --intraday heatmap reads this back as its \"Gravity\" row).[/]");
	}

	/// <summary>--dump (and the running-day --intraday capture): appends one row per in-window contract of this LIVE
	/// fetch to the <see cref="IvDumpStore"/> archive — the per-strike vendor inputs (bid/ask, vendor IV, OI) behind
	/// the displayed gex values, which are otherwise discarded after the run. Window = the same expiry/strike filters
	/// the heatmap uses; the row format and file layout live in the shared store.</summary>
	private static void AppendIvDump(string ticker, decimal spot, Dictionary<string, OptionContractQuote> quotes, AnalyzeGexSettings settings, DateTime asOf, DateTime? expiryFilter)
	{
		var nowEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		var band = settings.StrikeRangePct / 100m;
		var inWindow = quotes.Values.Where(q =>
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
			if (parsed == null || !ParsingHelpers.RootsMatchForAggregation(parsed.Root, ticker, parsed.ExpiryDate)) return false;
			var exp = parsed.ExpiryDate.Date;
			if (expiryFilter.HasValue ? exp != expiryFilter.Value.Date : exp < asOf.Date || exp > asOf.Date.AddDays(settings.Dte)) return false;
			return Math.Abs(parsed.Strike - spot) / spot <= band;
		});
		var rows = IvDumpStore.Append(ticker, settings.VendorName, nowEt, spot, inWindow);
		AnsiConsole.MarkupLine($"[dim]Dumped {rows} {settings.VendorName} contract row(s) to data/iv/{ticker}/{nowEt:yyyy-MM-dd}.csv.[/]");
	}

	private static string FormatCompact(decimal v)
	{
		var abs = Math.Abs(v);
		var sign = v < 0 ? "-" : "+";
		if (abs >= 1_000_000_000m) return $"{sign}{abs / 1_000_000_000m:F1}B";
		if (abs >= 1_000_000m) return $"{sign}{abs / 1_000_000m:F1}M";
		if (abs >= 1_000m) return $"{sign}{abs / 1_000m:F0}k";
		return $"{sign}{abs:F0}";
	}

	private static string FormatCompactDollars(decimal v)
	{
		var abs = Math.Abs(v);
		if (abs >= 1_000_000_000m) return $"${abs / 1_000_000_000m:F2}B";
		if (abs >= 1_000_000m) return $"${abs / 1_000_000m:F2}M";
		if (abs >= 1_000m) return $"${abs / 1_000m:F0}k";
		return $"${abs:F0}";
	}
}

/// <summary>Which greek the exposure matrix is built from. Gamma is the classic GEX; Vanna is the VEX the
/// dealer-flow work needs — see <see cref="GexMatrix.Build"/> for each one's dollar scaling.</summary>
internal enum GreekKind
{
	Gamma,
	Vanna,
}

/// <summary>One cell of the exposure matrix at a given (expiry, strike). CallGex/PutGex are the per-side dollar
/// exposures (see <see cref="GexMatrix.Build"/>); non-negative for gamma, signed for vanna. Net = call − put
/// (the dealer long-call / short-put convention), Gross = the magnitude concentration at the strike.</summary>
internal sealed record GexCell(decimal CallGex, decimal PutGex)
{
	// Abs-sum, not a plain sum: identical to call+put for gamma (both sides already non-negative), and the
	// right magnitude aggregate for vanna, whose per-side values are signed and would otherwise cancel.
	public decimal Gross => Math.Abs(CallGex) + Math.Abs(PutGex);
	public decimal Net => CallGex - PutGex;
}

/// <summary>Per-contract ingredients retained on the matrix so we can re-evaluate net dollar gamma at
/// a hypothetical spot S* (used by <see cref="GexMatrix.FindGammaFlip(decimal)"/>) and total ITM payout
/// at a strike (used by <see cref="GexMatrix.FindMaxPain"/>). One entry per (expiry, strike, side) that
/// survived the strike-range filter for a kept expiry — NOT capped by --max-strikes, since analytics
/// shouldn't be skewed by a display-only cap.</summary>
internal sealed record GexContributor(DateTime Expiry, decimal Strike, double TimeYears, decimal Iv, long Oi, bool IsCall);

internal sealed class GexMatrix
{
	public List<DateTime> Expiries { get; }
	public List<decimal> Strikes { get; }
	public Dictionary<(DateTime Expiry, decimal Strike), GexCell> Cells { get; }
	/// <summary>Same per-strike gamma cells as <see cref="Cells"/>, but bounded only by --strike-range ×
	/// kept-expiries — NOT the --max-strikes display cap. <see cref="FindWalls"/> reads from here for the
	/// same reason Gravity/Centroid/NetPull/Contributors do (see the comment above their construction
	/// below): a wall is an argmax, so a strike cap can silently swap in a lesser local max the moment the
	/// true wall falls outside the row window — the exact class of bug already fixed for Gravity.</summary>
	public Dictionary<(DateTime Expiry, decimal Strike), GexCell> FullCells { get; }
	/// <summary>Per-(expiry, strike) call/put open interest — same population and window as <see cref="FullCells"/>
	/// (exact-root, trusted-IV gate), read by <see cref="FindOiWalls"/> for the OI cluster columns.</summary>
	public Dictionary<(DateTime Expiry, decimal Strike), (long CallOi, long PutOi)> FullOi { get; }
	/// <summary>Per-(expiry, strike) call/put day volume — same population/window as <see cref="FullOi"/>,
	/// read by <see cref="FindVolumeWalls"/> for the volume cluster columns.</summary>
	public Dictionary<(DateTime Expiry, decimal Strike), (long CallVolume, long PutVolume)> FullVolume { get; }
	public decimal MaxGross { get; }
	public decimal MaxAbsNet { get; }
	public decimal TotalCallGex { get; }
	public decimal TotalPutGex { get; }
	public Dictionary<DateTime, decimal?> GravityByExpiry { get; }
	/// <summary>Gross-gamma-weighted mean strike per expiry — the smooth analogue of <see cref="GravityByExpiry"/>.
	/// Gravity is an argmax, so when the top strikes are near-tied it rattles between them minute to minute and a
	/// coarse sampling grid turns that flicker into a false signal. The centroid moves continuously instead.</summary>
	public Dictionary<DateTime, decimal?> GrossCentroidByExpiry { get; }
	/// <summary>Net-GEX-weighted signed distance from spot per expiry, in strike points:
	/// Σ(net_k × (K − S)) / Σ|net_k|. Positive = the net exposure sits ABOVE spot (a ladder that is green up top),
	/// negative = below (red underneath). This is the whole-ladder reading rather than any single strike.</summary>
	public Dictionary<DateTime, decimal?> NetPullByExpiry { get; }
	public IReadOnlyList<GexContributor> Contributors { get; }
	/// <summary>Separate from <see cref="Contributors"/>: max pain sums total ITM payout across every listed
	/// contract, so — unlike Gravity/Centroid/NetPull/gamma-flip, all argmax- or weighted-average-shaped and
	/// so exactly what a single static OI block (SPX's legacy AM-settled root on a standard-monthly expiry)
	/// can hijack — merging the sibling root back in is the right read here, matching
	/// <see cref="CandidateScorer.ComputeMaxPainPrice"/>'s choice to keep merging while
	/// <see cref="CandidateScorer.ComputeGex"/> stopped.</summary>
	public IReadOnlyList<GexContributor> MaxPainContributors { get; }

	private GexMatrix(List<DateTime> expiries, List<decimal> strikes, Dictionary<(DateTime, decimal), GexCell> cells, Dictionary<(DateTime, decimal), GexCell> fullCells, Dictionary<(DateTime, decimal), (long CallOi, long PutOi)> fullOi, Dictionary<(DateTime, decimal), (long CallVolume, long PutVolume)> fullVolume, decimal maxGross, decimal maxAbsNet, decimal totalCallGex, decimal totalPutGex, Dictionary<DateTime, decimal?> gravityByExpiry, Dictionary<DateTime, decimal?> grossCentroidByExpiry, Dictionary<DateTime, decimal?> netPullByExpiry, IReadOnlyList<GexContributor> contributors, IReadOnlyList<GexContributor> maxPainContributors)
	{
		Expiries = expiries;
		Strikes = strikes;
		Cells = cells;
		FullCells = fullCells;
		FullOi = fullOi;
		FullVolume = fullVolume;
		MaxGross = maxGross;
		MaxAbsNet = maxAbsNet;
		TotalCallGex = totalCallGex;
		TotalPutGex = totalPutGex;
		GravityByExpiry = gravityByExpiry;
		GrossCentroidByExpiry = grossCentroidByExpiry;
		NetPullByExpiry = netPullByExpiry;
		Contributors = contributors;
		MaxPainContributors = maxPainContributors;
	}

	/// <summary>
	/// Estimates the chain-wide gamma flip price S* — the underlying level where dealer net dollar-gamma
	/// (Σ callGEX − Σ putGEX) crosses zero, summed across every contributor in the displayed window.
	/// </summary>
	public decimal? FindGammaFlip(decimal currentSpot) => FindGammaFlipImpl(currentSpot, Contributors);

	/// <summary>
	/// Per-expiration variant of <see cref="FindGammaFlip(decimal)"/>: the flip price using only
	/// contributors that expire on <paramref name="expiry"/>. Useful for seeing how the flip migrates
	/// across maturities (front-month flips usually sit closer to spot than back-month).
	/// </summary>
	public decimal? FindGammaFlip(decimal currentSpot, DateTime expiry)
	{
		var perExpiry = Contributors.Where(c => c.Expiry == expiry.Date).ToList();
		return FindGammaFlipImpl(currentSpot, perExpiry);
	}

	/// <summary>
	/// Net dollar-gamma is typically monotone-increasing in S (puts dominate at low S, calls at high S),
	/// so we bracket the sign change by stepping outward from spot at 1% increments out to ±70%, then
	/// bisect to ~$0.01. Returns null when no sign change is found in the search range — usually means
	/// the contributor set is entirely call- or entirely put-dominated.
	/// </summary>
	private static decimal? FindGammaFlipImpl(decimal currentSpot, IReadOnlyList<GexContributor> contribs)
	{
		if (contribs.Count == 0 || currentSpot <= 0m) return null;

		decimal Net(decimal s)
		{
			decimal sum = 0m;
			foreach (var c in contribs)
			{
				var g = (decimal)OptionMath.Gamma(s, c.Strike, c.TimeYears, OptionMath.RiskFreeRate, c.Iv);
				var dollar = g * c.Oi * 100m * s;
				sum += c.IsCall ? dollar : -dollar;
			}
			return sum;
		}

		var atSpot = Net(currentSpot);
		if (atSpot == 0m) return Math.Round(currentSpot, 2);

		decimal lo, hi;
		var step = currentSpot * 0.01m;
		if (atSpot > 0m)
		{
			hi = currentSpot;
			lo = 0m;
			var minS = currentSpot * 0.3m;
			var found = false;
			for (var s = currentSpot - step; s >= minS; s -= step)
			{
				if (Net(s) <= 0m) { lo = s; found = true; break; }
				hi = s;
			}
			if (!found) return null;
		}
		else
		{
			lo = currentSpot;
			hi = 0m;
			var maxS = currentSpot * 1.7m;
			var found = false;
			for (var s = currentSpot + step; s <= maxS; s += step)
			{
				if (Net(s) >= 0m) { hi = s; found = true; break; }
				lo = s;
			}
			if (!found) return null;
		}

		// Bisect: invariant Net(lo) ≤ 0, Net(hi) ≥ 0, lo < hi
		for (int i = 0; i < 50; i++)
		{
			if (hi - lo < 0.01m) break;
			var mid = (lo + hi) / 2m;
			var nMid = Net(mid);
			if (nMid == 0m) return Math.Round(mid, 2);
			if (nMid < 0m) lo = mid;
			else hi = mid;
		}
		return Math.Round((lo + hi) / 2m, 2);
	}

	/// <summary>
	/// Returns (callWall, putWall) for <paramref name="expiry"/>: the strikes carrying the largest CallGex
	/// and PutGex within that expiry's row of the heatmap. Sourced from <see cref="FullCells"/> — every
	/// strike in range, not the --max-strikes-capped <see cref="Cells"/> the display table draws from — so
	/// a wall outside the row window can't be silently replaced by a lesser in-window local max (the same
	/// bug already fixed for Gravity).
	/// </summary>
	public (decimal? CallWall, decimal? PutWall) FindWalls(DateTime expiry)
	{
		decimal? bestCall = null, bestPut = null;
		decimal bestCallGex = 0m, bestPutGex = 0m;
		foreach (var ((exp, strike), cell) in FullCells)
		{
			if (exp != expiry.Date) continue;
			if (cell.CallGex > bestCallGex) { bestCallGex = cell.CallGex; bestCall = strike; }
			if (cell.PutGex > bestPutGex) { bestPutGex = cell.PutGex; bestPut = strike; }
		}
		return (bestCall, bestPut);
	}

	/// <summary>Same shape as <see cref="FindWalls"/> but ranked by standing open interest instead of dollar
	/// gamma — the strikes where the most contracts are actually parked, per side, for <paramref name="expiry"/>.</summary>
	public (decimal? CallWall, decimal? PutWall) FindOiWalls(DateTime expiry)
	{
		decimal? bestCall = null, bestPut = null;
		long bestCallOi = 0, bestPutOi = 0;
		foreach (var ((exp, strike), oi) in FullOi)
		{
			if (exp != expiry.Date) continue;
			if (oi.CallOi > bestCallOi) { bestCallOi = oi.CallOi; bestCall = strike; }
			if (oi.PutOi > bestPutOi) { bestPutOi = oi.PutOi; bestPut = strike; }
		}
		return (bestCall, bestPut);
	}

	/// <summary>Same shape as <see cref="FindWalls"/> but ranked by the day's traded volume instead of dollar
	/// gamma or standing OI — the strikes seeing the most FLOW today, per side, for <paramref name="expiry"/>.</summary>
	public (decimal? CallWall, decimal? PutWall) FindVolumeWalls(DateTime expiry)
	{
		decimal? bestCall = null, bestPut = null;
		long bestCallVolume = 0, bestPutVolume = 0;
		foreach (var ((exp, strike), vol) in FullVolume)
		{
			if (exp != expiry.Date) continue;
			if (vol.CallVolume > bestCallVolume) { bestCallVolume = vol.CallVolume; bestCall = strike; }
			if (vol.PutVolume > bestPutVolume) { bestPutVolume = vol.PutVolume; bestPut = strike; }
		}
		return (bestCall, bestPut);
	}

	/// <summary>
	/// Returns the max-pain strike for <paramref name="expiry"/>: the listed strike that minimizes the
	/// total dollar value of contracts expiring in-the-money (Σ max(S−K,0)·OI for calls + Σ max(K−S,0)·OI
	/// for puts). This is the "pin" level where holders collectively lose the most. Evaluated only at
	/// strikes actually present in the contributor set for that expiry. Unlike Gravity/Centroid/NetPull (which
	/// read the fetched chain within --strike-range), <see cref="MaxPainContributors"/> is NOT windowed by
	/// --strike-range at all — a candidate strike inside the window can still have its payout swung by real
	/// OI sitting outside it, matching <see cref="CandidateScorer.ComputeMaxPainPrice"/>'s own unbounded scan.
	/// A near-flat payout curve routinely leaves several strikes within pennies of the true minimum; ties
	/// break toward the strike closest to <paramref name="spot"/>, matching
	/// <see cref="CandidateScorer.ComputeMaxPainPrice"/> — this used to pick the lowest tied strike instead
	/// (no spot input at all), which was the last live source of `wa analyze gex` and `wa ai scan` reporting
	/// different max-pain for the identical book.
	/// </summary>
	public decimal? FindMaxPain(DateTime expiry, decimal spot)
	{
		var perExpiry = MaxPainContributors.Where(c => c.Expiry == expiry.Date).ToList();
		if (perExpiry.Count == 0) return null;

		var candidateStrikes = perExpiry.Select(c => c.Strike).Distinct().OrderBy(s => s).ToList();
		decimal? bestStrike = null;
		decimal? bestPayout = null;
		foreach (var s in candidateStrikes)
		{
			decimal payout = 0m;
			foreach (var c in perExpiry)
			{
				var itm = c.IsCall ? Math.Max(s - c.Strike, 0m) : Math.Max(c.Strike - s, 0m);
				payout += itm * c.Oi;
			}
			if (!bestPayout.HasValue || payout < bestPayout.Value)
			{
				bestPayout = payout;
				bestStrike = s;
				continue;
			}
			if (payout != bestPayout.Value || !bestStrike.HasValue) continue;
			if (Math.Abs(s - spot) < Math.Abs(bestStrike.Value - spot)) bestStrike = s;
		}
		return bestStrike;
	}

	/// <summary>
	/// Builds the (expiry × strike) exposure matrix from a raw chain. Per-cell exposure is split between
	/// CallGex and PutGex, each computed from <paramref name="greek"/>:
	/// <list type="bullet">
	/// <item><description><b>Gamma</b> (GEX) — gamma × OI × 100 × spot: dollars of dealer delta per $1 move in spot.</description></item>
	/// <item><description><b>Vanna</b> (VEX) — vanna × OI × 100 × spot × 0.01: dollars of dealer delta per ONE vol point
	/// of IV change. The 0.01 makes the number directly readable as flow (vanna itself is per 1.00 = 100 vol points),
	/// and keeps the two greeks on comparable "$ of delta per unit move" footing.</description></item>
	/// </list>
	/// Filters strikes to
	/// ±strikeRangeFraction of spot and (when expiryFilter is null) keeps every expiration within
	/// maxDteDays days-to-expiry. When expiryFilter is set, all other expirations are dropped.
	/// Caps row count to <paramref name="maxStrikes"/> by keeping the strikes closest to spot — high-priced
	/// underlyings (e.g. SPY) otherwise pull hundreds of strikes into the heatmap.
	/// </summary>
	/// <summary>True remaining life of a contract: calendar time from the observation instant to the expiry
	/// session's close (13:00 ET on early-close half days), floored at 15 minutes so an at-or-after-close run
	/// degrades to a near-expiry ATM spike instead of a division blow-up. Replaces the old one-day floor, which
	/// paired the vendor's true-clock IVs with a T up to ~4× too long all through a 0DTE session — overstating
	/// the implied distribution width and inflating far-from-money gamma (a 15.9k-OI put ~1% OTM out-gunned the
	/// ATM complex for gravity on 2026-08-18 under the floor; the same book at true clock ranked them correctly).
	/// The mid back-solve inside Build stays self-consistent either way because it solves at this same T.</summary>
	internal static double TimeYears(DateTime asOf, DateTime expiryDate)
	{
		var close = expiryDate.Date + (MarketCalendar.IsEarlyClose(expiryDate.Date) ? new TimeSpan(13, 0, 0) : new TimeSpan(16, 0, 0));
		return Math.Max((close - asOf).TotalDays, 15.0 / (24 * 60)) / 365.0;
	}

	public static GexMatrix Build(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		string ticker,
		decimal spot,
		DateTime asOf,
		DateTime? expiryFilter,
		decimal strikeRangeFraction,
		int maxDteDays,
		int maxStrikes,
		GreekKind greek = GreekKind.Gamma)
	{
		var minStrike = spot * (1m - strikeRangeFraction);
		var maxStrike = spot * (1m + strikeRangeFraction);
		var asOfDate = asOf.Date;

		var raw = new Dictionary<(DateTime, decimal), (decimal CallGex, decimal PutGex)>();
		// OI and volume clusters ("OI wall" / "volume wall"): the strikes carrying the most standing open interest
		// or the most day volume per side, per expiry — same population as the gamma cells above (exact-root,
		// same strike/expiry window, same trusted-IV gate), just ranked by a different per-contract field.
		var rawOi = new Dictionary<(DateTime, decimal), (long CallOi, long PutOi)>();
		var rawVolume = new Dictionary<(DateTime, decimal), (long CallVolume, long PutVolume)>();
		var expirySet = new HashSet<DateTime>();
		var strikeSet = new HashSet<decimal>();
		var rawContribs = new List<(DateTime Expiry, decimal Strike, double TimeYears, decimal Iv, long Oi, bool IsCall)>();
		// Max pain sums total ITM payout across every listed contract, so unlike Gravity/Centroid/NetPull/the
		// chain totals below (all argmax- or weighted-average-shaped, and so exactly the statistics a single
		// static block — SPX's legacy AM-settled OI on a standard-monthly expiry — can hijack, per
		// CandidateScorer.ComputeGex) it's the one place the merge is still the right read: a strike's real
		// total liability doesn't care which of the two roots the OI sits under. Kept as its own contributor
		// list rather than switching the whole loop, so it's the one exception, not a silent default.
		var maxPainContribs = new List<(DateTime Expiry, decimal Strike, double TimeYears, decimal Iv, long Oi, bool IsCall)>();

		foreach (var kv in quotes)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(kv.Key);
			if (parsed == null || !ParsingHelpers.RootsMatchForAggregation(parsed.Root, ticker, parsed.ExpiryDate)) continue;
			var exactRoot = string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase);
			if (expiryFilter.HasValue && parsed.ExpiryDate.Date != expiryFilter.Value.Date) continue;
			if (parsed.ExpiryDate.Date < asOfDate) continue;
			var q = kv.Value;
			if (!q.OpenInterest.HasValue || q.OpenInterest.Value <= 0) continue;

			var timeYears = TimeYears(asOf, parsed.ExpiryDate);
			var isCall = parsed.CallPut == "C";

			// Max pain only needs strike/side/OI, and — unlike everything below — is NOT bounded by --strike-range
			// (minStrike/maxStrike): a candidate strike WITHIN the window can still have its total payout swung by
			// OI sitting OUTSIDE it, and ComputeMaxPainPrice (the `wa ai scan` side) has no range restriction at
			// all. Recorded before both the IV gate below (a contract can carry real OI with a dead book — routine
			// for SPX's legacy strikes, which barely trade) and the range check further down. Verified live: with
			// --strike-range at its 20% default this was still leaving max pain $80 off CandidateScorer's answer
			// for the identical SPXW 10/16 book; widening to the full 200% converged them exactly, so the miss was
			// specifically OI beyond the display window feeding the payout sum, not a fetch or IV gap.
			maxPainContribs.Add((parsed.ExpiryDate.Date, parsed.Strike, timeYears, 0m, q.OpenInterest.Value, isCall));
			if (parsed.Strike < minStrike || parsed.Strike > maxStrike) continue;

			// Vendor IV is taken only from a live two-sided book (see OptionMath.TrustedVendorIv). Without that
			// guard the heatmap is at the mercy of dead books, and an expiry-day evening — exactly when this
			// command gets run — is full of them: Schwab quoted the just-expired SPCX 110P at no-bid/0.01-ask
			// with IV 887%, which cut that strike's dollar gamma from $13.4M to $1.0M and reshuffled the ladder.
			//
			// The data/oi EOD snapshot stores iv = null for every contract on its OWN expiry day: the Python
			// back-solve degenerates at T≈0 against the 16:00 stamp, so the entire 0DTE expiry would otherwise
			// vanish (and `analyze gex` falls through to the next day). Back-solve the IV from the captured mid
			// at the (already day-floored) timeYears so the 0DTE — which still carries real OI + bid/ask — survives.
			// That solve is now also the fallback for an untrusted vendor IV; it needs a two-sided book itself,
			// so a genuinely dead contract solves to nothing and drops out instead of contributing phantom gamma.
			var iv = OptionMath.TrustedVendorIv(q) ?? 0m;
			if (iv <= 0m && !string.IsNullOrEmpty(parsed.CallPut))
			{
				var mid = q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value > 0m && q.Ask.Value > 0m
					? (q.Bid.Value + q.Ask.Value) / 2m
					: q.LastPrice ?? 0m;
				if (mid > 0m)
				{
					var solved = OptionMath.ImpliedVol(spot, parsed.Strike, timeYears, OptionMath.RiskFreeRate, mid, parsed.CallPut);
					if (solved > 0.011m && solved < 4.99m) iv = solved;
				}
			}
			if (iv <= 0m && q.HistoricalVolatility is > 0m) iv = q.HistoricalVolatility.Value;
			if (iv <= 0m) continue;

			var dollarGex = greek == GreekKind.Vanna
				? OptionMath.Vanna(spot, parsed.Strike, timeYears, OptionMath.RiskFreeRate, iv) * q.OpenInterest.Value * 100m * spot * 0.01m
				: OptionMath.Gamma(spot, parsed.Strike, timeYears, OptionMath.RiskFreeRate, iv) * q.OpenInterest.Value * 100m * spot;
			// Only a dead contract is dropped. Gamma is non-negative so this is the old `<= 0` guard; vanna is signed,
			// and dropping its negative half would silently delete every strike above the d2 = 0 flip.
			if (dollarGex == 0m) continue;

			if (!exactRoot) continue; // merged-root-only contract: already counted for max pain above, nothing else below

			var key = (parsed.ExpiryDate.Date, parsed.Strike);
			raw.TryGetValue(key, out var existing);
			if (isCall)
				raw[key] = (existing.CallGex + dollarGex, existing.PutGex);
			else
				raw[key] = (existing.CallGex, existing.PutGex + dollarGex);
			rawContribs.Add((parsed.ExpiryDate.Date, parsed.Strike, timeYears, iv, q.OpenInterest.Value, isCall));
			expirySet.Add(parsed.ExpiryDate.Date);
			strikeSet.Add(parsed.Strike);

			rawOi.TryGetValue(key, out var existingOi);
			rawVolume.TryGetValue(key, out var existingVolume);
			var volume = q.Volume ?? 0L;
			if (isCall)
			{
				rawOi[key] = (existingOi.CallOi + q.OpenInterest.Value, existingOi.PutOi);
				rawVolume[key] = (existingVolume.CallVolume + volume, existingVolume.PutVolume);
			}
			else
			{
				rawOi[key] = (existingOi.CallOi, existingOi.PutOi + q.OpenInterest.Value);
				rawVolume[key] = (existingVolume.CallVolume, existingVolume.PutVolume + volume);
			}
		}

		var expiries = expirySet.OrderBy(d => d).ToList();
		if (!expiryFilter.HasValue)
			expiries = expiries.Where(e => (e - asOfDate).Days <= maxDteDays).ToList();
		var keptExpirySet = expiries.ToHashSet();

		// Drop strikes that have no surviving cell after the expiry-window cap, then cap to maxStrikes
		// closest to spot so high-priced underlyings don't blow up the row count.
		var liveStrikes = new HashSet<decimal>();
		foreach (var ((exp, strike), _) in raw)
			if (keptExpirySet.Contains(exp))
				liveStrikes.Add(strike);
		var keptStrikeSet = liveStrikes.OrderBy(s => Math.Abs(s - spot)).Take(maxStrikes).ToHashSet();
		var strikes = keptStrikeSet.OrderByDescending(s => s).ToList();

		var cells = new Dictionary<(DateTime, decimal), GexCell>();
		decimal maxGross = 0m, maxAbsNet = 0m, totalCall = 0m, totalPut = 0m;
		foreach (var ((exp, strike), v) in raw)
		{
			if (!keptExpirySet.Contains(exp)) continue;
			if (!keptStrikeSet.Contains(strike)) continue;
			var cell = new GexCell(v.CallGex, v.PutGex);
			cells[(exp, strike)] = cell;
			if (cell.Gross > maxGross) maxGross = cell.Gross;
			var absNet = Math.Abs(cell.Net);
			if (absNet > maxAbsNet) maxAbsNet = absNet;
			totalCall += cell.CallGex;
			totalPut += cell.PutGex;
		}

		// Gravity is an argmax over strikes, computed over the full in-range set rather than the --max-strikes
		// DISPLAY cap (the same reasoning Centroid/NetPull/Contributors below already follow). An argmax is
		// even more exposed to that cap than a weighted average: a smooth stat like centroid just shifts a
		// little when a tail strike drops out, but an argmax can flip entirely the moment the true winner
		// falls outside the row cap — exactly what happened live on a thin book (XSP), where the real gravity
		// strike sat outside the default 50-strike window and got silently replaced by a within-cap local max.
		var grossByExpiryFull = new Dictionary<DateTime, Dictionary<decimal, decimal>>();
		var fullCells = new Dictionary<(DateTime, decimal), GexCell>();
		foreach (var ((exp, strike), v) in raw)
		{
			if (!keptExpirySet.Contains(exp)) continue;
			var cell = new GexCell(v.CallGex, v.PutGex);
			fullCells[(exp, strike)] = cell;
			if (!grossByExpiryFull.TryGetValue(exp, out var g)) { g = new(); grossByExpiryFull[exp] = g; }
			g[strike] = cell.Gross;
		}

		// OI/volume walls read from the full in-range set too, for the same reason as fullCells above — an
		// argmax over a --max-strikes-trimmed row can silently swap in a lesser local max.
		var fullOi = new Dictionary<(DateTime, decimal), (long CallOi, long PutOi)>();
		foreach (var ((exp, strike), v) in rawOi)
			if (keptExpirySet.Contains(exp)) fullOi[(exp, strike)] = v;
		var fullVolume = new Dictionary<(DateTime, decimal), (long CallVolume, long PutVolume)>();
		foreach (var ((exp, strike), v) in rawVolume)
			if (keptExpirySet.Contains(exp)) fullVolume[(exp, strike)] = v;

		var gravity = new Dictionary<DateTime, decimal?>();
		foreach (var exp in expiries)
		{
			if (grossByExpiryFull.TryGetValue(exp, out var g) && g.Count > 0)
				gravity[exp] = g.OrderByDescending(kv => kv.Value).First().Key;
			else
				gravity[exp] = null;
		}

		// Whole-ladder aggregates, computed over every in-range strike rather than the --max-strikes DISPLAY set:
		// a centroid or a pull that shifted when the row cap trimmed a tail would be reporting the cap, not the book.
		// Same principle the contributors below follow.
		var centroid = new Dictionary<DateTime, decimal?>();
		var netPull = new Dictionary<DateTime, decimal?>();
		foreach (var exp in expiries)
		{
			decimal grossSum = 0m, grossMoment = 0m, absNetSum = 0m, netMoment = 0m;
			foreach (var ((e, strike), v) in raw)
			{
				if (e != exp) continue;
				var cell = new GexCell(v.CallGex, v.PutGex);
				grossSum += cell.Gross;
				grossMoment += cell.Gross * strike;
				absNetSum += Math.Abs(cell.Net);
				netMoment += cell.Net * (strike - spot);
			}
			centroid[exp] = grossSum > 0m ? grossMoment / grossSum : null;
			// Pull is reported in ATM-expected-move units: the raw lean (points) divided by spot·σ_atm·√T for the
			// contract's remaining life. In points the row decays mechanically all session — the true clock
			// concentrates gamma onto ATM as T shrinks, so a STATIC book bleeds toward zero into the close —
			// while in expected-move units a static book holds roughly flat and only genuine spot/IV changes
			// move the number. σ_atm = the IV of the contributor(s) nearest spot (normally the ATM C/P pair).
			var atm = rawContribs.Where(c => c.Expiry == exp).OrderBy(c => Math.Abs(c.Strike - spot)).Take(2).ToList();
			var expectedMove = atm.Count > 0 ? spot * (decimal)(Math.Sqrt(atm[0].TimeYears) * (double)atm.Average(c => c.Iv)) : 0m;
			netPull[exp] = absNetSum > 0m && expectedMove > 0m ? netMoment / absNetSum / expectedMove : null;
		}

		// Analytics use the full strike-range × kept-expiries set, NOT the --max-strikes display cap —
		// gamma flip would be skewed by an arbitrary display-row limit.
		var contributors = rawContribs
			.Where(r => keptExpirySet.Contains(r.Expiry))
			.Select(r => new GexContributor(r.Expiry, r.Strike, r.TimeYears, r.Iv, r.Oi, r.IsCall))
			.ToList();
		// Max pain's own contributor list — see the comment on maxPainContribs above for why this one stays merged.
		var maxPainContributors = maxPainContribs
			.Where(r => keptExpirySet.Contains(r.Expiry))
			.Select(r => new GexContributor(r.Expiry, r.Strike, r.TimeYears, r.Iv, r.Oi, r.IsCall))
			.ToList();

		return new GexMatrix(expiries, strikes, cells, fullCells, fullOi, fullVolume, maxGross, maxAbsNet, totalCall, totalPut, gravity, centroid, netPull, contributors, maxPainContributors);
	}
}
