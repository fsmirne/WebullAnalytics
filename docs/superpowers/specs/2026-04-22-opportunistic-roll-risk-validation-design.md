# Design: OpportunisticRollRule — Risk-Aware Roll Validation

**Date:** 2026-04-22
**Scope:** Replace the pure theta-maximisation logic in `OpportunisticRollRule` with a four-gate validation pass that checks each candidate scenario for OTM safety, break-even soundness, and delta impact before accepting it as a proposal.

---

## Background

The current rule selects the highest-theta candidate from `ScenarioEngine` without checking whether the resulting position is safe at current spot. This caused a 25.5P → 26P roll proposal when GME was at 25.66 — the new short was ITM, and RSI at 67 with a +5% daily move made a bullish assumption risky. The fix is to gate each candidate through four sequential checks before accepting it as `topFundable`. Any failure skips to the next candidate; if nothing passes, no proposal fires (silent skip, no `AlertOnly`).

---

## 1. Validation Flow

In `OpportunisticRollRule.Evaluate`, the existing loop that finds `topFundable` gains four ordered checks before accepting a scenario:

```
for each scenario (Roll kind only):
    1. OTM guard          → new short must be OTM; block if ITM
    2. OTM buffer         → spot must clear adjustedOtmPct from new short strike
    3. Break-even check   → position must be profitable at current spot at short expiry
    4. Delta change cap   → |proposedDelta| ≤ |currentDelta| × (1 + maxDeltaIncreasePct/100)
    → if all pass, accept as topFundable
```

Non-Roll scenarios (Close, AlertOnly) bypass these checks entirely — they have no new short leg to evaluate.

To identify the new short leg in a Roll proposal: scan `ScenarioResult.ProposalLegs` for the leg with `Action == "sell"`. Parse its OCC symbol (`ParsingHelpers.ParseOptionSymbol`) to get strike, expiry, and callPut.

---

## 2. OTM Guard

Simplest check — block any scenario where the new short leg is in-the-money:

```
put:  newShortStrike >= spot  → block
call: newShortStrike <= spot  → block
```

If the new short's OCC symbol cannot be parsed, skip the scenario (fail-closed).

---

## 3. OTM Buffer with Technical Adjustment

The new short must be a configurable minimum distance OTM. The base buffer widens when technical conditions are extended:

```
adjustedOtmPct = baseOtmBufferPct × (1 + |compositeScore| × technicalBufferMultiplier)
```

Where `compositeScore` is `TechnicalBias.Score` if the ticker has a signal in `ctx.TechnicalSignals`; 0.0 if not.

Distance check:
```
put:  (spot − newShortStrike) / spot  < adjustedOtmPct / 100  → block
call: (newShortStrike − spot) / spot  < adjustedOtmPct / 100  → block
```

**Default config values:**
- `baseOtmBufferPct = 2.0` (2% OTM required at neutral technicals)
- `technicalBufferMultiplier = 1.5`

**Example (GME at 25.66, score = +0.08):**
- `adjustedOtmPct = 2.0 × (1 + 0.08 × 1.5) = 2.0 × 1.12 = 2.24%`
- Minimum OTM distance required: 25.66 × 0.0224 = $0.57
- New short must be ≤ $25.09 for a put — a 26P at 25.66 fails the OTM guard before even reaching this check

**Example (score = −0.50, strongly bearish):**
- `adjustedOtmPct = 2.0 × (1 + 0.50 × 1.5) = 2.0 × 1.75 = 3.5%`

---

## 4. Break-Even Check at Current Spot

At the new short leg's expiry date, the resulting position is the existing **long** legs plus the **new short** leg. Compute the net value per share at current spot:

**Long legs** (unchanged by the roll):
- Use `OptionMath.BlackScholes(spot, strike, remainingYears, riskFreeRate, iv, callPut)`
- `remainingYears = (longExpiry − newShortExpiry).TotalDays / 365.0`
- IV: live from `ctx.Quotes[longLeg.Symbol].ImpliedVolatility` if available; else `IvDefaultPct / 100`

**New short leg** (at its own expiry, time = 0):
- Value = `OptionMath.Intrinsic(spot, newShortStrike, callPut)` (no time value at expiry)

**IV for new short leg** (used in the buffer check only — the intrinsic at expiry is exact):
- Not needed for the break-even check itself; intrinsic is exact at T=0

**Net value per share:**
```
netAtSpot = Σ(long leg BS values) − newShortIntrinsic − position.AdjustedNetDebit
```

Require `netAtSpot ≥ 0`. If the position would be at a loss at current spot at short expiry, skip.

Note: `position.AdjustedNetDebit` is the cumulative per-share cost basis including prior rolls.

---

## 5. Delta Change Cap

Compute net position delta before and after the roll using Black-Scholes delta.

**Add to `OptionMath`:**
```csharp
internal static decimal Delta(decimal spot, decimal strike, double timeYears, double riskFreeRate, decimal iv, string callPut)
{
    if (timeYears <= 0) return callPut == "C" ? (spot > strike ? 1m : 0m) : (spot < strike ? -1m : 0m);
    double s = (double)spot, k = (double)strike, sigma = (double)iv, t = timeYears, r = riskFreeRate;
    double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2.0) * t) / (sigma * Math.Sqrt(t));
    return callPut == "C" ? (decimal)NormalCdf(d1) : (decimal)(NormalCdf(d1) - 1.0);
}
```

**Current position delta** (computed from `position.Legs`):
```
Σ over existing legs:
    +Delta(leg) for long legs
    −Delta(leg) for short legs
```

Use `ctx.Now` to compute `timeYears` for each existing leg.

**Proposed position delta** (after the roll):
- Same as current, but replace the old short leg's contribution with the new short leg's delta
- Old short leg is identified by matching its symbol to the BUY leg in `ProposalLegs`

**Block condition:**
```
|proposedDelta| > |currentDelta| × (1 + maxDeltaIncreasePct / 100)  → block
```

**Default config value:**
- `maxDeltaIncreasePct = 25` (roll may increase delta exposure by up to 25%)

IV for delta computation:
- Existing legs: live from `ctx.Quotes` if available; else `IvDefaultPct / 100`
- New short leg: live from `ctx.Quotes[newShortSymbol]` if available (quote source may have it); else proxy from the old short leg's IV; else `IvDefaultPct / 100`

---

## 6. Config Changes

Add four fields to `OpportunisticRollConfig` in `AIConfig.cs`:

```json
"opportunisticRoll": {
    "enabled": true,
    "minImprovementPerDayPerContract": 0.50,
    "ivDefaultPct": 40,
    "strikeStep": 0.50,
    "baseOtmBufferPct": 2.0,
    "technicalBufferMultiplier": 1.5,
    "maxDeltaIncreasePct": 25.0,
    "technicalFilter": { ... }
}
```

Validation in `AIConfigLoader.Validate`:
- `baseOtmBufferPct ≥ 0`
- `technicalBufferMultiplier ≥ 0`
- `maxDeltaIncreasePct ≥ 0`

---

## 7. Rationale Enhancement

When a proposal fires, append a safety summary to the rationale so the output is auditable:

```
[OTM: 2.6% (adj 2.24% req), BE: +$0.08/sh, Δ: −0.12→−0.09]
```

Fields:
- `OTM`: actual OTM distance as pct of spot
- `BE`: net value per share at current spot at short expiry (how much profit margin at today's price)
- `Δ`: current delta → proposed delta (both rounded to 2 dp)

---

## 8. File Changes

| File | Change |
|------|--------|
| `OptionMath.cs` | Add `Delta(spot, strike, timeYears, riskFreeRate, iv, callPut)` method |
| `AI/AIConfig.cs` | Add `BaseOtmBufferPct`, `TechnicalBufferMultiplier`, `MaxDeltaIncreasePct` to `OpportunisticRollConfig`; add validation |
| `AI/Rules/OpportunisticRollRule.cs` | Add four-gate validation loop; enhance rationale |
| `ai-config.example.json` | Add three new fields with defaults |

---

## 9. Out of Scope

- Per-ticker buffer overrides
- Probability-weighted expected P&L (Approach C) — deferred pending results
- Automatic threshold tuning from replay outcomes
- Changes to any other rule
