using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.IO;
using WebullAnalytics.Positions;
using WebullAnalytics.Pricing;
using WebullAnalytics.Report;
using WebullAnalytics.Trading;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

// --- `analyze trade` ----------------------------------------------------------

/// <summary>`analyze trade` judges a trade on its own: it renders only the time-decay grid (break-even panel) of the legs given.
/// The book-level view — the trade merged into your positions, realized P&amp;L, rolls, summary — is `report --trade`.</summary>
internal sealed class AnalyzeTradeSettings : GridSettings
{
	[CommandArgument(0, "[spec]")]
	[Description("Hypothetical trades. Format: ACTION:SYMBOL:QTY@PRICE where ACTION is buy|sell, SYMBOL is an OCC option symbol, and PRICE is a decimal or BID|MID|ASK. Legs comma-separated; ';' separates strategy groups (each its own panel). Example: buy:GME260501C00023000:300@MID. Omit when using --proposal or --fill.")]
	public string Spec { get; set; } = "";

	[CommandOption("--proposal")]
	[Description("Reconstruct the hypothetical trade from a stored proposal snapshot (its legs, entry mids, spot, IVs and date) instead of the <spec> argument. Format: FILE[[:LINE]] where FILE is a path, a data/ filename (ai-proposals.SPY.0DTE.jsonl), or the TICKER.strategy shorthand (SPY.0DTE); LINE is a 1-based line number, defaulting to the last line.")]
	public string? Proposal { get; set; }

	[CommandOption("--fill [N]")]
	[Description("Evaluate one of your recent OPENING fills (Webull order history, last 30 days, any ticker), its per-leg fill prices as the cost basis, at --date: the live market now by default, --date fill for the market at the fill itself. Bare --fill picks interactively; N is the 1-based position in that list, newest first (not a file line).")]
	public FlagValue<int>? Fill { get; set; }

	[CommandOption("--compare")]
	[Description("After the panels, a table with one row per ';' group, each evaluated as if it were the only trade: net at the spec's prices and at the natural (ask/bid), margin, max profit/loss, profit/margin, break-evens, and the opener scorer's POP and EV (as `analyze risk` scores that group). The panels are unchanged.")]
	public bool Compare { get; set; }

	protected override bool FillAllowed => Fill?.IsSet == true;

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (OutputFormat.Equals("excel", StringComparison.OrdinalIgnoreCase))
			return ValidationResult.Error("--output must be 'console' or 'text' (excel exports the whole book: use `report --trade`)");

		var sources = (string.IsNullOrWhiteSpace(Spec) ? 0 : 1) + (Proposal != null ? 1 : 0) + (Fill?.IsSet == true ? 1 : 0);
		if (sources > 1)
			return ValidationResult.Error("pass only one of <spec>, --proposal or --fill");
		if (sources == 0)
			return ValidationResult.Error("provide <spec>, --proposal or --fill");
		if (Fill?.IsSet == true && Fill.Value < 0)
			return ValidationResult.Error($"--fill: N must be ≥ 1, got {Fill.Value}");
		if (!string.IsNullOrWhiteSpace(Spec) && AnalyzeCommon.ValidateTradeSpec(Spec, "<spec>") is { } specError)
			return ValidationResult.Error(specError);

		return ValidationResult.Success();
	}
}

internal sealed class AnalyzeTradeCommand : AsyncCommand<AnalyzeTradeSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, AnalyzeTradeSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);
		if (settings.OutputFormat.Equals("excel", StringComparison.OrdinalIgnoreCase)) settings.OutputFormat = "console"; // a config-level report default; excel is the whole-book export

		if (settings.Proposal != null && !ReconstructSpecFromProposal(settings))
			return 1;

		ReportCommand.ApplyEvaluationDate(settings);
		var atFill = false;
		if (settings.Fill?.IsSet == true)
		{
			var (spec, _, error) = await OpeningFillPicker.ResolveAtAsync(settings.Fill.Value, settings.DateSpec, cancellation);
			if (spec == null) { Console.Error.WriteLine($"Error: {error}"); return 1; }
			settings.Spec = spec;
			atFill = settings.DateSpec.Kind == AsOfKind.Fill;
		}

		var trades = new List<Trade>();
		var baseTime = EvaluationDate.IsPast ? EvaluationDate.Instant!.Value : DateTime.Now;
		var added = await AnalyzeCommon.AppendSyntheticTrades(trades, settings.Spec, baseTime, LiveQuoteSource.ResolveVendor(settings.Vendor), cancellation);
		if (added == null) return 1;

		var closeLookup = await ReportCommand.BuildUnderlyingCloseLookup(trades, cancellation);
		var (_, positions, _) = PositionTracker.ComputeReport(trades, 0m, null, closeLookup);
		var (positionRows, _, _) = PositionTracker.BuildPositionRows(positions, PositionTracker.BuildTradeIndex(trades), trades);
		if (positionRows.Count == 0)
		{
			Console.WriteLine("No open position — the legs net to zero.");
			return 0;
		}

		var (opts, optsError) = await ReportCommand.BuildAnalysisOptions(settings, positionRows, cancellation, added.Value.Quotes, added.Value.Underlyings, preloadedAsOf: added.Value.AsOf, nearestBoundary: atFill);
		if (opts == null) { Console.Error.WriteLine($"Error: {optsError}"); return 1; }
		var displayMode = settings.DisplayMode.ToLowerInvariant();
		var toText = settings.OutputFormat.Equals("text", StringComparison.OrdinalIgnoreCase);
		var writer = toText ? new StringWriter() : null;
		var console = writer != null ? TextFileExporter.CreateTextConsole(writer) : AnsiConsole.Console;
		TableRenderer.WriteBreakEvenPanels(console, positionRows, opts, settings.Range, displayMode, settings.ShowLegs, ascii: toText);
		if (settings.Compare)
		{
			var comparison = await TradeComparison.BuildAsync(settings.Spec, opts, added.Value.Quotes, settings, baseTime, closeLookup, console.Profile.Width, scoreAtCostBasis: settings.Fill?.IsSet == true, cancellation);
			TradeComparison.Render(console, comparison, opts, ascii: toText);
		}
		if (writer != null) TextFileExporter.WriteConsoleOutputToTextFile(writer, settings.OutputPath ?? AnalyzeCommon.DefaultTextOutputName("trade"), "Trade analysis exported to");
		return 0;
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

	/// <summary>Error message for a malformed trade spec (null when valid): every leg must be an OCC option with an @PRICE
	/// (decimal or BID|MID|ASK). <paramref name="label"/> names the option in the message (&lt;spec&gt;, --trade).</summary>
	internal static string? ValidateTradeSpec(string tradesSpec, string label)
	{
		List<ParsedLeg> legs;
		try { legs = ParseAllLegs(tradesSpec).ToList(); }
		catch (FormatException ex) { return $"{label}: {ex.Message}"; }
		if (legs.Count == 0) return $"{label}: no legs";
		foreach (var leg in legs)
		{
			if (leg.Option == null) return $"{label}: '{leg.Symbol}' is not an OCC option symbol (option legs only)";
			if (leg.Price == null && leg.PriceKeyword == null) return $"{label}: leg '{leg.Symbol}' is missing @PRICE (a decimal or BID|MID|ASK is required)";
		}
		return null;
	}

	/// <summary>Appends <paramref name="tradesSpec"/> to <paramref name="trades"/> as synthetic trades stamped at <paramref name="baseTime"/>
	/// (sequenced after the existing ones), first fetching live quotes when a leg prices at BID|MID|ASK. Returns the quotes and
	/// underlyings that fetch loaded (empty when none was needed), or null after printing the error when it failed.</summary>
	/// <summary>Appends the spec's trades, resolving BID/MID/ASK prices from the market at the evaluation instant. <c>AsOf</c> is true when
	/// those quotes are a past instant's real books (the report pipeline then reuses them instead of loading them again).</summary>
	internal static async Task<(IReadOnlyDictionary<string, OptionContractQuote>? Quotes, IReadOnlyDictionary<string, decimal>? Underlyings, bool AsOf)?> AppendSyntheticTrades(List<Trade> trades, string tradesSpec, DateTime baseTime, QuoteVendor vendor, CancellationToken cancellation)
	{
		IReadOnlyDictionary<string, OptionContractQuote>? quotes = null;
		IReadOnlyDictionary<string, decimal>? underlyings = null;
		var asOf = false;
		if (NeedsMarketPrices(tradesSpec))
		{
			var symbols = ParseAllLegs(tradesSpec).Select(leg => leg.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			// BID/MID/ASK at a past evaluation instant are that instant's books, not today's.
			if (EvaluationDate.IsPast)
			{
				var (market, error) = await HistoricalMarkOverlay.ApplyAsync(EvaluationDate.Instant!.Value, nearest: false, symbols, null, null, cancellation);
				if (market == null) { Console.Error.WriteLine($"Error: can't price BID/MID/ASK at {EvaluationDate.Instant:yyyy-MM-dd HH:mm} ET — {error}"); return null; }
				(quotes, underlyings, asOf) = (market.Quotes, market.UnderlyingPrices, true);
			}
			else
			{
				(quotes, underlyings) = await FetchQuotesAndUnderlyingForSymbolList(symbols, vendor, cancellation);
				if (quotes == null) return null;
			}
		}
		var nextSeq = trades.Count > 0 ? trades.Max(t => t.Seq) + 1 : 0;
		trades.AddRange(ParseSyntheticTrades(tradesSpec, nextSeq, baseTime, quotes));
		return (quotes, underlyings, asOf);
	}

	internal static bool NeedsMarketPrices(string tradesSpec) =>
		ParseAllLegs(tradesSpec).Any(leg => leg.PriceKeyword != null);

	private static string FormatLegSpec(ParsedLeg leg)
	{
		var price = leg.Price?.ToString(CultureInfo.InvariantCulture) ?? leg.PriceKeyword;
		return price == null
			? $"{leg.Action.ToString().ToLowerInvariant()}:{leg.Symbol}:{leg.Quantity}"
			: $"{leg.Action.ToString().ToLowerInvariant()}:{leg.Symbol}:{leg.Quantity}@{price}";
	}

	internal static async Task<IReadOnlyDictionary<string, OptionContractQuote>?> FetchQuotesForSymbols(string tradesSpec, QuoteVendor vendor, CancellationToken cancellation)
	{
		var symbols = ParseAllLegs(tradesSpec).Select(leg => leg.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var (quotes, _) = await FetchQuotesAndUnderlyingForSymbolList(symbols, vendor, cancellation);
		return quotes;
	}

	internal static async Task<IReadOnlyDictionary<string, OptionContractQuote>?> FetchQuotesForSymbolList(IReadOnlyCollection<string> symbols, QuoteVendor vendor, CancellationToken cancellation)
	{
		var (quotes, _) = await FetchQuotesAndUnderlyingForSymbolList(symbols, vendor, cancellation);
		return quotes;
	}

	internal static async Task<(IReadOnlyDictionary<string, OptionContractQuote>? Quotes, IReadOnlyDictionary<string, decimal>? UnderlyingPrices)> FetchQuotesAndUnderlyingForSymbolList(IReadOnlyCollection<string> symbols, QuoteVendor vendor, CancellationToken cancellation)
	{
		if (symbols.Count == 0) return (new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

		try
		{
			var (quotes, underlying) = await FetchLiveChainByVendor(symbols, vendor, cancellation);
			return (quotes, underlying);
		}
		catch (Exception ex)
		{
			if (ex is OperationCanceledException) throw;
			Console.WriteLine($"Error: Failed to fetch quotes: {ex.Message}");
			return (null, null);
		}
	}

	/// <summary>THE single live-chain fetch seam: every analyze/report command resolves its vendor to a
	/// <see cref="QuoteVendor"/> and calls this — the one place vendor is interpreted, so adding a vendor is a
	/// single new arm here rather than an if/else in each command. Returns the full chain + spots per requested
	/// root (so far-dated held legs are covered), with a uniform realized-vol HV backfilled on every leg
	/// regardless of vendor (<see cref="FillMissingHistoricalVolAsync"/>) so IV+HV read consistently. When
	/// <paramref name="targetExpiries"/> is supplied (single-root position/GEX use), the Webull arm refreshes
	/// OI/IV breadth around those expiries so max-pain/GEX don't lock onto the position's own strike; Schwab's
	/// range=ALL chain already carries that breadth. Throws on missing credentials so the caller can degrade.</summary>
	internal static async Task<(IReadOnlyDictionary<string, OptionContractQuote> Quotes, IReadOnlyDictionary<string, decimal> Underlyings)> FetchLiveChainByVendor(
		IReadOnlyCollection<string> symbols, QuoteVendor vendor, CancellationToken cancellation, IReadOnlyCollection<DateTime>? targetExpiries = null)
	{
		IReadOnlyDictionary<string, OptionContractQuote> quotes;
		IReadOnlyDictionary<string, decimal> underlyings;

		if (vendor == QuoteVendor.Schwab)
		{
			var symbolSet = symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var tickers = symbols.Select(s => ParsingHelpers.ParseOptionSymbol(s)?.Root).Where(r => r != null).Select(r => r!).ToHashSet(StringComparer.OrdinalIgnoreCase);
			WebullAnalytics.Utils.Log.Debug($"Schwab: fetching quotes for {symbols.Count} symbol(s)...");
			var snap = await new LiveQuoteSource(QuoteVendor.Schwab).GetQuotesAsync(OptionMath.ObservationInstant(), symbolSet, tickers, cancellation);
			(quotes, underlyings) = (snap.Options, snap.Underlyings);
		}
		else
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath)) throw new InvalidOperationException("api-config.json not found. Run 'sniff' first.");
			var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
			if (config == null || config.Webull.Headers.Count == 0) throw new InvalidOperationException("api-config.json has no headers. Run 'sniff' first.");

			if (targetExpiries is { Count: > 0 })
			{
				// Position/GEX path: full chain with OI/IV breadth around each target expiry, then queryBatch any
				// position legs the chain left unpriced (deep-OTM wings / far-dated expiries outside the cycle).
				var root = ParsingHelpers.ParseOptionSymbol(symbols.First())?.Root ?? throw new InvalidOperationException($"Cannot parse root from '{symbols.First()}'.");
				WebullAnalytics.Utils.Log.Debug($"Webull: fetching chain (expiry-refresh) for {root}...");
				var (chain, spot, derivativeIds) = await WebullOptionsClient.FetchChainWithExpiryRefreshAsync(config, root, targetExpiries, 0.20m, cancellation);
				var mutable = new Dictionary<string, OptionContractQuote>(chain, StringComparer.OrdinalIgnoreCase);
				var missing = symbols.Where(s => !mutable.TryGetValue(s, out var q) || q.Bid == null || q.Ask == null).ToList();
				if (missing.Count > 0)
				{
					var ids = new Dictionary<string, long>(derivativeIds, StringComparer.OrdinalIgnoreCase);
					foreach (var s in missing)
						if (!ids.ContainsKey(s) && DerivativeIdRegistry.TryGetId(s, out var rid)) ids[s] = rid;
					await WebullOptionsClient.RefreshContractsAsync(config, mutable, missing, ids, cancellation);
				}
				quotes = mutable;
				underlyings = spot.HasValue ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [root] = spot.Value } : new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
			}
			else
			{
				var minimalRows = symbols.Select(s => new PositionRow(Instrument: s, Asset: Asset.Option, OptionKind: "Call", Side: Side.Buy, Qty: 1, AvgPrice: 0m, Expiry: null, MatchKey: MatchKeys.Option(s))).ToList();
				WebullAnalytics.Utils.Log.Debug($"Webull: fetching quotes for {symbols.Count} symbol(s)...");
				(quotes, underlyings) = await WebullOptionsClient.FetchOptionQuotesAsync(config, minimalRows, cancellation);
			}
			OptionMath.LiveQuotesStruckAt = WebullOptionsClient.LastFetchHadLivePremarketBook
				? MarketTime.EtToInstant(MarketTime.NowEt())
				: null;
		}

		underlyings = BookSpots(quotes, underlyings, MarketTime.NowEt(), await ExDividendsSinceStrikeAsync(underlyings.Keys, cancellation));
		// Clock-consistent IVs against the book's own spot. The Schwab branch already normalized inside LiveQuoteSource against the
		// vendor spot; normalizing is idempotent (it always solves from the mid, else the preserved vendor IV), so re-running it here
		// re-bases those IVs too when BookSpots moved the spot.
		quotes = LiveQuoteSource.NormalizeIvs(quotes, underlyings);
		quotes = await ApplyRealizedVolAsync(quotes, cancellation);
		return (quotes, underlyings);
	}

	/// <summary>The spot each root's option book is actually struck at. During RTH that is the vendor's live spot. Outside it the book
	/// is either frozen at its last print — which is neither the 16:00 equity close nor the post-market last (SPY's options trade to
	/// 16:15; on 2026-11-20 the frozen book's parity sat at 693.26 between the 689.81 close Webull reports and the 697.48 post-market
	/// last Schwab reports) — or live in a global session; either way its own put-call parity is the spot it prices. Parity is read
	/// on expiries that end before the root's next ex-dividend date in <paramref name="exDividends"/> (a dividend inside the
	/// expiry reads the spot low by its PV). Roots whose chain has no two-sided call/put pair on such an expiry keep the vendor spot.</summary>
	internal static IReadOnlyDictionary<string, decimal> BookSpots(IReadOnlyDictionary<string, OptionContractQuote> quotes, IReadOnlyDictionary<string, decimal> vendorSpots, DateTime nowEt, IReadOnlyDictionary<string, DateTime> exDividends)
	{
		if (SchwabOptionsClient.IsRegularTradingHours(nowEt) || vendorSpots.Count == 0) return vendorSpots;
		var result = new Dictionary<string, decimal>(vendorSpots, StringComparer.OrdinalIgnoreCase);
		foreach (var root in vendorSpots.Keys)
		{
			var parityRoot = string.Equals(root, "SPX", StringComparison.OrdinalIgnoreCase) ? "SPXW" : root;
			DateTime? exDiv = exDividends.TryGetValue(root, out var ex) ? ex : null;
			if (AI.PremarketSpotOverride.DeriveSpotFromParity(parityRoot, quotes, OptionMath.RiskFreeRate, nowEt, allowLastPrice: false, excludeExpiriesFrom: exDiv) is { Spot: > 0m } parity)
				result[root] = Math.Round(parity.Spot, 2);
		}
		return result;
	}

	/// <summary>Each root's next ex-dividend date when it falls after the session the quotes were struck in (<see cref="OptionMath.ObservationInstant"/>):
	/// the dividend a frozen or live book is still carrying. Off-hours only — <see cref="BookSpots"/> doesn't read parity during RTH. Roots
	/// with no scheduled dividend (cash indexes, non-payers) are absent.</summary>
	private static async Task<IReadOnlyDictionary<string, DateTime>> ExDividendsSinceStrikeAsync(IEnumerable<string> roots, CancellationToken cancellation)
	{
		var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		var list = roots.ToList();
		if (list.Count == 0 || SchwabOptionsClient.IsRegularTradingHours(MarketTime.NowEt())) return result;
		var struckOn = OptionMath.ObservationInstant().Date;
		var calendar = await AI.Events.EventCalendarLoader.LoadAsync(list, new AI.OpenerEventsConfig(), EvaluationDate.Today, cancellation);
		foreach (var root in list)
			if (calendar.Get(root)?.NextExDividendDate is DateTime ex && ex.Date > struckOn)
				result[root] = ex.Date;
		return result;
	}

	/// <summary>Moves the board to the spot each run evaluates at: every root in <paramref name="overrides"/> (--spot / --extended /
	/// --overnight) is re-marked sticky-strike (<see cref="QuoteRemark.StickyStrike"/>) from the spot its book is struck at
	/// (<paramref name="bookSpots"/>) to the override. Returns the re-marked quotes and the spot map they are now consistent with
	/// (book spots with the overrides applied), so downstream code treats the result as a book struck at the evaluation spot —
	/// mids, IV back-solves and theoretical prices all agree on it.</summary>
	internal static (IReadOnlyDictionary<string, OptionContractQuote> Quotes, IReadOnlyDictionary<string, decimal> Spots) RemarkToSpots(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		IReadOnlyDictionary<string, decimal> bookSpots,
		IReadOnlyDictionary<string, decimal>? overrides)
	{
		if (overrides == null || overrides.Count == 0) return (quotes, bookSpots);
		var spots = new Dictionary<string, decimal>(bookSpots, StringComparer.OrdinalIgnoreCase);
		foreach (var (root, target) in overrides)
		{
			if (!ParsingHelpers.TryResolveForRoot(bookSpots, root, out var book) || book <= 0m || target <= 0m) { spots[root] = target; continue; }
			spots[root] = target;
			if (book == target) continue;
			quotes = QuoteRemark.StickyStrike(quotes, root, book, target, out var unpriced);
			var note = unpriced > 0 ? $"; {unpriced} quote(s) with no IV left as quoted" : "";
			AnsiConsole.MarkupLine($"[dim]{Markup.Escape(root)}: option quotes re-marked from the book's ${book:N2} to ${target:N2} (sticky-strike IV){Markup.Escape(note)}.[/]");
		}
		return (quotes, spots);
	}

	/// <summary>Merges explicit <c>--spot</c> entries with the live <paramref name="session"/>'s prices (<c>--extended</c> /
	/// <c>--overnight</c>) for <paramref name="tickers"/>. Explicit <c>--spot</c> entries win per ticker; the session fills in any
	/// remaining tickers (no-op at a past <c>--date</c>). Returns null when neither yields an override.</summary>
	internal static async Task<Dictionary<string, decimal>?> ResolveSpotOverrideMapAsync(
		string? spotSpec,
		SpotSession session,
		IEnumerable<string> tickers,
		QuoteVendor vendor,
		IReadOnlyDictionary<string, decimal>? rthSpots,
		CancellationToken cancellation)
	{
		var overrides = !string.IsNullOrWhiteSpace(spotSpec)
			? ReportCommand.ParseUnderlyingPriceOverrides(spotSpec)
			: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		if (session != SpotSession.Regular && !EvaluationDate.IsPast)
		{
			var missing = tickers
				.Where(t => !string.IsNullOrWhiteSpace(t) && !ParsingHelpers.TryResolveForRoot(overrides, t, out _))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (missing.Count > 0 && session == SpotSession.Overnight)
			{
				foreach (var (t, price) in await ResolveOvernightSpotsAsync(missing, cancellation))
					overrides[t] = price;
			}
			else if (missing.Count > 0)
			{
				var extSpots = await ResolveExtendedSpotsAsync(missing, vendor, cancellation, rthSpots);
				foreach (var (t, price) in extSpots)
				{
					// When the chain fetch already detected that the option book is live in premarket (so rthSpots[t]
					// is already the live extended-hours spot rather than yesterday's 16:00 close), only record an
					// override if the extended-hours spot meaningfully differs from the book's own live spot.
					if (rthSpots != null && ParsingHelpers.TryResolveForRoot(rthSpots, t, out var bookSpot) && bookSpot > 0m
						&& WebullOptionsClient.LastFetchHadLivePremarketBook && vendor == QuoteVendor.Webull)
						continue;
					if (rthSpots != null && ParsingHelpers.TryResolveForRoot(rthSpots, t, out var schwabBookSpot) && schwabBookSpot > 0m
						&& Math.Round(schwabBookSpot, 2) == Math.Round(price, 2))
						continue;
					overrides[t] = price;
				}
			}
		}

		return overrides.Count > 0 ? overrides : null;
	}

	/// <summary>Resolves the latest overnight-session (20:00–04:00 ET) price for each ticker from Webull's chart tape, whatever the
	/// quote vendor (Schwab has no overnight print). A ticker whose latest bar is not an overnight one — the session is closed, or the
	/// symbol doesn't trade in it — gets no entry and a warning, so it keeps its regular spot rather than a stale post-market price.
	/// Nothing is cached or written to disk.</summary>
	private static async Task<Dictionary<string, decimal>> ResolveOvernightSpotsAsync(IReadOnlyList<string> tickers, CancellationToken cancellation)
	{
		var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var apiConfig = AI.OpenCandidateEvaluator.TryLoadApiConfig();
		if (apiConfig is not { Webull.Headers.Count: > 0 })
		{
			AnsiConsole.MarkupLine("[yellow]--overnight: no Webull headers in api-config.json (run 'sniff'); using the regular spot.[/]");
			return result;
		}

		foreach (var ticker in tickers.Select(t => t.Trim().ToUpperInvariant()))
		{
			var bar = await WebullIntradayBars.FetchLatestOvernightBarAsync(apiConfig, ticker, cancellation);
			if (bar == null)
			{
				AnsiConsole.MarkupLine($"[yellow]--overnight: no overnight-session price for {Markup.Escape(ticker)}; using the regular spot.[/]");
				continue;
			}
			result[ticker] = Math.Round(bar.Close, 2);
			AnsiConsole.MarkupLine($"[dim]--overnight: {Markup.Escape(ticker)} spot {result[ticker]:0.00} from the {MarketTime.ToEt(bar.Timestamp):ddd HH:mm} ET overnight bar.[/]");
		}
		return result;
	}

	/// <summary>Resolves the latest extended-hours (pre/post-market) spot price for each ticker in <paramref name="tickers"/>.
	/// On Schwab, non-index tickers read the extended-hours <c>underlyingPrice</c> already captured by the chain fetch (zero
	/// extra round-trips). On Webull — and for SPX-family cash indexes (<c>SPXW</c>/<c>SPX</c>/<c>XSP</c>) on either vendor,
	/// which have no extended-hours index print of their own — pulls the latest extended-hours 1-minute bar via
	/// <see cref="WebullIntradayBars"/> (which splices and rescales SPY's extended-hours tape onto the index's RTH level).
	/// If Webull headers are unavailable on a Schwab index run, rescales the index's RTH spot by Schwab's SPY extended/RTH ratio.</summary>
	internal static async Task<Dictionary<string, decimal>> ResolveExtendedSpotsAsync(
		IEnumerable<string> tickers,
		QuoteVendor vendor,
		CancellationToken cancellation,
		IReadOnlyDictionary<string, decimal>? rthSpots = null)
	{
		var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		var distinct = tickers
			.Where(t => !string.IsNullOrWhiteSpace(t))
			.Select(t => t.Trim().ToUpperInvariant())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (distinct.Count == 0) return result;

		var apiConfig = AI.OpenCandidateEvaluator.TryLoadApiConfig();
		AI.Replay.IntradayBarFetcher? webullFetcher = apiConfig is { Webull.Headers.Count: > 0 }
			? WebullIntradayBars.CreateFetcher(apiConfig)
			: null;

		foreach (var ticker in distinct)
		{
			var isSpxFamily = WebullIntradayBars.SpxFamilyTickers.Contains(ticker);
			decimal? extSpot = null;

			if (vendor == QuoteVendor.Schwab && !isSpxFamily)
			{
				extSpot = SchwabOptionsClient.TryGetExtendedSpot(ticker)
					?? await TryFetchSchwabExtendedSpotAsync(apiConfig, ticker, cancellation);
			}

			if (extSpot is not > 0m && webullFetcher != null)
			{
				try
				{
					var bars = await webullFetcher(ticker, BarInterval.M1, count: 5, includeExtended: true, cancellation);
					if (bars.Count > 0 && bars[^1].Close > 0m)
						extSpot = bars[^1].Close;
				}
				catch (Exception ex) when (!cancellation.IsCancellationRequested)
				{
					Log.Debug($"Extended spot: Webull bar fetch failed for {ticker}: {ex.Message}");
				}
			}

			if (extSpot is not > 0m && isSpxFamily && rthSpots != null
				&& ParsingHelpers.TryResolveForRoot(rthSpots, ticker, out var idxRth) && idxRth > 0m)
			{
				var spyRatio = await TryGetSchwabSpyExtendedRatioAsync(apiConfig, cancellation);
				if (spyRatio is > 0m)
					extSpot = Math.Round(idxRth * spyRatio.Value, 2);
			}

			if (extSpot is not > 0m && !isSpxFamily && vendor != QuoteVendor.Schwab)
			{
				extSpot = SchwabOptionsClient.TryGetExtendedSpot(ticker)
					?? await TryFetchSchwabExtendedSpotAsync(apiConfig, ticker, cancellation);
			}

			if (extSpot is > 0m)
				result[ticker] = Math.Round(extSpot.Value, 2);
		}

		return result;
	}

	private static async Task<decimal?> TryFetchSchwabExtendedSpotAsync(ApiConfig? apiConfig, string ticker, CancellationToken cancellation)
	{
		if (apiConfig?.Schwab is null || string.IsNullOrEmpty(apiConfig.Schwab.RefreshToken)) return null;
		try
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			var token = await SchwabAuthClient.GetAccessTokenAsync(apiConfig.Schwab, configPath, cancellation);
			var today = DateOnly.FromDateTime(EvaluationDate.Today);
			await SchwabOptionsClient.FetchChainAsync(token, ticker, today, today.AddDays(7), cancellation, strikeCount: 1);
			return SchwabOptionsClient.TryGetExtendedSpot(ticker);
		}
		catch (Exception ex) when (!cancellation.IsCancellationRequested)
		{
			Log.Debug($"Extended spot: Schwab fallback failed for {ticker}: {ex.Message}");
			return null;
		}
	}

	private static async Task<decimal?> TryGetSchwabSpyExtendedRatioAsync(ApiConfig? apiConfig, CancellationToken cancellation)
	{
		if (apiConfig?.Schwab is null || string.IsNullOrEmpty(apiConfig.Schwab.RefreshToken)) return null;
		try
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			var token = await SchwabAuthClient.GetAccessTokenAsync(apiConfig.Schwab, configPath, cancellation);
			var today = DateOnly.FromDateTime(EvaluationDate.Today);
			var (spyRth, _) = await SchwabOptionsClient.FetchChainAsync(token, "SPY", today, today.AddDays(7), cancellation, strikeCount: 1);
			var spyExt = SchwabOptionsClient.TryGetExtendedSpot("SPY");
			return spyRth is > 0m && spyExt is > 0m ? spyExt.Value / spyRth.Value : null;
		}
		catch (Exception ex) when (!cancellation.IsCancellationRequested)
		{
			Log.Debug($"Extended spot: Schwab SPY ratio fetch failed: {ex.Message}");
			return null;
		}
	}

	/// <summary>Sets every leg's HistoricalVolatility to its underlying's 20-session annualized close-to-close
	/// realized vol — the SAME metric the opener scores on (<see cref="WebullAnalytics.AI.CandidateScorer.ComputeHistoricalVolatilityAnnualized"/>,
	/// <c>volatilityLookbackDays</c> default 20) — so HV means one consistent thing regardless of vendor.
	/// (Webull's proprietary hiv is a ~1-year window; Schwab reports none — both are replaced here.) One
	/// realized-vol number per underlying. Display-only; nothing prices off this field. Legs whose root has
	/// too little price history keep whatever they had.</summary>
	internal static async Task<IReadOnlyDictionary<string, OptionContractQuote>> ApplyRealizedVolAsync(IReadOnlyDictionary<string, OptionContractQuote> quotes, CancellationToken cancellation)
	{
		if (quotes.Count == 0) return quotes;

		var cache = new WebullAnalytics.AI.Replay.HistoricalPriceCache();
		var hvByRoot = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in quotes.Keys.Select(s => ParsingHelpers.ParseOptionSymbol(s)?.Root).Where(r => r != null).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var closes = await cache.GetRecentClosesAsync(root, 21, EvaluationDate.Today, cancellation);
			var hv = WebullAnalytics.AI.CandidateScorer.ComputeHistoricalVolatilityAnnualized(closes);
			if (hv is > 0m) hvByRoot[root] = hv.Value;
		}
		if (hvByRoot.Count == 0) return quotes;

		var result = new Dictionary<string, OptionContractQuote>(quotes.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, q) in quotes)
		{
			var root = ParsingHelpers.ParseOptionSymbol(sym)?.Root;
			result[sym] = root != null && ParsingHelpers.TryResolveForRoot(hvByRoot, root, out var hv) ? q with { HistoricalVolatility = hv } : q;
		}
		return result;
	}

	/// <summary>Re-bases every quote's ImpliedVolatility to the mid-implied value — back-solved from the
	/// bid/ask mid at the dividend-adjusted spot, anchored at <paramref name="asOf"/> — the instant the quotes were
	/// struck (<see cref="OptionMath.ObservationInstant"/> for live quotes: now live / last close off-hours). This is the mid-consistent surface the engine prices on; the
	/// vendor's reported IV field is 10–50 vol pts off at 0DTE. Quotes with no usable mid keep their existing
	/// (vendor) IV as the fallback. Shared by analyze position and analyze risk so the two can't drift.</summary>
	internal static IReadOnlyDictionary<string, OptionContractQuote> RecalibrateQuotesToMid(
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		IReadOnlyDictionary<string, decimal> underlyingPrices,
		IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends,
		DateTime asOf)
	{
		var result = new Dictionary<string, OptionContractQuote>(quotes.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, q) in quotes)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null || !ParsingHelpers.TryResolveForRoot(underlyingPrices, parsed.Root, out var spot)) { result[sym] = q; continue; }
			IReadOnlyList<DividendEvent>? divs = null;
			if (dividends != null) ParsingHelpers.TryResolveForRoot(dividends, parsed.Root, out divs);
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
}
