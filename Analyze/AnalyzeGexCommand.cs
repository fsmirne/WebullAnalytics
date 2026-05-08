using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// `wa analyze gex &lt;TICKER&gt;` — Renders a 2D GEX heatmap over the option chain
/// (strikes × expirations), a per-expiration summary (gravity / gamma flip / max pain), a chain
/// totals panel, and call/put walls. Pulls the chain from Webull directly (api-config.json must
/// already be sniffed). Yahoo isn't supported because chain-level analytics need full OI + IV
/// across every strike/expiry, which Webull's strategy/list payload returns in one shot.
/// </summary>
internal sealed class AnalyzeGexSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "<ticker>")]
	[Description("Underlying ticker symbol (e.g., GME, SPY).")]
	public string Ticker { get; set; } = "";

	[CommandOption("--expiry <DATE>")]
	[Description("Restrict to a single expiration date (YYYY-MM-DD). Default: show all expirations in the chain.")]
	public string? Expiry { get; set; }

	[CommandOption("--strike-range <PCT>")]
	[DefaultValue(20)]
	[Description("Strike window as ± percent of spot. Default: 20.")]
	public int StrikeRangePct { get; set; } = 20;

	[CommandOption("--max-strikes <N>")]
	[DefaultValue(25)]
	[Description("Max strike rows to display. Picks the N strikes closest to spot within --strike-range. Default: 25.")]
	public int MaxStrikes { get; set; } = 25;

	[CommandOption("--max-expiries <N>")]
	[DefaultValue(12)]
	[Description("Max expirations to display when --expiry is not set. Default: 12.")]
	public int MaxExpiries { get; set; } = 12;

	[CommandOption("--top-walls <N>")]
	[DefaultValue(5)]
	[Description("Number of top call/put walls to list. Default: 5.")]
	public int TopWalls { get; set; } = 5;

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;
		if (string.IsNullOrWhiteSpace(Ticker)) return ValidationResult.Error("<ticker> is required");
		if (Expiry != null && !DateTime.TryParseExact(Expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--expiry: expected YYYY-MM-DD, got '{Expiry}'");
		if (StrikeRangePct <= 0 || StrikeRangePct > 200) return ValidationResult.Error($"--strike-range: must be in (0, 200], got {StrikeRangePct}");
		if (MaxStrikes < 1 || MaxStrikes > 200) return ValidationResult.Error($"--max-strikes: must be in [1, 200], got {MaxStrikes}");
		if (MaxExpiries < 1 || MaxExpiries > 50) return ValidationResult.Error($"--max-expiries: must be in [1, 50], got {MaxExpiries}");
		if (TopWalls < 1 || TopWalls > 25) return ValidationResult.Error($"--top-walls: must be in [1, 25], got {TopWalls}");
		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeGexCommand : AsyncCommand<AnalyzeGexSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AnalyzeGexSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		TerminalHelper.EnsureTerminalWidthFromConfig();

		var configPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(configPath))
		{
			AnsiConsole.MarkupLine("[red]Error: api-config.json not found. Run 'sniff' first.[/]");
			return 1;
		}
		var apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
		if (apiConfig == null || apiConfig.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("[red]Error: api-config.json is empty or missing headers. Run 'sniff' first.[/]");
			return 1;
		}

		var ticker = settings.Ticker.ToUpperInvariant();
		var (initialQuotes, fetchedSpot, derivativeIds) = await WebullOptionsClient.FetchChainAsync(apiConfig, ticker, cancellation);
		if (initialQuotes.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]No option chain data returned for {ticker}.[/]");
			return 1;
		}

		var spot = ResolveSpotOverride(settings.Spot, ticker) ?? fetchedSpot;
		if (!spot.HasValue || spot.Value <= 0m)
		{
			AnsiConsole.MarkupLine($"[red]No spot price available for {ticker}. Pass --spot {ticker}:PRICE to override.[/]");
			return 1;
		}

		var asOf = settings.EvaluationDateOverride ?? DateTime.Now;
		DateTime? expiryFilter = settings.Expiry != null
			? DateTime.ParseExact(settings.Expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture)
			: null;

		// Webull's strategy/list only inlines OI/IV for the front-most expiration. To populate the heatmap
		// we re-pull contracts within the strike window and (when --expiry isn't set) the next maxExpiries
		// expirations through queryBatch — that endpoint returns OI/IV for any derivativeId we ask for.
		var quotes = new Dictionary<string, OptionContractQuote>(initialQuotes, StringComparer.OrdinalIgnoreCase);
		var refreshed = await RefreshInWindowContractsAsync(apiConfig, quotes, derivativeIds, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.MaxExpiries, settings.MaxStrikes, cancellation);
		if (refreshed > 0) AnsiConsole.MarkupLine($"[dim]Refreshed {refreshed} in-window contract(s) via queryBatch.[/]");

		var matrix = GexMatrix.Build(quotes, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.MaxExpiries, settings.MaxStrikes);
		if (matrix.Strikes.Count == 0 || matrix.Expiries.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No strikes match within ±{settings.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]");
			return 1;
		}

		RenderHeader(ticker, spot.Value, asOf, expiryFilter, matrix);
		AnsiConsole.WriteLine();
		RenderHeatmap(matrix, spot.Value);
		AnsiConsole.WriteLine();
		RenderPerExpirySummary(matrix, spot.Value, asOf);
		AnsiConsole.WriteLine();
		RenderTotals(matrix, spot.Value);
		AnsiConsole.WriteLine();
		RenderWalls(matrix, settings.TopWalls);
		return 0;
	}

	/// <summary>Identifies chain symbols within the heatmap window (strike range × selected expiries) that
	/// came back from strategy/list with missing OI or IV, then refreshes them via Webull's queryBatch.
	/// Pre-filters expiries to <paramref name="maxExpiries"/> when no explicit --expiry is set so we don't
	/// waste batches on far-dated stub contracts the user won't see.</summary>
	private static async Task<int> RefreshInWindowContractsAsync(
		ApiConfig apiConfig,
		IDictionary<string, OptionContractQuote> chain,
		IReadOnlyDictionary<string, long> derivativeIds,
		string ticker,
		decimal spot,
		DateTime asOf,
		DateTime? expiryFilter,
		decimal strikeRangeFraction,
		int maxExpiries,
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
			: inScopeExpiries.OrderBy(d => d).Take(maxExpiries).ToHashSet();

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
		AnsiConsole.MarkupLine($"[dim]Refreshing {symbolsToRefresh.Count} non-front-month contract(s) via queryBatch...[/]");
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
	private static void RenderHeatmap(GexMatrix matrix, decimal spot)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn(new TableColumn("[bold]Strike[/]").RightAligned().NoWrap());
		foreach (var exp in matrix.Expiries)
			table.AddColumn(new TableColumn($"[bold]{exp:M/d}[/]").Centered().NoWrap());

		// Strike closest to spot — gets the bold yellow ATM marker.
		var atmStrike = matrix.Strikes.OrderBy(s => Math.Abs(s - spot)).FirstOrDefault();
		var maxAbsNet = Math.Max(matrix.MaxAbsNet, 1m);

		foreach (var strike in matrix.Strikes)
		{
			var strikeStr = $"${strike:N2}";
			var isAtm = strike == atmStrike;
			var strikeMarkup = isAtm ? $"[bold yellow]{strikeStr}[/]" : strikeStr;

			var cells = new List<string> { strikeMarkup };
			foreach (var exp in matrix.Expiries)
			{
				matrix.Cells.TryGetValue((exp, strike), out var cell);
				var isGravity = matrix.GravityByExpiry.TryGetValue(exp, out var grav) && grav.HasValue && grav.Value == strike;
				cells.Add(BuildHeatmapCellMarkup(cell, maxAbsNet, isGravity));
			}
			table.AddRow(cells.ToArray());
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Cell = net GEX ($call gamma×OI − $put gamma×OI). [green]Green[/] = call-dominated, [red]red[/] = put-dominated, brightness ∝ |net|. Bold + underlined cell (e.g. [bold underline]+1.2M[/]) = per-expiry gravity strike (max gross gamma).[/]");
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

	private static void RenderTotals(GexMatrix matrix, decimal spot)
	{
		var totalAbs = matrix.TotalCallGex + matrix.TotalPutGex;
		var net = matrix.TotalCallGex - matrix.TotalPutGex;
		var netFrac = totalAbs > 0m ? net / totalAbs : 0m;
		var netSign = net >= 0m ? "+" : "−";
		var netColor = net >= 0m ? "green" : "red";

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Chain totals[/]");
		table.AddColumn("Metric");
		table.AddColumn(new TableColumn("Value").RightAligned().NoWrap());
		table.AddRow("Total call GEX", $"[green]+{FormatCompactDollars(matrix.TotalCallGex)}[/]");
		table.AddRow("Total put GEX",  $"[red]−{FormatCompactDollars(matrix.TotalPutGex)}[/]");
		table.AddRow("Total absolute (gross)", FormatCompactDollars(totalAbs));
		table.AddRow("Net (call − put)", $"[bold {netColor}]{netSign}{FormatCompactDollars(Math.Abs(net))}[/]");
		table.AddRow("Net fraction", $"[bold {netColor}]{netFrac:+0.00;-0.00}[/]  [dim](+1 = pure call, −1 = pure put)[/]");
		table.AddRow("Gamma flip", FormatGammaFlipDisplay(matrix.FindGammaFlip(spot), spot));
		AnsiConsole.Write(table);
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
			table.AddRow($"{exp:yyyy-MM-dd}", dte.ToString(), gravityCell, FormatWallStrike(callWall, "green"), FormatWallStrike(putWall, "red"), FormatPriceVsSpotCompact(flip, spot, regimeColor: true), FormatPriceVsSpotCompact(maxPain, spot, regimeColor: false));
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Gravity = strike with max gross gamma. [green]Call wall[/] / [red]put wall[/] = strike with the largest call / put GEX for that expiry (resistance / support). Gamma flip = where dealer net dollar-gamma crosses 0 ([green]green[/] = spot in positive-γ regime, [red]red[/] = negative-γ). Max pain = strike minimizing total ITM payout (where most contracts expire worthless).[/]");
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

/// <summary>One cell of the GEX matrix at a given (expiry, strike). CallGex/PutGex are dollar-gamma
/// exposures (gamma × OI × 100 × spot), always non-negative. Net = call − put (signed).</summary>
internal sealed record GexCell(decimal CallGex, decimal PutGex)
{
	public decimal Gross => CallGex + PutGex;
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
	/// Builds the (expiry × strike) GEX matrix from a raw chain. Per-cell exposure is split between
	/// CallGex and PutGex, each computed as Black-Scholes gamma × OI × 100 × spot. Filters strikes to
	/// ±strikeRangeFraction of spot and (when expiryFilter is null) limits to the next maxExpiries
	/// expirations sorted ascending. When expiryFilter is set, all other expirations are dropped.
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
		int maxExpiries,
		int maxStrikes)
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
			if (!q.ImpliedVolatility.HasValue || q.ImpliedVolatility.Value <= 0m) continue;

			var timeYears = Math.Max(1, (parsed.ExpiryDate.Date - asOfDate).Days) / 365.0;
			var gamma = (decimal)OptionMath.Gamma(spot, parsed.Strike, timeYears, OptionMath.RiskFreeRate, q.ImpliedVolatility.Value);
			var dollarGex = gamma * q.OpenInterest.Value * 100m * spot;
			if (dollarGex <= 0m) continue;

			var isCall = parsed.CallPut == "C";
			var key = (parsed.ExpiryDate.Date, parsed.Strike);
			raw.TryGetValue(key, out var existing);
			if (isCall)
				raw[key] = (existing.CallGex + dollarGex, existing.PutGex);
			else
				raw[key] = (existing.CallGex, existing.PutGex + dollarGex);
			rawContribs.Add((parsed.ExpiryDate.Date, parsed.Strike, timeYears, q.ImpliedVolatility.Value, q.OpenInterest.Value, isCall));
			expirySet.Add(parsed.ExpiryDate.Date);
			strikeSet.Add(parsed.Strike);
		}

		var expiries = expirySet.OrderBy(d => d).ToList();
		if (!expiryFilter.HasValue && expiries.Count > maxExpiries)
			expiries = expiries.Take(maxExpiries).ToList();
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
