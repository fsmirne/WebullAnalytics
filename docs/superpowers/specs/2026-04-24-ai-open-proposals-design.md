# `ai` Open-Proposal Engine Design

**Date:** 2026-04-24
**Status:** Proposed

## Summary

Extend `wa ai once` and `wa ai watch` to emit **opening-trade proposals** alongside the existing management-rule proposals. When invoked, each command will — in addition to scoring management actions for open positions — scan the configured tickers, enumerate candidate entries across three structure families (long calendars/diagonals, short verticals, single long calls/puts), score each candidate under a probability-weighted expected-value model, soft-weight by technical bias, and surface the top candidates per ticker with numeric scores and human rationale.

The feature is read-only: it never places orders. Output is structured enough (JSONL with a `type: "open"` discriminator) that a future automation layer can consume it directly.

## Goals / Non-goals

**Goals:**
- Scan configured tickers every tick, enumerate opening candidates per structure, score them, emit top-N per ticker.
- Probability-weighted EV scoring using Black-Scholes (delta / POP / 5-point scenario grid).
- Soft directional-fit weighting driven by the existing composite technical bias.
- Cash-reserve-aware sizing; blocked proposals still emitted with the blocker tagged.
- Per-tick fingerprint dedup for console noise reduction without suppressing the JSONL log.
- Shared infrastructure with management rules: same config file, same tick loop, same quote pipeline, same log sink file (different schema).

**Non-goals:**
- No order placement. No path from the loop to the broker.
- No opening candidates for structures outside the three families listed (no iron condors, iron flies, ratios, box spreads).
- No automation / auto-execute.
- No per-ticker structure overrides (all configured tickers share the same `opener` config).
- No notifications (desktop / push / Slack).
- No IV-regime gating (the rank score already reflects IV via BS pricing).

## Command Surface

The surface of `ai once` and `ai watch` is unchanged apart from one new flag:

```
wa ai once  [existing options] [--no-open-proposals]
wa ai watch [existing options] [--no-open-proposals]
```

`--no-open-proposals` disables the opening-proposal pass for the current run, leaving management rules intact. Config key `opener.enabled` (default `true`) provides the persistent equivalent.

## Architecture

Six new files under `AI/Open/`:

- **`OpenProposal.cs`** — result type. Peer to `ManagementProposal`.
- **`CandidateEnumerator.cs`** — pure enumeration of structure skeletons per ticker.
- **`CandidateScorer.cs`** — computes EV / POP / score for a skeleton given quotes and bias.
- **`OpenCandidateEvaluator.cs`** — orchestrator: enumerate → phase-3 quote fetch → score → rank → truncate → cash-tag → dedup.
- **`OpenProposalSink.cs`** — JSONL + console output for open proposals.
- **`OpenerConfig.cs`** — new `opener` section types on `AIConfig`.

### Pipeline hook

Both `AIOnceCommand` and `AIWatchCommand` gain two extra steps after the existing `RuleEvaluator.Evaluate(...)` call:

```csharp
var mgmtResults = evaluator.Evaluate(ctx);
foreach (var r in mgmtResults) sink.Emit(r.Proposal, r.IsRepeat);

if (config.Opener.Enabled && !settings.NoOpenProposals)
{
    var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
    foreach (var p in openResults) openSink.Emit(p);
}
```

### Quote fetch

`AIPipelineHelper` stays as-is for phases 1–2. `OpenCandidateEvaluator` adds a **phase-3 fetch**: gather every OCC symbol referenced across all candidate skeletons for all tickers, diff against `ctx.Quotes`, fetch the missing ones in a single batched call via `IQuoteSource`. The fetched quotes are merged into a local snapshot used only by the scorer (the management-side `ctx.Quotes` is not mutated).

## Data Model

### `OpenProposal` record

```csharp
internal enum OpenStructureKind { LongCalendar, LongDiagonal, ShortPutVertical, ShortCallVertical, LongCall, LongPut }

internal sealed record OpenProposal(
    string Ticker,
    OpenStructureKind StructureKind,
    IReadOnlyList<ProposalLeg> Legs,       // reuses existing ProposalLeg
    int Qty,                                // sized to cash; 0 when CashReserveBlocked
    decimal DebitOrCreditPerContract,       // negative = debit paid; positive = credit received
    decimal MaxProfitPerContract,
    decimal MaxLossPerContract,             // negative (loss magnitude)
    decimal CapitalAtRiskPerContract,       // debit for longs/calendars; width×100 − credit for verticals
    IReadOnlyList<decimal> Breakevens,
    decimal ProbabilityOfProfit,            // [0, 1]
    decimal ExpectedValuePerContract,       // from 5-point scenario grid
    int DaysToTarget,                       // DTE of nearest leg expiry used in scoring
    decimal RawScore,                       // EV / DaysToTarget / CapitalAtRisk
    decimal BiasAdjustedScore,              // RawScore × (1 + α · bias · fit)
    int DirectionalFit,                     // +1 / 0 / −1
    string Rationale,                       // human-readable
    string Fingerprint,                     // sha1(ticker|kind|legs|qty)
    bool CashReserveBlocked = false,
    string? CashReserveDetail = null
);
```

### Structure → directional-fit table

| Structure | Fit |
|---|---|
| `LongCall` | +1 |
| `ShortPutVertical` (put credit / bull put) | +1 |
| `LongPut` | −1 |
| `ShortCallVertical` (call credit / bear call) | −1 |
| `LongCalendar`, `LongDiagonal` | 0 |

## Candidate Enumeration

`CandidateEnumerator.Enumerate(ticker, spot, asOf, opener)` yields one candidate skeleton per concrete (structure, strike(s), expiry(ies)) tuple. No quotes required at this stage.

### Long calendar / long diagonal (call and put sides)

- **Short-leg expiries:** the first Friday with DTE ∈ `[longCalendar.shortDteMin, longCalendar.shortDteMax]` (default [3, 10]).
- **Long-leg expiries:** standard 3rd-Friday monthlies with DTE ∈ `[longCalendar.longDteMin, longCalendar.longDteMax]` (default [21, 60]).
- **Strikes (calendar):** short_strike = long_strike, chosen from `BracketStrikes(spot, strikeStep) ∪ {spot ± strikeStep, spot ± 2·strikeStep}` — up to 5 distinct strikes after dedup.
- **Strikes (diagonal):** short strike one `strikeStep` away from long strike, both sides.
- **Sides:** call and put variants.
- **Filter:** skip candidates where the short strike is ITM by more than one `strikeStep` (bad entry for a debit calendar/diagonal).

### Short vertical credit spreads

- **Expiries:** Fridays with DTE ∈ `[shortVertical.dteMin, shortVertical.dteMax]` (default [3, 10]).
- **Widths:** `shortVertical.widthSteps` (default `[1, 2]`) × `strikeStep`.
- **Short-leg strike selection:** iterate strikes outward from ATM; a candidate is emitted when the short-leg Black-Scholes delta falls in `[shortVertical.shortDeltaMin, shortVertical.shortDeltaMax]` (default [0.15, 0.30]).
  - **Put credit spread:** short strike below spot; long strike = short − width.
  - **Call credit spread:** short strike above spot; long strike = short + width.

### Single long call / long put

- **Expiries:** up to two nearest standard monthlies with DTE ∈ `[longCallPut.dteMin, longCallPut.dteMax]` (default [21, 60]).
- **Strike:** iterate strikes; candidate emitted when BS delta magnitude falls in `[longCallPut.deltaMin, longCallPut.deltaMax]` (default [0.30, 0.60]).
- **Sides:** call and put variants.

### Post-enumeration bounds

- **`maxCandidatesPerStructurePerTicker`** (default 8) — truncates the lowest-scored candidates within each structure family for a given ticker, applied **after** scoring, **before** the cross-structure final rank.
- **`topNPerTicker`** (default 5) — the final count emitted per ticker per tick.

## Scoring

The scorer consumes a skeleton and the quote snapshot and produces a fully-populated `OpenProposal`.

### Step 1 — Resolve IV & bid/ask per leg

`LiveOrBsMid` and `LiveBidAsk` from `ScenarioEngine` are reused. IV preference: live implied vol → `ivDefaultPct` fallback. If any required leg lacks both a live quote and a computable BS fallback (e.g. ticker has no spot), the candidate is dropped.

### Step 2 — Cash impact and capital at risk

| Structure | Cash impact / contract | Capital at risk / contract |
|---|---|---|
| Long call / put | `−long_ask × 100` (debit) | `long_ask × 100` |
| Short vertical | `+(short_bid − long_ask) × 100` (credit) | `width × 100 − credit` |
| Long calendar / diagonal | `−(long_ask − short_bid) × 100` (debit) | `debit` |

### Step 3 — Probability of profit at the target date

Black-Scholes with ATM-leg IV, risk-neutral drift, T = DTE-to-short-leg-expiry / 365:

- **Long call:** `P(S_T > strike + debit_per_share) = N(d₂(breakeven))`.
- **Long put:** `P(S_T < strike − debit_per_share) = 1 − N(d₂(breakeven))`.
- **Short vertical (put credit):** `P(S_T > short_strike − credit_per_share)`.
- **Short vertical (call credit):** `P(S_T < short_strike + credit_per_share)`.
- **Calendar / diagonal:** `P(|S_T − K_short| / spot < opener.profitBandPct / 100)` (default band 5%). A crude-but-honest proxy for "finishes near short strike."

### Step 4 — Expected value via 5-point scenario grid

Scenarios placed at `S_T ∈ {spot·e^(−2σ), spot·e^(−σ), spot, spot·e^(+σ), spot·e^(+2σ)}` where `σ = IV_ATM · √(T)`. Weights = log-normal density at each grid point, renormalized to sum to 1. Drift is neutral (technical bias is applied separately in Step 5 — **not** folded into the drift; avoids double-counting).

At each grid point, compute P&L per contract:

- **Long call / put:** `intrinsic(S_T) × 100 − debit`.
- **Short vertical:** full payoff diagram at expiry: `credit − max(0, loss_at_S_T) × 100`.
- **Calendar / diagonal:** `long_BS(S_T, K_long, T_long_remaining, r, IV_long, callPut) × 100 − max(0, S_T − K_short) × 100 − debit` (put side analogous with `max(0, K_short − S_T)`). `T_long_remaining = (long_expiry − short_expiry) / 365`.

`EV = Σᵢ wᵢ · PnLᵢ`.

### Step 5 — Raw score and bias-adjusted score

```
RawScore          = EV / max(1, DaysToTarget) / CapitalAtRisk
BiasAdjustedScore = RawScore × (1 + α · technicalBiasScore · DirectionalFit)
```

Where `α = opener.directionalFitWeight` (default 0.5). Bias = 0 or fit = 0 → no adjustment. Calendars and diagonals are never bias-lifted.

### Step 6 — Size to available cash

```
reserve    = CashReserveHelper.ComputeReserve(config.CashReserve, accountValue)
freeCash   = max(0, accountCash − reserve)
maxQty     = floor(freeCash / CapitalAtRiskPerContract)
```

If `maxQty ≥ 1`: `Qty = min(maxQty, 10)` (cap at 10 per proposal to avoid runaway sizing; configurable as `opener.maxQtyPerProposal`, default 10). If `maxQty = 0`: emit with `Qty = 0`, `CashReserveBlocked = true`, `CashReserveDetail = "free $X, requires $Y per contract"`.

### Step 7 — Rank and truncate

1. Within each (ticker, structure family) bucket, sort by `BiasAdjustedScore` descending and keep the top `maxCandidatesPerStructurePerTicker`.
2. Merge all surviving candidates for a ticker; sort by `BiasAdjustedScore` descending.
3. Take top `topNPerTicker` for emission.

### Rationale text format

Short, numeric, structured:

```
<Structure> <expiries> $<strikes> — <debit|credit> $<x>, max<profit|loss> $<y>,
POP <n>%, EV/ct/day/$risk <z> [tech <±bias>, fit <±1> → <±n>% <boost|cut>]. <thesis>
```

Examples:

- `PutCreditSpread 2026-05-01 $14/$13 — credit $0.40, maxLoss $0.60, POP 76%, EV/ct/day/$ 0.0051 [tech +0.42, fit +1 → +21% boost]. Bullish technicals align with bullish structure.`
- `LongCalendar (C) 2026-05-01/2026-06-20 $15/$15 — debit $1.05, POP 34%, EV/ct/day/$ 0.0039 [tech +0.42, fit 0 → no adjustment]. Theta-harvest at ATM; neutral to technicals.`

## Configuration

New `opener` section added to `ai-config.json`:

```json
"opener": {
    "enabled": true,
    "topNPerTicker": 5,
    "maxCandidatesPerStructurePerTicker": 8,
    "maxQtyPerProposal": 10,
    "directionalFitWeight": 0.5,
    "profitBandPct": 5.0,
    "ivDefaultPct": 40,
    "strikeStep": 0.50,
    "structures": {
        "longCalendar":  { "enabled": true, "shortDteMin": 3, "shortDteMax": 10, "longDteMin": 21, "longDteMax": 60 },
        "longDiagonal":  { "enabled": true, "shortDteMin": 3, "shortDteMax": 10, "longDteMin": 21, "longDteMax": 60 },
        "shortVertical": { "enabled": true, "dteMin": 3, "dteMax": 10, "widthSteps": [1, 2], "shortDeltaMin": 0.15, "shortDeltaMax": 0.30 },
        "longCallPut":   { "enabled": true, "dteMin": 21, "dteMax": 60, "deltaMin": 0.30, "deltaMax": 0.60 }
    }
}
```

`AIConfigLoader.Validate` gains field-by-field bounds checks:

- `topNPerTicker`, `maxCandidatesPerStructurePerTicker`, `maxQtyPerProposal` ≥ 1.
- `directionalFitWeight` ≥ 0.
- `profitBandPct` ∈ (0, 50].
- `ivDefaultPct` > 0.
- `strikeStep` > 0.
- All DTE min/max: min ≥ 0, max ≥ min.
- Width steps non-empty list of positive ints.
- Delta bounds in (0, 1) with min ≤ max.

When the `opener` section is entirely absent in an existing config file, defaults apply and the opener runs. `ai-config.example.json` gains a fully-populated `opener` block for new users.

## Output

### Console format

Per ticker, one block per tick (only when at least one proposal is emitted):

```
[GME] open proposals (spot $14.82, tech +0.42, cash free $1,240)
 1. PutCreditSpread  2026-05-01 $14/$13          credit $0.40  maxLoss $0.60  POP 76%  EV/d/$ 0.0051  score 0.0062 (↑21%)
    → bullish technicals align; short delta 0.22 in [0.15, 0.30] band
 2. LongCalendar (C) 2026-05-01/2026-06-20 $15/$15  debit $1.05  POP 34%  EV/d/$ 0.0039  score 0.0039
    → theta-harvest structure at ATM; neutral to technicals
 3. LongCall         2026-06-20 $16              debit $0.58  POP 38%  EV/d/$ 0.0028  score 0.0034 (↑21%)
    → bullish structure benefits from technical tailwind
```

The `(↑21%)` / `(↓18%)` annotation on the score column shows the delta between `BiasAdjustedScore` and `RawScore`, so the user can see exactly how technicals moved the rank.

### JSONL format

Appended to `data/ai-proposals.log` (the existing management log), with a `type` discriminator:

```json
{"type":"open","ts":"2026-04-24T10:14:03-04:00","mode":"watch","ticker":"GME",
 "structure":"PutCreditSpread",
 "legs":[{"action":"sell","symbol":"GME   260501P00014000","qty":1},
         {"action":"buy","symbol":"GME   260501P00013000","qty":1}],
 "qty":1,"cashImpactPerContract":40.00,"maxProfit":40.00,"maxLoss":-60.00,"capitalAtRisk":60.00,
 "pop":0.76,"ev":18.40,"daysToTarget":7,
 "rawScore":0.0051,"biasAdjustedScore":0.0062,"directionalFit":1,"technicalBias":0.42,
 "breakevens":[13.60],"rationale":"…","fingerprint":"ab12cd34…",
 "cashReserveBlocked":false,"cashReserveDetail":null}
```

Existing management-proposal JSONL entries gain `"type": "management"` for symmetry. Downstream log readers that don't filter on `type` will receive both streams and need a one-line filter update — acceptable cost for clean separation.

## Cash-Reserve Interaction

Reuses `CashReserveHelper` unchanged. The opener subtracts the current reserve from `accountCash` before computing `maxQty`. A candidate that cannot be sized to ≥ 1 contract within the reserve is still emitted so the user can see what would be possible with more cash, but with `Qty = 0` and `CashReserveBlocked = true`.

## Dedup Across Ticks

Each proposal carries `Fingerprint = sha1(ticker | structureKind | sorted(legs) | qty)`. `OpenProposalSink` keeps a per-fingerprint `(lastScore, lastEmittedAt)` dict scoped to the current session.

On repeat:
- Always write to the JSONL log (the log is the authoritative record).
- Suppress from console **unless** the bias-adjusted score has moved by ≥ 10% since last emission (sharp change is worth re-surfacing).

Fingerprints for proposals whose tickers are no longer in the universe are pruned at the end of each tick to prevent unbounded growth.

## Error Handling

- Missing IV or quote for any leg of a candidate → silently skip the candidate (log at `debug` verbosity only).
- Ticker has no spot price → skip opener for that ticker (same behavior as management rules today).
- Unhandled exception inside the opener for a ticker → log the error, continue to the next ticker (does not fail the tick).
- Config validation errors → rejected at `AIConfigLoader.Validate`, naming the specific field and received value.

## Testing

- **Scorer unit tests per structure** in `CandidateScorer`: given a fixed spot, IV, strikes, and expiries, assert `EV`, `POP`, `Breakevens`, `RawScore`, and `BiasAdjustedScore` match hand-computed values. Include both `+fit` and `−fit` paths and verify calendars are invariant to `technicalBiasScore` when `directionalFitWeight > 0`.
- **Enumerator unit tests**: assert the correct `(strike, expiry, width)` tuples are produced for a synthetic `spot`, `strikeStep`, and config. Verify DTE bounds are honored on both ends.
- **Cash-sizing tests**: assert `Qty` and `CashReserveBlocked` match expectations across a grid of `accountCash`, `reserve`, and `CapitalAtRisk` values including the edge cases `maxQty = 0` and `maxQty > maxQtyPerProposal`.
- **Integration test** in the existing replay harness: feed a historical day through the full `ai once` pipeline and assert the opener produces ≥ 1 proposal per ticker with internally-consistent scoring.
- **Regression check** that the existing management-rule tests still pass (the opener changes must not alter their behavior).

## Future Work (out of scope for this spec)

- Additional structure families (iron condors, ratios, etc.).
- Per-ticker structure overrides.
- Physical-drift modeling of the underlying (replace neutral-drift scenarios with a bias-driven drift); today's design applies bias only at the ranking-score level.
- Automated execution path from a proposal to the broker, with safety gates.
- Push / Slack notifications when a new proposal above a score threshold appears.
