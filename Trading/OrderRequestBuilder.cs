using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.Trading;

/// <summary>
/// Holds the parameters for a single "new order" submission plus the account ID.
/// Serialized directly to the JSON body of /openapi/trade/order/preview and /place.
/// </summary>
internal sealed class OrderRequestBody
{
	[JsonPropertyName("account_id")] public string AccountId { get; set; } = "";
	[JsonPropertyName("client_combo_order_id")] public string? ClientComboOrderId { get; set; }
	[JsonPropertyName("new_orders")] public List<NewOrder> NewOrders { get; set; } = new();
}

internal sealed class NewOrder
{
	[JsonPropertyName("client_order_id")] public string ClientOrderId { get; set; } = "";
	[JsonPropertyName("combo_type")] public string ComboType { get; set; } = "NORMAL";
	[JsonPropertyName("entrust_type")] public string EntrustType { get; set; } = "QTY";
	[JsonPropertyName("instrument_type")] public string InstrumentType { get; set; } = "EQUITY";
	[JsonPropertyName("market")] public string Market { get; set; } = "US";
	[JsonPropertyName("order_type")] public string OrderType { get; set; } = "LIMIT";
	[JsonPropertyName("side")] public string Side { get; set; } = "BUY";
	[JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
	[JsonPropertyName("time_in_force")] public string TimeInForce { get; set; } = "DAY";
	[JsonPropertyName("support_trading_session")] public string SupportTradingSession { get; set; } = "CORE";
	[JsonPropertyName("quantity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Quantity { get; set; }
	[JsonPropertyName("limit_price"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LimitPrice { get; set; }
	[JsonPropertyName("stop_price"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StopPrice { get; set; }
	[JsonPropertyName("trailing_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TrailingType { get; set; }
	[JsonPropertyName("trailing_stop_step"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TrailingStopStep { get; set; }
	[JsonPropertyName("option_strategy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OptionStrategy { get; set; }
	[JsonPropertyName("position_intent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PositionIntent { get; set; }
	[JsonPropertyName("legs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<OrderLeg>? Legs { get; set; }
}

internal sealed class OrderLeg
{
	[JsonPropertyName("instrument_type")] public string InstrumentType { get; set; } = "OPTION";
	[JsonPropertyName("market")] public string Market { get; set; } = "US";
	[JsonPropertyName("side")] public string Side { get; set; } = "BUY";
	[JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
	[JsonPropertyName("quantity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Quantity { get; set; }
	[JsonPropertyName("strike_price"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StrikePrice { get; set; }
	[JsonPropertyName("option_expire_date"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OptionExpireDate { get; set; }
	[JsonPropertyName("option_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OptionType { get; set; }
}

/// <summary>
/// Turns CLI-level trade parameters into an OrderRequestBody that can be sent to the Webull OpenAPI.
/// Also owns the mapping between our internal strategy names and the OpenAPI option_strategy enum.
/// </summary>
internal static class OrderRequestBuilder
{
	// Verified empirically in Task 11 (sandbox preview calls).
	// If Webull rejects a value, correct the single entry below.
	private static readonly Dictionary<string, string> OptionStrategyEnum = new(StringComparer.OrdinalIgnoreCase)
	{
		["Single"] = "SINGLE",
		["Vertical"] = "VERTICAL",
		["Calendar"] = "CALENDAR",
		["Diagonal"] = "DIAGONAL",
		["Straddle"] = "STRADDLE",
		["Strangle"] = "STRANGLE",
		["Butterfly"] = "BUTTERFLY",
		["Condor"] = "CONDOR",
		["IronButterfly"] = "IRON_BUTTERFLY",
		["IronCondor"] = "IRON_CONDOR",
		["CoveredCall"] = "COVERED_STOCK",
		["ProtectivePut"] = "COVERED_STOCK",
		["Collar"] = "COLLAR_WITH_STOCK",
	};

	/// <summary>Generates an 18-char client order ID: YYMMDD-HHMMSS-XXXX.</summary>
	internal static string GenerateClientOrderId(DateTime? nowUtc = null)
	{
		var n = nowUtc ?? DateTime.UtcNow;
		Span<byte> rand = stackalloc byte[2];
		System.Security.Cryptography.RandomNumberGenerator.Fill(rand);
		return $"{n:yyMMdd}-{n:HHmmss}-{rand[0]:X2}{rand[1]:X2}";
	}

	internal sealed record BuildParams(
		string AccountId,
		IReadOnlyList<ParsedLeg> Legs,
		string Strategy,          // value from StrategyClassifier / --strategy flag
		string Side,              // "BUY" or "SELL" — explicit combo direction
		string OrderType,         // "LIMIT" or "MARKET"
		decimal? LimitPrice,      // absolute per-share combo price; null only when OrderType == MARKET
		string TimeInForce,       // "DAY" or "GTC"
		string? PositionIntent = null, // BUY_TO_OPEN | SELL_TO_OPEN | BUY_TO_CLOSE | SELL_TO_CLOSE; applied to order + every option leg
		string TradingSession = "CORE", // support_trading_session: CORE (09:30-16:00 ET) | ALL (04:00-20:00) | NIGHT (20:00-04:00) | ALL_DAY
		decimal? StopPrice = null,      // STOP_LOSS / STOP_LOSS_LIMIT trigger (absolute per-share)
		string? TrailingType = null,    // TRAILING_STOP_LOSS: PERCENTAGE | AMOUNT
		decimal? TrailingStep = null,   // TRAILING_STOP_LOSS: trail distance (percent or amount per TrailingType)
		decimal? ProtectiveStop = null,  // attach a STOP_LOSS child that Webull activates when this order fills (MASTER + STOP_LOSS combo)
		decimal? ProtectiveLimit = null); // with ProtectiveStop: make the child a STOP_LOSS_LIMIT at this limit instead of a stop-market

	internal static OrderRequestBody Build(BuildParams p)
	{
		var body = new OrderRequestBody { AccountId = p.AccountId };
		// Webull requires limit_price to be the ABSOLUTE per-share combo price; direction is encoded in `side`.
		// A signed value (e.g. "-0.35" with side=BUY) is rejected with OAUTH_OPENAPI_OPTION_MULTI_ORDER_BUY_NOT_NEGATIVE.
		var order = new NewOrder
		{
			ClientOrderId = GenerateClientOrderId(),
			OrderType = p.OrderType,
			TimeInForce = p.TimeInForce,
			SupportTradingSession = p.TradingSession,
			LimitPrice = p.LimitPrice is decimal lp ? Math.Abs(lp).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : null,
			StopPrice = p.StopPrice is decimal sp ? sp.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : null,
			TrailingType = p.TrailingType,
			TrailingStopStep = p.TrailingStep is decimal ts ? ts.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : null,
		};
		body.NewOrders.Add(order);

		// Route by strategy.
		var strat = p.Strategy;
		var stockLegs = p.Legs.Where(l => l.Option == null).ToList();
		var optionLegs = p.Legs.Where(l => l.Option != null).ToList();

		if (string.Equals(strat, "Stock", StringComparison.OrdinalIgnoreCase))
		{
			var leg = stockLegs[0];
			order.InstrumentType = "EQUITY";
			order.ComboType = "NORMAL";
			order.Symbol = leg.Symbol;
			order.Side = p.Side;
			order.Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
			return body;
		}

		if (string.Equals(strat, "Single", StringComparison.OrdinalIgnoreCase))
		{
			var leg = optionLegs[0];
			order.InstrumentType = "OPTION";
			order.ComboType = "NORMAL";
			order.Symbol = leg.Option!.Root;
			order.Side = p.Side;
			order.Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
			order.OptionStrategy = OptionStrategyEnum["Single"];
			order.PositionIntent = p.PositionIntent; // option-only field; set on the order (MASTER), never per leg
			order.Legs = new List<OrderLeg> { BuildOptionLeg(leg) };
			if (p.ProtectiveStop is decimal stop) AttachStopLoss(body, order, optionLegs, stop, p.ProtectiveLimit);
			return body;
		}

		// Multi-leg combo (may include a stock leg for covered call / protective put / collar).
		body.ClientComboOrderId = GenerateClientOrderId();
		order.InstrumentType = "OPTION";
		order.ComboType = "NORMAL";
		order.Symbol = optionLegs[0].Option!.Root;
		order.Quantity = optionLegs[0].Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
		order.Side = p.Side;
		order.OptionStrategy = OptionStrategyEnum.TryGetValue(strat, out var mapped)
			? mapped
			: throw new InvalidOperationException($"Unknown strategy '{strat}' — extend OptionStrategyEnum");
		order.PositionIntent = p.PositionIntent; // option-only field; set on the order (MASTER), never per leg
		order.Legs = new List<OrderLeg>();
		foreach (var leg in stockLegs) order.Legs.Add(BuildStockLeg(leg));
		foreach (var leg in optionLegs) order.Legs.Add(BuildOptionLeg(leg));
		if (p.ProtectiveStop is decimal comboStop) AttachStopLoss(body, order, optionLegs, comboStop, p.ProtectiveLimit);
		return body;
	}

	/// <summary>Turns <paramref name="master"/> into the MASTER of a combo and appends a STOP_LOSS child that closes the same contract
	/// once the master fills (every leg flipped; the stop is on the net price). The child is GTC (Webull's documented example) and carries the opposite side with a *_TO_CLOSE intent.</summary>
	private static void AttachStopLoss(OrderRequestBody body, NewOrder master, IReadOnlyList<ParsedLeg> optionLegs, decimal stop, decimal? limit)
	{
		body.ClientComboOrderId = GenerateClientOrderId();
		master.ComboType = "MASTER";
		var closeSide = master.Side == "BUY" ? "SELL" : "BUY";
		var closeLegs = optionLegs.Select(l => { var c = BuildOptionLeg(l); c.Side = c.Side == "BUY" ? "SELL" : "BUY"; return c; }).ToList();
		body.NewOrders.Add(new NewOrder
		{
			ClientOrderId = GenerateClientOrderId(),
			ComboType = "STOP_LOSS",
			InstrumentType = "OPTION",
			OrderType = limit == null ? "STOP_LOSS" : "STOP_LOSS_LIMIT",
			LimitPrice = limit?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
			Side = closeSide,
			Symbol = master.Symbol,
			Quantity = master.Quantity,
			TimeInForce = "GTC",
			SupportTradingSession = master.SupportTradingSession,
			StopPrice = stop.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
			OptionStrategy = master.OptionStrategy,
			PositionIntent = DeriveOptionIntent(closeSide, opening: false),
			Legs = closeLegs,
		});
	}

	private static OrderLeg BuildStockLeg(ParsedLeg leg) => new()
	{
		InstrumentType = "EQUITY",
		Market = "US",
		Side = leg.Action == LegAction.Buy ? "BUY" : "SELL",
		Symbol = leg.Symbol,
		Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
	};

	private static OrderLeg BuildOptionLeg(ParsedLeg leg) => new()
	{
		InstrumentType = "OPTION",
		Market = "US",
		Side = leg.Action == LegAction.Buy ? "BUY" : "SELL",
		Symbol = leg.Option!.Root,
		Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
		StrikePrice = leg.Option.Strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
		OptionExpireDate = leg.Option.ExpiryDate.ToString("yyyy-MM-dd"),
		OptionType = leg.Option.CallPut == "C" ? "CALL" : "PUT",
	};

	/// <summary>Derives the Webull <c>position_intent</c> for an option order from the combo side and
	/// whether it opens or closes exposure. position_intent is an order-level (MASTER) field and must
	/// match <c>side</c>; it is NOT set per leg. Returns e.g. "SELL_TO_OPEN" for a credit/short open,
	/// "BUY_TO_CLOSE" for buying back a short, etc.</summary>
	internal static string DeriveOptionIntent(string side, bool opening) =>
		$"{side.ToUpperInvariant()}_TO_{(opening ? "OPEN" : "CLOSE")}";

	private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

	/// <summary>Serializes the body to compact JSON (no whitespace between keys and values).</summary>
	internal static string Serialize(OrderRequestBody body) =>
		JsonSerializer.Serialize(body, CompactJson);
}
