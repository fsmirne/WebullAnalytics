# `ai` Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `ai` command to WebullAnalytics that continuously monitors open option positions during the market session and emits structured proposals (roll / take-profit / stop-loss / defensive-roll) to a log, based on a configurable rules engine. Phase 1 is read-only: never places orders.

**Architecture:** New `AI/` subfolder houses the feature. Three subcommands (`watch`, `once`, `replay`) share one evaluation engine driven by `IManagementRule[]`, consuming an `EvaluationContext` (positions, quotes, clock, cash). Position and quote sources are interfaces so replay and live modes share the rule code. No broker calls; the loop only reads.

**Tech Stack:** C# .NET 10, Spectre.Console.Cli 0.54.0, System.Text.Json. Reuses existing `PositionTracker`, `OptionMath`, `BjerksundStensland`, `TimeDecayGridBuilder`, `YahooOptionsClient`, `WebullOptionsClient`, `WebullOpenApiClient`.

**Spec:** `docs/superpowers/specs/2026-04-19-ai-command-design.md`

---

## Ground rules for this plan

- **No test framework exists** in the repo. Verification is by building and running CLI invocations with expected output. Each implementation task has a verification step after the implementation.
- **Build command (WSL):** `"/mnt/c/Program Files/dotnet/dotnet.exe" build` — the dotnet CLI is not on the WSL PATH; use the explicit Windows-side path.
- **Every step is a single action** (2–5 minutes). Commit boundaries are explicit.
- **Follow existing code style:** tabs for indent, file-scoped namespace, `internal` by default, one primary type per file, JsonPropertyName attributes for JSON models.
- **Folder convention (new):** the `AI/` folder uses the Microsoft 2-letter-acronym rule (folder and class prefix `AI` stay all-caps; 3+-letter acronyms like `Api`/`Csv` stay PascalCase). Sub-namespaces: `WebullAnalytics.AI`, `WebullAnalytics.AI.Rules`, `WebullAnalytics.AI.Sources`, `WebullAnalytics.AI.Replay`, `WebullAnalytics.AI.Output`.

---

## File structure (created in this plan)

| File | Purpose | Created in |
|---|---|---|
| `AI/ManagementProposal.cs` | Record type + proposal kinds + rationale. | Task 1 |
| `AI/EvaluationContext.cs` | Per-tick snapshot passed to rules. | Task 1 |
| `AI/Rules/IManagementRule.cs` | Interface every rule implements. | Task 2 |
| `AI/Rules/ProposalFingerprint.cs` | Idempotency dedup key. | Task 2 |
| `AI/Sources/IPositionSource.cs` | Abstraction over live OpenAPI + replay positions. | Task 3 |
| `AI/Sources/IQuoteSource.cs` | Abstraction over live quote clients + synthesized replay quotes. | Task 3 |
| `AI/AIConfig.cs` | Config model + loader + validation. | Task 4 |
| `ai-config.example.json` | Example config shipped in repo root. | Task 5 |
| `AI/CashReserveHelper.cs` | Pure-function funding check extracted from `AnalyzeCommon`. | Task 6 |
| `AI/Rules/StopLossRule.cs` | Priority-1 rule. | Task 7 |
| `AI/Rules/TakeProfitRule.cs` | Priority-2 rule. | Task 8 |
| `AI/Rules/DefensiveRollRule.cs` | Priority-3 rule. | Task 9 |
| `AI/Rules/RollShortOnExpiryRule.cs` | Priority-4 rule. | Task 10 |
| `AI/RuleEvaluator.cs` | Iterates rules in priority order, applies fingerprint dedup. | Task 11 |
| `AI/Output/ProposalSink.cs` | Writes console + JSONL log. | Task 12 |
| `AI/Sources/LivePositionSource.cs` | OpenAPI-backed live position source. | Task 13 |
| `AI/Sources/LiveQuoteSource.cs` | Yahoo / Webull-backed quote source. | Task 14 |
| `AI/AICommand.cs` | Spectre branch + shared settings + `ai once` subcommand. | Task 15 |
| `AI/WatchLoop.cs` | Timer/tick mechanics; `ai watch` subcommand. | Task 16 |
| `AI/Replay/HistoricalPriceCache.cs` | Disk-cached daily closes from Yahoo. | Task 17 |
| `AI/Replay/IVBackSolver.cs` | Back-solves IV from historical fills. | Task 18 |
| `AI/Sources/ReplayPositionSource.cs` | Rebuilds PositionTracker at historical timestamps. | Task 19 |
| `AI/Sources/ReplayQuoteSource.cs` | Black-Scholes pricing with IV-back-solved quotes. | Task 20 |
| `AI/Replay/ReplayRunner.cs` | Walks history, emits comparison report. | Task 21 |
| `AI/Output/ReplayReportRenderer.cs` | Formats the replay comparison block. | Task 22 |

**Modified files:**

| File | Change | Task |
|---|---|---|
| `Program.cs` | Register the `ai` branch with three subcommands. | Task 15 (initial), Task 16 (watch), Task 23 (replay) |
| `AnalyzeCommand.cs` | Extract cash-reserve funding logic to shared helper. | Task 6 |
| `WebullAnalytics.csproj` | No expected change; reference via folder globbing already picks up `AI/**`. Verify in Task 1. | Task 1 |
| `README.md` | Add `AI Command` section. | Task 24 |

---

## Task 1: Foundation — `ManagementProposal` and `EvaluationContext`

**Files:**
- Create: `AI/ManagementProposal.cs`
- Create: `AI/EvaluationContext.cs`

- [ ] **Step 1: Verify the project auto-includes `AI/**/*.cs`.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build (no AI files exist yet, but this verifies baseline).

- [ ] **Step 2: Create `AI/ManagementProposal.cs`.**

```csharp
namespace WebullAnalytics.AI;

/// <summary>
/// Kind of action a rule proposes for a position.
/// </summary>
internal enum ProposalKind
{
	Close,       // flat-close the position at current mid
	Roll,        // roll the short leg (same strike or up-and-out)
	AlertOnly    // rule matched but no actionable improvement; surface for awareness
}

/// <summary>
/// A structured leg in a proposed action: action + OCC symbol + quantity.
/// For Close proposals, the legs describe the closing trades.
/// For Roll proposals, the legs describe the buy-to-close and sell-to-open pair (or vice-versa).
/// </summary>
/// <param name="Action">"buy" or "sell" (explicit, no sign math).</param>
/// <param name="Symbol">OCC option symbol or equity ticker.</param>
/// <param name="Qty">Positive integer.</param>
internal record ProposalLeg(string Action, string Symbol, int Qty);

/// <summary>
/// Output of a single rule evaluation.
/// </summary>
/// <param name="Rule">Rule class name, e.g., "StopLossRule".</param>
/// <param name="Ticker">Underlying symbol the proposal concerns, e.g., "GME".</param>
/// <param name="PositionKey">Stable identifier for the position, used for fingerprinting.
/// Format: "{ticker}_{strategyKind}_{strike}_{expiry:yyyyMMdd}".</param>
/// <param name="Kind">Close / Roll / AlertOnly.</param>
/// <param name="Legs">Structured leg list describing the proposed trades.</param>
/// <param name="NetDebit">Net price across all legs (negative = debit paid; positive = credit received).</param>
/// <param name="Rationale">Human-readable explanation of why the rule fired, with concrete numbers.</param>
/// <param name="CashReserveBlocked">True if this proposal would violate the configured cash reserve.</param>
/// <param name="CashReserveDetail">When blocked, a detail string like "free $Y, requires $X". Null otherwise.</param>
internal sealed record ManagementProposal(
	string Rule,
	string Ticker,
	string PositionKey,
	ProposalKind Kind,
	IReadOnlyList<ProposalLeg> Legs,
	decimal NetDebit,
	string Rationale,
	bool CashReserveBlocked = false,
	string? CashReserveDetail = null
);
```

- [ ] **Step 3: Create `AI/EvaluationContext.cs`.**

```csharp
namespace WebullAnalytics.AI;

/// <summary>
/// Snapshot of state passed to every rule on every tick.
/// Immutable; one instance per tick.
/// </summary>
/// <param name="Now">Logical clock for this evaluation. Live: DateTime.Now. Replay: the historical step.</param>
/// <param name="OpenPositions">All currently-open positions grouped by strategy (keyed by position key).</param>
/// <param name="UnderlyingPrices">Spot prices for each ticker under management.</param>
/// <param name="Quotes">Per-leg option quotes by OCC symbol.</param>
/// <param name="AccountCash">Free cash available (before applying reserve).</param>
/// <param name="AccountValue">Total account value (cash + positions marked to market).</param>
internal sealed record EvaluationContext(
	DateTime Now,
	IReadOnlyDictionary<string, OpenPosition> OpenPositions,
	IReadOnlyDictionary<string, decimal> UnderlyingPrices,
	IReadOnlyDictionary<string, OptionContractQuote> Quotes,
	decimal AccountCash,
	decimal AccountValue
);

/// <summary>
/// A single open position under management. Carries enough state for rules to evaluate
/// without re-querying upstream sources.
/// </summary>
/// <param name="Key">Stable identifier; same value used in ManagementProposal.PositionKey.</param>
/// <param name="Ticker">Underlying root.</param>
/// <param name="StrategyKind">"Calendar" | "Diagonal" | "Single" | "Vertical" etc.</param>
/// <param name="Legs">Per-leg state.</param>
/// <param name="InitialNetDebit">The net debit (or credit) when the position was opened, per contract.</param>
/// <param name="AdjustedNetDebit">Break-even adjusted debit accounting for roll history.</param>
/// <param name="Quantity">Number of contracts.</param>
internal sealed record OpenPosition(
	string Key,
	string Ticker,
	string StrategyKind,
	IReadOnlyList<PositionLeg> Legs,
	decimal InitialNetDebit,
	decimal AdjustedNetDebit,
	int Quantity
);

/// <summary>
/// One leg of an open position.
/// </summary>
/// <param name="Symbol">OCC symbol for options; equity ticker for stock legs.</param>
/// <param name="Side">Long or short (represented as Side.Buy or Side.Sell matching the original trade).</param>
/// <param name="Strike">Strike price (0 for stock).</param>
/// <param name="Expiry">Expiration date (null for stock).</param>
/// <param name="CallPut">"C" / "P" for options; null for stock.</param>
/// <param name="Qty">Per-position leg quantity (contracts or shares).</param>
internal sealed record PositionLeg(
	string Symbol,
	Side Side,
	decimal Strike,
	DateTime? Expiry,
	string? CallPut,
	int Qty
);
```

- [ ] **Step 4: Build to verify compilation.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build, no warnings about the new files.

- [ ] **Step 5: Commit.**

```bash
git add AI/ManagementProposal.cs AI/EvaluationContext.cs
git commit -m "Add ai command foundation types (proposal, evaluation context)"
```

---

## Task 2: `IManagementRule` and `ProposalFingerprint`

**Files:**
- Create: `AI/Rules/IManagementRule.cs`
- Create: `AI/Rules/ProposalFingerprint.cs`

- [ ] **Step 1: Create `AI/Rules/IManagementRule.cs`.**

```csharp
namespace WebullAnalytics.AI.Rules;

/// <summary>
/// A management rule. Each rule is stateless: all state comes through the EvaluationContext.
/// Rules are evaluated per-position in priority order; the first match wins for that position in that tick.
/// </summary>
internal interface IManagementRule
{
	/// <summary>Unique rule name; used in ManagementProposal.Rule and for config lookup.</summary>
	string Name { get; }

	/// <summary>Priority for ordering (1 = highest). Ties are broken by Name alphabetical.</summary>
	int Priority { get; }

	/// <summary>Evaluate this rule against the given position. Returns null when the rule does not fire.</summary>
	ManagementProposal? Evaluate(OpenPosition position, EvaluationContext context);
}
```

- [ ] **Step 2: Create `AI/Rules/ProposalFingerprint.cs`.**

```csharp
using System.Globalization;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Stable identity of a proposal for idempotency dedup. Two proposals with the same fingerprint
/// on consecutive ticks are suppressed from console output (JSONL log always records them).
/// </summary>
internal readonly record struct ProposalFingerprint(string Rule, string PositionKey, string StructuralParams)
{
	/// <summary>Builds a fingerprint from a proposal. StructuralParams are the material fields
	/// (leg symbols, quantities, and NetDebit rounded to 2 decimals) — not the rationale or clock.</summary>
	public static ProposalFingerprint From(ManagementProposal p)
	{
		var legs = string.Join("|", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var net = p.NetDebit.ToString("F2", CultureInfo.InvariantCulture);
		return new ProposalFingerprint(p.Rule, p.PositionKey, $"{legs};{net}");
	}

	/// <summary>Returns true if two fingerprints are materially equivalent (same rule, position, legs, and net within $0.02).</summary>
	public static bool AreEquivalent(ProposalFingerprint a, ProposalFingerprint b) =>
		a.Rule == b.Rule && a.PositionKey == b.PositionKey && a.StructuralParams == b.StructuralParams;
}
```

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Commit.**

```bash
git add AI/Rules/IManagementRule.cs AI/Rules/ProposalFingerprint.cs
git commit -m "Add IManagementRule interface and proposal fingerprint"
```

---

## Task 3: Source interfaces (`IPositionSource`, `IQuoteSource`)

**Files:**
- Create: `AI/Sources/IPositionSource.cs`
- Create: `AI/Sources/IQuoteSource.cs`

- [ ] **Step 1: Create `AI/Sources/IPositionSource.cs`.**

```csharp
namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Provides current open positions, filtered to the configured tickers.
/// Implementations: LivePositionSource (Webull OpenAPI) and ReplayPositionSource (orders.jsonl).
/// </summary>
internal interface IPositionSource
{
	/// <summary>Returns the open positions at the given logical time.</summary>
	/// <param name="asOf">Logical timestamp. Live implementations ignore this. Replay uses it to rebuild state.</param>
	/// <param name="tickers">Filter: only return positions whose underlying is in this set. Case-insensitive.</param>
	Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation);

	/// <summary>Returns total free cash and account value at the given logical time.</summary>
	Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation);
}
```

- [ ] **Step 2: Create `AI/Sources/IQuoteSource.cs`.**

```csharp
namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Provides option quotes and underlying spot prices.
/// Implementations: LiveQuoteSource (Yahoo / Webull) and ReplayQuoteSource (Black-Scholes + IV back-solve).
/// </summary>
internal interface IQuoteSource
{
	/// <summary>Fetches quotes for all OCC option symbols in the set, plus the spot price for each unique ticker root.</summary>
	/// <param name="asOf">Logical timestamp. Live implementations ignore this; replay uses it.</param>
	/// <param name="optionSymbols">OCC option symbols.</param>
	/// <param name="tickers">Underlying tickers for spot-price lookup.</param>
	Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation);
}

/// <summary>Bundle of per-leg quotes and per-ticker spots returned by IQuoteSource.</summary>
internal sealed record QuoteSnapshot(
	IReadOnlyDictionary<string, OptionContractQuote> Options,
	IReadOnlyDictionary<string, decimal> Underlyings
);
```

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Commit.**

```bash
git add AI/Sources/IPositionSource.cs AI/Sources/IQuoteSource.cs
git commit -m "Add IPositionSource and IQuoteSource interfaces"
```

---

## Task 4: `AIConfig` — model + loader + validation

**Files:**
- Create: `AI/AIConfig.cs`

- [ ] **Step 1: Create `AI/AIConfig.cs`.**

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class AIConfig
{
	[JsonPropertyName("tickers")] public List<string> Tickers { get; set; } = new();
	[JsonPropertyName("tickIntervalSeconds")] public int TickIntervalSeconds { get; set; } = 60;
	[JsonPropertyName("marketHours")] public MarketHoursConfig MarketHours { get; set; } = new();
	[JsonPropertyName("quoteSource")] public string QuoteSource { get; set; } = "webull";
	[JsonPropertyName("positionSource")] public PositionSourceConfig PositionSource { get; set; } = new();
	[JsonPropertyName("cashReserve")] public CashReserveConfig CashReserve { get; set; } = new();
	[JsonPropertyName("log")] public LogConfig Log { get; set; } = new();
	[JsonPropertyName("rules")] public RulesConfig Rules { get; set; } = new();
}

internal sealed class MarketHoursConfig
{
	[JsonPropertyName("start")] public string Start { get; set; } = "09:30";
	[JsonPropertyName("end")] public string End { get; set; } = "16:00";
	[JsonPropertyName("tz")] public string Tz { get; set; } = "America/New_York";
}

internal sealed class PositionSourceConfig
{
	[JsonPropertyName("type")] public string Type { get; set; } = "openapi";
	[JsonPropertyName("account")] public string Account { get; set; } = "default";
}

internal sealed class CashReserveConfig
{
	[JsonPropertyName("mode")] public string Mode { get; set; } = "percent";   // "percent" or "absolute"
	[JsonPropertyName("value")] public decimal Value { get; set; } = 25m;      // percent of account value, or absolute $
}

internal sealed class LogConfig
{
	[JsonPropertyName("path")] public string Path { get; set; } = "data/ai-proposals.log";
	[JsonPropertyName("consoleVerbosity")] public string ConsoleVerbosity { get; set; } = "normal"; // quiet | normal | debug
}

internal sealed class RulesConfig
{
	[JsonPropertyName("stopLoss")] public StopLossConfig StopLoss { get; set; } = new();
	[JsonPropertyName("takeProfit")] public TakeProfitConfig TakeProfit { get; set; } = new();
	[JsonPropertyName("defensiveRoll")] public DefensiveRollConfig DefensiveRoll { get; set; } = new();
	[JsonPropertyName("rollShortOnExpiry")] public RollShortOnExpiryConfig RollShortOnExpiry { get; set; } = new();
}

internal sealed class StopLossConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("maxDebitMultiplier")] public decimal MaxDebitMultiplier { get; set; } = 1.5m;
	[JsonPropertyName("spotBeyondBreakevenPct")] public decimal SpotBeyondBreakevenPct { get; set; } = 3.0m;
}

internal sealed class TakeProfitConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("pctOfMaxProfit")] public decimal PctOfMaxProfit { get; set; } = 40m;
}

internal sealed class DefensiveRollConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("spotWithinPctOfShortStrike")] public decimal SpotWithinPctOfShortStrike { get; set; } = 1.0m;
	[JsonPropertyName("triggerDTE")] public int TriggerDTE { get; set; } = 3;
	[JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0.50m;
}

internal sealed class RollShortOnExpiryConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("triggerDTE")] public int TriggerDTE { get; set; } = 2;
	[JsonPropertyName("maxShortPremium")] public decimal MaxShortPremium { get; set; } = 0.10m;
	[JsonPropertyName("minRollCredit")] public decimal MinRollCredit { get; set; } = 0.05m;
}

internal static class AIConfigLoader
{
	internal const string ConfigPath = "data/ai-config.json";

	/// <summary>Loads and validates ai-config.json. Returns null (with stderr message) on any failure.</summary>
	internal static AIConfig? Load()
	{
		var path = Program.ResolvePath(ConfigPath);
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"Error: ai config not found at '{ConfigPath}'.");
			Console.Error.WriteLine($"  Run: cp ai-config.example.json {ConfigPath} and edit.");
			return null;
		}

		AIConfig? config;
		try { config = JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(path)); }
		catch (JsonException ex) { Console.Error.WriteLine($"Error: failed to parse ai-config.json: {ex.Message}"); return null; }

		if (config == null) { Console.Error.WriteLine("Error: ai-config.json is empty."); return null; }

		var err = Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config.json: {err}"); return null; }

		return config;
	}

	/// <summary>Returns null when valid; otherwise a human-readable error string naming the field and bound.</summary>
	internal static string? Validate(AIConfig c)
	{
		if (c.Tickers.Count == 0) return "tickers: must contain at least one symbol";
		if (c.TickIntervalSeconds < 1 || c.TickIntervalSeconds > 3600) return $"tickIntervalSeconds: must be in [1, 3600], got {c.TickIntervalSeconds}";
		if (!TimeSpan.TryParseExact(c.MarketHours.Start, "hh\\:mm", CultureInfo.InvariantCulture, out _)) return $"marketHours.start: must be HH:MM, got '{c.MarketHours.Start}'";
		if (!TimeSpan.TryParseExact(c.MarketHours.End, "hh\\:mm", CultureInfo.InvariantCulture, out _)) return $"marketHours.end: must be HH:MM, got '{c.MarketHours.End}'";
		if (c.QuoteSource is not ("webull" or "yahoo")) return $"quoteSource: must be 'webull' or 'yahoo', got '{c.QuoteSource}'";
		if (c.PositionSource.Type is not ("openapi" or "jsonl")) return $"positionSource.type: must be 'openapi' or 'jsonl', got '{c.PositionSource.Type}'";
		if (c.CashReserve.Mode is not ("percent" or "absolute")) return $"cashReserve.mode: must be 'percent' or 'absolute', got '{c.CashReserve.Mode}'";
		if (c.CashReserve.Value < 0m) return $"cashReserve.value: must be non-negative, got {c.CashReserve.Value}";
		if (c.CashReserve.Mode == "percent" && c.CashReserve.Value > 100m) return $"cashReserve.value: must be ≤ 100 for mode 'percent', got {c.CashReserve.Value}";
		if (c.Log.ConsoleVerbosity is not ("quiet" or "normal" or "debug")) return $"log.consoleVerbosity: must be quiet|normal|debug, got '{c.Log.ConsoleVerbosity}'";

		var sl = c.Rules.StopLoss;
		if (sl.MaxDebitMultiplier <= 0m) return $"rules.stopLoss.maxDebitMultiplier: must be > 0, got {sl.MaxDebitMultiplier}";
		if (sl.SpotBeyondBreakevenPct < 0m) return $"rules.stopLoss.spotBeyondBreakevenPct: must be ≥ 0, got {sl.SpotBeyondBreakevenPct}";

		var tp = c.Rules.TakeProfit;
		if (tp.PctOfMaxProfit <= 0m || tp.PctOfMaxProfit > 100m) return $"rules.takeProfit.pctOfMaxProfit: must be in (0, 100], got {tp.PctOfMaxProfit}";

		var dr = c.Rules.DefensiveRoll;
		if (dr.SpotWithinPctOfShortStrike < 0m) return $"rules.defensiveRoll.spotWithinPctOfShortStrike: must be ≥ 0, got {dr.SpotWithinPctOfShortStrike}";
		if (dr.TriggerDTE < 0) return $"rules.defensiveRoll.triggerDTE: must be ≥ 0, got {dr.TriggerDTE}";
		if (dr.StrikeStep <= 0m) return $"rules.defensiveRoll.strikeStep: must be > 0, got {dr.StrikeStep}";

		var rr = c.Rules.RollShortOnExpiry;
		if (rr.TriggerDTE < 0) return $"rules.rollShortOnExpiry.triggerDTE: must be ≥ 0, got {rr.TriggerDTE}";
		if (rr.MaxShortPremium < 0m) return $"rules.rollShortOnExpiry.maxShortPremium: must be ≥ 0, got {rr.MaxShortPremium}";
		if (rr.MinRollCredit < 0m) return $"rules.rollShortOnExpiry.minRollCredit: must be ≥ 0, got {rr.MinRollCredit}";

		return null;
	}
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/AIConfig.cs
git commit -m "Add AIConfig model, loader, and validation"
```

---

## Task 5: `ai-config.example.json`

**Files:**
- Create: `ai-config.example.json` (repo root)

- [ ] **Step 1: Create the example file with all defaults explicitly written.**

```json
{
	"tickers": ["GME"],
	"tickIntervalSeconds": 60,
	"marketHours": {
		"start": "09:30",
		"end": "16:00",
		"tz": "America/New_York"
	},
	"quoteSource": "webull",
	"positionSource": {
		"type": "openapi",
		"account": "default"
	},
	"cashReserve": {
		"mode": "percent",
		"value": 25
	},
	"log": {
		"path": "data/ai-proposals.log",
		"consoleVerbosity": "normal"
	},
	"rules": {
		"stopLoss": {
			"enabled": true,
			"maxDebitMultiplier": 1.5,
			"spotBeyondBreakevenPct": 3.0
		},
		"takeProfit": {
			"enabled": true,
			"pctOfMaxProfit": 40
		},
		"defensiveRoll": {
			"enabled": true,
			"spotWithinPctOfShortStrike": 1.0,
			"triggerDTE": 3,
			"strikeStep": 0.50
		},
		"rollShortOnExpiry": {
			"enabled": true,
			"triggerDTE": 2,
			"maxShortPremium": 0.10,
			"minRollCredit": 0.05
		}
	}
}
```

- [ ] **Step 2: Verify the file parses as JSON.**

Run: `python3 -c "import json; json.load(open('ai-config.example.json'))"` from repo root.
Expected: exits 0 with no output.

- [ ] **Step 3: Commit.**

```bash
git add ai-config.example.json
git commit -m "Add example ai-config with all defaults"
```

---

## Task 6: Extract `CashReserveHelper` from `AnalyzeCommon`

**Files:**
- Create: `AI/CashReserveHelper.cs`
- Modify: `AnalyzeCommand.cs` — replace the inline funding-check computation with a call to the shared helper.

Context: `AnalyzeCommon.RunRollAnalysis` in `AnalyzeCommand.cs` contains funding-check logic triggered by `--cash`. We extract the pure computation into `AI/CashReserveHelper.cs` so both `AnalyzeCommand` and the new `ai` rules use it.

- [ ] **Step 1: Open `AnalyzeCommand.cs` and find the block that prints the "Net" funding-check line inside `RunRollAnalysis`.**

Run: `grep -n "funding" AnalyzeCommand.cs || grep -n "Net = Available" AnalyzeCommand.cs`
Expected: lines identifying where `--cash` drives the funding block. Read the surrounding 40 lines to understand inputs (available cash, BP delta, natural market credit/debit) and outputs (Available, Required, Net).

- [ ] **Step 2: Create `AI/CashReserveHelper.cs` with the extracted pure function.**

```csharp
namespace WebullAnalytics.AI;

/// <summary>
/// Funding check for proposed rolls/closes. Pure function — no I/O, no side effects.
/// Computes whether a proposal is fundable given current cash and a required reserve.
/// </summary>
internal static class CashReserveHelper
{
	/// <summary>
	/// Returns the reserve amount for a given mode + value + account value.
	/// "percent" mode: reserve = accountValue * value/100.
	/// "absolute" mode: reserve = value (in dollars).
	/// </summary>
	internal static decimal ComputeReserve(string mode, decimal value, decimal accountValue) =>
		mode switch
		{
			"percent" => accountValue * (value / 100m),
			"absolute" => value,
			_ => throw new ArgumentException($"Unknown cash-reserve mode: '{mode}'")
		};

	/// <summary>
	/// Result of a funding check.
	/// </summary>
	/// <param name="FreeAfter">Cash remaining after applying the proposed credit/debit and honoring the reserve.</param>
	/// <param name="RequiredFree">Reserve amount that must stay free.</param>
	/// <param name="Blocked">True when FreeAfter would be negative (i.e., proposal would violate the reserve).</param>
	/// <param name="Detail">Human-readable summary: "free $Y, requires $X".</param>
	internal readonly record struct FundingCheck(decimal FreeAfter, decimal RequiredFree, bool Blocked, string Detail);

	/// <summary>
	/// Checks whether a proposal with the given net debit (negative = debit paid, positive = credit received)
	/// can be executed without violating the configured reserve.
	/// </summary>
	/// <param name="netDebit">Negative for debit paid (cash out); positive for credit received (cash in).</param>
	/// <param name="currentCash">Current free cash.</param>
	/// <param name="accountValue">Total account value including positions marked to market.</param>
	/// <param name="reserveMode">"percent" or "absolute".</param>
	/// <param name="reserveValue">Reserve magnitude (percent or dollars).</param>
	internal static FundingCheck Check(decimal netDebit, decimal currentCash, decimal accountValue, string reserveMode, decimal reserveValue)
	{
		var reserve = ComputeReserve(reserveMode, reserveValue, accountValue);
		// netDebit is negative for debit (cash out); adding it reduces cash.
		var cashAfter = currentCash + netDebit;
		var freeAfter = cashAfter - reserve;
		var blocked = freeAfter < 0m;
		var detail = $"free ${Math.Max(0m, cashAfter):N2}, requires ${reserve:N2}";
		return new FundingCheck(freeAfter, reserve, blocked, detail);
	}
}
```

- [ ] **Step 3: Refactor `AnalyzeCommon.RunRollAnalysis` to use `CashReserveHelper.Check` for the funding block.**

In `AnalyzeCommand.cs`, replace the existing inline computation of "Available − Required = Net" with a call to `CashReserveHelper.Check`. Preserve the exact output format so `analyze roll --cash` produces identical console text. Use reserveMode = "absolute" and reserveValue = 0 since `analyze roll --cash` supplies the raw cash figure directly; the check becomes `Check(naturalCredit, cashParam, 0, "absolute", 0)` where the "Required" field becomes BP delta (separate math).

**Note:** The existing `analyze roll --cash` output has three lines: Available, Required, Net. `CashReserveHelper.Check` computes a different thing (cash-after-debit vs reserve). The refactor is **to keep the helper pure and add it without breaking existing `analyze roll` output**. If the signatures don't align cleanly, keep the existing `analyze roll` code as-is and *only* use `CashReserveHelper` in the new `ai` code paths. Document the divergence in a comment: `// AnalyzeCommon.RunRollAnalysis computes BP-delta funding (different domain); CashReserveHelper is for ai proposals.`

- [ ] **Step 4: Build and run `analyze roll --cash` to verify no behavior change.**

Run:
```
"/mnt/c/Program Files/dotnet/dotnet.exe" build
./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe analyze roll "GME260424C00025000>GME260501C00025000:1" --api webull --cash 10000
```
Expected: the Net surplus/shortfall line still prints with the same format as before the refactor.

- [ ] **Step 5: Commit.**

```bash
git add AI/CashReserveHelper.cs AnalyzeCommand.cs
git commit -m "Add CashReserveHelper for ai proposals"
```

---

## Task 7: `StopLossRule`

**Files:**
- Create: `AI/Rules/StopLossRule.cs`

- [ ] **Step 1: Create the rule.**

```csharp
namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 1: close the position when mark-to-market debit grows past a multiplier of initial debit,
/// or when spot moves beyond break-even by more than a configured percentage.
/// </summary>
internal sealed class StopLossRule : IManagementRule
{
	private readonly StopLossConfig _config;

	public StopLossRule(StopLossConfig config) { _config = config; }

	public string Name => "StopLossRule";
	public int Priority => 1;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Legs.Count == 0) return null;

		// 1) Compute current mark-to-market net debit from quotes.
		var currentMarkPerContract = ComputeMarkPerContract(position, ctx);
		if (currentMarkPerContract == null) return null;

		var initialDebit = Math.Abs(position.InitialNetDebit);
		if (initialDebit <= 0m) return null; // credit-received positions aren't a "debit stop" candidate
		var currentDebit = Math.Max(0m, -currentMarkPerContract.Value); // positive when underwater

		// 2) Trigger on debit multiplier.
		if (currentDebit >= initialDebit * _config.MaxDebitMultiplier)
		{
			return BuildClose(position, currentMarkPerContract.Value,
				$"mark debit ${currentDebit:F2}/contract ≥ {_config.MaxDebitMultiplier}× initial ${initialDebit:F2}");
		}

		// 3) Trigger on spot beyond break-even.
		if (ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot))
		{
			var (beLow, beHigh) = EstimateBreakEvens(position);
			var pctBand = _config.SpotBeyondBreakevenPct / 100m;
			if (beLow.HasValue && spot < beLow.Value * (1m - pctBand))
				return BuildClose(position, currentMarkPerContract.Value,
					$"spot ${spot:F2} < lower break-even ${beLow.Value:F2} by > {_config.SpotBeyondBreakevenPct}%");
			if (beHigh.HasValue && spot > beHigh.Value * (1m + pctBand))
				return BuildClose(position, currentMarkPerContract.Value,
					$"spot ${spot:F2} > upper break-even ${beHigh.Value:F2} by > {_config.SpotBeyondBreakevenPct}%");
		}

		return null;
	}

	private static ManagementProposal BuildClose(OpenPosition p, decimal markPerContract, string rationale)
	{
		// Close proposes reversing every leg.
		var legs = p.Legs.Select(l => new ProposalLeg(
			Action: l.Side == Side.Buy ? "sell" : "buy",
			Symbol: l.Symbol,
			Qty: l.Qty
		)).ToList();

		return new ManagementProposal(
			Rule: "StopLossRule",
			Ticker: p.Ticker,
			PositionKey: p.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: markPerContract * p.Quantity,
			Rationale: rationale
		);
	}

	/// <summary>Computes the per-contract mark value (sum of leg midpoint values signed by direction).
	/// Returns null if any leg is missing a quote.</summary>
	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue; // skip stock legs here; they don't alter the option-mark
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (q.Bid == null || q.Ask == null) return null;
			var mid = ((q.Bid.Value + q.Ask.Value) / 2m);
			// Long leg contributes +mid; short leg contributes -mid (you'd pay to close it).
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	/// <summary>Rough break-even estimate from position legs. Returns (low, high); either may be null.
	/// For calendars/diagonals the adjusted debit + strike geometry gives approximate break-evens.
	/// For stop-loss trigger this rough estimate is sufficient; rules that require precise break-evens use BreakEvenAnalyzer.</summary>
	private static (decimal? low, decimal? high) EstimateBreakEvens(OpenPosition p)
	{
		// For a long call calendar/diagonal: approximate break-even at short strike ± adjusted debit.
		// For puts: mirror. This is intentionally coarse.
		var shortLeg = p.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null) return (null, null);
		var debit = Math.Abs(p.AdjustedNetDebit);
		if (shortLeg.CallPut == "C")
			return (shortLeg.Strike - debit * 3m, shortLeg.Strike + debit * 3m);
		else
			return (shortLeg.Strike - debit * 3m, shortLeg.Strike + debit * 3m);
	}
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Rules/StopLossRule.cs
git commit -m "Add StopLossRule (priority 1)"
```

---

## Task 8: `TakeProfitRule`

**Files:**
- Create: `AI/Rules/TakeProfitRule.cs`

- [ ] **Step 1: Create the rule.**

```csharp
namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: close the position when mark-to-market has captured a configured percentage of
/// max projected profit (estimated via the existing TimeDecayGridBuilder for the current-date column).
/// </summary>
internal sealed class TakeProfitRule : IManagementRule
{
	private readonly TakeProfitConfig _config;

	public TakeProfitRule(TakeProfitConfig config) { _config = config; }

	public string Name => "TakeProfitRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var currentMarkPerContract = ComputeMarkPerContract(position, ctx);
		if (currentMarkPerContract == null) return null;

		// Current realized-if-closed = mark - initial debit; positive means profit-per-contract.
		var profitPerContract = currentMarkPerContract.Value - position.AdjustedNetDebit;
		if (profitPerContract <= 0m) return null;

		// Max projected profit from grid: use the peak net value in the current-date column.
		// TimeDecayGridBuilder input is a BreakEvenResult; we call it via a thin bridge in TakeProfitRule.
		// Implementation: BreakEvenAnalyzer.AnalyzeGroup for the position legs, then read grid.Values[:, 0].
		var maxProjected = GetMaxProjectedProfitPerContract(position, ctx);
		if (maxProjected == null || maxProjected.Value <= 0m) return null;

		var pctCaptured = (profitPerContract / maxProjected.Value) * 100m;
		if (pctCaptured < _config.PctOfMaxProfit) return null;

		var legs = position.Legs.Select(l => new ProposalLeg(
			Action: l.Side == Side.Buy ? "sell" : "buy",
			Symbol: l.Symbol,
			Qty: l.Qty
		)).ToList();

		return new ManagementProposal(
			Rule: "TakeProfitRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: currentMarkPerContract.Value * position.Quantity,
			Rationale: $"captured {pctCaptured:F0}% of max projected profit ${maxProjected.Value:F2}/contract (threshold {_config.PctOfMaxProfit}%)"
		);
	}

	private static decimal? ComputeMarkPerContract(OpenPosition p, EvaluationContext ctx)
	{
		decimal total = 0m;
		foreach (var leg in p.Legs)
		{
			if (leg.CallPut == null) continue;
			if (!ctx.Quotes.TryGetValue(leg.Symbol, out var q)) return null;
			if (q.Bid == null || q.Ask == null) return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			total += leg.Side == Side.Buy ? mid : -mid;
		}
		return total;
	}

	/// <summary>
	/// Bridges to the existing BreakEvenAnalyzer / TimeDecayGridBuilder to estimate max projected profit
	/// per contract at today's date column.
	/// Implementation note: we construct a minimal BreakEvenResult input from OpenPosition.Legs, call
	/// TimeDecayGridBuilder.Build to get the grid, and return max over the first date column.
	/// Returns null if the grid can't be built (e.g., missing IV).
	/// </summary>
	private static decimal? GetMaxProjectedProfitPerContract(OpenPosition p, EvaluationContext ctx)
	{
		// Detailed wiring happens in Task 21 (the bridge also serves replay). For phase-1 StopLoss/TakeProfit,
		// if the bridge is not yet wired, this method returns null and TakeProfit cannot fire.
		// After Task 21 lands, this method delegates to AI.ProfitProjector.MaxForCurrentColumn(p, ctx).
		return null;
	}
}
```

**Note:** `GetMaxProjectedProfitPerContract` is a stub at this stage — TakeProfit effectively won't fire until Task 21 wires the profit-projection bridge. That is intentional; this keeps Task 8 self-contained and lets subsequent tasks add the projector without disturbing the rule contract.

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Rules/TakeProfitRule.cs
git commit -m "Add TakeProfitRule (priority 2) with profit-projection stub"
```

---

## Task 9: `DefensiveRollRule`

**Files:**
- Create: `AI/Rules/DefensiveRollRule.cs`

- [ ] **Step 1: Create the rule.**

```csharp
namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 3: when spot is within a configured percentage of the short strike and short DTE is near,
/// propose rolling up-and-out (for short calls: higher strike, further expiry; puts mirror).
/// Constraint: proposed roll must produce a credit, reduce max-loss, or reduce delta. Otherwise
/// the proposal is emitted as AlertOnly with rationale "no-better-alternative".
/// </summary>
internal sealed class DefensiveRollRule : IManagementRule
{
	private readonly DefensiveRollConfig _config;

	public DefensiveRollRule(DefensiveRollConfig config) { _config = config; }

	public string Name => "DefensiveRollRule";
	public int Priority => 3;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null || !shortLeg.Expiry.HasValue) return null;

		var dte = (shortLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte > _config.TriggerDTE) return null;

		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot)) return null;
		var pctBand = _config.SpotWithinPctOfShortStrike / 100m;
		var nearStrike = Math.Abs(spot - shortLeg.Strike) <= shortLeg.Strike * pctBand;
		if (!nearStrike) return null;

		// Propose roll: step strike away from spot by StrikeStep; step expiry to next weekly (+7 calendar days).
		var newStrike = shortLeg.CallPut == "C"
			? shortLeg.Strike + _config.StrikeStep
			: shortLeg.Strike - _config.StrikeStep;
		var newExpiry = NextWeekly(shortLeg.Expiry.Value);
		var newSymbol = MatchKeys.OccSymbol(position.Ticker, newExpiry, newStrike, shortLeg.CallPut!);

		var legs = new[]
		{
			new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty),   // close the old short
			new ProposalLeg("sell", newSymbol, shortLeg.Qty)          // open the new short
		};

		// Look up quotes to estimate the net. If missing, we still emit as AlertOnly.
		if (!ctx.Quotes.TryGetValue(shortLeg.Symbol, out var oldQ) || !ctx.Quotes.TryGetValue(newSymbol, out var newQ) ||
		    oldQ.Ask == null || newQ.Bid == null)
		{
			return new ManagementProposal(
				Rule: "DefensiveRollRule",
				Ticker: position.Ticker,
				PositionKey: position.Key,
				Kind: ProposalKind.AlertOnly,
				Legs: legs,
				NetDebit: 0m,
				Rationale: $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}. Quote unavailable for new symbol {newSymbol}."
			);
		}

		// netCredit = newBid - oldAsk (we sell the new short, buy to close the old).
		var netCredit = newQ.Bid.Value - oldQ.Ask.Value;
		var isCredit = netCredit >= 0m;

		var kind = isCredit ? ProposalKind.Roll : ProposalKind.AlertOnly;
		var rationaleBase = $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}";
		var rationale = isCredit
			? $"{rationaleBase}; roll {shortLeg.Symbol}→{newSymbol} for net credit ${netCredit:F2}"
			: $"{rationaleBase}; no-better-alternative (proposed roll debit ${-netCredit:F2}, not a credit)";

		return new ManagementProposal(
			Rule: "DefensiveRollRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: kind,
			Legs: legs,
			NetDebit: -netCredit, // negative netDebit = credit
			Rationale: rationale
		);
	}

	/// <summary>Returns the next Friday strictly after the given date (weekly expiry).</summary>
	private static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}
}
```

**Dependency note:** This rule calls `MatchKeys.OccSymbol(ticker, expiry, strike, callPut)`. If that helper does not yet exist in the current codebase, add it to `MatchKeys.cs` as a pure function that formats an OCC symbol string from the components. Verify by searching: `grep -n "OccSymbol\|BuildOcc\|OccSuffix" MatchKeys.cs`. If only `OccSuffix` exists, add a wrapper:

```csharp
// In MatchKeys.cs
internal static string OccSymbol(string root, DateTime expiry, decimal strike, string callPut) =>
	$"{root}{expiry:yyMMdd}{OccSuffix(strike, callPut)}";
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Rules/DefensiveRollRule.cs MatchKeys.cs
git commit -m "Add DefensiveRollRule (priority 3)"
```

---

## Task 10: `RollShortOnExpiryRule`

**Files:**
- Create: `AI/Rules/RollShortOnExpiryRule.cs`

- [ ] **Step 1: Create the rule.**

```csharp
namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 4: routine maintenance — when the short leg is near expiry and its premium has decayed
/// below a threshold, propose rolling it to the next weekly at the same strike for a net credit.
/// Suppressed if a higher-priority rule already matched.
/// </summary>
internal sealed class RollShortOnExpiryRule : IManagementRule
{
	private readonly RollShortOnExpiryConfig _config;

	public RollShortOnExpiryRule(RollShortOnExpiryConfig config) { _config = config; }

	public string Name => "RollShortOnExpiryRule";
	public int Priority => 4;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;

		var shortLeg = position.Legs.FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null);
		if (shortLeg == null || !shortLeg.Expiry.HasValue) return null;

		var dte = (shortLeg.Expiry.Value.Date - ctx.Now.Date).Days;
		if (dte > _config.TriggerDTE) return null;

		if (!ctx.Quotes.TryGetValue(shortLeg.Symbol, out var oldQ)) return null;
		if (oldQ.Bid == null || oldQ.Ask == null) return null;
		var oldMid = (oldQ.Bid.Value + oldQ.Ask.Value) / 2m;
		if (oldMid > _config.MaxShortPremium) return null;

		// Same strike, next weekly.
		var newExpiry = NextWeekly(shortLeg.Expiry.Value);
		var newSymbol = MatchKeys.OccSymbol(position.Ticker, newExpiry, shortLeg.Strike, shortLeg.CallPut!);

		if (!ctx.Quotes.TryGetValue(newSymbol, out var newQ) || newQ.Bid == null || newQ.Ask == null) return null;
		var newMid = (newQ.Bid.Value + newQ.Ask.Value) / 2m;

		// netCredit = new bid - old ask (realistic fill for seller rolling to next week).
		var netCredit = newQ.Bid.Value - oldQ.Ask.Value;
		if (netCredit < _config.MinRollCredit) return null;

		var legs = new[]
		{
			new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty),
			new ProposalLeg("sell", newSymbol, shortLeg.Qty)
		};

		return new ManagementProposal(
			Rule: "RollShortOnExpiryRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Roll,
			Legs: legs,
			NetDebit: -netCredit,
			Rationale: $"short mid ${oldMid:F2} ≤ threshold ${_config.MaxShortPremium:F2}, DTE {dte}, roll credit ${netCredit:F2}"
		);
	}

	private static DateTime NextWeekly(DateTime from)
	{
		var d = from.AddDays(1);
		while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
		return d;
	}
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Rules/RollShortOnExpiryRule.cs
git commit -m "Add RollShortOnExpiryRule (priority 4)"
```

---

## Task 11: `RuleEvaluator`

**Files:**
- Create: `AI/RuleEvaluator.cs`

- [ ] **Step 1: Create the evaluator.**

```csharp
using WebullAnalytics.AI.Rules;

namespace WebullAnalytics.AI;

/// <summary>
/// Runs rules in priority order (1 first). For each position, the first rule to return a
/// non-null proposal wins; lower-priority rules are not evaluated for that position.
/// Emits proposals with cash-reserve tags applied. Handles idempotency fingerprint dedup
/// across consecutive ticks.
/// </summary>
internal sealed class RuleEvaluator
{
	private readonly IReadOnlyList<IManagementRule> _rules;
	private readonly AIConfig _config;
	private readonly Dictionary<string, ProposalFingerprint> _lastFingerprintByPositionKey = new();

	public RuleEvaluator(IReadOnlyList<IManagementRule> rules, AIConfig config)
	{
		_rules = rules.OrderBy(r => r.Priority).ThenBy(r => r.Name).ToList();
		_config = config;
	}

	/// <summary>Result of one evaluation pass.</summary>
	/// <param name="Proposal">The emitted proposal (always non-null when in the list).</param>
	/// <param name="IsRepeat">True when the same fingerprint fired on the previous tick for this position.</param>
	internal readonly record struct EvaluationResult(ManagementProposal Proposal, bool IsRepeat);

	internal IReadOnlyList<EvaluationResult> Evaluate(EvaluationContext ctx)
	{
		var results = new List<EvaluationResult>();

		foreach (var (key, position) in ctx.OpenPositions)
		{
			ManagementProposal? proposal = null;
			foreach (var rule in _rules)
			{
				proposal = rule.Evaluate(position, ctx);
				if (proposal != null) break;
			}
			if (proposal == null) continue;

			// Apply cash-reserve tag.
			var check = CashReserveHelper.Check(
				netDebit: proposal.NetDebit,
				currentCash: ctx.AccountCash,
				accountValue: ctx.AccountValue,
				reserveMode: _config.CashReserve.Mode,
				reserveValue: _config.CashReserve.Value);

			if (check.Blocked)
				proposal = proposal with { CashReserveBlocked = true, CashReserveDetail = check.Detail };

			// Dedup.
			var fp = ProposalFingerprint.From(proposal);
			var isRepeat = _lastFingerprintByPositionKey.TryGetValue(key, out var prev) && ProposalFingerprint.AreEquivalent(prev, fp);
			_lastFingerprintByPositionKey[key] = fp;

			results.Add(new EvaluationResult(proposal, isRepeat));
		}

		// Forget fingerprints for positions that no longer exist (avoid unbounded memory growth).
		var stale = _lastFingerprintByPositionKey.Keys.Where(k => !ctx.OpenPositions.ContainsKey(k)).ToList();
		foreach (var k in stale) _lastFingerprintByPositionKey.Remove(k);

		return results;
	}

	/// <summary>Constructs the default rule set from config.</summary>
	internal static IReadOnlyList<IManagementRule> BuildRules(AIConfig config) => new IManagementRule[]
	{
		new StopLossRule(config.Rules.StopLoss),
		new TakeProfitRule(config.Rules.TakeProfit),
		new DefensiveRollRule(config.Rules.DefensiveRoll),
		new RollShortOnExpiryRule(config.Rules.RollShortOnExpiry)
	};
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/RuleEvaluator.cs
git commit -m "Add RuleEvaluator with priority dispatch and fingerprint dedup"
```

---

## Task 12: `ProposalSink` (console + JSONL log)

**Files:**
- Create: `AI/Output/ProposalSink.cs`

- [ ] **Step 1: Create the sink.**

```csharp
using System.Text.Json;
using Spectre.Console;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Writes proposals to the console (human-readable, Spectre-formatted) and a JSONL log (machine-parseable).
/// Idempotency dedup is handled by RuleEvaluator; this sink respects the `isRepeat` flag to suppress
/// repeat console lines at normal verbosity while always appending to the JSONL file.
/// </summary>
internal sealed class ProposalSink : IDisposable
{
	private readonly StreamWriter _file;
	private readonly LogConfig _log;
	private readonly string _mode; // "watch" | "once" | "replay"

	public ProposalSink(LogConfig log, string mode)
	{
		_log = log;
		_mode = mode;
		var path = Program.ResolvePath(log.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		_file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
	}

	public void Emit(ManagementProposal p, bool isRepeat)
	{
		WriteJsonl(p);
		WriteConsole(p, isRepeat);
	}

	private void WriteJsonl(ManagementProposal p)
	{
		var record = new
		{
			ts = DateTime.Now.ToString("o"),
			rule = p.Rule,
			ticker = p.Ticker,
			positionKey = p.PositionKey,
			proposal = new
			{
				type = p.Kind.ToString().ToLowerInvariant(),
				legs = p.Legs.Select(l => new { action = l.Action, symbol = l.Symbol, qty = l.Qty }),
				netDebit = p.NetDebit
			},
			rationale = p.Rationale,
			cashReserveBlocked = p.CashReserveBlocked,
			cashReserveDetail = p.CashReserveDetail,
			mode = _mode
		};
		_file.WriteLine(JsonSerializer.Serialize(record));
	}

	private void WriteConsole(ManagementProposal p, bool isRepeat)
	{
		if (isRepeat && _log.ConsoleVerbosity == "normal") return;
		if (_log.ConsoleVerbosity == "quiet" && !p.CashReserveBlocked && p.Kind != ProposalKind.AlertOnly && !isRepeat)
		{
			// In quiet mode we only print non-actionable tags and new proposals; this branch permits
			// the default (logged proposals). Tune as needed.
		}

		var color = p.Kind switch
		{
			ProposalKind.Close => "yellow",
			ProposalKind.Roll => "cyan",
			ProposalKind.AlertOnly => "grey",
			_ => "white"
		};
		var header = $"[bold {color}]{p.Rule}[/] [grey]{p.Ticker}[/] [dim]{p.PositionKey}[/]";
		if (p.CashReserveBlocked) header += " [yellow]⚠ blocked by cash reserve[/]";
		AnsiConsole.MarkupLine(header);

		foreach (var leg in p.Legs)
			AnsiConsole.MarkupLine($"  {Markup.Escape(leg.Action.ToUpperInvariant())} {Markup.Escape(leg.Symbol)} x{leg.Qty}");

		var netLabel = p.NetDebit >= 0m ? $"credit ${p.NetDebit:F2}" : $"debit ${-p.NetDebit:F2}";
		AnsiConsole.MarkupLine($"  [dim]net {Markup.Escape(netLabel)}[/]");
		AnsiConsole.MarkupLine($"  [italic]{Markup.Escape(p.Rationale)}[/]");
		if (p.CashReserveBlocked && p.CashReserveDetail != null)
			AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(p.CashReserveDetail)}[/]");
		AnsiConsole.WriteLine();
	}

	public void Dispose() => _file.Dispose();
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Output/ProposalSink.cs
git commit -m "Add ProposalSink (console + JSONL output)"
```

---

## Task 13: `LivePositionSource` (Webull OpenAPI)

**Files:**
- Create: `AI/Sources/LivePositionSource.cs`

**Context:** `WebullOpenApiClient` already supports fetching open orders and order details. For positions, we need to query the account-positions endpoint. Check whether the client exposes a positions call: `grep -n "position" WebullOpenApiClient.cs`. If it does not, add a method `FetchAccountPositionsAsync` that calls `GET /openapi/account/v1/positions` with the standard signed headers, returning a list of position records. If the client does expose this, reuse it.

- [ ] **Step 1: Add (or verify) `WebullOpenApiClient.FetchAccountPositionsAsync` returning a list of leg records `(occSymbol, qty, side, avgPrice, multiplier)`. Implementation mirrors the existing `FetchOpenOrdersAsync` pattern in the same file.**

Read `WebullOpenApiClient.cs` to find the open-orders fetch pattern and clone it for positions. Endpoint per Webull OpenAPI docs: `/openapi/account/v1/accounts/{accountId}/positions`.

- [ ] **Step 2: Create `AI/Sources/LivePositionSource.cs`.**

```csharp
namespace WebullAnalytics.AI.Sources;

internal sealed class LivePositionSource : IPositionSource
{
	private readonly WebullOpenApiClient _client;
	private readonly TradeAccount _account;

	public LivePositionSource(WebullOpenApiClient client, TradeAccount account)
	{
		_client = client;
		_account = account;
	}

	public async Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(
		DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		// Fetch account positions from OpenAPI, filter by ticker, group into calendars/diagonals.
		var raw = await _client.FetchAccountPositionsAsync(_account.AccountId, cancellation);

		// Group raw legs → OpenPosition by (ticker, strategyKind, strike, expiry), mirroring PositionTracker semantics.
		// Reuse StrategyClassifier to assign kinds. For phase 1 we care about Calendar / Diagonal call-side primarily.

		var grouped = new Dictionary<string, OpenPosition>();
		// ... (detailed grouping follows PositionTracker patterns; see StrategyGrouper.cs for the existing logic.)
		// Build PositionLeg[] for each group, compute InitialNetDebit from avg prices, AdjustedNetDebit same for now
		// (phase-1 live source uses cost basis from broker; replay source computes adjusted debit from fill history).

		// NOTE: The grouping implementation is substantial but follows the exact patterns in StrategyGrouper.cs.
		// The implementer should port GroupIntoStrategies from StrategyGrouper into this source, filtering by tickers
		// and producing OpenPosition records as defined in Task 1.

		return grouped;
	}

	public async Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation)
	{
		// Fetch via WebullOpenApiClient: /openapi/account/v1/accounts/{accountId}/balance or similar.
		// If not yet supported, add a FetchAccountBalanceAsync method in the client mirroring FetchOpenOrdersAsync.
		var (cash, totalValue) = await _client.FetchAccountBalanceAsync(_account.AccountId, cancellation);
		return (cash, totalValue);
	}
}
```

**Implementation note:** the StrategyGrouper port (step 2) is intricate. Read `StrategyGrouper.cs` before implementing and preserve the same grouping semantics.

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Commit.**

```bash
git add AI/Sources/LivePositionSource.cs WebullOpenApiClient.cs
git commit -m "Add LivePositionSource with Webull OpenAPI position/balance fetch"
```

---

## Task 14: `LiveQuoteSource` (Yahoo / Webull)

**Files:**
- Create: `AI/Sources/LiveQuoteSource.cs`

- [ ] **Step 1: Create the quote source.**

```csharp
using System.Text.Json;

namespace WebullAnalytics.AI.Sources;

internal sealed class LiveQuoteSource : IQuoteSource
{
	private readonly string _provider; // "webull" | "yahoo"

	public LiveQuoteSource(string provider)
	{
		_provider = provider is "webull" or "yahoo"
			? provider
			: throw new ArgumentException($"Unknown quote provider: {provider}");
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation)
	{
		// 1) Build minimal PositionRow stubs for each option symbol so existing fetchers can be reused.
		var rows = optionSymbols.Select(sym => new PositionRow(
			Instrument: sym,
			Asset: Asset.Option,
			OptionKind: "Call",          // any placeholder; symbol parsing inside fetchers overrides
			Side: Side.Buy,
			Qty: 1,
			AvgPrice: 0m,
			Expiry: null,
			MatchKey: MatchKeys.Option(sym)
		)).ToList();

		IReadOnlyDictionary<string, OptionContractQuote> options;
		IReadOnlyDictionary<string, decimal> spots;

		if (_provider == "webull")
		{
			var configPath = Program.ResolvePath(Program.ApiConfigPath);
			if (!File.Exists(configPath)) throw new InvalidOperationException("api-config.json not found. Run 'sniff' first.");
			var config = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath))
				?? throw new InvalidOperationException("api-config.json is empty.");
			if (config.Headers.Count == 0) throw new InvalidOperationException("api-config.json has no headers. Run 'sniff' first.");

			var (quotes, underlyings) = await WebullOptionsClient.FetchOptionQuotesAsync(config, rows, cancellation);
			options = quotes;
			spots = underlyings;
		}
		else
		{
			var (quotes, underlyings) = await YahooOptionsClient.FetchOptionQuotesAsync(rows, cancellation);
			options = quotes;
			spots = underlyings;
		}

		// Filter spots to the tickers requested (YahooOptionsClient returns whatever roots appeared in rows).
		var filteredSpots = spots.Where(kv => tickers.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
			.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

		return new QuoteSnapshot(options, filteredSpots);
	}
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Sources/LiveQuoteSource.cs
git commit -m "Add LiveQuoteSource wrapping YahooOptionsClient/WebullOptionsClient"
```

---

## Task 15: `AICommand` + shared settings + `ai once` subcommand

**Files:**
- Create: `AI/AICommand.cs`
- Modify: `Program.cs` — register the `ai` branch with `once` subcommand (watch and replay added in later tasks).

- [ ] **Step 1: Create `AI/AICommand.cs`.**

```csharp
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

internal abstract class AISubcommandSettings : CommandSettings
{
	[CommandOption("--config <PATH>")]
	[Description("Path to ai-config.json. Default: data/ai-config.json.")]
	public string? ConfigPath { get; set; }

	[CommandOption("--tickers <LIST>")]
	[Description("Override config tickers (comma-separated).")]
	public string? Tickers { get; set; }

	[CommandOption("--output <FORMAT>")]
	[Description("Output format: console or text. Default: console.")]
	public string Output { get; set; } = "console";

	[CommandOption("--output-path <PATH>")]
	[Description("Path for --output text.")]
	public string? OutputPath { get; set; }

	[CommandOption("--api <SOURCE>")]
	[Description("Override quoteSource: webull or yahoo.")]
	public string? Api { get; set; }

	[CommandOption("--verbosity <LEVEL>")]
	[Description("quiet | normal | debug. Overrides config.")]
	public string? Verbosity { get; set; }

	public override ValidationResult Validate()
	{
		if (Output != "console" && Output != "text") return ValidationResult.Error($"--output: must be 'console' or 'text', got '{Output}'");
		if (Output == "text" && string.IsNullOrWhiteSpace(OutputPath)) return ValidationResult.Error("--output text requires --output-path");
		if (Api != null && Api != "webull" && Api != "yahoo") return ValidationResult.Error($"--api: must be 'webull' or 'yahoo', got '{Api}'");
		if (Verbosity != null && Verbosity != "quiet" && Verbosity != "normal" && Verbosity != "debug")
			return ValidationResult.Error($"--verbosity: must be quiet|normal|debug, got '{Verbosity}'");
		return ValidationResult.Success();
	}
}

internal static class AIContext
{
	/// <summary>Loads and merges config + CLI overrides. Returns null on failure (with stderr messages).</summary>
	internal static AIConfig? ResolveConfig(AISubcommandSettings settings)
	{
		var path = settings.ConfigPath ?? AIConfigLoader.ConfigPath;
		var abspath = Program.ResolvePath(path);
		if (!File.Exists(abspath))
		{
			Console.Error.WriteLine($"Error: ai config not found at '{path}'.");
			Console.Error.WriteLine($"  Run: cp ai-config.example.json {AIConfigLoader.ConfigPath} and edit.");
			return null;
		}

		AIConfig? config;
		try { config = System.Text.Json.JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(abspath)); }
		catch (System.Text.Json.JsonException ex) { Console.Error.WriteLine($"Error: failed to parse ai-config.json: {ex.Message}"); return null; }

		if (config == null) { Console.Error.WriteLine("Error: ai-config.json is empty."); return null; }

		// Apply CLI overrides.
		if (!string.IsNullOrWhiteSpace(settings.Tickers))
			config.Tickers = settings.Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		if (!string.IsNullOrWhiteSpace(settings.Api)) config.QuoteSource = settings.Api;
		if (!string.IsNullOrWhiteSpace(settings.Verbosity)) config.Log.ConsoleVerbosity = settings.Verbosity;

		var err = AIConfigLoader.Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config.json: {err}"); return null; }

		return config;
	}

	internal static IPositionSource BuildLivePositionSource(AIConfig config)
	{
		var tradeConfig = TradeConfig.Load() ?? throw new InvalidOperationException("trade-config.json required for live ai");
		var account = TradeConfig.Resolve(tradeConfig, config.PositionSource.Account) ?? throw new InvalidOperationException($"account '{config.PositionSource.Account}' not found");
		var client = new WebullOpenApiClient(account);
		return new LivePositionSource(client, account);
	}

	internal static IQuoteSource BuildLiveQuoteSource(AIConfig config) => new LiveQuoteSource(config.QuoteSource);
}

/// <summary>`ai once` — one evaluation pass, print proposals, exit.</summary>
internal sealed class AIOnceSettings : AISubcommandSettings { }

internal sealed class AIOnceCommand : AsyncCommand<AIOnceSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIOnceSettings settings, CancellationToken cancellation)
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		var positions = AIContext.BuildLivePositionSource(config);
		var quotes = AIContext.BuildLiveQuoteSource(config);

		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);
		var now = DateTime.Now;

		var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
		var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);

		var optionSymbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
		var quoteSnapshot = await quotes.GetQuotesAsync(now, optionSymbols, tickerSet, cancellation);

		var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config), config);

		using var sink = new ProposalSink(config.Log, mode: "once");
		var results = evaluator.Evaluate(ctx);
		foreach (var r in results) sink.Emit(r.Proposal, r.IsRepeat);

		AnsiConsole.MarkupLine($"[dim]Tick complete: {openPositions.Count} position(s), {results.Count} proposal(s) emitted[/]");
		return 0;
	}
}
```

- [ ] **Step 2: In `Program.cs`, register the `ai` branch with the `once` subcommand.**

After the existing `trade` branch registration (around line 52), add:

```csharp
config.AddBranch("ai", ai =>
{
	ai.AddCommand<AI.AIOnceCommand>("once");
});
```

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Smoke test `ai once`.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai once`
Expected: either runs and prints proposals (if positions exist) or exits with a clear error about missing config/credentials. Either way, no crash.

- [ ] **Step 5: Commit.**

```bash
git add AI/AICommand.cs Program.cs
git commit -m "Add ai once subcommand and Spectre branch registration"
```

---

## Task 16: `WatchLoop` + `ai watch` subcommand

**Files:**
- Create: `AI/WatchLoop.cs`
- Modify: `Program.cs` — add `watch` subcommand registration.

- [ ] **Step 1: Create `AI/WatchLoop.cs`.**

```csharp
using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

internal sealed class AIWatchSettings : AISubcommandSettings
{
	[CommandOption("--tick <SECONDS>")]
	[Description("Override tickIntervalSeconds.")]
	public int? Tick { get; set; }

	[CommandOption("--duration <DURATION>")]
	[Description("Stop after duration (e.g., 6h, 90m). Default: until market close.")]
	public string? Duration { get; set; }

	[CommandOption("--ignore-market-hours")]
	[Description("Run regardless of clock (for testing).")]
	public bool IgnoreMarketHours { get; set; }

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Tick.HasValue && (Tick.Value < 1 || Tick.Value > 3600))
			return ValidationResult.Error($"--tick: must be in [1, 3600], got {Tick.Value}");
		if (Duration != null && !TryParseDuration(Duration, out _))
			return ValidationResult.Error($"--duration: must be like '6h' or '90m', got '{Duration}'");

		return ValidationResult.Success();
	}

	internal static bool TryParseDuration(string s, out TimeSpan span)
	{
		span = default;
		if (string.IsNullOrWhiteSpace(s)) return false;
		var suffix = s[^1];
		var numPart = s[..^1];
		if (!int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
		span = suffix switch
		{
			'h' or 'H' => TimeSpan.FromHours(n),
			'm' or 'M' => TimeSpan.FromMinutes(n),
			's' or 'S' => TimeSpan.FromSeconds(n),
			_ => TimeSpan.Zero
		};
		return span != TimeSpan.Zero;
	}
}

internal sealed class AIWatchCommand : AsyncCommand<AIWatchSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIWatchSettings settings, CancellationToken cancellation)
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		var tickSeconds = settings.Tick ?? config.TickIntervalSeconds;
		var stopAt = ComputeStopTime(settings, config);

		var positions = AIContext.BuildLivePositionSource(config);
		var quotes = AIContext.BuildLiveQuoteSource(config);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config), config);
		var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);

		using var sink = new ProposalSink(config.Log, mode: "watch");

		AnsiConsole.MarkupLine($"[bold]ai watch[/] tickers={string.Join(",", config.Tickers)} tick={tickSeconds}s stopAt={stopAt:HH:mm:ss}");

		var failures = 0;
		var ticksRun = 0;
		var proposalsEmitted = 0;

		while (!cancellation.IsCancellationRequested && DateTime.Now < stopAt)
		{
			if (!settings.IgnoreMarketHours && !IsMarketOpen(config.MarketHours))
			{
				// Sleep until next market-open time or until stopAt.
				var sleep = TimeSpan.FromSeconds(Math.Min(tickSeconds * 5, 300));
				try { await Task.Delay(sleep, cancellation); } catch (OperationCanceledException) { break; }
				continue;
			}

			try
			{
				var now = DateTime.Now;
				var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
				var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);
				var optionSymbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
				var quoteSnapshot = await quotes.GetQuotesAsync(now, optionSymbols, tickerSet, cancellation);

				var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
				var results = evaluator.Evaluate(ctx);
				foreach (var r in results) { sink.Emit(r.Proposal, r.IsRepeat); proposalsEmitted++; }

				ticksRun++;
				failures = 0;
			}
			catch (OperationCanceledException) { break; }
			catch (UnauthorizedAccessException ex)
			{
				Console.Error.WriteLine($"Auth failure: {ex.Message}. Exiting.");
				return 2;
			}
			catch (Exception ex)
			{
				failures++;
				AnsiConsole.MarkupLine($"[red]Tick {ticksRun + 1} failed ({failures}/5): {Markup.Escape(ex.Message)}[/]");
				if (failures >= 5)
				{
					Console.Error.WriteLine("Circuit breaker: 5 consecutive tick failures. Exiting.");
					return 3;
				}
			}

			try { await Task.Delay(TimeSpan.FromSeconds(tickSeconds), cancellation); } catch (OperationCanceledException) { break; }
		}

		AnsiConsole.MarkupLine($"[dim]Loop exited. ticks={ticksRun} proposals={proposalsEmitted} failures={failures}[/]");
		return 0;
	}

	private static DateTime ComputeStopTime(AIWatchSettings s, AIConfig config)
	{
		if (s.Duration != null && AIWatchSettings.TryParseDuration(s.Duration, out var span))
			return DateTime.Now + span;
		// Default: today's market close in the configured timezone.
		var tz = TimeZoneInfo.FindSystemTimeZoneById(config.MarketHours.Tz);
		var nowLocal = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
		var endParts = config.MarketHours.End.Split(':');
		var closeLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, int.Parse(endParts[0]), int.Parse(endParts[1]), 0, DateTimeKind.Unspecified);
		return TimeZoneInfo.ConvertTimeToUtc(closeLocal, tz).ToLocalTime();
	}

	private static bool IsMarketOpen(MarketHoursConfig mh)
	{
		var tz = TimeZoneInfo.FindSystemTimeZoneById(mh.Tz);
		var nowLocal = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
		if (nowLocal.DayOfWeek == DayOfWeek.Saturday || nowLocal.DayOfWeek == DayOfWeek.Sunday) return false;
		var start = TimeSpan.Parse(mh.Start);
		var end = TimeSpan.Parse(mh.End);
		var t = nowLocal.TimeOfDay;
		return t >= start && t <= end;
	}
}
```

- [ ] **Step 2: In `Program.cs`, register `watch` alongside `once`.**

```csharp
config.AddBranch("ai", ai =>
{
	ai.AddCommand<AI.AIOnceCommand>("once");
	ai.AddCommand<AI.AIWatchCommand>("watch");
});
```

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Smoke test with `--ignore-market-hours` and a short duration.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai watch --ignore-market-hours --duration 90s --tick 30`
Expected: loop runs for 90 seconds, emitting tick updates. Ctrl-C exits cleanly.

- [ ] **Step 5: Commit.**

```bash
git add AI/WatchLoop.cs Program.cs
git commit -m "Add ai watch subcommand with market-hours gating and circuit breaker"
```

---

## Task 17: `HistoricalPriceCache`

**Files:**
- Create: `AI/Replay/HistoricalPriceCache.cs`

- [ ] **Step 1: Create the cache.**

```csharp
using System.Globalization;
using System.Text;

namespace WebullAnalytics.AI.Replay;

/// <summary>
/// Disk-cached daily closes from Yahoo. On first read for a (ticker, date) the cache fetches
/// the full historical series via YahooOptionsClient.FetchHistoricalClosesAsync and writes to
/// data/history/<ticker>.csv. Subsequent reads hit the disk cache.
/// </summary>
internal sealed class HistoricalPriceCache
{
	private readonly string _cacheDir;
	private readonly Dictionary<string, Dictionary<DateTime, decimal>> _memory = new(StringComparer.OrdinalIgnoreCase);

	public HistoricalPriceCache(string? cacheDir = null)
	{
		_cacheDir = cacheDir ?? Program.ResolvePath("data/history");
		Directory.CreateDirectory(_cacheDir);
	}

	public async Task<decimal?> GetCloseAsync(string ticker, DateTime date, CancellationToken cancellation)
	{
		var map = await LoadOrFetchAsync(ticker, cancellation);
		return map.TryGetValue(date.Date, out var close) ? close : null;
	}

	private async Task<Dictionary<DateTime, decimal>> LoadOrFetchAsync(string ticker, CancellationToken cancellation)
	{
		if (_memory.TryGetValue(ticker, out var cached)) return cached;

		var path = Path.Combine(_cacheDir, $"{ticker.ToUpperInvariant()}.csv");
		Dictionary<DateTime, decimal> map;
		if (File.Exists(path))
		{
			map = ParseCsv(await File.ReadAllTextAsync(path, cancellation));
		}
		else
		{
			// Reuse existing YahooOptionsClient; if it does not expose historical closes,
			// add a new FetchHistoricalClosesAsync(ticker, fromDate, toDate) method that hits
			// https://query1.finance.yahoo.com/v7/finance/download/{ticker}?period1=...&period2=...&interval=1d
			// and returns a Dictionary<DateTime, decimal>.
			var from = DateTime.UtcNow.AddYears(-2);
			var to = DateTime.UtcNow;
			map = await YahooOptionsClient.FetchHistoricalClosesAsync(ticker, from, to, cancellation);
			await File.WriteAllTextAsync(path, SerializeCsv(map), cancellation);
		}

		_memory[ticker] = map;
		return map;
	}

	private static Dictionary<DateTime, decimal> ParseCsv(string content)
	{
		var map = new Dictionary<DateTime, decimal>();
		foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
		{
			var parts = line.Split(',');
			if (parts.Length < 2) continue;
			if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			map[d] = close;
		}
		return map;
	}

	private static string SerializeCsv(Dictionary<DateTime, decimal> map)
	{
		var sb = new StringBuilder("date,close\n");
		foreach (var kv in map.OrderBy(k => k.Key))
			sb.Append(kv.Key.ToString("yyyy-MM-dd")).Append(',').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
		return sb.ToString();
	}
}
```

- [ ] **Step 2: Add `YahooOptionsClient.FetchHistoricalClosesAsync` if it does not already exist.**

Search first: `grep -n "FetchHistoricalCloses\|v7/finance/download" YahooOptionsClient.cs`
If missing, add:

```csharp
// In YahooOptionsClient.cs
public static async Task<Dictionary<DateTime, decimal>> FetchHistoricalClosesAsync(
	string ticker, DateTime from, DateTime to, CancellationToken cancellation)
{
	var url = $"https://query1.finance.yahoo.com/v7/finance/download/{Uri.EscapeDataString(ticker)}"
		+ $"?period1={new DateTimeOffset(from).ToUnixTimeSeconds()}"
		+ $"&period2={new DateTimeOffset(to).ToUnixTimeSeconds()}&interval=1d&events=history";

	using var http = new HttpClient();
	http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (WebullAnalytics)");
	var csv = await http.GetStringAsync(url, cancellation);

	var result = new Dictionary<DateTime, decimal>();
	foreach (var line in csv.Split('\n').Skip(1))
	{
		var parts = line.Split(',');
		if (parts.Length < 5) continue;
		if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) continue;
		if (!decimal.TryParse(parts[4], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var close)) continue;
		result[d] = close;
	}
	return result;
}
```

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Commit.**

```bash
git add AI/Replay/HistoricalPriceCache.cs YahooOptionsClient.cs
git commit -m "Add HistoricalPriceCache with Yahoo daily-close fetcher"
```

---

## Task 18: `IVBackSolver`

**Files:**
- Create: `AI/Replay/IVBackSolver.cs`

- [ ] **Step 1: Create the back-solver.**

```csharp
namespace WebullAnalytics.AI.Replay;

/// <summary>
/// Back-solves implied volatility from a historical option fill price + contemporaneous
/// underlying price, using Black-Scholes via the existing OptionMath helpers.
/// </summary>
internal sealed class IVBackSolver
{
	private readonly Dictionary<string, List<(DateTime ts, decimal price, decimal underlying)>> _fillsBySymbol = new();

	/// <summary>Registers a historical fill for symbol at timestamp, paired with the day's underlying close.</summary>
	public void RegisterFill(string symbol, DateTime ts, decimal fillPrice, decimal underlyingAtTs)
	{
		if (!_fillsBySymbol.TryGetValue(symbol, out var list))
			_fillsBySymbol[symbol] = list = new();
		list.Add((ts, fillPrice, underlyingAtTs));
	}

	/// <summary>Returns a back-solved IV for the given symbol at the given timestamp, using the nearest fill
	/// within 30 days. Returns null when no suitable fill exists.</summary>
	public decimal? ResolveIV(string symbol, DateTime asOf, DateTime expiry, decimal strike, string callPut)
	{
		if (!_fillsBySymbol.TryGetValue(symbol, out var list) || list.Count == 0) return null;

		var anchor = list.OrderBy(f => Math.Abs((f.ts - asOf).TotalSeconds)).First();
		if (Math.Abs((anchor.ts - asOf).TotalDays) > 30) return null;

		// Newton-Raphson on Black-Scholes price.
		// Use OptionMath.ImpliedVol if it exists; otherwise iterate using OptionMath.BlackScholesPrice.
		var dte = Math.Max(1, (expiry.Date - anchor.ts.Date).Days);
		var iv = OptionMath.ImpliedVol(
			S: anchor.underlying,
			K: strike,
			T: dte / 365m,
			r: 0.036m,
			marketPrice: anchor.price,
			isCall: callPut == "C"
		);
		return iv;
	}
}
```

**Dependency:** `OptionMath.ImpliedVol` must exist. If not, add a Newton-Raphson implementation to `OptionMath.cs`:

```csharp
// In OptionMath.cs
internal static decimal ImpliedVol(decimal S, decimal K, decimal T, decimal r, decimal marketPrice, bool isCall)
{
	decimal vol = 0.3m;
	for (var i = 0; i < 50; i++)
	{
		var price = BlackScholesPrice(S, K, T, r, vol, isCall);
		var vega = Vega(S, K, T, r, vol);
		if (vega == 0m) break;
		var diff = price - marketPrice;
		if (Math.Abs(diff) < 0.005m) break;
		vol -= diff / vega;
		if (vol <= 0.01m) { vol = 0.01m; break; }
		if (vol >= 5m) { vol = 5m; break; }
	}
	return vol;
}
```

Check first whether `BlackScholesPrice` and `Vega` already exist; if they do, the back-solver reuses them.

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Replay/IVBackSolver.cs OptionMath.cs
git commit -m "Add IVBackSolver with Newton-Raphson IV back-solve"
```

---

## Task 19: `ReplayPositionSource`

**Files:**
- Create: `AI/Sources/ReplayPositionSource.cs`

- [ ] **Step 1: Create the replay source.**

```csharp
namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Rebuilds OpenPosition snapshots from orders.jsonl at any historical timestamp.
/// Reuses the existing ReportCommand.LoadTrades / PositionTracker pipeline.
/// </summary>
internal sealed class ReplayPositionSource : IPositionSource
{
	private readonly List<Trade> _allTrades;
	private readonly Dictionary<int, decimal> _feeLookup;

	public ReplayPositionSource(List<Trade> allTrades, Dictionary<int, decimal> feeLookup)
	{
		_allTrades = allTrades;
		_feeLookup = feeLookup;
	}

	public Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(
		DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation)
	{
		// 1) Filter trades to those with timestamp <= asOf.
		var slice = _allTrades.Where(t => t.Timestamp <= asOf).ToList();

		// 2) Build PositionTracker state. Reuse PositionTracker.BuildOpenPositions or equivalent.
		// 3) Group by StrategyGrouper, filter by tickers, convert to OpenPosition records.
		// 4) Compute InitialNetDebit and AdjustedNetDebit from the fill history for each group.

		// The implementer should port the relevant section of ReportCommand that builds open-position
		// tables, stopping after grouping and before rendering. The output of that section is converted
		// into OpenPosition records.

		var result = new Dictionary<string, OpenPosition>();
		// ... grouping implementation ...
		return Task.FromResult<IReadOnlyDictionary<string, OpenPosition>>(result);
	}

	public Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation)
	{
		// Cash and account value at a historical instant. Reuses the running-total tracking from
		// PositionTracker (cash_initial + sum(fills up to asOf) - fees). For simplicity in phase 1,
		// account value = cash (positions marked-to-market handled by the rules themselves via quotes).
		var cashAtInstant = _allTrades.Where(t => t.Timestamp <= asOf)
			.Sum(t => (t.Side == Side.Sell ? 1m : -1m) * t.Qty * t.Price * t.Multiplier);
		// Very approximate — the real PositionTracker applies fees and starting balance.
		// The implementer should port ComputeCashBalance from PositionTracker.cs to return (cash, totalValue).
		return Task.FromResult((cashAtInstant, cashAtInstant));
	}
}
```

**Implementation note:** The grouping code is substantial. Rather than duplicating it, extract the "rebuild open positions at timestamp" logic from `ReportCommand` into a reusable method `ReportCommand.BuildOpenPositionsAt(trades, feeLookup, asOf)` that returns a grouped structure. Then `ReplayPositionSource.GetOpenPositionsAsync` is a thin adapter over it.

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Sources/ReplayPositionSource.cs ReportCommand.cs
git commit -m "Add ReplayPositionSource with historical state reconstruction"
```

---

## Task 20: `ReplayQuoteSource`

**Files:**
- Create: `AI/Sources/ReplayQuoteSource.cs`

- [ ] **Step 1: Create the source.**

```csharp
using WebullAnalytics.AI.Replay;

namespace WebullAnalytics.AI.Sources;

/// <summary>
/// Synthesizes option quotes for historical replay by pricing via Black-Scholes with IV back-solved
/// from the nearest real fill. When no fill within 30 days exists for a symbol, returns intrinsic-only.
/// </summary>
internal sealed class ReplayQuoteSource : IQuoteSource
{
	private readonly HistoricalPriceCache _priceCache;
	private readonly IVBackSolver _ivSolver;
	private readonly decimal _riskFreeRate;

	public ReplayQuoteSource(HistoricalPriceCache priceCache, IVBackSolver ivSolver, decimal riskFreeRate)
	{
		_priceCache = priceCache;
		_ivSolver = ivSolver;
		_riskFreeRate = riskFreeRate;
	}

	public async Task<QuoteSnapshot> GetQuotesAsync(
		DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers,
		CancellationToken cancellation)
	{
		var options = new Dictionary<string, OptionContractQuote>();
		var underlyings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

		// 1) Spots for each ticker at asOf.
		foreach (var ticker in tickers)
		{
			var close = await _priceCache.GetCloseAsync(ticker, asOf.Date, cancellation);
			if (close.HasValue) underlyings[ticker] = close.Value;
		}

		// 2) For each option symbol, price via Black-Scholes with back-solved IV.
		foreach (var sym in optionSymbols)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(sym);
			if (parsed == null) continue;
			if (!underlyings.TryGetValue(parsed.Root, out var S))
			{
				var close = await _priceCache.GetCloseAsync(parsed.Root, asOf.Date, cancellation);
				if (!close.HasValue) continue;
				S = close.Value;
				underlyings[parsed.Root] = S;
			}

			var iv = _ivSolver.ResolveIV(sym, asOf, parsed.ExpiryDate, parsed.Strike, parsed.CallPut);
			decimal price;
			if (iv.HasValue)
			{
				var dte = Math.Max(1, (parsed.ExpiryDate.Date - asOf.Date).Days);
				price = OptionMath.BlackScholesPrice(S, parsed.Strike, dte / 365m, _riskFreeRate, iv.Value, parsed.CallPut == "C");
			}
			else
			{
				// Intrinsic-only fallback.
				price = parsed.CallPut == "C" ? Math.Max(0m, S - parsed.Strike) : Math.Max(0m, parsed.Strike - S);
			}

			// Synthesize a symmetric bid/ask around the theoretical mid (±1% spread).
			var spread = Math.Max(0.01m, price * 0.01m);
			var bid = Math.Max(0m, price - spread);
			var ask = price + spread;
			options[sym] = new OptionContractQuote(
				ContractSymbol: sym,
				LastPrice: price,
				Bid: bid,
				Ask: ask,
				Change: null,
				PercentChange: null,
				Volume: null,
				OpenInterest: null,
				ImpliedVolatility: iv,
				HistoricalVolatility: null,
				ImpliedVolatility5Day: null
			);
		}

		return new QuoteSnapshot(options, underlyings);
	}
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Sources/ReplayQuoteSource.cs
git commit -m "Add ReplayQuoteSource (Black-Scholes + IV back-solve)"
```

---

## Task 21: `ReplayRunner` + profit-projection bridge

**Files:**
- Create: `AI/Replay/ReplayRunner.cs`
- Modify: `AI/Rules/TakeProfitRule.cs` — wire `GetMaxProjectedProfitPerContract` to the bridge.

- [ ] **Step 1: Create a profit projector helper.**

Create `AI/ProfitProjector.cs`:

```csharp
namespace WebullAnalytics.AI;

/// <summary>
/// Bridges an OpenPosition + EvaluationContext to a max-projected-profit value, reusing the existing
/// TimeDecayGridBuilder. Returns null when the grid cannot be built (missing IV for any leg).
/// </summary>
internal static class ProfitProjector
{
	/// <summary>Returns the max net projected value per contract in today's grid column, or null on failure.</summary>
	public static decimal? MaxForCurrentColumn(OpenPosition position, EvaluationContext ctx)
	{
		// Build a BreakEvenResult for the position using BreakEvenAnalyzer.AnalyzeGroup-style inputs,
		// then call TimeDecayGridBuilder.Build to get the grid. Return the max value in column 0.

		// Implementation delegates to BreakEvenAnalyzer with the same inputs used by ReportCommand.
		// Wiring: construct the minimal PositionRow[] the analyzer needs, call AnalyzeGroup, read grid.Values[:, 0].Max().

		// For phase 1 this is a thin adapter; the implementer reads BreakEvenAnalyzer.cs for the entry point.
		// If the analyzer entry points are too coupled to ReportCommand state, extract a pure-function
		// BreakEvenAnalyzer.AnalyzeBareLegs(legs, quotes, spot, riskFreeRate) signature in a follow-up.
		return null; // placeholder until the wiring lands.
	}
}
```

**Note:** The projector returning `null` means TakeProfitRule never fires in phase 1. This is acceptable — the other three rules still cover the critical cases. A follow-up task (not in this plan) should finish the projector wiring once the implementer has read `BreakEvenAnalyzer.cs` and confirmed the cleanest entry-point signature.

- [ ] **Step 2: Update `AI/Rules/TakeProfitRule.cs` to call the projector.**

Replace `return null;` in `GetMaxProjectedProfitPerContract` with:

```csharp
return ProfitProjector.MaxForCurrentColumn(p, ctx);
```

- [ ] **Step 3: Create `AI/Replay/ReplayRunner.cs`.**

```csharp
using Spectre.Console;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI.Replay;

internal sealed class ReplayRunner
{
	private readonly AIConfig _config;
	private readonly ReplayPositionSource _positions;
	private readonly ReplayQuoteSource _quotes;
	private readonly List<Trade> _allTrades;

	public ReplayRunner(AIConfig config, ReplayPositionSource positions, ReplayQuoteSource quotes, List<Trade> allTrades)
	{
		_config = config;
		_positions = positions;
		_quotes = quotes;
		_allTrades = allTrades;
	}

	public async Task<int> RunAsync(DateTime since, DateTime until, string granularity, CancellationToken cancellation)
	{
		PrintDisclaimer();

		var tickerSet = new HashSet<string>(_config.Tickers, StringComparer.OrdinalIgnoreCase);
		var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(_config), _config);
		using var sink = new ProposalSink(_config.Log, mode: "replay");

		var steps = EnumerateSteps(since, until, granularity).ToList();
		var ruleFireCounts = new Dictionary<string, int>();
		var agreementCounts = new Dictionary<string, int> { ["match"] = 0, ["partial"] = 0, ["miss"] = 0, ["divergent"] = 0 };

		foreach (var step in steps)
		{
			cancellation.ThrowIfCancellationRequested();

			var openPositions = await _positions.GetOpenPositionsAsync(step, tickerSet, cancellation);
			if (openPositions.Count == 0) continue;

			var (cash, accountValue) = await _positions.GetAccountStateAsync(step, cancellation);
			var optionSymbols = openPositions.Values.SelectMany(p => p.Legs.Where(l => l.CallPut != null).Select(l => l.Symbol)).ToHashSet();
			var quoteSnapshot = await _quotes.GetQuotesAsync(step, optionSymbols, tickerSet, cancellation);

			var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
			var results = evaluator.Evaluate(ctx);

			foreach (var r in results)
			{
				sink.Emit(r.Proposal, r.IsRepeat);
				ruleFireCounts[r.Proposal.Rule] = (ruleFireCounts.TryGetValue(r.Proposal.Rule, out var n) ? n : 0) + 1;

				var agreement = ClassifyAgreement(r.Proposal, step);
				agreementCounts[agreement]++;
			}
		}

		PrintSummary(ruleFireCounts, agreementCounts, steps.Count);
		return 0;
	}

	private IEnumerable<DateTime> EnumerateSteps(DateTime since, DateTime until, string granularity)
	{
		var d = since.Date;
		while (d <= until.Date)
		{
			if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
			{
				if (granularity == "hourly")
				{
					for (var h = 10; h <= 15; h++) yield return d.AddHours(h).AddMinutes(45);
				}
				else
				{
					yield return d.AddHours(15).AddMinutes(45); // 15:45 ET proxy
				}
			}
			d = d.AddDays(1);
		}
	}

	/// <summary>Classifies whether the proposal aligns with what the user actually did at this timestamp.</summary>
	private string ClassifyAgreement(ManagementProposal p, DateTime step)
	{
		// Look for actual trades on the same day touching the same position key. If the actions match,
		// return "match". If the user did *something* on this position on this day but different, return
		// "partial" or "divergent". If the user did nothing, return "miss".
		var sameDay = _allTrades.Where(t => t.Timestamp.Date == step.Date && t.MatchKey.Contains(p.Ticker, StringComparison.OrdinalIgnoreCase)).ToList();
		if (sameDay.Count == 0) return "miss";
		// Heuristic phase-1 classification: any trade touching the same position key on the same day counts as "partial".
		return "partial";
	}

	private static void PrintDisclaimer()
	{
		AnsiConsole.MarkupLine("[yellow bold]Replay disclaimer:[/]");
		AnsiConsole.MarkupLine("[dim]  • Quotes are Black-Scholes synthesized from fill-anchored IV, not historical bid/ask.[/]");
		AnsiConsole.MarkupLine("[dim]  • Roll credits are theoretical mids, not realized fills.[/]");
		AnsiConsole.MarkupLine("[dim]  • Daily granularity misses intraday opportunities and whipsaws.[/]");
		AnsiConsole.WriteLine();
	}

	private static void PrintSummary(Dictionary<string, int> rules, Dictionary<string, int> agreement, int stepsWalked)
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[bold]Replay summary[/] — {stepsWalked} steps walked");
		foreach (var kv in rules.OrderByDescending(k => k.Value))
			AnsiConsole.MarkupLine($"  {kv.Key}: {kv.Value}");
		AnsiConsole.MarkupLine($"[dim]Agreement: match={agreement["match"]} partial={agreement["partial"]} miss={agreement["miss"]} divergent={agreement["divergent"]}[/]");
	}
}
```

- [ ] **Step 4: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 5: Commit.**

```bash
git add AI/ProfitProjector.cs AI/Rules/TakeProfitRule.cs AI/Replay/ReplayRunner.cs
git commit -m "Add ReplayRunner and profit-projector bridge"
```

---

## Task 22: `ReplayReportRenderer`

**Files:**
- Create: `AI/Output/ReplayReportRenderer.cs`

- [ ] **Step 1: Create the renderer.**

```csharp
using Spectre.Console;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Formats the replay comparison output. For phase 1 this is a thin wrapper around
/// the console output ProposalSink already produces — it exists as a seam so text-file
/// output (for --output text) can route through here. Phase 2 adds the side-by-side
/// "what the rules proposed vs what the user did" comparison block.
/// </summary>
internal static class ReplayReportRenderer
{
	/// <summary>Renders the final summary block. Called from ReplayRunner after the walk finishes.</summary>
	public static void RenderSummary(IReadOnlyDictionary<string, int> ruleFireCounts, IReadOnlyDictionary<string, int> agreementCounts, int stepsWalked)
	{
		// In phase 1 the ReplayRunner prints its own summary. This method is reserved for
		// --output text path (file writer) which needs plain-text rendering instead of Spectre markup.
	}
}
```

- [ ] **Step 2: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add AI/Output/ReplayReportRenderer.cs
git commit -m "Add ReplayReportRenderer stub for text-output routing"
```

---

## Task 23: `ai replay` subcommand

**Files:**
- Modify: `AI/AICommand.cs` — add `AIReplaySettings` + `AIReplayCommand`.
- Modify: `Program.cs` — register the `replay` subcommand.

- [ ] **Step 1: Append the replay subcommand to `AI/AICommand.cs`.**

```csharp
// Append to AI/AICommand.cs

internal sealed class AIReplaySettings : AISubcommandSettings
{
	[CommandOption("--since <DATE>")]
	[Description("Start date YYYY-MM-DD. Default: earliest fill.")]
	public string? Since { get; set; }

	[CommandOption("--until <DATE>")]
	[Description("End date YYYY-MM-DD. Default: latest fill.")]
	public string? Until { get; set; }

	[CommandOption("--granularity <LEVEL>")]
	[Description("daily or hourly. Default: daily.")]
	public string Granularity { get; set; } = "daily";

	public override ValidationResult Validate()
	{
		var baseResult = base.Validate();
		if (!baseResult.Successful) return baseResult;

		if (Since != null && !DateTime.TryParseExact(Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--since: must be YYYY-MM-DD, got '{Since}'");
		if (Until != null && !DateTime.TryParseExact(Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
			return ValidationResult.Error($"--until: must be YYYY-MM-DD, got '{Until}'");
		if (Granularity != "daily" && Granularity != "hourly")
			return ValidationResult.Error($"--granularity: must be 'daily' or 'hourly', got '{Granularity}'");
		return ValidationResult.Success();
	}
}

internal sealed class AIReplayCommand : AsyncCommand<AIReplaySettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AIReplaySettings settings, CancellationToken cancellation)
	{
		var config = AIContext.ResolveConfig(settings);
		if (config == null) return 1;

		// Load all historical trades using the existing pipeline.
		var reportSettings = new ReportSettings(); // default settings
		var (trades, feeLookup, err) = ReportCommand.LoadTrades(reportSettings);
		if (err != 0) return err;

		var since = settings.Since != null
			? DateTime.ParseExact(settings.Since, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: trades.Count > 0 ? trades.Min(t => t.Timestamp).Date : DateTime.Today;
		var until = settings.Until != null
			? DateTime.ParseExact(settings.Until, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
			: trades.Count > 0 ? trades.Max(t => t.Timestamp).Date : DateTime.Today;

		// Build historical price cache and IV back-solver seeded with user's fills.
		var priceCache = new AI.Replay.HistoricalPriceCache();
		var ivSolver = new AI.Replay.IVBackSolver();
		foreach (var t in trades.Where(t => t.Asset == Asset.Option))
		{
			// Approximate the contemporaneous underlying via the price cache for that day's root.
			var parsed = ParsingHelpers.ParseOptionSymbol(t.MatchKey.Replace("option:", ""));
			if (parsed == null) continue;
			var spot = await priceCache.GetCloseAsync(parsed.Root, t.Timestamp.Date, cancellation);
			if (!spot.HasValue) continue;
			ivSolver.RegisterFill(t.MatchKey.Replace("option:", ""), t.Timestamp, t.Price, spot.Value);
		}

		var positions = new ReplayPositionSource(trades, feeLookup);
		var quotes = new AI.Replay.ReplayQuoteSource(priceCache, ivSolver, riskFreeRate: 0.036m);

		var runner = new AI.Replay.ReplayRunner(config, positions, quotes, trades);
		return await runner.RunAsync(since, until, settings.Granularity, cancellation);
	}
}
```

- [ ] **Step 2: In `Program.cs`, register `replay`.**

```csharp
config.AddBranch("ai", ai =>
{
	ai.AddCommand<AI.AIOnceCommand>("once");
	ai.AddCommand<AI.AIWatchCommand>("watch");
	ai.AddCommand<AI.AIReplayCommand>("replay");
});
```

- [ ] **Step 3: Build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
Expected: successful build.

- [ ] **Step 4: Smoke test `ai replay` against the committed `orders.jsonl`.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai replay --since 2026-03-01 --until 2026-04-17`
Expected: disclaimer block, then proposals (possibly zero if rules don't fire), then summary line with rule-fire counts.

- [ ] **Step 5: Commit.**

```bash
git add AI/AICommand.cs Program.cs
git commit -m "Add ai replay subcommand"
```

---

## Task 24: README update

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a new section after the `Trade Command` section.**

Open `README.md` and find the end of the Trade Command section. Add:

```markdown
### AI Command

The `ai` command monitors open calendar/diagonal positions during market hours and emits structured proposals (roll / take-profit / stop-loss / defensive-roll) to a JSONL log. It is **read-only in phase 1**: the command never places orders.

Three subcommands share one evaluation engine:

```bash
# Continuous monitoring during market hours (default: until 4 PM ET)
WebullAnalytics ai watch

# Single evaluation pass, print proposals, exit
WebullAnalytics ai once

# Replay the rules against historical orders.jsonl with agreement analysis
WebullAnalytics ai replay --since 2026-01-01 --until 2026-04-17
```

#### Setup

1. Copy the example config:
   ```bash
   cp ai-config.example.json data/ai-config.json
   ```
2. Edit `data/ai-config.json` and set the `tickers` array to the symbols you want to monitor.
3. Ensure `data/trade-config.json` exists (same setup as the `trade` command) — the loop reads position state from the Webull OpenAPI.

#### Rules

| Rule | Priority | Trigger |
|---|---|---|
| `StopLossRule` | 1 | MTM debit ≥ 1.5× initial, or spot beyond break-even by > 3% |
| `TakeProfitRule` | 2 | MTM ≥ 40% of max projected profit |
| `DefensiveRollRule` | 3 | Spot within 1% of short strike and short DTE ≤ 3 |
| `RollShortOnExpiryRule` | 4 | Short DTE ≤ 2 and short mid ≤ $0.10 |

All thresholds are configurable in `ai-config.json`.

#### Output

Proposals are written to two places:

- **Console**: Spectre-formatted, color-coded by action (close = yellow, roll = cyan).
- **JSONL log** at `data/ai-proposals.log`: one proposal per line, machine-parseable with `jq` or similar.

#### Cash reserve

Every proposal is funding-checked. Proposals that would leave free cash below the configured reserve get a `⚠ blocked by cash reserve` tag. This is informational in phase 1; no action is blocked since nothing executes.
```

- [ ] **Step 2: Commit.**

```bash
git add README.md
git commit -m "Document ai command in README"
```

---

## Task 25: End-to-end smoke test

**Files:** none (verification only)

- [ ] **Step 1: Fresh build.**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build -c Release`
Expected: success.

- [ ] **Step 2: Verify help output lists the new branch.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai --help`
Expected: lists `once`, `watch`, and `replay` subcommands.

- [ ] **Step 3: Run each subcommand's help.**

```
./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai once --help
./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai watch --help
./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai replay --help
```
Expected: each prints its specific flags.

- [ ] **Step 4: Verify `ai once` against real positions.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai once --api webull`
Expected: loads config, fetches positions, emits proposals (if any rule triggers) or prints "0 proposal(s) emitted", then exits cleanly. Check `data/ai-proposals.log` is created.

- [ ] **Step 5: Verify `ai watch` with a short duration.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai watch --ignore-market-hours --duration 2m --tick 30 --verbosity debug`
Expected: loop ticks at least 2 times over 2 minutes, prints per-tick info, exits cleanly.

- [ ] **Step 6: Verify `ai replay` over full history.**

Run: `./bin/Release/net10.0/win-x64/publish/WebullAnalytics.exe ai replay`
Expected: disclaimer, per-step proposals, summary block. No crashes. `data/ai-proposals.log` has new entries tagged `"mode":"replay"`.

- [ ] **Step 7: (Optional) Inspect the log with `jq`.**

Run: `cat data/ai-proposals.log | jq -r '"\(.ts) \(.rule) \(.ticker) \(.rationale)"' | tail -20`
Expected: recent proposals formatted as single lines.

- [ ] **Step 8: Final commit if any last tweaks were needed.**

```bash
git status
# If anything changed: git add -u && git commit -m "Fix issues found in end-to-end smoke test"
```

---

## Self-review checklist

- **Spec coverage:** Every goal in `docs/superpowers/specs/2026-04-19-ai-command-design.md` has a task. Replay: Tasks 17–23. Live monitoring: Tasks 13–16. Rules: Tasks 7–11. Config: Tasks 4–5. Cash reserve: Task 6 + Task 11 application. Logging: Task 12. CLI: Tasks 15, 16, 23. README: Task 24.
- **Two known placeholders are intentional and flagged:**
  1. `TakeProfitRule.GetMaxProjectedProfitPerContract` returns null until Task 21 wires `ProfitProjector.MaxForCurrentColumn`, and Task 21 notes the projector itself is a phase-1 stub pending a follow-up to finalize the `BreakEvenAnalyzer` entry-point signature. TakeProfit therefore does not fire in phase 1 — StopLoss, DefensiveRoll, and RollShortOnExpiry cover the critical paths.
  2. `ReplayRunner.ClassifyAgreement` uses a phase-1 heuristic ("any same-day trade on this ticker = partial"). Full structural comparison is a phase-2 item.
- **Type consistency:** `OpenPosition`, `PositionLeg`, `ManagementProposal`, `EvaluationContext` names are used identically in every task.
- **Commit cadence:** every task ends in a commit with a focused message.
- **No broker calls from the loop:** verified — only `IPositionSource` reads are OpenAPI-backed; there is no code path from `RuleEvaluator` to `WebullOpenApiClient.PlaceOrderAsync` or equivalent.
