# `PositionReplay` — Linear Cash-Flow Replay to Replace `StrategyGrouper`

## Problem

`StrategyGrouper` today does two jobs: FIFO lot allocation (`GroupIntoStrategies`, ~260 LOC) and adjusted-basis computation (`BuildStrategyRows` + helpers, ~740 LOC). The basis computation fans out into five branches — pure, replay, inherited-sibling, own-trades, partial-brand-new — each with its own strike/parent-seq filters used to select "trades relevant to this group." Every new trade pattern the user executes tends to reveal a seam between branches where some trades are excluded that should be included, or vice versa. The result is a steady stream of edge-case bugs that each require new branch logic to patch.

The concrete bug that motivated this rewrite: for a strike-changing roll (e.g., rolling a short put from $25.5 to $25 on an existing diagonal), the "inherited-sibling" branch searches for standalone roll-credit trades only at the **old** parent's strikes. The new short's credit (at the *new* strike) is never subtracted from the running adjusted basis, so the result is off by the new short's credit. The user's actual Option-A running-cash basis is $1.43/share; the tool reports $1.64. Shifting the strike filter to include both old and new strikes would fix this case, but the pattern of "each branch has its own filter, each filter has gaps" means the next new trade pattern will hit another gap.

## Solution

Replace `GroupIntoStrategies` + `BuildStrategyRows` with a single new component, `PositionReplay`, that walks trades chronologically as a state machine and produces position rows directly.

Core model — **Option A running cash:**

- Every lineage maintains a single cumulative cash ledger (`RunningCash`) that accumulates every trade's net cash impact over the lineage's life.
- The adjusted basis at any point is `RunningCash / (UnitQty × Multiplier)`.
- Partial closes, rolls, and full closes are all handled by the same cash update — no special-casing of "realized vs. open cost."
- Synthetic trades (hypothetical, from `analyze trade`) are treated identically to broker-executed trades.

Lineage identity — **matchKey overlap with qty-match fallback:**

- A trade whose leg matches a currently-open leg by matchKey belongs to the lineage that holds that leg.
- A trade that opens a new leg joins an existing orphan lineage only when **exactly one** orphan in the same bucket (underlying + call/put) has matching unit quantity; otherwise the new leg spawns its own lineage.
- Multi-leg strategy orders (trades sharing a `ParentStrategySeq`) are processed as one event — all legs belong to the same lineage, which can span call and put buckets.

No heuristics beyond these rules. No branches based on group classification.

## Architecture

One class, `PositionReplay`, implementing `Execute` that returns the same triple today's `PositionTracker.BuildPositionRows` returns — `(List<PositionRow>, Dictionary<int, StrategyAdjustment>, Dictionary<string, List<NetDebitTrade>>)`. No downstream caller changes. The new class replaces `GroupIntoStrategies` + `BuildStrategyRows` + their helpers; no other files change behavior (some gain tiny routing changes described in §Pipeline).

### Data structure

```csharp
internal sealed class Lineage
{
    public int Id;                                        // monotonic per underlying
    public string Underlying;
    public Dictionary<string, (Side Side, int Qty)> OpenLegs = new();  // keyed by matchKey
    public int Multiplier;                                // 1 stock, 100 option
    public decimal RunningCash;                            // cumulative cash out (paid − received)
    public int UnitQty;                                    // balanced qty across open legs
    public decimal FirstEntryCash;                         // immutable after first event
    public int FirstEntryQty;                              // immutable after first event
    public DateTime FirstEntryTimestamp;
    public List<NetDebitTrade> TradeHistory = new();       // for StrategyAdjustment output
}
```

Derived outputs:

- `AdjustedAvgPrice = RunningCash / (UnitQty × Multiplier)`
- `InitialAvgPrice = FirstEntryCash / (FirstEntryQty × Multiplier)`

A lineage is "active" iff `OpenLegs` contains at least one non-zero-qty entry. When it deactivates (all legs hit zero), it's removed from the active list and produces no `PositionRow` output (it's a closed position).

### Events

An `Event` is either:

- A **strategy order event** — all trades sharing a `ParentStrategySeq`, processed as one multi-leg unit.
- A **standalone event** — a single trade with no `ParentStrategySeq`.

Events are ordered chronologically across all of an underlying's trades (both calls and puts), then processed in sequence. Strategy orders are atomic — all their legs apply in one state transition.

### Per-event classification

For each event, classify each leg vs the currently-active lineages:

- **Reducing leg** — matchKey matches an open leg of an active lineage, trade direction is opposite (closes qty).
- **Adding leg (same direction)** — matchKey matches an open leg, same direction (increases qty).
- **New leg** — matchKey does not match any open leg in any active lineage.

### State transition rules

**Rule 1 — Standalone reduce.** The single leg matches an open leg of lineage L in opposite direction. Decrease L's leg qty by trade qty. Update `RunningCash` with the event's cash (positive for buy, negative for sell, multiplied by qty × multiplier). Append to `TradeHistory`. If the leg hits zero qty, remove it from `OpenLegs`. If `OpenLegs` becomes empty, deactivate L.

**Rule 2 — Standalone add (same direction).** The single leg matches an open leg of lineage L in same direction.

- If L is single-leg: increase the leg's qty, grow `UnitQty` to match, update `RunningCash`.
- If L is multi-leg: the add would break L's balance invariant. Spawn a new standalone single-leg lineage L' with this trade's qty. L' gets this trade's cash as its `RunningCash`. L is unchanged.

**Rule 3 — Standalone new leg.** The single leg doesn't match any open leg. Search active lineages within the **same bucket** (underlying + call/put) for orphans (single-leg lineages) with `UnitQty == trade.Qty`:

- If exactly one orphan matches: add this leg to that lineage, update its `RunningCash`, recompute `UnitQty` as min of leg qtys (all legs now share this qty, so `UnitQty` is unchanged).
- If zero or multiple orphans match: spawn a new standalone lineage with this leg.

**Rule 4 — Strategy order (multi-leg grouped event).** Determine which lineages the event "touches" — any active lineage whose open legs overlap by matchKey with any of the event's legs.

- **Touches zero active lineages** → spawn a new lineage with all event legs. Strategy orders can span buckets (iron condors, iron butterflies, collars).
- **Touches exactly one lineage (L)** → continue L. For each event leg: if it matches an open leg of L, reduce or add it; if new, add it to L's `OpenLegs`. Update `RunningCash` with the event's total cash impact. Append to `TradeHistory`.
- **Touches multiple lineages** → deterministic tiebreaker: the oldest active lineage by `FirstEntryTimestamp` wins. Log a warning with the event details for audit. Continue as single-lineage case. (In practice, user-stated invariant that two lineages never share a matchKey makes this case unreachable; the tiebreaker exists for safety only.)

### Imbalance invariant (auto-split)

After every event, every active multi-leg lineage must satisfy: **all open legs share the same qty**. If Rule 1 or Rule 4 produces an imbalanced state, split immediately:

- Keep the balanced subset as the original lineage L (`UnitQty = min of leg qtys`).
- Spawn a new standalone lineage L' containing the excess (the legs with qty above `min`), with the `min`-qty-worth of cost subtracted from L and allocated to L' proportionally.
- Proportional cash split: `L'.RunningCash = L.RunningCash × (excess_qty / original_unit_qty)`, `L.RunningCash` keeps the complement.
- `L'.FirstEntryCash` and `L'.FirstEntryQty` are proportionally derived from L's original first-entry values.
- `L'.FirstEntryTimestamp = L.FirstEntryTimestamp` (the split inherits the lineage's history; it's not a "new position" in the entry-price sense).
- `L.TradeHistory` and `L'.TradeHistory` each retain a reference to the event that caused the split.

This invariant is asserted at the end of every event's state transition. Violations throw with lineage state dumped — bug in the state machine, not valid runtime state.

### Bucket scoping summary

- **Orphan-matching for standalone new-leg trades (Rule 3)**: restricted to same underlying + same call/put bucket. A long call orphan and a long put standalone don't match.
- **Strategy order events (Rule 4)**: no bucket restriction. An iron condor strategy order touches its legs across both call and put buckets and forms a single cross-bucket lineage.
- **Imbalance auto-split (above)**: operates within a single lineage regardless of bucket.

### Expiry handling

Options that reach expiry without a close trade get a synthetic terminal event applied at the replay's end-of-input:

- Iterate all open legs of active lineages. For each leg whose expiry date is in the past relative to the evaluation date:
- **OTM at expiry** (spot-at-expiry didn't cross strike): emit a synthetic zero-cash event; remove the leg from `OpenLegs`. No `RunningCash` impact.
- **ITM at expiry**: emit a synthetic assignment/exercise event whose cash impact matches what today's `StrategyGrouper.AdjustForExpiredStrategyLegs` produces for the same inputs. Remove the leg. Update `RunningCash`.

This mirrors today's expiry semantics exactly — we are porting the ITM cash-impact formula from `AdjustForExpiredStrategyLegs` verbatim, not redesigning it. If the user doesn't know historical spot-at-expiry (a pre-existing limitation — we don't have intraday spot history for past expiries), the legacy code's default assumption (OTM) carries over. Any bugs in today's expiry handling are out of scope for this rewrite.

### Stock + option multiplier handling

Covered calls / protective puts / collars mix 100-share stock lots with 1-contract option lots. `Multiplier` is stored per lineage (not per leg) — it's the *option* multiplier, 100 — and `UnitQty` is expressed in option contract units.

- Stock leg `OpenLegs` qty is stored as option-equivalent (e.g., 100 shares → qty 1 in the lineage's leg dict; but the underlying `Lot` / trade data retains the share count).
- Orphan qty-match (Rule 3): compares option-contract equivalents across legs.
- `AdjustedAvgPrice` for the parent row divides by `UnitQty × 100` (the option multiplier), consistent with how today's code prices these strategies.

Implementation detail: the lineage tracks an auxiliary `StockQtyByMatchKey` dict to preserve share-count granularity for stock legs; `OpenLegs` stores the option-equivalent qty used by the state machine.

### Emission (Lineage → `PositionRow`)

At end of replay, for each active lineage:

1. Classify the shape of `OpenLegs` via `ParsingHelpers.ClassifyStrategyKind` to derive the strategy label ("Diagonal", "Calendar", "IronCondor", etc.).
2. If the lineage has >1 open leg → emit a parent `PositionRow` with `Asset = OptionStrategy`, `OptionKind = label`, `AvgPrice = AdjustedAvgPrice`, `InitialAvgPrice`, `AdjustedAvgPrice` populated.
3. Emit one leg `PositionRow` per open leg (`IsStrategyLeg = true` when lineage has >1 leg).
   - Per-leg `InitialAvgPrice` = the leg's own weighted-avg fill price across its originating trades (sourced from the lineage's `TradeHistory` filtered to that leg's matchKey).
   - Per-leg `AdjustedAvgPrice` = leg's `InitialAvgPrice` + share of `(RunningCash − Σ leg initial cash)`. Apportionment assigns the entire "adjustment" to a single "target" leg (the long leg by convention, matching today's `ReconcileLegPricesToParent`). Sum of signed per-leg `AdjustedAvgPrice` equals parent `AdjustedAvgPrice` × sign.
4. For a single-leg lineage, only the leg `PositionRow` is emitted (no parent).

`StrategyAdjustment` dict entry per lineage (keyed by lineage `Id`): `Trades = lineage.TradeHistory`, `TotalNetDebit = RunningCash`, `LastFlatTime = null`, `InitNetDebit = FirstEntryCash`.

`singleLegStandalones` dict entry per single-leg lineage that has `TradeHistory.Count > 1` (i.e., a standalone position that's had rolls applied): keyed by the leg's matchKey, value is the trade history.

## Pipeline Integration

### What changes

- **Delete** (Phase 3): `StrategyGrouper.cs` (all ~1100 LOC) and any private types exposed only to it (`StrategyGroup`, `PositionEntry` — grep confirms these are only used inside `StrategyGrouper`).
- **Add**: `PositionReplay.cs` (~300–400 LOC, single class, 5 methods).
- **Modify**: `PositionTracker.BuildPositionRows` (~5 LOC) to dispatch to either `StrategyGrouper.BuildPositionRows` (legacy) or `PositionReplay.Execute` (new) based on a flag.
- **Add** (Phase 2 tooling): `wa diff-positions` CLI subcommand that runs both paths and emits a diff table. Lives under `AnalyzeCommand` or a new file; deleted in Phase 3.

### What does not change

- `JsonlParser` — the JSONL → `Trade` pipeline is untouched. `ParentStrategySeq` assignment (grouping by `TransactTime`) stays as today's implementation.
- `CsvParser` — CSV handling is untouched.
- `PositionTracker.ComputeReport` — fee attribution, realized P&L, and raw lot building stay as today.
- `Trade`, `Lot`, `PositionRow`, `NetDebitTrade`, `StrategyAdjustment` record shapes — unchanged.
- All downstream consumers: `TableBuilder`, `BreakEvenAnalyzer`, `AdjustmentReportBuilder`, `CombinedBreakEvenAnalyzer`, `LegMerger`, `OptionMath.GetPremium`, `ExcelExporter`, `AnalyzePositionCommand`, `AnalyzeCommand`, `AI/Sources/*`, `ProposalSink` — all consume the unchanged `PositionRow` shape.

## Validation Strategy

Three phases with the legacy path kept alive through Phase 2.

### Phase 1 — Implement; both paths compile

`PositionTracker.BuildPositionRows` gains a single dispatch branch keyed off `AdjBasisBackend` (env var `WA_ADJ_BASIS=legacy|replay` or CLI option `--adj-basis=legacy`; default `replay`). Both backends compile and produce the same return type. The legacy `StrategyGrouper` stays in the repo, unmodified.

### Phase 2 — `wa diff-positions` and manual classification

New CLI subcommand renders a table of positions where the two backends produce different `AdjustedAvgPrice` or `InitialAvgPrice`:

```
Position                               Legacy init  New init   Legacy adj   New adj    Δ adj
GME Diagonal Put $25/$26 (200x)        $1.07        $1.07      $1.64        $1.43      −$0.21
GME Calendar Put $25 (100x)            $0.85        $0.85      $0.85        $0.85      — (match)
...
```

For each diff the user classifies:

- **intended fix** — new number matches Option-A running-cash math; legacy was wrong.
- **unexpected regression** — legacy was right; new code has a bug.
- **different semantics but both defensible** — discussed individually.

Regressions trigger rework of the state-machine rule that produced the wrong output. The `pre-strategygrouper-rewrite` git tag lets the user roll the whole rewrite back if Phase 2 goes badly.

Phase 2 has no time limit. It ends when the user signs off that every diff is accepted.

### Phase 3 — Retire legacy

A single cleanup commit:

1. Delete `StrategyGrouper.cs`.
2. Delete the `PositionTracker.BuildPositionRows` dispatch flag (new path is unconditional).
3. Delete `wa diff-positions` subcommand (or rename it to an internal debug tool if any value remains).
4. Delete now-unreferenced private types in `Models.cs` (`StrategyGroup`, `PositionEntry` — confirm via grep at cleanup time).

### Invariant assertions in the new code

`PositionReplay` asserts at the end of each event's state transition and again at the end of the full replay:

- No active multi-leg lineage has imbalanced legs (all open legs share one qty).
- Sum of signed per-leg `AdjustedAvgPrice` equals parent `AdjustedAvgPrice` × lineage sign.
- `RunningCash` is finite, not NaN.
- `UnitQty > 0` for every active lineage.
- `OpenLegs` never contains a zero-qty entry.

Violations throw with the triggering event and lineage state dumped. A violation means a bug in the state machine code, not valid runtime state.

### Smoke tests before Phase 3

No automated test framework exists. Pre-Phase-3 smoke checklist, run by the user:

- `wa report` — every currently-open position renders with sensible numbers.
- `wa analyze position` — scenario engine receives correct `CostBasis` via `OptionMath.GetPremium` path we already fixed.
- `wa ai once` and `wa ai replay` — rule evaluation against current positions produces correct adj basis in proposals.
- `wa analyze trade` — post-hypothetical-trade position panels show correct new adj basis.

## Behavioral Changes (Intentional)

These differ from current output and are deliberate:

1. **Straddles and strangles drop their grouping** — if the source JSONL shows them as two separate trades (same `TransactTime` would group them; otherwise they're split), the new code emits them as two per-bucket lineages rather than one "Straddle"/"Strangle" parent. If the broker grouped them as a strategy order, they stay grouped. See §Bucket scoping.
2. **Partial close of one leg of a multi-leg position auto-splits the excess** — under today's code this case may either keep the imbalanced position as-is or mis-compute basis; under the new code it always produces a balanced lineage + an orphan standalone lineage with proportional cash.
3. **Strike-changing rolls are computed by cumulative cash flow** — this is the core bug fix. Rolling a short from strike A to strike B (same or different expiry) now correctly debits/credits the running position rather than dropping the credit of either the old or new short.
4. **Lineage identity via matchKey + qty-match** — standalone trades that close one leg and open another at a new strike are now linked via qty-match-on-orphan, so the user sees "one rolled diagonal" rather than "one orphan long + one orphan short" in the common case.

All four diffs are observable via `wa diff-positions` in Phase 2.

## Out of Scope

- Changes to JSONL or CSV parsing logic.
- Changes to `PositionTracker.ComputeReport` (realized P&L, lot tracking, fee attribution).
- Changes to `Trade`, `Lot`, or `PositionRow` record shapes.
- Changes to break-even math (`BreakEvenAnalyzer`, `CombinedBreakEvenAnalyzer`).
- Adding a persistent user-linkage sidecar file (rejected earlier — solved via qty-match rule).
- Rewriting `GroupIntoStrategies`'s FIFO lot allocator as a stateful pass prior to replay (absorbed into `PositionReplay` directly; no separate allocator survives).
- Any changes to `wa trade place` behavior.

## Acceptance

- `PositionReplay.Execute` produces `PositionRow` output with `InitialAvgPrice` and `AdjustedAvgPrice` values that, on every active lineage, equal the running-cash-divided-by-unit-qty formula computed from the full chronological trade stream for that lineage.
- `wa diff-positions` against the user's current `orders.jsonl` enumerates every position where legacy and replay outputs differ, and the user has classified each diff as an intended fix.
- `wa report`, `wa analyze position`, `wa analyze trade`, and `wa ai once/replay` produce output the user accepts across their typical trading patterns (diagonals, calendars, verticals, iron condors, partial closes, strike-changing rolls).
- Phase 3 cleanup commit removes `StrategyGrouper.cs` and all code paths that reference it; `wa` builds clean with the new code alone.
- `pre-strategygrouper-rewrite` tag stays available locally for the user's safety.
