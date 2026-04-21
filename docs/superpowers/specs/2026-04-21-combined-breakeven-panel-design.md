# Combined Break-Even Panel — Design

**Date:** 2026-04-21
**Status:** Approved via brainstorming, pending implementation plan
**Scope:** New panel for the `report` command that shows an aggregated break-even view across all open positions on a given ticker.

---

## 1. Motivation

Today the `report` command renders one break-even panel per position or multi-leg strategy. When a user holds more than one position on the same underlying (e.g., a call spread plus a covered call on GME), they can see each panel individually but have no portfolio-level view of how those positions combine. The combined panel fills that gap: one aggregated break-even analysis per ticker that holds 2 or more independent positions.

---

## 2. Scope and Trigger

**Visibility:** A combined panel is rendered for a ticker if and only if that ticker has **2 or more `BreakEvenResult` units** produced by the existing analyzer. A ticker with a single multi-leg strategy (e.g., one iron condor) does not trigger the combined panel — its existing panel already shows the combined break-even for that strategy.

**Positions included in the aggregation:** all position types on the ticker — stock, single options, and multi-leg strategies. Stock contributes a linear term to net P&L but does not appear as a row in the time-decay grid's math.

**Expiry handling:** follows the existing calendar/diagonal convention. Max profit, max loss, and break-evens are evaluated at the **earliest option expiry** among the constituent legs. The time-decay grid may extend to the latest expiry to show the full trajectory, matching current behavior for single multi-expiry strategies.

**Leg merging:** option legs across positions that share the same `MatchKey` (same strike, expiry, call/put) are merged into one net leg — signed quantities summed, prices weighted-averaged. If the net quantity is zero, the leg is dropped entirely.

---

## 3. Architecture

### New files

**`CombinedBreakEvenAnalyzer.cs`**
- `public static List<BreakEvenResult> Analyze(List<PositionRow> positions, AnalysisOptions opts)`
- Returns one combined `BreakEvenResult` per ticker that has 2+ units; empty list otherwise.
- Internally: group positions by ticker, count units using the same grouping logic `BreakEvenAnalyzer.GroupPositions` uses, flatten qualifying tickers to a net leg list via `LegMerger`, call into `OptionMath` helpers and `TimeDecayGridBuilder` to populate a `BreakEvenResult`.

**`LegMerger.cs`**
- `public static List<MergedLeg> Merge(IEnumerable<PositionRow> positions)`
- Walks all legs (strategy legs, singletons, stock), groups by `MatchKey`, nets signed quantities, weighted-averages prices, drops zero-net legs.
- `MergedLeg` — internal record holding instrument, kind, strike, expiry, signed qty, adjusted avg price, and a back-reference to source positions for display annotations.

### Touched files

**`TableRenderer.cs`**
- After the existing per-position breakeven panel loop, call `CombinedBreakEvenAnalyzer.Analyze(...)` and render each returned result with the existing `TableBuilder.BuildBreakEvenPanel(...)`. Combined panels for a ticker render immediately after that ticker's individual panels.

**`OptionMath.cs`** — additive only
- Add `StrategyPnLWithBsMixed(underlyingPrice, legs, evaluationDate, opts)` where `legs` carries per-leg signed quantities. Existing `StrategyPnLWithBs` (uniform-qty) is unchanged and still used by the per-position analyzer. The new overload sums `LegPnLWithBs(..., leg.qty, ...)` per leg using each leg's own quantity.

**`TimeDecayGridBuilder.cs`** — additive only
- Add a `Build` overload that accepts per-leg signed quantities instead of a single shared `qty`/`parentSide` pair. The overload internally loops legs with their own qtys, accumulates total dollar P&L, and emits grid values and P&L without relying on a uniform-qty divisor. The existing `Build` signature is preserved for all current callers.

### Unchanged

`BreakEvenAnalyzer`, `TableBuilder`. The combined analyzer composes existing helpers plus the two small overloads above.

### Data flow

```
positions
  ├─ BreakEvenAnalyzer.Analyze ───────────────► individual BreakEvenResults (rendered)
  └─ CombinedBreakEvenAnalyzer.Analyze
       ├─ group by ticker, keep tickers with 2+ units
       ├─ LegMerger.Merge ─► net legs
       └─ OptionMath + TimeDecayGridBuilder ─► combined BreakEvenResult
                                                    │
                                                    ▼
                                       rendered via BuildBreakEvenPanel
                                       (placed after this ticker's individual panels)
```

---

## 4. Leg Merging Algorithm

**Input:** all `PositionRow`s on a single ticker (parents, strategy legs, singletons, stock).

**Steps:**

1. **Expand to a flat leg list.** Each option leg contributes one entry with signed quantity (`+qty` for Buy, `−qty` for Sell). Stock contributes one entry keyed by ticker symbol (`+shares` long, `−shares` short).

2. **Group by `MatchKey`** for options (already unique per strike/expiry/kind); by ticker symbol for stock.

3. **Net within each group:**
   - `netQty = Σ signedQty`
   - `weightedPrice = Σ (signedQty × adjustedAvgPrice) / netQty`
   - If `netQty == 0`, drop the group entirely.
   - Merged leg `Side`: positive `netQty` → Buy, negative → Sell; magnitude is `|netQty|`.
   - **Price source:** use `AdjustedAvgPrice ?? AvgPrice`, matching the existing `OptionMath.GetPremium` behavior.

4. **Preserve source references.** Each `MergedLeg` tracks which source positions contributed so the panel can annotate merged entries (e.g., `"(merged from 2 positions)"`).

### Worked example

GME with a long call spread (2× long $25, 2× short $30) and a standalone short $30 call (1× short):

| MatchKey | Signed qtys | Net qty | Merged leg |
|---|---|---|---|
| GME $25 Call | +2 | +2 | Long 2× Call $25 @ avg |
| GME $30 Call | −2, −1 | −3 | Short 3× Call $30 @ avg |

Portfolio net: a 2-leg 1×2 ratio spread (long 2× $25, short 3× $30).

### Edge cases

- **All legs net to zero:** no combined result for the ticker; no panel rendered.
- **Only stock remains after netting:** combined result with just the stock term; panel shows a linear P&L (same shape as a stock-only `BreakEvenResult`).
- **Mixed expiries after netting:** analysis point is the earliest expiry; grid spans to the latest.

---

## 5. Aggregation Math

Once legs are merged, every calculation reuses existing `OptionMath` helpers. No new math is introduced.

**Net premium:** `Σ signedQty × weightedPrice × multiplier` across merged legs. Same formula `OptionMath.GetPremium` uses today. Stock contributes `shares × cost`.

**P&L at a given underlying price (for grid and ladder):**
For each option leg, call `OptionMath.LegPnLWithBs(..., legQty, ...)` using the leg's own net quantity. Sum across legs, add the stock term `stockQty × (price − stockCost)`, subtract net premium. This is what the new `OptionMath.StrategyPnLWithBsMixed` overload does — the combined analyzer calls it with the merged leg list. The existing uniform-qty `StrategyPnLWithBs` is not suitable here because merged legs can have different net qtys (e.g., long 2× $25 combined with short 3× $30).

**Break-evens:** prices where net P&L crosses zero at the **earliest option expiry**. Use the existing numerical solver `BreakEvenAnalyzer.AnalyzeStrategy` uses (scan the price ladder for sign changes, then bisect). Up to two break-evens reported, matching existing behavior.

**Max profit / max loss:** evaluate net P&L at the earliest expiry across the strike-centered price range that `BreakEvenAnalyzer` uses today (padded by the `--range` option). Min → max loss, max → max profit. If the P&L curve trends up at the upper range edge, `MaxProfit = null` (unlimited); same for the lower edge and `MaxLoss`. Matches today's behavior for long calls and puts.

**Time-decay grid:** call the new `TimeDecayGridBuilder.Build` per-leg-qty overload with:
- `legs`: the merged option legs, each carrying its own signed net qty
- `netPremium`: as computed above
- `latestExpiry`: the latest merged-leg expiry so the grid spans the full trajectory
- `opts`, `padding`, `centerPrice`, `breakEvens`, `maxColumns`, `underlyingPrice`: same as today

Stock P&L is injected into each grid cell's net total after the grid returns: add `stockQty × (cellPrice − stockCost)` to each net value before coloring. This keeps the grid axis logic (date selection, price rows, market-mid anchoring) shared with the existing builder.

**IV handling:** per-leg IV resolution uses `OptionMath.GetLegIv(leg, opts)` — instrument-keyed overrides and quotes apply automatically to merged legs because `MatchKey` is preserved.

**Unavailable IV:** if any unexpired leg lacks IV, follow the existing analyzer's fallback — emit the result with a `Note` explaining IV is required for full grid and break-even math, and degrade to a price ladder at the earliest expiry (intrinsic-only for expiring legs, BS-dependent cells skipped). Same user experience as a calendar without IV today.

---

## 6. Rendering and Panel Layout

The combined panel reuses `TableBuilder.BuildBreakEvenPanel(BreakEvenResult, ...)` unchanged. Everything is driven by the fields of the `BreakEvenResult`.

**Title format:** continues the symbol-first style of existing panels, listing every distinct merged leg.

Examples:
- Options only: `"GME Combined — Long 2× Call $25, Short 3× Call $30"`
- With stock: `"GME Combined — 100 sh Stock, Short 1× Call $35"`

The position count and expiry metadata move into the details line, e.g.:
`"3 positions · Earliest expiry 13 Feb 2026 · DTE 18"` (with `" · mixed expiries"` appended when applicable).

**Leg list (flat):** one line per merged leg, in the format existing panels already use for strategy legs — side, qty, strike, expiry, bid/ask, IV, historical vol, change. When a merged leg was contributed by more than one source position, append a dim suffix `" (merged from N positions)"`. Stock appears on its own line: `"Stock — 100 sh @ $26.10 cost, last $27.50"`.

**Summary metrics, early-exercise block, time-decay grid, price-ladder fallback, and notes** are rendered by the existing panel builder without modification.

**Placement:** in `TableRenderer.RenderReport()`, after the existing breakeven panel loop completes. Combined panels render in the same ticker order as the individual panels. For each ticker with a combined result, the combined panel appears immediately after that ticker's individual panels (GME combined after GME individuals, AAPL combined after AAPL individuals).

**Excel and text-file output:** both exporters consume the same break-even-results list as the console renderer. Combined results are added to that list (or a parallel list exporters iterate) so they are picked up automatically. Implementation verifies this during integration; if an exporter does not pick them up, a small addition to its break-even loop is included.

**Visual distinction:** no special styling in v1 — the title prefix `"<TICKER> Combined"` is enough signal. A different border or header color can be added later in a one-line change to the panel builder call if desired.

---

## 7. Testing

### `LegMerger` unit tests

- Two long positions on the same leg → merged with summed qty and weighted-average price.
- Long + short on the same leg → netted to the sign of the larger side.
- Fully offsetting long + short on the same leg → leg dropped.
- Disjoint legs (different strikes or expiries) → kept separate, qty unchanged.
- Stock-only ticker → passes through as a single stock merged leg.
- Mixed stock + options → both preserved.

### `CombinedBreakEvenAnalyzer` unit tests

- Ticker with only 1 unit → returns empty (no combined result).
- Ticker with 2+ units → returns one combined result with merged legs.
- Multiple tickers → one combined result per qualifying ticker, in ticker order.
- All legs net to zero → returns empty for that ticker.
- Mixed expiries → analysis point is earliest expiry; grid spans to latest.
- Missing IV on any unexpired leg → result carries the appropriate `Note` and falls back to a price ladder.
- Stock + options combined → net P&L at grid points includes the linear stock term.

### Integration smoke test

Run the `report` command against a fixture with multiple positions on one ticker. Assert:
- The combined panel appears after that ticker's individual panels.
- The title lists every distinct merged leg.
- The details line shows the position count and earliest-expiry DTE.
- Grid cells reflect net P&L across all merged legs (including the stock term when applicable).

---

## 8. Out of Scope for v1

- Cross-ticker portfolio aggregation (a single global panel spanning all tickers). Break-even math is not meaningful across independent underlyings.
- Alternative visual styling (border color, header highlight) for the combined panel — can be added later with minimal change.
- Portfolio-level Greeks or risk metrics beyond what the existing panel already displays per position.
