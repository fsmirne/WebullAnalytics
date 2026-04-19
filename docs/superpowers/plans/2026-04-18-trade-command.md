# `trade` Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `trade` command to WebullAnalytics that uses the Webull OpenAPI to preview, place, cancel, and inspect orders — including multi-leg option combos — against the sandbox or production environment.

**Architecture:** Eight new/modified files. Pure-function seams for signing, leg parsing, strategy classification, and request building; thin HTTP wrapper around `HttpClient`; Spectre.Console.Cli branch with three subcommands (`place`, `cancel`, `status`). Every mutating action prompts interactively — no `--yes`. Default behavior of `trade place` is preview-only; placing requires `--submit`.

**Tech Stack:** C# .NET 10, Spectre.Console.Cli 0.53.1, System.Text.Json, System.Security.Cryptography (HMACSHA1, MD5), System.Net.Http.

**Spec:** `docs/superpowers/specs/2026-04-18-trade-command-design.md`

---

## Ground rules for this plan

- **No test framework exists** in the repo. Verification is done via CLI invocations with expected output. Each implementation task has a verification step after the implementation.
- **`data/` is already gitignored** (verified: `.gitignore:494` contains `data/`). No `.gitignore` change is required. The spec mentions a `.gitignore` addition — that task is dropped here.
- **Every step is a single action** (2–5 minutes). Commit boundaries are explicit.
- **Follow existing code style**: tabs for indent, file-scoped namespace `namespace WebullAnalytics;`, `internal` by default for new types, `Models.cs` hosts small records, `*Command.cs` files host one Spectre command each.

---

## File structure (created in this plan)

| File | Purpose | Created in |
|---|---|---|
| `trade-config.example.json` | Example config with 3 sandbox accounts, committed at repo root. | Task 1 |
| `OpenApiSigner.cs` | Pure static helper: builds the seven `x-*` headers for a signed request. | Task 3 |
| `TradeConfig.cs` | Config record (`TradeConfigFile`, `TradeAccount`) and loader/resolver. | Task 2 |
| `TradeLegParser.cs` | Parses `--trades` strings in the new `ACTION:SYMBOL:QTY[@PRICE]` format. | Task 4 |
| `StrategyClassifier.cs` | Classifies `ParsedLeg[]` into a strategy kind; wraps existing logic in `ParsingHelpers`. | Task 5 |
| `OrderRequestBuilder.cs` | Builds the OpenAPI JSON payload from parsed legs + strategy + CLI flags. | Task 6 |
| `WebullOpenApiClient.cs` | HTTP wrapper for preview/place/cancel/list-open/detail; uses `OpenApiSigner`. | Task 7 |
| `TradeCommand.cs` | Spectre branch + subcommands (`place`, `cancel`, `status`) + shared setup. | Tasks 8–11 |

**Modified files:**

| File | Change | Task |
|---|---|---|
| `Program.cs` | Register the `trade` branch. | Task 8 |
| `README.md` | Add a `Trade Command` section and `trade-config.json` setup guide. | Task 12 |

---

## Task 1: Create `trade-config.example.json`

**Files:**
- Create: `trade-config.example.json` (repo root)

- [ ] **Step 1: Create the example config with the three Webull-published sandbox accounts.**

```json
{
	"defaultAccount": "test1",
	"accounts": [
		{
			"alias": "test1",
			"accountId": "J6HA4EBQRQFJD2J6NQH0F7M649",
			"appKey": "a88f2efed4dca02b9bc1a3cecbc35dba",
			"appSecret": "c2895b3526cc7c7588758351ddf425d6",
			"sandbox": true
		},
		{
			"alias": "test2",
			"accountId": "HBGQE8NM0CQG4Q34ABOM83HD09",
			"appKey": "6d9f1a0aa919a127697b567bb704369e",
			"appSecret": "adb8931f708ea3d57ec1486f10abf58c",
			"sandbox": true
		},
		{
			"alias": "test3",
			"accountId": "4BJITU00JUIVEDO5V3PRA5C5G8",
			"appKey": "eecbf4489f460ad2f7aecef37b267618",
			"appSecret": "8abf920a9cc3cb7af3ea5e9e03850692",
			"sandbox": true
		}
	]
}
```

- [ ] **Step 2: Verify the file parses as JSON.**

Run: `python3 -c "import json; json.load(open('trade-config.example.json'))"` from repo root.
Expected: exits 0 with no output.

- [ ] **Step 3: Commit.**

```bash
git add trade-config.example.json
git commit -m "Add example trade-config with Webull sandbox accounts"
```

---

## Task 2: `TradeConfig.cs` — model + loader + resolver

**Files:**
- Create: `TradeConfig.cs`

- [ ] **Step 1: Create the file with the record types and loader.**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics;

internal sealed class TradeAccount
{
	[JsonPropertyName("alias")] public string Alias { get; set; } = "";
	[JsonPropertyName("accountId")] public string AccountId { get; set; } = "";
	[JsonPropertyName("appKey")] public string AppKey { get; set; } = "";
	[JsonPropertyName("appSecret")] public string AppSecret { get; set; } = "";
	[JsonPropertyName("sandbox")] public bool Sandbox { get; set; } = true;

	public string BaseUrl => Sandbox
		? "https://us-openapi-alb.uat.webullbroker.com"
		: "https://api.webull.com";
}

internal sealed class TradeConfigFile
{
	[JsonPropertyName("defaultAccount")] public string? DefaultAccount { get; set; }
	[JsonPropertyName("accounts")] public List<TradeAccount> Accounts { get; set; } = new();
}

internal static class TradeConfig
{
	internal const string ConfigPath = "data/trade-config.json";

	/// <summary>Loads and parses the trade-config.json file. Returns null (with stderr message) if the file is missing or malformed.</summary>
	internal static TradeConfigFile? Load()
	{
		var path = Program.ResolvePath(ConfigPath);
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"Error: trade config not found at '{ConfigPath}'.");
			Console.Error.WriteLine($"  Run: cp trade-config.example.json {ConfigPath} and edit.");
			return null;
		}
		try
		{
			var config = JsonSerializer.Deserialize<TradeConfigFile>(File.ReadAllText(path));
			if (config == null || config.Accounts.Count == 0)
			{
				Console.Error.WriteLine("Error: trade-config.json must contain at least one account.");
				return null;
			}
			return config;
		}
		catch (JsonException ex)
		{
			Console.Error.WriteLine($"Error: failed to parse trade-config.json: {ex.Message}");
			return null;
		}
	}

	/// <summary>Resolves the account to use given the --account flag (which may be null/empty).
	/// Returns null (with stderr message) if resolution fails.</summary>
	internal static TradeAccount? Resolve(TradeConfigFile config, string? accountFlag)
	{
		var key = string.IsNullOrWhiteSpace(accountFlag) ? config.DefaultAccount : accountFlag;
		if (string.IsNullOrWhiteSpace(key))
		{
			Console.Error.WriteLine("Error: no --account flag and no 'defaultAccount' in trade-config.json.");
			return null;
		}
		var match = config.Accounts.FirstOrDefault(a =>
			string.Equals(a.Alias, key, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(a.AccountId, key, StringComparison.OrdinalIgnoreCase));
		if (match == null)
		{
			var aliases = string.Join(", ", config.Accounts.Select(a => a.Alias));
			Console.Error.WriteLine($"Error: account '{key}' not found. Valid aliases: {aliases}");
			return null;
		}
		return match;
	}
}
```

- [ ] **Step 2: Verify the project builds.**

Run: `dotnet build` from repo root.
Expected: build succeeds (0 errors, warnings acceptable).

- [ ] **Step 3: Commit.**

```bash
git add TradeConfig.cs
git commit -m "Add TradeConfig model, loader, and account resolver"
```

---

## Task 3: `OpenApiSigner.cs` — HMAC-SHA1 request signing

**Files:**
- Create: `OpenApiSigner.cs`

- [ ] **Step 1: Create the signer with canonical-string construction and HMAC-SHA1.**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace WebullAnalytics;

/// <summary>
/// Builds the seven x-* headers required by the Webull OpenAPI.
/// Pure: given the same inputs (including the injected timestamp and nonce), produces the same outputs.
/// </summary>
internal static class OpenApiSigner
{
	/// <summary>
	/// Canonical signing flow:
	///   1. str1 = all query params + signing headers (minus x-signature, x-version) sorted alphabetically,
	///      joined as key1=val1&key2=val2&...
	///   2. str2 = uppercase MD5 of the compact JSON body (if a body is present).
	///   3. str3 = path & str1              (no body)
	///            path & str1 & str2        (with body)
	///   4. encoded = Uri.EscapeDataString(str3)
	///   5. signature = base64(HMAC-SHA1(appSecret + "&", encoded))
	/// The App Secret is NEVER transmitted in any header.
	/// </summary>
	internal static Dictionary<string, string> SignRequest(
		string appKey,
		string appSecret,
		string path,
		IReadOnlyDictionary<string, string> queryParams,
		string? jsonBody)
		=> SignRequest(appKey, appSecret, path, queryParams, jsonBody, DateTime.UtcNow, Guid.NewGuid().ToString("N"));

	/// <summary>Test-friendly overload: timestamp and nonce injectable for deterministic output.</summary>
	internal static Dictionary<string, string> SignRequest(
		string appKey,
		string appSecret,
		string path,
		IReadOnlyDictionary<string, string> queryParams,
		string? jsonBody,
		DateTime timestampUtc,
		string nonce)
	{
		var timestamp = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");

		// Signing headers (exclude x-signature and x-version per spec).
		var signingHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["x-app-key"] = appKey,
			["x-timestamp"] = timestamp,
			["x-signature-algorithm"] = "HMAC-SHA1",
			["x-signature-nonce"] = nonce,
			["x-signature-version"] = "1.0",
		};

		// Merge query + headers, sort alphabetically, join.
		var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (k, v) in queryParams) merged[k] = v;
		foreach (var (k, v) in signingHeaders) merged[k] = v;
		var str1 = string.Join("&", merged.Select(kv => $"{kv.Key}={kv.Value}"));

		var str3 = string.IsNullOrEmpty(jsonBody)
			? $"{path}&{str1}"
			: $"{path}&{str1}&{UppercaseMd5(jsonBody!)}";

		var encoded = Uri.EscapeDataString(str3);

		var signingKey = Encoding.UTF8.GetBytes(appSecret + "&");
		using var hmac = new HMACSHA1(signingKey);
		var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(encoded)));

		return new Dictionary<string, string>
		{
			["x-app-key"] = appKey,
			["x-timestamp"] = timestamp,
			["x-signature"] = signature,
			["x-signature-algorithm"] = "HMAC-SHA1",
			["x-signature-version"] = "1.0",
			["x-signature-nonce"] = nonce,
			["x-version"] = "v2",
		};
	}

	private static string UppercaseMd5(string input)
	{
		using var md5 = MD5.Create();
		var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
		var sb = new StringBuilder(hash.Length * 2);
		foreach (var b in hash) sb.Append(b.ToString("X2"));
		return sb.ToString();
	}
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add OpenApiSigner.cs
git commit -m "Add OpenApiSigner for Webull HMAC-SHA1 request signing"
```

- [ ] **Step 4: Verification note.**

The signer cannot be bit-matched against Webull's Python reference until that reference is consulted and known-good inputs/outputs are captured. The first real verification happens end-to-end in Task 11 (preview call against the sandbox). If that call fails with a signature error, inspect the canonical string by temporarily logging it before HMAC and compare to what Python produces for the same inputs.

---

## Task 4: `TradeLegParser.cs` — parse `ACTION:SYMBOL:QTY[@PRICE]`

**Files:**
- Create: `TradeLegParser.cs`

- [ ] **Step 1: Create the parser.**

```csharp
using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// Represents a single leg after parsing the --trades syntax (ACTION:SYMBOL:QTY[@PRICE]).
/// Price is populated only for analyze-style inputs; trade command rejects any leg with a price.
/// </summary>
internal sealed record ParsedLeg(
	LegAction Action,
	string Symbol,
	int Quantity,
	OptionParsed? Option,
	decimal? Price,
	string? PriceKeyword);

internal enum LegAction { Buy, Sell }

internal static class TradeLegParser
{
	/// <summary>
	/// Parses a comma-separated list of legs.
	/// Each leg: ACTION:SYMBOL:QTY[@PRICE]
	///   ACTION = buy|sell (case-insensitive)
	///   SYMBOL = equity ticker or OCC option symbol
	///   QTY    = unsigned positive integer
	///   @PRICE = optional; decimal or one of BID|MID|ASK (case-insensitive)
	/// Throws FormatException with a readable message on any malformed input.
	/// </summary>
	internal static List<ParsedLeg> Parse(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
			throw new FormatException("Leg list is empty.");

		var results = new List<ParsedLeg>();
		var legs = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		for (int i = 0; i < legs.Length; i++)
		{
			try { results.Add(ParseLeg(legs[i])); }
			catch (FormatException ex) { throw new FormatException($"Leg {i + 1} ('{legs[i]}'): {ex.Message}"); }
		}
		return results;
	}

	private static ParsedLeg ParseLeg(string leg)
	{
		// Split off optional @PRICE first.
		string main; string? priceToken;
		var atIdx = leg.IndexOf('@');
		if (atIdx >= 0) { main = leg[..atIdx]; priceToken = leg[(atIdx + 1)..].Trim(); }
		else { main = leg; priceToken = null; }

		var parts = main.Split(':', StringSplitOptions.TrimEntries);
		if (parts.Length != 3)
			throw new FormatException("expected ACTION:SYMBOL:QTY");

		LegAction action = parts[0].ToLowerInvariant() switch
		{
			"buy" => LegAction.Buy,
			"sell" => LegAction.Sell,
			_ => throw new FormatException($"ACTION must be 'buy' or 'sell', got '{parts[0]}'")
		};

		var symbol = parts[1];
		if (string.IsNullOrWhiteSpace(symbol)) throw new FormatException("SYMBOL is empty");

		if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
			throw new FormatException($"QTY must be a positive integer, got '{parts[2]}'");

		var option = ParsingHelpers.ParseOptionSymbol(symbol); // null for equity

		decimal? price = null;
		string? keyword = null;
		if (priceToken != null)
		{
			var upper = priceToken.ToUpperInvariant();
			if (upper is "BID" or "MID" or "ASK") keyword = upper;
			else if (decimal.TryParse(priceToken, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) price = p;
			else throw new FormatException($"@PRICE must be decimal or BID|MID|ASK, got '{priceToken}'");
		}

		return new ParsedLeg(action, symbol, qty, option, price, keyword);
	}
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add TradeLegParser.cs
git commit -m "Add TradeLegParser for ACTION:SYMBOL:QTY[@PRICE] syntax"
```

---

## Task 5: `StrategyClassifier.cs` — classify legs into a strategy

**Background:** `ParsingHelpers.ClassifyStrategyKind(legCount, distinctExpiries, distinctStrikes, distinctCallPut)` already exists (`ParsingHelpers.cs:121`) and returns strings like "Vertical", "Calendar", "Diagonal", "IronCondor", "Butterfly", etc. It handles **option-only multi-leg** cases. We wrap it with a higher-level function that takes `ParsedLeg[]` and additionally recognizes: `Single`, `Stock`, `CoveredCall`, `ProtectivePut`, `Collar`.

**Files:**
- Create: `StrategyClassifier.cs`

- [ ] **Step 1: Create the classifier.**

```csharp
namespace WebullAnalytics;

internal static class StrategyClassifier
{
	/// <summary>
	/// Classifies a list of parsed legs into a strategy kind. Returns null if the legs
	/// do not form a recognizable strategy (caller should surface "pass --strategy explicitly").
	/// Possible values: "Stock", "Single", "Vertical", "Calendar", "Diagonal",
	/// "IronCondor", "IronButterfly", "Butterfly", "Condor", "Spread",
	/// "CoveredCall", "ProtectivePut", "Collar", "Straddle", "Strangle".
	/// </summary>
	internal static string? Classify(IReadOnlyList<ParsedLeg> legs)
	{
		if (legs.Count == 0) return null;

		var stockLegs = legs.Where(l => l.Option == null).ToList();
		var optionLegs = legs.Where(l => l.Option != null).ToList();

		// Equity-only cases.
		if (optionLegs.Count == 0)
		{
			if (stockLegs.Count == 1) return "Stock";
			return null;
		}

		// Single option.
		if (optionLegs.Count == 1 && stockLegs.Count == 0) return "Single";

		// All option legs must share a root.
		var roots = optionLegs.Select(l => l.Option!.Root).Distinct().ToList();
		if (roots.Count > 1) return null;

		// Stock + option combos (common brokerage strategies).
		if (stockLegs.Count == 1)
		{
			// Root must match the stock symbol.
			if (!string.Equals(stockLegs[0].Symbol, roots[0], StringComparison.OrdinalIgnoreCase))
				return null;

			var stockIsLong = stockLegs[0].Action == LegAction.Buy;

			if (optionLegs.Count == 1)
			{
				var o = optionLegs[0];
				var isShortCall = o.Action == LegAction.Sell && o.Option!.CallPut == "C";
				var isLongPut = o.Action == LegAction.Buy && o.Option!.CallPut == "P";
				if (stockIsLong && isShortCall) return "CoveredCall";
				if (stockIsLong && isLongPut) return "ProtectivePut";
				return null;
			}

			if (optionLegs.Count == 2 && stockIsLong)
			{
				var hasLongPut = optionLegs.Any(l => l.Action == LegAction.Buy && l.Option!.CallPut == "P");
				var hasShortCall = optionLegs.Any(l => l.Action == LegAction.Sell && l.Option!.CallPut == "C");
				if (hasLongPut && hasShortCall) return "Collar";
			}

			return null;
		}

		// Option-only multi-leg: delegate to existing classifier.
		if (stockLegs.Count == 0 && optionLegs.Count >= 2)
		{
			var distinctExpiries = optionLegs.Select(l => l.Option!.ExpiryDate).Distinct().Count();
			var distinctStrikes = optionLegs.Select(l => l.Option!.Strike).Distinct().Count();
			var distinctCallPut = optionLegs.Select(l => l.Option!.CallPut).Distinct().Count();

			// Straddle: 2 legs, same strike, same expiry, one call + one put.
			if (optionLegs.Count == 2 && distinctStrikes == 1 && distinctExpiries == 1 && distinctCallPut == 2)
				return "Straddle";

			// Strangle: 2 legs, different strikes, same expiry, one call + one put.
			if (optionLegs.Count == 2 && distinctStrikes == 2 && distinctExpiries == 1 && distinctCallPut == 2)
				return "Strangle";

			return ParsingHelpers.ClassifyStrategyKind(optionLegs.Count, distinctExpiries, distinctStrikes, distinctCallPut);
		}

		return null;
	}
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add StrategyClassifier.cs
git commit -m "Add StrategyClassifier for ParsedLeg-based strategy recognition"
```

---

## Task 6: `OrderRequestBuilder.cs` — build OpenAPI payload

**Files:**
- Create: `OrderRequestBuilder.cs`

- [ ] **Step 1: Create the builder with result type and construction logic.**

```csharp
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
		["CoveredCall"] = "COVERED_STOCK", // placeholder — verify with Webull; may be "COVERED_CALL"
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

	/// <summary>Serializes the body to compact JSON (no whitespace between keys and values).</summary>
	internal static string Serialize(OrderRequestBody body) =>
		JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add OrderRequestBuilder.cs
git commit -m "Add OrderRequestBuilder for OpenAPI order payloads"
```

---

## Task 7: `WebullOpenApiClient.cs` — HTTP wrapper

**Files:**
- Create: `WebullOpenApiClient.cs`

- [ ] **Step 1: Create the client with typed methods for preview, place, cancel, list-open, detail.**

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics;

internal sealed class WebullOpenApiException : Exception
{
	internal string? ErrorCode { get; }
	internal int HttpStatus { get; }
	internal WebullOpenApiException(string? errorCode, string message, int httpStatus) : base(message)
	{
		ErrorCode = errorCode;
		HttpStatus = httpStatus;
	}
}

internal sealed class WebullOpenApiClient : IDisposable
{
	private readonly HttpClient _http;
	private readonly TradeAccount _account;

	internal WebullOpenApiClient(TradeAccount account)
	{
		_account = account;
		_http = new HttpClient { BaseAddress = new Uri(account.BaseUrl) };
	}

	public void Dispose() => _http.Dispose();

	// ─── Preview / Place ──────────────────────────────────────────────────────

	internal sealed record PreviewResult(
		[property: JsonPropertyName("estimated_cost")] string? EstimatedCost,
		[property: JsonPropertyName("estimated_transaction_fee")] string? EstimatedTransactionFee);

	internal sealed record PlaceResult(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("client_combo_order_id")] string? ClientComboOrderId,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId);

	internal async Task<PreviewResult> PreviewOrderAsync(OrderRequestBody body, CancellationToken ct = default) =>
		await PostAsync<PreviewResult>("/openapi/trade/order/preview", body, ct);

	internal async Task<PlaceResult> PlaceOrderAsync(OrderRequestBody body, CancellationToken ct = default) =>
		await PostAsync<PlaceResult>("/openapi/trade/order/place", body, ct);

	// ─── Cancel ───────────────────────────────────────────────────────────────

	private sealed class CancelRequest
	{
		[JsonPropertyName("account_id")] public string AccountId { get; set; } = "";
		[JsonPropertyName("client_order_id")] public string ClientOrderId { get; set; } = "";
	}

	internal sealed record CancelResult(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId);

	internal async Task<CancelResult> CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
		await PostAsync<CancelResult>("/openapi/trade/order/cancel",
			new CancelRequest { AccountId = _account.AccountId, ClientOrderId = clientOrderId }, ct);

	// ─── List open orders ─────────────────────────────────────────────────────

	internal sealed record OpenOrder(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId,
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("side")] string? Side,
		[property: JsonPropertyName("status")] string? Status,
		[property: JsonPropertyName("total_quantity")] string? TotalQuantity,
		[property: JsonPropertyName("filled_quantity")] string? FilledQuantity,
		[property: JsonPropertyName("order_type")] string? OrderType,
		[property: JsonPropertyName("combo_type")] string? ComboType);

	/// <summary>Iterates pages until the server returns fewer than page_size results.</summary>
	internal async Task<List<OpenOrder>> ListOpenOrdersAsync(CancellationToken ct = default)
	{
		const int pageSize = 100;
		var all = new List<OpenOrder>();
		string? cursor = null;
		while (true)
		{
			var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
			{
				["account_id"] = _account.AccountId,
				["page_size"] = pageSize.ToString(),
			};
			if (cursor != null) query["last_client_order_id"] = cursor;
			var page = await GetAsync<List<OpenOrder>>("/openapi/trade/order/open", query, ct);
			if (page.Count == 0) break;
			all.AddRange(page);
			if (page.Count < pageSize) break;
			cursor = page[^1].ClientOrderId;
			if (string.IsNullOrEmpty(cursor)) break;
		}
		return all;
	}

	// ─── Order detail ─────────────────────────────────────────────────────────

	internal sealed record OrderDetailLeg(
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("side")] string? Side,
		[property: JsonPropertyName("quantity")] string? Quantity,
		[property: JsonPropertyName("option_type")] string? OptionType,
		[property: JsonPropertyName("strike_price")] string? StrikePrice,
		[property: JsonPropertyName("option_expire_date")] string? OptionExpireDate);

	internal sealed record OrderDetailOrder(
		[property: JsonPropertyName("client_order_id")] string? ClientOrderId,
		[property: JsonPropertyName("order_id")] string? OrderId,
		[property: JsonPropertyName("status")] string? Status,
		[property: JsonPropertyName("symbol")] string? Symbol,
		[property: JsonPropertyName("side")] string? Side,
		[property: JsonPropertyName("total_quantity")] string? TotalQuantity,
		[property: JsonPropertyName("filled_quantity")] string? FilledQuantity,
		[property: JsonPropertyName("filled_price")] string? FilledPrice,
		[property: JsonPropertyName("place_time")] string? PlaceTime,
		[property: JsonPropertyName("filled_time")] string? FilledTime,
		[property: JsonPropertyName("position_intent")] string? PositionIntent,
		[property: JsonPropertyName("legs")] List<OrderDetailLeg>? Legs);

	internal sealed record OrderDetail(
		[property: JsonPropertyName("combo_type")] string? ComboType,
		[property: JsonPropertyName("combo_order_id")] string? ComboOrderId,
		[property: JsonPropertyName("orders")] List<OrderDetailOrder>? Orders);

	internal async Task<OrderDetail> GetOrderAsync(string clientOrderId, CancellationToken ct = default) =>
		await GetAsync<OrderDetail>("/openapi/trade/order/detail",
			new SortedDictionary<string, string>(StringComparer.Ordinal)
			{
				["account_id"] = _account.AccountId,
				["client_order_id"] = clientOrderId,
			}, ct);

	// ─── Transport ────────────────────────────────────────────────────────────

	private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
	{
		var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, path, new Dictionary<string, string>(), json);
		using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
		using var resp = await _http.SendAsync(req, ct);
		return await Read<T>(resp);
	}

	private async Task<T> GetAsync<T>(string path, IReadOnlyDictionary<string, string> query, CancellationToken ct)
	{
		var headers = OpenApiSigner.SignRequest(_account.AppKey, _account.AppSecret, path, query, null);
		var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
		var uri = string.IsNullOrEmpty(qs) ? path : $"{path}?{qs}";
		using var req = new HttpRequestMessage(HttpMethod.Get, uri);
		foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
		using var resp = await _http.SendAsync(req, ct);
		return await Read<T>(resp);
	}

	private static async Task<T> Read<T>(HttpResponseMessage resp)
	{
		var body = await resp.Content.ReadAsStringAsync();
		if ((int)resp.StatusCode >= 500)
			throw new WebullOpenApiException(null, $"Webull API unavailable (HTTP {(int)resp.StatusCode}): {Truncate(body, 200)}", (int)resp.StatusCode);
		if ((int)resp.StatusCode >= 400)
		{
			try
			{
				using var doc = JsonDocument.Parse(body);
				var code = doc.RootElement.TryGetProperty("error_code", out var c) ? c.GetString() : null;
				var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
				throw new WebullOpenApiException(code, msg, (int)resp.StatusCode);
			}
			catch (JsonException)
			{
				throw new WebullOpenApiException(null, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}", (int)resp.StatusCode);
			}
		}
		var result = JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
		if (result == null) throw new WebullOpenApiException(null, "Empty response body", (int)resp.StatusCode);
		return result;
	}

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add WebullOpenApiClient.cs
git commit -m "Add WebullOpenApiClient for preview/place/cancel/list/detail"
```

---

## Task 8: `TradeCommand.cs` — branch + shared setup + `place` subcommand

**Files:**
- Create: `TradeCommand.cs`
- Modify: `Program.cs:38-42` (register the trade branch)

- [ ] **Step 1: Create TradeCommand.cs with a branch stub, shared helpers, and the `place` subcommand.**

```csharp
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
		AnsiConsole.Markup($"{prompt} [y/N] ");
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
	public override async Task<int> ExecuteAsync(CommandContext context, TradePlaceSettings s)
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
		if (type == "limit" && !decimal.TryParse(s.Limit, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLimit))
		{ AnsiConsole.MarkupLine($"[red]Error:[/] --limit '{s.Limit}' is not a valid decimal."); return 2; }
		else if (type == "limit") limit = decimal.Parse(s.Limit!, NumberStyles.Any, CultureInfo.InvariantCulture);

		// 3. Resolve strategy.
		var strategy = s.Strategy != null ? NormalizeStrategyFlag(s.Strategy) : StrategyClassifier.Classify(legs);
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
```

- [ ] **Step 2: Register the branch in `Program.cs:38-42`.**

Change:
```csharp
		config.AddCommand<ReportCommand>("report");
		config.AddCommand<AnalyzeCommand>("analyze");
		config.AddCommand<FetchCommand>("fetch");
		config.AddCommand<SniffCommand>("sniff");
```
To:
```csharp
		config.AddCommand<ReportCommand>("report");
		config.AddCommand<AnalyzeCommand>("analyze");
		config.AddCommand<FetchCommand>("fetch");
		config.AddCommand<SniffCommand>("sniff");
		config.AddBranch("trade", trade =>
		{
			trade.AddCommand<TradePlaceCommand>("place");
			// trade cancel and status commands registered in Tasks 9–10.
		});
```

- [ ] **Step 3: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 4: Verify the branch is registered by invoking help.**

Run: `dotnet run -- trade --help`
Expected: output lists `place` as a subcommand.

- [ ] **Step 5: Verify parse-only validation errors fire before any network call.**

Set up a dummy config:
```bash
cp trade-config.example.json data/trade-config.json
```

Run: `dotnet run -- trade place --trades "" --limit 1`
Expected: `Error parsing --trades: Leg list is empty.` exits 2.

Run: `dotnet run -- trade place --trades "buy:SPY:10"`
Expected: `Error: --limit is required with --type limit.` exits 2.

Run: `dotnet run -- trade place --trades "buy:SPY:10,sell:GME:10" --type market`
Expected: `Error: multi-leg combo orders must be limit.` exits 2.

- [ ] **Step 6: Commit.**

```bash
git add TradeCommand.cs Program.cs
git commit -m "Add trade branch command with place subcommand"
```

---

## Task 9: `trade cancel` subcommand (single + `--all`)

**Files:**
- Modify: `TradeCommand.cs` (append new settings + command)
- Modify: `Program.cs:38-47` (register the cancel subcommand)

- [ ] **Step 1: Append the cancel settings and command to `TradeCommand.cs`.**

Add to the bottom of `TradeCommand.cs`:

```csharp
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
	public override async Task<int> ExecuteAsync(CommandContext context, TradeCancelSettings s)
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
			try { orders = await client.ListOpenOrdersAsync(); }
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
					await client.CancelOrderAsync(o.ClientOrderId);
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
			var result = await client.CancelOrderAsync(s.ClientOrderId!);
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
```

- [ ] **Step 2: Register the subcommand.**

In `Program.cs`, update the branch block:
```csharp
		config.AddBranch("trade", trade =>
		{
			trade.AddCommand<TradePlaceCommand>("place");
			trade.AddCommand<TradeCancelCommand>("cancel");
		});
```

- [ ] **Step 3: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 4: Verify argument-validation errors fire before any network call.**

Run: `dotnet run -- trade cancel`
Expected: `Error: pass a client order ID, or --all.` exits 2.

Run: `dotnet run -- trade cancel abc123 --all`
Expected: `Error: pass either <clientOrderId> or --all, not both.` exits 2.

- [ ] **Step 5: Commit.**

```bash
git add TradeCommand.cs Program.cs
git commit -m "Add trade cancel subcommand (single + --all)"
```

---

## Task 10: `trade status` subcommand

**Files:**
- Modify: `TradeCommand.cs` (append)
- Modify: `Program.cs` (register)

- [ ] **Step 1: Append the status settings and command to `TradeCommand.cs`.**

```csharp
// ─── `trade status` ───────────────────────────────────────────────────────────

internal sealed class TradeStatusSettings : TradeSubcommandSettings
{
	[CommandArgument(0, "<clientOrderId>")]
	[Description("Client order ID to look up.")]
	public string ClientOrderId { get; set; } = "";
}

internal sealed class TradeStatusCommand : AsyncCommand<TradeStatusSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TradeStatusSettings s)
	{
		var account = TradeContext.ResolveOrExit(s.Account, quietBanner: false);
		if (account == null) return 2;

		using var client = new WebullOpenApiClient(account);
		WebullOpenApiClient.OrderDetail detail;
		try { detail = await client.GetOrderAsync(s.ClientOrderId); }
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
```

- [ ] **Step 2: Register the subcommand.**

In `Program.cs`, the branch block becomes:
```csharp
		config.AddBranch("trade", trade =>
		{
			trade.AddCommand<TradePlaceCommand>("place");
			trade.AddCommand<TradeCancelCommand>("cancel");
			trade.AddCommand<TradeStatusCommand>("status");
		});
```

- [ ] **Step 3: Build.**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 4: Verify help text.**

Run: `dotnet run -- trade --help`
Expected: output lists `place`, `cancel`, `status`.

- [ ] **Step 5: Commit.**

```bash
git add TradeCommand.cs Program.cs
git commit -m "Add trade status subcommand"
```

---

## Task 11: Sandbox end-to-end verification

This task is **not code** — it runs the commands against the Webull sandbox to confirm the signer, request builder, and command wiring all work against a live server. Halt and investigate on any failure.

**Prerequisites:**
- `data/trade-config.json` exists with at least one sandbox account (copied from `trade-config.example.json`).

- [ ] **Step 1: Sanity check — preview a single equity limit buy.**

Run: `dotnet run -- trade place --trades "buy:SPY:1" --limit 1.00`
Expected:
- `[SANDBOX]` banner in green.
- Client order ID printed.
- Preview response with `estimated_cost` and `estimated_transaction_fee`.
- Exits 0.

If the preview call returns a signature error, this is where to debug. Inspect the canonical string by temporarily adding `Console.Error.WriteLine(str3)` before `Uri.EscapeDataString(str3)` in `OpenApiSigner.SignRequest`, compare against a Python reference with the same inputs, and fix the divergence. Remove the log before continuing. **Commit any bugfix separately** so it's easy to find later.

- [ ] **Step 2: Preview a vertical spread.**

Pick a currently-tradable option chain. Example (substitute a real OCC symbol):
```
dotnet run -- trade place --trades "buy:SPY260516C00580000:1,sell:SPY260516C00590000:1" --limit -2.00
```
Expected: preview returns without error, with `option_strategy: VERTICAL` implied. If Webull rejects `"VERTICAL"` as the enum value, update `OrderRequestBuilder.OptionStrategyEnum` to match whatever enum string Webull's docs or error message indicates.

- [ ] **Step 3: Preview a calendar.**

Example (same root, two expirations):
```
dotnet run -- trade place --trades "sell:SPY260410C00580000:1,buy:SPY260516C00580000:1" --limit -0.50
```
Expected: preview returns without error.

- [ ] **Step 4: Preview a covered call.**

```
dotnet run -- trade place --trades "buy:SPY:100,sell:SPY260516C00600000:1" --limit -580.00
```
Expected: preview returns without error. If the strategy enum name differs from `"COVERED_STOCK"`, update the map.

- [ ] **Step 5: Place an order with `--submit` and confirm it shows up in `trade status`.**

```
dotnet run -- trade place --trades "buy:SPY:1" --limit 1.00 --submit
```
Expected: prompts → confirm `y` → `Placed.` with order ID and client order ID printed.

Then:
```
dotnet run -- trade status <clientOrderId-from-place>
```
Expected: shows the order, symbol, side, quantity, status.

- [ ] **Step 6: Cancel the order.**

```
dotnet run -- trade cancel <clientOrderId-from-Step-5>
```
Expected: prompts → confirm `y` → `Cancelled.`

- [ ] **Step 7: `cancel --all` smoke.**

Place 2–3 very-low-price orders that will not fill, then:
```
dotnet run -- trade cancel --all
```
Expected: lists the orders, prompts, cancels each, prints summary. If an `error_code` indicates a specific order is not cancellable (already filled, for example), that one is reported as failed and the others still succeed.

- [ ] **Step 8: Document any enum-map discrepancies.**

If `OrderRequestBuilder.OptionStrategyEnum` needed any adjustments during Steps 2–4, commit those changes:
```bash
git add OrderRequestBuilder.cs
git commit -m "Correct Webull option_strategy enum values from sandbox verification"
```

---

## Task 12: README update

**Files:**
- Modify: `README.md` (add a new top-level section)

- [ ] **Step 1: Add a `Trade Command` section to `README.md`, after the existing `Sniff Command` section and before `Data Sources`.**

Insert the following block. Keep the formatting consistent with surrounding sections (same heading levels, same use of code fences).

```markdown
### Trade Command

The `trade` command places orders via the Webull OpenAPI. It supports single-leg equity orders, single-leg option orders, and multi-leg option strategies (including stock+option combos like covered calls). Unlike `fetch` — which uses Webull's session-based web API — `trade` uses the OpenAPI with App Key + App Secret authentication.

Every `trade place` invocation runs a preview against the broker by default. The order is only submitted when you pass `--submit`, and every mutating action (`place --submit`, `cancel`, `cancel --all`) prompts interactively before sending.

#### Setup

Copy the example config and fill in your own account(s):

```bash
cp trade-config.example.json data/trade-config.json
```

The example ships with the three sandbox test accounts Webull publishes in its OpenAPI documentation. For a production account, add a new entry with `sandbox: false` and edit `defaultAccount` to point at it.

#### Commands

```bash
# Preview a single equity limit buy (no order is placed).
WebullAnalytics trade place --trades "buy:SPY:10" --limit 580

# Place the same order.
WebullAnalytics trade place --trades "buy:SPY:10" --limit 580 --submit

# Preview a vertical call spread for 1 contract, net debit $0.75.
WebullAnalytics trade place --trades "buy:SPY260516C00580000:1,sell:SPY260516C00590000:1" --limit -0.75

# Calendar roll — sell near, buy far.
WebullAnalytics trade place --trades "sell:GME260410C00023000:1,buy:GME260417C00023000:1" --limit -0.20

# Covered call — long 100 shares + short 1 call.
WebullAnalytics trade place --trades "buy:GME:100,sell:GME260501C00025000:1" --limit -23.50

# Market order, single equity.
WebullAnalytics trade place --trades "buy:SPY:10" --type market --submit

# Cancel a single order.
WebullAnalytics trade cancel <clientOrderId>

# Cancel every open order for the account.
WebullAnalytics trade cancel --all

# Check an order's status.
WebullAnalytics trade status <clientOrderId>

# Use a non-default account.
WebullAnalytics trade place --trades "buy:SPY:1" --limit 1 --account test2
```

#### `--trades` syntax

Format: `ACTION:SYMBOL:QTY`, comma-separated for multiple legs.

- `ACTION` — `buy` or `sell` (explicit, no sign math).
- `SYMBOL` — equity ticker (e.g. `GME`) or OCC option symbol (e.g. `GME260501C00023000`).
- `QTY` — positive integer.

Per-leg prices (`@PRICE`) are **not** allowed in `trade` — combo orders use a single `--limit` for the net price across all legs. Positive `--limit` means net credit; negative means net debit.

#### Options

```
Options (place):
  --trades <legs>           Comma-separated legs in ACTION:SYMBOL:QTY format (required).
  --limit <net>             Net limit price. Required for --type limit. Positive = credit; negative = debit.
  --type <type>             limit or market. Default: limit. Market is rejected for multi-leg orders.
  --tif <tif>               Time-in-force: day or gtc. Default: day.
  --strategy <name>         Override auto-detected strategy. Values: single, stock, vertical, calendar,
                            diagonal, iron_condor, iron_butterfly, butterfly, condor, straddle, strangle,
                            covered_call, protective_put, collar.
  --account <id-or-alias>   Pick an account from trade-config.json. Defaults to defaultAccount.
  --submit                  Actually place the order. Without this, runs preview only.

Options (cancel):
  <clientOrderId>           Client order ID of the order to cancel.
  --all                     Cancel every open order for the account.
  --account <id-or-alias>   Pick a non-default account.

Options (status):
  <clientOrderId>           Client order ID to look up.
  --account <id-or-alias>   Pick a non-default account.
```

#### Sandbox vs production

Each account in `trade-config.json` has a `sandbox: true|false` flag. Sandbox accounts hit `https://us-openapi-alb.uat.webullbroker.com`; production accounts hit `https://api.webull.com`. A colored banner (green `[SANDBOX]` / red `[PRODUCTION]`) is printed at the top of every `trade` invocation so you always know which environment you are in.

There is no `--yes` flag — every place, cancel, and cancel-all prompts interactively. Piped empty input aborts.
```

- [ ] **Step 2: Update the single-sentence summary of commands near the top of the README (the line that says `WebullAnalytics has four commands: report, analyze, fetch, sniff`).**

Find and replace in `README.md`:
- Old: `WebullAnalytics has four commands: \`report\` (generate a P&L report), \`analyze\` (hypothetical what-if analysis), \`fetch\` (download order data from the Webull API), and \`sniff\` (automatically capture fresh API session headers).`
- New: `WebullAnalytics has five commands: \`report\` (generate a P&L report), \`analyze\` (hypothetical what-if analysis), \`fetch\` (download order data from the Webull API), \`sniff\` (automatically capture fresh API session headers), and \`trade\` (place, cancel, and inspect orders via the Webull OpenAPI).`

- [ ] **Step 3: Commit.**

```bash
git add README.md
git commit -m "Document trade command in README"
```

---

## Self-review checklist (to be run by the implementing agent after Task 12)

- [ ] **Spec coverage:** Every goal in `docs/superpowers/specs/2026-04-18-trade-command-design.md` is addressed:
	- Place single-leg equity (Task 6, Task 8) ✓
	- Place single-leg option (Task 6, Task 8) ✓
	- Place multi-leg option combo (Task 6, Task 8) ✓
	- Place stock+option combo (Task 6 covered-call path, Task 5 classifier) ✓
	- Cancel by client order ID (Task 9) ✓
	- Cancel --all (Task 9) ✓
	- Order status lookup (Task 10) ✓
	- Preview by default, place on --submit (Task 8) ✓
	- Sandbox/prod banner (Task 8, `TradeContext.PrintBanner`) ✓
	- Per-account sandbox flag routing to different base URLs (Task 2, `TradeAccount.BaseUrl`) ✓
	- HMAC-SHA1 signing per spec (Task 3) ✓
	- Config file in `data/trade-config.json`, example at repo root (Tasks 1–2) ✓
	- App Secret never transmitted in a header (Task 3) ✓
	- `--trades` syntax rejecting `@PRICE` (Task 4 parser populates fields; Task 8 rejects) ✓
	- `--type market` with multi-leg → error (Task 8) ✓
	- `--limit` required for limit, rejected for market (Task 8) ✓
	- No `--yes` flag; every mutating action prompts (Tasks 8–9) ✓
- [ ] **Placeholder scan:** No `TBD`, `TODO`, `fill in`, `similar to Task N`, or vague validation phrases in any task. (One legitimate comment in Task 6 flags that enum strings need sandbox verification — this is expected to be resolved in Task 11 Step 8 and is not a plan placeholder.)
- [ ] **Type consistency:**
	- `ParsedLeg` (Task 4) — referenced by `StrategyClassifier` (Task 5) and `OrderRequestBuilder` (Task 6). ✓
	- `TradeAccount` / `TradeConfigFile` (Task 2) — referenced by `TradeContext` (Task 8) and `WebullOpenApiClient` (Task 7). ✓
	- `OrderRequestBody` / `NewOrder` / `OrderLeg` (Task 6) — referenced by `WebullOpenApiClient` (Task 7) as `object`-typed body. ✓
	- `WebullOpenApiClient.PreviewResult`, `PlaceResult`, `CancelResult`, `OpenOrder`, `OrderDetail*` (Task 7) — referenced by commands (Tasks 8–10). ✓
	- `LegAction` (Task 4) enum values `Buy`/`Sell` — used consistently in Tasks 5, 6, 8. ✓
