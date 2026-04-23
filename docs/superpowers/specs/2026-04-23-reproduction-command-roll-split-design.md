# Reproduction Commands — Drop `--side`, Split Non-Calendar Rolls

## Summary

Two display sites in WebullAnalytics emit copy-paste `wa trade place` reproduction commands so the user can turn a computed scenario into an actual order:

- `AnalyzePositionCommand.BuildReproductionCommands` — one line per scenario in the `wa analyze position` scenario panels.
- `AI/Output/ProposalSink.WriteConsole` — one line per proposal emitted by the AI rule engine.

Both currently emit a single combo `wa trade place --trade "..." --limit X --side buy|sell` line. Two problems:

1. Webull's combo engine rejects any combo order where one leg reverses an existing position (error `OAUTH_OPENAPI_ORDER_NOT_SUPPORT_REVERSE_OPTION`). This fires on diagonal rolls and on same-expiry / different-strike "vertical-shaped" rolls of a diagonal position. Only same-strike calendar rolls go through as a combo.
2. The `--side` flag is redundant. `SideInferrer` derives combo direction from leg structure, and the reproduction sites' scenarios are all shapes the inferrer handles correctly. Emitting `--side` is extra noise that must be kept in sync with the inferrer.

This spec fixes both.

## Changes

### 1. Drop `--side` from both reproduction sites

- `AnalyzePositionCommand.cs:985-986` — remove the `var side = ...` line and the ` --side {side}` suffix.
- `AI/Output/ProposalSink.cs:83-84` — same treatment.

Result: every emitted `wa trade place` line is `wa trade place --trade "..." --limit X`. Side is inferred when the command runs. If the inferrer ever fails on a shape these sites produce, that's a bug in `SideInferrer` — fix there, not by hardcoding `--side` at the emission site.

### 2. Split non-calendar rolls into two commands

A **roll** is a scenario that closes an existing leg and opens a new leg. A **calendar roll** has both legs at the same strike with different expiries. Webull accepts calendar-roll combos; it rejects everything else.

New emission rule for roll scenarios:

- If the legs form a **same-strike calendar** (all option legs share one strike, two distinct expiries) → emit one `wa trade place` combo line (unchanged behavior).
- Otherwise → emit **two separate** `wa trade place` lines, each single-leg, **in the order the legs appear in the source**. Each uses its own per-share price as `--limit`.

Close-before-open ordering is a responsibility of the source:

- `Scenario.ActionSummary` from `ScenarioEngine.EmitRoll` already prints `BUY oldShort, SELL newShort` (close-first) for short-side rolls.
- `ProposalLeg[]` from `DefensiveRollRule` / `RollShortOnExpiryRule` is constructed `{ buy old, sell new }` (close-first).

The split emitter passes legs through in order. If a future source emits open-first legs, it must be updated to emit close-first — the split emitter doesn't try to reclassify legs as close vs open.

The `wa analyze trade` line stays as a single command in both cases — analyze isn't subject to the broker restriction.

Example output for a diagonal-roll scenario (2-leg, different strikes, different expiries):

```
↪ wa trade place --trade "buy:GME260424P00025500:200" --limit 0.40
↪ wa trade place --trade "sell:GME260501P00025000:200" --limit 0.25
↪ wa analyze trade "buy:GME260424P00025500:200@0.40,sell:GME260501P00025000:200@0.25" --ticker-price 24.50
```

The user runs the close first, waits for fill via `wa trade status`, then runs the open. Broker accepts each single-leg order because neither is a combo — the reversal-detection logic doesn't apply to single-leg orders.

### 3. Roll detection

Both sites need a way to tell "this scenario is a roll" apart from "this scenario is a fresh add" (an add opens a new position alongside the existing one — Webull accepts it as a combo regardless of shape, so it must not be split).

- **AnalyzePosition**: add `bool IsRoll` to the `Scenario` record (`AnalyzePositionCommand.cs:277`). Scenario generators set it to `true` for roll scenarios (close-and-reopen) and leave it `false` for hold/add/close-only scenarios. `BuildReproductionCommands` only considers splitting when `IsRoll == true`.
- **ProposalSink**: use `ManagementProposal.Kind == ProposalKind.Roll` — already present, no schema change needed.

### 4. Per-leg prices for ProposalSink splits

`AnalyzePosition.BuildReproductionCommands` already parses per-leg `@PRICE` values out of `Scenario.ActionSummary`, so splitting there is a local refactor with no new data.

`ProposalSink`'s legs come from `ProposalLeg`, which today carries only `Action`, `Symbol`, `Qty`. To split, each leg needs its per-share price (the price that was used to compute `NetDebit`).

- Add `decimal? PricePerShare` to `ProposalLeg`.
- Populate it in the rules/engine paths that already have the quote in hand:
  - `DefensiveRollRule.cs` — close-leg gets `oldQ.Ask`, open-leg gets `newQ.Bid` (already computed inline to derive `netCredit`).
  - `ScenarioEngine.EmitRoll` / `EmitReset` — each leg's price is the same mid the scenario generator used to build `cashPerShareOfChange`; thread it through.
  - `RollShortOnExpiryRule.cs` — same-strike calendar rolls never split, but populate for consistency.
- `ProposalSink` falls back to a single combo line (no split, legacy behavior) if any leg in a non-calendar roll has `PricePerShare == null`. This keeps the emission code well-defined even if a rule forgets to populate prices. No warning needed — the combo line will either fill (if Webull accepts) or get rejected with the original error, at which point the user has a clear signal to update the rule.

### 5. Calendar detection helper

Add a small static helper (e.g., `RollShape.IsSameStrikeCalendar(IReadOnlyList<ProposalLeg>)` — placed near `SideInferrer`) so both emission sites share the same calendar-vs-non-calendar test. The test:

- All legs are option legs (no equity).
- Exactly two legs.
- Both legs share one strike.
- Legs have distinct expiries.

Returns `true` only if all four hold. Everything else (single leg, 3+ legs, mixed strikes, same expiry) returns `false` and — for rolls — triggers the split.

## Out of Scope

- **4-leg reset scenarios** (close existing spread + open new spread, produced by `ScenarioEngine.EmitReset`). These already emit a 4-leg combo; Webull's acceptance of them depends on whether the close-pair and the open-pair each pass the reversal check. Keep current behavior (single combo line). If production rejections show up, handle in a follow-up.
- **OTO/one-triggers-other execution inside `trade place`**. Considered and rejected — would require quote fetching, per-leg-limit derivation from a net limit, polling loops, and partial-fill policy inside what is currently a simple "legs + net → broker" primitive. If a true single-command roll proves valuable, add a new `trade roll` command that owns orchestration; do not overload `trade place`.

## Testing

- **Unit — `IsSameStrikeCalendar`**: covers single leg, 2-leg same-strike-diff-expiry (true), 2-leg same-strike-same-expiry (false), 2-leg diff-strike-diff-expiry (false), 2-leg diff-strike-same-expiry (false), 3-leg, equity+option.
- **Unit — `AnalyzePosition.BuildReproductionCommands`**: scenarios with `IsRoll == true` + calendar legs → one line; `IsRoll == true` + diagonal legs → two lines in close-then-open order; `IsRoll == false` → one line (add/hold paths unchanged); `ActionSummary` without `@PRICE` → one line fallback.
- **Unit — `ProposalSink.WriteConsole`**: `ProposalKind.Roll` + calendar legs → one line; `Roll` + diagonal legs with all prices populated → two lines; `Roll` + diagonal legs with any null price → one combined fallback line; `ProposalKind.Close` / `AlertOnly` → unchanged.
- **Manual/sandbox**: submit a split diagonal roll generated from `wa analyze position` against the sandbox account, confirm each single-leg order previews and places without the `REVERSE_OPTION` rejection.

## Acceptance

- No `--side` token appears in any emission from either site.
- A `wa analyze position` run on a position whose recommended scenario is a non-calendar roll prints two `wa trade place` lines (close, then open) each with a per-leg `--limit`, plus one `wa analyze trade` line.
- The same run on a calendar roll prints exactly one `wa trade place` line and one `wa analyze trade` line.
- `wa trade place` itself is unchanged — no new flags, no new code paths, no quote fetching.
