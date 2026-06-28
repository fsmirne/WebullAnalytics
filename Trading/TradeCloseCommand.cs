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
	[CommandArgument(0, "[ticker]")]
	[Description("Restrict to one underlying (e.g. SPY). Default: every option position at the broker.")]
	public string? Ticker { get; set; }

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
		if (Pricing != SuggestionPricing.Mid && Pricing != SuggestionPricing.BidAsk) return ValidationResult.Error($"--pricing: must be 'mid' or 'bidask', got '{Pricing}'");
		var tif = Tif.ToLowerInvariant();
		if (tif != "day" && tif != "gtc") return ValidationResult.Error($"--tif: must be 'day' or 'gtc', got '{Tif}'");
		return ValidationResult.Success();
	}
}

/// <summary>`wa trade close` — builds one closing combo order per open option position (broker truth via
/// the same position source the AI rules use) and previews or places them. Replaces the manual close-it-
/// in-the-Webull-app routine: one command flattens the book at mid or marketable bid/ask limits. Each
/// position closes as its own combo (the same per-combo grain Webull holds them at), so split structures
/// (the two halves of a double calendar/diagonal) close as two orders — exactly how they opened. Placed
/// closes are recorded in the local order ledger (dedup; they never consume the opener's daily cap).</summary>
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

		var optionHoldings = holdings.Where(h => h.Legs is { Count: > 0 } && !string.IsNullOrEmpty(h.Symbol)
			&& (s.Ticker == null || string.Equals(h.Symbol, s.Ticker, StringComparison.OrdinalIgnoreCase))).ToList();
		if (optionHoldings.Count == 0)
		{
			AnsiConsole.MarkupLine($"[dim]No open option positions{(s.Ticker != null ? $" for {Markup.Escape(s.Ticker.ToUpperInvariant())}" : "")}.[/]");
			return 0;
		}

		var tickerSet = optionHoldings.Select(h => h.Symbol!.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var positions = await new LivePositionSource(account).GetOpenPositionsAsync(DateTime.Now, tickerSet, cancellation);
		if (positions.Count < optionHoldings.Count)
			AnsiConsole.MarkupLine($"[yellow]warning:[/] {optionHoldings.Count - positions.Count} holding(s) skipped (unclassifiable structure — close those in the Webull app).");
		if (positions.Count == 0) return positions.Count < optionHoldings.Count ? 3 : 0;

		// 2. One quote fetch for every leg across all positions.
		var legSymbols = positions.Values.SelectMany(p => p.Legs.Select(l => l.Symbol)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		QuoteSnapshot quotes;
		try { quotes = await new LiveQuoteSource().GetQuotesAsync(DateTime.Now, legSymbols, tickerSet, cancellation); }
		catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Quote fetch failed:[/] {Markup.Escape(ex.Message)}"); return 3; }

		// 3. Plan pass: price every closable position and print the plan. Submission (below) only
		// happens after the explicit --submit flag or the y/N confirmation, so this output IS the prompt's
		// context — the user confirms exactly what they just read.
		var plans = new List<(OpenPosition Pos, List<(string Action, string Symbol, int Qty)> LegSpecs, string Side, decimal LimitAbs, string ArgLegs, string Summary)>();
		var skipped = 0;
		foreach (var pos in positions.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			var legSpecs = pos.Legs.Select(l => (Action: l.Side == Side.Buy ? "sell" : "buy", l.Symbol, l.Qty)).ToList();

			// Per-leg close price by mode. Any leg without a two-sided book makes the combo un-priceable —
			// skip the position rather than guess (same stance as the management executor).
			decimal net = 0m; string? unpriceable = null;
			foreach (var leg in legSpecs)
			{
				if (!quotes.Options.TryGetValue(leg.Symbol, out var q) || q.Bid is not > 0m || q.Ask is not > 0m) { unpriceable = leg.Symbol; break; }
				var price = s.Pricing == SuggestionPricing.BidAsk ? (leg.Action == "sell" ? q.Bid.Value : q.Ask.Value) : (q.Bid.Value + q.Ask.Value) / 2m;
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
			var argLegs = string.Join(",", legSpecs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
			var summary = $"close {pos.Ticker} {pos.StrategyKind} x{pos.Quantity} @ ${limitAbs:F2} ({side.ToLowerInvariant()}, {(net >= 0m ? "credit" : "debit")})";

			AnsiConsole.MarkupLine($"[cyan]planned:[/] {Markup.Escape(summary)}");
			AnsiConsole.MarkupLine($"  [grey50]wa trade place --trade \"{Markup.Escape(argLegs)}\" --side {side.ToLowerInvariant()} --limit {limitAbs:F2} --tif {s.Tif.ToLowerInvariant()} --submit[/]");
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

		// 4. Broker-truth dedup: don't stack a second close onto one already working/filled.
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
}
