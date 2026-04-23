# Reproduction Command Roll-Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Drop `--side` from emitted `wa trade place` reproduction lines and split non-calendar rolls into two single-leg commands (close + open) so the user can execute diagonal / vertical-shaped rolls that Webull's combo engine rejects.

**Architecture:** Two emission sites (`AnalyzePositionCommand.BuildReproductionCommands` and `AI/Output/ProposalSink.WriteConsole`) share one calendar-detection helper (`RollShape.IsSameStrikeCalendar`). Roll scenarios get a new signal — an `IsRoll` flag on `Scenario`, and the existing `ProposalKind.Roll` on `ManagementProposal`. Per-leg prices needed for splitting already exist in `Scenario.ActionSummary`; they are added to `ProposalLeg.PricePerShare` and populated by the three rule/engine paths that currently produce rolls. `wa trade place` itself is untouched.

**Tech Stack:** C# / .NET 10, Spectre.Console, Spectre.Console.Cli. No test framework in the repo — verification is via CLI invocation with a `--date` override so Black-Scholes pricing yields deterministic output without live quotes.

---

## File Map

| File | Change |
|------|--------|
| `RollShape.cs` | **Create** — `IsSameStrikeCalendar(IEnumerable<string> occSymbols)` static helper |
| `AnalyzePositionCommand.cs` | Add `IsRoll` to `Scenario` record; propagate from `EmitFullAndPartial`, `EmitAdd`, `NewScenario`, `NewScenarioSpread`, and the single-long scenario generator; rewrite `BuildReproductionCommands` to drop `--side` and emit two `wa trade place` lines for non-calendar rolls |
| `AI/ManagementProposal.cs` | Add `decimal? PricePerShare` to `ProposalLeg` |
| `AI/Rules/DefensiveRollRule.cs` | Populate `PricePerShare` on both legs from the same quotes used to compute `netCredit` |
| `AI/Rules/RollShortOnExpiryRule.cs` | Populate `PricePerShare` on both legs (same-strike calendar, never splits — populated for consistency) |
| `AI/ScenarioEngine.cs` | Populate `PricePerShare` in `EmitRoll` and `EmitReset` |
| `AI/Output/ProposalSink.cs` | Drop `--side`; split non-calendar Roll proposals into two single-leg lines using per-leg `PricePerShare` |

---

## Task 1: Add `RollShape.IsSameStrikeCalendar` helper

**Files:**
- Create: `RollShape.cs`

The helper takes a sequence of OCC option symbols and returns `true` iff there are exactly two, both parse as options, they share one strike, and they have distinct expiries. Anything else (single leg, 3+ legs, any equity leg, unparseable symbol, same expiry, different strikes) returns `false`.

- [ ] **Step 1: Create `RollShape.cs`**

```csharp
namespace WebullAnalytics;

/// <summary>
/// Shared helper for detecting whether a list of option legs forms a same-strike calendar
/// roll — the one roll shape Webull's combo engine accepts when it includes a position reversal.
/// Used by the `wa analyze position` and AI `ProposalSink` reproduction-command emitters to
/// decide whether to emit one combo `wa trade place` line or two separate single-leg lines.
/// </summary>
internal static class RollShape
{
	/// <summary>
	/// Returns true iff `occSymbols` contains exactly two OCC option symbols that share one
	/// strike and have distinct expiries. Returns false for any other shape, including single
	/// leg, 3+ legs, equity legs, unparseable symbols, same-expiry, and different-strike inputs.
	/// </summary>
	internal static bool IsSameStrikeCalendar(IEnumerable<string> occSymbols)
	{
		var parsed = new List<OptionParsed>(2);
		foreach (var sym in occSymbols)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null) return false;
			parsed.Add(p);
			if (parsed.Count > 2) return false;
		}
		if (parsed.Count != 2) return false;
		if (parsed[0].Strike != parsed[1].Strike) return false;
		if (parsed[0].ExpiryDate == parsed[1].ExpiryDate) return false;
		return true;
	}
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add RollShape.cs
git commit -m "RollShape: add IsSameStrikeCalendar helper"
```

---

## Task 2: Add `IsRoll` to `Scenario` record

**Files:**
- Modify: `AnalyzePositionCommand.cs` (record definition at line 277–286; scenario-creating methods `NewScenario`, `NewScenarioSpread`, `EmitFullAndPartial`, `EmitAdd`; three direct `new Scenario(...)` call sites inside spread-scenario generation)

Every scenario declares whether it's a roll (closes an existing leg and opens a new one). The `BuildReproductionCommands` emitter will consult this flag when deciding whether to split. Roll scenarios: "Roll short (...)" (same-strike calendar), "Roll short to $X (same exp, ...)" (vertical-shaped), "Roll short to $X (new-exp, ...)" (diagonal), and "Reset to $X calendar" (4-leg). Non-roll scenarios: "Hold", "Close now", "Close short only", "Close all", "Convert to calendar", "Add ...".

- [ ] **Step 1: Add `IsRoll` field to the `Scenario` record**

Replace lines 277–286 of `AnalyzePositionCommand.cs`:

```csharp
internal sealed record Scenario(
    string Name,
    string ActionSummary,
    decimal CashImpactPerContract,      // per-contract
    decimal ProjectedValuePerContract,  // per-contract
    decimal TotalPnLPerContract,        // per-contract
    decimal BPDeltaPerContract,         // per-contract additional BP required (negative = BP frees up)
    int Qty,
    int DaysToTarget,                   // days from evaluation date to this scenario's target date; used to rank P&L per day
    string Rationale,
    bool IsRoll = false);               // true iff this scenario closes an existing leg and opens a new one (consulted by BuildReproductionCommands)
```

- [ ] **Step 2: Thread `isRoll` through `NewScenario` and `NewScenarioSpread`**

Replace lines 770–786 of `AnalyzePositionCommand.cs`:

```csharp
private static Scenario NewScenario(string name, PositionSnapshot longLeg, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false)
{
	var initialDebit = longLeg.CostBasis;
	var cashPerContract = cashNow * 100m;
	var valuePerContract = valueAtTarget * 100m;
	var totalPerContract = valuePerContract + cashPerContract - initialDebit * 100m;
	return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, longLeg.Qty, daysToTarget, rationale, isRoll);
}

private static Scenario NewScenarioSpread(string name, IReadOnlyList<PositionSnapshot> legs, string actionSummary, decimal cashNow, decimal valueAtTarget, decimal bpDeltaPerContract, int daysToTarget, string rationale, bool isRoll = false)
```

(The `NewScenarioSpread` body on lines 779–786 keeps its current logic; its final `return new Scenario(...)` must also pass `isRoll`.)

Find `NewScenarioSpread`'s body (it starts around line 779) and replace its `return new Scenario(...)` line so the last positional argument becomes `isRoll`. In this file the body currently returns `new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, qty, daysToTarget, rationale);`. Change to:

```csharp
return new Scenario(name, actionSummary, cashPerContract, valuePerContract, totalPerContract, bpDeltaPerContract, qty, daysToTarget, rationale, isRoll);
```

- [ ] **Step 3: Thread `isRoll` through `EmitFullAndPartial`**

In `AnalyzePositionCommand.cs`, replace the `EmitFullAndPartial` signature (line 715–726) and both `list.Add(new Scenario(...))` calls inside the body (lines 736–745 and 756–765). The two `Scenario` constructions each gain a trailing `IsRoll: isRoll` named argument.

Replace lines 715–766 with:

```csharp
/// <summary>Appends a full-quantity scenario to the list. If the full BP delta exceeds available
/// cash AND there's a positive max-fundable partial quantity, also appends a partial variant.
/// In the partial, the unchanged portion is valued at its natural terminal date (the hold projection),
/// so the mix doesn't double-count time decay.</summary>
private static void EmitFullAndPartial(
	List<Scenario> list,
	IReadOnlyList<PositionSnapshot> legs,
	decimal? availableCash,
	string name,
	string actionSummary,
	decimal cashPerShareOfChange,
	decimal newProjectedPerShare,
	decimal unchangedProjectedPerShare,
	decimal bpPerContract,
	int daysToTarget,
	string rationale,
	bool isRoll = false)
{
	var fullQty = legs[0].Qty;
	var initialDebitPerShare = legs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.CostBasis);
	var initialDebitPerContract = initialDebitPerShare * 100m;

	// Full scenario.
	var fullCashPerContract = cashPerShareOfChange * 100m;
	var fullProjectedPerContract = newProjectedPerShare * 100m;
	var fullTotalPerContract = fullProjectedPerContract + fullCashPerContract - initialDebitPerContract;
	list.Add(new Scenario(
		name,
		actionSummary.Replace("{qty}", fullQty.ToString()),
		CashImpactPerContract: fullCashPerContract,
		ProjectedValuePerContract: fullProjectedPerContract,
		TotalPnLPerContract: fullTotalPerContract,
		BPDeltaPerContract: bpPerContract,
		Qty: fullQty,
		DaysToTarget: daysToTarget,
		Rationale: rationale,
		IsRoll: isRoll));

	// Partial variant: only emit if BP is positive and cash is constrained below full.
	if (!availableCash.HasValue || bpPerContract <= 0m) return;
	var maxPartial = (int)Math.Floor(availableCash.Value / bpPerContract);
	if (maxPartial <= 0 || maxPartial >= fullQty) return;

	// Per-contract-of-total values for the partial mix.
	var partialCashTotal = cashPerShareOfChange * 100m * maxPartial;
	var partialProjectedTotal = newProjectedPerShare * 100m * maxPartial + unchangedProjectedPerShare * 100m * (fullQty - maxPartial);
	var partialTotalPnL = partialCashTotal + partialProjectedTotal - initialDebitPerContract * fullQty;
	list.Add(new Scenario(
		$"{name} · partial {maxPartial}/{fullQty}",
		actionSummary.Replace("{qty}", maxPartial.ToString()),
		CashImpactPerContract: partialCashTotal / fullQty,
		ProjectedValuePerContract: partialProjectedTotal / fullQty,
		TotalPnLPerContract: partialTotalPnL / fullQty,
		BPDeltaPerContract: bpPerContract * maxPartial / fullQty,
		Qty: fullQty,
		DaysToTarget: daysToTarget,
		Rationale: $"execute on {maxPartial} contracts (${bpPerContract * maxPartial:N0} BP); hold remaining {fullQty - maxPartial} as original → ${unchangedProjectedPerShare:F2}/share at original exp",
		IsRoll: isRoll));
}
```

- [ ] **Step 4: Mark the three roll call sites and the reset call site with `isRoll: true`**

All four call sites invoke `EmitFullAndPartial`. Add `isRoll: true` as the final argument to each. Search for `EmitFullAndPartial(list, legs, settings.Cash,` in `AnalyzePositionCommand.cs` — four matches:

At the end of the "Roll short same-strike" call (ends around line 468), change the trailing `rationale: "..."` argument to be followed by `, isRoll: true`. Specifically:

```csharp
EmitFullAndPartial(list, legs, settings.Cash,
    name: $"Roll short ({newExp:MM-dd}, same strike)",
    actionSummary: $"BUY {shortLeg.Symbol} x{{qty}} @{FmtPrice(shortAskNow)}, SELL {newSym} x{{qty}} @{FmtPrice(newShortBid)}",
    cashPerShareOfChange: cashPerShare,
    newProjectedPerShare: newProjectedPerShare,
    unchangedProjectedPerShare: holdNetPerShare,
    bpPerContract: bpDelta,
    daysToTarget: dteNewShort,
    rationale: $"buy short @${shortAskNow:F2} ask, sell new @${newShortBid:F2} bid → net ${cashPerShare:+0.00;-0.00}/share; at new exp: ${newProjectedPerShare:F2}",
    isRoll: true);
```

Do the same for the "Roll short to $X (same exp...)" call (around line 497), the "Roll short to $X (new-exp ...)" call (around line 530), and the "Reset to $X calendar" call (around line 572). Each gets `isRoll: true` appended.

- [ ] **Step 5: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add AnalyzePositionCommand.cs
git commit -m "AnalyzePosition: add IsRoll flag to Scenario, mark roll and reset scenarios"
```

---

## Task 3: Rewrite `BuildReproductionCommands` — drop `--side`, split non-calendar rolls

**Files:**
- Modify: `AnalyzePositionCommand.cs` (lines 958–994, `BuildReproductionCommands` method)

The method currently builds `tradeLegs` (action:symbol:qty) and `analyzeLegs` (action:symbol:qty@price), then emits one combo `wa trade place` line with a net `--limit` and `--side`. The rewrite keeps `analyzeLegs` unchanged, drops `--side`, and — when `sc.IsRoll == true` and the legs aren't a same-strike calendar — emits one `wa trade place` line per leg with the per-leg price parsed from `sc.ActionSummary` as the individual `--limit`.

- [ ] **Step 1: Change the return type and rewrite the method body**

Replace lines 958–994 of `AnalyzePositionCommand.cs`:

```csharp
/// <summary>Converts a scenario ActionSummary like "BUY SYM x200 @0.305, SELL SYM2 x200 @0.44"
/// into reproducible commands: one or more 'wa trade place' lines for execution and a single
/// 'wa analyze trade' line for validation. For same-strike calendar rolls and non-roll scenarios,
/// one combo 'wa trade place' line is emitted with the net `--limit` derived from CashImpactPerContract.
/// For non-calendar rolls (diagonals, same-expiry-different-strike), Webull's combo engine rejects
/// the reversal, so two separate single-leg 'wa trade place' lines are emitted in the order the legs
/// appear in ActionSummary (close-first, open-second — a contract upheld by the scenario generators).
/// Each uses that leg's per-share price as its `--limit`. Returns (null, null) for hold/no-op scenarios.</summary>
private static (IReadOnlyList<string>? Trades, string? Analyze) BuildReproductionCommands(Scenario sc, AnalyzePositionSettings settings)
{
	if (string.IsNullOrWhiteSpace(sc.ActionSummary) || sc.ActionSummary == "—") return (null, null);
	var parts = sc.ActionSummary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	// Parse each "ACTION SYMBOL xQTY @PRICE" part into (action, symbol, qty, price).
	var legs = new List<(string Action, string Symbol, string Qty, string Price)>(parts.Length);
	foreach (var part in parts)
	{
		var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (tokens.Length != 4) return (null, null);
		var action = tokens[0].ToLowerInvariant();
		if (action != "buy" && action != "sell") return (null, null);
		legs.Add((action, tokens[1], tokens[2].TrimStart('x'), tokens[3].TrimStart('@')));
	}

	// Analyze-trade line: one combined command, per-leg @PRICE preserved.
	var analyzeLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@{l.Price}");
	var extras = new List<string>();
	if (!string.IsNullOrEmpty(settings.TickerPrice)) extras.Add($"--ticker-price {settings.TickerPrice}");
	if (!string.IsNullOrEmpty(settings.Date)) extras.Add($"--date {settings.Date}");
	var suffix = extras.Count > 0 ? " " + string.Join(" ", extras) : "";
	var analyze = $"wa analyze trade \"{string.Join(",", analyzeLegs)}\"{suffix}";

	// Split non-calendar rolls into per-leg orders so Webull's combo engine accepts them.
	var splittable = sc.IsRoll && legs.Count == 2 && !RollShape.IsSameStrikeCalendar(legs.Select(l => l.Symbol));
	if (splittable)
	{
		var trades = legs.Select(l => $"wa trade place --trade \"{l.Action}:{l.Symbol}:{l.Qty}\" --limit {l.Price}").ToList();
		return (trades, analyze);
	}

	// Combo line: legs without per-leg prices, net limit from CashImpactPerContract.
	var tradeLegs = legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}");
	var limit = Math.Abs(sc.CashImpactPerContract / 100m).ToString("F2", CultureInfo.InvariantCulture);
	var combo = $"wa trade place --trade \"{string.Join(",", tradeLegs)}\" --limit {limit}";
	return (new[] { combo }, analyze);
}
```

- [ ] **Step 2: Update the single caller of `BuildReproductionCommands`**

The caller is in `RenderScenarioTable` at around line 934. Today it destructures `(tradeCmd, analyzeCmd)` and emits at most one `↪ {tradeCmd}` line. Replace the three lines 934–938 with:

```csharp
var (tradeCmds, analyzeCmd) = BuildReproductionCommands(sc, settings);
if (tradeCmds != null)
	foreach (var cmd in tradeCmds)
		lines.Add(new Markup($"[grey50]↪ {Markup.Escape(cmd)}[/]"));
if (analyzeCmd != null)
	lines.Add(new Markup($"[grey50]↪ {Markup.Escape(analyzeCmd)}[/]"));
```

- [ ] **Step 3: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add AnalyzePositionCommand.cs
git commit -m "AnalyzePosition: drop --side, split non-calendar roll reproductions into two wa trade place lines"
```

---

## Task 4: Verify AnalyzePosition output

**Files:**
- None modified; CLI-based verification only.

A diagonal position with `--date` override gives Black-Scholes scenario pricing (no live quotes needed). Expected behaviors:

- **Same-strike calendar roll** scenario (e.g., "Roll short (..., same strike)") → one `wa trade place` line, no `--side`.
- **Non-calendar roll** scenario (e.g., "Roll short to $X (same exp, ...)" or "Roll short to $X (..., diagonal)") → two `wa trade place` lines (close first, open second), each with its own `--limit`, no `--side`.
- **Add / hold / close / convert** scenarios → one line, no `--side`.

- [ ] **Step 1: Run the CLI against a diagonal spec**

Pick a GME short call diagonal (short front-week, long back-week, different strikes) and evaluate on a fixed date so BS pricing is used:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- analyze position \
  "sell:GME260424C00025000:1@0.48,buy:GME260515C00026000:1@1.11" \
  --ticker-price 24.50 --date 2026-04-20 2>&1 | tee /tmp/wa_analyze_position_verify.txt
```

- [ ] **Step 2: Inspect the output for each scenario category**

Open `/tmp/wa_analyze_position_verify.txt` and confirm:

- No line containing `wa trade place` contains the substring `--side`.
- At least one scenario labelled `Roll short (..., same strike)` emits exactly one `wa trade place` line.
- At least one scenario labelled either `Roll short to $X (same exp, ...)` or `Roll short to $X (..., diagonal)` emits exactly two consecutive `wa trade place` lines whose first token after `--trade "` is `buy:` and second is `sell:`.
- Each split line carries its own `--limit <decimal>`.
- The final `wa analyze trade "..."` line is still present as a single combined command.

Run these greps to confirm:

```bash
# Must print no matches.
grep -E 'wa trade place .*--side' /tmp/wa_analyze_position_verify.txt

# Must find at least one "Roll short (..., same strike)" followed by exactly one `wa trade place`.
# Must find at least one non-calendar roll (either "same exp" non-calendar or "new-exp" diagonal)
# with two consecutive `wa trade place` lines.
grep -nE 'Roll short|wa trade place|wa analyze trade' /tmp/wa_analyze_position_verify.txt | head -40
```

- [ ] **Step 3: If any grep fails, stop and debug before committing further tasks**

No commit in this task (verification only).

---

## Task 5: Add `PricePerShare` to `ProposalLeg`

**Files:**
- Modify: `AI/ManagementProposal.cs` (record definition around line 21)

- [ ] **Step 1: Add the optional field**

Replace line 21 of `AI/ManagementProposal.cs`:

```csharp
internal record ProposalLeg(string Action, string Symbol, int Qty, decimal? PricePerShare = null);
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)` (default value keeps all existing constructors compiling).

- [ ] **Step 3: Commit**

```bash
git add AI/ManagementProposal.cs
git commit -m "ProposalLeg: add optional PricePerShare for per-leg roll pricing"
```

---

## Task 6: Populate `PricePerShare` in rules and scenario engine

**Files:**
- Modify: `AI/Rules/DefensiveRollRule.cs` (lines 40–44, leg construction)
- Modify: `AI/Rules/RollShortOnExpiryRule.cs` (lines 43–47, leg construction)
- Modify: `AI/ScenarioEngine.cs` (`EmitRoll` and `EmitReset`; the engine needs the price values passed in from call sites, or reconstructed inside — we pass them in)

The rule files already have the per-leg quotes in scope where they build the legs. `ScenarioEngine.EmitRoll` is called from `SpreadScenarios` (lines 168–?) where the quotes are also available; `EmitReset` likewise. Thread two `decimal` price parameters (`oldShortPrice`, `newShortPrice`, and for reset also `oldLongPrice`, `newLongPrice`) into both emitter methods.

- [ ] **Step 1: `DefensiveRollRule` — pass close at `oldQ.Ask`, open at `newQ.Bid`**

In `AI/Rules/DefensiveRollRule.cs`, the current leg construction (lines 40–44) happens before the quote lookup. Move leg construction to after the quote lookup and populate `PricePerShare`. Replace the block from line 40 (the `var legs = new[] { ... };`) through the end of the `quote unavailable` branch at line 59 with:

```csharp
// Look up quotes to estimate the net. If missing, we still emit as AlertOnly (legs get no prices).
if (!ctx.Quotes.TryGetValue(shortLeg.Symbol, out var oldQ) || !ctx.Quotes.TryGetValue(newSymbol, out var newQ) ||
    oldQ.Ask == null || newQ.Bid == null)
{
	var alertLegs = new[]
	{
		new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty),
		new ProposalLeg("sell", newSymbol, shortLeg.Qty)
	};
	return new ManagementProposal(
		Rule: "DefensiveRollRule",
		Ticker: position.Ticker,
		PositionKey: position.Key,
		Kind: ProposalKind.AlertOnly,
		Legs: alertLegs,
		NetDebit: 0m,
		Rationale: $"spot ${spot:F2} within {_config.SpotWithinPctOfShortStrike}% of short strike ${shortLeg.Strike:F2}, DTE {dte}. Quote unavailable for new symbol {newSymbol}."
	);
}

var legs = new[]
{
	new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty, oldQ.Ask),   // close the old short at ask
	new ProposalLeg("sell", newSymbol, shortLeg.Qty, newQ.Bid)          // open the new short at bid
};
```

The remaining logic (netCredit calculation, isCredit, kind/rationale, final `return new ManagementProposal(...)`) stays the same. Make sure the final `ManagementProposal` constructor still references `legs` (the one with prices).

- [ ] **Step 2: `RollShortOnExpiryRule` — pass close at `oldQ.Ask`, open at `newQ.Bid`**

In `AI/Rules/RollShortOnExpiryRule.cs`, replace lines 43–47:

```csharp
var legs = new[]
{
	new ProposalLeg("buy", shortLeg.Symbol, shortLeg.Qty, oldQ.Ask),
	new ProposalLeg("sell", newSymbol, shortLeg.Qty, newQ.Bid)
};
```

- [ ] **Step 3: `ScenarioEngine.EmitRoll` — add price parameters and populate legs**

In `AI/ScenarioEngine.cs`, the current `EmitRoll` signature (line 412) is:

```csharp
private static void EmitRoll(List<ScenarioResult> list, int fullQty, string name, string oldShortSym, string newShortSym, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
```

Add two parameters `decimal oldShortPrice, decimal newShortPrice` after `newShortSym`, and use them in both `new ProposalLeg(...)` calls inside (lines 420 and 432). New signature:

```csharp
private static void EmitRoll(List<ScenarioResult> list, int fullQty, string name, string oldShortSym, string newShortSym, decimal oldShortPrice, decimal newShortPrice, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
```

Replace both `new[] { new ProposalLeg("buy", oldShortSym, fullQty), new ProposalLeg("sell", newShortSym, fullQty) }` blocks with:

```csharp
new[] { new ProposalLeg("buy", oldShortSym, fullQty, oldShortPrice), new ProposalLeg("sell", newShortSym, fullQty, newShortPrice) }
```

And the partial's `new[] { new ProposalLeg("buy", oldShortSym, maxPartial), new ProposalLeg("sell", newShortSym, maxPartial) }` with:

```csharp
new[] { new ProposalLeg("buy", oldShortSym, maxPartial, oldShortPrice), new ProposalLeg("sell", newShortSym, maxPartial, newShortPrice) }
```

Then update all three `EmitRoll(` call sites in the same file to pass the prices (the old short is always bought back at `shortAskNow`; the new short is sold at the local `newShortBid*` variable in scope):

- Line ~240 ("Roll short (..., same strike)") — add `oldShortPrice: shortAskNow, newShortPrice: newShortBid,` after `newSym,`.
- Line ~266 ("Roll short to $X (same exp, ...)") — add `oldShortPrice: shortAskNow, newShortPrice: newShortBidSameExp,` after `sameExpSym,`.
- Line ~291 ("Roll short to $X (new-exp, ...)") — add `oldShortPrice: shortAskNow, newShortPrice: newShortBid,` after `newSym,`.

- [ ] **Step 4: `ScenarioEngine.EmitReset` — add four price parameters and populate legs**

The `EmitReset` signature (line 438) is:

```csharp
private static void EmitReset(List<ScenarioResult> list, int fullQty, string name, string oldShortSym, string oldLongSym, string newShortSym, string newLongSym, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
```

Add four price parameters after `newLongSym`:

```csharp
private static void EmitReset(List<ScenarioResult> list, int fullQty, string name, string oldShortSym, string oldLongSym, string newShortSym, string newLongSym, decimal oldShortPrice, decimal oldLongPrice, decimal newShortPrice, decimal newLongPrice, decimal cashPerShareOfChange, decimal newProjectedPerShare, decimal unchangedProjectedPerShare, decimal bpPerContract, decimal initialDebitPerShare, int daysToTarget, decimal? availableCash, string rationale)
```

In both the full and partial `new[] { ... }` leg arrays, populate the fourth `PricePerShare` argument on each `ProposalLeg` constructor call. The 4 legs are `buy oldShortSym` (price = `oldShortPrice`), `sell oldLongSym` (price = `oldLongPrice`), `buy newLongSym` (price = `newLongPrice`), `sell newShortSym` (price = `newShortPrice`).

Update the single `EmitReset(` call site (around line 327) to pass the four prices. Add `oldShortPrice: shortAskNow, oldLongPrice: longBidNow, newShortPrice: newShortBid, newLongPrice: newLongAsk,` after the four symbol arguments (`shortLeg.Symbol, longLeg.Symbol, newShortSym, newLongSym,`).

- [ ] **Step 5: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add AI/Rules/DefensiveRollRule.cs AI/Rules/RollShortOnExpiryRule.cs AI/ScenarioEngine.cs
git commit -m "AI: populate ProposalLeg.PricePerShare in rules and scenario engine"
```

---

## Task 7: Rewrite `ProposalSink.WriteConsole` — drop `--side`, split non-calendar rolls

**Files:**
- Modify: `AI/Output/ProposalSink.cs` (lines 76–86, the post-proposal command-hint block)

The current block emits one `wa trade place` and one `wa analyze trade` line for every non-AlertOnly proposal. The rewrite:

1. Drop `--side` unconditionally.
2. If `p.Kind == ProposalKind.Roll` AND the legs aren't a same-strike calendar AND every leg has a non-null `PricePerShare` → emit two single-leg `wa trade place` lines (one per leg, in order) each with its leg's `PricePerShare` as `--limit`.
3. Otherwise → emit one combo `wa trade place` line with the net `--limit` derived from `p.NetDebit / 100m` (current behavior minus `--side`).

- [ ] **Step 1: Rewrite the command-hint block**

Replace lines 76–86 of `AI/Output/ProposalSink.cs`:

```csharp
if (p.Kind != ProposalKind.AlertOnly && p.Legs.Count > 0)
{
	var analyzeArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}@MID"));

	// Non-calendar rolls get split into per-leg orders so Webull's combo engine accepts the reversal.
	// Requires every leg to carry PricePerShare; otherwise fall back to the combo line.
	var canSplit = p.Kind == ProposalKind.Roll
		&& p.Legs.Count == 2
		&& p.Legs.All(l => l.PricePerShare.HasValue)
		&& !RollShape.IsSameStrikeCalendar(p.Legs.Select(l => l.Symbol));

	if (canSplit)
	{
		foreach (var leg in p.Legs)
		{
			var legLimit = leg.PricePerShare!.Value.ToString("F2", CultureInfo.InvariantCulture);
			AnsiConsole.MarkupLine($"  [dim]wa trade place --trade \"{Markup.Escape($"{leg.Action}:{leg.Symbol}:{leg.Qty}")}\" --limit {legLimit}[/]");
		}
	}
	else
	{
		var tradesArg = string.Join(",", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var limit = Math.Abs(p.NetDebit / 100m).ToString("F2", CultureInfo.InvariantCulture);
		AnsiConsole.MarkupLine($"  [dim]wa trade place --trade \"{Markup.Escape(tradesArg)}\" --limit {limit}[/]");
	}

	AnsiConsole.MarkupLine($"  [dim]wa analyze trade \"{Markup.Escape(analyzeArg)}\"[/]");
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add AI/Output/ProposalSink.cs
git commit -m "ProposalSink: drop --side, split non-calendar roll proposals into two wa trade place lines"
```

---

## Task 8: Verify AI path output

**Files:**
- None modified; CLI-based verification only.

Use `wa ai once` against a position that will trigger `DefensiveRollRule` (a diagonal proposal — different strike than existing short, different expiry). Config and input data already exist in `data/ai-config.json` and `data/orders.jsonl`. The goal is to confirm `ProposalSink` output follows the new rules.

- [ ] **Step 1: Run `wa ai once` capturing output**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- ai once 2>&1 | tee /tmp/wa_ai_once_verify.txt
```

If `ai once` requires live data or credentials and fails in the verification sandbox, fall back to `wa ai replay --since <date> --until <date>` against a range where a defensive roll was known to trigger (see `2026-04-22-technical-filter-and-replay-comparison-design.md` for replay setup conventions in this repo).

- [ ] **Step 2: Confirm no `--side` and that Roll proposals split**

```bash
# Must print no matches.
grep -E 'wa trade place .*--side' /tmp/wa_ai_once_verify.txt

# List each trade place line — for a Roll proposal whose legs are a non-calendar pair,
# expect two consecutive lines under the same proposal header.
grep -nE 'wa trade place|wa analyze trade|^\[.+\] [A-Z]' /tmp/wa_ai_once_verify.txt | head -40
```

If no diagonal Roll proposal fires in the given run, construct a hand-rolled `OpenPosition` fixture and feed it through replay, or accept that the codepath is exercised only by Task 7's unit reasoning — record this in the commit message of the final task.

- [ ] **Step 3: Record results**

No commit in this task. If issues are found, address them in the relevant earlier task and re-run.

---

## Task 9: Final integration sanity check

**Files:**
- None modified; CLI-based verification only.

- [ ] **Step 1: Build, confirm clean**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -5
```

Expected: `Build succeeded.` and `0 Error(s)`.

- [ ] **Step 2: Confirm the combined greps across both verification outputs still show no `--side`**

```bash
grep -E 'wa trade place .*--side' /tmp/wa_analyze_position_verify.txt /tmp/wa_ai_once_verify.txt 2>/dev/null
```

Expected: no output (all instances of `--side` gone from reproduction emissions).

- [ ] **Step 3: Show the final git log for the branch**

```bash
git log --oneline origin/master..HEAD
```

Expected: commits from Tasks 1, 2, 3, 5, 6, 7 listed in order.

---

## Out of Scope — not in this plan

- Splitting 4-leg `Reset to $X calendar` scenarios. They keep the single combo line. If Webull rejects them in production, handle in a follow-up.
- Any change to `wa trade place` itself (no quote fetching, no OTO loop, no new flags).
- Any change to the `wa analyze trade` reproduction line — it stays combined in both emitters.
