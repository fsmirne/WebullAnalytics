# OpportunisticRollRule Risk Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add four sequential safety gates to `OpportunisticRollRule` — OTM guard, OTM buffer with technical adjustment, break-even check at current spot, and delta change cap — so the rule only fires when a roll keeps risk similar and the premium is worth the risk change.

**Architecture:** Each Roll scenario from `ScenarioEngine` passes through four ordered checks before being accepted as `topFundable`. Any failure silently skips to the next candidate. Three new private static methods handle the math: `ComputeBreakEvenMargin`, `ComputeNetDelta`, `ComputeProposedDelta`. A new `PassesRollRiskChecks` method sequences the four gates and out-params a safety note appended to the rationale. `OptionMath.Delta` is added to provide BS delta.

**Tech Stack:** C# / .NET 10, Black-Scholes (existing `OptionMath` class), Spectre.Console for output.

---

## File Map

| File | Change |
|------|--------|
| `OptionMath.cs` | Add `Delta(spot, strike, timeYears, riskFreeRate, iv, callPut)` static method |
| `AI/AIConfig.cs` | Add `BaseOtmBufferPct`, `TechnicalBufferMultiplier`, `MaxDeltaIncreasePct` to `OpportunisticRollConfig`; add three validation checks |
| `AI/Rules/OpportunisticRollRule.cs` | Add `PassesRollRiskChecks`, `ComputeBreakEvenMargin`, `ComputeNetDelta`, `ComputeProposedDelta` private static methods; wire into candidate loop; enhance rationale |
| `ai-config.example.json` | Add three new fields under `opportunisticRoll` |

---

## Task 1: Add `OptionMath.Delta`

**Files:**
- Modify: `OptionMath.cs` (after the `BlackScholes` method, around line 34)

`OptionMath` is in `namespace WebullAnalytics`. Add the delta method directly after `BlackScholes`. It reuses the same `d1` formula — delta for a call is `N(d1)`, for a put is `N(d1) − 1`.

- [ ] **Step 1: Add the `Delta` method to `OptionMath.cs`**

Insert after the closing brace of `BlackScholes` (after line 34) and before the `NormalCdf` summary comment:

```csharp
/// <summary>Computes the Black-Scholes delta for a European option. Returns signed delta: positive for calls, negative for puts.</summary>
internal static decimal Delta(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv, string callPut)
{
    if (timeYears <= 0)
        return callPut == "C" ? (spot > strike ? 1m : 0m) : (spot < strike ? -1m : 0m);
    double s = (double)spot, k = (double)strike, sigma = (double)iv, t = timeYears, r = riskFreeRate;
    double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2.0) * t) / (sigma * Math.Sqrt(t));
    return callPut == "C" ? (decimal)NormalCdf(d1) : (decimal)(NormalCdf(d1) - 1.0);
}
```

- [ ] **Step 2: Build to verify**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3
```

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add OptionMath.cs
git commit -m "OptionMath: add Delta method (BS delta for calls and puts)"
```

---

## Task 2: Config fields + example JSON

**Files:**
- Modify: `AI/AIConfig.cs` (lines 53–61, `OpportunisticRollConfig` class and `Validate` method)
- Modify: `ai-config.example.json` (lines 28–43, `opportunisticRoll` block)

- [ ] **Step 1: Add three properties to `OpportunisticRollConfig` in `AI/AIConfig.cs`**

Replace the existing `OpportunisticRollConfig` class (lines 53–61):

```csharp
internal sealed class OpportunisticRollConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Minimum P&L-per-day-per-contract improvement (dollars) vs hold required to fire a proposal.</summary>
	[JsonPropertyName("minImprovementPerDayPerContract")] public decimal MinImprovementPerDayPerContract { get; set; } = 0.50m;
	[JsonPropertyName("ivDefaultPct")] public decimal IvDefaultPct { get; set; } = 40m;
	[JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0.50m;
	/// <summary>Minimum OTM distance required for the new short leg, as a percentage of spot, at neutral technicals.</summary>
	[JsonPropertyName("baseOtmBufferPct")] public decimal BaseOtmBufferPct { get; set; } = 2.0m;
	/// <summary>Scales the OTM buffer by (1 + |compositeScore| × multiplier) when technicals are extended.</summary>
	[JsonPropertyName("technicalBufferMultiplier")] public decimal TechnicalBufferMultiplier { get; set; } = 1.5m;
	/// <summary>Maximum allowed increase in net position delta magnitude after the roll, as a percentage of current delta.</summary>
	[JsonPropertyName("maxDeltaIncreasePct")] public decimal MaxDeltaIncreasePct { get; set; } = 25.0m;
	[JsonPropertyName("technicalFilter")] public TechnicalFilterConfig TechnicalFilter { get; set; } = new();
}
```

- [ ] **Step 2: Add three validation checks in `AIConfigLoader.Validate`**

In the `Validate` method, after the existing `var tf = ...` block (around line 165), and before `return null;`, the existing technical filter validation is already gated on `tf.Enabled`. Add these three checks for the roll config itself, before the `tf` block:

```csharp
var or = c.Rules.OpportunisticRoll;
if (or.BaseOtmBufferPct < 0m) return $"rules.opportunisticRoll.baseOtmBufferPct: must be ≥ 0, got {or.BaseOtmBufferPct}";
if (or.TechnicalBufferMultiplier < 0m) return $"rules.opportunisticRoll.technicalBufferMultiplier: must be ≥ 0, got {or.TechnicalBufferMultiplier}";
if (or.MaxDeltaIncreasePct < 0m) return $"rules.opportunisticRoll.maxDeltaIncreasePct: must be ≥ 0, got {or.MaxDeltaIncreasePct}";
```

- [ ] **Step 3: Update `ai-config.example.json`**

Replace the `opportunisticRoll` block:

```json
"opportunisticRoll": {
    "enabled": true,
    "minImprovementPerDayPerContract": 0.50,
    "ivDefaultPct": 40,
    "strikeStep": 0.50,
    "baseOtmBufferPct": 2.0,
    "technicalBufferMultiplier": 1.5,
    "maxDeltaIncreasePct": 25.0,
    "technicalFilter": {
        "enabled": true,
        "lookbackDays": 20,
        "smaWeight": 1.0,
        "rsiWeight": 1.0,
        "momentumWeight": 1.0,
        "momentumDays": 5,
        "bullishBlockThreshold": 0.25,
        "bearishBlockThreshold": -0.25
    }
},
```

- [ ] **Step 4: Build to verify**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3
```

Expected: `0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add AI/AIConfig.cs ai-config.example.json
git commit -m "AIConfig: add baseOtmBufferPct, technicalBufferMultiplier, maxDeltaIncreasePct to OpportunisticRollConfig"
```

---

## Task 3: Four-gate validation in `OpportunisticRollRule`

**Files:**
- Modify: `AI/Rules/OpportunisticRollRule.cs`

This is the main task. Add four private static helper methods and wire them into the candidate selection loop. The full rewritten file is shown below — implement it exactly as written.

### Context

`ParsingHelpers.ParseOptionSymbol(symbol)` returns `OptionParsed?`. The `OptionParsed` record has properties: `Root` (string), `ExpiryDate` (DateTime), `CallPut` (string), `Strike` (decimal).

`OptionContractQuote` has `ImpliedVolatility` (decimal? — fraction, e.g. 0.45 for 45% IV).

`OptionMath.RiskFreeRate` is a static `double` field (updated at runtime from market data; use it instead of a hardcoded constant).

`position.Legs` is `IReadOnlyList<PositionLeg>`. `PositionLeg` has: `Symbol`, `Side` (Side.Buy or Side.Sell), `Strike`, `Expiry` (DateTime?), `CallPut` (string?), `Qty`.

### Step-by-step

- [ ] **Step 1: Add four private static helper methods to `OpportunisticRollRule.cs`**

Add the following four methods inside the `OpportunisticRollRule` class, after the closing brace of `Evaluate`:

```csharp
/// <summary>Runs the four sequential safety gates on a Roll scenario. Returns false to skip, true to accept.
/// On true, safetyNote contains a formatted string to embed in the proposal rationale.</summary>
private static bool PassesRollRiskChecks(ScenarioEngine.ScenarioResult s, OpenPosition position, decimal spot, EvaluationContext ctx, OpportunisticRollConfig config, out string safetyNote)
{
    safetyNote = "";

    var newShortProposalLeg = s.ProposalLegs.FirstOrDefault(l => l.Action == "sell");
    var oldShortProposalLeg = s.ProposalLegs.FirstOrDefault(l => l.Action == "buy");
    if (newShortProposalLeg == null) return true; // no new short leg — not a roll we validate

    var newShort = ParsingHelpers.ParseOptionSymbol(newShortProposalLeg.Symbol);
    if (newShort == null) return false;

    // Gate 1: OTM guard — new short must be out-of-the-money.
    if (newShort.CallPut == "P" && newShort.Strike >= spot) return false;
    if (newShort.CallPut == "C" && newShort.Strike <= spot) return false;

    // Gate 2: OTM buffer adjusted by technical extension.
    var compositeScore = ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias) ? bias.Score : 0m;
    var requiredOtmFraction = config.BaseOtmBufferPct * (1m + Math.Abs(compositeScore) * config.TechnicalBufferMultiplier) / 100m;
    var actualOtmFraction = newShort.CallPut == "P"
        ? (spot - newShort.Strike) / spot
        : (newShort.Strike - spot) / spot;
    if (actualOtmFraction < requiredOtmFraction) return false;

    // Gate 3: Break-even at current spot at new short expiry.
    var beMargin = ComputeBreakEvenMargin(position, newShort, spot, ctx, config);
    if (beMargin < 0m) return false;

    // Gate 4: Delta change cap.
    var ivDefault = config.IvDefaultPct / 100m;
    var currentDelta = ComputeNetDelta(position.Legs, spot, ctx.Now, ctx.Quotes, ivDefault);
    var proposedDelta = ComputeProposedDelta(position.Legs, spot, ctx.Now, ctx.Quotes, ivDefault, oldShortProposalLeg?.Symbol, newShort, newShortProposalLeg.Symbol);
    var maxAllowedAbsDelta = Math.Abs(currentDelta) * (1m + config.MaxDeltaIncreasePct / 100m);
    if (Math.Abs(proposedDelta) > maxAllowedAbsDelta) return false;

    safetyNote = $" [OTM: {actualOtmFraction * 100m:F1}% (req {requiredOtmFraction * 100m:F1}%), BE: {(beMargin >= 0m ? "+" : "")}{beMargin:F2}/sh, Δ: {currentDelta:+0.00;-0.00}→{proposedDelta:+0.00;-0.00}]";
    return true;
}

/// <summary>Net value per share of the position at new short expiry at current spot.
/// Long legs are valued via Black-Scholes with their remaining time; new short is valued at intrinsic (T=0).</summary>
private static decimal ComputeBreakEvenMargin(OpenPosition position, OptionParsed newShort, decimal spot, EvaluationContext ctx, OpportunisticRollConfig config)
{
    var shortExpiry = newShort.ExpiryDate.Date;
    var ivDefault = config.IvDefaultPct / 100m;
    var longValue = 0m;
    foreach (var leg in position.Legs)
    {
        if (leg.Side != Side.Buy || leg.CallPut == null || !leg.Expiry.HasValue) continue;
        var remainingYears = Math.Max(0.0, (leg.Expiry.Value.Date - shortExpiry).TotalDays / 365.0);
        var iv = ctx.Quotes.TryGetValue(leg.Symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
            ? q.ImpliedVolatility.Value : ivDefault;
        longValue += OptionMath.BlackScholes(spot, leg.Strike, remainingYears, OptionMath.RiskFreeRate, iv, leg.CallPut);
    }
    var shortIntrinsic = OptionMath.Intrinsic(spot, newShort.Strike, newShort.CallPut);
    return longValue - shortIntrinsic - position.AdjustedNetDebit;
}

/// <summary>Net Black-Scholes delta of a set of legs at the given spot and time. Long legs contribute +delta, short legs -delta.</summary>
private static decimal ComputeNetDelta(IReadOnlyList<PositionLeg> legs, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal ivDefault)
{
    var netDelta = 0m;
    foreach (var leg in legs)
    {
        if (leg.CallPut == null || !leg.Expiry.HasValue) continue;
        var years = Math.Max(0.0, (leg.Expiry.Value.Date - asOf.Date).TotalDays / 365.0);
        var iv = quotes.TryGetValue(leg.Symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
            ? q.ImpliedVolatility.Value : ivDefault;
        var delta = OptionMath.Delta(spot, leg.Strike, years, OptionMath.RiskFreeRate, iv, leg.CallPut);
        netDelta += leg.Side == Side.Buy ? delta : -delta;
    }
    return netDelta;
}

/// <summary>Net delta after the roll: removes the old short leg's contribution and adds the new short's.</summary>
private static decimal ComputeProposedDelta(IReadOnlyList<PositionLeg> legs, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal ivDefault, string? oldShortSymbol, OptionParsed newShort, string newShortSymbol)
{
    var netDelta = ComputeNetDelta(legs, spot, asOf, quotes, ivDefault);

    // Undo the old short's contribution (it was subtracted as a short leg; add it back).
    if (oldShortSymbol != null)
    {
        var oldLeg = legs.FirstOrDefault(l => l.Symbol == oldShortSymbol);
        if (oldLeg != null && oldLeg.CallPut != null && oldLeg.Expiry.HasValue)
        {
            var years = Math.Max(0.0, (oldLeg.Expiry.Value.Date - asOf.Date).TotalDays / 365.0);
            var iv = quotes.TryGetValue(oldShortSymbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m
                ? q.ImpliedVolatility.Value : ivDefault;
            netDelta += OptionMath.Delta(spot, oldLeg.Strike, years, OptionMath.RiskFreeRate, iv, oldLeg.CallPut); // undo -delta
        }
    }

    // Add new short's contribution (-delta).
    var newYears = Math.Max(0.0, (newShort.ExpiryDate.Date - asOf.Date).TotalDays / 365.0);
    // IV for new short: live quote → old short's IV proxy → default
    decimal newIv;
    if (quotes.TryGetValue(newShortSymbol, out var newQ) && newQ.ImpliedVolatility.HasValue && newQ.ImpliedVolatility.Value > 0m)
        newIv = newQ.ImpliedVolatility.Value;
    else if (oldShortSymbol != null && quotes.TryGetValue(oldShortSymbol, out var oldQ) && oldQ.ImpliedVolatility.HasValue && oldQ.ImpliedVolatility.Value > 0m)
        newIv = oldQ.ImpliedVolatility.Value;
    else
        newIv = ivDefault;

    netDelta -= OptionMath.Delta(spot, newShort.Strike, newYears, OptionMath.RiskFreeRate, newIv, newShort.CallPut);
    return netDelta;
}
```

- [ ] **Step 2: Wire `PassesRollRiskChecks` into the candidate selection loop**

In `Evaluate`, replace the existing `topFundable` loop:

```csharp
// Find "Hold" baseline (highest-P&L-per-day scenario that involves no execution) and top
// fundable scenario (positive BPDelta ≤ availableCash, or BPDelta ≤ 0).
ScenarioEngine.ScenarioResult? hold = scenarios.FirstOrDefault(s => s.ProposalLegs.Count == 0);
ScenarioEngine.ScenarioResult? topFundable = null;
foreach (var s in scenarios)
{
    if (s.ProposalLegs.Count == 0) continue; // skip "hold"/alert-only
    var bpTotal = s.BPDeltaPerContract * s.Qty;
    if (bpTotal > 0m && availableCash > 0m && bpTotal > availableCash) continue;
    topFundable = s;
    break;
}
```

With:

```csharp
// Find "Hold" baseline (highest-P&L-per-day scenario that involves no execution) and top
// fundable scenario (positive BPDelta ≤ availableCash, or BPDelta ≤ 0) that passes risk checks.
ScenarioEngine.ScenarioResult? hold = scenarios.FirstOrDefault(s => s.ProposalLegs.Count == 0);
ScenarioEngine.ScenarioResult? topFundable = null;
var rollSafetyNote = "";
foreach (var s in scenarios)
{
    if (s.ProposalLegs.Count == 0) continue; // skip "hold"/alert-only
    var bpTotal = s.BPDeltaPerContract * s.Qty;
    if (bpTotal > 0m && availableCash > 0m && bpTotal > availableCash) continue;
    if (s.Kind == ProposalKind.Roll && !PassesRollRiskChecks(s, position, spot, ctx, _config, out rollSafetyNote)) continue;
    topFundable = s;
    break;
}
```

- [ ] **Step 3: Append `rollSafetyNote` to the rationale**

Replace:

```csharp
var rationale = $"optimizer: {topFundable.Name} projects ${topPerDay:+0.00;-0.00}/ct/day vs hold ${holdPerDay:+0.00;-0.00}/ct/day (Δ ${improvementPerDayPerContract:+0.00;-0.00}/ct/day over {topFundable.DaysToTarget}d){biasNote}. {topFundable.Rationale}";
```

With:

```csharp
var rationale = $"optimizer: {topFundable.Name} projects ${topPerDay:+0.00;-0.00}/ct/day vs hold ${holdPerDay:+0.00;-0.00}/ct/day (Δ ${improvementPerDayPerContract:+0.00;-0.00}/ct/day over {topFundable.DaysToTarget}d){biasNote}{rollSafetyNote}. {topFundable.Rationale}";
```

- [ ] **Step 4: Build to verify**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3
```

Expected: `0 Error(s)`

- [ ] **Step 5: Smoke test**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- ai replay --since 2026-04-21 --until 2026-04-21 2>&1
```

Expected: The 25.5P → 26P roll proposal from `OpportunisticRollRule` should no longer appear (blocked by Gate 1 — new short would be ITM at spot 25.66). If any proposal fires, the rationale should contain `[OTM: X.X% (req Y.Y%), BE: +$Z.ZZ/sh, Δ: A→B]`.

- [ ] **Step 6: Commit**

```bash
git add AI/Rules/OpportunisticRollRule.cs
git commit -m "OpportunisticRollRule: add four-gate roll risk validation (OTM guard, buffer, break-even, delta cap)"
```
