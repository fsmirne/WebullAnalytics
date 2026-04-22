# Design: Technical Filter for OpportunisticRollRule + Replay Comparison

**Date:** 2026-04-22  
**Scope:** Two independent improvements to the AI rule pipeline:
1. A composite technical bias signal that gates `OpportunisticRollRule` based on market direction
2. Improved replay agreement classification and inline fill display

---

## 1. Technical Signal Pipeline

### 1.1 `TechnicalBias` record

```csharp
internal sealed record TechnicalBias(
    decimal Score,          // composite, −1 (max bearish) to +1 (max bullish)
    decimal SmaScore,       // (SMA5/SMA20 − 1) clamped to [−1, +1]
    decimal RsiScore,       // (RSI14 − 50) / 50
    decimal MomentumScore   // N-day % return, clamped to [−1, +1]
)
{
    public bool IsAdverse(string callPut, decimal bullishThreshold, decimal bearishThreshold) =>
        callPut == "C" ? Score >= bullishThreshold : Score <= bearishThreshold;
}
```

When insufficient price history is available, the pipeline omits the ticker from `TechnicalSignals`. Rules treat a missing entry as neutral (no block).

### 1.2 `TechnicalIndicators` static class

Pure math only — no I/O. All methods return null when the close list is too short.

| Method | Input requirement | Computation |
|--------|------------------|-------------|
| `ComputeSmaScore(closes)` | ≥ 20 closes | `(SMA5 / SMA20 − 1)` clamped to `[−1, +1]` |
| `ComputeRsiScore(closes)` | ≥ 15 closes | Wilder RSI(14); `(RSI − 50) / 50` |
| `ComputeMomentumScore(closes, days)` | ≥ `days + 1` closes | `(close[-1] / close[-1-days] − 1)` clamped to `[−1, +1]` |
| `Compute(closes, config)` | — | Weighted average of all three; weights normalized internally |

SMA5 = average of last 5 closes. SMA20 = average of last 20 closes. RSI uses Wilder smoothing (initial average gain/loss seed, then exponential). All closes are ordered oldest-first.

### 1.3 `EvaluationContext` change

One new property added to the existing record:

```csharp
IReadOnlyDictionary<string, TechnicalBias> TechnicalSignals
```

Keyed by ticker (same casing as `UnderlyingPrices`). Empty dict is valid — rules treat missing entries as neutral.

### 1.4 Pipeline injection

Both pipelines compute signals before building `EvaluationContext`:

**Live** (`AICommand.ExecuteAsync`, `WatchLoop` tick loop):  
- Call `HistoricalPriceCache.GetRecentClosesAsync(ticker, lookbackDays, asOf: DateTime.Now, cancellation)` per configured ticker  
- `HistoricalPriceCache` already disk-caches Yahoo daily closes; live mode seeds it the same way (one fetch per ticker per session)  
- Compute `TechnicalBias` via `TechnicalIndicators.Compute`  
- Build `TechnicalSignals` dict, pass into `EvaluationContext` constructor  

**Replay** (`ReplayRunner.RunAsync`):  
- Same `HistoricalPriceCache` instance already exists in the runner; reuse it  
- Compute signals per step using closes up to (but not including) the step date — no look-ahead  
- Pass into `EvaluationContext` each step  

`HistoricalPriceCache` needs one new method: `GetRecentClosesAsync(ticker, count, asOf, cancellation) → IReadOnlyList<decimal>` — returns the last `count` daily closes on or before `asOf`, oldest-first.

### 1.5 `OpportunisticRollRule` changes

After resolving spot price and before calling `ScenarioEngine.Evaluate`:

```csharp
if (_config.TechnicalFilter.Enabled
    && ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias)
    && bias.IsAdverse(callPut, _config.TechnicalFilter.BullishBlockThreshold, _config.TechnicalFilter.BearishBlockThreshold))
    return null;
```

`callPut` is taken from the position's short leg. The rationale emitted when the rule fires includes the composite score for transparency.

---

## 2. Config

New `TechnicalFilterConfig` nested inside `OpportunisticRollConfig` in `AIConfig.cs`:

```json
"opportunisticRoll": {
    "enabled": true,
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
}
```

Weights are relative; they are normalized to sum to 1.0 internally. Setting a weight to 0 disables that indicator. `lookbackDays` must be ≥ 20 (required for SMA20); `AIConfigLoader.Validate` enforces this and returns an error string if violated.

---

## 3. Replay Comparison

### 3.1 Four-way agreement classifier

`ClassifyAgreement(proposal, step, allTrades)` replaces the current Phase-1 heuristic:

| Classification | Condition |
|---------------|-----------|
| **match** | Every proposed leg (OCC symbol + buy/sell direction) has a corresponding same-day fill with matching symbol and action on the same ticker |
| **divergent** | At least one fill shares a leg OCC symbol with the position (i.e., the same position was managed) but the full set doesn't satisfy match |
| **partial** | Same-day fills exist on the same ticker, but none overlap with any proposed leg OCC symbol |
| **miss** | No same-day fills on the proposal's ticker |

The classifier extracts proposed OCC symbols from `ManagementProposal.Legs`, scans `_allTrades` for `t.Timestamp.Date == step.Date` and `t.MatchKey` containing the ticker, then applies the four conditions in order (match → divergent → partial → miss).

### 3.2 Inline fill display

In replay mode, after each proposal block, `ReplayRunner` appends a dim line with the actual fills and classification tag. This is rendered directly by `ReplayRunner` after `ProposalSink.Emit` returns — no changes to `ProposalSink` needed.

Examples:

```
  ↳ actual: BUY GME260424C00025000 x239, SELL GME260515C00025000 x239  [match]
  ↳ actual: no fills on this position  [miss]
  ↳ actual: BUY GME260424C00024500 x100  [divergent — proposed x260 to May-01]
  ↳ actual: SELL GME260501C00026000 x50 (different position)  [partial]
```

The fill detail line lists the same-day fills relevant to the proposal's ticker, truncated to the most relevant legs if there are more than three.

---

## 4. File changes summary

| File | Change |
|------|--------|
| `AI/TechnicalBias.cs` | New record |
| `AI/TechnicalIndicators.cs` | New static class |
| `AI/EvaluationContext.cs` | Add `TechnicalSignals` property |
| `AI/Replay/HistoricalPriceCache.cs` | Add `GetRecentClosesAsync` method |
| `AI/AIConfig.cs` | Add `TechnicalFilterConfig` + wire into `OpportunisticRollConfig` |
| `AI/Rules/OpportunisticRollRule.cs` | Read `TechnicalSignals`, block if adverse |
| `AI/AICommand.cs` | Compute signals before building context |
| `AI/WatchLoop.cs` | Compute signals before building context each tick |
| `AI/Replay/ReplayRunner.cs` | Compute signals per step; improved `ClassifyAgreement`; inline fill display |
| `ai-config.example.json` | Add `technicalFilter` block |

---

## 5. Out of scope

- Threshold auto-tuning based on replay results
- Per-ticker indicator weight overrides
- Visual chart rendering of technical signals
- Scaling the improvement threshold instead of hard-blocking (deferred pending results)
