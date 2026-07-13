using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Report;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

// Shared base for both subcommands.
internal abstract class AnalyzeSubcommandSettings : ReportSettings
{
	[CommandOption("--date")]
	[Description("Override 'today' for evaluation. Simulates running on a different date (e.g., after short leg expiration). Format: YYYY-MM-DD")]
	public string? Date { get; set; }

	internal DateTime? EvaluationDateOverride => Date != null ? DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Date != null && !DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return ValidationResult.Error($"--date: expected format YYYY-MM-DD, got '{Date}'");

		return ValidationResult.Success();
	}
}

// --- `analyze trade` ----------------------------------------------------------

internal sealed class AnalyzeTradeSettings : AnalyzeSubcommandSettings
{
	[CommandArgument(0, "[spec]")]
	[Description("Hypothetical trades. Format: ACTION:SYMBOL:QTY@PRICE where ACTION is buy|sell, SYMBOL is an OCC option symbol, and PRICE is a decimal or BID|MID|ASK (keywords require --api). Comma-separated for multiple. Example: buy:GME260501C00023000:300@MID. Omit when using --proposal.")]
	public string Spec { get; set; } = "";

	[CommandOption("--proposal")]
	[Description("Reconstruct the hypothetical trade from a stored proposal snapshot (its legs, entry mids, spot, IVs and date) instead of the <spec> argument. Format: FILE[[:LINE]] where FILE is a path, a data/ filename (ai-proposals.SPY.0DTE.jsonl), or the TICKER.strategy shorthand (SPY.0DTE); LINE is a 1-based line number, defaulting to the last line.")]
	public string? Proposal { get; set; }

	[CommandOption("--standalone")]
	[Description("Ignore all existing trades/positions and run the pipeline only on the synthetic legs. Useful for judging a trade in isolation.")]
	public bool Standalone { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Proposal != null)
		{
			if (!string.IsNullOrWhiteSpace(Spec))
				return ValidationResult.Error("pass either <spec> or --proposal, not both");
			return ValidationResult.Success();
		}

		if (string.IsNullOrWhiteSpace(Spec))
			return ValidationResult.Error("provide <spec> or --proposal");

		List<ParsedLeg> legs;
		try { legs = AnalyzeCommon.ParseAllLegs(Spec).ToList(); }
		catch (FormatException ex) { return ValidationResult.Error($"<spec>: {ex.Message}"); }

		foreach (var leg in legs)
		{
			if (leg.Option == null)
				return ValidationResult.Error($"<spec>: '{leg.Symbol}' is not an OCC option symbol (analyze trade supports option legs only)");
			if (leg.Price == null && leg.PriceKeyword == null)
				return ValidationResult.Error($"<spec>: leg '{leg.Symbol}' is missing @PRICE (a decimal or BID|MID|ASK is required)");
		}

		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeTradeCommand : AsyncCommand<AnalyzeTradeSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeTradeSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.Proposal != null && !ReconstructSpecFromProposal(settings))
			return 1;

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		List<Trade> trades;
		Dictionary<(DateTime, Side, int), decimal>? feeLookup;
		if (settings.Standalone)
		{
			trades = new List<Trade>();
			feeLookup = null;
		}
		else
		{
			int err;
			(trades, feeLookup, err) = ReportCommand.LoadTrades(settings);
			if (err != 0) return err;
		}

		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
		IReadOnlyDictionary<string, decimal>? underlyingPrices = null;
		if (AnalyzeCommon.NeedsMarketPrices(settings.Spec))
		{
			var symbols = AnalyzeCommon.ParseAllLegs(settings.Spec).Select(leg => leg.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			(quotes, underlyingPrices) = await AnalyzeCommon.FetchQuotesAndUnderlyingForSymbolList(symbols, cancellation);
			if (quotes == null) return 1;
		}

		var maxSeq = trades.Count > 0 ? trades.Max(t => t.Seq) + 1 : 0;
		// The synthetic legs represent a trade evaluated "now" (or at --until). Stamp them at that moment,
		// NOT at the latest existing trade's timestamp: when a Since/Until window (often config-driven)
		// excludes older trades, a synthetic stamped in the past gets culled by RunReportPipeline's window
		// filter and the hypothetical silently vanishes — visible only with --standalone. maxSeq+1 already
		// orders the synthetic after existing trades, so the timestamp only needs to land inside the window.
		var baseTime = settings.Until != null ? settings.UntilDate.AddHours(18) : DateTime.Now;
		trades.AddRange(AnalyzeCommon.ParseSyntheticTrades(settings.Spec, maxSeq, baseTime, quotes));

		return await ReportCommand.RunReportPipeline(settings, trades, feeLookup, cancellation, preloadedQuotes: quotes, preloadedUnderlyingPrices: underlyingPrices);
	}

	/// <summary>--proposal: rebuild the <c>&lt;spec&gt;</c> from a stored proposal snapshot — its legs at the entry
	/// mids captured when scored (the cost basis) — then let the report pipeline value and project the trade
	/// against the LIVE market now, not the market as it stood when the proposal was emitted. Spot, IVs and the
	/// evaluation date are left to resolve live (explicit --spot/--iv/--date flags still win). Only
	/// 'analyze risk --proposal' replays the frozen snapshot. Returns false (error printed) when it can't load.</summary>
	private static bool ReconstructSpecFromProposal(AnalyzeTradeSettings settings)
	{
		var (snap, error) = ProposalSnapshot.TryLoad(settings.Proposal!);
		if (snap == null) { Console.Error.WriteLine($"Error: {error}"); return false; }

		settings.Spec = string.Join(",", snap.Legs.Select(l => $"{(l.Action == LegAction.Buy ? "buy" : "sell")}:{l.Symbol}:{l.Qty}@{snap.CostBasis(l.Symbol).ToString(CultureInfo.InvariantCulture)}"));

		Console.WriteLine($"Proposal snapshot: {Path.GetFileName(snap.SourcePath)} line {snap.LineNumber}, emitted {snap.AsOf:yyyy-MM-dd HH:mm:ss} — evaluating against the live market now.");
		return true;
	}
}

// --- `analyze roll` -----------------------------------------------------------

internal sealed class AnalyzeRollSettings : AnalyzeBaseSettings
{
	[CommandArgument(0, "<spec>")]
	[Description("Roll spec. Format: OLD_SYMBOL>NEW_SYMBOL:QTY. Example: GME260410C00023000>GME260417C00023000:300")]
	public string Spec { get; set; } = "";

	[CommandOption("--side")]
	[Description("Position side. 'short' computes close-short-on-old / open-short-on-new (credit = new_bid - old_ask). 'long' computes close-long-on-old / open-long-on-new (credit = old_bid - new_ask). Default: short.")]
	public string? Side { get; set; }

	[CommandOption("--pair")]
	[Description("Static paired leg for spread margin calculation. Format: SYMBOL:QTY where SYMBOL is an equity ticker or OCC option symbol. Only meaningful with --side short. Example: --pair GME260515C00025000:499 or --pair GME:500")]
	public string? Pair { get; set; }

	[CommandOption("--cash")]
	[Description("Available cash for funding the roll. Format: dollar amount (e.g. 23015 or 23015.50). Prints a funding-check block against the BP delta. Only meaningful with --side short.")]
	public string? Cash { get; set; }

	// Grid display options carried over from ReportSettings — analyze roll renders its own
	// price-by-time grid and uses these the same way the report does.
	[CommandOption("--range")]
	[DefaultValue(0.0)]
	[Description("Grid granularity: rows per strike gap in the roll-credit grid. Default 0 = auto. Pass a positive value to override (higher = more rows).")]
	public decimal Range { get; set; } = 0;

	[CommandOption("--view")]
	[DefaultValue("detailed")]
	[Description("Grid width: 'detailed' (default) or 'simplified' (narrower terminal layout)")]
	public string View { get; set; } = "detailed";

	[CommandOption("--levels")]
	[Description("Additional reference price levels to show in the roll-credit grid. Format: TICKER:P1/P2/P3 (e.g., GME:20/25/30)")]
	public string? Levels { get; set; }

	public bool Simplified => View.Equals("simplified", StringComparison.OrdinalIgnoreCase);

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		var gtIdx = Spec.IndexOf('>');
		if (gtIdx < 1)
			return ValidationResult.Error("<spec>: expected format OLD_SYMBOL>NEW_SYMBOL:QTY");
		var remaining = Spec[(gtIdx + 1)..];
		var colonIdx = remaining.IndexOf(':');
		var oldSym = Spec[..gtIdx];
		var newSym = colonIdx >= 0 ? remaining[..colonIdx] : remaining;
		var qtyStr = colonIdx >= 0 ? remaining[(colonIdx + 1)..] : null;

		if (ParsingHelpers.ParseOptionSymbol(oldSym) == null)
			return ValidationResult.Error($"<spec>: invalid OCC symbol '{oldSym}'");
		if (ParsingHelpers.ParseOptionSymbol(newSym) == null)
			return ValidationResult.Error($"<spec>: invalid OCC symbol '{newSym}'");
		if (qtyStr != null && (!int.TryParse(qtyStr, out var rqty) || rqty <= 0))
			return ValidationResult.Error($"<spec>: invalid quantity '{qtyStr}'");

		if (Side != null && !string.Equals(Side, "long", StringComparison.OrdinalIgnoreCase) && !string.Equals(Side, "short", StringComparison.OrdinalIgnoreCase))
			return ValidationResult.Error($"--side: must be 'long' or 'short', got '{Side}'");

		if (Pair != null)
		{
			var isLongSide = string.Equals(Side, "long", StringComparison.OrdinalIgnoreCase);
			if (isLongSide)
				return ValidationResult.Error("--pair is only meaningful with --side short (the default). Long-side rolls don't affect Reg-T margin.");

			var parts = Pair.Split(':');
			if (parts.Length != 2)
				return ValidationResult.Error($"--pair: expected SYMBOL:QTY, got '{Pair}'");
			if (string.IsNullOrWhiteSpace(parts[0]))
				return ValidationResult.Error($"--pair: SYMBOL is empty");
			if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lqty) || lqty <= 0)
				return ValidationResult.Error($"--pair: QTY must be a positive integer, got '{parts[1]}'");

			// SYMBOL must be either a valid OCC option or an equity ticker. If it parses as OCC, also verify the root matches the rolled leg's root.
			var longOpt = ParsingHelpers.ParseOptionSymbol(parts[0]);
			if (longOpt != null)
			{
				// Cross-check root against the rolled leg.
				var specGtIdx = Spec.IndexOf('>');
				if (specGtIdx > 0)
				{
					var oldSymSpec = Spec[..specGtIdx];
					var oldOpt = ParsingHelpers.ParseOptionSymbol(oldSymSpec);
					if (oldOpt != null && !string.Equals(longOpt.Root, oldOpt.Root, StringComparison.OrdinalIgnoreCase))
						return ValidationResult.Error($"--pair: option root '{longOpt.Root}' does not match rolled leg root '{oldOpt.Root}'");
				}
			}
			// else: equity ticker, no additional validation (we don't validate ticker strings against any registry).
		}

		if (Cash != null)
		{
			if (!decimal.TryParse(Cash, NumberStyles.Any, CultureInfo.InvariantCulture, out var c) || c < 0m)
				return ValidationResult.Error($"--cash: must be a non-negative decimal, got '{Cash}'");

			var isLongSide = string.Equals(Side, "long", StringComparison.OrdinalIgnoreCase);
			if (isLongSide)
				return ValidationResult.Error("--cash is only meaningful with --side short (the default). Long-side rolls don't affect Reg-T margin.");
		}

		if (Range < 0)
			return ValidationResult.Error("--range must be 0 (auto) or a positive number");

		var view = View.ToLowerInvariant();
		if (view is not ("detailed" or "simplified"))
			return ValidationResult.Error("--view must be 'detailed' or 'simplified'");

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
}

internal sealed class AnalyzeRollCommand : AsyncCommand<AnalyzeRollSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeRollSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);

		if (settings.EvaluationDateOverride.HasValue)
		{
			EvaluationDate.Set(settings.EvaluationDateOverride.Value);
			Console.WriteLine($"Evaluation date override: {EvaluationDate.Today:yyyy-MM-dd}");
		}

		return await AnalyzeCommon.RunRollAnalysis(settings, cancellation);
	}
}

// --- Shared helpers (ported from old AnalyzeCommand) -------------------------

internal static class AnalyzeCommon
{
	/// <summary>Wraps the leg list, cost-basis line, per-leg detail, and risk diagnostic into a single
	/// outer panel — same shape `wa ai scan` uses for proposals, so analyze-position and analyze-risk
	/// match the rest of the AI surface visually. Pass <paramref name="ascii"/>=true with a file-backed
	/// <paramref name="console"/> for the --output text path.</summary>
	internal static void RenderProposalPanel(IReadOnlyList<AnalyzePositionCommand.PositionSnapshot> legs, string strategyLabel, decimal spot, RiskDiagnostic diagnostic, IAnsiConsole? console = null, bool ascii = false)
	{
		console ??= AnsiConsole.Console;
		var ticker = legs[0].Parsed.Root;
		var qty = legs[0].Qty;
		var initialDebit = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);

		var rows = new List<IRenderable>();
		var legsText = string.Join(", ", legs.Select(l => $"{(l.Action == LegAction.Buy ? "BUY" : "SELL")} {l.Symbol} x{l.Qty}"));
		rows.Add(new Markup($"[bold]{Markup.Escape(legsText)}[/]"));
		rows.Add(new Markup($"[dim]cost basis ${initialDebit:F2}/contract — spot ${spot:F2}, date {EvaluationDate.Today:yyyy-MM-dd}[/]"));
		foreach (var l in legs)
		{
			var label = l.Action == LegAction.Buy ? "[green]Long [/]" : "[red]Short[/]";
			rows.Add(new Markup($"  {label}  {Markup.Escape(l.Symbol)} x{l.Qty} @ ${l.CostBasis:F2}  (exp {l.Parsed.ExpiryDate:yyyy-MM-dd}, DTE {(l.Parsed.ExpiryDate.Date - EvaluationDate.Today.Date).Days})"));
		}
		rows.Add(RiskDiagnosticRenderer.Build(diagnostic, ascii));

		// Tooling notice (not a position-risk finding, so kept out of the diagnostic's "Rules fired"):
		// the opener score — EM / PoP / breakevens — needs a per-ticker opener config, which this ticker
		// lacks. Surface what to create above the panel rather than silently omitting the block.
		if (diagnostic.Probe?.ScoreUnavailableReason is { } scoreUnavailableReason)
			console.MarkupLine($"[yellow]{(ascii ? "!" : "?")} EM / PoP / breakevens unavailable — {Markup.Escape(scoreUnavailableReason)}[/]");

		var header = $"[bold cyan]{Markup.Escape(strategyLabel)}[/] [grey]{Markup.Escape(ticker)}[/] x{qty}";
		var panel = new Panel(new Rows(rows))
			.Header(header)
			.Expand()
			.Border(ascii ? BoxBorder.Ascii : BoxBorder.Rounded)
			.BorderColor(Color.Cyan1);
		console.Write(panel);
		console.WriteLine();
	}

	/// <summary>Returns the conventional default filename for an analyze-* text export. Mirrors
	/// `wa report`'s `WebullAnalytics_{date}.txt` shape so output files cluster predictably.</summary>
	internal static string DefaultTextOutputName(string commandLabel) =>
		$"WebullAnalytics_{commandLabel}_{DateTime.Now:yyyyMMdd}.txt";

	/// <summary>Splits a trade spec on ';' into strategy groups, then parses each as a comma-separated leg
	/// list. Empty groups are skipped. Used wherever we need a flat list of legs irrespective of which
	/// group they belong to (e.g. quote prefetching, validation).</summary>
	internal static IEnumerable<ParsedLeg> ParseAllLegs(string tradesSpec) =>
		tradesSpec
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.SelectMany(g => TradeLegParser.Parse(g));

	internal static bool NeedsMarketPrices(string tradesSpec) =>
		ParseAllLegs(tradesSpec).Any(leg => leg.PriceKeyword != null);

	private static string FormatLegSpec(ParsedLeg leg)
	{
		var price = leg.Price?.ToString(CultureInfo.InvariantCulture) ?? leg.PriceKeyword;
		return price == null
			? $"{leg.Action.ToString().ToLowerInvariant()}:{leg.Symbol}:{leg.Quantity}"
			: $"{leg.Action.ToString().ToLowerInvariant()}:{leg.Symbol}:{leg.Quantity}@{price}";
	}

	internal static async Task<IReadOnlyDictionary<string, OptionContractQuote>?> FetchQuotesForSymbols(string tradesSpec, CancellationToken cancellation)
	{
		var symbols = ParseAllLegs(tradesSpec).Select(leg => leg.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var (quotes, _) = await FetchQuotesAndUnderlyingForSymbolList(symbols, cancellation);
		return quotes;
	}

	internal static async Task<IReadOnlyDictionary<string, OptionContractQuote>?> FetchQuotesForSymbolList(IReadOnlyCollection<string> symbols, CancellationToken cancellation)
	{
		var (quotes, _) = await FetchQuotesAndUnderlyingForSymbolList(symbols, cancellation);
		return quotes;
	}

	internal static async Task<(IReadOnlyDictionary<string, OptionContractQuote>? Quotes, IReadOnlyDictionary<string, decimal>? UnderlyingPrices)> FetchQuotesAndUnderlyingForSymbolList(IReadOnlyCollection<string> symbols, CancellationToken cancellation)
	{
		if (symbols.Count == 0) return (new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
		var minimalRows = symbols.Select(s => new PositionRow(Instrument: s, Asset: Asset.Option, OptionKind: "Call", Side: Side.Buy, Qty: 1, AvgPrice: 0m, Expiry: null, MatchKey: MatchKeys.Option(s))).ToList();

		try
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath)) { Console.WriteLine("Error: api-config.json not found. Run 'sniff' first."); return (null, null); }
			var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
			if (config == null || config.Webull.Headers.Count == 0) { Console.WriteLine("Error: api-config.json has no headers. Run 'sniff' first."); return (null, null); }
			WebullAnalytics.Utils.Log.Debug($"Webull: fetching quotes for {symbols.Count} symbol(s)...");
			var (quotes, underlying) = await WebullOptionsClient.FetchOptionQuotesAsync(config, minimalRows, cancellation);
			return (quotes, underlying);
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Error: Failed to fetch quotes: {ex.Message}");
			return (null, null);
		}
	}

	/// <summary>Re-bases every quote's ImpliedVolatility to the mid-implied value — back-solved from the
	/// bid/ask mid at the dividend-adjusted spot, anchored at <see cref="OptionMath.ObservationInstant"/>
	/// (now live / last close off-hours). This is the mid-consistent surface the engine prices on; the
	/// vendor's reported IV field is 10–50 vol pts off at 0DTE. Quotes with no usable mid keep their existing
	/// (vendor) IV as the fallback. Shared by analyze position and analyze risk so the two can't drift.</summary>
	internal static IReadOnlyDictionary<string, OptionContractQuote> RecalibrateQuotesToMid(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		IReadOnlyDictionary<string, decimal> underlyingPrices,
		IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends)
	{
		var asOf = OptionMath.ObservationInstant();
		var result = new Dictionary<string, OptionContractQuote>(quotes.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, q) in quotes)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null || !underlyingPrices.TryGetValue(parsed.Root, out var spot)) { result[sym] = q; continue; }
			IReadOnlyList<DividendEvent>? divs = null;
			dividends?.TryGetValue(parsed.Root, out divs);
			var adjSpot = OptionMath.DividendAdjustedSpot(spot, divs, asOf, parsed.ExpiryDate.Date + OptionMath.MarketClose, OptionMath.RiskFreeRate);
			var iv = OptionMath.TryMarketImpliedIv(sym, parsed, adjSpot, asOf, quotes);
			result[sym] = iv.HasValue ? q with { ImpliedVolatility = iv.Value, VendorImpliedVolatility = q.VendorImpliedVolatility ?? q.ImpliedVolatility } : q;
		}
		return result;
	}

	internal static decimal ResolvePrice(ParsedLeg leg, IReadOnlyDictionary<string, OptionContractQuote>? quotes)
	{
		if (leg.Price.HasValue) return leg.Price.Value;
		if (leg.PriceKeyword == null) throw new InvalidOperationException($"Leg '{leg.Symbol}' has no price");
		if (quotes == null || !quotes.TryGetValue(leg.Symbol, out var quote))
			throw new InvalidOperationException($"No quote found for '{leg.Symbol}'");
		return leg.PriceKeyword switch
		{
			"BID" => quote.Bid ?? throw new InvalidOperationException($"No bid price for '{leg.Symbol}'"),
			"ASK" => quote.Ask ?? throw new InvalidOperationException($"No ask price for '{leg.Symbol}'"),
			"MID" => (quote.Bid ?? 0m) + (quote.Ask ?? 0m) == 0m ? throw new InvalidOperationException($"No bid/ask for '{leg.Symbol}'") : ((quote.Bid ?? 0m) + (quote.Ask ?? 0m)) / 2m,
			_ => throw new InvalidOperationException($"Unknown price keyword '{leg.PriceKeyword}'")
		};
	}

	internal static List<Trade> ParseSyntheticTrades(string tradesSpec, int startSeq, DateTime baseTimestamp, IReadOnlyDictionary<string, OptionContractQuote>? quotes = null)
	{
		// Spec format: legs comma-separated within a strategy group, groups separated by ';'.
      // Each multi-leg group becomes one PositionReplay Event AND emits an Asset.OptionStrategy
		// parent row — without that parent row, the report-table renderer sees orphan legs (no parent
		// at the synthetic ParentStrategySeq) and visually attaches them to whichever parent row
		// precedes them in seq order, lumping the synthetic into the last real strategy trade.
		// Mirrors JsonlParser's parent emission (line 131-141) for real Webull strategy orders.
		// Examples:
      //   "sell:A,buy:B"           ? one strategy parent + 2 legs
		//   "sell:A,buy:B;sell:C,buy:D" ? two parents + 4 legs
		//   "buy:A"                  ? single standalone leg, no parent
		var result = new List<Trade>();
		var seq = startSeq;
		var groups = tradesSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (var groupSpec in groups)
		{
			var legs = TradeLegParser.Parse(groupSpec);
			if (legs.Count == 0) continue;

			// Resolve all leg prices/sides up front so we can compute net cash for the parent row.
			var resolvedLegs = legs.Select(leg => new
			{
				Leg = leg,
				Parsed = leg.Option!,
				Price = ResolvePrice(leg, quotes),
				Side = leg.Action == LegAction.Buy ? Side.Buy : Side.Sell
			}).ToList();

			int? parentSeq = null;
			if (resolvedLegs.Count > 1)
			{
				parentSeq = seq++;
				var parentTimestamp = baseTimestamp.AddSeconds(seq - startSeq);
				var qty = resolvedLegs[0].Leg.Quantity; // strategy orders share qty across legs
														// Cash convention: positive = received, negative = paid. Mirror JsonlParser.
				var netCash = resolvedLegs.Sum(r => (r.Side == Side.Sell ? 1m : -1m) * r.Price * r.Leg.Quantity);
				var parentSide = netCash >= 0m ? Side.Sell : Side.Buy;
				var parentPrice = qty > 0 ? Math.Abs(netCash) / qty : 0m;
				var expDate = resolvedLegs.Max(r => r.Parsed.ExpiryDate);
				var root = resolvedLegs[0].Parsed.Root;
				var legsKey = string.Join(",", resolvedLegs.Select(r => $"{r.Parsed.CallPut}{Formatters.FormatQty(r.Parsed.Strike)}").OrderBy(s => s));
				var strategyKind = ParsingHelpers.ClassifyStrategyKind(
					legCount: resolvedLegs.Count,
					distinctExpiries: resolvedLegs.Select(r => r.Parsed.ExpiryDate).Distinct().Count(),
					distinctStrikes: resolvedLegs.Select(r => r.Parsed.Strike).Distinct().Count(),
					distinctCallPut: resolvedLegs.Select(r => r.Parsed.CallPut).Distinct().Count());
				var matchKey = $"strategy:{strategyKind}:{root}:{expDate:yyyy-MM-dd}:{legsKey}";
				var instrument = $"{root} {Formatters.FormatOptionDate(expDate)}";
				result.Add(new Trade(Seq: parentSeq.Value, Timestamp: parentTimestamp, Instrument: instrument, MatchKey: matchKey, Asset: Asset.OptionStrategy, OptionKind: strategyKind, Side: parentSide, Qty: qty, Price: parentPrice, Multiplier: Trade.OptionMultiplier, Expiry: expDate));
			}

			foreach (var r in resolvedLegs)
			{
				var timestamp = baseTimestamp.AddSeconds(seq - startSeq + 1);
				result.Add(new Trade(Seq: seq++, Timestamp: timestamp, Instrument: Formatters.FormatOptionDisplay(r.Parsed.Root, r.Parsed.ExpiryDate, r.Parsed.Strike), MatchKey: MatchKeys.Option(r.Leg.Symbol), Asset: Asset.Option, OptionKind: ParsingHelpers.CallPutDisplayName(r.Parsed.CallPut), Side: r.Side, Qty: r.Leg.Quantity, Price: r.Price, Multiplier: Trade.OptionMultiplier, Expiry: r.Parsed.ExpiryDate, ParentStrategySeq: parentSeq));
			}
		}

		return result;
	}

	internal static async Task<int> RunRollAnalysis(AnalyzeRollSettings settings, CancellationToken cancellation)
	{
		TerminalHelper.EnsureTerminalWidthFromConfig();

		var gtIdx = settings.Spec.IndexOf('>');
		var remaining = settings.Spec[(gtIdx + 1)..];
		var colonIdx = remaining.IndexOf(':');
		var oldSymbol = settings.Spec[..gtIdx];
		var newSymbol = colonIdx >= 0 ? remaining[..colonIdx] : remaining;
		var qty = colonIdx >= 0 ? int.Parse(remaining[(colonIdx + 1)..]) : 1;

		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSymbol)!;
		var newParsed = ParsingHelpers.ParseOptionSymbol(newSymbol)!;

		// Parse optional --pair leg for spread margin.
		OptionParsed? longOpt = null;
		string? longStockTicker = null;
		int longQty = 0;
		string? pairOccSymbol = null;
		if (!string.IsNullOrEmpty(settings.Pair))
		{
			var parts = settings.Pair.Split(':');
			longQty = int.Parse(parts[1], CultureInfo.InvariantCulture);
			longOpt = ParsingHelpers.ParseOptionSymbol(parts[0]);
			if (longOpt == null) longStockTicker = parts[0];
			else pairOccSymbol = parts[0];
		}

		// Fetch quotes for both legs to get IV and current prices
		var allSymbols = pairOccSymbol != null
			? new[] { oldSymbol, newSymbol, pairOccSymbol }
			: new[] { oldSymbol, newSymbol };
		var minimalRows = allSymbols.Select(s => new PositionRow(Instrument: s, Asset: Asset.Option, OptionKind: "Call", Side: Side.Buy, Qty: 1, AvgPrice: 0m, Expiry: null, MatchKey: MatchKeys.Option(s))).ToList();

		IReadOnlyDictionary<string, OptionContractQuote> quotes;
		IReadOnlyDictionary<string, decimal> underlyingPrices;
		try
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath)) { Console.WriteLine("Error: api-config.json not found. Run 'sniff' first."); return 1; }
			var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
			if (config == null || config.Webull.Headers.Count == 0) { Console.WriteLine("Error: api-config.json has no headers. Run 'sniff' first."); return 1; }
			WebullAnalytics.Utils.Log.Debug("Webull: fetching option chain data for roll analysis...");
			(quotes, underlyingPrices) = await WebullOptionsClient.FetchOptionQuotesAsync(config, minimalRows, cancellation);

			var riskFreeRate = await YahooOptionsClient.FetchRiskFreeRateAsync(cancellation);
			if (riskFreeRate.HasValue)
			{
				OptionMath.RiskFreeRate = riskFreeRate.Value;
				WebullAnalytics.Utils.Log.Debug($"Risk-free rate (13-week T-bill): {riskFreeRate.Value:P2}");
			}
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Error: Failed to fetch option data: {ex.Message}");
			return 1;
		}

		// Resolve IVs (--iv overrides take priority)
		var ivOverrides = settings.IvOverrides != null ? ReportCommand.ParseIvOverrides(settings.IvOverrides) : null;
		decimal? oldIv = ivOverrides != null && ivOverrides.TryGetValue(oldSymbol, out var oiv) ? oiv : quotes.TryGetValue(oldSymbol, out var oq) && oq.ImpliedVolatility > 0 ? oq.ImpliedVolatility : null;
		decimal? newIv = ivOverrides != null && ivOverrides.TryGetValue(newSymbol, out var niv) ? niv : quotes.TryGetValue(newSymbol, out var nq) && nq.ImpliedVolatility > 0 ? nq.ImpliedVolatility : null;

		if (!oldIv.HasValue || !newIv.HasValue)
		{
			Console.WriteLine($"Error: Could not determine IV for {(!oldIv.HasValue ? oldSymbol : newSymbol)}. Use --iv to override.");
			return 1;
		}

		var spot = underlyingPrices.TryGetValue(oldParsed.Root, out var sp) ? sp : 0m;
		if (settings.Spot != null)
		{
			var overrides = ReportCommand.ParseUnderlyingPriceOverrides(settings.Spot);
			if (overrides.TryGetValue(oldParsed.Root, out var ovr)) spot = ovr;
		}
		if (spot == 0) { Console.WriteLine($"Error: Could not determine underlying price for {oldParsed.Root}"); return 1; }

		// Current market prices
		var oldBid = quotes.TryGetValue(oldSymbol, out var obq) ? obq.Bid : null;
		var oldAsk = quotes.TryGetValue(oldSymbol, out var oaq) ? oaq.Ask : null;
		var newBid = quotes.TryGetValue(newSymbol, out var nbq) ? nbq.Bid : null;
		var newAsk = quotes.TryGetValue(newSymbol, out var naq) ? naq.Ask : null;

		decimal pairMid = 0m;
		if (pairOccSymbol != null && quotes.TryGetValue(pairOccSymbol, out var pq))
		{
			if (pq.Bid.HasValue && pq.Ask.HasValue) pairMid = (pq.Bid.Value + pq.Ask.Value) / 2m;
		}

		// Build price grid: roll credit at various underlying prices
		var rangeFactor = settings.Range > 0 ? settings.Range : 2m;
		var loStrike = Math.Min(oldParsed.Strike, newParsed.Strike);
		var hiStrike = Math.Max(oldParsed.Strike, newParsed.Strike);

		var today = EvaluationDate.Today;
		var oldExpiry = oldParsed.ExpiryDate;
		var newExpiry = newParsed.ExpiryDate;
		var rfr = OptionMath.RiskFreeRate;

		// Header
		Console.WriteLine();
      Console.WriteLine($"Roll Analysis: {Formatters.FormatOptionDisplay(oldParsed.Root, oldParsed.ExpiryDate, oldParsed.Strike)} {ParsingHelpers.CallPutDisplayName(oldParsed.CallPut)} → {Formatters.FormatOptionDisplay(newParsed.Root, newParsed.ExpiryDate, newParsed.Strike)} {ParsingHelpers.CallPutDisplayName(newParsed.CallPut)}  ({qty}x)");
		Console.WriteLine($"Current: {oldParsed.Root} @ ${spot}  |  Close {oldSymbol}: Bid ${oldBid?.ToString("N2") ?? "?"} / Ask ${oldAsk?.ToString("N2") ?? "?"}  |  Open {newSymbol}: Bid ${newBid?.ToString("N2") ?? "?"} / Ask ${newAsk?.ToString("N2") ?? "?"}");
		Console.WriteLine($"IV: Close leg {oldIv.Value:P1} | Open leg {newIv.Value:P1}");

		var isLong = string.Equals(settings.Side, "long", StringComparison.OrdinalIgnoreCase);
		var sideLabel = isLong ? "long" : "short";

		// Compute current market roll net: short-roll = new_bid - old_ask; long-roll = old_bid - new_ask.
		var currentCredit = isLong ? (oldBid ?? 0m) - (newAsk ?? 0m) : (newBid ?? 0m) - (oldAsk ?? 0m);
		Console.WriteLine($"Current roll net (natural, {sideLabel}): ${currentCredit:N4}/contract = ${currentCredit * qty * 100m:N2} total");

		if (!isLong)
		{
			var oldMarketMid = (oldBid.HasValue && oldAsk.HasValue) ? (oldBid.Value + oldAsk.Value) / 2m : 0m;
			var newMarketMid = (newBid.HasValue && newAsk.HasValue) ? (newBid.Value + newAsk.Value) / 2m : 0m;

			var oldCov = ComputeLegMargin(oldParsed, qty, spot, oldMarketMid, longOpt, longStockTicker, longQty, pairMid, isExisting: true);
			var newCov = ComputeLegMargin(newParsed, qty, spot, newMarketMid, longOpt, longStockTicker, longQty, pairMid, isExisting: false);

			var header = settings.Pair != null
				? $"Margin analysis (Reg-T estimate, at spot ${spot:N2}, with pair {Markup.Escape(settings.Pair)}):"
				: $"Margin analysis (Reg-T estimate, at spot ${spot:N2}):";
			Console.WriteLine(header);
			Console.WriteLine($"  Current requirement:  {oldCov.StatusLabel} = ${oldCov.Total:N2} total");
			Console.WriteLine($"  New requirement:      {newCov.StatusLabel} = ${newCov.Total:N2} total");
			var deltaMargin = newCov.Total - oldCov.Total;
			var deltaSign = deltaMargin >= 0 ? "+" : "-";
			Console.WriteLine($"  BP delta:             {deltaSign}${Math.Abs(deltaMargin):N2} total");

			if (!string.IsNullOrEmpty(settings.Cash))
			{
				var cash = decimal.Parse(settings.Cash, CultureInfo.InvariantCulture);
				var rollNetTotal = currentCredit * qty * 100m; // positive = credit; negative = debit
				var available = cash + rollNetTotal;
				var net = available - deltaMargin;
				var rollLabel = rollNetTotal >= 0m
					? $"${cash:N2} cash + ${rollNetTotal:N2} roll credit = ${available:N2}"
					: $"${cash:N2} cash - ${Math.Abs(rollNetTotal):N2} roll debit = ${available:N2}";
				var netSign = net >= 0m ? "+" : "-";
              var netLabel = net >= 0m ? "sufficient" : "shortfall — needs additional funds";
				Console.WriteLine();
				Console.WriteLine($"Funding check (--cash ${cash:N2}):");
				Console.WriteLine($"  Available:  {rollLabel}");
				Console.WriteLine($"  Required:   ${deltaMargin:N2} (BP delta)");
				Console.WriteLine($"  Net:        {netSign}${Math.Abs(net):N2} ({netLabel})");
			}
		}

		Console.WriteLine();

		// Compute max columns from terminal width using the same estimator as the report/break-even grids.
		// Each cell renders old|new|net, so size columns based on the widest leg/credit text rather than
		// the strike string length. This avoids overestimating how many date columns fit and prevents the
		// last columns from wrapping in narrower detailed windows.
		var terminalWidth = settings.Simplified ? TerminalHelper.SimplifiedMinWidth : TerminalHelper.DetailedMinWidth;
		try { terminalWidth = Math.Max(terminalWidth, Console.WindowWidth); } catch { /* use default */ }
		var maxLegValueWidth = Math.Max(oldBid ?? 0m, Math.Max(oldAsk ?? 0m, Math.Max(newBid ?? 0m, newAsk ?? 0m))).ToString("N2", CultureInfo.InvariantCulture).Length;
		var maxCols = Math.Max(3, TableBuilder.ComputeMaxGridColumns(terminalWidth, displayMode: "value", showLegs: true, maxLegCount: 2, maxLegValueWidth: maxLegValueWidth, gridTableOuterBorders: 2));

		// Build time columns: hourly on expiry day for <=1 DTE, daily otherwise
		var oldDays = (int)(oldExpiry.Date - today).TotalDays;
		var evalTimes = new List<DateTime>();
		var isIntraday = oldDays <= 1;
		if (isIntraday)
		{
			// Hourly on the expiry day from market open to 4 PM (options stop trading)
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
              // Ceiling division: ensures loop produces = (maxCols-1) items so total (with appended last) stays within maxCols.
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

		// Find the optimal credit price via fine search over a wide range spanning both strikes.
		var searchMin = Math.Max(0.01m, loStrike - OptionMath.GetPriceStep(loStrike) * 10m);
		var searchMax = hiStrike + OptionMath.GetPriceStep(hiStrike) * 10m;
		var searchStep = OptionMath.GetPriceStep(loStrike) / rangeFactor / 5m / 10m;
		var bestPrice = loStrike;
		var bestCredit = decimal.MinValue;
		for (var p = searchMin; p <= searchMax; p += searchStep)
		{
			var credit = evalTimes.Max(t =>
			{
				var newVal = OptionMath.BlackScholes(p, newParsed.Strike, (newExpiry.Date + OptionMath.MarketClose - t).TotalDays / 365.0, rfr, newIv.Value, newParsed.CallPut);
				var oldVal = OptionMath.BlackScholes(p, oldParsed.Strike, (oldExpiry.Date + OptionMath.MarketClose - t).TotalDays / 365.0, rfr, oldIv.Value, oldParsed.CallPut);
				return isLong ? oldVal - newVal : newVal - oldVal;
			});
			if (credit > bestCredit) { bestCredit = credit; bestPrice = Math.Round(p, 2); }
		}

		// Collect extra notables: spot, bestPrice, user levels.
		var extraNotables = new List<decimal> { spot, bestPrice };
		if (settings.Levels != null)
			foreach (var pair in ReportCommand.ParseLevels(settings.Levels))
				if (pair.Key.Equals(oldParsed.Root, StringComparison.OrdinalIgnoreCase))
					extraNotables.AddRange(pair.Value);

		// Build price rows targeting ~20 rows — same auto-step logic as the report grids.
		var priceList = TimeDecayGridBuilder.BuildPriceRows(spot, settings.Range, [], [oldParsed.Strike, newParsed.Strike], extraNotables);

		// Compute grid data for initial evalTimes.
		var (oldGrid, newGrid, creditGrid, maxCredit, maxCreditPrice, maxCreditDate, oldWidth, newWidth, creditWidth) =
			ComputeRollGrid(priceList, evalTimes, oldParsed, newParsed, oldExpiry, newExpiry, isLong, oldIv.Value, newIv.Value, rfr, today);

		// Opportunistically expand date columns using actual cell widths (mirrors BuildFittedGrid in BreakEvenAnalyzer).
		if (!isIntraday)
		{
			var priceColWidth = priceList.Max(p => $"${p:N2}".Length);
			for (int extra = 1; extra <= 5; extra++)
			{
				var moreTimes = TimeDecayGridBuilder.BuildDateColumns(oldExpiry, maxCols + extra);
				if (moreTimes.Count <= evalTimes.Count) break;
				var (og2, ng2, cg2, mc2, mcp2, mcd2, ow2, nw2, cw2) = ComputeRollGrid(priceList, moreTimes, oldParsed, newParsed, oldExpiry, newExpiry, isLong, oldIv.Value, newIv.Value, rfr, today);
				var cellW = Math.Max(6, ow2 + 1 + nw2 + 1 + cw2);
				// Rounded Spectre table: 1 (left\u2502) + (priceColWidth+2+1) + N*(cellW+2+1)
				var tableW = 1 + priceColWidth + 3 + moreTimes.Count * (cellW + 3);
				if (tableW > terminalWidth) break;
				(evalTimes, oldGrid, newGrid, creditGrid, maxCredit, maxCreditPrice, maxCreditDate, oldWidth, newWidth, creditWidth) =
					(moreTimes, og2, ng2, cg2, mc2, mcp2, mcd2, ow2, nw2, cw2);
			}
		}

		// Build 2D grid table now that evalTimes is finalized.
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn(new TableColumn("[bold]Price[/]").RightAligned().NoWrap());
		foreach (var t in evalTimes)
		{
			var label = isIntraday ? t.ToString("h tt") : (t == evalTimes[^1] ? "At Exp" : t.ToString("dd MMM"));
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

		AnsiConsole.Write(table);
		var maxDateLabel = isIntraday ? $"at {maxCreditDate:h:mm tt}" : $"on {maxCreditDate:dd MMM}";
		AnsiConsole.MarkupLine($"  [bold underline green]max net ({sideLabel})[/] (${maxCredit:N4} @ ${maxCreditPrice:N2} {maxDateLabel})    [bold yellow]current price[/]    [green]price with max net[/]");
		Console.WriteLine($"  Each cell: Close|Open|Net per contract. Total for {qty}x: max ${maxCredit * qty * 100m:N2}");
		Console.WriteLine();

		return 0;
	}

	/// <summary>
	/// Reg-T naked short option margin estimate (per contract, = per 100-share unit).
	/// Formula: max(0.20 * spot * 100 - OTM_amount * 100, 0.10 * strike * 100) + premium * 100.
	/// For calls, OTM_amount = max(strike - spot, 0).
	/// For puts,  OTM_amount = max(spot - strike, 0).
	/// premium is the per-share option value used to anchor the collateral (pass the market mid if available, else 0 for a conservative lower bound).
	/// </summary>
	internal static decimal EstimateNakedShortMargin(decimal spot, decimal strike, string callPut, decimal premium)
	{
		var otm = callPut == "C" ? Math.Max(strike - spot, 0m) : Math.Max(spot - strike, 0m);
		var primary = 0.20m * spot * 100m - otm * 100m;
		var floor = 0.10m * strike * 100m;
		return Math.Max(primary, floor) + premium * 100m;
	}

	/// <summary>
	/// Holds a per-leg combined margin result: status label for display plus the total margin in dollars.
	/// </summary>
	internal sealed record LegMargin(string StatusLabel, decimal Total);

	/// <summary>
	/// Computes combined margin for a single short leg paired with an optional static long leg.
  /// Covered time spreads / debit spreads do not require broker margin here — the debit is cash paid,
	/// not collateral. We therefore surface only true Reg-T-style collateral requirements:
	///   - naked shorts => naked margin
	///   - protected credit spreads / inverted diagonals => strike-width collateral on the covered leg
	///   - calendars / covered diagonals / debit verticals => 0
	/// where strike_loss = long_strike - short_strike for calls, short_strike - long_strike for puts.
	///
	/// Cases still treated as naked: no pair, wrong ticker, wrong call/put type, long expires before
	/// short, or long stock paired with a short put (long stock covers only short calls).
	/// </summary>
	internal static LegMargin ComputeLegMargin(OptionParsed shortLeg, int shortQty, decimal spot, decimal shortPremium, OptionParsed? longOpt, string? longStockTicker, int longQty, decimal longPremium, bool isExisting)
	{
		var naked = EstimateNakedShortMargin(spot, shortLeg.Strike, shortLeg.CallPut, shortPremium);

      // No pair ? naked on all contracts.
		if (longOpt == null && string.IsNullOrEmpty(longStockTicker))
         return new LegMargin($"naked  ${naked:N2}/contract × {shortQty}", naked * shortQty);

		// Long stock.
		if (longStockTicker != null)
		{
            if (!string.Equals(longStockTicker, shortLeg.Root, StringComparison.OrdinalIgnoreCase))
				return new LegMargin($"no cover (stock ticker '{longStockTicker}' ? '{shortLeg.Root}')  ${naked:N2}/contract × {shortQty}", naked * shortQty);
			if (shortLeg.CallPut != "C")
             return new LegMargin($"no cover (long stock does not cover short puts)  ${naked:N2}/contract × {shortQty}", naked * shortQty);
			var coverable = Math.Min(shortQty, longQty / 100);
			var uncovered = shortQty - coverable;
			var total = uncovered * naked;
            var label = uncovered == 0
				? $"covered by stock (long {longQty} shares)  $0.00/contract × {shortQty}"
				: $"partial cover ({coverable} covered by stock, {uncovered} naked @ ${naked:N2})";
			return new LegMargin(label, total);
		}

      // Long option — longOpt is guaranteed non-null here.
		var lo = longOpt!;
		if (shortLeg.CallPut != lo.CallPut)
         return new LegMargin($"no cover (long {(lo.CallPut == "C" ? "call" : "put")} does not cover short {(shortLeg.CallPut == "C" ? "call" : "put")})  ${naked:N2}/contract × {shortQty}", naked * shortQty);

		if (lo.ExpiryDate < shortLeg.ExpiryDate)
         return new LegMargin($"no cover (long expires {lo.ExpiryDate:yyyy-MM-dd} < short expires {shortLeg.ExpiryDate:yyyy-MM-dd})  ${naked:N2}/contract × {shortQty}", naked * shortQty);

		// Covered spread / diagonal collateral:
		// - calendars / covered diagonals / debit verticals => $0
		// - credit verticals => strike-width collateral
		// - inverted diagonals => strike-width assignment loss + entry debit
		var strikeLoss = shortLeg.CallPut == "C"
			? Math.Max(lo.Strike - shortLeg.Strike, 0m)
			: Math.Max(shortLeg.Strike - lo.Strike, 0m);
		var isTimeSpread = lo.ExpiryDate > shortLeg.ExpiryDate;
		var debit = Math.Max(longPremium - shortPremium, 0m);
		var coveredPer = strikeLoss == 0m
			? 0m
			: isTimeSpread
				? strikeLoss * 100m + debit * 100m
				: strikeLoss * 100m;

		var coverableOpt = Math.Min(shortQty, longQty);
		var uncoveredOpt = shortQty - coverableOpt;
		var totalOpt = coverableOpt * coveredPer + uncoveredOpt * naked;

        // Label explains the structure: strike_loss = 0 ? no-margin covered structure; positive ? margin-capped spread.
		var structureLabel = strikeLoss == 0m
		  ? (lo.Strike == shortLeg.Strike ? "calendar" : lo.ExpiryDate == shortLeg.ExpiryDate ? "debit vertical" : "covered diagonal")
			: lo.ExpiryDate == shortLeg.ExpiryDate ? $"credit vertical (width ${strikeLoss * 100m:N2})" : $"inverted diagonal (strike loss ${strikeLoss * 100m:N2})";
		var costBreakdown = coveredPer == 0m
			? "$0.00 margin/contract"
		  : isTimeSpread && strikeLoss > 0m
				? $"${strikeLoss * 100m:N2} strike + ${debit * 100m:N2} debit = ${coveredPer:N2} margin/contract"
			: $"${coveredPer:N2} margin/contract";
        var labelOpt = uncoveredOpt == 0
			? $"{structureLabel}  {costBreakdown} × {shortQty}"
			: $"partial cover ({structureLabel}: {coverableOpt} @ ${coveredPer:N2}, {uncoveredOpt} naked @ ${naked:N2})";
		return new LegMargin(labelOpt, totalOpt);
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
				var oldDte = (oldExpiry.Date + OptionMath.MarketClose - evalTimes[di]).TotalDays / 365.0;
				var newDte = (newExpiry.Date + OptionMath.MarketClose - evalTimes[di]).TotalDays / 365.0;
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
