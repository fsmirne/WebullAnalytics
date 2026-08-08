using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
	[Description("Restrict to a single expiration date (YYYY-MM-DD). Default: show all expirations in the chain.")]
	public string? Expiry { get; set; }

	[CommandOption("--strike-range <PCT>")]
	[DefaultValue(20)]
	[Description("Strike window as ± percent of spot. Default: 20.")]
	public int StrikeRangePct { get; set; } = 20;

	[CommandOption("--max-strikes <N>")]
	[DefaultValue(50)]
	[Description("Max strike rows to display. Picks the N strikes closest to spot within --strike-range. Default: 50.")]
	public int MaxStrikes { get; set; } = 50;

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
	[Description("0DTE intraday GEX heatmap: rows = strikes, columns = RTH time buckets (--interval), recomputing per-strike GEX at each bucket's spot (from data/intraday) against the day's fixed OI. Shows the gravity migrating as price moves. Without --date (or with --date today) this is the RUNNING-DAY view: OI/IVs come from the live chain fetch and the columns run 09:30 through the current bucket. A past --date replays that day offline from its data/oi snapshot, with per-bucket IVs back-solved from the minute-quote store (data/quotes) when it covers the day, else frozen from the snapshot. Skips the walls/totals/per-expiry tables.")]
	public bool Intraday { get; set; }

	[CommandOption("--interval <MIN>")]
	[Description("--intraday time-bucket size in minutes. Default: auto — the finest of 30/45/60/90/120 whose panels fit the terminal width side by side (both the gamma and VEX migration tables under the default --greek both; just the gamma table under --greek gamma).")]
	public int? IntervalMin { get; set; }

	[CommandOption("--time <HH:MM>")]
	[Description("--intraday only: narrow the VEX side panel to the single bucket nearest this ET time (default: a full VEX migration panel, one column per bucket like the gamma table) — e.g. --time 10:00 focuses on what the 0DTE vanna book looked like at 10am. The gamma migration columns are unaffected.")]
	public string? Time { get; set; }

	[CommandOption("--exante")]
	[Description("--intraday only: price the 0DTE gamma with the PRIOR trading day's snapshot IVs (falling back to a back-solve from the prior day's mids at the prior day's spot) instead of back-solving from this day's EOD mids. The default solve leaks the session's outcome into every column — a put that finished ITM has a fat EOD mid, back-solves to an inflated IV, and its strike re-brightens/dims by where the day CLOSED; ex-ante IVs show what was actually hedgeable at each bucket. 0DTE contracts absent from the prior snapshot are dropped.")]
	public bool Exante { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("<ticker> is required");
		if (!BothGreeks && !TryParseGreek(Greek, out _)) return ValidationResult.Error($"--greek: expected 'gamma', 'vanna' or 'both', got '{Greek}'");
		if (Expiry != null && !DateTime.TryParseExact(Expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--expiry: expected YYYY-MM-DD, got '{Expiry}'");
		if (StrikeRangePct <= 0 || StrikeRangePct > 200) return ValidationResult.Error($"--strike-range: must be in (0, 200], got {StrikeRangePct}");
		if (MaxStrikes < 1 || MaxStrikes > 200) return ValidationResult.Error($"--max-strikes: must be in [1, 200], got {MaxStrikes}");
		if (Dte < 0 || Dte > 60) return ValidationResult.Error($"--dte: must be in [0, 60], got {Dte}");
		if (IntervalMin.HasValue && (IntervalMin.Value < 5 || IntervalMin.Value > 120)) return ValidationResult.Error($"--interval: must be in [5, 120] minutes, got {IntervalMin}");
		if (Time != null && !Intraday) return ValidationResult.Error("--time anchors the --intraday VEX column; add --intraday");
		if (Time != null && !TimeSpan.TryParseExact(Time, @"hh\:mm", CultureInfo.InvariantCulture, out var vt)) return ValidationResult.Error($"--time: expected HH:MM (ET), got '{Time}'");
		else if (Time != null && TimeSpan.TryParseExact(Time, @"hh\:mm", CultureInfo.InvariantCulture, out var vtOk) && (vtOk < new TimeSpan(9, 30, 0) || vtOk > new TimeSpan(16, 0, 0))) return ValidationResult.Error($"--time: must be within RTH 09:30–16:00 ET, got '{Time}'");
		if (Exante && !Intraday) return ValidationResult.Error("--exante only applies to the --intraday heatmap");
		// The intraday view's whole subject is the gravity strike migrating as spot moves, and it reads its Gravity row
		// back out of the gamma-only data/gex log. Vanna has no gravity, so an explicit vanna request is refused
		// outright; the 'both' default quietly resolves to the gamma view so plain --intraday keeps working.
		if (Intraday && !BothGreeks && TryParseGreek(Greek, out var g) && g != GreekKind.Gamma) return ValidationResult.Error("--intraday maps the gamma gravity migration and has no vanna equivalent; drop --greek vanna");
		if (TopWalls < 1 || TopWalls > 25) return ValidationResult.Error($"--top-walls: must be in [1, 25], got {TopWalls}");
		if (Dump && EvaluationDateOverride.HasValue) return ValidationResult.Error("--dump applies to live fetches only (no --date)");
		return ValidationResult.Success();
	}

	/// <summary>'both' = render the gamma AND vanna maps in one run, side by side when the terminal is wide enough.</summary>
	internal bool BothGreeks => string.Equals(Greek?.Trim(), "both", StringComparison.OrdinalIgnoreCase);

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
					// Bound to the near-money strikes the heatmap shows (the webull path already caps at MaxStrikes),
					// so the common case is one small request; the client still date-splits as a safety net.
					(schwabSpot, schwabQuotes) = await SchwabOptionsClient.FetchChainAsync(token, ticker, fromExpiry, toExpiry, cancellation, strikeCount: settings.MaxStrikes);
				}
				catch (SchwabAuthException ex)
				{
					AnsiConsole.MarkupLine($"[red]Schwab auth failed: {Markup.Escape(ex.Message)} Re-run 'wa schwab login'.[/]");
					return 1;
				}
				// A $SPX chains request returns BOTH the SPX and SPXW roots — keep only the requested one.
				quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
				foreach (var q in schwabQuotes)
					if (string.Equals(ParsingHelpers.ParseOptionSymbol(q.ContractSymbol)?.Root, ticker, StringComparison.OrdinalIgnoreCase))
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

		// --intraday: 0DTE strikes × RTH-hours gravity-migration heatmap. Offline-historical only (needs an explicit
		// --date with both a data/oi snapshot and a data/intraday spot file). Replaces the normal tables.
		if (settings.Intraday)
		{
			if (!isOfflineHistorical && asOf.Date != DateTime.Today)
			{
				AnsiConsole.MarkupLine("[red]--intraday for a past day requires a data/oi snapshot for that --date (offline-historical mode); none was loaded. The running day needs no snapshot — omit --date.[/]");
				return 1;
			}
			if (settings.Exante && !ApplyExanteIvs(ticker, asOf.Date, quotes))
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
			}
			TimeSpan? vexAt = settings.Time != null ? TimeSpan.ParseExact(settings.Time, @"hh\:mm", CultureInfo.InvariantCulture) : null;
			RenderIntradayGexHeatmap(ticker, asOf.Date, quotes, settings.StrikeRangePct / 100m, settings.MaxStrikes, settings.IntervalMin, settings.Exante, settings.VendorName, liveChain: !isOfflineHistorical, withVexNow: settings.BothGreeks, vexAt: vexAt);
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
			AnsiConsole.MarkupLine($"[yellow]No strikes match within ±{settings.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]");
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

		RenderHeader(ticker, spot.Value, asOf, expiryFilter, matrix);
		AnsiConsole.WriteLine();
		if (vannaMatrix != null)
			RenderHeatmapsSideBySide(matrix, vannaMatrix, spot.Value);
		else
			RenderHeatmap(matrix, spot.Value, greek);
		AnsiConsole.WriteLine();
		if (greek == GreekKind.Vanna)
			RenderPerExpiryVanna(matrix, asOf);
		else
			RenderPerExpirySummary(matrix, spot.Value, asOf);
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

	/// <summary>--exante: replaces every 0DTE contract's IV with the prior trading day's snapshot value (falling back
	/// to a back-solve from the prior day's captured mid at the prior day's spot). The data/oi EOD snapshot stores
	/// iv = null for the own-day expiry, so GexMatrix.Build otherwise back-solves 0DTE IVs from POST-session mids at
	/// each bucket's historical spot — which leaks the day's outcome into every column (a put that finished ITM has a
	/// fat EOD mid, back-solves to an inflated IV, and its gamma re-shapes by where the day closed). Contracts with no
	/// usable prior-day IV or mid are removed so Build cannot fall back to the leaky same-day solve.</summary>
	private static bool ApplyExanteIvs(string ticker, DateTime date, Dictionary<string, OptionContractQuote> quotes)
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

		const double timeYears = 1.0 / 365.0; // same 0DTE day-floor as GexMatrix.Build
		int applied = 0, solved = 0, dropped = 0;
		foreach (var sym in quotes.Keys.ToList())
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null || !string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase) || parsed.ExpiryDate.Date != date) continue; // only the 0DTE expiry is rendered
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
		AnsiConsole.MarkupLine($"[dim]--exante: 0DTE IVs from {Markup.Escape(priorPath)} ({applied} snapshot IVs, {solved} back-solved from prior-day mids, {dropped} contract(s) dropped).[/]");
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
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
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
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
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

		foreach (var strike in matrix.Strikes)
		{
			var strikeStr = StrikeLabel(strike, wholeGrid);
			var isAtm = strike == atmStrike;
			var strikeMarkup = isAtm ? $"[bold yellow]{strikeStr}[/]" : strikeStr;

			var cells = new List<string> { strikeMarkup };
			foreach (var exp in shownExpiries)
			{
				matrix.Cells.TryGetValue((exp, strike), out var cell);
				// The gravity marker is a max-gross-GAMMA anchor. Under vanna the same strike would just be the biggest
				// |vanna|×OI pile, which is precisely the "level" reading this greek does not support — so it is not drawn.
				var isGravity = greek == GreekKind.Gamma && matrix.GravityByExpiry.TryGetValue(exp, out var grav) && grav.HasValue && grav.Value == strike;
				cells.Add(BuildHeatmapCellMarkup(cell, maxAbsNet, isGravity));
			}
			table.AddRow(cells.ToArray());
		}

		return table;
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
		table.AddColumn(new TableColumn("[bold]Gamma flip[/]").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("[bold]Max pain[/]").RightAligned().NoWrap());

		foreach (var exp in matrix.Expiries)
		{
			var dte = Math.Max(0, (exp.Date - asOf.Date).Days);
			matrix.GravityByExpiry.TryGetValue(exp, out var gravity);
			var (callWall, putWall) = matrix.FindWalls(exp);
			var flip = matrix.FindGammaFlip(spot, exp);
			var maxPain = matrix.FindMaxPain(exp);

			var gravityCell = gravity.HasValue ? $"${gravity.Value:N2}" : "[dim]—[/]";
			var grossCell = gravity.HasValue && matrix.Cells.TryGetValue((exp, gravity.Value), out var gc) ? FormatCompactDollars(gc.Gross) : "[dim]—[/]";
			table.AddRow($"{exp:yyyy-MM-dd}", dte.ToString(), gravityCell, grossCell, FormatWallStrike(callWall, "green"), FormatWallStrike(putWall, "red"), FormatPriceVsSpotCompact(flip, spot, regimeColor: true), FormatPriceVsSpotCompact(maxPain, spot, regimeColor: false));
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Gravity = strike with max gross gamma; Gross γ = the gross GEX ($call γ×OI + $put γ×OI) at that strike (the value underlined in the heatmap above). [green]Call wall[/] / [red]put wall[/] = strike with the largest call / put GEX for that expiry (resistance / support). Gamma flip = where dealer net dollar-gamma crosses 0 ([green]green[/] = spot in positive-γ regime, [red]red[/] = negative-γ). Max pain = strike minimizing total ITM payout (where most contracts expire worthless).[/]");
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

	/// <summary>Renders the 0DTE intraday GEX gravity-migration heatmap: rows = strikes (descending), columns = RTH
	/// hour marks. At each hour the per-strike GEX is recomputed at that hour's intraday spot against the day's fixed
	/// OI, so the gravity strike (bold-underlined) is seen migrating as price moves. Brightness ∝ |net| across all
	/// hours; green = call-dominated, red = put-dominated. When the minute-quote store (data/quotes, written by the
	/// wa-scraper / ThetaData sync) covers this day, each bucket's IVs are back-solved from THAT minute's NBBO mids
	/// instead of the morning snapshot's frozen values — the snapshot IVs age badly through a 0DTE session (IV
	/// collapses intraday, sharpening gamma toward ATM), which is why a frozen-IV replay disagrees with what the
	/// live command showed. A data/gex live log (when present) is rendered as a "Gravity" footer row for comparison.</summary>
	private static TimeSpan AbsSpan(TimeSpan t) => t < TimeSpan.Zero ? -t : t;

	private static void RenderIntradayGexHeatmap(string ticker, DateTime date, Dictionary<string, OptionContractQuote> quotes, decimal strikeRangeFraction, int maxStrikes, int? intervalMin, bool exante, string source, bool liveChain = false, bool withVexNow = false, TimeSpan? vexAt = null)
	{
		var expiry = date.Date;
		var intradaySpots = LoadIntradaySpots(ticker, date);
		if (intradaySpots.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]No intraday spots in data/intraday/{ticker}/{date:yyyy-MM-dd}.csv (file absent or no RTH rows).[/]");
			return;
		}

		// --exante deliberately pins prior-day IVs, so the time-matched minute quotes would defeat its purpose.

		// Resolve the bucket size: an explicit --interval is honored as-is; the default picks the finest of the
		// standard sizes whose panel set fits the terminal side by side (two migration panels under --greek both,
		// one under --greek gamma), using the same fixed cell geometry as the rendering below. Strike labels track
		// the spot magnitude, so a tape sample prices the column widths before any matrix is built.
		var panels = withVexNow ? 2 : 1;
		var sampleSpot = intradaySpots.Values.First();
		var estStrikeW = sampleSpot < 100m ? $"${sampleSpot:N2}".Length : StrikeLabel(Math.Round(sampleSpot), wholeGrid: true).Length;
		var estColW = 3 + Math.Max(7, $"{sampleSpot:F2}".Length);
		var fixedW = panels * (1 + estStrikeW + 3) + (panels - 1) * 2;
		int BucketsFor(int min) { var n = 0; for (var t = new TimeSpan(9, 30, 0); t < new TimeSpan(16, 0, 0); t += TimeSpan.FromMinutes(min)) n++; return n + 1; }
		var interval = intervalMin ?? new[] { 30, 45, 60, 90, 120 }.FirstOrDefault(c => fixedW + panels * BucketsFor(c) * estColW <= AnsiConsole.Profile.Width, 120);
		var minuteQuotes = exante ? new SortedDictionary<TimeSpan, Dictionary<string, OptionContractQuote>>() : LoadMinuteQuoteSets(ticker, date, quotes);

		// Column marks: 09:30 stepping by --interval to 16:00 (always include the 16:00 close).
		var open = new TimeSpan(9, 30, 0);
		var close = new TimeSpan(16, 0, 0);
		var hourMarks = new List<TimeSpan>();
		for (var t = open; t < close; t += TimeSpan.FromMinutes(interval)) hourMarks.Add(t);
		hourMarks.Add(close);
		// Match the nearest intraday minute within half a bucket so adjacent columns never share a spot.
		var tolerance = TimeSpan.FromMinutes(Math.Max(1, interval / 2));

		// Per kept hour: the spot, the per-strike GexCells for the 0DTE, and that hour's gravity strike.
		var hours = new List<(TimeSpan Mark, decimal Spot, Dictionary<decimal, GexCell> Cells, decimal? Gravity)>();
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
			if (minuteQuotes.Count > 0)
			{
				Dictionary<string, OptionContractQuote>? nearest = null;
				var bestQuoteDiff = tolerance;
				foreach (var kv in minuteQuotes)
				{
					var diff = kv.Key >= mark ? kv.Key - mark : mark - kv.Key;
					if (diff <= bestQuoteDiff) { bestQuoteDiff = diff; nearest = kv.Value; }
				}
				if (nearest == null) { skipped.Add(mark); continue; }   // store covers the day but not this bucket — a frozen-IV cell among time-matched ones would mislead
				bucketQuotes = nearest;
			}

			var m = GexMatrix.Build(bucketQuotes, ticker, spot.Value, expiry + mark, expiryFilter: expiry, strikeRangeFraction, maxDteDays: 0, maxStrikes);
			var cells = new Dictionary<decimal, GexCell>();
			foreach (var strike in m.Strikes)
				if (m.Cells.TryGetValue((expiry, strike), out var c)) cells[strike] = c;
			if (cells.Count == 0) continue;
			m.GravityByExpiry.TryGetValue(expiry, out var grav);
			hours.Add((mark, spot.Value, cells, grav));
			if (withVexNow)
			{
				var vmB = GexMatrix.Build(bucketQuotes, ticker, spot.Value, expiry + mark, expiryFilter: expiry, strikeRangeFraction, maxDteDays: 0, maxStrikes, GreekKind.Vanna);
				var vCells = new Dictionary<decimal, GexCell>();
				foreach (var strike in vmB.Strikes)
					if (vmB.Cells.TryGetValue((expiry, strike), out var vc)) vCells[strike] = vc;
				vannaHours.Add((mark, spot.Value, vCells));
			}
		}

		if (hours.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No 0DTE GEX cells at any hour for {ticker} {date:yyyy-MM-dd} (±{strikeRangeFraction * 100m:F0}% window).[/]");
			return;
		}

		var maxAbsNet = Math.Max(1m, hours.SelectMany(h => h.Cells.Values).Select(c => Math.Abs(c.Net)).DefaultIfEmpty(0m).Max());
		var allStrikes = hours.SelectMany(h => h.Cells.Keys).Distinct().OrderByDescending(s => s).ToList();
		var wholeGrid = IsWholeGrid(allStrikes);

		AnsiConsole.MarkupLine($"[bold]{ticker}[/] 0DTE {date:yyyy-MM-dd} — intraday GEX gravity migration");
		AnsiConsole.MarkupLine(minuteQuotes.Count > 0
			? $"[dim]IVs: back-solved per bucket from minute NBBO mids (data/quotes/{ticker}/{date:yyyy-MM-dd}.csv); OI fixed from the day's snapshot.[/]"
			: exante ? "[dim]IVs: frozen from the prior-day --exante values (no minute-quote coverage for this day).[/]"
			: liveChain ? "[dim]IVs: frozen from the live chain at fetch time; OI fixed from the same fetch (running day — early columns replay today's OI at each bucket's spot with current IVs).[/]"
			: "[dim]IVs: frozen from the day's OI-snapshot values (no minute-quote coverage for this day in data/quotes).[/]");

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]GEX (gamma)[/]");
		table.AddColumn(new TableColumn("[bold]Strike[/]").RightAligned().NoWrap());
		foreach (var h in hours)
			table.AddColumn(new TableColumn($"[bold]{h.Mark:hh\\:mm}[/]\n[dim]{h.Spot:F2}[/]").Centered().NoWrap());

		foreach (var strike in allStrikes)
		{
			var cells = new List<string> { StrikeLabel(strike, wholeGrid) };
			foreach (var h in hours)
			{
				h.Cells.TryGetValue(strike, out var cell);
				var isGravity = h.Gravity.HasValue && h.Gravity.Value == strike;
				cells.Add(BuildHeatmapCellMarkup(cell, maxAbsNet, isGravity));
			}
			table.AddRow(cells.ToArray());
		}

		// "Gravity" footer row: per bucket, the gravity the live `analyze gex` runs actually displayed (data/gex log)
		// nearest the mark. The live values come from vendor-reported IVs that are never persisted, so this row is
		// the only ground truth a replay can be compared against.
		var liveGravity = LoadLiveGravityLog(ticker, date, source);
		if (liveGravity.Count > 0)
		{
			var liveCells = new List<string> { "[bold cyan]Gravity[/]" };
			foreach (var h in hours)
			{
				decimal? g = null;
				var bestDiff = tolerance;
				foreach (var kv in liveGravity)
				{
					var diff = kv.Key >= h.Mark ? kv.Key - h.Mark : h.Mark - kv.Key;
					if (diff <= bestDiff) { bestDiff = diff; g = kv.Value; }
				}
				liveCells.Add(g.HasValue ? $"[bold cyan]${g.Value:N2}[/]" : "[dim]·[/]");
			}
			table.AddRow(liveCells.ToArray());
		}

		// VEX migration panel: the vanna analogue of the gamma table, bucket for bucket from the SAME
		// time-matched quotes, sharing the exact strike rows (headers are two lines in both, neither table has a
		// title, and the gamma table's Gravity footer is its LAST row — so the ladders align 1:1). --time narrows
		// the panel to the single bucket nearest that ET time for a focused read. Columns wraps the panel below
		// when the pair exceeds the terminal; the hint suggests the interval that would fit side by side.
		if (withVexNow && vannaHours.Count > 0)
		{
			var shownVanna = vexAt == null ? vannaHours : new List<(TimeSpan Mark, decimal Spot, Dictionary<decimal, GexCell> Cells)> { vannaHours.OrderBy(v => AbsSpan(v.Mark - vexAt.Value)).First() };
			var vMax = Math.Max(1m, shownVanna.SelectMany(h => h.Cells.Values).Select(c => Math.Abs(c.Net)).DefaultIfEmpty(0m).Max());
			var vex = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]VEX (vanna)[/]");
			vex.AddColumn(new TableColumn("[bold]Strike[/]").RightAligned().NoWrap());
			foreach (var h in shownVanna)
				vex.AddColumn(new TableColumn($"[bold]{h.Mark:hh\\:mm}[/]\n[dim]{h.Spot:F2}[/]").Centered().NoWrap());
			foreach (var strike in allStrikes)
			{
				var vCells = new List<string> { StrikeLabel(strike, wholeGrid) };
				foreach (var h in shownVanna)
				{
					h.Cells.TryGetValue(strike, out var cell);
					vCells.Add(BuildHeatmapCellMarkup(cell, vMax, isGravity: false));
				}
				vex.AddRow(vCells.ToArray());
			}
			AnsiConsole.Write(new Columns(table, vex) { Expand = false });

			// Same fixed cell geometry as the side-by-side expiry heatmaps: content max(7, spot label) + 2 padding
			// + 1 border per bucket column; strike column + 2 padding + 1 border; +1 closing border per panel.
			var strikeW = allStrikes.Count > 0 ? allStrikes.Max(k => StrikeLabel(k, wholeGrid).Length) : 7;
			var colW = 3 + Math.Max(7, hours.Max(h => $"{h.Spot:F2}".Length));
			var pairWidth = 2 * (1 + strikeW + 3) + (hours.Count + shownVanna.Count) * colW + 2;
			if (pairWidth > AnsiConsole.Profile.Width && vexAt == null)
			{
				var fitBuckets = Math.Max(2, (AnsiConsole.Profile.Width - 2 - 2 * (1 + strikeW + 3)) / (2 * colW));
				var fitInterval = (int)Math.Ceiling(390.0 / (fitBuckets - 1) / 30) * 30;
				AnsiConsole.MarkupLine($"[dim]Panels are stacked — together they need {pairWidth} columns but the terminal has {AnsiConsole.Profile.Width}. Raise --interval to ~{fitInterval} (or drop --interval for the auto fit, or use --time HH:MM for a single VEX bucket) to fit them side by side.[/]");
			}
		}
		else
			AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Cell = net GEX recomputed at each bucket's spot against the day's fixed OI. [green]Green[/] = call-dominated, [red]red[/] = put-dominated, brightness ∝ |net|. Bold + underlined cell = that bucket's gravity strike (max gross gamma)." + (liveGravity.Count > 0 ? " [cyan]Gravity[/] row = the gravity strike logged in real time by live `analyze gex` runs (data/gex) nearest each bucket." : "") + "[/]");
		if (withVexNow && vannaHours.Count > 0)
			AnsiConsole.MarkupLine($"[dim]VEX panel = 0DTE net vanna ($ dealer delta per vol point) recomputed at each bucket's spot, same quotes as the gamma columns{(vexAt != null ? $" (narrowed to the bucket nearest --time {vexAt:hh\\:mm})" : "")}. No gravity marker — vanna maps a hedging flow under an IV move, not a level. Full multi-expiry VEX: `analyze gex {Markup.Escape(ticker)}` without --intraday.[/]");
		if (skipped.Count > 0 && liveChain)
			AnsiConsole.MarkupLine($"[dim]{skipped.Count} bucket(s) still ahead ({string.Join(", ", skipped.Select(s => s.ToString(@"hh\:mm")))}) — the session tape ends at {intradaySpots.Keys.Last():hh\\:mm} ET; re-run to extend the columns as the day unfolds.[/]");
		else if (skipped.Count > 0)
			AnsiConsole.MarkupLine($"[yellow]Dropped {skipped.Count} bucket(s) with no spot/quote within {tolerance.TotalMinutes:F0} min: {string.Join(", ", skipped.Select(s => s.ToString(@"hh\:mm")))} — the spot tape ends at {intradaySpots.Keys.Last():hh\\:mm} ET{(date.Date == DateTime.Today ? $". Refresh today's tape with `wa ai history {ticker} --partial`" : "")}.[/]");
	}

	/// <summary>Reads the day's minute NBBO from <c>data/quotes/<TICKER>/<date>.csv</c> (the per-expiry store the
	/// wa-scraper and the ThetaData evening sync both write) into one quote-set per RTH minute: every 0DTE contract that
	/// had a two-sided book that minute, carrying the snapshot's OI but THAT minute's bid/ask with the IV nulled — so
	/// <see cref="GexMatrix.Build"/> back-solves each bucket's IVs from the time-matched mids instead of reusing the
	/// morning snapshot's frozen values. Store rows are end-of-bar labeled (row T = the book at instant T+1, the
	/// convention validated 2026-06-11), so labels are shifted +1 minute here to mean wall-clock instants. One-sided or
	/// empty books (bid/ask 0.0) are skipped; contracts absent from the snapshot have no OI and are skipped too.</summary>
	private static SortedDictionary<TimeSpan, Dictionary<string, OptionContractQuote>> LoadMinuteQuoteSets(string ticker, DateTime date, Dictionary<string, OptionContractQuote> snapshot)
	{
		var result = new SortedDictionary<TimeSpan, Dictionary<string, OptionContractQuote>>();
		var path = Program.ResolvePath($"data/quotes/{ticker}/{date:yyyy-MM-dd}.csv");
		if (!File.Exists(path)) return result;

		var bySide = new Dictionary<(decimal Strike, string Right), OptionContractQuote>();
		foreach (var (sym, q) in snapshot)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || p.ExpiryDate.Date != date.Date || string.IsNullOrEmpty(p.CallPut)) continue;
			bySide[(p.Strike, p.CallPut)] = q;
		}
		if (bySide.Count == 0) return result;

		var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var first = true;
		foreach (var line in File.ReadLines(path))
		{
			if (first) { first = false; continue; } // header: date,time,strike,right,bid,ask,bid_size,ask_size
			if (!line.StartsWith(dateStr, StringComparison.Ordinal)) continue;
			var parts = line.Split(',');
			if (parts.Length < 6) continue;
			if (!TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out var tod)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var bid) || !decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var ask)) continue;
			if (bid <= 0m || ask <= 0m) continue;
			if (!bySide.TryGetValue((strike, parts[3]), out var snap)) continue;
			var instant = tod + TimeSpan.FromMinutes(1);
			if (!result.TryGetValue(instant, out var set)) { set = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase); result[instant] = set; }
			set[snap.ContractSymbol] = snap with { Bid = bid, Ask = ask, ImpliedVolatility = null };
		}
		return result;
	}

	/// <summary>Reads the live `analyze gex` log at <c>data/gex/<TICKER>/<date>.jsonl</c> and returns
	/// ET time-of-day → the gravity strike that run displayed for the <paramref name="date"/> (0DTE) expiry.
	/// Only records from <paramref name="source"/> are kept (records without a source field predate the
	/// --source option and count as webull). Corrupt lines are skipped — a torn concurrent append must not
	/// take down the heatmap.</summary>
	private static SortedDictionary<TimeSpan, decimal> LoadLiveGravityLog(string ticker, DateTime date, string source)
	{
		var result = new SortedDictionary<TimeSpan, decimal>();
		var path = Program.ResolvePath($"data/gex/{ticker}/{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(path)) return result;
		var expiryStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
					if (e.TryGetProperty("expiry", out var ex) && ex.GetString() == expiryStr && e.TryGetProperty("gravity", out var g) && g.ValueKind == JsonValueKind.Number)
						result[ts.TimeOfDay] = g.GetDecimal();
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
			rows.Add(new GexLogExpiry(exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), gravity, gross, callWall, putWall, matrix.FindGammaFlip(spot, exp), matrix.FindMaxPain(exp)));
		}
		var record = new GexLogRecord(nowEt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), settings.VendorName, spot, settings.StrikeRangePct, settings.MaxStrikes, settings.Dte, rows);
		var path = Program.ResolvePath($"data/gex/{ticker}/{nowEt:yyyy-MM-dd}.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
		AnsiConsole.MarkupLine($"[dim]Logged to data/gex/{ticker}/{nowEt:yyyy-MM-dd}.jsonl (the --intraday heatmap reads this back as its \"Gravity\" row).[/]");
	}

	/// <summary>--dump: appends one CSV row per in-window contract of this LIVE fetch to
	/// <c>data/iv/<TICKER>/<ET date>.csv</c> — the per-strike vendor inputs (bid/ask, vendor IV, OI)
	/// behind the displayed gex values, which are otherwise discarded after the run. Source-tagged so
	/// interleaved webull/schwab dumps land in one day file and join on (time, expiry, strike, right).
	/// Window = the same expiry/strike filters the heatmap uses; null bid/ask/IV/OI dump as empty fields
	/// (a vendor null is itself data). The time column is the actual ET fetch time, not a bar label.</summary>
	private static void AppendIvDump(string ticker, decimal spot, Dictionary<string, OptionContractQuote> quotes, AnalyzeGexSettings settings, DateTime asOf, DateTime? expiryFilter)
	{
		var nowEt = TimeZoneInfo.ConvertTime(DateTime.Now, NyTz);
		var source = settings.VendorName;
		var band = settings.StrikeRangePct / 100m;
		var sb = new System.Text.StringBuilder();
		var rows = 0;
		foreach (var kv in quotes.OrderBy(k => k.Key, StringComparer.Ordinal))
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(kv.Key);
			if (parsed == null || !string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			var exp = parsed.ExpiryDate.Date;
			if (expiryFilter.HasValue ? exp != expiryFilter.Value.Date : exp < asOf.Date || exp > asOf.Date.AddDays(settings.Dte)) continue;
			if (Math.Abs(parsed.Strike - spot) / spot > band) continue;
			var q = kv.Value;
			string D(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
			sb.Append(nowEt.ToString("yyyy-MM-dd,HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(source).Append(',')
				.Append(exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
				.Append(parsed.Strike.ToString(CultureInfo.InvariantCulture)).Append(',').Append(parsed.CallPut).Append(',')
				.Append(D(q.Bid)).Append(',').Append(D(q.Ask)).Append(',').Append(D(q.ImpliedVolatility)).Append(',')
				.Append(q.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
				.Append(spot.ToString(CultureInfo.InvariantCulture)).Append('\n');
			rows++;
		}
		var path = Program.ResolvePath($"data/iv/{ticker}/{nowEt:yyyy-MM-dd}.csv");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		if (!File.Exists(path)) File.WriteAllText(path, "date,time,source,expiry,strike,right,bid,ask,iv,oi,spot\n");
		File.AppendAllText(path, sb.ToString());
		AnsiConsole.MarkupLine($"[dim]Dumped {rows} {source} contract row(s) to data/iv/{ticker}/{nowEt:yyyy-MM-dd}.csv.[/]");
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
	public decimal MaxGross { get; }
	public decimal MaxAbsNet { get; }
	public decimal TotalCallGex { get; }
	public decimal TotalPutGex { get; }
	public Dictionary<DateTime, decimal?> GravityByExpiry { get; }
	public IReadOnlyList<GexContributor> Contributors { get; }

	private GexMatrix(List<DateTime> expiries, List<decimal> strikes, Dictionary<(DateTime, decimal), GexCell> cells, decimal maxGross, decimal maxAbsNet, decimal totalCallGex, decimal totalPutGex, Dictionary<DateTime, decimal?> gravityByExpiry, IReadOnlyList<GexContributor> contributors)
	{
		Expiries = expiries;
		Strikes = strikes;
		Cells = cells;
		MaxGross = maxGross;
		MaxAbsNet = maxAbsNet;
		TotalCallGex = totalCallGex;
		TotalPutGex = totalPutGex;
		GravityByExpiry = gravityByExpiry;
		Contributors = contributors;
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
	/// and PutGex within that expiry's row of the heatmap. Walls are sourced from the displayed cell set
	/// (so they reflect the same window as the per-expiry gravity marker), not from the wider analytic set.
	/// </summary>
	public (decimal? CallWall, decimal? PutWall) FindWalls(DateTime expiry)
	{
		decimal? bestCall = null, bestPut = null;
		decimal bestCallGex = 0m, bestPutGex = 0m;
		foreach (var ((exp, strike), cell) in Cells)
		{
			if (exp != expiry.Date) continue;
			if (cell.CallGex > bestCallGex) { bestCallGex = cell.CallGex; bestCall = strike; }
			if (cell.PutGex > bestPutGex) { bestPutGex = cell.PutGex; bestPut = strike; }
		}
		return (bestCall, bestPut);
	}

	/// <summary>
	/// Returns the max-pain strike for <paramref name="expiry"/>: the listed strike that minimizes the
	/// total dollar value of contracts expiring in-the-money (Σ max(S−K,0)·OI for calls + Σ max(K−S,0)·OI
	/// for puts). This is the "pin" level where holders collectively lose the most. Evaluated only at
	/// strikes actually present in the contributor set for that expiry — out-of-window strikes are ignored,
	/// so for narrow --strike-range values the result may miss a true max-pain that sits beyond the window.
	/// </summary>
	public decimal? FindMaxPain(DateTime expiry)
	{
		var perExpiry = Contributors.Where(c => c.Expiry == expiry.Date).ToList();
		if (perExpiry.Count == 0) return null;

		var candidateStrikes = perExpiry.Select(c => c.Strike).Distinct().OrderBy(s => s).ToList();
		decimal? bestStrike = null;
		decimal bestPayout = decimal.MaxValue;
		foreach (var s in candidateStrikes)
		{
			decimal payout = 0m;
			foreach (var c in perExpiry)
			{
				var itm = c.IsCall ? Math.Max(s - c.Strike, 0m) : Math.Max(c.Strike - s, 0m);
				payout += itm * c.Oi;
			}
			if (payout < bestPayout)
			{
				bestPayout = payout;
				bestStrike = s;
			}
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
		var expirySet = new HashSet<DateTime>();
		var strikeSet = new HashSet<decimal>();
		var rawContribs = new List<(DateTime Expiry, decimal Strike, double TimeYears, decimal Iv, long Oi, bool IsCall)>();

		foreach (var kv in quotes)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(kv.Key);
			if (parsed == null || !string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (expiryFilter.HasValue && parsed.ExpiryDate.Date != expiryFilter.Value.Date) continue;
			if (parsed.ExpiryDate.Date < asOfDate) continue;
			if (parsed.Strike < minStrike || parsed.Strike > maxStrike) continue;
			var q = kv.Value;
			if (!q.OpenInterest.HasValue || q.OpenInterest.Value <= 0) continue;

			var timeYears = Math.Max(1, (parsed.ExpiryDate.Date - asOfDate).Days) / 365.0;

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

			var isCall = parsed.CallPut == "C";
			var key = (parsed.ExpiryDate.Date, parsed.Strike);
			raw.TryGetValue(key, out var existing);
			if (isCall)
				raw[key] = (existing.CallGex + dollarGex, existing.PutGex);
			else
				raw[key] = (existing.CallGex, existing.PutGex + dollarGex);
			rawContribs.Add((parsed.ExpiryDate.Date, parsed.Strike, timeYears, iv, q.OpenInterest.Value, isCall));
			expirySet.Add(parsed.ExpiryDate.Date);
			strikeSet.Add(parsed.Strike);
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
		var grossByExpiry = new Dictionary<DateTime, Dictionary<decimal, decimal>>();
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
			if (!grossByExpiry.TryGetValue(exp, out var g)) { g = new(); grossByExpiry[exp] = g; }
			g[strike] = cell.Gross;
		}

		var gravity = new Dictionary<DateTime, decimal?>();
		foreach (var exp in expiries)
		{
			if (grossByExpiry.TryGetValue(exp, out var g) && g.Count > 0)
				gravity[exp] = g.OrderByDescending(kv => kv.Value).First().Key;
			else
				gravity[exp] = null;
		}

		// Analytics use the full strike-range × kept-expiries set, NOT the --max-strikes display cap —
		// per-expiry max pain and gamma flip would be skewed by an arbitrary display-row limit.
		var contributors = rawContribs
			.Where(r => keptExpirySet.Contains(r.Expiry))
			.Select(r => new GexContributor(r.Expiry, r.Strike, r.TimeYears, r.Iv, r.Oi, r.IsCall))
			.ToList();

		return new GexMatrix(expiries, strikes, cells, maxGross, maxAbsNet, totalCall, totalPut, gravity, contributors);
	}
}
