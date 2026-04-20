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
	[Description("Fallback implied volatility for projecting hypothetical legs when per-symbol IV is unavailable. Percent, default 40.")]
	public decimal IvDefault { get; set; } = 40m;

	[CommandOption("--strike-step")]
	[Description("Strike increment used for near-spot scenarios. Default 0.50.")]
	public decimal StrikeStep { get; set; } = 0.50m;

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

		await Task.CompletedTask; // placeholder for future live-quote integration

		var structure = ClassifyStructure(positionLegs);

		RenderHeader(positionLegs, structure, spot.Value);

		var scenarios = GenerateScenarios(positionLegs, structure, settings, spot.Value, EvaluationDate.Today);
		if (scenarios.Count == 0)
		{
			AnsiConsole.MarkupLine($"[yellow]No scenarios defined yet for structure type '{structure}'. Phase 1 supports single-long and calendar/diagonal.[/]");
			return 0;
		}

		RenderScenarioTable(scenarios);
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
		int Qty,
		string Rationale);

	private static List<Scenario> GenerateScenarios(
		List<PositionSnapshot> legs, StructureKind kind,
		AnalyzePositionSettings settings, decimal spot, DateTime asOf) =>
		kind switch
		{
			StructureKind.SingleLong => GenerateSingleLongScenarios(legs[0], settings, spot, asOf),
			StructureKind.Calendar or StructureKind.Diagonal => GenerateSpreadScenarios(legs, settings, spot, asOf, kind),
			_ => new List<Scenario>()
		};

	private static List<Scenario> GenerateSingleLongScenarios(PositionSnapshot longLeg, AnalyzePositionSettings settings, decimal spot, DateTime asOf)
	{
		var list = new List<Scenario>();
		var iv = ResolveIV(longLeg.Symbol, settings) / 100m;
		var callPut = longLeg.Parsed.CallPut;

		// 1. Hold (do nothing) — value at expiry = intrinsic.
		var valueAtExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
		list.Add(NewScenario("Hold to expiry", longLeg, "—",
			cashNow: 0m,
			valueAtTarget: valueAtExpiry,
			rationale: $"value at expiry ({longLeg.Parsed.ExpiryDate:yyyy-MM-dd}) = intrinsic ${valueAtExpiry:F2}/share"));

		// 2. Close now at theoretical mid.
		var dteNow = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days);
		var midNow = (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, dteNow / 365.0, 0.036, iv, callPut);
		list.Add(NewScenario("Close now", longLeg, $"SELL {longLeg.Symbol} x{longLeg.Qty}",
			cashNow: midNow,
			valueAtTarget: 0m,
			rationale: $"BS mid at spot ${spot:F2}, IV {iv * 100m:F0}%, DTE {dteNow} → ${midNow:F2}/share"));

		// 3. Convert to calendar: sell a near-expiry short at same strike (if we can find a suitable short).
		var shortExpiry = asOf.AddDays(7);
		while (shortExpiry.DayOfWeek != DayOfWeek.Friday) shortExpiry = shortExpiry.AddDays(1);
		if (shortExpiry < longLeg.Parsed.ExpiryDate)
		{
			var dteShort = Math.Max(1, (shortExpiry - asOf).Days);
			var shortMid = (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, dteShort / 365.0, 0.036, iv, callPut);
			var newShortSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, shortExpiry, longLeg.Parsed.Strike, callPut);
			// Project value at short expiry: long leg BS value with reduced DTE, short leg intrinsic.
			var dteLongAtShortExpiry = Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days);
			var longAtShortExpiry = (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, dteLongAtShortExpiry / 365.0, 0.036, iv, callPut);
			var shortAtShortExpiry = Intrinsic(spot, longLeg.Parsed.Strike, callPut);
			var net = longAtShortExpiry - shortAtShortExpiry;
			list.Add(NewScenario($"Convert to calendar (sell {shortExpiry:yyyy-MM-dd} @ ${longLeg.Parsed.Strike:F2})",
				longLeg, $"SELL {newShortSym} x{longLeg.Qty}",
				cashNow: shortMid,
				valueAtTarget: net,
				rationale: $"collect ${shortMid:F2}/share short premium; at short exp: long ${longAtShortExpiry:F2} − short ${shortAtShortExpiry:F2} = ${net:F2}"));
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract).ToList();
	}

	private static List<Scenario> GenerateSpreadScenarios(List<PositionSnapshot> legs, AnalyzePositionSettings settings, decimal spot, DateTime asOf, StructureKind kind)
	{
		var list = new List<Scenario>();
		var shortLeg = legs.First(l => l.Action == LegAction.Sell);
		var longLeg = legs.First(l => l.Action == LegAction.Buy);
		var callPut = shortLeg.Parsed.CallPut;
		var ivShort = ResolveIV(shortLeg.Symbol, settings) / 100m;
		var ivLong = ResolveIV(longLeg.Symbol, settings) / 100m;

		decimal ShortMidNow() => (decimal)OptionMath.BlackScholes(spot, shortLeg.Parsed.Strike, Math.Max(1, (shortLeg.Parsed.ExpiryDate.Date - asOf.Date).Days) / 365.0, 0.036, ivShort, callPut);
		decimal LongMidNow() => (decimal)OptionMath.BlackScholes(spot, longLeg.Parsed.Strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - asOf.Date).Days) / 365.0, 0.036, ivLong, callPut);
		decimal LongValueAtShortExpiry(decimal strike, DateTime shortExpiry) =>
			(decimal)OptionMath.BlackScholes(spot, strike, Math.Max(1, (longLeg.Parsed.ExpiryDate.Date - shortExpiry.Date).Days) / 365.0, 0.036, ivLong, callPut);

		var shortNow = ShortMidNow();
		var longNow = LongMidNow();

		// 1. Hold to short expiry.
		{
			var longAtExpiry = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
			var shortAtExpiry = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
			var net = longAtExpiry - shortAtExpiry;
			list.Add(NewScenarioSpread("Hold to short expiry", legs, "—",
				cashNow: 0m, valueAtTarget: net,
				rationale: $"at {shortLeg.Parsed.ExpiryDate:yyyy-MM-dd}: long ${longAtExpiry:F2} − short ${shortAtExpiry:F2} intrinsic = ${net:F2}"));
		}

		// 2. Close short only.
		{
			var cash = -shortNow;
			var longAtExpiry = LongValueAtShortExpiry(longLeg.Parsed.Strike, shortLeg.Parsed.ExpiryDate);
			list.Add(NewScenarioSpread("Close short only", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty}",
				cashNow: cash, valueAtTarget: longAtExpiry,
				rationale: $"pay ${shortNow:F2}/share to buy back short; keep long → ${longAtExpiry:F2}/share at short exp"));
		}

		// 3. Close all.
		{
			var cash = longNow - shortNow;
			list.Add(NewScenarioSpread("Close all", legs,
				$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {longLeg.Symbol} x{longLeg.Qty}",
				cashNow: cash, valueAtTarget: 0m,
				rationale: $"sell long ${longNow:F2}, buy short ${shortNow:F2} → net ${cash:+0.00;-0.00}/share now"));
		}

		// 4. Roll short same strike, next weekly.
		{
			var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
			if (newExp < longLeg.Parsed.ExpiryDate)
			{
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMid = (decimal)OptionMath.BlackScholes(spot, shortLeg.Parsed.Strike, dteNewShort / 365.0, 0.036, ivShort, callPut);
				var cash = newShortMid - shortNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, shortLeg.Parsed.Strike, callPut);
				var net = longAtNewShortExp - shortAtNewShortExp;
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, shortLeg.Parsed.Strike, callPut);
				list.Add(NewScenarioSpread($"Roll short to {newExp:yyyy-MM-dd} same strike", legs,
					$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {newSym} x{shortLeg.Qty}",
					cashNow: cash, valueAtTarget: net,
					rationale: $"close ${shortNow:F2}, open new short ${newShortMid:F2} → credit ${cash:+0.00;-0.00}; at new exp: long ${longAtNewShortExp:F2} − short ${shortAtNewShortExp:F2}"));
			}
		}

		// 5. Roll short to strike closest to spot (converts calendar → diagonal or shifts diagonal).
		{
			var newExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
			var newStrike = NearestStrike(spot, settings.StrikeStep);
			if (newExp < longLeg.Parsed.ExpiryDate && newStrike != shortLeg.Parsed.Strike && newStrike > 0m)
			{
				var dteNewShort = Math.Max(1, (newExp - asOf).Days);
				var newShortMid = (decimal)OptionMath.BlackScholes(spot, newStrike, dteNewShort / 365.0, 0.036, ivShort, callPut);
				var cash = newShortMid - shortNow;
				var longAtNewShortExp = LongValueAtShortExpiry(longLeg.Parsed.Strike, newExp);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var net = longAtNewShortExp - shortAtNewShortExp;
				var newSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newExp, newStrike, callPut);
				list.Add(NewScenarioSpread($"Roll short to {newExp:yyyy-MM-dd} ${newStrike:F2} strike (→ diagonal)", legs,
					$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {newSym} x{shortLeg.Qty}",
					cashNow: cash, valueAtTarget: net,
					rationale: $"step new short to ${newStrike:F2} (near spot ${spot:F2}); credit ${cash:+0.00;-0.00}/share; at exp: ${net:F2}"));
			}
		}

		// 6. Close all and reopen a fresh calendar at near-spot strike.
		{
			var newStrike = NearestStrike(spot, settings.StrikeStep);
			var newShortExp = NextWeekly(shortLeg.Parsed.ExpiryDate);
			var newLongExp = longLeg.Parsed.ExpiryDate > newShortExp ? longLeg.Parsed.ExpiryDate : newShortExp.AddDays(21);
			if (newStrike > 0m)
			{
				var dteNewShort = Math.Max(1, (newShortExp - asOf).Days);
				var dteNewLong = Math.Max(1, (newLongExp - asOf).Days);
				var newShortMid = (decimal)OptionMath.BlackScholes(spot, newStrike, dteNewShort / 365.0, 0.036, ivShort, callPut);
				var newLongMid = (decimal)OptionMath.BlackScholes(spot, newStrike, dteNewLong / 365.0, 0.036, ivLong, callPut);
				var closeNet = longNow - shortNow;
				var openNet = newShortMid - newLongMid;
				var cash = closeNet + openNet;
				var longAtNewShortExp = (decimal)OptionMath.BlackScholes(spot, newStrike, Math.Max(1, (newLongExp.Date - newShortExp.Date).Days) / 365.0, 0.036, ivLong, callPut);
				var shortAtNewShortExp = Intrinsic(spot, newStrike, callPut);
				var net = longAtNewShortExp - shortAtNewShortExp;
				var newShortSym = MatchKeys.OccSymbol(shortLeg.Parsed.Root, newShortExp, newStrike, callPut);
				var newLongSym = MatchKeys.OccSymbol(longLeg.Parsed.Root, newLongExp, newStrike, callPut);
				list.Add(NewScenarioSpread($"Reset at ${newStrike:F2} (new calendar)", legs,
					$"BUY {shortLeg.Symbol} x{shortLeg.Qty}, SELL {longLeg.Symbol} x{longLeg.Qty}, BUY {newLongSym} x{shortLeg.Qty}, SELL {newShortSym} x{shortLeg.Qty}",
					cashNow: cash, valueAtTarget: net,
					rationale: $"close (${closeNet:+0.00;-0.00}), open new cal (debit ${-openNet:F2}); projected at new short exp: ${net:F2}"));
			}
		}

		return list.OrderByDescending(s => s.TotalPnLPerContract).ToList();
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static Scenario NewScenario(string name, PositionSnapshot longLeg, string actionSummary, decimal cashNow, decimal valueAtTarget, string rationale)
	{
		var initialDebit = longLeg.CostBasis; // single long: paid CostBasis per share
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, longLeg.Qty, rationale);
	}

	private static Scenario NewScenarioSpread(string name, IReadOnlyList<PositionSnapshot> legs, string actionSummary, decimal cashNow, decimal valueAtTarget, string rationale)
	{
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
		var qty = legs[0].Qty;
		var cashPerContract = cashNow * 100m;
		var valuePerContract = valueAtTarget * 100m;
		var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
		return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, qty, rationale);
	}

	private static decimal ResolveIV(string symbol, AnalyzePositionSettings settings)
	{
		if (!string.IsNullOrEmpty(settings.IvOverrides))
		{
			foreach (var entry in settings.IvOverrides.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = entry.Split(':');
				if (parts.Length == 2 && parts[0].Equals(symbol, StringComparison.OrdinalIgnoreCase)
					&& decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var iv))
					return iv;
			}
		}
		return settings.IvDefault;
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

	private static void RenderScenarioTable(IReadOnlyList<Scenario> scenarios)
	{
		var table = new Table().Expand();
		table.AddColumn("Scenario");
		table.AddColumn(new TableColumn("Cash now").RightAligned());
		table.AddColumn(new TableColumn("Projected @ target").RightAligned());
		table.AddColumn(new TableColumn("Total P&L").RightAligned());
		table.AddColumn("Rationale");

		bool first = true;
		foreach (var sc in scenarios)
		{
			var style = first ? "bold green" : "";
			var totalTotal = sc.TotalPnLPerContract * sc.Qty;
			var sign = totalTotal >= 0 ? "+" : "-";
			var totalStr = $"${sc.TotalPnLPerContract:F2}/contract → {sign}${Math.Abs(totalTotal):N2} total";
			var cashStr = $"${sc.CashImpactPerContract:+0.00;-0.00}/contract";
			var projStr = $"${sc.ProjectedValuePerContract:F2}/contract";
			table.AddRow(
				new Markup($"[{style}]{Markup.Escape(sc.Name)}[/]"),
				new Markup(cashStr),
				new Markup(projStr),
				new Markup($"[{style}]{Markup.Escape(totalStr)}[/]"),
				new Markup($"[dim]{Markup.Escape(sc.Rationale)}[/]"));
			first = false;
		}

		AnsiConsole.Write(table);
	}
}
