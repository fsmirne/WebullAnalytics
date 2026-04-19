using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics;

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
	[JsonPropertyName("entrust_type")] public string EntrustType { get; set; } = "LMT";
	[JsonPropertyName("instrument_type")] public string InstrumentType { get; set; } = "EQUITY";
	[JsonPropertyName("market")] public string Market { get; set; } = "US";
	[JsonPropertyName("order_type")] public string OrderType { get; set; } = "LIMIT";
	[JsonPropertyName("side")] public string Side { get; set; } = "BUY";
	[JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
	[JsonPropertyName("time_in_force")] public string TimeInForce { get; set; } = "DAY";
	[JsonPropertyName("quantity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Quantity { get; set; }
	[JsonPropertyName("limit_price"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LimitPrice { get; set; }
	[JsonPropertyName("option_strategy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OptionStrategy { get; set; }
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
		["ProtectivePut"] = "PROTECTIVE_PUT",
		["Collar"] = "COLLAR",
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
		string OrderType,         // "LIMIT" or "MARKET"
		decimal? LimitPrice,      // null only when OrderType == MARKET
		string TimeInForce);      // "DAY" or "GTC"

	internal static OrderRequestBody Build(BuildParams p)
	{
		var body = new OrderRequestBody { AccountId = p.AccountId };
		var order = new NewOrder
		{
			ClientOrderId = GenerateClientOrderId(),
			OrderType = p.OrderType,
			TimeInForce = p.TimeInForce,
			LimitPrice = p.OrderType == "LIMIT" ? p.LimitPrice?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : null,
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
			order.Side = leg.Action == LegAction.Buy ? "BUY" : "SELL";
			order.Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
			return body;
		}

		if (string.Equals(strat, "Single", StringComparison.OrdinalIgnoreCase))
		{
			var leg = optionLegs[0];
			order.InstrumentType = "OPTION";
			order.ComboType = "NORMAL";
			order.Symbol = leg.Option!.Root;
			order.Side = leg.Action == LegAction.Buy ? "BUY" : "SELL";
			order.Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
			order.OptionStrategy = OptionStrategyEnum["Single"];
			order.Legs = new List<OrderLeg> { BuildOptionLeg(leg) };
			return body;
		}

		// Multi-leg combo (may include a stock leg for covered call / protective put / collar).
		body.ClientComboOrderId = GenerateClientOrderId();
		order.InstrumentType = "OPTION";
		order.ComboType = "COMBO";
		order.Symbol = optionLegs[0].Option!.Root;
		// Side for a combo is typically the net side; Webull expects BUY for net-debit, SELL for net-credit.
		// Convention: if --limit is negative (net debit), side=BUY; else side=SELL.
		order.Side = (p.LimitPrice ?? 0m) < 0m ? "BUY" : "SELL";
		order.OptionStrategy = OptionStrategyEnum.TryGetValue(strat, out var mapped)
			? mapped
			: throw new InvalidOperationException($"Unknown strategy '{strat}' — extend OptionStrategyEnum");
		order.Legs = new List<OrderLeg>();
		foreach (var leg in stockLegs) order.Legs.Add(BuildStockLeg(leg));
		foreach (var leg in optionLegs) order.Legs.Add(BuildOptionLeg(leg));
		return body;
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
		Symbol = leg.Symbol,
		Quantity = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
		StrikePrice = leg.Option!.Strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
		OptionExpireDate = leg.Option.ExpiryDate.ToString("yyyy-MM-dd"),
		OptionType = leg.Option.CallPut == "C" ? "CALL" : "PUT",
	};

	private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

	/// <summary>Serializes the body to compact JSON (no whitespace between keys and values).</summary>
	internal static string Serialize(OrderRequestBody body) =>
		JsonSerializer.Serialize(body, CompactJson);
}
