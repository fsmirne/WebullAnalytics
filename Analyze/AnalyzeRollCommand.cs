using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.ComponentModel;
using System.Globalization;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.IO;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Report;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

// --- `analyze roll` -----------------------------------------------------------

internal sealed class AnalyzeRollSettings : AnalyzeIvOverrideSettings
{
	internal static readonly string[] ValidSortModes = ["ev", "pop", "credit", "natural", "credit-day", "margin", "exp", "strike"];
	private static readonly string[] ValidPricingModes = [SuggestionPricing.Mid, SuggestionPricing.BidAsk];

	[CommandArgument(0, "[spec]")]
	[Description("Position to roll. Format: ACTION:SYMBOL:QTY (with optional @PRICE, defaulting to MID), comma-separated — same as analyze position / analyze trade. Omit to select from open positions interactively.")]
	public string Spec { get; set; } = "";

	[CommandOption("--leg <SELECTOR>")]
	[Description("Option leg to roll: an OCC symbol, 'short', or 'long'. Defaults to the short leg (or the sole option leg) in non-interactive mode; prompted when omitted interactively on multi-leg positions.")]
	public string? Leg { get; set; }

	[CommandOption("--to <SYMBOL>")]
	[Description("Target OCC option symbol. Alone (without --strikes/--exp), renders the price-by-time roll-credit grid directly. Combined with --strikes/--exp, anchors the chain sweep on this contract's strike & expiry.")]
	public string? To { get; set; }

	[CommandOption("--strikes <N>")]
	[Description("Compare ±N listed strikes around the anchor strike on the option chain. Default: 2 (prompted in interactive mode when omitted).")]
	public int? Strikes { get; set; }

	[CommandOption("--exp <N>")]
	[Description("Compare ±N listed expirations around the anchor expiry on the option chain, clamped at today. Default: 2 (prompted in interactive mode when omitted).")]
	public int? Exp { get; set; }

	[CommandOption("--credit-only")]
	[Description("Show only candidate rolls with net credit ≥ 0 at mid.")]
	public bool CreditOnly { get; set; }

	[CommandOption("--sort <METRIC>")]
	[DefaultValue("ev")]
	[Description("Sort order: ev (default; managed EV, fallback to credit-day), pop, credit, natural, credit-day, margin, exp, strike.")]
	public string Sort { get; set; } = "ev";

	[CommandOption("--top <N>")]
	[DefaultValue(15)]
	[Description("Maximum candidate rows to display (0 = all). Default: 15.")]
	public int Top { get; set; } = 15;

	[CommandOption("--cash <AMOUNT>")]
	[Description("Available cash/BP for funding checks (auto-detected from --account when selecting an open position).")]
	public decimal? Cash { get; set; }

	[CommandOption("--account <ALIAS>")]
	[Description("Account alias or ID from api-config.json used to auto-detect cash/BP when selecting an open position.")]
	public string? Account { get; set; }

	[CommandOption("--range <VALUE>")]
	[DefaultValue(0.0)]
	[Description("Grid granularity for break-even analysis and the price-by-time roll-credit grid. Default 0 = auto.")]
	public decimal Range { get; set; } = 0;

	[CommandOption("--levels <SPEC>")]
	[Description("Additional reference price levels for the price-by-time roll-credit grid. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30).")]
	public string? Levels { get; set; }

	[CommandOption("--pricing <MODE>")]
	[DefaultValue(SuggestionPricing.Mid)]
	[Description("Price basis for roll credits, funding checks, and generated commands: mid (default) or bidask.")]
	public string Pricing { get; set; } = SuggestionPricing.Mid;

	internal bool UseBidAsk => SuggestionPricing.Normalize(Pricing) == SuggestionPricing.BidAsk;

	[CommandOption("--calibrated")]
	[DefaultValue(true)]
	[Description("ON by default: back-solve each leg's IV from its live bid/ask mid before scoring and pricing. Pass --calibrated false to use raw vendor IV.")]
	public bool Calibrated { get; set; } = true;

	internal override void ApplyConfig(Dictionary<string, System.Text.Json.JsonElement> cfg)
	{
		base.ApplyConfig(cfg);
		if (!Program.HasCliOption("calibrated") && cfg.TryGetBool("calibrated", out var calibrated)) Calibrated = calibrated;
	}

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (!string.IsNullOrWhiteSpace(Spec))
		{
			List<ParsedLeg> legs;
			try { legs = TradeLegParser.Parse(Spec); }
			catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }

			var optionLegs = legs.Where(l => l.Option != null).ToList();
			if (optionLegs.Count == 0)
				return ValidationResult.Error("<spec>: at least one OCC option leg is required");

			var firstOpt = optionLegs[0].Option!;
			foreach (var leg in optionLegs.Skip(1))
			{
				if (!RootsShareBook(firstOpt.Root, leg.Option!.Root, firstOpt.ExpiryDate, leg.Option.ExpiryDate))
					return ValidationResult.Error($"<spec>: all option legs must share an underlying (found '{firstOpt.Root}' and '{leg.Option.Root}')");
			}
		}

		if (Leg != null)
		{
			var trimmed = Leg.Trim();
			var isKeyword = trimmed.Equals("short", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("long", StringComparison.OrdinalIgnoreCase);
			if (!isKeyword && ParsingHelpers.ParseOptionSymbol(trimmed) == null)
				return ValidationResult.Error($"--leg: must be 'short', 'long', or a valid OCC option symbol, got '{Leg}'");
		}

		if (To != null && ParsingHelpers.ParseOptionSymbol(To.Trim()) == null)
			return ValidationResult.Error($"--to: invalid OCC option symbol '{To}'");

		if (Strikes.HasValue && Strikes.Value < 0)
			return ValidationResult.Error($"--strikes: must be ≥ 0, got {Strikes.Value}");

		if (Exp.HasValue && Exp.Value < 0)
			return ValidationResult.Error($"--exp: must be ≥ 0, got {Exp.Value}");

		if (!ValidSortModes.Contains(Sort, StringComparer.OrdinalIgnoreCase))
			return ValidationResult.Error($"--sort: must be one of {string.Join("|", ValidSortModes)}, got '{Sort}'");

		if (!ValidPricingModes.Contains(Pricing, StringComparer.OrdinalIgnoreCase))
			return ValidationResult.Error($"--pricing: must be mid|bidask, got '{Pricing}'");

		if (Top < 0)
			return ValidationResult.Error($"--top: must be ≥ 0, got {Top}");

		if (Cash.HasValue && Cash.Value < 0m)
			return ValidationResult.Error($"--cash: must be non-negative, got {Cash.Value}");

		if (Range < 0)
			return ValidationResult.Error("--range must be 0 (auto) or a positive number");

		if (Levels != null)
		{
			foreach (var pair in Levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = pair.Split(':', 2);
				if (parts.Length != 2)
					return ValidationResult.Error($"--levels: invalid entry '{pair}'. Expected format: TICKER:P1/P2/P3 (e.g., GME:20/25/30)");
				foreach (var priceStr in parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					if (!decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
						return ValidationResult.Error($"--levels: invalid price '{priceStr}' for ticker '{parts[0].Trim()}'. Prices must be numeric.");
				}
			}
		}

		return ValidationResult.Success();
	}

	internal static bool RootsShareBook(string rootA, string rootB, DateTime expiryA, DateTime expiryB) =>
		ParsingHelpers.RootsMatchForAggregation(rootA, rootB, expiryA) || ParsingHelpers.RootsMatchForAggregation(rootA, rootB, expiryB);
}

internal sealed record RollPositionLeg(
	string Symbol,
	LegAction Action,
	int Qty,
	OptionParsed? Option,
	decimal? ExplicitPrice,
	string? PriceKeyword);

internal sealed record RollCandidate(
	string Symbol,
	OptionParsed Parsed,
	OptionContractQuote Quote,
	bool IsAnchor,
	bool IsSnappedStrike = false);

internal sealed record RollCandidateRow(
	string Symbol,
	OptionParsed Parsed,
	bool IsCurrent,
	bool IsAnchor,
	bool IsInverted,
	bool IsSnappedStrike,
	int AddedDte,
	decimal? RollNetMid,
	decimal? RollNatural,
	decimal? CreditPerDay,
	decimal? DeltaChange,
	decimal? Margin,
	decimal? BpDelta,
	decimal? MaxProfit,
	decimal? MaxProfitPrice,
	decimal? MaxLoss,
	IReadOnlyList<decimal> BreakEvens,
	decimal? ProbabilityOfProfit,
	decimal? HoldExpectedValue,
	decimal? ManagedExpectedValue);

/// <summary>Enumerates listed option contracts within ±<paramref name="strikesN"/> strikes and ±<paramref name="expN"/>
/// expirations of an anchor contract on the live (or historical) chain, clamping earlier expiries at <paramref name="today"/>.</summary>
internal static class RollCandidateEnumerator
{
	internal static (List<RollCandidate> Candidates, int SkippedIlliquid) Enumerate(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		OptionParsed oldLeg,
		string oldSymbol,
		OptionParsed anchor,
		string? anchorSymbol,
		LegAction rolledAction,
		int strikesN,
		int expN,
		DateTime today,
		decimal spot)
	{
		var candidates = new List<RollCandidate>();
		var skippedIlliquid = 0;
		foreach (var (sym, parsed, quote, isSnapped) in Window(quotes, oldLeg, anchor, strikesN, expN, today))
		{
			if (string.Equals(sym, oldSymbol, StringComparison.OrdinalIgnoreCase)) continue;
			if (!IsLiquidEnough(quote, parsed, spot, rolledAction))
			{
				skippedIlliquid++;
				continue;
			}
			var isAnchor = anchorSymbol != null && string.Equals(sym, anchorSymbol, StringComparison.OrdinalIgnoreCase);
			candidates.Add(new RollCandidate(sym, parsed, quote, isAnchor, isSnapped));
		}
		return (candidates, skippedIlliquid);
	}

	/// <summary>Every listed contract (same right and book as <paramref name="oldLeg"/>, whatever its quote) ±<paramref name="strikesN"/> listed strikes
	/// and ±<paramref name="expN"/> listed expirations around <paramref name="anchor"/>, expiries clamped at <paramref name="today"/>. IsSnapped marks
	/// the nearest strike standing in for an anchor strike the expiry doesn't list. A chain that lists contracts as quote-less stubs (Webull past the
	/// front expiry) still yields them, so a caller can fetch their quotes before <see cref="Enumerate"/> filters them.</summary>
	internal static IEnumerable<(string Symbol, OptionParsed Parsed, OptionContractQuote Quote, bool IsSnapped)> Window(
		IReadOnlyDictionary<string, OptionContractQuote> quotes, OptionParsed oldLeg, OptionParsed anchor, int strikesN, int expN, DateTime today)
	{
		var byExpiry = new SortedDictionary<DateTime, List<(string Symbol, OptionParsed Parsed, OptionContractQuote Quote)>>();
		foreach (var (sym, quote) in quotes)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;
			if (!string.Equals(parsed.CallPut, oldLeg.CallPut, StringComparison.OrdinalIgnoreCase)) continue;
			if (!AnalyzeRollSettings.RootsShareBook(parsed.Root, oldLeg.Root, parsed.ExpiryDate, oldLeg.ExpiryDate)) continue;
			if (parsed.ExpiryDate.Date < today.Date) continue;

			if (!byExpiry.TryGetValue(parsed.ExpiryDate.Date, out var list))
			{
				list = new List<(string, OptionParsed, OptionContractQuote)>();
				byExpiry[parsed.ExpiryDate.Date] = list;
			}
			list.Add((sym, parsed, quote));
		}
		if (byExpiry.Count == 0) yield break;

		var expiries = byExpiry.Keys.ToList();
		var anchorExpIdx = FindNearestIndex(expiries, anchor.ExpiryDate.Date, (a, b) => Math.Abs((a - b).TotalDays));
		var minExpIdx = Math.Max(0, anchorExpIdx - expN);
		var maxExpIdx = Math.Min(expiries.Count - 1, anchorExpIdx + expN);

		for (int ei = minExpIdx; ei <= maxExpIdx; ei++)
		{
			// Deduplicate same strike at the same expiry (prefer exact root match, e.g. SPXW vs SPX)
			var byStrike = byExpiry[expiries[ei]]
				.GroupBy(c => c.Parsed.Strike)
				.OrderBy(g => g.Key)
				.Select(g => g.OrderByDescending(c => string.Equals(c.Parsed.Root, anchor.Root, StringComparison.OrdinalIgnoreCase)).First())
				.ToList();

			var strikes = byStrike.Select(c => c.Parsed.Strike).ToList();
			var anchorStrikeIdx = FindNearestIndex(strikes, anchor.Strike, (a, b) => (double)Math.Abs(a - b));
			var anchorStrikeExists = byStrike[anchorStrikeIdx].Parsed.Strike == anchor.Strike;
			var minStrikeIdx = Math.Max(0, anchorStrikeIdx - strikesN);
			var maxStrikeIdx = Math.Min(byStrike.Count - 1, anchorStrikeIdx + strikesN);
			for (int si = minStrikeIdx; si <= maxStrikeIdx; si++)
				yield return (byStrike[si].Symbol, byStrike[si].Parsed, byStrike[si].Quote, !anchorStrikeExists && si == anchorStrikeIdx);
		}
	}

	/// <summary>A candidate is usable when it has a two-sided quote (bid and ask), the execution side is non-zero,
	/// it is not a dead zero-OI/zero-volume contract, and its spread isn't grossly wide: on a $1+ option, wider than 15% of its extrinsic value
	/// (<see cref="OptionMath.QuoteSpreadFraction"/>). A mid inside a spread wider than that is noise the scorer books as edge — 2026-11-23
	/// pre-open, selling the 12-11 660C (65.67/75.70, IV 34% beside the 731C's 22%) against a 12-11 731C ranked first on a +$423/contract EV
	/// that was all the rich mid.</summary>
	internal static bool IsLiquidEnough(OptionContractQuote quote, OptionParsed parsed, decimal spot, LegAction rolledAction)
	{
		if (quote.Bid is not decimal bid || quote.Ask is not decimal ask) return false;
		if (ask <= 0m) return false;
		if (rolledAction == LegAction.Sell && bid <= 0m) return false;
		if (quote.OpenInterest is 0 && (quote.Volume ?? 0) == 0) return false;
		if ((bid + ask) / 2m >= 1.00m && OptionMath.QuoteSpreadFraction(bid, ask, parsed, spot) > 0.15m) return false;
		return true;
	}

	private static int FindNearestIndex<T>(IReadOnlyList<T> sorted, T target, Func<T, T, double> distance)
	{
		var bestIdx = 0;
		var bestDist = double.MaxValue;
		for (int i = 0; i < sorted.Count; i++)
		{
			var d = distance(sorted[i], target);
			if (d < bestDist) { bestDist = d; bestIdx = i; }
		}
		return bestIdx;
	}
}

internal static class RollComparison
{
	internal static List<RollCandidateRow> FilterAndSort(IReadOnlyList<RollCandidateRow> candidates, bool creditOnly, string sortMode, int top, bool useBidAsk = false)
	{
		IEnumerable<RollCandidateRow> query = candidates;
		if (creditOnly)
			query = query.Where(r => (useBidAsk ? r.RollNatural : r.RollNetMid) is decimal net && net >= 0m);

		var list = query.ToList();
		var hasAnyEv = list.Any(r => r.ManagedExpectedValue.HasValue || r.HoldExpectedValue.HasValue);

		IOrderedEnumerable<RollCandidateRow> ordered = sortMode.ToLowerInvariant() switch
		{
			"ev" when hasAnyEv => list
				.OrderByDescending(r => r.ManagedExpectedValue.HasValue || r.HoldExpectedValue.HasValue)
				.ThenByDescending(r => r.ManagedExpectedValue ?? r.HoldExpectedValue ?? decimal.MinValue)
				.ThenByDescending(r => r.CreditPerDay ?? r.RollNetMid ?? decimal.MinValue),
			"ev" => list
				.OrderByDescending(r => r.CreditPerDay.HasValue)
				.ThenByDescending(r => r.CreditPerDay ?? decimal.MinValue)
				.ThenByDescending(r => r.RollNetMid ?? decimal.MinValue),
			"pop" => list
				.OrderByDescending(r => r.ProbabilityOfProfit.HasValue)
				.ThenByDescending(r => r.ProbabilityOfProfit ?? decimal.MinValue)
				.ThenByDescending(r => r.RollNetMid ?? decimal.MinValue),
			"credit" => list
				.OrderByDescending(r => r.RollNetMid ?? decimal.MinValue)
				.ThenByDescending(r => r.RollNatural ?? decimal.MinValue),
			"natural" => list
				.OrderByDescending(r => r.RollNatural ?? decimal.MinValue)
				.ThenByDescending(r => r.RollNetMid ?? decimal.MinValue),
			"credit-day" => list
				.OrderByDescending(r => r.CreditPerDay.HasValue)
				.ThenByDescending(r => r.CreditPerDay ?? decimal.MinValue)
				.ThenByDescending(r => r.RollNetMid ?? decimal.MinValue),
			"margin" => list
				.OrderBy(r => r.Margin ?? decimal.MaxValue)
				.ThenByDescending(r => r.RollNetMid ?? decimal.MinValue),
			"exp" => list
				.OrderBy(r => r.Parsed.ExpiryDate)
				.ThenBy(r => r.Parsed.Strike),
			"strike" => list
				.OrderBy(r => r.Parsed.Strike)
				.ThenBy(r => r.Parsed.ExpiryDate),
			_ => list.OrderByDescending(r => r.RollNetMid ?? decimal.MinValue)
		};

		var sorted = ordered.ToList();
		return top > 0 && sorted.Count > top ? sorted.Take(top).ToList() : sorted;
	}

	internal static void Render(
		IAnsiConsole console,
		string ticker,
		decimal spot,
		RollPositionLeg rolledLeg,
		IReadOnlyList<RollPositionLeg> positionLegs,
		RollCandidateRow currentRow,
		IReadOnlyList<RollCandidateRow> candidates,
		int totalEvaluated,
		int skippedIlliquid,
		int strikesN,
		int expN,
		decimal anchorStrike,
		decimal? cash,
		bool creditOnly,
		bool useBidAsk,
		string sortMode,
		bool ascii)
	{
		var table = new Table { Expand = true }.Border(ascii ? TableBorder.Ascii : TableBorder.Rounded);
		table.AddColumn("Target");
		foreach (var header in new[] { "Roll Net", "Natural", "Cr/Day", "Delta Δ", "BP Δ", "Margin", "Max profit", "Max loss", "Break-evens", "POP", "EV hold", "EV managed" })
			table.AddColumn(new TableColumn(header).RightAligned());

		// Pinned row 0: Hold (Current)
		table.AddRow(BuildCells(currentRow, cash, rolledLeg.Qty, useBidAsk, ascii));

		foreach (var row in candidates)
			table.AddRow(BuildCells(row, cash, rolledLeg.Qty, useBidAsk, ascii));

		var posDesc = DescribePosition(positionLegs);
		var rolledDesc = $"{(rolledLeg.Action == LegAction.Sell ? "SELL" : "BUY")} {Formatters.FormatQty(rolledLeg.Option!.Strike)}{rolledLeg.Option.CallPut} {rolledLeg.Option.ExpiryDate.ToString("dd MMM", CultureInfo.InvariantCulture)} x{rolledLeg.Qty}";
		var sweepNote = $"±{strikesN} strikes, ±{expN} exp";
		var countNote = candidates.Count < totalEvaluated ? $"showing {candidates.Count} of {totalEvaluated}" : $"{candidates.Count} candidates";
		var extraNotes = new List<string>();
		if (useBidAsk) extraNotes.Add("pricing: bidask");
		if (creditOnly) extraNotes.Add("credit-only");
		if (skippedIlliquid > 0) extraNotes.Add($"{skippedIlliquid} illiquid hidden");
		if (cash.HasValue) extraNotes.Add($"cash ${cash.Value:N0}");
		var suffix = extraNotes.Count > 0 ? $" ({string.Join(", ", extraNotes)})" : "";

		var items = new List<IRenderable>
		{
			new Markup($"[bold]{Markup.Escape(posDesc)}[/]  [dim]|  Rolling: {Markup.Escape(rolledDesc)}  |  {sweepNote}, sorted by {Markup.Escape(sortMode)}{Markup.Escape(suffix)}[/]"),
			table,
			new Markup("[dim]Roll Net / Natural: per share (+ = credit, − = debit); Net at mid, Natural crosses bid/ask. Cr/Day: Roll Net ÷ added DTE.[/]"),
			new Markup("[dim]Delta Δ: position delta change per share. BP Δ: Reg-T margin change vs Hold (red = exceeds available cash).[/]"),
			new Markup("[dim]Margin, Max profit/loss, Break-evens, POP, EV: forward-looking post-roll structure priced at current mid (apples-to-apples vs Hold).[/]")
		};
		if (candidates.Any(c => c.IsSnappedStrike))
			items.Add(new Markup($"[yellow]~ Anchor strike (${Formatters.FormatQty(anchorStrike)}) is not listed at this expiration; snapped to nearest listed strike.[/]"));
		if (candidates.Any(c => c.IsInverted))
			items.Add(new Markup($"[yellow]{(ascii ? "!" : "⚠")} Target expiry is later than the paired long leg's expiry (inverted calendar/diagonal).[/]"));

		var panel = new Panel(new Rows(items))
		{
			Header = new PanelHeader(Markup.Escape($"{ticker} Roll Compare — {countNote}, spot ${spot:N2}")),
			Expand = true,
			Border = ascii ? BoxBorder.Ascii : BoxBorder.Rounded
		};
		console.Write(panel);
		console.WriteLine();
	}

	private static string[] BuildCells(RollCandidateRow r, decimal? cash, int qty, bool useBidAsk, bool ascii)
	{
		string target;
		if (r.IsCurrent)
		{
			var dte = Math.Max(0, (r.Parsed.ExpiryDate.Date - EvaluationDate.Today.Date).Days);
			target = $"[bold cyan]Hold ({Formatters.FormatQty(r.Parsed.Strike)}{r.Parsed.CallPut} {r.Parsed.ExpiryDate.ToString("dd MMM", CultureInfo.InvariantCulture)}, {dte}d)[/]";
		}
		else
		{
			var dteDelta = r.AddedDte == 0 ? "0d" : r.AddedDte > 0 ? $"+{r.AddedDte}d" : $"{r.AddedDte}d";
			var snapped = r.IsSnappedStrike ? " ~" : "";
			var warn = r.IsInverted ? (ascii ? " !" : " ⚠") : "";
			var star = r.IsAnchor ? (ascii ? " *" : " ★") : "";
			var text = $"{Formatters.FormatQty(r.Parsed.Strike)}{r.Parsed.CallPut} {r.Parsed.ExpiryDate.ToString("dd MMM", CultureInfo.InvariantCulture)} ({dteDelta}){snapped}{warn}{star}";
			target = r.IsAnchor ? $"[bold yellow]{Markup.Escape(text)}[/]" : Markup.Escape(text);
		}

		var bpDeltaStr = "—";
		if (!r.IsCurrent && r.BpDelta is decimal bpd)
		{
			var formatted = SignedDollars(bpd);
			var rollNetTotal = (useBidAsk ? (r.RollNatural ?? r.RollNetMid ?? 0m) : (r.RollNetMid ?? r.RollNatural ?? 0m)) * qty * 100m;
			var exceedsCash = cash.HasValue && (bpd - rollNetTotal) > cash.Value;
			bpDeltaStr = exceedsCash ? $"[bold red]{formatted}[/]" : bpd > 0m ? $"[yellow]{formatted}[/]" : bpd < 0m ? $"[green]{formatted}[/]" : formatted;
		}

		return
		[
			target,
			r.RollNetMid is decimal net ? SignedPriceMarkup(net) : "—",
			r.RollNatural is decimal nat ? SignedPriceMarkup(nat) : "—",
			r.CreditPerDay is decimal cpd ? SignedPriceMarkup(cpd) : "—",
			r.DeltaChange is decimal dd ? SignedDecimal(dd) : "—",
			bpDeltaStr,
			r.Margin is decimal m ? Dollars(m) : "—",
			r.MaxProfit is decimal mp ? $"{Dollars(mp)}{(r.MaxProfitPrice is decimal at ? $" @ {at:0.##}" : "")}" : "Unlimited",
			r.MaxLoss is decimal ml ? PnlMarkup(-ml) : "Unlimited",
			r.BreakEvens.Count > 0 ? string.Join(" / ", r.BreakEvens.Select(b => b.ToString("0.00", CultureInfo.InvariantCulture))) : "—",
			r.ProbabilityOfProfit is decimal pop ? $"{pop * 100m:0.0}%" : "—",
			r.HoldExpectedValue is decimal hold ? PnlMarkup(hold) : "—",
			r.ManagedExpectedValue is decimal managed ? PnlMarkup(managed) : "—"
		];
	}

	internal static string DescribePosition(IReadOnlyList<RollPositionLeg> legs)
	{
		var ratio = legs.Select(l => l.Qty).Distinct().Count() > 1;
		return string.Join("  ", legs.Select(l =>
		{
			var sign = l.Action == LegAction.Buy ? "+" : "−";
			var qtyPrefix = ratio ? $"{l.Qty}x" : "";
			if (l.Option != null)
				return $"{sign}{qtyPrefix}{Formatters.FormatQty(l.Option.Strike)}{l.Option.CallPut} {l.Option.ExpiryDate.ToString("dd MMM", CultureInfo.InvariantCulture)}";
			return $"{sign}{l.Qty} {l.Symbol}";
		})) + (ratio ? "" : $"  (x{legs[0].Qty})");
	}

	private static string SignedPriceMarkup(decimal value)
	{
		var sign = value > 0m ? "+" : value < 0m ? "-" : "";
		var text = $"{sign}${Math.Abs(value):0.00}";
		return value > 0m ? $"[green]{text}[/]" : value < 0m ? $"[red]{text}[/]" : text;
	}

	private static string SignedDecimal(decimal value) => value >= 0m ? $"+{value:0.00}" : $"{value:0.00}";

	private static string Dollars(decimal value) => value < 0m ? $"-${Math.Abs(value):N0}" : $"${value:N0}";

	private static string SignedDollars(decimal value) => value > 0m ? $"+${value:N0}" : value < 0m ? $"-${Math.Abs(value):N0}" : "$0";

	private static string PnlMarkup(decimal value) => value < 0m ? $"[red]{Dollars(value)}[/]" : $"[green]{Dollars(value)}[/]";
}

internal static class RollGridRenderer
{
	/// <summary>Rendered width of the roll grid's bordered table: 1 (left border) + (price column + 2 padding + 1 separator)
	/// + per date column (old|new|net cell, min 6, + 2 padding + 1 separator).</summary>
	private static int RollGridTableWidth(int priceColWidth, int dateColumns, int oldWidth, int newWidth, int creditWidth)
		=> 1 + priceColWidth + 3 + dateColumns * (Math.Max(6, oldWidth + 1 + newWidth + 1 + creditWidth) + 3);

	internal static int Render(
		IAnsiConsole console,
		RollPositionLeg rolledLeg,
		IReadOnlyList<RollPositionLeg> staticLegs,
		string newSymbol,
		OptionParsed newParsed,
		decimal spot,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		IReadOnlyDictionary<string, decimal>? ivOverrides,
		AnalyzeRollSettings settings,
		bool ascii)
	{
		var oldSymbol = rolledLeg.Symbol;
		var oldParsed = rolledLeg.Option!;
		var qty = rolledLeg.Qty;
		var isLong = rolledLeg.Action == LegAction.Buy;
		var sideLabel = isLong ? "long" : "short";

		decimal? oldIv = ivOverrides != null && ivOverrides.TryGetValue(oldSymbol, out var oiv) ? oiv : quotes.TryGetValue(oldSymbol, out var oq) && oq.ImpliedVolatility > 0 ? oq.ImpliedVolatility : null;
		decimal? newIv = ivOverrides != null && ivOverrides.TryGetValue(newSymbol, out var niv) ? niv : quotes.TryGetValue(newSymbol, out var nq) && nq.ImpliedVolatility > 0 ? nq.ImpliedVolatility : null;

		if (!oldIv.HasValue || !newIv.HasValue)
		{
			console.MarkupLine($"[red]Error:[/] Could not determine IV for {Markup.Escape(!oldIv.HasValue ? oldSymbol : newSymbol)}. Use --iv to override.");
			return 1;
		}

		var oldBid = quotes.TryGetValue(oldSymbol, out var obq) ? obq.Bid : null;
		var oldAsk = quotes.TryGetValue(oldSymbol, out var oaq) ? oaq.Ask : null;
		var newBid = quotes.TryGetValue(newSymbol, out var nbq) ? nbq.Bid : null;
		var newAsk = quotes.TryGetValue(newSymbol, out var naq) ? naq.Ask : null;

		var rangeFactor = settings.Range > 0 ? settings.Range : 2m;
		var loStrike = Math.Min(oldParsed.Strike, newParsed.Strike);
		var hiStrike = Math.Max(oldParsed.Strike, newParsed.Strike);

		var today = EvaluationDate.Today;
		var oldExpiry = oldParsed.ExpiryDate;
		var newExpiry = newParsed.ExpiryDate;
		var rfr = OptionMath.RiskFreeRate;

		console.WriteLine();
		console.WriteLine($"Roll Analysis: {Formatters.FormatOptionDisplay(oldParsed.Root, oldParsed.ExpiryDate, oldParsed.Strike)} {ParsingHelpers.CallPutDisplayName(oldParsed.CallPut)} → {Formatters.FormatOptionDisplay(newParsed.Root, newParsed.ExpiryDate, newParsed.Strike)} {ParsingHelpers.CallPutDisplayName(newParsed.CallPut)}  ({qty}x)");
		console.WriteLine($"Current: {oldParsed.Root} @ ${spot}  |  Close {oldSymbol}: Bid ${oldBid?.ToString("N2") ?? "?"} / Ask ${oldAsk?.ToString("N2") ?? "?"}  |  Open {newSymbol}: Bid ${newBid?.ToString("N2") ?? "?"} / Ask ${newAsk?.ToString("N2") ?? "?"}");
		console.WriteLine($"IV: Close leg {oldIv.Value:P1} | Open leg {newIv.Value:P1}");

		var currentCredit = isLong ? (oldBid ?? 0m) - (newAsk ?? 0m) : (newBid ?? 0m) - (oldAsk ?? 0m);
		decimal? oldMid = oldBid.HasValue && oldAsk.HasValue ? (oldBid.Value + oldAsk.Value) / 2m : null;
		decimal? newMid = newBid.HasValue && newAsk.HasValue ? (newBid.Value + newAsk.Value) / 2m : null;
		var midCredit = oldMid.HasValue && newMid.HasValue ? (isLong ? oldMid.Value - newMid.Value : newMid.Value - oldMid.Value) : currentCredit;
		var effectiveCredit = settings.UseBidAsk ? currentCredit : midCredit;
		console.WriteLine($"Current roll net (mid, {sideLabel}):     ${midCredit:N4}/share = ${midCredit * 100m:N2}/contract = ${midCredit * qty * 100m:N2} total");
		console.WriteLine($"Current roll net (natural, {sideLabel}): ${currentCredit:N4}/share = ${currentCredit * 100m:N2}/contract = ${currentCredit * qty * 100m:N2} total");

		if (!isLong)
		{
			var oldCov = ComputePositionMargin(rolledLeg, staticLegs, spot, quotes);
			var rolledNew = rolledLeg with { Symbol = newSymbol, Option = newParsed };
			var newCov = ComputePositionMargin(rolledNew, staticLegs, spot, quotes);

			var pairNote = staticLegs.Count > 0 ? $", with {RollComparison.DescribePosition(staticLegs)}" : "";
			console.WriteLine($"Margin analysis (Reg-T estimate, at spot ${spot:N2}{pairNote}):");
			console.WriteLine($"  Current requirement:  {oldCov.Label} = ${oldCov.Total:N2} total");
			console.WriteLine($"  New requirement:      {newCov.Label} = ${newCov.Total:N2} total");
			var deltaMargin = newCov.Total - oldCov.Total;
			var deltaSign = deltaMargin >= 0 ? "+" : "-";
			console.WriteLine($"  BP delta:             {deltaSign}${Math.Abs(deltaMargin):N2} total");

			if (settings.Cash.HasValue)
			{
				var cash = settings.Cash.Value;
				var rollNetTotal = effectiveCredit * qty * 100m;
				var available = cash + rollNetTotal;
				var net = available - deltaMargin;
				var rollLabel = rollNetTotal >= 0m
					? $"${cash:N2} cash + ${rollNetTotal:N2} roll credit = ${available:N2}"
					: $"${cash:N2} cash - ${Math.Abs(rollNetTotal):N2} roll debit = ${available:N2}";
				var netSign = net >= 0m ? "+" : "-";
				var netLabel = net >= 0m ? "sufficient" : "shortfall — needs additional funds";
				console.WriteLine();
				console.WriteLine($"Funding check (--cash ${cash:N2}, {SuggestionPricing.Normalize(settings.Pricing)}):");
				console.WriteLine($"  Available:  {rollLabel}");
				console.WriteLine($"  Required:   ${deltaMargin:N2} (BP delta)");
				console.WriteLine($"  Net:        {netSign}${Math.Abs(net):N2} ({netLabel})");
			}
		}

		var tradeCmds = BuildTradePlaceCommands(rolledLeg, newSymbol, effectiveCredit, oldBid, oldAsk, newBid, newAsk, settings.UseBidAsk);
		var postRollSpec = BuildPostRollSpec(rolledLeg, newSymbol, staticLegs, effectiveCredit);
		var analyzeTradeCmd = BuildAnalyzeTradeCommand(rolledLeg, newSymbol, staticLegs, oldBid, oldAsk, newBid, newAsk, settings.UseBidAsk);
		console.WriteLine();
		console.MarkupLine("[dim]Commands:[/]");
		if (tradeCmds.Count == 1)
		{
			console.MarkupLine($"  [dim]Roll: [/] {Markup.Escape(tradeCmds[0])}");
		}
		else
		{
			console.MarkupLine($"  [dim]Close:[/] {Markup.Escape(tradeCmds[0])}");
			console.MarkupLine($"  [dim]Open: [/] {Markup.Escape(tradeCmds[1])}");
		}
		if (postRollSpec != null)
			console.MarkupLine($"  [dim]Risk: [/] {Markup.Escape($"wa analyze risk \"{postRollSpec}\"")}");
		if (analyzeTradeCmd != null)
			console.MarkupLine($"  [dim]Trade:[/] {Markup.Escape(analyzeTradeCmd)}");

		console.WriteLine();

		var terminalWidth = console.Profile.Width;
		var maxLegValueWidth = Math.Max(oldBid ?? 0m, Math.Max(oldAsk ?? 0m, Math.Max(newBid ?? 0m, newAsk ?? 0m))).ToString("N2", CultureInfo.InvariantCulture).Length;
		var maxCols = Math.Max(3, TableBuilder.ComputeMaxGridColumns(terminalWidth, displayMode: "value", showLegs: true, maxLegCount: 2, maxLegValueWidth: maxLegValueWidth, gridTableOuterBorders: 2));

		var oldDays = (int)(oldExpiry.Date - today).TotalDays;
		var evalTimes = new List<DateTime>();
		var isIntraday = oldDays <= 1;
		if (isIntraday)
		{
			var optionsClose = new TimeSpan(16, 0, 0);
			var expiryDay = oldExpiry.Date;
			var allHours = new List<DateTime>();
			for (var h = OptionMath.MarketOpen; h < optionsClose; h += TimeSpan.FromHours(1))
				allHours.Add(expiryDay + h);
			allHours.Add(expiryDay + optionsClose);

			if (allHours.Count <= maxCols)
				evalTimes.AddRange(allHours);
			else
			{
				var hourStep = Math.Max(1, (allHours.Count - 1 + maxCols - 2) / (maxCols - 1));
				for (var i = 0; i < allHours.Count - 1; i += hourStep)
					evalTimes.Add(allHours[i]);
				if (evalTimes[^1] != allHours[^1])
					evalTimes.Add(allHours[^1]);
			}
		}
		else
		{
			evalTimes = TimeDecayGridBuilder.BuildDateColumns(oldExpiry, maxCols);
		}

		var searchMin = Math.Max(0.01m, loStrike - OptionMath.GetPriceStep(loStrike) * 10m);
		var searchMax = hiStrike + OptionMath.GetPriceStep(hiStrike) * 10m;
		var searchStep = OptionMath.GetPriceStep(loStrike) / rangeFactor / 5m / 10m;
		var bestPrice = loStrike;
		var bestCredit = decimal.MinValue;
		for (var p = searchMin; p <= searchMax; p += searchStep)
		{
			var credit = evalTimes.Max(t =>
			{
				var newVal = OptionMath.BlackScholes(p, newParsed.Strike, MarketClock.Between(newParsed.Root, t, newExpiry.Date + OptionMath.MarketClose), rfr, newIv.Value, newParsed.CallPut);
				var oldVal = OptionMath.BlackScholes(p, oldParsed.Strike, MarketClock.Between(oldParsed.Root, t, oldExpiry.Date + OptionMath.MarketClose), rfr, oldIv.Value, oldParsed.CallPut);
				return isLong ? oldVal - newVal : newVal - oldVal;
			});
			if (credit > bestCredit) { bestCredit = credit; bestPrice = Math.Round(p, 2); }
		}

		var extraNotables = new List<decimal> { spot, bestPrice };
		if (settings.Levels != null)
			foreach (var pair in ReportCommand.ParseLevels(settings.Levels))
				if (ParsingHelpers.RootsMatchForAggregation(oldParsed.Root, pair.Key, oldParsed.ExpiryDate))
					extraNotables.AddRange(pair.Value);

		var priceList = TimeDecayGridBuilder.BuildPriceRows(spot, settings.Range, [], [oldParsed.Strike, newParsed.Strike], extraNotables);

		var (oldGrid, newGrid, creditGrid, maxCredit, maxCreditPrice, maxCreditDate, oldWidth, newWidth, creditWidth) =
			ComputeRollGrid(priceList, evalTimes, oldParsed, newParsed, oldExpiry, newExpiry, isLong, oldIv.Value, newIv.Value, rfr, today);

		if (!isIntraday)
		{
			var priceColWidth = priceList.Max(p => $"${p:N2}".Length);
			while (maxCols > 3 && RollGridTableWidth(priceColWidth, evalTimes.Count, oldWidth, newWidth, creditWidth) > terminalWidth)
			{
				maxCols--;
				evalTimes = TimeDecayGridBuilder.BuildDateColumns(oldExpiry, maxCols);
				(oldGrid, newGrid, creditGrid, maxCredit, maxCreditPrice, maxCreditDate, oldWidth, newWidth, creditWidth) =
					ComputeRollGrid(priceList, evalTimes, oldParsed, newParsed, oldExpiry, newExpiry, isLong, oldIv.Value, newIv.Value, rfr, today);
			}

			for (int extra = 1; extra <= 5; extra++)
			{
				var moreTimes = TimeDecayGridBuilder.BuildDateColumns(oldExpiry, maxCols + extra);
				if (moreTimes.Count <= evalTimes.Count) break;
				var (og2, ng2, cg2, mc2, mcp2, mcd2, ow2, nw2, cw2) = ComputeRollGrid(priceList, moreTimes, oldParsed, newParsed, oldExpiry, newExpiry, isLong, oldIv.Value, newIv.Value, rfr, today);
				if (RollGridTableWidth(priceColWidth, moreTimes.Count, ow2, nw2, cw2) > terminalWidth) break;
				(evalTimes, oldGrid, newGrid, creditGrid, maxCredit, maxCreditPrice, maxCreditDate, oldWidth, newWidth, creditWidth) =
					(moreTimes, og2, ng2, cg2, mc2, mcp2, mcd2, ow2, nw2, cw2);
			}
		}

		var table = new Table().Border(ascii ? TableBorder.Ascii : TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn(new TableColumn("[bold]Price[/]").RightAligned().NoWrap());
		foreach (var t in evalTimes)
		{
			var label = isIntraday ? t.ToString("h tt") : TableBuilder.DateColumnHeaderMarkup(t, t == evalTimes[^1]);
			table.AddColumn(new TableColumn($"[bold]{label}[/]").RightAligned().NoWrap());
		}

		const char pad = '\u2007';

		for (int pi = 0; pi < priceList.Count; pi++)
		{
			var isCurrent = priceList[pi] == Math.Round(spot, 2);
			var isMaxRow = Enumerable.Range(0, evalTimes.Count).Any(di => creditGrid[pi, di] == maxCredit);
			var priceStr = $"${priceList[pi]:N2}";
			if (isCurrent) priceStr = $"[bold yellow]{priceStr}[/]";
			else if (isMaxRow) priceStr = $"[green]{priceStr}[/]";

			var cells = new List<string> { priceStr };
			for (int di = 0; di < evalTimes.Count; di++)
			{
				var c = creditGrid[pi, di];
				var isGlobalMax = c == maxCredit;
				var creditColor = c >= 0 ? "green" : "red";
				var creditSign = Math.Round(c, 2) >= 0 ? "+" : "";
				var creditText = $"{creditSign}{c:N2}".PadLeft(creditWidth, pad);
				string creditStr;
				if (isCurrent) creditStr = $"[bold yellow]{creditText}[/]";
				else if (isGlobalMax) creditStr = $"[bold underline {creditColor}]{creditText}[/]";
				else creditStr = $"[{creditColor}]{creditText}[/]";
				var oldText = oldGrid[pi, di].ToString("N2").PadLeft(oldWidth, pad);
				var newText = newGrid[pi, di].ToString("N2").PadLeft(newWidth, pad);
				cells.Add($"[grey]{oldText}[/]|[grey]{newText}[/]|{creditStr}");
			}
			table.AddRow(cells.ToArray());
		}

		console.Write(table);
		var maxDateLabel = isIntraday ? $"at {maxCreditDate:h:mm tt}" : $"on {maxCreditDate:dd MMM}";
		console.MarkupLine($"  [bold underline green]max net ({sideLabel})[/] (${maxCredit:N4}/share @ ${maxCreditPrice:N2} {maxDateLabel})    [bold yellow]current price[/]    [green]price with max net[/]");
		console.WriteLine($"  Each cell: Close|Open|Net per share (×100 per contract). Total for {qty}x: max ${maxCredit * qty * 100m:N2}");
		console.WriteLine();

		return 0;
	}

	/// <summary>Builds executable <c>wa trade place</c> command(s) for the roll using <see cref="RollShape.SupportsComboRoll"/>:
	/// verticals and same-strike calendars emit one combo order; diagonals split into separate close + open single-leg orders.</summary>
	internal static IReadOnlyList<string> BuildTradePlaceCommands(
		RollPositionLeg rolledLeg,
		string newSymbol,
		decimal rollNet,
		decimal? oldBid,
		decimal? oldAsk,
		decimal? newBid,
		decimal? newAsk,
		bool useBidAsk = false)
	{
		var closeAction = rolledLeg.Action == LegAction.Sell ? "buy" : "sell";
		var openAction = rolledLeg.Action == LegAction.Sell ? "sell" : "buy";

		if (RollShape.SupportsComboRoll([rolledLeg.Symbol, newSymbol]))
		{
			var legs = $"{closeAction}:{rolledLeg.Symbol}:{rolledLeg.Qty},{openAction}:{newSymbol}:{rolledLeg.Qty}";
			var side = rollNet >= 0m ? "sell" : "buy";
			var limit = Math.Round(Math.Abs(rollNet), 2);
			if (limit <= 0m) limit = 0.01m;
			return [$"wa trade place \"{legs}\" --side {side} --limit {limit.ToString("0.00", CultureInfo.InvariantCulture)}"];
		}

		// Diagonal roll (different strike AND different expiry): Webull requires two single-leg orders.
		decimal closePrice, openPrice;
		if (useBidAsk)
		{
			closePrice = rolledLeg.Action == LegAction.Sell ? (oldAsk ?? oldBid ?? 0.01m) : (oldBid ?? oldAsk ?? 0.01m);
			openPrice = rolledLeg.Action == LegAction.Sell ? (newBid ?? newAsk ?? 0.01m) : (newAsk ?? newBid ?? 0.01m);
		}
		else
		{
			closePrice = (oldBid.HasValue && oldAsk.HasValue) ? (oldBid.Value + oldAsk.Value) / 2m : (oldAsk ?? oldBid ?? 0.01m);
			openPrice = (newBid.HasValue && newAsk.HasValue) ? (newBid.Value + newAsk.Value) / 2m : (newBid ?? newAsk ?? 0.01m);
		}
		var closeLimit = Math.Max(0.01m, Math.Round(closePrice, 2));
		var openLimit = Math.Max(0.01m, Math.Round(openPrice, 2));

		return
		[
			$"wa trade place \"{closeAction}:{rolledLeg.Symbol}:{rolledLeg.Qty}\" --limit {closeLimit.ToString("0.00", CultureInfo.InvariantCulture)}",
			$"wa trade place \"{openAction}:{newSymbol}:{rolledLeg.Qty}\" --limit {openLimit.ToString("0.00", CultureInfo.InvariantCulture)}"
		];
	}

	/// <summary>Builds a transparent multi-trade <c>wa analyze trade</c> command containing the original position
	/// followed by the roll leg(s), letting <see cref="PositionTracker"/> compute the exact after-roll basis natively.</summary>
	internal static string? BuildAnalyzeTradeCommand(
		RollPositionLeg rolledLeg,
		string newSymbol,
		IReadOnlyList<RollPositionLeg> staticLegs,
		decimal? oldBid,
		decimal? oldAsk,
		decimal? newBid,
		decimal? newAsk,
		bool useBidAsk = false)
	{
		var origParts = new List<string>();
		var allOrig = new List<RollPositionLeg> { rolledLeg };
		allOrig.AddRange(staticLegs.Where(l => l.Option != null));
		foreach (var leg in allOrig)
		{
			var action = leg.Action == LegAction.Buy ? "buy" : "sell";
			var priceToken = leg.ExplicitPrice is decimal p && p > 0m
				? p.ToString("0.00", CultureInfo.InvariantCulture)
				: "MID";
			origParts.Add($"{action}:{leg.Symbol}:{leg.Qty}@{priceToken}");
		}
		if (origParts.Count == 0) return null;

		var closeAction = rolledLeg.Action == LegAction.Sell ? "buy" : "sell";
		var openAction = rolledLeg.Action == LegAction.Sell ? "sell" : "buy";
		decimal? closePrice = useBidAsk
			? (rolledLeg.Action == LegAction.Sell ? (oldAsk ?? oldBid) : (oldBid ?? oldAsk))
			: (oldBid.HasValue && oldAsk.HasValue ? (oldBid.Value + oldAsk.Value) / 2m : (oldAsk ?? oldBid));
		decimal? openPrice = useBidAsk
			? (rolledLeg.Action == LegAction.Sell ? (newBid ?? newAsk) : (newAsk ?? newBid))
			: (newBid.HasValue && newAsk.HasValue ? (newBid.Value + newAsk.Value) / 2m : (newBid ?? newAsk));

		var closeToken = closePrice is decimal cp && cp > 0m ? cp.ToString("0.00", CultureInfo.InvariantCulture) : "MID";
		var openToken = openPrice is decimal op && op > 0m ? op.ToString("0.00", CultureInfo.InvariantCulture) : "MID";
		var closeLeg = $"{closeAction}:{rolledLeg.Symbol}:{rolledLeg.Qty}@{closeToken}";
		var openLeg = $"{openAction}:{newSymbol}:{rolledLeg.Qty}@{openToken}";

		var rollGroup = RollShape.SupportsComboRoll([rolledLeg.Symbol, newSymbol])
			? $"{closeLeg},{openLeg}"
			: $"{closeLeg};{openLeg}";

		return $"wa analyze trade \"{string.Join(",", origParts)};{rollGroup}\"";
	}

	/// <summary>Builds the post-roll option leg spec for <c>wa analyze risk</c>,
	/// keeping static legs at their original cost basis and folding the roll credit/debit into the new leg's
	/// effective cost basis (short: oldBasis + rollNet; long: oldBasis − rollNet). Falls back to @MID when
	/// no original cost basis is available or the adjusted basis is non-positive.</summary>
	internal static string? BuildPostRollSpec(RollPositionLeg rolledLeg, string newSymbol, IReadOnlyList<RollPositionLeg> staticLegs, decimal rollNet)
	{
		var parts = new List<string>();

		var newAction = rolledLeg.Action == LegAction.Buy ? "buy" : "sell";
		string newPriceToken;
		if (rolledLeg.ExplicitPrice is decimal oldBasis)
		{
			var adjusted = rolledLeg.Action == LegAction.Sell ? oldBasis + rollNet : oldBasis - rollNet;
			newPriceToken = adjusted > 0m ? adjusted.ToString("0.00", CultureInfo.InvariantCulture) : "MID";
		}
		else
		{
			newPriceToken = "MID";
		}
		parts.Add($"{newAction}:{newSymbol}:{rolledLeg.Qty}@{newPriceToken}");

		foreach (var leg in staticLegs)
		{
			if (leg.Option == null) continue;
			var action = leg.Action == LegAction.Buy ? "buy" : "sell";
			var priceToken = leg.ExplicitPrice is decimal p && p > 0m
				? p.ToString("0.00", CultureInfo.InvariantCulture)
				: "MID";
			parts.Add($"{action}:{leg.Symbol}:{leg.Qty}@{priceToken}");
		}

		return parts.Count > 0 ? string.Join(",", parts) : null;
	}

	internal static MarginRequirement ComputePositionMargin(
		RollPositionLeg activeLeg,
		IReadOnlyList<RollPositionLeg> staticLegs,
		decimal spot,
		IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		var allLegs = new List<RollPositionLeg>(1 + staticLegs.Count) { activeLeg };
		allLegs.AddRange(staticLegs);

		var root = activeLeg.Option!.Root;
		var marginLegs = new List<MarginLeg>();
		var equityShares = 0;
		foreach (var leg in allLegs)
		{
			if (leg.Option != null)
			{
				var isLong = leg.Action == LegAction.Buy;
				var premium = 0m;
				if (!isLong && quotes.TryGetValue(leg.Symbol, out var q) && q.Bid.HasValue && q.Ask.HasValue)
					premium = (q.Bid.Value + q.Ask.Value) / 2m;
				marginLegs.Add(new MarginLeg(leg.Option, leg.Qty, isLong, premium));
			}
			else if (leg.Action == LegAction.Buy && string.Equals(leg.Symbol, root, StringComparison.OrdinalIgnoreCase))
			{
				equityShares += leg.Qty;
			}
		}

		return MarginCalculator.Requirement(marginLegs, spot, equityShares);
	}

	private static (decimal[,] OldGrid, decimal[,] NewGrid, decimal[,] CreditGrid, decimal MaxCredit, decimal MaxCreditPrice, DateTime MaxCreditDate, int OldWidth, int NewWidth, int CreditWidth)
		ComputeRollGrid(List<decimal> priceList, List<DateTime> evalTimes, OptionParsed oldParsed, OptionParsed newParsed, DateTime oldExpiry, DateTime newExpiry, bool isLong, decimal oldIv, decimal newIv, double rfr, DateTime today)
	{
		var og = new decimal[priceList.Count, evalTimes.Count];
		var ng = new decimal[priceList.Count, evalTimes.Count];
		var cg = new decimal[priceList.Count, evalTimes.Count];
		var maxC = decimal.MinValue;
		var maxCPrice = 0m;
		var maxCDate = today;
		for (int pi = 0; pi < priceList.Count; pi++)
			for (int di = 0; di < evalTimes.Count; di++)
			{
				var oldDte = MarketClock.Between(oldParsed.Root, evalTimes[di], oldExpiry.Date + OptionMath.MarketClose);
				var newDte = MarketClock.Between(newParsed.Root, evalTimes[di], newExpiry.Date + OptionMath.MarketClose);
				og[pi, di] = OptionMath.BlackScholes(priceList[pi], oldParsed.Strike, oldDte, rfr, oldIv, oldParsed.CallPut);
				ng[pi, di] = OptionMath.BlackScholes(priceList[pi], newParsed.Strike, newDte, rfr, newIv, newParsed.CallPut);
				cg[pi, di] = isLong ? og[pi, di] - ng[pi, di] : ng[pi, di] - og[pi, di];
				if (cg[pi, di] > maxC) { maxC = cg[pi, di]; maxCPrice = priceList[pi]; maxCDate = evalTimes[di]; }
			}
		int ow = 0, nw = 0, cw = 0;
		for (int pi = 0; pi < priceList.Count; pi++)
			for (int di = 0; di < evalTimes.Count; di++)
			{
				ow = Math.Max(ow, og[pi, di].ToString("N2").Length);
				nw = Math.Max(nw, ng[pi, di].ToString("N2").Length);
				var c = cg[pi, di];
				cw = Math.Max(cw, $"{(Math.Round(c, 2) >= 0 ? "+" : "")}{c:N2}".Length);
			}
		return (og, ng, cg, maxC, maxCPrice, maxCDate, ow, nw, cw);
	}
}

internal sealed class AnalyzeRollCommand : AsyncCommand<AnalyzeRollSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeRollSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.PinnedInstant is { } instant) ReportCommand.PinEvaluation(instant);

		var toText = settings.OutputFormat.Equals("text", StringComparison.OrdinalIgnoreCase);
		var isInteractive = string.IsNullOrWhiteSpace(settings.Spec) && !Console.IsInputRedirected;

		// Step 1: Resolve position legs (from <spec> or interactively from open positions)
		List<RollPositionLeg> positionLegs;
		if (string.IsNullOrWhiteSpace(settings.Spec))
		{
			if (Console.IsInputRedirected)
			{
				Console.Error.WriteLine("Error: <spec> is required in non-interactive mode (stdin is redirected).");
				return 1;
			}

			var (loaded, loadError) = AnalyzePositionCommand.SelectPositionFromLog("Select a position to roll:");
			if (loaded == null)
			{
				Console.Error.WriteLine($"Error: {loadError}");
				return 1;
			}
			positionLegs = loaded.Select(s => new RollPositionLeg(s.Symbol, s.Action, s.Qty, s.Parsed, s.CostBasis, null)).ToList();

			if (!settings.Cash.HasValue)
			{
				var detectedCash = await AnalyzePositionCommand.TryResolveAvailableCashAsync(settings.Account, cancellation);
				if (detectedCash.HasValue)
				{
					settings.Cash = detectedCash.Value.Cash;
					AnsiConsole.MarkupLine($"[dim]Using available cash/BP from account '{Markup.Escape(detectedCash.Value.AccountAlias)}': ${detectedCash.Value.Cash:N2}.[/]");
				}
			}
		}
		else
		{
			positionLegs = TradeLegParser.Parse(settings.Spec)
				.Select(l => new RollPositionLeg(l.Symbol, l.Action, l.Quantity, l.Option, l.Price, l.PriceKeyword))
				.ToList();
		}

		// Step 2: Select which option leg to roll
		var (rolledIndex, legError) = SelectRolledLeg(positionLegs, settings.Leg, isInteractive);
		if (rolledIndex < 0)
		{
			Console.Error.WriteLine($"Error: {legError}");
			return 1;
		}

		var rolledLeg = positionLegs[rolledIndex];
		var staticLegs = positionLegs.Where((_, i) => i != rolledIndex).ToList();
		var ticker = rolledLeg.Option!.Root;

		// Validate --to root matches rolled leg
		OptionParsed? targetParsed = null;
		string? targetSymbol = null;
		if (settings.To != null)
		{
			targetSymbol = settings.To.Trim().ToUpperInvariant();
			targetParsed = ParsingHelpers.ParseOptionSymbol(targetSymbol)!;
			if (!AnalyzeRollSettings.RootsShareBook(targetParsed.Root, rolledLeg.Option.Root, targetParsed.ExpiryDate, rolledLeg.Option.ExpiryDate))
			{
				Console.Error.WriteLine($"Error: --to root '{targetParsed.Root}' does not match rolled leg root '{rolledLeg.Option.Root}'.");
				return 1;
			}
		}

		// Direct grid mode: --to provided WITHOUT --strikes or --exp
		var directGridMode = targetParsed != null && !settings.Strikes.HasValue && !settings.Exp.HasValue;

		int strikesN = 0, expN = 0;
		if (!directGridMode)
		{
			strikesN = settings.Strikes ?? (isInteractive
				? AnsiConsole.Prompt(new TextPrompt<int>("Strikes (±N around anchor):")
					.DefaultValue(2)
					.Validate(v => v >= 0 ? ValidationResult.Success() : ValidationResult.Error("Must be ≥ 0")))
				: 2);
			expN = settings.Exp ?? (isInteractive
				? AnsiConsole.Prompt(new TextPrompt<int>("Expiries (±N around anchor):")
					.DefaultValue(2)
					.Validate(v => v >= 0 ? ValidationResult.Success() : ValidationResult.Error("Must be ≥ 0")))
				: 2);
		}

		// Step 3: Fetch quotes & spot
		var allOptionSymbols = positionLegs.Where(l => l.Option != null).Select(l => l.Symbol).ToList();
		if (targetSymbol != null) allOptionSymbols.Add(targetSymbol);
		allOptionSymbols = allOptionSymbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		IReadOnlyDictionary<string, OptionContractQuote> quotes;
		IReadOnlyDictionary<string, decimal> underlyingPrices;
		if (EvaluationDate.IsPast)
		{
			var (market, error) = await HistoricalMarkOverlay.ApplyAsync(EvaluationDate.Instant!.Value, nearest: false, allOptionSymbols, null, null, cancellation);
			if (market == null) { Console.Error.WriteLine($"Error: can't rebuild the market at {EvaluationDate.Instant:yyyy-MM-dd HH:mm} ET — {error}"); return 1; }
			underlyingPrices = market.UnderlyingPrices;
			quotes = AnalyzeCommon.RecalibrateQuotesToMid(market.Quotes, underlyingPrices, dividends: null, OptionMath.ObservationInstant());
		}
		else
		{
			try
			{
				(quotes, underlyingPrices) = await AnalyzeCommon.FetchLiveChainByVendor(allOptionSymbols, settings.ResolvedVendor, cancellation);
				YahooOptionsClient.ApplyToOptionMath(await AI.Backtest.HistoricalRateCache.FetchLiveOrLatestAsync(cancellation));
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException) throw;
				Console.Error.WriteLine($"Error: Failed to fetch option data: {ex.Message}");
				return 1;
			}
		}

		var spot = ParsingHelpers.TryResolveForRoot(underlyingPrices, ticker, out var sp) ? sp : 0m;
		var spotOverrides = await AnalyzeCommon.ResolveSpotOverrideMapAsync(settings.Spot, settings.SpotSession, [ticker], settings.ResolvedVendor, underlyingPrices, cancellation);
		decimal ovr = 0m;
		var hasSpotOverride = spotOverrides != null && ParsingHelpers.TryResolveForRoot(spotOverrides, ticker, out ovr);
		if (hasSpotOverride) spot = ovr;
		if (spot <= 0m) { Console.Error.WriteLine($"Error: Could not determine underlying price for {ticker}."); return 1; }
		(quotes, underlyingPrices) = AnalyzeCommon.RemarkToSpots(quotes, underlyingPrices, spotOverrides);

		var asOf = EvaluationDate.IsPast ? EvaluationDate.Instant!.Value : DateTime.Now;
		var quoteAsOf = EvaluationDate.IsPast ? EvaluationDate.Instant!.Value : OptionMath.ObservationInstant();
		if (settings.Calibrated && !EvaluationDate.IsPast)
			quotes = AnalyzeCommon.RecalibrateQuotesToMid(quotes, underlyingPrices, dividends: null, quoteAsOf);

		var ivOverrides = settings.IvOverrides != null ? ReportCommand.ParseIvOverrides(settings.IvOverrides) : null;

		var writer = toText ? new StringWriter() : null;
		var console = writer != null ? TextFileExporter.CreateTextConsole(writer) : AnsiConsole.Console;

		if (directGridMode)
		{
			var rc = RollGridRenderer.Render(console, rolledLeg, staticLegs, targetSymbol!, targetParsed!, spot, quotes, ivOverrides, settings, ascii: toText);
			if (writer != null) TextFileExporter.WriteConsoleOutputToTextFile(writer, settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("roll"), "Roll analysis exported to");
			return rc;
		}

		// Step 4: Enumerate candidates on the chain
		var anchorParsed = targetParsed ?? rolledLeg.Option;
		var (candidates, skippedIlliquid) = RollCandidateEnumerator.Enumerate(
			quotes, rolledLeg.Option, rolledLeg.Symbol, anchorParsed, targetSymbol, rolledLeg.Action, strikesN, expN, EvaluationDate.Today, spot);

		// Step 5: Evaluate Hold baseline + every candidate
		var effectiveUnderlyings = new Dictionary<string, decimal>(underlyingPrices, StringComparer.OrdinalIgnoreCase) { [ticker] = spot };
		var opts = new AnalysisOptions(
			OptionQuotes: quotes,
			UnderlyingPrices: effectiveUnderlyings,
			UnderlyingPriceOverrides: hasSpotOverride ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [ticker] = spot } : null,
			IvOverrides: ivOverrides,
			MarksAsOfEvalDate: EvaluationDate.IsPast);

		var intradayCache = AnalyzeRiskCommand.CreateIntradayCache();
		var scoringInputs = await AnalyzeRiskCommand.LoadOpenerScoringInputsAsync(ticker, asOf, includeCurrentBar: !EvaluationDate.IsPast, intradayCache, cancellation);
		var baseTime = EvaluationDate.IsPast ? EvaluationDate.Instant!.Value : DateTime.Now;
		Func<string, DateTime, decimal?> closeLookup = (_, _) => null;

		var oldQuote = quotes.GetValueOrDefault(rolledLeg.Symbol);
		var oldMid = MidPrice(oldQuote);
		var oldDelta = ComputeLegDelta(rolledLeg.Option, oldQuote, spot, asOf, ivOverrides);
		var currentMargin = RollGridRenderer.ComputePositionMargin(rolledLeg, staticLegs, spot, quotes).Total;

		var currentRow = EvaluateStructure(
			symbol: rolledLeg.Symbol,
			parsed: rolledLeg.Option,
			activeLeg: rolledLeg,
			staticLegs: staticLegs,
			isCurrent: true,
			isAnchor: false,
			isInverted: false,
			isSnappedStrike: false,
			addedDte: 0,
			rollNetMid: null,
			rollNatural: null,
			creditPerDay: null,
			deltaChange: null,
			currentMargin: currentMargin,
			spot: spot,
			quotes: quotes,
			opts: opts,
			scoringInputs: scoringInputs,
			asOf: asOf,
			quoteAsOf: quoteAsOf,
			baseTime: baseTime,
			closeLookup: closeLookup,
			range: settings.Range,
			terminalWidth: console.Profile.Width);

		var staticLongExpiries = staticLegs
			.Where(l => l.Option != null && l.Action == LegAction.Buy)
			.Select(l => l.Option!.ExpiryDate.Date)
			.ToList();

		var evaluated = new List<RollCandidateRow>(candidates.Count);
		var isLongRoll = rolledLeg.Action == LegAction.Buy;

		foreach (var cand in candidates)
		{
			var newMid = MidPrice(cand.Quote);
			decimal? rollNetMid = oldMid.HasValue && newMid.HasValue
				? (isLongRoll ? oldMid.Value - newMid.Value : newMid.Value - oldMid.Value)
				: null;
			decimal? rollNatural = isLongRoll
				? (oldQuote?.Bid is decimal ob && cand.Quote.Ask is decimal na ? ob - na : null)
				: (cand.Quote.Bid is decimal nb && oldQuote?.Ask is decimal oa ? nb - oa : null);

			var addedDte = (cand.Parsed.ExpiryDate.Date - rolledLeg.Option.ExpiryDate.Date).Days;
			var basisNet = settings.UseBidAsk ? rollNatural : rollNetMid;
			decimal? creditPerDay = basisNet.HasValue && addedDte > 0 ? basisNet.Value / addedDte : null;

			var newDelta = ComputeLegDelta(cand.Parsed, cand.Quote, spot, asOf, ivOverrides);
			decimal? deltaChange = oldDelta.HasValue && newDelta.HasValue
				? (isLongRoll ? newDelta.Value - oldDelta.Value : -(newDelta.Value - oldDelta.Value))
				: null;

			var isInverted = !isLongRoll && staticLongExpiries.Count > 0 && staticLongExpiries.Any(le => cand.Parsed.ExpiryDate.Date > le);
			var rolledNewLeg = rolledLeg with { Symbol = cand.Symbol, Option = cand.Parsed };

			evaluated.Add(EvaluateStructure(
				symbol: cand.Symbol,
				parsed: cand.Parsed,
				activeLeg: rolledNewLeg,
				staticLegs: staticLegs,
				isCurrent: false,
				isAnchor: cand.IsAnchor,
				isInverted: isInverted,
				isSnappedStrike: cand.IsSnappedStrike,
				addedDte: addedDte,
				rollNetMid: rollNetMid,
				rollNatural: rollNatural,
				creditPerDay: creditPerDay,
				deltaChange: deltaChange,
				currentMargin: currentMargin,
				spot: spot,
				quotes: quotes,
				opts: opts,
				scoringInputs: scoringInputs,
				asOf: asOf,
				quoteAsOf: quoteAsOf,
					baseTime: baseTime,
				closeLookup: closeLookup,
				range: settings.Range,
				terminalWidth: console.Profile.Width));
		}

		var displayed = RollComparison.FilterAndSort(evaluated, settings.CreditOnly, settings.Sort, settings.Top, settings.UseBidAsk);

		RollComparison.Render(
			console, ticker, spot, rolledLeg, positionLegs, currentRow, displayed,
			evaluated.Count, skippedIlliquid, strikesN, expN, anchorParsed.Strike, settings.Cash, settings.CreditOnly, settings.UseBidAsk, settings.Sort, ascii: toText);

		if (writer != null)
		{
			TextFileExporter.WriteConsoleOutputToTextFile(writer, settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("roll"), "Roll analysis exported to");
			return 0;
		}

		// Step 6: Optional interactive drill-down into the price-by-time grid
		if (isInteractive && displayed.Count > 0)
		{
			while (true)
			{
				var choices = new List<DrillDownChoice> { new(null) };
				choices.AddRange(displayed.Select(r => new DrillDownChoice(r)));

				var picked = AnsiConsole.Prompt(new SelectionPrompt<DrillDownChoice>()
					.Title("Inspect a roll's price-by-time grid?")
					.UseConverter(c => c.Row == null
						? "Done"
						: FormatDrillDownLabel(c.Row))
					.AddChoices(choices));

				if (picked.Row == null) break;

				RollGridRenderer.Render(AnsiConsole.Console, rolledLeg, staticLegs, picked.Row.Symbol, picked.Row.Parsed, spot, quotes, ivOverrides, settings, ascii: false);
			}
		}

		return 0;
	}

	private readonly record struct DrillDownChoice(RollCandidateRow? Row);

	private static string FormatDrillDownLabel(RollCandidateRow r)
	{
		var dteDelta = r.AddedDte == 0 ? "0d" : r.AddedDte > 0 ? $"+{r.AddedDte}d" : $"{r.AddedDte}d";
		var net = r.RollNetMid is decimal n ? $"{(n >= 0m ? "+" : "-")}${Math.Abs(n):0.00}" : "—";
		var ev = r.ManagedExpectedValue is decimal m ? $"  EV {(m >= 0m ? "+" : "-")}${Math.Abs(m):N0}" : "";
		return $"{Formatters.FormatQty(r.Parsed.Strike)}{r.Parsed.CallPut} {r.Parsed.ExpiryDate.ToString("dd MMM", CultureInfo.InvariantCulture)} ({dteDelta})  Net {net}{ev}";
	}

	internal static (int Index, string? Error) SelectRolledLeg(IReadOnlyList<RollPositionLeg> legs, string? legSelector, bool interactive)
	{
		var optionIndices = legs
			.Select((l, i) => (Leg: l, Index: i))
			.Where(x => x.Leg.Option != null)
			.ToList();

		if (optionIndices.Count == 0) return (-1, "Position has no option legs.");

		if (!string.IsNullOrWhiteSpace(legSelector))
		{
			var trimmed = legSelector.Trim();
			if (trimmed.Equals("short", StringComparison.OrdinalIgnoreCase))
			{
				var match = optionIndices.Where(x => x.Leg.Action == LegAction.Sell).OrderBy(x => x.Leg.Option!.ExpiryDate).FirstOrDefault();
				return match.Leg != null ? (match.Index, null) : (-1, "--leg short: position has no short option leg.");
			}
			if (trimmed.Equals("long", StringComparison.OrdinalIgnoreCase))
			{
				var match = optionIndices.Where(x => x.Leg.Action == LegAction.Buy).OrderBy(x => x.Leg.Option!.ExpiryDate).FirstOrDefault();
				return match.Leg != null ? (match.Index, null) : (-1, "--leg long: position has no long option leg.");
			}
			var bySym = optionIndices.FirstOrDefault(x => string.Equals(x.Leg.Symbol, trimmed, StringComparison.OrdinalIgnoreCase));
			return bySym.Leg != null ? (bySym.Index, null) : (-1, $"--leg: '{trimmed}' is not one of the position's option legs.");
		}

		if (optionIndices.Count == 1) return (optionIndices[0].Index, null);

		// Order short legs first, then earliest expiry
		var ordered = optionIndices
			.OrderBy(x => x.Leg.Action == LegAction.Sell ? 0 : 1)
			.ThenBy(x => x.Leg.Option!.ExpiryDate)
			.ThenBy(x => x.Leg.Option!.Strike)
			.ToList();

		if (!interactive) return (ordered[0].Index, null);

		var chosen = AnsiConsole.Prompt(new SelectionPrompt<(RollPositionLeg Leg, int Index)>()
			.Title("Which leg do you want to roll?")
			.UseConverter(x =>
			{
				var side = x.Leg.Action == LegAction.Sell ? "SELL (short)" : "BUY  (long) ";
				var opt = x.Leg.Option!;
				return $"{side}  {opt.Root} {Formatters.FormatQty(opt.Strike)}{opt.CallPut} {opt.ExpiryDate:yyyy-MM-dd}  x{x.Leg.Qty}";
			})
			.AddChoices(ordered));
		return (chosen.Index, null);
	}

	private static RollCandidateRow EvaluateStructure(
		string symbol,
		OptionParsed parsed,
		RollPositionLeg activeLeg,
		IReadOnlyList<RollPositionLeg> staticLegs,
		bool isCurrent,
		bool isAnchor,
		bool isInverted,
		bool isSnappedStrike,
		int addedDte,
		decimal? rollNetMid,
		decimal? rollNatural,
		decimal? creditPerDay,
		decimal? deltaChange,
		decimal currentMargin,
		decimal spot,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		AnalysisOptions opts,
		AnalyzeRiskCommand.OpenerScoringInputs scoringInputs,
		DateTime asOf,
		DateTime quoteAsOf,
		DateTime baseTime,
		Func<string, DateTime, decimal?> closeLookup,
		decimal range,
		int terminalWidth)
	{
		var allLegs = new List<RollPositionLeg>(1 + staticLegs.Count) { activeLeg };
		allLegs.AddRange(staticLegs);

		var optionLegs = allLegs.Where(l => l.Option != null).ToList();
		var margin = RollGridRenderer.ComputePositionMargin(activeLeg, staticLegs, spot, quotes).Total;
		decimal? bpDelta = isCurrent ? null : margin - currentMargin;

		// Build synthetic forward-looking position at current mid for BreakEvenAnalyzer (mirrors TradeComparison)
		decimal? maxProfit = null, maxProfitPrice = null, maxLoss = null;
		IReadOnlyList<decimal> breakEvens = [];
		try
		{
			var specParts = new List<string>(allLegs.Count);
			var allPriced = true;
			foreach (var leg in allLegs)
			{
				var price = leg.Option != null
					? MidPrice(quotes.GetValueOrDefault(leg.Symbol)) ?? leg.ExplicitPrice
					: spot;
				if (!price.HasValue) { allPriced = false; break; }
				specParts.Add($"{(leg.Action == LegAction.Buy ? "buy" : "sell")}:{leg.Symbol}:{leg.Qty}@{price.Value.ToString(CultureInfo.InvariantCulture)}");
			}

			if (allPriced)
			{
				var syntheticSpec = string.Join(",", specParts);
				var trades = AnalyzeCommon.ParseSyntheticTrades(syntheticSpec, 0, baseTime, quotes);
				var (_, positions, _) = PositionTracker.ComputeReport(trades, 0m, null, closeLookup);
				var (positionRows, _, _) = PositionTracker.BuildPositionRows(positions, PositionTracker.BuildTradeIndex(trades), trades);
				if (positionRows.Count > 0)
				{
					var results = BreakEvenAnalyzer.Analyze(positionRows, opts, range, terminalWidth, "pnl", showLegs: false, gridTableHasBorder: true);
					var result = results.Count == 1
						? results[0]
						: CombinedBreakEvenAnalyzer.Analyze(positionRows, opts, range, terminalWidth, "pnl", showLegs: false, gridTableHasBorder: true, individualResults: results).FirstOrDefault();
					if (result != null)
					{
						maxProfit = result.MaxProfit;
						maxProfitPrice = result.MaxProfitPrice;
						maxLoss = result.MaxLoss;
						breakEvens = result.BreakEvens;
						if (result.Margin.HasValue) margin = result.Margin.Value;
					}
				}
			}
		}
		catch
		{
			// Break-even analysis is best-effort for exotic/unsupported combinations; roll metrics still render.
		}

		// Score POP / EV via the same opener scorer TradeComparison and analyze risk use (option-only equal-qty structures)
		decimal? pop = null, holdEv = null, managedEv = null;
		if (optionLegs.Count == allLegs.Count && optionLegs.Select(l => l.Qty).Distinct().Count() == 1 && optionLegs.All(l => quotes.ContainsKey(l.Symbol)))
		{
			var snapshots = optionLegs.Select(l =>
			{
				var mid = MidPrice(quotes.GetValueOrDefault(l.Symbol)) ?? l.ExplicitPrice ?? 0m;
				return new AnalyzePositionCommand.PositionSnapshot(l.Symbol, l.Action, l.Qty, mid, l.Option!);
			}).ToList();

			var diagLegs = AnalyzeRiskCommand.BuildDiagnosticLegs(snapshots, quotes, spot, asOf, AnalyzeRiskSettings.DefaultIvPct);
			var probe = AnalyzeRiskCommand.BuildProbe(diagLegs, spot, asOf, quoteAsOf, quotes, scoringInputs, AnalyzeRiskSettings.DefaultIvPct, scoreAtCostBasis: false, useMarketImpliedIv: true);
			if (probe.OpenerScore is { } score)
			{
				var qty = optionLegs[0].Qty;
				pop = score.ProbabilityOfProfit;
				holdEv = score.ExpectedValuePerContract * qty;
				managedEv = score.ManagedExpectedValuePerContract * qty;
			}
		}

		return new RollCandidateRow(
			Symbol: symbol,
			Parsed: parsed,
			IsCurrent: isCurrent,
			IsAnchor: isAnchor,
			IsInverted: isInverted,
			IsSnappedStrike: isSnappedStrike,
			AddedDte: addedDte,
			RollNetMid: rollNetMid,
			RollNatural: rollNatural,
			CreditPerDay: creditPerDay,
			DeltaChange: deltaChange,
			Margin: margin,
			BpDelta: bpDelta,
			MaxProfit: maxProfit,
			MaxProfitPrice: maxProfitPrice,
			MaxLoss: maxLoss,
			BreakEvens: breakEvens,
			ProbabilityOfProfit: pop,
			HoldExpectedValue: holdEv,
			ManagedExpectedValue: managedEv);
	}

	private static decimal? MidPrice(OptionContractQuote? quote)
	{
		if (quote == null) return null;
		if (quote.Bid is decimal b && quote.Ask is decimal a) return (b + a) / 2m;
		return quote.LastPrice;
	}

	private static decimal? ComputeLegDelta(OptionParsed opt, OptionContractQuote? quote, decimal spot, DateTime asOf, IReadOnlyDictionary<string, decimal>? ivOverrides)
	{
		var sym = MatchKeys.OccSymbol(opt.Root, opt.ExpiryDate, opt.Strike, opt.CallPut);
		decimal? iv = ivOverrides != null && ivOverrides.TryGetValue(sym, out var ov) ? ov : quote?.ImpliedVolatility;
		if (iv is not decimal ivVal || ivVal <= 0m) return null;
		var time = MarketClock.Between(opt.Root, asOf, opt.ExpiryDate.Date + OptionMath.MarketClose);
		return OptionMath.Delta(spot, opt.Strike, time, OptionMath.RiskFreeRate, ivVal, opt.CallPut);
	}
}
