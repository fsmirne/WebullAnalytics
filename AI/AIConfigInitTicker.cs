using System.Globalization;
using System.Text.Json.Nodes;
using Spectre.Console;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>Ticker mode of <c>wa ai config init &lt;TICKER&gt;</c>: scaffolds a minimal per-ticker override
/// (<c>ai-config.&lt;TICKER&gt;.json</c>) plus a starter strategy layer (<c>ai-config.&lt;TICKER&gt;.&lt;STRATEGY&gt;.json</c>).
/// <c>strikeStep</c> and <c>ivDefaultPct</c> are derived from the live chain when it is reachable, else
/// placeholdered with a loud warning. Both files are always written together: the loader mandates a
/// strategy layer (see <see cref="AICommand.ResolveLayers"/>), so a lone per-ticker file could not be
/// scanned.</summary>
internal static class AIConfigInitTicker
{
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

		var (spot, strikeStep, ivPct, live) = await DeriveChainParamsAsync(settings, ticker, cancellation);

		Directory.CreateDirectory(Path.GetDirectoryName(tickerPath)!);
		File.WriteAllText(tickerPath, ConfigJsonWriter.Serialize(BuildTickerConfig(strategy, strikeStep, ivPct)));
		File.WriteAllText(stratPath, ConfigJsonWriter.Serialize(BuildStrategyConfig()));

		PrintSummary(ticker, strategy, tickerPath, stratPath, spot, strikeStep, ivPct, live);
		return 0;
	}

	/// <summary>Bootstrap-fetches the live chain with a single placeholder OCC symbol (Webull returns the
	/// full chain for any leg of the root — the same trick <c>--premarket</c> uses) and reads the front
	/// expiry's smallest strike gap as strikeStep and its ATM implied vol as ivDefaultPct. Any failure
	/// (no session, offline, empty chain) falls back to a placeholder strikeStep so init still succeeds.</summary>
	private static async Task<(decimal? spot, decimal strikeStep, decimal? ivPct, bool live)> DeriveChainParamsAsync(AIConfigInitSettings settings, string ticker, CancellationToken cancellation)
	{
		try
		{
			var vendor = LiveQuoteSource.ResolveVendor(settings.Vendor);
			AnsiConsole.MarkupLine($"[dim]Deriving strikeStep/IV from live chain via {LiveQuoteSource.VendorName(vendor)}…[/]");
			var quotes = AIContext.BuildLiveQuoteSource(vendor);
			var now = DateTime.UtcNow;                          // logical only; the live source ignores it
			var placeholder = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MatchKeys.OccSymbol(ticker, now.Date.AddDays(7), 1m, "C") };
			var tickerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticker };
			var snap = await quotes.GetQuotesAsync(now, placeholder, tickerSet, cancellation);

			var contracts = snap.Options
				.Select(kv => (parsed: ParsingHelpers.ParseOptionSymbol(kv.Key), q: kv.Value))
				.Where(x => x.parsed is { } p && string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase))
				.Select(x => (p: x.parsed!, x.q))
				.ToList();
			if (contracts.Count == 0)
			{
				AnsiConsole.MarkupLine($"[yellow]Warning:[/] the chain fetch returned no {ticker} contracts; using placeholder strikeStep=1.0.");
				return (null, 1.0m, null, false);
			}

			decimal? spot = snap.Underlyings.TryGetValue(ticker, out var s) && s > 0 ? s : null;
			var frontExpiry = contracts.Select(c => c.p.ExpiryDate).Min();
			var frontStrikes = contracts.Where(c => c.p.ExpiryDate == frontExpiry).Select(c => c.p.Strike).Distinct().OrderBy(x => x).ToList();
			return (spot, MinGap(frontStrikes), AtmIvPct(contracts, frontExpiry, spot), true);
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[yellow]Warning:[/] live chain fetch failed ({Markup.Escape(ex.Message)}); using placeholder strikeStep=1.0.");
			return (null, 1.0m, null, false);
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

	/// <summary>ATM implied vol (fraction) at the strike nearest spot on the front expiry, converted to the
	/// whole-percent scale ivDefaultPct expects. Null when spot or a positive IV is unavailable.</summary>
	private static decimal? AtmIvPct(List<(OptionParsed p, OptionContractQuote q)> contracts, DateTime expiry, decimal? spot)
	{
		if (spot is not { } s) return null;
		var iv = contracts
			.Where(c => c.p.ExpiryDate == expiry && c.q.ImpliedVolatility is > 0m)
			.OrderBy(c => Math.Abs(c.p.Strike - s))
			.Select(c => c.q.ImpliedVolatility)
			.FirstOrDefault();
		return iv is { } v ? Math.Round(v * 100m, 0) : null;   // stored as a fraction (0.18); config wants 18
	}

	/// <summary>Minimal per-ticker override, matching the shape of the hand-written single-name configs
	/// (defaultStrategy + the required strikeStep + sizing/liquidity). Everything else inherits the base.</summary>
	private static JsonNode BuildTickerConfig(string strategy, decimal strikeStep, decimal? ivPct)
	{
		var step = strikeStep.ToString("0.####", CultureInfo.InvariantCulture);
		var iv = ivPct.HasValue ? (int)ivPct.Value : 40;        // neutral single-name fallback when IV wasn't derived
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

	/// <summary>Starter strategy layer: defined-risk, range-bound credit structures (iron condor, short
	/// vertical, iron butterfly) enabled so `wa ai scan` produces proposals immediately; calendars,
	/// diagonals and directional longs left off (a young single-name chain rarely has the far-dated
	/// expiries they need). Auto-execute stays off — this is a look-only scaffold to edit.</summary>
	private static JsonNode BuildStrategyConfig()
	{
		var json = """
		{
			"rules": {
				"stopLoss": { "enabled": true, "pctOfMaxLoss": 0.75 },
				"takeProfit": { "enabled": true, "pctOfMaxProfit": 0.50 },
				"closeBeforeShortExpiry": { "enabled": true, "minProfitPct": 25, "emergencyBreakEvenBufferPct": 1.0 }
			},
			"execution": { "slippagePerSharePerOrder": 0.02, "roundTrips": 2 },
			"opener": {
				"weights": {
					"directionalFit": 0.10,
					"biasDrift": 0.50,
					"whipsaw": 3.0,
					"volatilityFit": 0.50,
					"maxPain": 0.25,
					"gex": 0,
					"statArb": 0.15,
					"sentiment": 0,
					"expectedMoveCredit": 0.60,
					"ivRealizedPremium": 0.40,
					"vixTermStructure": 0,
					"intradayTape": 0.30
				},
				"structures": {
					"longCalendar": { "enabled": false },
					"doubleCalendar": { "enabled": false },
					"longDiagonal": { "enabled": false },
					"doubleDiagonal": { "enabled": false },
					"ironButterfly": { "enabled": true, "dteMin": 7, "dteMax": 30, "wingSteps": [4, 6, 10] },
					"ironCondor": { "enabled": true, "dteMin": 7, "dteMax": 30, "widthSteps": [2, 4, 6], "bodyWidthSteps": [4, 6, 10], "shortDeltaMin": 0.15, "shortDeltaMax": 0.30 },
					"condor": { "enabled": false, "dteMin": 7, "dteMax": 30, "side": "both", "widthSteps": [2, 4], "bodyWidthSteps": [2, 4], "shortDeltaMin": 0.15, "shortDeltaMax": 0.45 },
					"shortVertical": { "enabled": true, "dteMin": 7, "dteMax": 30, "widthSteps": [2, 4, 6], "shortDeltaMin": 0.15, "shortDeltaMax": 0.30 },
					"longCallPut": { "enabled": false },
					"longVertical": { "enabled": false },
					"diagonalVertical": { "enabled": false },
					"calendarVertical": { "enabled": false }
				},
				"balanceRrExponent": 0.0
			},
			"autoExecute": {
				"management": { "enabled": false, "submit": false, "timeInForce": "GTC", "rules": ["StopLossRule", "TakeProfitRule", "CloseBeforeShortExpiryRule"] },
				"opener": { "enabled": false, "submit": false, "timeInForce": "GTC", "maxOrdersPerDay": 1, "structures": [] }
			}
		}
		""";
		return JsonNode.Parse(json)!;
	}

	private static void PrintSummary(string ticker, string strategy, string tickerPath, string stratPath, decimal? spot, decimal strikeStep, decimal? ivPct, bool live)
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[green]Scaffolded[/] {ticker} config ([bold]{strategy}[/] strategy):");
		AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(tickerPath)}[/]");
		AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(stratPath)}[/]");
		AnsiConsole.WriteLine();
		if (live)
		{
			var spotStr = spot is { } sp ? $"${sp:N2}" : "n/a";
			var ivStr = ivPct is { } iv ? $"{iv:N0}%" : "n/a (fallback 40)";
			AnsiConsole.MarkupLine($"  spot [bold]{spotStr}[/] · strikeStep [bold]{strikeStep.ToString("0.####", CultureInfo.InvariantCulture)}[/] · ATM IV [bold]{ivStr}[/] (from live chain)");
		}
		else
		{
			AnsiConsole.MarkupLine($"  [yellow]strikeStep/IV not derived — verify indicators.strikeStep against the live chain before trading.[/]");
		}
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"Next: [bold]wa ai scan {ticker}[/]  (edit the strategy layer to tune structures/weights)");
	}
}
