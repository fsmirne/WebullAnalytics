using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace WebullAnalytics;

// ─── `analyze position` ───────────────────────────────────────────────────────

internal sealed class AnalyzePositionSettings : AnalyzeSubcommandSettings
{
	[CommandArgument(0, "<spec>")]
	[Description("Open position. Format: ACTION:SYMBOL:QTY@PRICE,... where PRICE is your cost basis per leg. Example: sell:GME260424C00025000:499@0.48,buy:GME260515C00025000:499@1.11")]
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

		var legs = TradeLegParser.Parse(settings.Spec);
		var positionLegs = legs.Select(l => new PositionSnapshot(
			Symbol: l.Symbol,
			Action: l.Action,
			Qty: l.Quantity,
			CostBasis: l.Price!.Value,
			Parsed: l.Option!)).ToList();

		var ticker = positionLegs[0].Parsed.Root;
		var spot = ResolveSpot(ticker, settings);
		if (spot == null)
		{
			Console.Error.WriteLine($"Error: no underlying price for '{ticker}'. Pass --current-underlying-price {ticker}:<price>.");
			return 1;
		}

		var structure = ClassifyStructure(positionLegs);

		// Build the full list of OCC symbols we'll need quotes for: current legs plus every hypothetical
		// new leg we'll generate. Fetched once up front so the scenario generator has real IV and bid/ask.
		var symbolsNeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var l in positionLegs) symbolsNeeded.Add(l.Symbol);
		foreach (var s in EnumerateHypotheticalSymbols(positionLegs, structure, settings, spot.Value)) symbolsNeeded.Add(s);

		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
		if (!string.IsNullOrEmpty(settings.Api))
			quotes = await AnalyzeCommon.FetchQuotesForSymbolList(settings, symbolsNeeded.ToList(), cancellation);

		RenderHeader(positionLegs, structure, spot.Value);

		var scenarios = GenerateScenarios(positionLegs, structure, settings, spot.Value, EvaluationDate.Today, quotes);
		if (scenarios.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No scenarios defined yet for structure type '{structure}'. Phase 1 supports single-long and calendar/diagonal.[/]");
			return 0;
		}

		RenderScenarioTable(scenarios, settings.Cash);
		return 0;
	}

	// ─── Input model ──────────────────────────────────────────────────────────

	internal sealed record PositionSnapshot(string Symbol, LegAction Action, int Qty, decimal CostBasis, OptionParsed Parsed);

	internal enum StructureKind { SingleLong, SingleShort, Calendar, Diagonal, Vertical, Unsupported }

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
		string Rationale);

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
			var nearSpot = NearestStrike(spot, settings.StrikeStep);
			if (nearSpot > 0m && nearSpot != shortLeg.Parsed.Strike)
				yield return MatchKeys.OccSymbol(root, newExpiry, nearSpot, callPut);
			// Reset-at-spot new-calendar legs (short = newExpiry, long = same long expiry if later else newExpiry+21).
			var newLongExp = longLeg.Parsed.ExpiryDate > newExpiry ? longLeg.Parsed.ExpiryDate : newExpiry.AddDays(21);
			if (nearSpot > 0m)
				yield return MatchKeys.OccSymbol(root, newLongExp, nearSpot, callPut);
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

		// 1. Hold (do nothing) — value at expiry = intrinsic.
		var valueAtExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		list.Add(NewScenario("Hold to expiry", longLeg, "—",
			cashNow: 0m, valueAtTarget: valueAtExpiry, bpDeltaPerContract: 0m,
			rationale: $"value at expiry ({longLeg.Parsed.ExpiryDate:yyyy-MM-dd}) = intrinsic ${valueAtExpiry:F2}/share"));

		// 2. Close now at theoretical mid (or live mid if available).
		var dteNow = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var midNow = LiveOrBsMid(quotes, longLeg.Symbol, spot, longLeg.Parsed.Strike, dteNow, iv, callPut);
		list.Add(NewScenario("Close now", longLeg, $"SELL {longLeg.Symbol} x{longLeg.Qty}",
			cashNow: midNow, valueAtTarget: 0m, bpDeltaPerContract: 0m,
			rationale: $"sell at mid ${midNow:F2}/share → close position"));

		// 3. Convert to calendar: sell a near-expiry short at same strike.
		var shortExpiry = NextWeeklyFromToday();
		if (shortExpiry < longLeg.Parsed.ExpiryDate)
		{
			var newShortSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, shortExpiry, longLeg.Parsed.Strike, callPut);
			var ivNewShort = ResolveIV(newShortSym, settings, quotes);
			var dteShort = Math.Max(1, (shortExpiry - asOf).Days);
			var shortMid = LiveOrBsMid(quotes, newShortSym, spot, longLeg.Parsed.Strike, dteShort, ivNewShort, callPut);
			// Project value at short expiry: long BS value, short intrinsic.
			var dteLongAtShortExp = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days);
			var longAtShortExp = (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, dteLongAtShortExp / 365.0, 0.036, iv, callPut);
			var shortAtShortExp = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
			var net = longAtShortExp - shortAtShortExp;
			// BP delta: becomes a calendar (strike_loss = 0). Current is single long (no BP). Delta = 0.
			list.Add(NewScenario($"Convert to calendar (sell {shortExpiry:yyyy-MM-dd} @ ${longLeg.Parsed.Strike:F2})",
				longLeg, $"SELL {newShortSym} x{longLeg.Qty}",
				cashNow: shortMid, valueAtTarget: net, bpDeltaPerContract: 0m,
				rationale: $"collect ${shortMid:F2}/share short premium; at short exp: long ${longAtShortExp:F2} − short ${shortAtShortExp:F2} = ${net:F2}"));
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract).ToList();
	}

	private static List<Scenario> GenerateSpreadScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, StructureKind kind, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		var list = new List<Scenario>();
		var shortLeg = legs.First(l => l.Action == LegAction.Sell);
		var longLeg = legs.First(l => l.Action == LegAction.Buy);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, settings, quotes);
		var ivLong = ResolveIV(longLeg.Symbol, settings, quotes);

		var shortMidNow = LiveOrBsMid(quotes, shortLeg.Symbol, spot, shortLeg.Parsed.Strike, Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivShort, callPut);
		var longMidNow = LiveOrBsMid(quotes, longLeg.Symbol, spot, longLeg.Parsed.Strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days), ivLong, callPut);
		var (shortBidNow, shortAskNow) = LiveBidAsk(quotes, shortLeg.Symbol, shortMidNow);
		var (longBidNow, longAskNow) = LiveBidAsk(quotes, longLeg.Symbol, longMidNow);

		// Current BP (ongoing; covered-structure debit is sunk).
		var currentBp = AnalyzeCommon.ComputeLegMargin(shortLeg.Parsed, 1, spot, shortMidNow, longLeg.Parsed, null, 1, longMidNow, isExisting: true).Total;

		decimal LongValueAtShortExpiry(decimal longStrike, DateTime shortExpiry) =>
			(decimal)OptionMath.BlackScholes(spot, longStrike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days) / 365.0, 0.036, ivLong, callPut);

		// 1. Hold to short expiry.
		{
			var longAtExpiry = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
			var shortAtExpiry = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
			var net = longAtExpiry - shortAtExpiry;
			list.Add(NewScenarioSpread("Hold to short expiry", legs, "—",
				cashNow: 0m, valueAtTarget: net, bpDeltaPerContract: 0m,
				rationale: $"at {shortLeg.Parsed.ExpiryDate:yyyy-MM-dd}: long ${longAtExpiry:F2} − short ${shortAtExpiry:F2} intrinsic = ${net:F2}"));
		}

		// 2. Close short only (realistic: pay short ask).
		{
			var cash = -shortAskNow;
			var longAtExpiry = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
			// Post-action: single long. No short = no BP requirement.
			var bpDelta = 0m - currentBp;
			list.Add(NewScenarioSpread("Close short only", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty}",
				cashNow: cash, valueAtTarget: longAtExpiry, bpDeltaPerContract: bpDelta,
				rationale: $"pay ${shortAskNow:F2} ask to buy back short; keep long → ${longAtExpiry:F2}/share at short exp"));
		}

		// 3. Close all (sell long at bid, buy short at ask).
		{
			var cash = longBidNow - shortAskNow;
			var bpDelta = 0m - currentBp;
			list.Add(NewScenarioSpread("Close all", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {longLeg.Symbol} x{longLeg.Qty}",
				cashNow: cash, valueAtTarget: 0m, bpDeltaPerContract: bpDelta,
				rationale: $"sell long @${longBidNow:F2} bid, buy short @${shortAskNow:F2} ask → net ${cash:+0.00;-0.00}/share"));
		}

		// 4. Roll short same strike, next weekly.
		{
			var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
			if (newExp < longLeg.Parsed.ExpiryDate)
			{
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, shortLeg.Parsed.Strike, callPut);
				var ivNewShort = ResolveIV(newSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotes, newSym, spot, shortLeg.Parsed.Strike, dteNewShort, ivNewShort, callPut);
				var (newShortBid, _) = LiveBidAsk(quotes, newSym, newShortMidExec);
				var cash = newShortBid - shortAskNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
				var net = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, shortLeg.Parsed.Strike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var bpDelta = newBp - currentBp;
				list.Add(NewScenarioSpread($"Roll short to {newExp:yyyy-MM-dd} same strike", legs,
					$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {newSym} x{shortLeg.Qty}",
					cashNow: cash, valueAtTarget: net, bpDeltaPerContract: bpDelta,
					rationale: $"buy short @${shortAskNow:F2} ask, sell new @${newShortBid:F2} bid → net ${cash:+0.00;-0.00}; at exp: ${net:F2}"));
			}
		}

		// 5. Roll short to near-spot strike (calendar → diagonal or diagonal shift).
		{
			var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
			var newStrike = NearestStrike(spot, settings.StrikeStep);
			if (newExp < longLeg.Parsed.ExpiryDate && newStrike != shortLeg.Parsed.Strike && newStrike > 0m)
			{
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, newStrike, callPut);
				var ivNewShort = ResolveIV(newSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotes, newSym, spot, newStrike, dteNewShort, ivNewShort, callPut);
				var (newShortBid, _) = LiveBidAsk(quotes, newSym, newShortMidExec);
				var cash = newShortBid - shortAskNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var net = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newExp, callPut, newStrike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, longLeg.Parsed, null, 1, longMidNow, isExisting: false).Total;
				var bpDelta = newBp - currentBp;
				list.Add(NewScenarioSpread($"Roll short to {newExp:yyyy-MM-dd} ${newStrike:F2} strike (→ diagonal)", legs,
					$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {newSym} x{shortLeg.Qty}",
					cashNow: cash, valueAtTarget: net, bpDeltaPerContract: bpDelta,
					rationale: $"step short to ${newStrike:F2} (spot ${spot:F2}); credit ${cash:+0.00;-0.00}/share; at exp: ${net:F2}"));
			}
		}

		// 6. Close all and reopen a fresh calendar at near-spot strike.
		{
			var newStrike = NearestStrike(spot, settings.StrikeStep);
			var newShortExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
			var newLongExp = longLeg.Parsed.ExpiryDate > newShortExp ? longLeg.Parsed.ExpiryDate : newShortExp.AddDays(21);
			if (newStrike > 0m)
			{
				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newShortExp, newStrike, callPut);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newLongExp, newStrike, callPut);
				var ivNewShort = ResolveIV(newShortSym, settings, quotes);
				var ivNewLong = ResolveIV(newLongSym, settings, quotes);
				var dteNewShort = Math.Max(1, (newShortExp - asOf).Days);
				var dteNewLong = Math.Max(1, (newLongExp - asOf).Days);
				var newShortMidExec = LiveOrBsMid(quotes, newShortSym, spot, newStrike, dteNewShort, ivNewShort, callPut);
				var newLongMidExec = LiveOrBsMid(quotes, newLongSym, spot, newStrike, dteNewLong, ivNewLong, callPut);
				var (newShortBid, _) = LiveBidAsk(quotes, newShortSym, newShortMidExec);
				var (_, newLongAsk) = LiveBidAsk(quotes, newLongSym, newLongMidExec);
				var closeNet = longBidNow - shortAskNow;
				var openNet = newShortBid - newLongAsk;
				var cash = closeNet + openNet;
				var longAtNewShortExp = (decimal)OptionMath.BlackScholes(spot, newStrike, Math.Max(1, (newLongExp.Date - newShortExp.Date).Days) / 365.0, 0.036, ivNewLong, callPut);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var net = longAtNewShortExp - shortAtNewShortExp;
				var newShortParsed = new OptionParsed(shortLeg.Parsed.Root, newShortExp, callPut, newStrike);
				var newLongParsed = new OptionParsed(longLeg.Parsed.Root, newLongExp, callPut, newStrike);
				var newBp = AnalyzeCommon.ComputeLegMargin(newShortParsed, 1, spot, newShortMidExec, newLongParsed, null, 1, newLongMidExec, isExisting: false).Total;
				var bpDelta = newBp - currentBp;
				list.Add(NewScenarioSpread($"Reset at ${newStrike:F2} (new calendar)", legs,
					$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {longLeg.Symbol} x{longLeg.Qty}, BUY {newLongSym} x{shortLeg.Qty}, SELL {newShortSym} x{shortLeg.Qty}",
					cashNow: cash, valueAtTarget: net, bpDeltaPerContract: bpDelta,
					rationale: $"close net ${closeNet:+0.00;-0.00}, open new net ${openNet:+0.00;-0.00}; projected at new short exp: ${net:F2}"));
			}
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract).ToList();
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static Scenario NewScenario(string name, PositionSnapshot longLeg, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, string rationale)
	{
		var initialDebit = longLeg.CostBasis;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, longLeg.Qty, rationale);
	}

	private static Scenario NewScenarioSpread(string name, IReadOnlyList<PositionSnapshot> legs, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, string rationale)
	{
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var qty = legs[0].Qty;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, qty, rationale);
	}

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

	private static decimal? ResolveSpot(string ticker, AnalyzePositionSettings settings)
	{
		if (string.IsNullOrEmpty(settings.CurrentUnderlyingPrice)) return null;
		foreach (var entry in settings.CurrentUnderlyingPrice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = entry.Split(':');
			if (parts.Length == 2 && parts[0].Equals(ticker, StringComparison.OrdinalIgnoreCase)
				&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
				return p;
		}
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

	private static void RenderScenarioTable(IReadOnlyList<Scenario> scenarios, decimal? availableCash)
	{
		var table = new Table().Expand();
		table.AddColumn("Scenario");
		table.AddColumn(new TableColumn("Cash now").RightAligned());
		table.AddColumn(new TableColumn("Projected @ target").RightAligned());
		table.AddColumn(new TableColumn("Total P&L").RightAligned());
		table.AddColumn(new TableColumn("BP Impact").RightAligned());
		table.AddColumn("Rationale");

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
			var rowStyle = isRecommended ? "bold green" : (fundable ? "" : "dim");

			var totalTotal = sc.TotalPnLPerContract * sc.Qty;
			var totalStr = $"${sc.TotalPnLPerContract:F2}/contract → {(totalTotal >= 0 ? "+" : "-")}${Math.Abs(totalTotal):N2} total";
			var cashStr = $"${sc.CashImpactPerContract:+0.00;-0.00}/contract";
			var projStr = $"${sc.ProjectedValuePerContract:F2}/contract";
			var bpStr = bpTotal == 0m
				? "[dim]no change[/]"
				: bpTotal < 0m
					? $"[green]{bpTotal:+$0;-$0;0} frees up[/]"
					: (availableCash.HasValue && bpTotal > availableCash.Value
						? $"[red]+${bpTotal:N2} (NEEDS ${bpTotal - availableCash.Value:N2} MORE)[/]"
						: $"[yellow]+${bpTotal:N2}[/]");
			var nameMarkup = !fundable
				? $"[dim]{Markup.Escape(sc.Name)} [red](not fundable)[/][/]"
				: isRecommended
					? $"[{rowStyle}]★ {Markup.Escape(sc.Name)}[/]"
					: $"[{rowStyle}]{Markup.Escape(sc.Name)}[/]";
			var totalMarkup = $"[{rowStyle}]{Markup.Escape(totalStr)}[/]";

			table.AddRow(
				new Markup(nameMarkup),
				new Markup(cashStr),
				new Markup(projStr),
				new Markup(totalMarkup),
				new Markup(bpStr),
				new Markup($"[dim]{Markup.Escape(sc.Rationale)}[/]"));
		}

		AnsiConsole.Write(table);
		if (availableCash.HasValue)
			AnsiConsole.MarkupLine($"[dim]Fundability check: available cash/BP = ${availableCash.Value:N2}. Scenarios exceeding this are dimmed.[/]");
	}
}
