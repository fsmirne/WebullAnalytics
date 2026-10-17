using System.Globalization;
using Spectre.Console;
using WebullAnalytics.Api;
using WebullAnalytics.IO;
using WebullAnalytics.Trading;

namespace WebullAnalytics.Analyze;

/// <summary>
/// <c>--fill [N]</c> for the analyze commands: lists your recent OPENING fills straight from the broker (the
/// OpenAPI order history of the default account, last <see cref="LookbackDays"/> days) — opener-placed or
/// manual, any ticker. An order counts as an open when Webull's own <c>position_intent</c> says
/// <c>BUY_TO_OPEN</c>/<c>SELL_TO_OPEN</c>; closes and rolls (which Webull labels <c>*_TO_CLOSE</c>) are excluded.
/// N is the 1-based position in that list, newest first — not a file line number.
/// The OpenAPI history only carries the combo's NET fill price, so per-leg fill prices come from Webull's
/// web order list: the cached <c>data/orders.jsonl</c> first, then a live single-ticker <c>orderList</c> call
/// (needs a fresh web session — <c>wa sniff</c>) for tickers <c>wa fetch</c> doesn't cover or fills newer than
/// the last fetch. No approximation: if neither source has the legs, it says so.
/// </summary>
internal static class OpeningFillPicker
{
	internal const int LookbackDays = 30;

	internal sealed record FillLeg(LegAction Action, string Symbol, int Qty, OptionParsed Parsed);

	internal sealed record PricedLeg(FillLeg Leg, decimal Price);

	internal sealed record OpeningFill(string OrderId, string Ticker, string Strategy, LegAction Side, int Qty, decimal NetPrice, long PlacedMs, long FilledMs, IReadOnlyList<FillLeg> Legs)
	{
		public DateTime FilledEt => ToEt(FilledMs);
		public DateTime PlacedEt => ToEt(PlacedMs);
	}

	private static readonly TimeZoneInfo EtZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	private static DateTime ToEt(long epochMs) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime, EtZone);

	/// <summary>trade/position path: pick an unexpired opening fill and return it as a <c>&lt;spec&gt;</c> at its
	/// real per-leg fill prices. Returns a clean error string, never throws.</summary>
	public static async Task<(string? Spec, string? Error)> ResolveSpecAsync(int index, CancellationToken cancellation)
	{
		var (config, fills, error) = await LoadAsync(unexpiredOnly: true, cancellation);
		if (fills == null) return (null, error);

		var (fill, pickError) = Pick(fills, index, "Select an opening fill to analyze:");
		if (fill == null) return (null, pickError);

		var (legs, source, priceError) = await PriceLegsAsync(config!, fill, cancellation);
		if (legs == null) return (null, priceError);

		Console.WriteLine($"Opening fill: {FormatLabel(fill)} — leg prices from {source}; evaluating against the live market now.");
		return (string.Join(",", legs.Select(l => $"{(l.Leg.Action == LegAction.Buy ? "buy" : "sell")}:{l.Leg.Symbol}:{l.Leg.Qty}@{l.Price.ToString(CultureInfo.InvariantCulture)}")), null);
	}

	/// <summary>Pulls the default account's order history and keeps filled option orders whose
	/// position_intent is an open, newest first. <paramref name="unexpiredOnly"/> drops orders with any leg past
	/// expiry (live quotes can't price them).</summary>
	public static async Task<(ApiConfig? Config, List<OpeningFill>? Fills, string? Error)> LoadAsync(bool unexpiredOnly, CancellationToken cancellation)
	{
		var config = TradeConfig.Load(quiet: true);
		if (config == null) return (null, null, "--fill: could not load api-config.json");
		var account = TradeConfig.Resolve(config, null, quiet: true);
		if (account == null) return (null, null, "--fill: no defaultAccount in api-config.json");

		var todayEt = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EtZone).Date;
		List<WebullOpenApiClient.OpenOrder> orders;
		try
		{
			using var client = new WebullOpenApiClient(account);
			orders = await client.ListOrderHistoryAsync(todayEt.AddDays(-LookbackDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), todayEt.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cancellation);
		}
		catch (WebullOpenApiException ex) { return (null, null, $"--fill: order history lookup failed [{ex.ErrorCode ?? "?"}]: {ex.Message}"); }
		catch (HttpRequestException ex) { return (null, null, $"--fill: network error: {ex.Message}"); }

		var fills = orders.SelectMany(co => co.Orders ?? new()).Select(ToOpeningFill).OfType<OpeningFill>()
			.Where(f => !unexpiredOnly || f.Legs.All(l => l.Parsed.ExpiryDate.Date >= EvaluationDate.Today.Date))
			.OrderByDescending(f => f.FilledMs)
			.ToList();
		if (fills.Count == 0)
			return (null, null, $"--fill: no {(unexpiredOnly ? "unexpired " : "")}opening fills on account {account.Alias} in the last {LookbackDays} days");
		return (config, fills, null);
	}

	private static OpeningFill? ToOpeningFill(WebullOpenApiClient.OrderDetailOrder o)
	{
		if (!string.Equals(o.Status, "FILLED", StringComparison.OrdinalIgnoreCase)) return null;
		if (o.PositionIntent is not ("BUY_TO_OPEN" or "SELL_TO_OPEN")) return null;
		if (o.Legs is not { Count: > 0 } || string.IsNullOrEmpty(o.OrderId)) return null;
		if (!long.TryParse(o.FilledTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var filledMs)) return null;
		if (!long.TryParse(o.PlaceTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var placedMs)) placedMs = filledMs;
		if (!decimal.TryParse(o.FilledPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var net)) return null;
		if (!int.TryParse(o.FilledQuantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0) return null;

		var legs = new List<FillLeg>(o.Legs.Count);
		foreach (var leg in o.Legs)
		{
			var occ = TradeContext.BuildOccSymbol(leg);
			var parsed = occ != null ? ParsingHelpers.ParseOptionSymbol(occ) : null;
			if (parsed == null || !int.TryParse(leg.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legQty)) return null;
			legs.Add(new FillLeg(string.Equals(leg.Side, "BUY", StringComparison.OrdinalIgnoreCase) ? LegAction.Buy : LegAction.Sell, occ!, legQty, parsed));
		}

		var side = string.Equals(o.Side, "BUY", StringComparison.OrdinalIgnoreCase) ? LegAction.Buy : LegAction.Sell;
		return new OpeningFill(o.OrderId, legs[0].Parsed.Root, o.OptionStrategy ?? "?", side, qty, net, placedMs, filledMs, legs);
	}

	/// <summary>Selects one fill: <paramref name="index"/> N ≥ 1 = the N-th in the list; 0 = interactive (a lone
	/// candidate is auto-picked). Non-interactive shells get the numbered list and an error asking for --fill N.</summary>
	public static (OpeningFill? Fill, string? Error) Pick(IReadOnlyList<OpeningFill> fills, int index, string title)
	{
		if (index > 0)
			return index <= fills.Count ? (fills[index - 1], null) : (null, $"--fill: {index} is out of range (there are {fills.Count} fills)");
		if (fills.Count == 1)
			return (fills[0], null);
		if (!AnsiConsole.Profile.Capabilities.Interactive)
		{
			AnsiConsole.MarkupLine($"[bold]{fills.Count} opening fill(s), newest first:[/]");
			for (var i = 0; i < fills.Count; i++)
				AnsiConsole.MarkupLine($"  {i + 1,3}  {Markup.Escape(FormatLabel(fills[i]))}");
			return (null, "--fill: non-interactive shell; pass --fill N to pick one from the list above");
		}
		return (AnsiConsole.Prompt(new SelectionPrompt<OpeningFill>().Title(title).PageSize(15).UseConverter(FormatLabel).AddChoices(fills)), null);
	}

	/// <summary>Per-leg fill prices for <paramref name="fill"/>: the cached orders.jsonl first, then a live
	/// orderList call for the fill's ticker. Returns the source used, or an error when neither has every leg.</summary>
	public static async Task<(IReadOnlyList<PricedLeg>? Legs, string? Source, string? Error)> PriceLegsAsync(ApiConfig config, OpeningFill fill, CancellationToken cancellation)
	{
		var ordersPath = Program.ResolvePath(Program.OrdersPath);
		if (File.Exists(ordersPath))
		{
			var cached = SharedFileReader.ReadAllLines(ordersPath).Where(l => !string.IsNullOrWhiteSpace(l)).SelectMany(JsonlParser.ParseOptionLegFills).ToList();
			if (MatchLegs(fill, cached) is { } fromCache) return (Checked(fill, fromCache), "orders.jsonl", null);
		}

		var ids = await WebullOptionsClient.ResolveTickerIdsAsync(new[] { fill.Ticker }, cancellation);
		if (!ids.TryGetValue(fill.Ticker, out var tickerId))
			return (null, null, $"--fill: could not resolve Webull ticker id for {fill.Ticker}");

		string json;
		try
		{
			using var http = new HttpClient();
			var day = fill.FilledEt.Date;
			json = await ApiClient.FetchTickerOrderListAsync(http, config, tickerId, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cancellation);
		}
		catch (HttpRequestException ex)
		{
			return (null, null, $"--fill: per-leg prices for this fill aren't in orders.jsonl and the live Webull order list failed ({ex.Message}). Refresh the web session with 'wa sniff' and retry.");
		}

		var live = MatchLegs(fill, JsonlParser.ParseOptionLegFills(json));
		return live != null
			? (Checked(fill, live), "Webull order list (live)", null)
			: (null, null, $"--fill: Webull's order list has no per-leg rows matching order {fill.OrderId} yet (it can lag fills by 10+ minutes); retry shortly.");
	}

	/// <summary>Each leg's rows: same contract and side, executed between placement and the final fill. They
	/// must add up to exactly the leg's quantity (else the window caught another order, or the list is
	/// incomplete) — the leg's price is then their quantity-weighted average.</summary>
	private static List<PricedLeg>? MatchLegs(OpeningFill fill, IReadOnlyList<JsonlParser.OptionLegFill> rows)
	{
		// transactTime is truncated to the second; allow one second either side of the OpenAPI millisecond stamps.
		var fromMs = fill.PlacedMs - 1000;
		var toMs = fill.FilledMs + 1000;
		var priced = new List<PricedLeg>(fill.Legs.Count);
		foreach (var leg in fill.Legs)
		{
			var side = leg.Action == LegAction.Buy ? Side.Buy : Side.Sell;
			var matches = rows.Where(r => r.Side == side && r.TransactTimeMs >= fromMs && r.TransactTimeMs <= toMs && string.Equals(r.OccSymbol, leg.Symbol, StringComparison.OrdinalIgnoreCase)).ToList();
			if (matches.Sum(r => r.Qty) != leg.Qty) return null;
			priced.Add(new PricedLeg(leg, matches.Sum(r => r.Price * r.Qty) / leg.Qty));
		}
		return priced;
	}

	/// <summary>Warns when the matched legs don't reproduce the broker's net fill price (beyond quote rounding).</summary>
	private static List<PricedLeg> Checked(OpeningFill fill, List<PricedLeg> legs)
	{
		var net = Math.Abs(legs.Sum(l => (l.Leg.Action == LegAction.Sell ? 1m : -1m) * l.Price * l.Leg.Qty)) / fill.Qty;
		if (Math.Abs(net - fill.NetPrice) > 0.01m)
			Console.Error.WriteLine($"Warning: leg fills net to {net:0.00##} but Webull reports the order filled at {fill.NetPrice:0.00##}.");
		return legs;
	}

	internal static string FormatLabel(OpeningFill f)
	{
		var legs = string.Join("  ", f.Legs.OrderBy(l => l.Parsed.ExpiryDate).ThenBy(l => l.Parsed.Strike).Select(l => $"{(l.Action == LegAction.Buy ? "+" : "-")}{l.Parsed.CallPut}{l.Parsed.Strike.ToString("0.##", CultureInfo.InvariantCulture)} {l.Parsed.ExpiryDate:MM-dd}"));
		var net = $"{(f.Side == LegAction.Buy ? "debit" : "credit")} {f.NetPrice.ToString("0.00##", CultureInfo.InvariantCulture)}";
		return $"{f.FilledEt:yyyy-MM-dd HH:mm}  {f.Ticker}  {f.Strategy}  x{f.Qty}  {legs}  ({net})";
	}
}
