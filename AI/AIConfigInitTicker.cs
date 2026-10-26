using System.Globalization;
using System.Text.Json.Nodes;
using Spectre.Console;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>Ticker mode of <c>wa ai config init &lt;TICKER&gt;</c>: scaffolds a minimal per-ticker override
/// (<c>ai-config.&lt;TICKER&gt;.json</c>) plus a starter strategy layer (<c>ai-config.&lt;TICKER&gt;.&lt;STRATEGY&gt;.json</c>).
/// <c>strikeStep</c> and <c>ivDefaultPct</c> are derived from the live chain when it is reachable, else
/// placeholdered with a loud warning; the strategy layer enables the <c>--structures</c> chosen, with values
/// derived from the same chain (see <see cref="AIConfigInitStructures"/>). Both files are always written together: the loader mandates a
/// strategy layer (see <see cref="AICommand.ResolveLayers"/>), so a lone per-ticker file could not be
/// scanned.</summary>
internal static class AIConfigInitTicker
{
	/// <summary>Days the bootstrap chain fetch reaches past the furthest structure horizon, so a window with no
	/// listed expiry (a monthly-only name) can still be widened to the next one.</summary>
	private const int BootstrapExtraDays = 45;

	/// <summary>DTE whose ATM IV seeds ivDefaultPct — a month out, so a front-week event (earnings, an
	/// election) does not set the ticker's default vol.</summary>
	private const int IvReferenceDte = 30;

	public static async Task<int> RunAsync(AIConfigInitSettings settings, CancellationToken cancellation)
	{
		var ticker = settings.Ticker!.Trim().ToUpperInvariant();
		var strategy = settings.Strategy.Trim();
		if (string.IsNullOrWhiteSpace(strategy)) { Console.Error.WriteLine("Error: --strategy token cannot be empty."); return 1; }

		var baseRel = AIConfigLoader.ConfigPath;                 // data/ai-config.json
		var dir = string.IsNullOrWhiteSpace(settings.Out) ? (Path.GetDirectoryName(baseRel) ?? string.Empty) : settings.Out!;
		var stem = Path.GetFileNameWithoutExtension(baseRel);    // ai-config
		var ext = Path.GetExtension(baseRel);                    // .json

		var tickerPath = Program.ResolvePath(Path.Combine(dir, $"{stem}.{ticker}{ext}"));
		var stratPath = Program.ResolvePath(Path.Combine(dir, $"{stem}.{ticker}.{strategy}{ext}"));

		if (!settings.Force)
		{
			foreach (var p in new[] { tickerPath, stratPath })
				if (File.Exists(p)) { Console.Error.WriteLine($"Error: {p} already exists. Pass --force to overwrite."); return 1; }
		}

		var (enabled, error) = AIConfigInitStructures.Parse(settings.Structures);
		if (error != null) { Console.Error.WriteLine($"Error: {error}"); return 1; }

		var today = DateTime.Today;
		var chain = await FetchChainAsync(settings, ticker, today.AddDays(AIConfigInitStructures.MaxDte(enabled) + BootstrapExtraDays), cancellation);
		var strikeStep = chain?.StrikeStep ?? 1.0m;
		var ivPct = chain is { Spot: { } spot } ? ReferenceIv(chain, spot, today) : null;
		var (structures, notes) = AIConfigInitStructures.Build(enabled, chain, today);

		Directory.CreateDirectory(Path.GetDirectoryName(tickerPath)!);
		File.WriteAllText(tickerPath, ConfigJsonWriter.Serialize(BuildTickerConfig(strategy, strikeStep, ivPct)));
		File.WriteAllText(stratPath, ConfigJsonWriter.Serialize(BuildStrategyConfig(structures)));

		PrintSummary(ticker, strategy, tickerPath, stratPath, chain, ivPct, notes);
		return 0;
	}

	/// <summary>Bootstrap-fetches the live chain with two placeholder OCC symbols spanning today ..
	/// <paramref name="through"/> (Webull returns the full chain for any leg of the root; Schwab fetches exactly
	/// the expiry window the placeholders span, so a lone placeholder on a non-expiry date — e.g. a weekend —
	/// returned nothing). strikeStep is the front expiry's smallest strike gap. Any failure (no session,
	/// offline, empty chain) returns null so init still succeeds on placeholders.</summary>
	private static async Task<InitChain?> FetchChainAsync(AIConfigInitSettings settings, string ticker, DateTime through, CancellationToken cancellation)
	{
		try
		{
			var vendor = LiveQuoteSource.ResolveVendor(settings.Vendor);
			AnsiConsole.MarkupLine($"[dim]Deriving strikeStep/IV/structures from live chain via {LiveQuoteSource.VendorName(vendor)}…[/]");
			var quotes = AIContext.BuildLiveQuoteSource(vendor);
			var now = DateTime.UtcNow;                          // logical only; the live source ignores it
			var placeholder = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MatchKeys.OccSymbol(ticker, now.Date, 1m, "C"), MatchKeys.OccSymbol(ticker, through, 1m, "C") };
			var tickerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker };
			var snap = await quotes.GetQuotesAsync(now, placeholder, tickerSet, cancellation);

			var contracts = snap.Options
				.Select(kv => (parsed: ParsingHelpers.ParseOptionSymbol(kv.Key), q: kv.Value))
				.Where(x => x.parsed is { } p && (string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase) || (ParsingHelpers.IsIndexMonthlyFragmentedRoot(p.Root) && ParsingHelpers.IsIndexMonthlyFragmentedRoot(ticker))))
				.Select(x => (x.parsed!, x.q))
				.ToList();
			if (contracts.Count == 0)
			{
				AnsiConsole.MarkupLine($"[yellow]Warning:[/] the chain fetch returned no {ticker} contracts; using placeholder strikeStep=1.0 and fallback structure widths.");
				return null;
			}

			decimal? spot = snap.Underlyings.TryGetValue(ticker, out var s) && s > 0 ? s : null;
			var frontExpiry = contracts.Select(c => c.Item1.ExpiryDate).Min();
			var frontStrikes = contracts.Where(c => c.Item1.ExpiryDate == frontExpiry).Select(c => c.Item1.Strike).Distinct().OrderBy(x => x).ToList();
			return new InitChain(spot, MinGap(frontStrikes), contracts);
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] live chain fetch failed ({Markup.Escape(ex.Message)}); using placeholder strikeStep=1.0 and fallback structure widths.");
			return null;
		}
	}

	/// <summary>Smallest positive gap between consecutive sorted strikes, i.e. the listed strike increment.
	/// Falls back to 1.0 when fewer than two strikes are present.</summary>
	private static decimal MinGap(List<decimal> sortedStrikes)
	{
		var min = 0m;
		for (var i = 1; i < sortedStrikes.Count; i++)
		{
			var gap = sortedStrikes[i] - sortedStrikes[i - 1];
			if (gap > 0 && (min == 0m || gap < min)) min = gap;
		}
		return min > 0m ? min : 1.0m;
	}

	/// <summary>ivDefaultPct: ATM IV (0-1 fraction) on the listed expiry nearest <see cref="IvReferenceDte"/>
	/// that has one. Null when no expiry does.</summary>
	private static decimal? ReferenceIv(InitChain chain, decimal spot, DateTime today) => chain.Expiries
		.OrderBy(e => Math.Abs((e - today).Days - IvReferenceDte))
		.Select(e => AIConfigInitStructures.AtmIv(chain, e, spot))
		.FirstOrDefault(iv => iv != null);

	/// <summary>Minimal per-ticker override, matching the shape of the hand-written single-name configs
	/// (defaultStrategy + the required strikeStep + sizing/liquidity). Everything else inherits the base.</summary>
	private static JsonNode BuildTickerConfig(string strategy, decimal strikeStep, decimal? ivPct)
	{
		var step = strikeStep.ToString("0.####", CultureInfo.InvariantCulture);
		var iv = ivPct.HasValue ? ivPct.Value.ToString("0.####", CultureInfo.InvariantCulture) : "0.40";   // neutral single-name fallback when IV wasn't derived
		var json = $$"""
		{
			"defaultStrategy": "{{strategy}}",
			"cashReserve": { "mode": "percent", "value": 0 },
			"log-level": "information",
			"indicators": {
				"strikeStep": {{step}},
				"ivDefaultPct": {{iv}}
			},
			"execution": { "roundTrips": 1 },
			"opener": {
				"enabled": true,
				"topNPerTicker": 5,
				"maxCandidatesPerStructurePerTicker": 12,
				"maxQtyPerProposal": 10,
				"maxRiskPctPerProposal": 0.05,
				"maxDollarRiskPerProposal": 5000,
				"minScoreToOpen": 0.0,
				"liquidity": { "minOpenInterest": 0, "minRelativeOpenInterest": 0, "minAbsoluteOpenInterest": 0, "weight": 0 },
				"realizedEvScoring": true
			}
		}
		""";
		return JsonNode.Parse(json)!;
	}

	/// <summary>Starter strategy layer around the derived <paramref name="structures"/> block, so `wa ai scan`
	/// produces proposals immediately. Auto-execute stays off — this is a look-only scaffold to edit.</summary>
	private static JsonNode BuildStrategyConfig(JsonObject structures)
	{
		var json = """
		{
			"rules": {
				"stopLoss": { "enabled": true, "pctOfMaxLoss": 0.75 },
				"takeProfit": { "enabled": true, "profitTargetPctOfPremium": 0.5 },
				"closeBeforeShortExpiry": { "enabled": true, "minProfitPct": 0.25, "emergencyBreakEvenBufferPct": 0.01 }
			},
			"execution": { "slippagePerSharePerOrder": 0.02, "roundTrips": 2 },
			"opener": {
				"weights": {
					"directionalFit": 0.10,
					"biasDrift": 0.50,
					"whipsaw": 3.0,
					"statArb": 0.15,
					"sentiment": 0,
					"expectedMoveCredit": 0.60,
					"vixTermStructure": 0,
					"intradayTape": 0.30
				},
				"structures": {},
				"balanceRrExponent": 0.0
			},
			"autoExecute": {
				"management": { "enabled": false, "submit": false, "timeInForce": "GTC", "rules": ["StopLossRule", "TakeProfitRule", "CloseBeforeShortExpiryRule"] },
				"opener": { "enabled": false, "submit": false, "timeInForce": "GTC", "maxOrdersPerDay": 1, "structures": [] }
			}
		}
		""";
		var root = JsonNode.Parse(json)!;
		root["opener"]!["structures"] = structures;
		return root;
	}

	private static void PrintSummary(string ticker, string strategy, string tickerPath, string stratPath, InitChain? chain, decimal? ivPct, List<string> notes)
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[green]Scaffolded[/] {ticker} config ([bold]{strategy}[/] strategy):");
		AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(tickerPath)}[/]");
		AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(stratPath)}[/]");
		AnsiConsole.WriteLine();
		if (chain != null)
		{
			var spotStr = chain.Spot is { } sp ? $"${sp:N2}" : "n/a";
			var ivStr = ivPct is { } iv ? $"{iv.ToString("0%", CultureInfo.InvariantCulture)} (~{IvReferenceDte} DTE)" : "n/a (fallback 40%)";
			AnsiConsole.MarkupLine($"  spot [bold]{spotStr}[/] · strikeStep [bold]{chain.StrikeStep.ToString("0.####", CultureInfo.InvariantCulture)}[/] · ivDefaultPct [bold]{ivStr}[/] (from live chain)");
		}
		else
		{
			AnsiConsole.MarkupLine($"  [yellow]strikeStep/IV not derived — verify indicators.strikeStep against the live chain before trading.[/]");
		}
		foreach (var note in notes) AnsiConsole.MarkupLine($"  {Markup.Escape(note)}");
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"Next: [bold]wa ai scan {ticker}[/]  (edit the strategy layer to tune structures/weights)");
	}
}
