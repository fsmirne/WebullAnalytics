using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using WebullAnalytics.IO;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Report;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics;

// ─── `analyze position` ───────────────────────────────────────────────────────

internal sealed class AnalyzePositionSettings : AnalyzeSubcommandSettings
{
	[CommandArgument(0, "[spec]")]
	[Description("Open position. Format: ACTION:SYMBOL:QTY@PRICE,... where PRICE is your cost basis per leg. Example: sell:GME260424C00025000:499@0.48,buy:GME260515C00025000:499@1.11. Omit to select from open positions interactively.")]
	public string Spec { get; set; } = "";

	[CommandOption("--iv-default")]
	[Description("Fallback implied volatility for hypothetical legs when no live quote exists. Percent, default 40.")]
	public decimal IvDefault { get; set; } = 40m;

	[CommandOption("--strike-step")]
	[Description("Strike increment used for near-spot scenarios. Default 0.50.")]
	public decimal StrikeStep { get; set; } = 0.50m;

	[CommandOption("--cash")]
	[Description("Available cash/BP for funding. Scenarios whose BP delta exceeds this amount are flagged as not fundable.")]
	public decimal? Cash { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (!string.IsNullOrEmpty(Spec))
		{
			List<ParsedLeg> legs;
			try { legs = TradeLegParser.Parse(Spec); }
			catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }

			foreach (var leg in legs)
			{
				if (leg.Option == null)
					return ValidationResult.Error($"<spec>: '{leg.Symbol}' is not an OCC option symbol (analyze position requires option legs)");
				if (!leg.Price.HasValue)
					return ValidationResult.Error($"<spec>: leg '{leg.Symbol}' is missing @PRICE (cost basis per share is required)");
			}
		}

		if (IvDefault <= 0m || IvDefault > 500m)
			return ValidationResult.Error($"--iv-default: must be in (0, 500], got {IvDefault}");

		if (StrikeStep <= 0m)
			return ValidationResult.Error($"--strike-step: must be > 0, got {StrikeStep}");

		if (Cash.HasValue && Cash.Value < 0m)
			return ValidationResult.Error($"--cash: must be non-negative, got {Cash.Value}");

		return ValidationResult.Success();
	}
}

internal sealed class AnalyzePositionCommand : AsyncCommand<AnalyzePositionSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AnalyzePositionSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		TerminalHelper.EnsureTerminalWidthFromConfig();

		List<PositionSnapshot> positionLegs;
		if (string.IsNullOrEmpty(settings.Spec))
		{
			var (loaded, error) = SelectPositionFromLog();
			if (loaded == null)
			{
				Console.Error.WriteLine($"Error: {error}");
				return 1;
			}
			positionLegs = loaded;
		}
		else
		{
			var legs = TradeLegParser.Parse(settings.Spec);
			positionLegs = legs.Select(l => new PositionSnapshot(Symbol: l.Symbol, Action: l.Action, Qty: l.Quantity, CostBasis: l.Price!.Value, Parsed: l.Option!)).ToList();
		}

		var ticker = positionLegs[0].Parsed.Root;

		// Phase 1: fetch quotes for the position legs. We need spot before we can enumerate
		// hypothetical strikes for scenarios, and the underlying price comes back as a byproduct
		// of this same fetch. --ticker-price still wins if supplied.
		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
		IReadOnlyDictionary<string, decimal>? underlyingPrices = null;
		if (!string.IsNullOrEmpty(settings.Api))
		{
			var positionSymbols = positionLegs.Select(l => l.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			(quotes, underlyingPrices) = await AnalyzeCommon.FetchQuotesAndUnderlyingForSymbolList(settings, positionSymbols, cancellation);
		}

		var spot = ResolveSpot(ticker, settings, underlyingPrices);
		if (spot == null)
		{
			Console.Error.WriteLine($"Error: no underlying price for '{ticker}'. Pass --ticker-price {ticker}:<price> or configure --api yahoo|webull.");
			return 1;
		}

		var structure = ClassifyStructure(positionLegs);

		// Phase 2: fetch quotes for the hypothetical-scenario symbols we couldn't enumerate without spot.
		if (!string.IsNullOrEmpty(settings.Api))
		{
			var alreadyFetched = quotes ?? new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
			var hypotheticalSymbols = EnumerateHypotheticalSymbols(positionLegs, structure, settings, spot.Value)
				.Where(s => !alreadyFetched.ContainsKey(s))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (hypotheticalSymbols.Count > 0)
			{
				var hypotheticalQuotes = await AnalyzeCommon.FetchQuotesForSymbolList(settings, hypotheticalSymbols, cancellation);
				if (hypotheticalQuotes != null)
				{
					var merged = new Dictionary<string, OptionContractQuote>(alreadyFetched, StringComparer.OrdinalIgnoreCase);
					foreach (var kvp in hypotheticalQuotes) merged[kvp.Key] = kvp.Value;
					quotes = merged;
				}
			}
		}

		RenderHeader(positionLegs, structure, spot.Value);

		var scenarios = GenerateScenarios(positionLegs, structure, settings, spot.Value, EvaluationDate.Today, quotes);
		if (scenarios.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No scenarios defined yet for structure type '{structure}'. Phase 1 supports single-long and calendar/diagonal.[/]");
			return 0;
		}

		RenderScenarioTable(scenarios, settings);
		return 0;
	}

	// ─── Input model ──────────────────────────────────────────────────────────

	internal sealed record PositionSnapshot(string Symbol, LegAction Action, int Qty, decimal CostBasis, OptionParsed Parsed);

	internal enum StructureKind { SingleLong, SingleShort, Calendar, Diagonal, Vertical, Unsupported }

	// ─── Load from trade log ──────────────────────────────────────────────────

	private static (List<PositionSnapshot>? snapshots, string? error) SelectPositionFromLog()
	{
		var ordersPath = Program.ResolvePath(Program.OrdersPath);
		if (!File.Exists(ordersPath))
			return (null, $"Orders file '{ordersPath}' does not exist.");

		var (trades, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
		var (_, positions, _) = PositionTracker.ComputeReport(trades, feeLookup: feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);
		var (rows, _, _) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		var strategies = FindAllStrategyGroups(rows);

		if (strategies.Count == 0)
			return (null, "No open strategy positions found in the trade log.");

		var chosen = strategies.Count == 1
			? strategies[0]
			: AnsiConsole.Prompt(new SelectionPrompt<(PositionRow parent, List<PositionRow> legs)>()
				.Title("Select a position to analyze:")
				.UseConverter(item => FormatPositionLabel(item.parent, item.legs))
				.AddChoices(strategies));

		var snapshots = BuildSnapshotsFromLegs(chosen.legs);
		return snapshots.Count == 0
			? (null, "Could not parse any option legs for the selected position.")
			: (snapshots, null);
	}

	private static List<(PositionRow parent, List<PositionRow> legs)> FindAllStrategyGroups(IReadOnlyList<PositionRow> rows)
	{
		var result = new List<(PositionRow, List<PositionRow>)>();
		PositionRow? currentParent = null;
		var currentLegs = new List<PositionRow>();

		foreach (var row in rows)
		{
			if (row.IsStrategyLeg)
			{
				if (currentParent != null) currentLegs.Add(row);
				continue;
			}

			if (currentParent != null && currentLegs.Count > 0)
				result.Add((currentParent, new List<PositionRow>(currentLegs)));
			currentParent = null;
			currentLegs = new List<PositionRow>();
			if (row.Asset == Asset.OptionStrategy) currentParent = row;
		}

		if (currentParent != null && currentLegs.Count > 0)
			result.Add((currentParent, new List<PositionRow>(currentLegs)));

		return result;
	}

	private static string FormatPositionLabel(PositionRow parent, List<PositionRow> legs)
	{
		var parsedLegs = new List<(PositionRow row, OptionParsed opt)>();
		foreach (var leg in legs)
		{
			if (leg.MatchKey == null) continue;
			var occ = leg.MatchKey.StartsWith("option:") ? leg.MatchKey[7..] : leg.MatchKey;
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed != null) parsedLegs.Add((leg, parsed));
		}

		if (parsedLegs.Count == 0) return parent.Instrument;

		parsedLegs.Sort((a, b) => a.opt.ExpiryDate.CompareTo(b.opt.ExpiryDate));
		var ticker = parsedLegs[0].opt.Root;
		var callPut = parsedLegs[0].opt.CallPut == "C" ? "Call" : "Put";
		var qty = parsedLegs[0].row.Qty;
		var shortOpt = parsedLegs[0].opt;
		var longOpt = parsedLegs[^1].opt;

		var strikeStr = shortOpt.Strike == longOpt.Strike ? $"${shortOpt.Strike:F2}" : $"${shortOpt.Strike:F2}→${longOpt.Strike:F2}";
		var expiryStr = parsedLegs.Count > 1 ? $"{shortOpt.ExpiryDate:MM-dd}/{longOpt.ExpiryDate:MM-dd}" : $"{shortOpt.ExpiryDate:MM-dd}";

		return $"{ticker}  {parent.OptionKind}  {callPut}  {strikeStr}  {expiryStr}  x{qty}";
	}

	private static List<PositionSnapshot> BuildSnapshotsFromLegs(List<PositionRow> legRows)
	{
		var snapshots = new List<PositionSnapshot>();
		foreach (var leg in legRows)
		{
			if (leg.MatchKey == null) continue;
			var occ = leg.MatchKey.StartsWith("option:") ? leg.MatchKey[7..] : leg.MatchKey;
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed == null) continue;
			var action = leg.Side == Side.Buy ? LegAction.Buy : LegAction.Sell;
			snapshots.Add(new PositionSnapshot(Symbol: occ, Action: action, Qty: leg.Qty, CostBasis: OptionMath.GetPremium(leg), Parsed: parsed));
		}
		return snapshots;
	}

	// ─── Classifier ──────────────────────────────────────────────────────────

	private static StructureKind ClassifyStructure(IReadOnlyList<PositionSnapshot> legs)
	{
		if (legs.Count == 1)
			return legs[0].Action == LegAction.Buy ? StructureKind.SingleLong : StructureKind.SingleShort;

		if (legs.Count == 2)
		{
			var sl = legs.FirstOrDefault(l => l.Action == LegAction.Sell);
			var ll = legs.FirstOrDefault(l => l.Action == LegAction.Buy);
			if (sl == null || ll == null) return StructureKind.Unsupported;
			if (sl.Parsed.Root != ll.Parsed.Root || sl.Parsed.CallPut != ll.Parsed.CallPut) return StructureKind.Unsupported;
			if (sl.Parsed.ExpiryDate == ll.Parsed.ExpiryDate) return StructureKind.Vertical;
			if (sl.Parsed.ExpiryDate < ll.Parsed.ExpiryDate)
				return sl.Parsed.Strike == ll.Parsed.Strike ? StructureKind.Calendar : StructureKind.Diagonal;
		}

		return StructureKind.Unsupported;
	}

	// ─── Scenario generation ─────────────────────────────────────────────────

	internal sealed record Scenario(
		string Name,
		string ActionSummary,
		decimal CashImpactPerContract,      // per-contract
		decimal ProjectedValuePerContract,  // per-contract
		decimal TotalPnLPerContract,        // per-contract
		decimal BPDeltaPerContract,         // per-contract additional BP required (negative = BP frees up)
		int Qty,
		int DaysToTarget,                   // days from evaluation date to this scenario's target date; used to rank P&L per day
		string Rationale,
		bool IsRoll = false);               // true iff this scenario closes an existing leg and opens a new one (consulted by BuildReproductionCommands)

	/// <summary>Hypothetical OCC symbols the scenario generators will reference. Pre-enumerated so we can
	/// include them in a single up-front quote fetch.</summary>
	private static IEnumerable<string> EnumerateHypotheticalSymbols(IReadOnlyList<PositionSnapshot> legs, StructureKind kind, AnalyzePositionSettings settings, decimal spot)
	{
		if (legs.Count == 0) yield break;
		var root = legs[0].Parsed.Root;
		var callPut = legs[0].Parsed.CallPut;

		if (kind == StructureKind.SingleLong)
		{
			var longLeg = legs[0];
			var shortExpiry = NextWeeklyFromToday();
			if (shortExpiry < longLeg.Parsed.ExpiryDate)
				yield return MatchKeys.OccSymbol(root, shortExpiry, longLeg.Parsed.Strike, callPut);
		}
		else if (kind == StructureKind.Calendar || kind == StructureKind.Diagonal)
		{
			var shortLeg = legs.First(l => l.Action == LegAction.Sell);
			var longLeg = legs.First(l => l.Action == LegAction.Buy);
			var newExpiry = NextWeekly(shortLeg.Parsed.ExpiryDate);
			yield return MatchKeys.OccSymbol(root, newExpiry, shortLeg.Parsed.Strike, callPut);
			var newLongExp = longLeg.Parsed.ExpiryDate > newExpiry ? longLeg.Parsed.ExpiryDate : newExpiry.AddDays(21);
			var oppositeCp = callPut == "C" ? "P" : "C";
			foreach (var strike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (strike <= 0m) continue;
				if (strike != shortLeg.Parsed.Strike)
				{
					yield return MatchKeys.OccSymbol(root, shortLeg.Parsed.ExpiryDate, strike, callPut); // same-expiry strike roll
					yield return MatchKeys.OccSymbol(root, newExpiry, strike, callPut);                 // next-weekly strike roll
				}
				yield return MatchKeys.OccSymbol(root, newLongExp, strike, callPut);                     // reset-to-new-calendar long leg
				// "Add" scenarios: same-side second calendar + opposite-side double calendar/diagonal.
				yield return MatchKeys.OccSymbol(root, newExpiry, strike, oppositeCp);
				yield return MatchKeys.OccSymbol(root, newLongExp, strike, oppositeCp);
			}
		}
	}

	private static List<Scenario> GenerateScenarios(
		List<PositionSnapshot> legs, StructureKind kind,
		AnalyzePositionSettings settings, decimal spot, DateTime asOf,
		IReadOnlyDictionary<string, OptionContractQuote>? quotes) =>
		kind switch
		{
			StructureKind.SingleLong => GenerateSingleLongScenarios(legs[0], settings, spot, asOf, quotes),
			StructureKind.Calendar or StructureKind.Diagonal => GenerateSpreadScenarios(legs, settings, spot, asOf, kind, quotes),
			_ => new List<Scenario>()
		};

	private static List<Scenario> GenerateSingleLongScenarios(PositionSnapshot longLeg, AnalyzePositionSettings settings, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var list = new List<Scenario>();
		var iv = ResolveIV(longLeg.Symbol, settings, quotes);
		var callPut = longLeg.Parsed.CallPut;
		var quotesForPricing = settings.EvaluationDateOverride.HasValue ? null : quotes;

		// 1. Hold (do nothing) — value at expiry = intrinsic.
		var valueAtExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		var holdDte = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		list.Add(NewScenario("Hold to expiry", longLeg, "—",
			cashNow: 0m, valueAtTarget: valueAtExpiry, bpDeltaPerContract: 0m, daysToTarget: holdDte,
			rationale: $"value at expiry ({longLeg.Parsed.ExpiryDate:yyyy-MM-dd}) = intrinsic ${valueAtExpiry:F2}/share"));

		// 2. Close now at theoretical mid (or live mid if available).
		var dteNow = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var midNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, dteNow, iv, callPut);
		list.Add(NewScenario("Close now", longLeg, $"SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(midNow)}",
			cashNow: midNow, valueAtTarget: 0m, bpDeltaPerContract: 0m, daysToTarget: 1,
			rationale: $"sell at mid ${midNow:F2}/share → close position"));

		// 3. Convert to calendar: sell a near-expiry short at same strike.
		var shortExpiry = NextWeeklyFromToday();
		if (shortExpiry < longLeg.Parsed.ExpiryDate)
		{
			var newShortSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, shortExpiry, longLeg.Parsed.Strike, callPut);
			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var dteShort = Math.Max(1, (shortExpiry - asOf).Days);
			var shortMid = LiveOrBsMid(quotesForPricing, newShortSym, spot, longLeg.Parsed.Strike, dteShort, ivNewShort, callPut);
			// Project value at short expiry: long BS value, short intrinsic.
			var dteLongAtShortExp = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days);
			var longAtShortExp = (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, dteLongAtShortExp / 365.0, 0.036, iv, callPut);
			var shortAtShortExp = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
			var net = longAtShortExp - shortAtShortExp;
			// BP delta: becomes a calendar (strike_loss = 0). Current is single long (no BP). Delta = 0.
			list.Add(NewScenario($"Convert to calendar (sell {shortExpiry:yyyy-MM-dd} @ ${longLeg.Parsed.Strike:F2})",
				longLeg, $"SELL {newShortSym} x{longLeg.Qty} @{FmtPrice(shortMid)}",
				cashNow: shortMid, valueAtTarget: net, bpDeltaPerContract: 0m, daysToTarget: dteShort,
				rationale: $"collect ${shortMid:F2}/share short premium; at short exp: long ${longAtShortExp:F2} - short ${shortAtShortExp:F2} = ${net:F2}"));
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract / Math.Max(1m, s.DaysToTarget)).ToList();
	}

	private static List<Scenario> GenerateSpreadScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, StructureKind kind, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var list = new List<Scenario>();
		var shortLeg = legs.First(l => l.Action == LegAction.Sell);
		var longLeg = legs.First(l => l.Action == LegAction.Buy);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, settings, quotes);
		var ivLong = ResolveIV(longLeg.Symbol, settings, quotes);

		// When a date override is active, live bid/ask reflects today's market prices, not the
		// evaluation date's. Null out quotes for pricing so LiveOrBsMid/LiveBidAsk always use
		// Black-Scholes with the correct DTE from asOf. IV is still sourced from live quotes above.
		var quotesForPricing = settings.EvaluationDateOverride.HasValue ? null : quotes;

		var shortMidNow = LiveOrBsMid(quotesForPricing, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivShort, callPut);
		var longMidNow = LiveOrBsMid(quotesForPricing, longLeg.Symbol, spot, longLeg.Parsed.Strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivLong, callPut);
		var (shortBidNow, shortAskNow) = LiveBidAsk(quotesForPricing, shortLeg.Symbol, shortMidNow);
		var (longBidNow, longAskNow) = LiveBidAsk(quotesForPricing, longLeg.Symbol, longMidNow);

		// Current BP (ongoing; covered-structure debit is sunk).
		var currentBp = AnalyzeCommon.ComputeLegMargin(shortLeg.Parsed, 1, spot, shortMidNow, longLeg.Parsed, null, 1, longMidNow, isExisting: true).Total;

		decimal LongValueAtShortExpiry(decimal longStrike, DateTime shortExpiry) =>
			(decimal)OptionMath.BlackScholes(spot, longStrike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days) / 365.0, 0.036, ivLong, callPut);

		// Compute the "hold" per-share value once — used as the unchanged-portion projection for partial variants.
		var longAtOriginalExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
		var shortAtOriginalExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
		var holdNetPerShare = longAtOriginalExp - shortAtOriginalExp;

		var origShortDte = Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);

		// 1. Hold to short expiry.
		list.Add(NewScenarioSpread("Hold to short expiry", legs, "—",
			cashNow: 0m, valueAtTarget: holdNetPerShare, bpDeltaPerContract: 0m, daysToTarget: origShortDte,
			rationale: $"at {shortLeg.Parsed.ExpiryDate:yyyy-MM-dd}: long ${longAtOriginalExp:F2} - short ${shortAtOriginalExp:F2} intrinsic = ${holdNetPerShare:F2}"));

		// 2. Close short only (realistic: pay short ask).
		{
			var cash = -shortAskNow;
			var longAtExpiry = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
			// Post-action: single long. No short = no BP requirement.
			var bpDelta = 0m - currentBp;
			list.Add(NewScenarioSpread("Close short only", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortAskNow)}",
				cashNow: cash, valueAtTarget: longAtExpiry, bpDeltaPerContract: bpDelta, daysToTarget: origShortDte,
				rationale: $"pay ${shortAskNow:F2} ask to buy back short; keep long → ${longAtExpiry:F2}/share at short exp"));
		}

		// 3. Close all (sell long at bid, buy short at ask).
		{
			var cash = longBidNow - shortAskNow;
			var bpDelta = 0m - currentBp;
			list.Add(NewScenarioSpread("Close all", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty} @{FmtPrice(shortAskNow)}, SELL {longLeg.Symbol} x{longLeg.Qty} @{FmtPrice(longBidNow)}",
				cashNow: cash, valueAtTarget: 0m, bpDeltaPerContract: bpDelta, daysToTarget: 1,
				rationale: $"sell long @${longBidNow:F2} bid, buy short @${shortAskNow:F2} ask → net ${cash:+0.00;-0.00}/share"));
		}

		var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
		if (newExp < longLeg.Parsed.ExpiryDate)
		{
			// 4. Roll short same strike, next weekly.
			{
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, shortLeg.Parsed.Strike, callPut);
				if (quotesForPricing == null || HasLiveQuote(quotesForPricing, newSym))
				{
				var ivNewShort = ResolveIV(newSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newSym, spot, shortLeg.Parsed.Strike, dteNewShort, ivNewShort, callPut);
				var (newShortBid, _) = LiveBidAsk(quotesForPricing, newSym, newShortMidExec);
				var cashPerShare = newShortBid - shortAskNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, shortLeg.Parsed.Strike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var bpDelta = newBp - currentBp;
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short ({newExp:MM-dd}, same strike)",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortAskNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortBid)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					bpPerContract: bpDelta,
					daysToTarget: dteNewShort,
					rationale: $"buy short @${shortAskNow:F2} ask, sell new @${newShortBid:F2} bid → net ${cashPerShare:+0.00;-0.00}/share; at new exp: ${newProjectedPerShare:F2}",
					isRoll: true);
				}
			}

			// 4.5. Roll short to bracket strikes, SAME expiry — keeps the short on the current week
			// so theta keeps working over the next few days. Projects at the original short expiry.
			foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

				var sameExpSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, newStrike, callPut);
				if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, sameExpSym)) continue;

				var ivSameExp = ResolveIV(sameExpSym, settings, quotes);
				var dteSameExp = Math.Max(1, (shortLeg.Parsed.ExpiryDate - asOf).Days);
				var newShortMidSameExp = LiveOrBsMid(quotesForPricing, sameExpSym, spot, newStrike, dteSameExp, ivSameExp, callPut);
				var (newShortBidSameExp, _) = LiveBidAsk(quotesForPricing, sameExpSym, newShortMidSameExp);
				var cashPerShareSameExp = newShortBidSameExp - shortAskNow;
				// At original short expiry: long has full remaining DTE to long expiry; new short at intrinsic.
				var longAtOrigExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
				var newShortIntrinsicAtExp = Intrinsic(spot, newStrike, callPut);
				var projSameExpPerShare = longAtOrigExp - newShortIntrinsicAtExp;
				var sameExpShortParsed = new OptionParsed(shortLeg.Parsed.Root, shortLeg.Parsed.ExpiryDate, callPut, newStrike);
				var sameExpBp = AnalyzeCommon.ComputeLegMargin(sameExpShortParsed, 1, spot, newShortMidSameExp, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var sameExpBpDelta = sameExpBp - currentBp;

				var sameExpStructure = callPut == "C"
					? (newStrike < longLeg.Parsed.Strike ? "inverted diagonal" : newStrike > longLeg.Parsed.Strike ? "covered diagonal" : "calendar")
					: (newStrike > longLeg.Parsed.Strike ? "inverted diagonal" : newStrike < longLeg.Parsed.Strike ? "covered diagonal" : "calendar");
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short to ${newStrike:F2} (same exp {shortLeg.Parsed.ExpiryDate:MM-dd}, {sameExpStructure})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortAskNow)}, SELL {sameExpSym} x{{qty}} @{FmtPrice(newShortBidSameExp)}",
					cashPerShareOfChange: cashPerShareSameExp,
					newProjectedPerShare: projSameExpPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					bpPerContract: sameExpBpDelta,
					daysToTarget: dteSameExp,
					rationale: $"shift to ${newStrike:F2} strike, keep {shortLeg.Parsed.ExpiryDate:MM-dd} expiry — collect theta this week; credit ${cashPerShareSameExp:+0.00;-0.00}/share; at exp: ${projSameExpPerShare:F2}",
					isRoll: true);
			}

			// 5. Roll short to bracket strikes near spot (one per strike).
			foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m || newStrike == shortLeg.Parsed.Strike) continue;

				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, newStrike, callPut);
				if (quotesForPricing != null && !HasLiveQuote(quotesForPricing, newSym)) continue; // skip if contract not listed
				var ivNewShort = ResolveIV(newSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newSym, spot, newStrike, dteNewShort, ivNewShort, callPut);
				var (newShortBid, _) = LiveBidAsk(quotesForPricing, newSym, newShortMidExec);
				var cashPerShare = newShortBid - shortAskNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, newStrike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var bpDelta = newBp - currentBp;

				var structureLabel = callPut == "C"
					? (newStrike < longLeg.Parsed.Strike ? "inverted diagonal" : newStrike > longLeg.Parsed.Strike ? "covered diagonal" : "calendar")
					: (newStrike > longLeg.Parsed.Strike ? "inverted diagonal" : newStrike < longLeg.Parsed.Strike ? "covered diagonal" : "calendar");
				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Roll short to ${newStrike:F2} ({newExp:MM-dd}, {structureLabel})",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortAskNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortBid)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					bpPerContract: bpDelta,
					daysToTarget: dteNewShort,
					rationale: $"step short to ${newStrike:F2} (spot ${spot:F2}); credit ${cashPerShare:+0.00;-0.00}/share; at new exp: ${newProjectedPerShare:F2}",
					isRoll: true);
			}
		}

		// 6. Close all and reopen a fresh calendar at bracket strikes near spot.
		{
			var newShortExp = newExp;
			var newLongExp = longLeg.Parsed.ExpiryDate > newShortExp ? longLeg.Parsed.ExpiryDate : newShortExp.AddDays(21);
			foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
			{
				if (newStrike <= 0m) continue;

				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newShortExp, newStrike, callPut);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newLongExp, newStrike, callPut);
				if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;
				var ivNewShort = ResolveIV(newShortSym, settings, quotes);
				var ivNewLong = ResolveIV(newLongSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newShortExp - asOf).Days);
				var dteNewLong = Math.Max(1, (newLongExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newStrike, dteNewShort, ivNewShort, callPut);
				var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newStrike, dteNewLong, ivNewLong, callPut);
				var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
				var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
				var closeNet = longBidNow - shortAskNow;
				var openNet = newShortBid - newLongAsk;
				var cashPerShare = closeNet + openNet;
				var longAtNewShortExp = (decimal)OptionMath.BlackScholes(spot, newStrike, Math.Max(1, (newLongExp.Date - newShortExp.Date).Days) / 365.0, 0.036, ivNewLong, callPut);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var newProjectedPerShare = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newShortExp, callPut, newStrike);
				var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newLongExp, callPut, newStrike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, newLongParsed, null, 1, newLongMidExec, isExisting: false).Total;
				var bpDelta = newBp - currentBp;

				EmitFullAndPartial(list, legs, settings.Cash,
					name: $"Reset to ${newStrike:F2} calendar",
					actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortAskNow)}, SELL {longLeg.Symbol} x{{qty}} @{FmtPrice(longBidNow)}, BUY {newLongSym} x{{qty}} @{FmtPrice(newLongAsk)}, SELL {newShortSym} x{{qty}} @{FmtPrice(newShortBid)}",
					cashPerShareOfChange: cashPerShare,
					newProjectedPerShare: newProjectedPerShare,
					unchangedProjectedPerShare: holdNetPerShare,
					bpPerContract: bpDelta,
					daysToTarget: dteNewShort,
					rationale: $"close net ${closeNet:+0.00;-0.00}, open new net ${openNet:+0.00;-0.00}; projected at new short exp: ${newProjectedPerShare:F2}",
					isRoll: true);
			}
		}

		// 7. Add new position alongside existing (hedging / diversification).
		// Same-side: second calendar/diagonal at a different strike.
		// Opposite-side: creates a double calendar or double diagonal.
		// Existing position untouched; new position projected at existing short's expiry.
		{
			var addShortExp = newExp;
			var addLongExp = longLeg.Parsed.ExpiryDate > addShortExp ? longLeg.Parsed.ExpiryDate : addShortExp.AddDays(21);
			foreach (var addCp in new[] { callPut, callPut == "C" ? "P" : "C" })
			{
				var isOppositeSide = addCp != callPut;
				foreach (var newStrike in BracketStrikes(spot, settings.StrikeStep))
				{
					if (newStrike <= 0m) continue;
					if (!isOppositeSide && newStrike == shortLeg.Parsed.Strike) continue; // avoid doubling same-CP same-strike

					var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, addShortExp, newStrike, addCp);
					var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, addLongExp, newStrike, addCp);
					if (quotesForPricing != null && (!HasLiveQuote(quotesForPricing, newShortSym) || !HasLiveQuote(quotesForPricing, newLongSym))) continue;

					var ivNewShort = ResolveIV(newShortSym, settings, quotes);
					var ivNewLong = ResolveIV(newLongSym, settings, quotes);
					var dteNewShort = Math.Max(1, (addShortExp - asOf).Days);
					var dteNewLong = Math.Max(1, (addLongExp - asOf).Days);
					var newShortMidExec = LiveOrBsMid(quotesForPricing, newShortSym, spot, newStrike, dteNewShort, ivNewShort, addCp);
					var newLongMidExec = LiveOrBsMid(quotesForPricing, newLongSym, spot, newStrike, dteNewLong, ivNewLong, addCp);
					var (newShortBid, _) = LiveBidAsk(quotesForPricing, newShortSym, newShortMidExec);
					var (_, newLongAsk) = LiveBidAsk(quotesForPricing, newLongSym, newLongMidExec);
					var cashPerShare = newShortBid - newLongAsk; // debit (negative) to open new calendar

					// Project the NEW position at existing short's expiry (first milestone).
					var origShortExp = shortLeg.Parsed.ExpiryDate;
					var tRemainNewShort = Math.Max(1, (addShortExp.Date - origShortExp.Date).Days) / 365.0;
					var tRemainNewLong = Math.Max(1, (addLongExp.Date - origShortExp.Date).Days) / 365.0;
					var newShortAtOrigExp = (decimal)OptionMath.BlackScholes(spot, newStrike, tRemainNewShort, 0.036, ivNewShort, addCp);
					var newLongAtOrigExp = (decimal)OptionMath.BlackScholes(spot, newStrike, tRemainNewLong, 0.036, ivNewLong, addCp);
					var newPositionValuePerShare = newLongAtOrigExp - newShortAtOrigExp;

					// Opening a long calendar/diagonal is pure-debit: BP required = actual cash debit paid,
					// using the realistic worst-case fill (long ask, short bid). Using mid-based ComputeLegMargin
					// here would understate BP when bid/ask spreads are wide (e.g. put legs), and cause the
					// partial-qty sizing to suggest more contracts than the cash can actually fund.
					var newBp = Math.Max(-cashPerShare, 0m) * 100m;

					var sideLabel = addCp == "C" ? "call" : "put";
					// The added trade has both legs at `newStrike` with different expiries — always a calendar.
					// Same-side adds a second calendar at a different strike; opposite-side creates a double calendar.
					var structureType = isOppositeSide ? "double calendar" : "second-strike calendar";

					EmitAdd(list, legs, settings.Cash,
						name: $"Add ${newStrike:F2} {sideLabel} {addShortExp:MM-dd}/{addLongExp:MM-dd} ({structureType}, keep existing)",
						newShortSym: newShortSym,
						newLongSym: newLongSym,
						newShortPrice: newShortBid,
						newLongPrice: newLongAsk,
						cashPerShareOfChange: cashPerShare,
						newProjectedPerShare: newPositionValuePerShare,
						unchangedProjectedPerShare: holdNetPerShare,
						bpPerContract: newBp,
						daysToTarget: origShortDte,
						rationale: $"open new {sideLabel} calendar at ${newStrike:F2} (debit ${-cashPerShare:F2}/share); existing untouched → at {origShortExp:MM-dd}: existing ${holdNetPerShare:F2} + new ${newPositionValuePerShare:F2} = ${holdNetPerShare + newPositionValuePerShare:F2}/share");
				}
			}
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract / Math.Max(1m, s.DaysToTarget)).ToList();
	}

	/// <summary>Appends an "add new position alongside existing" scenario. Existing untouched;
	/// new position adds BP and debit. Combined projection = existing hold + new position value at target.
	/// Partial variant sizes the added qty to fit available cash while keeping all existing contracts.</summary>
	private static void EmitAdd(
		List<Scenario> list,
		IReadOnlyList<PositionSnapshot> legs,
		decimal? availableCash,
		string name,
		string newShortSym,
		string newLongSym,
		decimal newShortPrice,
		decimal newLongPrice,
		decimal cashPerShareOfChange,
		decimal newProjectedPerShare,
		decimal unchangedProjectedPerShare,
		decimal bpPerContract,
		int daysToTarget,
		string rationale)
	{
		var fullQty = legs[0].Qty;
		var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var initialDebitPerContract = initialDebitPerShare * 100m;

		var fullCashPerContract = cashPerShareOfChange * 100m;
		var fullCombinedValuePerContract = (unchangedProjectedPerShare + newProjectedPerShare) * 100m;
		var fullTotalPerContract = fullCombinedValuePerContract + fullCashPerContract - initialDebitPerContract;
		list.Add(new Scenario(
			name,
			$"BUY {newLongSym} x{fullQty} @{FmtPrice(newLongPrice)}, SELL {newShortSym} x{fullQty} @{FmtPrice(newShortPrice)}",
			CashImpactPerContract: fullCashPerContract,
			ProjectedValuePerContract: fullCombinedValuePerContract,
			TotalPnLPerContract: fullTotalPerContract,
			BPDeltaPerContract: bpPerContract,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: rationale));

		if (!availableCash.HasValue || bpPerContract <= 0m) return;
		var maxPartial = (int)Math.Floor(availableCash.Value / bpPerContract);
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		// Partial: add `maxPartial` new contracts, keep all `fullQty` existing.
		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialNewValue = newProjectedPerShare * 100m * maxPartial;
		var partialExistingValue = unchangedProjectedPerShare * 100m * fullQty;
		var partialCombinedValue = partialNewValue + partialExistingValue;
		var partialTotalPnL = partialCashTotal + partialCombinedValue - initialDebitPerContract * fullQty;

		list.Add(new Scenario(
			$"{name} · partial {maxPartial}",
			$"BUY {newLongSym} x{maxPartial} @{FmtPrice(newLongPrice)}, SELL {newShortSym} x{maxPartial} @{FmtPrice(newShortPrice)}",
			CashImpactPerContract: partialCashTotal / fullQty,
			ProjectedValuePerContract: partialCombinedValue / fullQty,
			TotalPnLPerContract: partialTotalPnL / fullQty,
			BPDeltaPerContract: bpPerContract * maxPartial / fullQty,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: $"add {maxPartial} new contract(s) (${bpPerContract * maxPartial:N0} BP); keep all {fullQty} existing"));
	}

	/// <summary>Appends a full-quantity scenario to the list. If the full BP delta exceeds available
	/// cash AND there's a positive max-fundable partial quantity, also appends a partial variant.
	/// In the partial, the unchanged portion is valued at its natural terminal date (the hold projection),
	/// so the mix doesn't double-count time decay. Pass isRoll:true when the scenario closes an existing
	/// leg and opens a new one — BuildReproductionCommands uses the flag to decide whether to split the
	/// emitted `wa trade place` command into two single-leg orders for non-calendar rolls.</summary>
	private static void EmitFullAndPartial(
		List<Scenario> list,
		IReadOnlyList<PositionSnapshot> legs,
		decimal? availableCash,
		string name,
		string actionSummary,
		decimal cashPerShareOfChange,
		decimal newProjectedPerShare,
		decimal unchangedProjectedPerShare,
		decimal bpPerContract,
		int daysToTarget,
		string rationale,
		bool isRoll = false)
	{
		var fullQty = legs[0].Qty;
		var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var initialDebitPerContract = initialDebitPerShare * 100m;

		// Full scenario.
		var fullCashPerContract = cashPerShareOfChange * 100m;
		var fullProjectedPerContract = newProjectedPerShare * 100m;
		var fullTotalPerContract = fullProjectedPerContract + fullCashPerContract - initialDebitPerContract;
		list.Add(new Scenario(
			name,
			actionSummary.Replace("{qty}", fullQty.ToString()),
			CashImpactPerContract: fullCashPerContract,
			ProjectedValuePerContract: fullProjectedPerContract,
			TotalPnLPerContract: fullTotalPerContract,
			BPDeltaPerContract: bpPerContract,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: rationale,
			IsRoll: isRoll));

		// Partial variant: only emit if BP is positive and cash is constrained below full.
		if (!availableCash.HasValue || bpPerContract <= 0m) return;
		var maxPartial = (int)Math.Floor(availableCash.Value / bpPerContract);
		if (maxPartial <= 0 || maxPartial >= fullQty) return;

		// Per-contract-of-total values for the partial mix.
		var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
		var partialProjectedTotal = newProjectedPerShare * 100m * maxPartial + unchangedProjectedPerShare * 100m * (fullQty - maxPartial);
		var partialTotalPnL = partialCashTotal + partialProjectedTotal - initialDebitPerContract * fullQty;
		list.Add(new Scenario(
			$"{name} · partial {maxPartial}/{fullQty}",
			actionSummary.Replace("{qty}", maxPartial.ToString()),
			CashImpactPerContract: partialCashTotal / fullQty,
			ProjectedValuePerContract: partialProjectedTotal / fullQty,
			TotalPnLPerContract: partialTotalPnL / fullQty,
			BPDeltaPerContract: bpPerContract * maxPartial / fullQty,
			Qty: fullQty,
			DaysToTarget: daysToTarget,
			Rationale: $"execute on {maxPartial} contracts (${bpPerContract * maxPartial:N0} BP); hold remaining {fullQty - maxPartial} as original → ${unchangedProjectedPerShare:F2}/share at original exp",
			IsRoll: isRoll));
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static Scenario NewScenario(string name, PositionSnapshot longLeg, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false)
	{
		var initialDebit = longLeg.CostBasis;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, longLeg.Qty, daysToTarget, rationale, isRoll);
	}

	private static Scenario NewScenarioSpread(string name, IReadOnlyList<PositionSnapshot> legs, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false)
	{
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var qty = legs[0].Qty;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, qty, daysToTarget, rationale, isRoll);
	}

	/// <summary>Returns true if the quote dictionary has a real, usable quote for this symbol
	/// (both bid and ask populated, ask > 0). Used when --api is set to skip scenarios whose
	/// hypothetical contracts aren't listed at the exchange (e.g., odd strikes at non-standard expiries).</summary>
	private static bool HasLiveQuote(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol) =>
		quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Ask.Value > 0m;

	/// <summary>Returns live mid from quotes if both bid and ask are populated; otherwise a BS theoretical mid.</summary>
	private static decimal LiveOrBsMid(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal spot, decimal strike, int dte, decimal iv, string callPut)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value + q.Ask.Value) / 2m;
		return (decimal)OptionMath.BlackScholes(spot, strike, dte / 365.0, 0.036, iv, callPut);
	}

	/// <summary>Returns (bid, ask) from live quote when available; otherwise a ±1% synthetic spread around the given mid.</summary>
	private static (decimal bid, decimal ask) LiveBidAsk(IReadOnlyDictionary<string, OptionContractQuote>? quotes, string symbol, decimal fallbackMid)
	{
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value >= 0m && q.Ask.Value > 0m)
			return (q.Bid.Value, q.Ask.Value);
		var spread = Math.Max(0.01m, fallbackMid * 0.01m);
		return (Math.Max(0m, fallbackMid - spread), fallbackMid + spread);
	}

	/// <summary>Returns IV as a fraction (0.40 for 40%). Sources in priority order:
	/// per-symbol --iv override (user-supplied percent, divided by 100), then live quote
	/// (already a fraction), then --iv-default (percent, divided by 100).</summary>
	private static decimal ResolveIV(string symbol, AnalyzePositionSettings settings, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (!string.IsNullOrEmpty(settings.IvOverrides))
		{
			foreach (var entry in settings.IvOverrides.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = entry.Split(':');
				if (parts.Length == 2 && parts[0].Equals(symbol, StringComparison.OrdinalIgnoreCase)
					&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var iv))
					return iv / 100m;
			}
		}
		if (quotes != null && quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m)
			return q.ImpliedVolatility.Value;
		return settings.IvDefault / 100m;
	}

	private static decimal? ResolveSpot(string ticker, AnalyzePositionSettings settings, IReadOnlyDictionary<string, decimal>? underlyingPrices)
	{
		if (!string.IsNullOrEmpty(settings.TickerPrice))
		{
			foreach (var entry in settings.TickerPrice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = entry.Split(':');
				if (parts.Length == 2 && parts[0].Equals(ticker, StringComparison.OrdinalIgnoreCase)
					&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
					return p;
			}
		}
		if (underlyingPrices != null && underlyingPrices.TryGetValue(ticker, out var apiPrice) && apiPrice > 0m)
			return apiPrice;
		return null;
	}

	private static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}

	private static DateTime NextWeeklyFromToday()
	{
		var d = EvaluationDate.Today.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}

	private static decimal Intrinsic(decimal spot, decimal strike, string callPut) =>
		callPut == "C" ? Math.Max(0m, spot - strike) : Math.Max(0m, strike - spot);

	private static decimal NearestStrike(decimal spot, decimal step) =>
		Math.Round(spot / step) * step;

	/// <summary>Returns the two strikes bracketing spot on the configured step grid. When spot lands
	/// exactly on a strike, the same value is yielded once.</summary>
	private static IEnumerable<decimal> BracketStrikes(decimal spot, decimal step)
	{
		var below = Math.Floor(spot / step) * step;
		var above = Math.Ceiling(spot / step) * step;
		yield return below;
		if (above != below) yield return above;
	}

	// ─── Rendering ───────────────────────────────────────────────────────────

	private static void RenderHeader(IReadOnlyList<PositionSnapshot> legs, StructureKind kind, decimal spot)
	{
		var ticker = legs[0].Parsed.Root;
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var qty = legs[0].Qty;
		AnsiConsole.MarkupLine($"[bold cyan]{kind}[/] [bold]{Markup.Escape(ticker)}[/] x{qty} @ cost basis ${initialDebit:F2}/contract — evaluating at spot [yellow]${spot:F2}[/], date [yellow]{EvaluationDate.Today:yyyy-MM-dd}[/]");
		foreach (var l in legs)
		{
			var label = l.Action == LegAction.Buy ? "[green]Long [/]" : "[red]Short[/]";
			AnsiConsole.MarkupLine($"  {label}  {Markup.Escape(l.Symbol)} x{l.Qty} @ ${l.CostBasis:F2}  (exp {l.Parsed.ExpiryDate:yyyy-MM-dd}, DTE {(l.Parsed.ExpiryDate.Date - EvaluationDate.Today.Date).Days})");
		}
		AnsiConsole.WriteLine();
	}

	private static void RenderScenarioTable(IReadOnlyList<Scenario> scenarios, AnalyzePositionSettings settings)
	{
		var availableCash = settings.Cash;

		// Identify the first fundable scenario so we can highlight it (green). The top-ranked
		// scenario may be unfundable — the actionable recommendation is the best fundable one.
		Scenario? topFundable = null;
		foreach (var sc in scenarios)
		{
			var delta = sc.BPDeltaPerContract * sc.Qty;
			if (!availableCash.HasValue || delta <= availableCash.Value) { topFundable = sc; break; }
		}

		foreach (var sc in scenarios)
		{
			var bpTotal = sc.BPDeltaPerContract * sc.Qty;
			var fundable = !availableCash.HasValue || bpTotal <= availableCash.Value;
			var isRecommended = topFundable != null && ReferenceEquals(sc, topFundable);
			var style = isRecommended ? "bold green" : (fundable ? "white" : "dim");

			var totalTotal = sc.TotalPnLPerContract * sc.Qty;
			var totalStr = $"${sc.TotalPnLPerContract:F2}/contract → {(totalTotal >= 0 ? "+" : "-")}${Math.Abs(totalTotal):N2} total";
			var cashStr = $"${sc.CashImpactPerContract:+0.00;-0.00}/contract";
			var projStr = $"${sc.ProjectedValuePerContract:F2}/contract";
			var bpMarkup = bpTotal == 0m
				? "[dim]no BP change[/]"
				: bpTotal < 0m
					? $"[green]BP {bpTotal:+$0;-$0;0} frees up[/]"
					: (availableCash.HasValue && bpTotal > availableCash.Value
						? $"[red]BP +${bpTotal:N2} (NEEDS ${bpTotal - availableCash.Value:N2} MORE)[/]"
						: $"[yellow]BP +${bpTotal:N2}[/]");
			var fundMarker = !fundable ? " [red](not fundable)[/]" : "";
			var prefix = isRecommended ? "★ " : "";

			var lines = new List<IRenderable>
			{
				new Markup($"[dim]Cash {Markup.Escape(cashStr)}  │  Projected {Markup.Escape(projStr)}  │  P&L {Markup.Escape(totalStr)}  │  {bpMarkup}[/]"),
				new Markup($"[dim]{Markup.Escape(sc.Rationale)}[/]"),
			};
			var (tradeCmds, analyzeCmd) = BuildReproductionCommands(sc, settings);
			if (tradeCmds != null)
				foreach (var cmd in tradeCmds)
					lines.Add(new Markup($"[grey50]↪ {Markup.Escape(cmd)}[/]"));
			if (analyzeCmd != null)
				lines.Add(new Markup($"[grey50]↪ {Markup.Escape(analyzeCmd)}[/]"));

			var panel = new Panel(new Rows(lines))
				.Header($"[{style}]{prefix}{Markup.Escape(sc.Name)}[/]{fundMarker}")
				.Expand()
				.BorderColor(isRecommended ? Color.Green : (fundable ? Color.Grey : Color.Grey35));
			AnsiConsole.Write(panel);
		}

		if (availableCash.HasValue)
		{
			AnsiConsole.WriteLine();
			AnsiConsole.MarkupLine($"[dim]Fundability check: available cash/BP = ${availableCash.Value:N2}.[/]");
		}
	}

	/// <summary>Formats a per-share option price for inclusion in a trade-spec leg. Uses 3 decimals
	/// to preserve sub-penny mid precision without gratuitous trailing zeros.</summary>
	private static string FmtPrice(decimal price) => price.ToString("0.###", CultureInfo.InvariantCulture);

	/// <summary>Converts a scenario ActionSummary like "BUY SYM x200 @0.305, SELL SYM2 x200 @0.44"
	/// into reproducible commands: one or more 'wa trade place' lines for execution and a single
	/// 'wa analyze trade' line for validation. For same-strike calendar rolls and non-roll scenarios,
	/// one combo 'wa trade place' line is emitted with the net `--limit` derived from CashImpactPerContract.
	/// For non-calendar rolls (diagonals, same-expiry-different-strike), Webull's combo engine rejects
	/// the reversal, so two separate single-leg 'wa trade place' lines are emitted in the order the legs
	/// appear in ActionSummary (close-first, open-second — a contract upheld by the scenario generators).
	/// Each uses that leg's per-share price as its `--limit`. Returns (null, null) for hold/no-op scenarios.</summary>
	private static (IReadOnlyList<string>? Trades, string? Analyze) BuildReproductionCommands(Scenario sc, AnalyzePositionSettings settings)
	{
		if (string.IsNullOrWhiteSpace(sc.ActionSummary) || sc.ActionSummary == "—") return (null, null);
		var parts = sc.ActionSummary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		// Parse each "ACTION SYMBOL xQTY @PRICE" part into (action, symbol, qty, price).
		var legs = new List<(string Action, string Symbol, string Qty, string Price)>(parts.Length);
		foreach (var part in parts)
		{
			var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length != 4) return (null, null);
			var action = tokens[0].ToLowerInvariant();
			if (action != "buy" && action != "sell") return (null, null);
			legs.Add((action, tokens[1], tokens[2].TrimStart('x'), tokens[3].TrimStart('@')));
		}

		// Analyze-trade line: one combined command, per-leg @PRICE preserved.
		var analyzeLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}");
		var extras = new List<string>();
		if (!string.IsNullOrEmpty(settings.TickerPrice)) extras.Add($"--ticker-price {settings.TickerPrice}");
		if (!string.IsNullOrEmpty(settings.Date)) extras.Add($"--date {settings.Date}");
		var suffix = extras.Count > 0 ? " " + string.Join(" ", extras) : "";
		var analyze = $"wa analyze trade \"{string.Join(",", analyzeLegs)}\"{suffix}";

		// Split non-calendar rolls into per-leg orders so Webull's combo engine accepts them.
		// Per-leg --limit is rounded to cents so it round-trips through the broker (sub-penny isn't a valid tick).
		var splittable = sc.IsRoll && legs.Count == 2 && !RollShape.IsSameStrikeCalendar(legs.Select(l => l.Symbol));
		if (splittable)
		{
			var trades = legs.Select(l =>
			{
				var legLimit = decimal.TryParse(l.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
					? p.ToString("F2", CultureInfo.InvariantCulture)
					: l.Price;
				return $"wa trade place --trade \"{l.Action}:{l.Symbol}:{l.Qty}\" --limit {legLimit}";
			}).ToList();
			return (trades, analyze);
		}

		// Combo line: legs without per-leg prices, net limit from CashImpactPerContract.
		var tradeLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}");
		var limit = Math.Abs(sc.CashImpactPerContract / 100m).ToString("F2", CultureInfo.InvariantCulture);
		var combo = $"wa trade place --trade \"{string.Join(",", tradeLegs)}\" --limit {limit}";
		return (new[] { combo }, analyze);
	}
}
