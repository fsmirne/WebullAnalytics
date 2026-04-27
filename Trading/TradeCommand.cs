using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using WebullAnalytics.Api;
using WebullAnalytics.Positions;

namespace WebullAnalytics.Trading;

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

	/// <summary>Formats an API-returned decimal-string as USD currency. Returns "-" if null/empty, returns the raw string if not a decimal.</summary>
	internal static string FormatCurrency(string? raw)
	{
		if (string.IsNullOrEmpty(raw)) return "-";
		if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return raw;
		var sign = d < 0m ? "-" : "";
		return $"{sign}${Math.Abs(d).ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}";
	}

	/// <summary>Formats an API-returned decimal-string as a quantity with no trailing zeros. Returns "-" if null/empty, returns the raw string if not a decimal.</summary>
	internal static string FormatQty(string? raw)
	{
		if (string.IsNullOrEmpty(raw)) return "-";
		if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return raw;
		return d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <summary>Synthesizes an OCC option symbol from leg fields. Returns null if any required field is missing/malformed.</summary>
	internal static string? BuildOccSymbol(WebullOpenApiClient.OrderDetailLeg leg)
	{
		if (string.IsNullOrEmpty(leg.Symbol) || string.IsNullOrEmpty(leg.OptionType) || string.IsNullOrEmpty(leg.OptionExpireDate) || string.IsNullOrEmpty(leg.StrikePrice))
			return null;
		if (!System.DateTime.TryParseExact(leg.OptionExpireDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expiry))
			return null;
		if (!decimal.TryParse(leg.StrikePrice, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var strike))
			return null;
		var cp = leg.OptionType == "CALL" ? "C" : leg.OptionType == "PUT" ? "P" : null;
		if (cp == null) return null;
		return $"{leg.Symbol}{expiry:yyMMdd}{MatchKeys.OccSuffix(strike, cp)}";
	}
}

// ─── `trade place` ────────────────────────────────────────────────────────────

internal sealed class TradePlaceSettings : TradeSubcommandSettings
{
	[CommandOption("--trade <VALUE>")]
	[Description("Comma-separated legs in ACTION:SYMBOL:QTY format. Example: \"buy:GME260501C00023000:1,sell:GME260501C00024000:1\"")]
	public string Trades { get; set; } = "";

	[CommandOption("--limit <VALUE>")]
	[Description("Absolute per-share net limit price (always positive). Required for --type limit.")]
	public string? Limit { get; set; }

	[CommandOption("--side <VALUE>")]
	[Description("Combo direction override: buy (net-debit, pay to open) or sell (net-credit, receive to open). Auto-inferred from the leg structure; only pass this for unusual constructions the inferrer gets wrong.")]
	public string? Side { get; set; }

	[CommandOption("--type <VALUE>")]
	[Description("Order type: limit|market. Default: limit. Market is rejected for multi-leg orders.")]
	public string Type { get; set; } = "limit";

	[CommandOption("--tif <VALUE>")]
	[Description("Time-in-force: day|gtc. Default: day.")]
	public string Tif { get; set; } = "day";

	[CommandOption("--strategy <VALUE>")]
	[Description("Override auto-detected strategy. Values: single|stock|vertical|calendar|diagonal|iron_condor|iron_butterfly|butterfly|condor|straddle|strangle|covered_call|protective_put|collar.")]
	public string? Strategy { get; set; }

	[CommandOption("--submit")]
	[Description("Actually place the order. Without this, runs preview only.")]
	public bool Submit { get; set; }

	[CommandOption("--debug")]
	[Description("Print the raw JSON payload that will be sent to the Webull API.")]
	public bool Debug { get; set; }
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
		catch (FormatException ex) { AnsiConsole.MarkupLine($"[red]Error parsing --trade:[/] {Markup.Escape(ex.Message)}"); return 2; }

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

		// 2b. Validate --tif.
		var tifLower = s.Tif.ToLowerInvariant();
		if (tifLower != "day" && tifLower != "gtc") { AnsiConsole.MarkupLine("[red]Error:[/] --tif must be 'day' or 'gtc'."); return 2; }

		decimal? limit = null;
		if (type == "limit")
		{
			if (!decimal.TryParse(s.Limit, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLimit))
			{ AnsiConsole.MarkupLine($"[red]Error:[/] --limit '{Markup.Escape(s.Limit!)}' is not a valid decimal."); return 2; }
			if (parsedLimit < 0m)
			{ AnsiConsole.MarkupLine($"[red]Error:[/] --limit must be a positive absolute value. Use --side buy for net-debit orders, --side sell for net-credit orders."); return 2; }
			limit = parsedLimit;
		}

		// 3. Resolve strategy.
		string? strategy;
		try { strategy = s.Strategy != null ? NormalizeStrategyFlag(s.Strategy) : StrategyClassifier.Classify(legs); }
		catch (ArgumentException ex) { AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}"); return 2; }
		if (strategy == null)
		{ AnsiConsole.MarkupLine("[red]Error:[/] could not classify legs; pass --strategy explicitly."); return 2; }

		// 3b. Validate leg count matches the declared/inferred strategy.
		var stockCount = legs.Count(l => l.Option == null);
		var optionCount = legs.Count(l => l.Option != null);
		string? legCountError = strategy switch
		{
			"Stock" => (stockCount == 1 && optionCount == 0) ? null : "Stock requires 1 equity leg, 0 option legs.",
			"Single" => (stockCount == 0 && optionCount == 1) ? null : "Single requires 0 equity legs, 1 option leg.",
			"Vertical" or "Calendar" or "Diagonal" or "Straddle" or "Strangle" => (stockCount == 0 && optionCount == 2) ? null : $"{strategy} requires 0 equity legs, 2 option legs.",
			"Butterfly" or "Condor" => (stockCount == 0 && optionCount == 3) ? null : $"{strategy} requires 0 equity legs, 3 option legs.",
			"IronButterfly" or "IronCondor" => (stockCount == 0 && optionCount == 4) ? null : $"{strategy} requires 0 equity legs, 4 option legs.",
			"CoveredCall" or "ProtectivePut" => (stockCount == 1 && optionCount == 1) ? null : $"{strategy} requires 1 equity leg, 1 option leg.",
			"Collar" => (stockCount == 1 && optionCount == 2) ? null : "Collar requires 1 equity leg, 2 option legs.",
			_ => null
		};
		if (legCountError != null)
		{ AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(legCountError)}"); return 2; }

		// 4. Cross-underlying / single-root check for multi-leg.
		var optionRoots = legs.Where(l => l.Option != null).Select(l => l.Option!.Root).Distinct().ToList();
		if (optionRoots.Count > 1)
		{ AnsiConsole.MarkupLine("[red]Error:[/] combo order legs must share one underlying symbol."); return 2; }

		// 5. Resolve combo side (explicit --side or inferred from leg structure).
		string? side;
		if (!string.IsNullOrEmpty(s.Side))
		{
			var normalized = s.Side.ToLowerInvariant();
			if (normalized != "buy" && normalized != "sell")
			{ AnsiConsole.MarkupLine("[red]Error:[/] --side must be 'buy' or 'sell'."); return 2; }
			side = normalized.ToUpperInvariant();
		}
		else
		{
			side = SideInferrer.Infer(legs, strategy);
			if (side == null)
			{ AnsiConsole.MarkupLine($"[red]Error:[/] cannot infer --side for strategy '{strategy}'. Pass --side buy (net-debit) or --side sell (net-credit) explicitly."); return 2; }
		}

		// 6. Build order.
		var body = OrderRequestBuilder.Build(new OrderRequestBuilder.BuildParams(
			AccountId: account.AccountId,
			Legs: legs,
			Strategy: strategy,
			Side: side,
			OrderType: type.ToUpperInvariant(),
			LimitPrice: limit,
			TimeInForce: s.Tif.ToUpperInvariant()
		));

		AnsiConsole.MarkupLine($"[dim]Client order ID:[/] [bold]{Markup.Escape(body.NewOrders[0].ClientOrderId)}[/]  [dim]Strategy:[/] {Markup.Escape(strategy)}  [dim]Side:[/] {Markup.Escape(side)}  [dim]Type:[/] {type.ToUpperInvariant()}  [dim]TIF:[/] {s.Tif.ToUpperInvariant()}");
		if (s.Debug)
			AnsiConsole.MarkupLine($"[dim]Payload:[/] {Markup.Escape(OrderRequestBuilder.Serialize(body))}");

		// 7. Preview.
		using var client = new WebullOpenApiClient(account);
      WebullOpenApiClient.PreviewResponse previewResponse;
		try
		{
			previewResponse = s.Debug
				? await client.PreviewOrderWithRawAsync(body)
				: new WebullOpenApiClient.PreviewResponse(await client.PreviewOrderAsync(body), "");
		}
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]Preview failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
		catch (HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }
        var preview = previewResponse.Result;
		var marginSummary = preview.TryGetMarginSummary();
		var marginText = string.IsNullOrEmpty(marginSummary) ? "" : $"  {Markup.Escape(marginSummary)}";
		AnsiConsole.MarkupLine($"[bold]Preview:[/] cost={TradeContext.FormatCurrency(preview.EstimatedCost)}  fees={TradeContext.FormatCurrency(preview.EstimatedTransactionFee)}{marginText}");
		if (s.Debug)
			AnsiConsole.MarkupLine($"[dim]Preview response:[/] {Markup.Escape(previewResponse.RawJson)}");

		if (!s.Submit) { AnsiConsole.MarkupLine("[dim]Preview only (no --submit). Exiting.[/]"); return 0; }

		// 8. Confirm and place.
		if (!TradeContext.Confirm("Place this order?")) { AnsiConsole.MarkupLine("[dim]Aborted.[/]"); return 0; }

		try
		{
			var placed = await client.PlaceOrderAsync(body);
			AnsiConsole.MarkupLine($"[green]Placed.[/] order_id={Markup.Escape(placed.OrderId ?? "-")}  client_order_id={Markup.Escape(placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId)}");
			AnsiConsole.MarkupLine($"[dim]Check status with:[/] wa trade status {Markup.Escape(placed.ClientOrderId ?? body.NewOrders[0].ClientOrderId)}");
			return 0;
		}
		catch (WebullOpenApiException ex)
		{
			AnsiConsole.MarkupLine($"[red]Place failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]");
			AnsiConsole.MarkupLine("[dim](Preview succeeded but place was rejected.)[/]");
			return 3;
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}");
			AnsiConsole.MarkupLine("[dim](Preview succeeded but place could not be sent.)[/]");
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
			catch (HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }

			if (orders.Count == 0) { AnsiConsole.MarkupLine("[dim]No open orders.[/]"); return 0; }

			AnsiConsole.MarkupLine($"[bold]{orders.Count} open order(s):[/]");
			foreach (var o in orders)
			{
				var inner = o.Orders?.FirstOrDefault();
				AnsiConsole.MarkupLine($"  {Markup.Escape(o.ClientOrderId ?? "?"),-22} {Markup.Escape(inner?.Symbol ?? "?"),-8} {Markup.Escape(inner?.Side ?? "?"),-5} {Markup.Escape(inner?.FilledQuantity ?? "0")}/{Markup.Escape(inner?.TotalQuantity ?? "?")} {Markup.Escape(inner?.Status ?? "?")}");
			}

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
				catch (HttpRequestException ex)
				{
					AnsiConsole.MarkupLine($"  [red]failed[/] {Markup.Escape(o.ClientOrderId)} (network) {Markup.Escape(ex.Message)}");
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
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}");
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
		catch (HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }

		AnsiConsole.MarkupLine($"[bold]Combo type:[/] {Markup.Escape(detail.ComboType ?? "-")}  [bold]Combo order ID:[/] {Markup.Escape(detail.ComboOrderId ?? "-")}");
		if (detail.Orders == null || detail.Orders.Count == 0)
		{ AnsiConsole.MarkupLine("[dim]No orders returned.[/]"); return 0; }

		foreach (var o in detail.Orders)
		{
			AnsiConsole.MarkupLine($"[bold]Order[/] {Markup.Escape(o.ClientOrderId ?? "-")}  [dim]id[/]={Markup.Escape(o.OrderId ?? "-")}  [dim]status[/]={Markup.Escape(o.Status ?? "-")}");
			var occ = o.Legs != null && o.Legs.Count == 1 ? TradeContext.BuildOccSymbol(o.Legs[0]) : null;
			var occSuffix = occ != null ? $" {Markup.Escape(occ)}" : "";
			AnsiConsole.MarkupLine($"  {Markup.Escape(o.Symbol ?? "-")} {Markup.Escape(o.Side ?? "-")} {Markup.Escape(TradeContext.FormatQty(o.TotalQuantity))}{occSuffix} @ {Markup.Escape(TradeContext.FormatCurrency(o.LimitPrice))}");
			var hasFill = decimal.TryParse(o.FilledQuantity, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fq) && fq > 0m;
			var fillPriceSuffix = hasFill && !string.IsNullOrEmpty(o.FilledPrice) ? $" @ {TradeContext.FormatCurrency(o.FilledPrice)}" : "";
			AnsiConsole.MarkupLine($"  [dim]placed:[/] {Markup.Escape(o.PlaceTimeAt ?? o.PlaceTime ?? "-")}  [dim]filled:[/] {Markup.Escape(TradeContext.FormatQty(o.FilledQuantity))}/{Markup.Escape(TradeContext.FormatQty(o.TotalQuantity))}{Markup.Escape(fillPriceSuffix)}  [dim]intent:[/] {Markup.Escape(o.PositionIntent ?? "-")}");
			if (o.Legs != null)
				foreach (var leg in o.Legs)
					AnsiConsole.MarkupLine($"  └─ {Markup.Escape(leg.Symbol ?? "-")} {Markup.Escape(leg.Side ?? "-")} {Markup.Escape(TradeContext.FormatQty(leg.Quantity))} {Markup.Escape(leg.OptionType ?? "")} strike={Markup.Escape(leg.StrikePrice ?? "-")} exp={Markup.Escape(leg.OptionExpireDate ?? "-")}");
		}
		return 0;
	}
}

// ─── `trade list` ─────────────────────────────────────────────────────────────

internal sealed class TradeListSettings : TradeSubcommandSettings
{
	[CommandOption("--debug")]
	[Description("Print the raw JSON response from Webull instead of the formatted table.")]
	public bool Debug { get; set; }
}

internal sealed class TradeListCommand : AsyncCommand<TradeListSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeListSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		using var client = new WebullOpenApiClient(account);

		if (s.Debug)
		{
			try
			{
				var raw = await client.ListOpenOrdersRawAsync(cancellation);
				AnsiConsole.WriteLine(raw);
				return 0;
			}
			catch (System.Net.Http.HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }
		}

		List<WebullOpenApiClient.OpenOrder> orders;
		try { orders = await client.ListOpenOrdersAsync(cancellation); }
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]List failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
		catch (System.Net.Http.HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }

		if (orders.Count == 0) { AnsiConsole.MarkupLine("[dim]No open orders.[/]"); return 0; }

		AnsiConsole.MarkupLine($"[bold]{orders.Count} open order(s):[/]");
		foreach (var o in orders)
		{
			var inner = o.Orders?.FirstOrDefault();
			AnsiConsole.MarkupLine($"  {Markup.Escape(o.ClientOrderId ?? "?"),-22} {Markup.Escape(inner?.Symbol ?? "?"),-8} {Markup.Escape(inner?.Side ?? "?"),-5} {Markup.Escape(inner?.FilledQuantity ?? "0")}/{Markup.Escape(inner?.TotalQuantity ?? "?")} {Markup.Escape(inner?.Status ?? "?")}");
		}
		return 0;
	}
}

// ─── `trade positions` (diagnostic) ───────────────────────────────────────────

internal sealed class TradePositionsSettings : TradeSubcommandSettings { }

internal sealed class TradePositionsCommand : AsyncCommand<TradePositionsSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradePositionsSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		using var client = new WebullOpenApiClient(account);
		try
		{
			var raw = await client.FetchAccountPositionsRawAsync(cancellation);
			AnsiConsole.WriteLine(raw);
			return 0;
		}
		catch (System.Net.Http.HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }
	}
}

// ─── `trade token create` / `trade token check` ──────────────────────────────

internal sealed class TradeTokenCreateSettings : TradeSubcommandSettings { }

internal sealed class TradeTokenCreateCommand : AsyncCommand<TradeTokenCreateSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeTokenCreateSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		using var client = new WebullOpenApiClient(account);
		WebullOpenApiClient.TokenCreateResult result;
		try { result = await client.CreateTokenAsync(cancellation); }
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]Create failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
		catch (System.Net.Http.HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }

		if (string.IsNullOrEmpty(result.Token)) { AnsiConsole.MarkupLine("[red]No token in response.[/]"); return 3; }

		TokenStore.Save(account.Alias, result.Token, result.Expires ?? 0, result.Status ?? "");
		AnsiConsole.MarkupLine($"[bold]Token created:[/] status=[yellow]{Markup.Escape(result.Status ?? "?")}[/]");
		AnsiConsole.MarkupLine($"  [dim]token:[/]   {Markup.Escape(result.Token)}");
		AnsiConsole.MarkupLine($"  [dim]expires:[/] {result.Expires}");
		AnsiConsole.MarkupLine($"  [dim]saved to:[/] {TokenStore.StorePath}");
		if (result.Status == "PENDING")
		{
			AnsiConsole.WriteLine();
			AnsiConsole.MarkupLine("[bold yellow]Approve within 5 minutes:[/]");
			AnsiConsole.MarkupLine("  1. Open the Webull mobile app");
			AnsiConsole.MarkupLine("  2. Menu → Messages → OpenAPI Notifications");
			AnsiConsole.MarkupLine("  3. Tap the latest verification message → Check Now");
			AnsiConsole.MarkupLine("  4. Enter the SMS code → Confirm");
			AnsiConsole.MarkupLine($"  5. Run: [bold]WebullAnalytics trade token check --account {Markup.Escape(account.Alias)}[/]");
		}
		return 0;
	}
}

internal sealed class TradeTokenCheckSettings : TradeSubcommandSettings { }

internal sealed class TradeTokenCheckCommand : AsyncCommand<TradeTokenCheckSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeTokenCheckSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		var cached = TokenStore.Load(account.Alias);
		if (cached == null) { AnsiConsole.MarkupLine($"[red]No cached token for '{Markup.Escape(account.Alias)}'. Run 'trade token create --account {Markup.Escape(account.Alias)}' first.[/]"); return 3; }

		using var client = new WebullOpenApiClient(account);
		WebullOpenApiClient.TokenCheckResult result;
		try { result = await client.CheckTokenAsync(cached.Token, cancellation); }
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]Check failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
		catch (System.Net.Http.HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }

		TokenStore.Save(account.Alias, cached.Token, result.Expires ?? cached.Expires, result.Status ?? "");
		var color = result.Status switch { "NORMAL" => "green", "PENDING" => "yellow", _ => "red" };
		AnsiConsole.MarkupLine($"[bold]Token status:[/] [{color}]{Markup.Escape(result.Status ?? "?")}[/]");
		AnsiConsole.MarkupLine($"  [dim]expires:[/] {result.Expires}");
		if (result.Status == "NORMAL")
			AnsiConsole.MarkupLine("[green]Ready — other trade/ai commands will authenticate with this token.[/]");
		return 0;
	}
}

// ─── `trade accounts` ─────────────────────────────────────────────────────────

internal sealed class TradeAccountsSettings : TradeSubcommandSettings { }

internal sealed class TradeAccountsCommand : AsyncCommand<TradeAccountsSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeAccountsSettings s, CancellationToken cancellation)
	{
		var account = TradeContext.ResolveOrExit(s.Account);
		if (account == null) return 2;

		using var client = new WebullOpenApiClient(account);
		List<WebullOpenApiClient.AppSubscription> subs;
		try { subs = await client.ListAppSubscriptionsAsync(cancellation); }
		catch (WebullOpenApiException ex) { AnsiConsole.MarkupLine($"[red]List failed [[{Markup.Escape(ex.ErrorCode ?? "?")}]]: {Markup.Escape(ex.Message)}[/]"); return 3; }
		catch (System.Net.Http.HttpRequestException ex) { AnsiConsole.MarkupLine($"[red]Network error:[/] {Markup.Escape(ex.Message)}"); return 3; }

		if (subs.Count == 0) { AnsiConsole.MarkupLine("[dim]No account subscriptions.[/]"); return 0; }

		AnsiConsole.MarkupLine($"[bold]{subs.Count} account subscription(s):[/]");
		AnsiConsole.MarkupLine("  [dim]Copy [bold]account_id[/] (not account_number) into your trade-config.json as 'accountId'.[/]");
		AnsiConsole.WriteLine();
		foreach (var sub in subs)
		{
			AnsiConsole.MarkupLine($"  [bold]account_id:[/]       {Markup.Escape(sub.AccountId ?? "?")}");
			AnsiConsole.MarkupLine($"  [dim]account_number:[/]   {Markup.Escape(sub.AccountNumber ?? "?")}");
			AnsiConsole.MarkupLine($"  [dim]subscription_id:[/]  {Markup.Escape(sub.SubscriptionId ?? "?")}");
			AnsiConsole.MarkupLine($"  [dim]user_id:[/]          {Markup.Escape(sub.UserId ?? "?")}");
			AnsiConsole.WriteLine();
		}
		return 0;
	}
}
