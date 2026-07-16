using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;

namespace WebullAnalytics.Trading;

internal sealed class TradeCloseSettings : TradeSubcommandSettings
{
	[CommandArgument(0, "[id]")]
	[Description("Broker position ID of the position to close — shown by a bare `wa trade close` or `wa trade positions`. A unique case-insensitive prefix is enough.")]
	public string? Id { get; set; }

	[CommandOption("--all")]
	[Description("Close every open option position at the broker.")]
	public bool All { get; set; }

	[CommandOption("--pricing <MODE>")]
	[Description("Limit pricing per leg: 'mid' (patient, (bid+ask)/2) or 'bidask' (marketable — sell legs at bid, buy legs at ask; crosses the spread for a fast fill). Default: mid.")]
	public string Pricing { get; set; } = SuggestionPricing.Mid;

	[CommandOption("--tif <VALUE>")]
	[Description("Time-in-force: day|gtc. Default: day.")]
	public string Tif { get; set; } = "day";

	[CommandOption("--submit")]
	[Description("Place the close orders without prompting. Without this, prints the planned orders (with wa trade place hints) and asks for y/N confirmation (default N), like wa trade place.")]
	public bool Submit { get; set; }

	public override ValidationResult Validate()
	{
		if (Id != null && All) return ValidationResult.Error("pass a position ID or --all, not both");
		if (Pricing != SuggestionPricing.Mid && Pricing != SuggestionPricing.BidAsk) return ValidationResult.Error($"--pricing: must be 'mid' or 'bidask', got '{Pricing}'");
		var tif = Tif.ToLowerInvariant();
		if (tif != "day" && tif != "gtc") return ValidationResult.Error($"--tif: must be 'day' or 'gtc', got '{Tif}'");
		return ValidationResult.Success();
	}
}

/// <summary>`wa trade close` — closes open option positions at mid or marketable bid/ask limits (broker
/// truth via the same position source the AI rules use). A bare run prompts you to pick one position
/// (arrow keys, like `wa analyze position`; non-interactive shells get a plain list with broker IDs);
/// `wa trade close <id>` closes one directly (unique ID prefix is enough); `--all` flattens the book. Each position closes as its own combo (the same per-combo grain Webull holds them at), so
/// split structures (the two halves of a double calendar/diagonal) close as two orders — exactly how
/// they opened. Placed closes are recorded in the local order ledger (dedup; they never consume the
/// opener's daily cap).</summary>
internal sealed class TradeCloseCommand : AsyncCommand<TradeCloseSettings>
{
	protected override async Task<int> ExecuteAsync(CommandContext context, TradeCloseSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		// 1. Holdings (raw) to know what exists; positions (typed) for leg sides. LivePositionSource
		// infers per-leg direction (Webull's holdings response has no leg side) and skips structures it
		// can't classify — surface those so "close all" never silently leaves something open.
		List<WebullOpenApiClient.AccountHolding> holdings;
		using (var client = new WebullOpenApiClient(account))
		{
			try { holdings = await client.FetchAccountPositionsAsync(cancellation); }
			catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]Positions lookup failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
			catch (HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }
		}

		var optionHoldings = holdings.Where(h => h.Legs is { Count: > 0 } && !string.IsNullOrEmpty(h.Symbol)).ToList();
		if (optionHoldings.Count == 0)
		{
			AnsiConsole.MarkupLine("[dim]No open option positions.[/]");
			return 0;
		}

		var tickerSet = optionHoldings.Select(h => h.Symbol!.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var positions = await new LivePositionSource(account).GetOpenPositionsAsync(DateTime.Now, tickerSet, cancellation);
		var unclassifiable = optionHoldings.Where(h => !positions.Values.Any(p => string.Equals(p.PositionId, h.PositionId, StringComparison.OrdinalIgnoreCase))).ToList();
		if (unclassifiable.Count > 0)
			AnsiConsole.MarkupLine($"[yellow]warning:[/] {unclassifiable.Count} holding(s) skipped (unclassifiable structure — close those in the Webull app).");
		if (positions.Count == 0) return unclassifiable.Count > 0 ? 3 : 0;

		// 2. Selection: bare run lists positions with their broker IDs and exits; <id> targets one
		// position by unique ID prefix; --all takes the whole book.
		List<OpenPosition> selected;
		if (s.All)
		{
			selected = positions.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
		}
		else if (s.Id != null)
		{
			var matches = positions.Values.Where(p => p.PositionId != null && p.PositionId.StartsWith(s.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
			if (matches.Count != 1)
			{
				AnsiConsole.MarkupLine(matches.Count == 0
					? $"[red]No closable position with ID prefix '{Markup.Escape(s.Id)}'.[/] Open positions:"
					: $"[red]ID prefix '{Markup.Escape(s.Id)}' is ambiguous ({matches.Count} matches):[/]");
				foreach (var p in matches.Count == 0 ? positions.Values.OrderBy(p => p.Key, StringComparer.Ordinal).AsEnumerable() : matches)
					AnsiConsole.MarkupLine($"  {FormatPositionLine(p)}");
				return 2;
			}
			selected = matches;
		}
		else
		{
			// Bare run: pick one interactively (same UX as `wa analyze position`); a lone position is
			// auto-picked — the y/N confirmation below still gates the order. Non-interactive shells
			// (piped/redirected output) get the plain list with the <id> hint instead.
			foreach (var h in unclassifiable)
				AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(h.PositionId ?? "-")}  {Markup.Escape($"{h.Symbol} {h.OptionStrategy ?? "?"} x{TradeContext.FormatQty(h.Quantity)}")} (unclassifiable — close in the Webull app)[/]");
			var ordered = positions.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
			if (!AnsiConsole.Profile.Capabilities.Interactive)
			{
				AnsiConsole.MarkupLine($"[bold]{positions.Count} closable position(s):[/]");
				foreach (var p in ordered)
					AnsiConsole.MarkupLine($"  {FormatPositionLine(p)}");
				AnsiConsole.MarkupLine("[dim]Close one with `wa trade close <id>` (unique prefix OK) or everything with `wa trade close --all`.[/]");
				return 0;
			}
			var chosen = ordered.Count == 1 ? ordered[0] : AnsiConsole.Prompt(new SelectionPrompt<OpenPosition>()
				.Title("Select a position to close:")
				.UseConverter(FormatPositionLine)
				.AddChoices(ordered));
			selected = new List<OpenPosition> { chosen };
		}

		// 3. One quote fetch for every leg across the selected positions.
		var legSymbols = selected.SelectMany(p => p.Legs.Select(l => l.Symbol)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		QuoteSnapshot quotes;
		try { quotes = await new LiveQuoteSource().GetQuotesAsync(DateTime.Now, legSymbols, tickerSet, cancellation); }
		catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Quote fetch failed:[/] {Markup.Escape(ex.Message)}"); return 3; }

		// 4. Plan pass: price every selected position and print the plan. Submission (below) only
		// happens after the explicit --submit flag or the y/N confirmation, so this output IS the prompt's
		// context — the user confirms exactly what they just read.
		var plans = new List<(OpenPosition Pos, List<(string Action, string Symbol, int Qty)> LegSpecs, string Side, decimal LimitAbs, string ArgLegs, string Summary)>();
		var skipped = 0;
		foreach (var pos in selected)
		{
			var legSpecs = pos.Legs.Select(l => (Action: l.Side == Side.Buy ? "sell" : "buy", l.Symbol, l.Qty)).ToList();

			// Per-leg close price by mode. A buy-back leg without an ask is un-priceable (its cost can't
			// be bounded) — skip the position rather than guess. A sell leg with a hollow bid is valued
			// at 0: worthless wings routinely go 0-bid on expiry day, and refusing to price them would
			// block flattening exactly when it matters; the net limit only understates the credit.
			decimal net = 0m; string? unpriceable = null;
			foreach (var leg in legSpecs)
			{
				if (!quotes.Options.TryGetValue(leg.Symbol, out var q)) { unpriceable = leg.Symbol; break; }
				var bid = q.Bid is > 0m ? q.Bid.Value : 0m;
				var ask = q.Ask is > 0m ? q.Ask : null;
				if (leg.Action == "buy" && ask == null) { unpriceable = leg.Symbol; break; }
				var price = s.Pricing == SuggestionPricing.BidAsk ? (leg.Action == "sell" ? bid : ask!.Value) : (bid + (ask ?? bid)) / 2m;
				net += leg.Action == "sell" ? price : -price;
			}
			if (unpriceable != null)
			{
				AnsiConsole.MarkupLine($"[yellow]skipped (no two-sided quote for {Markup.Escape(unpriceable)}):[/] {Markup.Escape(pos.Key)}");
				skipped++;
				continue;
			}

			var side = net >= 0m ? "SELL" : "BUY";
			var limitAbs = OptionPriceRounding.RoundToTick(Math.Abs(net), pos.Legs.Count, pos.Ticker);
			// A worthless position rounds to a $0.00 net, which the broker rejects (invalid limit_price),
			// and around zero the side sign is quote noise anyway. Flatten by paying one tick — the only
			// direction such an order actually fills.
			if (limitAbs == 0m) { side = "BUY"; limitAbs = OptionPriceRounding.MinTick(pos.Legs.Count, pos.Ticker); }
			var argLegs = string.Join(",", legSpecs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
			var summary = $"close {pos.Ticker} {pos.StrategyKind} x{pos.Quantity} @ ${limitAbs:F2} ({side.ToLowerInvariant()}, {(net >= 0m ? "credit" : "debit")}) [{pos.PositionId ?? "-"}]";

			AnsiConsole.MarkupLine($"[cyan]planned:[/] {Markup.Escape(summary)}");
			AnsiConsole.MarkupLine($"  [grey50]wa trade place \"{Markup.Escape(argLegs)}\" --side {side.ToLowerInvariant()} --limit {limitAbs:F2} --tif {s.Tif.ToLowerInvariant()} --submit[/]");
			plans.Add((pos, legSpecs, side, limitAbs, argLegs, summary));
		}

		if (plans.Count == 0)
		{
			AnsiConsole.MarkupLine($"[bold]0 close order(s) plannable[/]{(skipped > 0 ? $", {skipped} skipped" : "")}.");
			return skipped > 0 ? 3 : 0;
		}

		// Same gate semantics as wa trade place: --submit is the explicit "I mean it" flag (no prompt);
		// without it, confirm at y/N defaulting to No.
		if (!s.Submit && !TradeContext.Confirm($"Submit {plans.Count} close order(s)?")) { AnsiConsole.MarkupLine("[dim]Preview only. Exiting.[/]"); return 0; }

		// 5. Broker-truth dedup: don't stack a second close onto one already working/filled.
		var brokerState = new BrokerStateService(account, new LocalOrderLedger(Program.ResolvePath("data/local-orders.jsonl")));
		if (!await brokerState.TryRefreshAsync(cancellation)) return 3;

		var placed = 0; var failed = 0;
		foreach (var (pos, legSpecs, side, limitAbs, argLegs, summary) in plans)
		{
			if (brokerState.HasPendingMatching(legSpecs.Select(l => (l.Symbol, l.Action))))
			{
				AnsiConsole.MarkupLine($"[yellow]skipped (matching close already at broker):[/] {Markup.Escape(pos.Key)}");
				skipped++;
				continue;
			}

			var legs = TradeLegParser.Parse(argLegs);
			string strategy;
			try { strategy = StrategyClassifier.Classify(legs) ?? "Single"; }
			catch (Exception) { strategy = "Single"; }

			var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
				AccountId: account.AccountId,
				Legs: legs,
				Strategy: strategy,
				Side: side,
				OrderType: "LIMIT",
				LimitPrice: limitAbs,
				TimeInForce: s.Tif.ToUpperInvariant(),
				PositionIntent: OrderRequestBuilder.DeriveOptionIntent(side, opening: false)));

			try
			{
				using var client = new WebullOpenApiClient(account);
				var result = await client.PlaceOrderAsync(body, cancellation);
				AnsiConsole.MarkupLine($"[green]placed:[/] {Markup.Escape(summary)}  order_id={Markup.Escape(result.OrderId ?? "-")}");
				brokerState.RecordLocalPlacement(pos.Ticker, legSpecs.Select(l => (l.Symbol, l.Action)), result.ClientOrderId ?? body.NewOrders[0].ClientOrderId, isOpen: false);
				placed++;
			}
			catch (Exception ex) when (ex is WebullOpenApiException or HttpRequestException)
			{
				var tag = ex is WebullOpenApiException w ? $"[[{Markup.Escape(w.ErrorCode ?? "?")}]]" : "network error";
				AnsiConsole.MarkupLine($"[red]failed {tag}:[/] {Markup.Escape(summary)} — {Markup.Escape(ex.Message)}");
				failed++;
			}
		}

		AnsiConsole.MarkupLine($"[bold]{placed} close order(s) placed[/]{(skipped > 0 ? $", {skipped} skipped" : "")}{(failed > 0 ? $", [red]{failed} failed[/]" : "")}.");
		return failed > 0 ? 3 : 0;
	}

	/// <summary>One listing line per position: broker ID first (what `wa trade close <id>` takes), then
	/// the structure at a glance (+ = long leg, − = short leg).</summary>
	private static string FormatPositionLine(OpenPosition p)
	{
		var legs = string.Join(" / ", p.Legs.Select(l => $"{(l.Side == Side.Buy ? "+" : "-")}{l.Strike:0.##}{l.CallPut} {l.Expiry:MM/dd}"));
		return $"[bold]{Markup.Escape(p.PositionId ?? "-")}[/]  {Markup.Escape($"{p.Ticker} {p.StrategyKind} x{p.Quantity}  {legs}")}";
	}
}
