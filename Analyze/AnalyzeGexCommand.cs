using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Analyze;

/// <summary>
/// `wa analyze gex &lt;TICKER&gt;` — Renders a 2D GEX heatmap over the option chain
/// (strikes × expirations) plus a totals panel and call/put walls. Pulls the chain from Webull
/// directly (api-config.json must already be sniffed). Yahoo isn't supported because chain-level
/// analytics need full OI + IV across every strike/expiry, which Webull's strategy/list payload
/// returns in one shot.
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
		var refreshed = await RefreshInWindowContractsAsync(apiConfig, quotes, derivativeIds, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.MaxExpiries, cancellation);
		if (refreshed > 0) AnsiConsole.MarkupLine($"[dim]Refreshed {refreshed} in-window contract(s) via queryBatch.[/]");

		var matrix = GexMatrix.Build(quotes, ticker, spot.Value, asOf, expiryFilter, settings.StrikeRangePct / 100m, settings.MaxExpiries);
		if (matrix.Strikes.Count == 0 || matrix.Expiries.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No strikes match within ±{settings.StrikeRangePct}% of spot ${spot:F2} for the selected expirations.[/]");
			return 1;
		}

		RenderHeader(ticker, spot.Value, asOf, expiryFilter, matrix);
		AnsiConsole.WriteLine();
		RenderHeatmap(matrix, spot.Value);
		AnsiConsole.WriteLine();
		RenderTotals(matrix);
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
		CancellationToken cancellation)
	{
		var minStrike = spot * (1m - strikeRangeFraction);
		var maxStrike = spot * (1m + strikeRangeFraction);
		var asOfDate = asOf.Date;

		// First pass: find which expiries we'll actually keep, so we only refresh contracts in those buckets.
		var inScopeExpiries = new HashSet<DateTime>();
		foreach (var sym in chain.Keys)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (p.ExpiryDate.Date < asOfDate) continue;
			if (expiryFilter.HasValue && p.ExpiryDate.Date != expiryFilter.Value.Date) continue;
			inScopeExpiries.Add(p.ExpiryDate.Date);
		}
		var keptExpiries = expiryFilter.HasValue
			? inScopeExpiries
			: inScopeExpiries.OrderBy(d => d).Take(maxExpiries).ToHashSet();

		var symbolsToRefresh = new List<string>();
		foreach (var (sym, q) in chain)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (!keptExpiries.Contains(p.ExpiryDate.Date)) continue;
			if (p.Strike < minStrike || p.Strike > maxStrike) continue;
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

	private static void RenderTotals(GexMatrix matrix)
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
		AnsiConsole.Write(table);
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

	private GexMatrix(List<DateTime> expiries, List<decimal> strikes, Dictionary<(DateTime, decimal), GexCell> cells, decimal maxGross, decimal maxAbsNet, decimal totalCallGex, decimal totalPutGex, Dictionary<DateTime, decimal?> gravityByExpiry)
	{
		Expiries = expiries;
		Strikes = strikes;
		Cells = cells;
		MaxGross = maxGross;
		MaxAbsNet = maxAbsNet;
		TotalCallGex = totalCallGex;
		TotalPutGex = totalPutGex;
		GravityByExpiry = gravityByExpiry;
	}

	/// <summary>
	/// Builds the (expiry × strike) GEX matrix from a raw chain. Per-cell exposure is split between
	/// CallGex and PutGex, each computed as Black-Scholes gamma × OI × 100 × spot. Filters strikes to
	/// ±strikeRangeFraction of spot and (when expiryFilter is null) limits to the next maxExpiries
	/// expirations sorted ascending. When expiryFilter is set, all other expirations are dropped.
	/// </summary>
	public static GexMatrix Build(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		string ticker,
		decimal spot,
		DateTime asOf,
		DateTime? expiryFilter,
		decimal strikeRangeFraction,
		int maxExpiries)
	{
		var minStrike = spot * (1m - strikeRangeFraction);
		var maxStrike = spot * (1m + strikeRangeFraction);
		var asOfDate = asOf.Date;

		var raw = new Dictionary<(DateTime, decimal), (decimal CallGex, decimal PutGex)>();
		var expirySet = new HashSet<DateTime>();
		var strikeSet = new HashSet<decimal>();

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

			var key = (parsed.ExpiryDate.Date, parsed.Strike);
			raw.TryGetValue(key, out var existing);
			if (parsed.CallPut == "C")
				raw[key] = (existing.CallGex + dollarGex, existing.PutGex);
			else
				raw[key] = (existing.CallGex, existing.PutGex + dollarGex);
			expirySet.Add(parsed.ExpiryDate.Date);
			strikeSet.Add(parsed.Strike);
		}

		var expiries = expirySet.OrderBy(d => d).ToList();
		if (!expiryFilter.HasValue && expiries.Count > maxExpiries)
			expiries = expiries.Take(maxExpiries).ToList();
		var keptExpirySet = expiries.ToHashSet();

		// Drop strikes that have no surviving cell after the expiry-window cap.
		var liveStrikes = new HashSet<decimal>();
		foreach (var ((exp, strike), _) in raw)
			if (keptExpirySet.Contains(exp))
				liveStrikes.Add(strike);
		var strikes = liveStrikes.OrderByDescending(s => s).ToList();

		var cells = new Dictionary<(DateTime, decimal), GexCell>();
		decimal maxGross = 0m, maxAbsNet = 0m, totalCall = 0m, totalPut = 0m;
		var grossByExpiry = new Dictionary<DateTime, Dictionary<decimal, decimal>>();
		foreach (var ((exp, strike), v) in raw)
		{
			if (!keptExpirySet.Contains(exp)) continue;
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

		return new GexMatrix(expiries, strikes, cells, maxGross, maxAbsNet, totalCall, totalPut, gravity);
	}
}
