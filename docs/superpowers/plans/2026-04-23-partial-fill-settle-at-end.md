# Partial-Fill Settle-at-End Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Defer `PositionReplay`'s imbalance-split logic from per-event to once-per-underlying at end of replay so partial-fill strategy orders don't spawn permanent orphan lineages.

**Architecture:** Rename the existing `RebalanceLineage` to `SettleImbalance` (single-lineage operation, no `Event` parameter), wrap it in a new `SettleImbalances` iterator called at end of per-underlying replay. Remove the per-event call from `ApplyEventToLineage`. Relax `AssertInvariants`'s balance check mid-replay via a new `enforceBalance` flag; enforce it only after `SettleImbalances` has run.

**Tech Stack:** C# / .NET 10, Spectre.Console.Cli. Verification via CLI integration (`wa diff-positions`, `wa report --adj-basis=replay`); no test framework in repo.

---

## File Map

| File | Change |
|------|--------|
| `PositionReplay.cs` | Rename `RebalanceLineage` → `SettleImbalance` (different signature); add `SettleImbalances` wrapper; remove per-event call from `ApplyEventToLineage`; add `enforceBalance` parameter to `AssertInvariants`; update call sites |

Single file, three edits. No new files.

---

## Task 1: Rename and relocate the rebalance logic

**Files:**
- Modify: `PositionReplay.cs`

Move all imbalance-splitting to end of per-underlying replay. Rename `RebalanceLineage` to `SettleImbalance` (operates on one lineage), add `SettleImbalances` (plural) wrapper that iterates active lineages, wire into `Execute`, and delete the per-event call from `ApplyEventToLineage`.

- [ ] **Step 1: Rename `RebalanceLineage` and drop the `Event` parameter**

Find the current method signature in `PositionReplay.cs`:

```csharp
private static void RebalanceLineage(Lineage lin, Event causingEvent, List<Lineage> active, ref int lineageIdCounter)
{
	if (lin.OpenLegs.Count < 2) return;
	var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
	var maxQty = lin.OpenLegs.Values.Max(v => v.Qty);
	if (minQty == maxQty) return; // balanced

	var imbalancedKeys = lin.OpenLegs.Where(kv => kv.Value.Qty > minQty).Select(kv => kv.Key).ToList();
	var origUnit = maxQty;

	foreach (var key in imbalancedKeys)
	{
		var (side, qty) = lin.OpenLegs[key];
		var excess = qty - minQty;
		var spawnRatio = (decimal)excess / origUnit;
		var spawn = new Lineage
		{
			Id = ++lineageIdCounter,
			Underlying = lin.Underlying,
			Multiplier = lin.Multiplier,
			FirstEntryTimestamp = lin.FirstEntryTimestamp,
			RunningCash = lin.RunningCash * spawnRatio,
			FirstEntryCash = lin.FirstEntryCash * spawnRatio,
			FirstEntryQty = excess,
			UnitQty = excess
		};
		spawn.OpenLegs[key] = (side, excess);
		spawn.TradeHistory.Add(new NetDebitTrade(causingEvent.Timestamp, $"[split from lineage {lin.Id}]", Side.Buy, excess, 0m, spawn.RunningCash));
		active.Add(spawn);
		lin.OpenLegs[key] = (side, minQty);
	}

	var keepRatio = (decimal)minQty / origUnit;
	lin.RunningCash *= keepRatio;
	lin.FirstEntryCash *= keepRatio;
	lin.FirstEntryQty = minQty;
	lin.UnitQty = minQty;
}
```

Replace with:

```csharp
/// <summary>
/// If `lin` has any open leg whose qty exceeds the minimum leg qty, split the excess into a new
/// standalone lineage. Proportionally allocates cash: the split lineage gets (excess/origUnit) × cash.
/// Original lineage's RunningCash and FirstEntryCash are scaled down by (min/origUnit).
/// Called once per lineage at end of per-underlying replay (after all events, after ApplyExpiries).
/// </summary>
private static void SettleImbalance(Lineage lin, List<Lineage> active, ref int lineageIdCounter)
{
	if (lin.OpenLegs.Count < 2) return;
	var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
	var maxQty = lin.OpenLegs.Values.Max(v => v.Qty);
	if (minQty == maxQty) return; // balanced

	var imbalancedKeys = lin.OpenLegs.Where(kv => kv.Value.Qty > minQty).Select(kv => kv.Key).ToList();
	var origUnit = maxQty;

	// Split-marker timestamp: use the lineage's most-recent trade timestamp (no causing Event at settlement).
	var markerTimestamp = lin.TradeHistory.Count > 0 ? lin.TradeHistory[^1].Timestamp : lin.FirstEntryTimestamp;

	foreach (var key in imbalancedKeys)
	{
		var (side, qty) = lin.OpenLegs[key];
		var excess = qty - minQty;
		var spawnRatio = (decimal)excess / origUnit;
		var spawn = new Lineage
		{
			Id = ++lineageIdCounter,
			Underlying = lin.Underlying,
			Multiplier = lin.Multiplier,
			FirstEntryTimestamp = lin.FirstEntryTimestamp,
			RunningCash = lin.RunningCash * spawnRatio,
			FirstEntryCash = lin.FirstEntryCash * spawnRatio,
			FirstEntryQty = excess,
			UnitQty = excess
		};
		spawn.OpenLegs[key] = (side, excess);
		spawn.TradeHistory.Add(new NetDebitTrade(markerTimestamp, $"[split from lineage {lin.Id} at end of replay]", Side.Buy, excess, 0m, spawn.RunningCash));
		active.Add(spawn);
		lin.OpenLegs[key] = (side, minQty);
	}

	var keepRatio = (decimal)minQty / origUnit;
	lin.RunningCash *= keepRatio;
	lin.FirstEntryCash *= keepRatio;
	lin.FirstEntryQty = minQty;
	lin.UnitQty = minQty;
}

/// <summary>
/// End-of-replay settlement: walks active lineages once, calling SettleImbalance on each.
/// Any imbalance that survived the event loop (e.g., a deliberate partial close that wasn't
/// rebalanced by subsequent trades) becomes a standalone orphan lineage at this point.
/// Partial-fill strategy orders, by contrast, self-resolve during the event loop and produce
/// no imbalance at settlement.
/// </summary>
private static void SettleImbalances(List<Lineage> active, ref int lineageIdCounter)
{
	// Snapshot the current list; spawned lineages append to `active` but shouldn't be re-examined
	// (they're single-leg by construction and have nothing to settle).
	var snapshot = active.ToList();
	foreach (var lin in snapshot)
		SettleImbalance(lin, active, ref lineageIdCounter);
}
```

- [ ] **Step 2: Remove the per-event call from `ApplyEventToLineage`**

Find the current tail of `ApplyEventToLineage`:

```csharp
	lin.UnitQty = lin.OpenLegs.Values.Count > 0 ? lin.OpenLegs.Values.Min(v => v.Qty) : 0;

	// Post-event invariant: every multi-leg lineage must be balanced (all open legs share one qty).
	// Any imbalance is split off immediately — keeping the lineage balanced at the common-qty minimum
	// and spawning a new standalone lineage for the excess, with proportional cash allocation.
	RebalanceLineage(lin, evt, active, ref lineageIdCounter);
}
```

Replace with (delete the call and its preamble comment; keep the `UnitQty` assignment):

```csharp
	lin.UnitQty = lin.OpenLegs.Values.Count > 0 ? lin.OpenLegs.Values.Min(v => v.Qty) : 0;

	// Imbalance (if any) is deferred to end-of-replay settlement via SettleImbalances —
	// partial-fill strategy orders temporarily imbalance lineages mid-replay but usually
	// self-resolve before the replay ends.
}
```

- [ ] **Step 3: Wire `SettleImbalances` into `Execute`**

In `Execute`, find the per-underlying loop:

```csharp
foreach (var (underlying, events) in eventsPerUnderlying)
{
	var active = new List<Lineage>();
	foreach (var evt in events)
		ApplyEvent(active, evt, underlying, ref lineageIdCounter);
	ApplyExpiries(active, evaluationDate);
	AssertInvariants(active, $"end of replay for {underlying}");
	allLineages.AddRange(active);
}
```

Insert `SettleImbalances` between `ApplyExpiries` and `AssertInvariants`:

```csharp
foreach (var (underlying, events) in eventsPerUnderlying)
{
	var active = new List<Lineage>();
	foreach (var evt in events)
		ApplyEvent(active, evt, underlying, ref lineageIdCounter);
	ApplyExpiries(active, evaluationDate);
	SettleImbalances(active, ref lineageIdCounter);
	AssertInvariants(active, $"end of replay for {underlying}");
	allLineages.AddRange(active);
}
```

- [ ] **Step 4: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`. Build will fail until Task 2 relaxes `AssertInvariants` because partial-fill lineages are now imbalanced when the per-event assertion runs. If Inv2 throws during replay in smoke testing, confirm the failure and proceed to Task 2 — don't try to fix it here.

Run the smoke test:

```bash
WA_ADJ_BASIS=replay "/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -5
```

If an `InvalidOperationException` fires with a message about imbalance, that's expected and will be fixed in Task 2. Capture the message — the next task will resolve it.

If no exception fires, great — Inv2 currently only runs at end-of-replay in some branch of the code, meaning relaxing it mid-replay is a no-op. Proceed to Task 2 anyway.

- [ ] **Step 5: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: defer imbalance split from per-event to end of replay via SettleImbalances"
```

---

## Task 2: Relax `AssertInvariants` mid-replay

**Files:**
- Modify: `PositionReplay.cs`

Add `enforceBalance` parameter (default `false`) to `AssertInvariants`. Only check Inv2 ("multi-leg lineage balance") when set. Per-event call sites keep the default; end-of-replay call site sets it to `true`.

- [ ] **Step 1: Add the `enforceBalance` parameter**

Find the current signature:

```csharp
private static void AssertInvariants(IEnumerable<Lineage> active, string context)
```

Replace with:

```csharp
private static void AssertInvariants(IEnumerable<Lineage> active, string context, bool enforceBalance = false)
```

- [ ] **Step 2: Gate Inv2 on the parameter**

Find the Inv2 block inside `AssertInvariants`:

```csharp
		// Inv2: multi-leg lineages are balanced.
		if (lin.OpenLegs.Count > 1)
		{
			var qtys = lin.OpenLegs.Values.Select(v => v.Qty).Distinct().ToList();
			if (qtys.Count > 1)
				throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} is imbalanced: legs have qtys [{string.Join(",", qtys)}] (context: {context})");
		}
```

Replace with:

```csharp
		// Inv2: multi-leg lineages are balanced. Only enforced at end-of-replay (after SettleImbalances);
		// mid-replay imbalance is expected for partial-fill strategy orders.
		if (enforceBalance && lin.OpenLegs.Count > 1)
		{
			var qtys = lin.OpenLegs.Values.Select(v => v.Qty).Distinct().ToList();
			if (qtys.Count > 1)
				throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} is imbalanced: legs have qtys [{string.Join(",", qtys)}] (context: {context})");
		}
```

- [ ] **Step 3: Update the end-of-replay call site to enforce balance**

In `Execute`'s per-underlying loop, find:

```csharp
AssertInvariants(active, $"end of replay for {underlying}");
```

Replace with:

```csharp
AssertInvariants(active, $"end of replay for {underlying}", enforceBalance: true);
```

The per-event call site inside `ApplyEvent` stays as-is (default `false` is correct there).

- [ ] **Step 4: Build and smoke-test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3
```

Expected: `0 Error(s)`.

```bash
WA_ADJ_BASIS=replay "/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -5
```

Expected:
- No `InvalidOperationException`.
- Summary line shows **significantly fewer** `only-in-replay` entries than the pre-fix 7 (ideally 0 for partial-fill-caused phantoms; may still show legitimate orphans from deliberate partial closes).
- No `[PositionReplay] Warning: event touches N lineages` messages.

Include the observed Summary line and a grep of the full stderr for warnings:

```bash
WA_ADJ_BASIS=replay "/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | grep -c "PositionReplay.*Warning"
```

Expected: 0 (or near-0 for edge cases).

- [ ] **Step 5: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: gate Inv2 balance check behind enforceBalance flag (end-of-replay only)"
```

---

## Task 3: User-driven validation

**Files:**
- None modified; CLI-based verification.

Reruns the validation loop that exposed the original bug. Compare `wa diff-positions` output before (7 phantoms, 22 warnings) and after the fix.

- [ ] **Step 1: Capture current replay output**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tee /tmp/wa_diff_after_fix.txt | tail -10
```

Classify each remaining diff:
- `match` — both backends agree, ideal.
- `diff` — both backends see the position but adj-basis values differ. Classify as intended fix vs unexpected regression.
- `only-in-replay` — replay emits a position legacy doesn't. At this stage should be near-zero. Any remaining should be investigated individually.
- `only-in-legacy` — legacy emits something replay doesn't. Any remaining should also be investigated.

- [ ] **Step 2: Verify no multi-touch warnings**

```bash
grep -c "\[PositionReplay\] Warning" /tmp/wa_diff_after_fix.txt
```

Expected: 0.

If > 0, paste a sample warning and we'll investigate.

- [ ] **Step 3: Verify `wa report` under replay still shows the same current open position**

```bash
WA_ADJ_BASIS=replay "/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- report --view simplified 2>&1 | awk '/Open Positions/,/└────┘/' | head -20
```

Expected: same 1-position output as before the fix (GME 15 May 2026 Diagonal, 200 contracts, 1.07 init / 1.07 adj).

- [ ] **Step 4: Sign off or iterate**

If all three checks look clean, Task 14 (validation from the original PositionReplay plan) can be marked resolved and Task 15 (retire legacy) becomes eligible for user-triggered execution.

If any diffs surface that don't fit "intended fix," stop and file precise bug details. Rollback remains `git reset --hard pre-strategygrouper-rewrite`.

No commit for this task (verification only).

---

## Out of Scope

- Changes to rules 1–4, expiry handling, stock-multiplier handling, emission, or `diff-positions`.
- Merge-on-multi-touch (approach 2 from the brainstorm). Only the deferral approach is implemented. If multi-touch warnings persist after this fix, they indicate a separate issue (legitimate overlapping positions) and can be addressed later.
- Time-window batching at the event-builder level (approach 3). Not pursued.
- Retiring legacy `StrategyGrouper.cs`. Still deferred until the user explicitly signs off in Task 3.
