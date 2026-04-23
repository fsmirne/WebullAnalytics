# Partial-Fill-Aware Lineage Merging — Defer Imbalance Split to End of Replay

## Problem

`PositionReplay` (introduced 2026-04-23) applies `RebalanceLineage` after every event. When a single logical roll is split by the broker into multiple partial-fill strategy orders, each partial fill creates transient imbalance in the touched lineage. The per-event rebalance immediately splits that imbalance into a new standalone lineage before the next partial fill can resolve it.

Observed on real data (GME 03/20/2026): one 205-qty roll executed as three partial fills (1, 195, 9). Event 1 splits off a 203-qty orphan short leg; events 2 and 3 then see a "touches 2 lineages" condition and pick one arbitrarily via tiebreaker. By end of replay, 7 phantom lineages accumulate and 22 multi-touch warnings fire.

End-result of the replay on this data: user-visible positions match legacy for the current open GME diagonal, but the replay carries seven extra standalone lineages that legacy correctly identifies as closed. These phantoms show up in `wa diff-positions` and make the rewrite unshippable.

Root cause: the "balanced multi-leg lineage at every event boundary" invariant is too strict for partial-fill reality. Partial fills produce temporary imbalance by design.

## Solution

Defer `RebalanceLineage`'s split logic from per-event to once-per-underlying at end of replay. Allow lineages to be imbalanced during event processing; settle all imbalances in a single pass after the per-underlying event loop completes (and after expiry handling).

This is a three-edit change. No other rule changes. The split math itself (`RebalanceLineage`'s body) is preserved verbatim.

## Changes

### 1. `ApplyEventToLineage` — remove per-event rebalance

At the end of `ApplyEventToLineage`, delete the `RebalanceLineage(lin, evt, active, ref lineageIdCounter)` call. The `UnitQty = min(leg qtys)` assignment stays; lineages remain in whatever shape the event left them, possibly with differing per-leg qtys.

### 2. `Execute` — call `SettleImbalances` per underlying

Between the event loop and the end-of-replay `AssertInvariants`, add a new `SettleImbalances(active, ref lineageIdCounter)` call:

```csharp
foreach (var evt in events)
    ApplyEvent(active, evt, underlying, ref lineageIdCounter);
ApplyExpiries(active, evaluationDate);
SettleImbalances(active, ref lineageIdCounter);    // NEW
AssertInvariants(active, $"end of replay for {underlying}");
```

`SettleImbalances` iterates `active` and, for each lineage with `OpenLegs.Count > 1` whose leg qtys differ, delegates to the existing split logic from `RebalanceLineage`. To keep this a single-responsibility component, rename:

- **Old** `RebalanceLineage(Lineage, Event, ...)` → **New** `SettleImbalance(Lineage lin, List<Lineage> active, ref int lineageIdCounter)`. Signature no longer takes an `Event` parameter because the causing-event is no longer available at settlement time. The synthetic split-marker trade in `TradeHistory` uses the lineage's most-recent trade timestamp (`lin.TradeHistory[^1].Timestamp`, falling back to `lin.FirstEntryTimestamp` if the history is empty) and the marker instrument `"[split from lineage {id} at end of replay]"`. All other split math (proportional cash, preserved FirstEntryTimestamp, etc.) is unchanged.
- **New** `SettleImbalances(List<Lineage> active, ref int lineageIdCounter)` — iterates active lineages, calls `SettleImbalance` on any that need it. Spawned lineages get appended to `active` for emission.

### 3. `AssertInvariants` — relax mid-replay balance check

Add a `bool enforceBalance` parameter, defaulting to `false`. The Inv2 check ("multi-leg lineages are balanced") runs only when `enforceBalance == true`.

Call sites:
- Per-event (at end of `ApplyEvent`): `AssertInvariants(active, $"after event at {evt.Timestamp}")` → `enforceBalance=false` (default).
- End-of-replay (in `Execute` per-underlying loop): `AssertInvariants(active, $"end of replay for {underlying}", enforceBalance: true)` — since `SettleImbalances` has just run, every remaining multi-leg lineage must be balanced.

All other invariant checks (Inv1 positive leg qty, Inv3 UnitQty > 0, Inv4 finite RunningCash) continue to run unconditionally.

## Behavioral impact

### Partial-fill roll (the bug scenario)

Three partial fills totaling 205 qty (1 + 195 + 9). After the new behavior:

1. Event 1 (qty 1): lineage becomes `{27-Mar short: 204, 17-Apr long: 1}` — imbalanced, but no split.
2. Event 2 (qty 195): lineage becomes `{27-Mar short: 9, 17-Apr long: 196}` — still imbalanced. No split. No multi-touch warning (only one lineage exists).
3. Event 3 (qty 9): lineage becomes `{17-Apr long: 205}` — balanced single-leg. The 27-Mar short fully closed.

End of replay: no imbalance. `SettleImbalances` is a no-op on this lineage. One clean lineage emits to output.

### Deliberate partial close

User holds a 200-qty diagonal. Closes 40 of the short leg only via a standalone trade.

1. Event applies → `{long: 200, short: 160}` imbalanced. No mid-event split.
2. No subsequent rebalancing event.
3. End of replay: `SettleImbalance` fires. Splits 40 long into a new standalone lineage with proportional cash. Original lineage becomes `{long: 160, short: 160}` balanced.

Output: one balanced 160-qty diagonal + one 40-qty standalone long. Matches the spec's intended behavior for partial-close from the original PositionReplay design.

### Multi-touch warning frequency

Multi-touch fires only when an event's legs match open legs in two or more distinct lineages. Under the old rule, every partial-fill roll created orphans that subsequent fills matched, producing the warnings. Under the new rule, no orphans are created until end-of-replay, so multi-touch only fires in rare legitimate cases (user holds multiple independent positions with an overlapping leg matchKey — stated by the user to "never happen in practice").

## Validation

Same as the original PositionReplay rewrite: `wa diff-positions` runs both backends; user classifies each diff.

Expected after this fix:
- The 7 only-in-replay phantoms on the user's current data disappear.
- Multi-touch warnings drop from 22 to 0 (or very near 0).
- All pre-existing matches remain matches.
- No new diffs appear (the fix makes the replay STRICTLY better, not differently).

Invariant assertions continue to hold — Inv1/Inv3/Inv4 at every event, Inv2 at end-of-replay.

Rollback safety: the `pre-strategygrouper-rewrite` tag still lets us drop the whole rewrite (this spec included) with one command. If this fix itself introduces new issues, it's three isolated edits to revert.

## Out of Scope

- Changes to rules 1–4.
- Changes to expiry handling, stock+option multiplier handling, emission logic.
- Changes to `diff-positions` command.
- Changes to `AssertInvariants` beyond the `enforceBalance` gate.
- Any refactoring of `PositionReplay.cs` not required by the three edits above.

## Acceptance

- `wa diff-positions` on the user's current `orders.jsonl` produces `N match, 0 diff, 0 only-in-replay, 0 only-in-legacy` for the pre-existing current-open positions (or any diffs are explicitly accepted by the user).
- `[PositionReplay] Warning: event touches N lineages` messages do not appear under normal replay.
- Invariant assertions still fire correctly when bugs exist: Inv2 at end-of-replay catches any lineage that `SettleImbalances` didn't resolve.
- Rollback via `pre-strategygrouper-rewrite` tag remains available; this fix is additive to the existing PositionReplay branch.
