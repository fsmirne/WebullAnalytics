using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;

namespace WebullAnalytics;

// ─── Branch & shared settings ─────────────────────────────────────────────────

internal abstract class TradeSubcommandSettings : CommandSettings
{
	[CommandOption("--account <VALUE>")]
	[Description("Account alias or ID from trade-config.json (defaults to defaultAccount).")]
	public string? Account { get; set; }
}

internal static class TradeContext
{
	/// <summary>Loads config, resolves the account, prints the environment banner. Returns null on any failure.</summary>
	internal static TradeAccount? ResolveOrExit(string? accountFlag, bool quietBanner = false)
	{
		var config = TradeConfig.Load();
		if (config == null) return null;
		var account = TradeConfig.Resolve(config, accountFlag);
		if (account == null) return null;

		if (!quietBanner)
			PrintBanner(account);
		return account;
	}

	internal static void PrintBanner(TradeAccount account)
	{
		var tag = account.Sandbox ? "[green][[SANDBOX]][/]" : "[red bold][[PRODUCTION]][/]";
		var redact = string.IsNullOrEmpty(account.AppKey) ? "?" : account.AppKey[..Math.Min(4, account.AppKey.Length)] + "…";
		AnsiConsole.MarkupLine($"{tag}  alias: [bold]{Markup.Escape(account.Alias)}[/]  account: [bold]{Markup.Escape(account.AccountId)}[/]  app-key: {redact}");
	}

	/// <summary>Prompts the user for yes/no. Returns false on EOF or anything other than y/yes (case-insensitive).</summary>
	internal static bool Confirm(string prompt)
	{
		AnsiConsole.Markup($"{prompt} [[y/N]] ");
		var input = Console.ReadLine();
		if (input == null) { AnsiConsole.WriteLine(); return false; }
		var t = input.Trim().ToLowerInvariant();
		return t == "y" || t == "yes";
	}
}

// ─── `trade place` ────────────────────────────────────────────────────────────

internal sealed class TradePlaceSettings : TradeSubcommandSettings
{
	[CommandOption("--trades <VALUE>")]
	[Description("Comma-separated legs in ACTION:SYMBOL:QTY format. Example: \"buy:GME260501C00023000:1,sell:GME260501C00024000:1\"")]
	public string Trades { get; set; } = "";

	[CommandOption("--limit <VALUE>")]
	[Description("Net limit price. Required for --type limit. Positive = net credit; negative = net debit.")]
	public string? Limit { get; set; }

	[CommandOption("--type <VALUE>")]
	[Description("Order type: limit|market. Default: limit. Market is rejected for multi-leg orders.")]
	public string Type { get; set; } = "limit";

	[CommandOption("--tif <VALUE>")]
	[Description("Time-in-force: day|gtc. Default: day.")]
	public string Tif { get; set; } = "day";

	[CommandOption("--strategy <VALUE>")]
	[Description("Override auto-detected strategy. Values: single|stock|vertical|calendar|diagonal|iron_condor|butterfly|straddle|strangle|covered_call|protective_put|collar.")]
	public string? Strategy { get; set; }

	[CommandOption("--submit")]
	[Description("Actually place the order. Without this, runs preview only.")]
	public bool Submit { get; set; }
}

internal sealed class TradePlaceCommand : AsyncCommand<TradePlaceSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradePlaceSettings s, CancellationToken cancellationToken)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		// 1. Parse legs.
		List<ParsedLeg> legs;
		try { legs = TradeLegParser.Parse(s.Trades); }
		catch (FormatException ex) { AnsiConsole.MarkupLine($"[red]Error parsing --trades:[/] {Markup.Escape(ex.Message)}"); return 2; }

		if (legs.Any(l => l.Price != null || l.PriceKeyword != null))
		{
			AnsiConsole.MarkupLine("[red]Error:[/] per-leg @PRICE is not allowed in trade. Use --limit for the combo net price.");
			return 2;
		}

		// 2. Validate --type / --limit compatibility.
		var type = s.Type.ToLowerInvariant();
		if (type != "limit" && type != "market") { AnsiConsole.MarkupLine("[red]Error:[/] --type must be 'limit' or 'market'."); return 2; }
		if (type == "market" && !string.IsNullOrEmpty(s.Limit)) { AnsiConsole.MarkupLine("[red]Error:[/] --limit is not allowed with --type market."); return 2; }
		if (type == "limit" && string.IsNullOrEmpty(s.Limit)) { AnsiConsole.MarkupLine("[red]Error:[/] --limit is required with --type limit."); return 2; }
		if (type == "market" && legs.Count > 1) { AnsiConsole.MarkupLine("[red]Error:[/] multi-leg combo orders must be limit."); return 2; }

		decimal? limit = null;
		if (type == "limit")
		{
			if (!decimal.TryParse(s.Limit, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLimit))
			{ AnsiConsole.MarkupLine($"[red]Error:[/] --limit '{Markup.Escape(s.Limit!)}' is not a valid decimal."); return 2; }
			limit = parsedLimit;
		}

		// 3. Resolve strategy.
		string? strategy;
		try { strategy = s.Strategy != null ? NormalizeStrategyFlag(s.Strategy) : StrategyClassifier.Classify(legs); }
		catch (ArgumentException ex) { AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}"); return 2; }
		if (strategy == null)
		{ AnsiConsole.MarkupLine("[red]Error:[/] could not classify legs; pass --strategy explicitly."); return 2; }

		// 4. Cross-underlying / single-root check for multi-leg.
		var optionRoots = legs.Where(l => l.Option != null).Select(l => l.Option!.Root).Distinct().ToList();
		if (optionRoots.Count > 1)
		{ AnsiConsole.MarkupLine("[red]Error:[/] combo order legs must share one underlying symbol."); return 2; }

		// 5. Sign-sanity warning.
		if (limit.HasValue && legs.All(l => l.Action == LegAction.Buy) && limit > 0)
			AnsiConsole.MarkupLine("[yellow]Warning:[/] all legs are buys but --limit is positive (credit). Double-check the sign.");

		// 6. Build order.
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
			AccountId: account.AccountId,
			Legs: legs,
			Strategy: strategy,
			OrderType: type.ToUpperInvariant(),
			LimitPrice: limit,
			TimeInForce: s.Tif.ToUpperInvariant()
		));

		AnsiConsole.MarkupLine($"[dim]Client order ID:[/] [bold]{Markup.Escape(body.NewOrders[0].ClientOrderId)}[/]");
		AnsiConsole.MarkupLine($"[dim]Strategy:[/] {Markup.Escape(strategy)}  [dim]Type:[/] {type.ToUpperInvariant()}  [dim]TIF:[/] {s.Tif.ToUpperInvariant()}");
		AnsiConsole.MarkupLine($"[dim]Payload:[/] {Markup.Escape(OrderRequestBuilder.Serialize(body))}");

		// 7. Preview.
		using var client = new WebullOpenApiClient(account);
		WebullOpenApiClient.PreviewResult preview;
		try { preview = await client.PreviewOrderAsync(body); }
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]Preview failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
		AnsiConsole.MarkupLine($"[bold]Preview:[/] cost={preview.EstimatedCost ?? "-"}  fees={preview.EstimatedTransactionFee ?? "-"}");

		if (!s.Submit) { AnsiConsole.MarkupLine("[dim]Preview only (no --submit). Exiting.[/]"); return 0; }

		// 8. Confirm and place.
		if (!TradeContext.Confirm("Place this order?")) { AnsiConsole.MarkupLine("[dim]Aborted.[/]"); return 0; }

		try
		{
			var placed = await client.PlaceOrderAsync(body);
			AnsiConsole.MarkupLine($"[green]Placed.[/] order_id={Markup.Escape(placed.OrderId ?? "-")}  client_order_id={Markup.Escape(placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId)}");
			AnsiConsole.MarkupLine($"[dim]Check status with:[/] WebullAnalytics trade status {Markup.Escape(placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId)}");
			return 0;
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]Place failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]");
			AnsiConsole.MarkupLine("[dim](Preview succeeded but place was rejected.)[/]");
			return 3;
		}
	}

	private static string NormalizeStrategyFlag(string flag) => flag.ToLowerInvariant() switch
	{
		"single" => "Single",
		"stock" => "Stock",
		"vertical" => "Vertical",
		"calendar" => "Calendar",
		"diagonal" => "Diagonal",
		"iron_condor" or "ironcondor" => "IronCondor",
		"iron_butterfly" or "ironbutterfly" => "IronButterfly",
		"butterfly" => "Butterfly",
		"condor" => "Condor",
		"straddle" => "Straddle",
		"strangle" => "Strangle",
		"covered_call" or "coveredcall" => "CoveredCall",
		"protective_put" or "protectiveput" => "ProtectivePut",
		"collar" => "Collar",
		_ => throw new ArgumentException($"Unknown --strategy value '{flag}'")
	};
}

// ─── `trade cancel` ───────────────────────────────────────────────────────────

internal sealed class TradeCancelSettings : TradeSubcommandSettings
{
	[CommandArgument(0, "[clientOrderId]")]
	[Description("Client order ID to cancel. Omit when using --all.")]
	public string? ClientOrderId { get; set; }

	[CommandOption("--all")]
	[Description("Cancel every open order for the account.")]
	public bool All { get; set; }
}

internal sealed class TradeCancelCommand : AsyncCommand<TradeCancelSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeCancelSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		if (s.All && !string.IsNullOrEmpty(s.ClientOrderId))
		{ AnsiConsole.MarkupLine("[red]Error:[/] pass either <clientOrderId> or --all, not both."); return 2; }
		if (!s.All && string.IsNullOrEmpty(s.ClientOrderId))
		{ AnsiConsole.MarkupLine("[red]Error:[/] pass a client order ID, or --all."); return 2; }

		using var client = new WebullOpenApiClient(account);

		if (s.All)
		{
			List<WebullOpenApiClient.OpenOrder> orders;
			try { orders = await client.ListOpenOrdersAsync(cancellation); }
			catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]List failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }

			if (orders.Count == 0) { AnsiConsole.MarkupLine("[dim]No open orders.[/]"); return 0; }

			AnsiConsole.MarkupLine($"[bold]{orders.Count} open order(s):[/]");
			foreach (var o in orders)
				AnsiConsole.MarkupLine($"  {Markup.Escape(o.ClientOrderId ?? "?"),-22} {Markup.Escape(o.Symbol ?? "?"),-22} {Markup.Escape(o.Side ?? "?"),-5} {Markup.Escape(o.FilledQuantity ?? "0")}/{Markup.Escape(o.TotalQuantity ?? "?")} {Markup.Escape(o.Status ?? "?")}");

			if (!TradeContext.Confirm($"Cancel all {orders.Count} open orders?")) { AnsiConsole.MarkupLine("[dim]Aborted.[/]"); return 0; }

			int succeeded = 0, failed = 0;
			foreach (var o in orders)
			{
				if (string.IsNullOrEmpty(o.ClientOrderId)) { AnsiConsole.MarkupLine("[yellow]Skipped:[/] missing client_order_id."); failed++; continue; }
				try
				{
					await client.CancelOrderAsync(o.ClientOrderId, cancellation);
					AnsiConsole.MarkupLine($"  [green]cancelled[/] {Markup.Escape(o.ClientOrderId)}");
					succeeded++;
				}
				catch (WebullOpenApiException ex)
				{
					AnsiConsole.MarkupLine($"  [red]failed[/] {Markup.Escape(o.ClientOrderId)} [[{Markup.Escape(ex.ErrorCode ?? "?")}]] {Markup.Escape(ex.Message)}");
					failed++;
				}
			}
			AnsiConsole.MarkupLine($"[bold]Summary:[/] cancelled {succeeded} of {orders.Count}. Failed: {failed}.");
			return failed == 0 ? 0 : 3;
		}

		// Single cancel.
		if (!TradeContext.Confirm($"Cancel order {s.ClientOrderId}?")) { AnsiConsole.MarkupLine("[dim]Aborted.[/]"); return 0; }
		try
		{
			var result = await client.CancelOrderAsync(s.ClientOrderId!, cancellation);
			AnsiConsole.MarkupLine($"[green]Cancelled.[/] order_id={Markup.Escape(result.OrderId ?? "-")}  client_order_id={Markup.Escape(result.ClientOrderId ?? s.ClientOrderId!)}");
			return 0;
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]Cancel failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]");
			return 3;
		}
	}
}

// ─── `trade status` ───────────────────────────────────────────────────────────

internal sealed class TradeStatusSettings : TradeSubcommandSettings
{
	[CommandArgument(0, "<clientOrderId>")]
	[Description("Client order ID to look up.")]
	public string ClientOrderId { get; set; } = "";
}

internal sealed class TradeStatusCommand : AsyncCommand<TradeStatusSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeStatusSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account, quietBanner: false);
		if (account == null) return 2;

		using var client = new WebullOpenApiClient(account);
		WebullOpenApiClient.OrderDetail detail;
		try { detail = await client.GetOrderAsync(s.ClientOrderId, cancellation); }
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]Lookup failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }

		AnsiConsole.MarkupLine($"[bold]Combo type:[/] {Markup.Escape(detail.ComboType ?? "-")}  [bold]Combo order ID:[/] {Markup.Escape(detail.ComboOrderId ?? "-")}");
		if (detail.Orders == null || detail.Orders.Count == 0)
		{ AnsiConsole.MarkupLine("[dim]No orders returned.[/]"); return 0; }

		foreach (var o in detail.Orders)
		{
			AnsiConsole.MarkupLine($"[bold]Order[/] {Markup.Escape(o.ClientOrderId ?? "-")}  [dim]id[/]={Markup.Escape(o.OrderId ?? "-")}  [dim]status[/]={Markup.Escape(o.Status ?? "-")}");
			AnsiConsole.MarkupLine($"  {Markup.Escape(o.Symbol ?? "-")} {Markup.Escape(o.Side ?? "-")} {Markup.Escape(o.FilledQuantity ?? "0")}/{Markup.Escape(o.TotalQuantity ?? "-")} @ {Markup.Escape(o.FilledPrice ?? "-")}");
			AnsiConsole.MarkupLine($"  [dim]placed[/] {Markup.Escape(o.PlaceTime ?? "-")}  [dim]filled[/] {Markup.Escape(o.FilledTime ?? "-")}  [dim]intent[/] {Markup.Escape(o.PositionIntent ?? "-")}");
			if (o.Legs != null)
				foreach (var leg in o.Legs)
					AnsiConsole.MarkupLine($"  └─ {Markup.Escape(leg.Symbol ?? "-")} {Markup.Escape(leg.Side ?? "-")} {Markup.Escape(leg.Quantity ?? "-")} {Markup.Escape(leg.OptionType ?? "")} strike={Markup.Escape(leg.StrikePrice ?? "-")} exp={Markup.Escape(leg.OptionExpireDate ?? "-")}");
		}
		return 0;
	}
}
