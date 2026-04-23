# PositionReplay Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `StrategyGrouper` (FIFO lot allocation + 5-branch adj-basis computation, ~1100 LOC) with a single new `PositionReplay` class that walks trades chronologically as a state machine and produces `PositionRow` output directly, using an Option-A running cash-flow model.

**Architecture:** A single stateful walker processes trades per-underlying in chronological order. Events (groups of trades with shared `ParentStrategySeq`, or standalones) apply four state-machine rules to a list of active `Lineage` records. Each lineage maintains cumulative cash, open legs, and unit qty. After every event, an invariant check asserts no multi-leg lineage is imbalanced; any imbalance auto-splits into a balanced lineage plus a standalone orphan. At end of replay, active lineages emit parent+leg `PositionRow`s using the existing apportionment pattern. The legacy `StrategyGrouper` stays alive behind a dispatch flag through Phase 2; Phase 3 deletes it once the user accepts the new output.

**Tech Stack:** C# / .NET 10, Spectre.Console for CLI. No test framework exists — verification is via CLI integration (`wa diff-positions` + `wa report`) plus invariant-assertion throws at runtime.

---

## File Map

| File | Change |
|------|--------|
| `PositionReplay.cs` | **Create** — all state machine logic, `Lineage` record, `Event` record, `Execute` entry point |
| `PositionTracker.cs` (line 245) | Modify `BuildPositionRows` to dispatch on `WA_ADJ_BASIS` env var to either legacy `StrategyGrouper.BuildPositionRows` or `PositionReplay.Execute` |
| `DiffPositionsCommand.cs` | **Create** — new CLI subcommand that runs both backends and renders a diff table |
| `Program.cs` (line 43–72) | Register `DiffPositionsCommand` under the command root |
| `StrategyGrouper.cs` | Untouched through Phase 2; deleted wholesale in Phase 3 |

Implementation order interleaves plumbing and state-machine rules so that after each state-machine task, `wa diff-positions` shows concrete partial progress against the legacy backend.

---

## Phase 1A — Scaffolding (Tasks 1–3)

### Task 1: Create `PositionReplay.cs` skeleton

**Files:**
- Create: `PositionReplay.cs`

- [ ] **Step 1: Create the file with all types and an empty `Execute` method**

```csharp
namespace WebullAnalytics;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Linear cash-flow replay that produces PositionRows directly from a trade stream.
/// Replaces StrategyGrouper's FIFO lot allocation + 5-branch adjusted-basis computation.
/// See docs/superpowers/specs/2026-04-23-position-replay-rewrite-design.md for rules.
/// </summary>
internal static class PositionReplay
{
	/// <summary>An active position being tracked during replay.</summary>
	internal sealed class Lineage
	{
		public int Id;
		public string Underlying = "";
		public Dictionary<string, (Side Side, int Qty)> OpenLegs = new(StringComparer.Ordinal);
		public int Multiplier;
		public decimal RunningCash;
		public int UnitQty;
		public decimal FirstEntryCash;
		public int FirstEntryQty;
		public DateTime FirstEntryTimestamp;
		public List<NetDebitTrade> TradeHistory = new();
		public Dictionary<string, int> StockShareCount = new(StringComparer.Ordinal); // share count for stock matchKeys; option legs absent
	}

	/// <summary>One state-machine input — either a strategy order (multiple trades sharing ParentStrategySeq) or a standalone trade.</summary>
	internal sealed class Event
	{
		public DateTime Timestamp;
		public int? ParentStrategySeq;
		public List<Trade> Trades = new();
		public bool IsStrategyOrder => ParentStrategySeq.HasValue;
	}

	/// <summary>
	/// Entry point. Same return shape as PositionTracker.BuildPositionRows so callers are unchanged.
	/// </summary>
	public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
		Execute(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
	{
		// Not yet implemented — returns empty while scaffolding is built out.
		return (new List<PositionRow>(), new Dictionary<int, StrategyAdjustment>(), new Dictionary<string, List<NetDebitTrade>>());
	}
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: skeleton with Lineage, Event types and empty Execute"
```

---

### Task 2: Dispatch flag in `PositionTracker.BuildPositionRows`

**Files:**
- Modify: `PositionTracker.cs:245–250`

- [ ] **Step 1: Replace the method with a dispatch that honors `WA_ADJ_BASIS=replay`**

Find the current signature at line 245:

```csharp
public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones) BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
{
```

Add a dispatch block at the top of the method body:

```csharp
public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones) BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
{
	// Dispatch: legacy StrategyGrouper path vs new PositionReplay path.
	// Controlled by WA_ADJ_BASIS env var ("replay" selects the new path; default is "legacy" through Phase 2).
	var backend = Environment.GetEnvironmentVariable("WA_ADJ_BASIS");
	if (string.Equals(backend, "replay", StringComparison.OrdinalIgnoreCase))
		return PositionReplay.Execute(positions, tradeIndex, allTrades);

	// ... (existing legacy body continues here)
```

Preserve the existing method body verbatim below the dispatch block. The legacy path stays the default for compatibility; only `WA_ADJ_BASIS=replay` opts into the new backend.

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Smoke test both paths return without crashing**

Legacy path (default):

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- report --view simplified 2>&1 | head -5
```

Expected: report renders normally (as it does today).

Replay path (via env var):

```bash
WA_ADJ_BASIS=replay "/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- report --view simplified 2>&1 | head -10
```

Expected: report runs but shows no open positions (empty replay returns empty rows). No crash.

- [ ] **Step 4: Commit**

```bash
git add PositionTracker.cs
git commit -m "PositionTracker: dispatch BuildPositionRows to PositionReplay when WA_ADJ_BASIS=replay"
```

---

### Task 3: `wa diff-positions` CLI subcommand

**Files:**
- Create: `DiffPositionsCommand.cs`
- Modify: `Program.cs` (command registration)

- [ ] **Step 1: Create `DiffPositionsCommand.cs`**

```csharp
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace WebullAnalytics;

internal sealed class DiffPositionsSettings : CommandSettings
{
	[CommandOption("--source")]
	[Description("Data source: 'api' (JSONL, default) or 'export' (Webull CSV exports)")]
	public string Source { get; set; } = "api";
}

/// <summary>
/// Runs both the legacy StrategyGrouper path and the new PositionReplay path against the same trade stream,
/// then renders a table of PositionRows where InitialAvgPrice or AdjustedAvgPrice differ. Used during Phase 2
/// validation of the PositionReplay rewrite; to be removed after Phase 3.
/// </summary>
internal sealed class DiffPositionsCommand : AsyncCommand<DiffPositionsSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, DiffPositionsSettings settings, CancellationToken cancellation)
	{
		// Load trades once (shared between both backends).
		var ordersPath = Program.ResolvePath(Program.OrdersPath);
		if (!File.Exists(ordersPath))
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] Orders file '{Markup.Escape(ordersPath)}' does not exist.");
			return 2;
		}
		var (trades, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
		var (_, positions, _) = PositionTracker.ComputeReport(trades, feeLookup: feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);

		// Run legacy.
		Environment.SetEnvironmentVariable("WA_ADJ_BASIS", "legacy");
		var (legacyRows, _, _) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		// Run replay.
		Environment.SetEnvironmentVariable("WA_ADJ_BASIS", "replay");
		var (replayRows, _, _) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);
		Environment.SetEnvironmentVariable("WA_ADJ_BASIS", null);

		// Build a key → row map for each. Key = Instrument + "|" + Expiry (matches visible display).
		string RowKey(PositionRow r) => $"{r.Instrument}|{r.Expiry?.ToString("yyyy-MM-dd") ?? "-"}|{r.Side}";
		var legacyByKey = legacyRows.Where(r => !r.IsStrategyLeg).ToDictionary(RowKey, r => r);
		var replayByKey = replayRows.Where(r => !r.IsStrategyLeg).ToDictionary(RowKey, r => r);
		var allKeys = legacyByKey.Keys.Union(replayByKey.Keys).OrderBy(k => k).ToList();

		var table = new Table().Title("[bold]Position adj-basis diff — legacy vs replay[/]");
		table.AddColumn("Position");
		table.AddColumn("Qty");
		table.AddColumn(new TableColumn("Legacy init").RightAligned());
		table.AddColumn(new TableColumn("Replay init").RightAligned());
		table.AddColumn(new TableColumn("Legacy adj").RightAligned());
		table.AddColumn(new TableColumn("Replay adj").RightAligned());
		table.AddColumn(new TableColumn("Δ adj").RightAligned());

		int matches = 0, diffs = 0, missingLeft = 0, missingRight = 0;
		foreach (var key in allKeys)
		{
			legacyByKey.TryGetValue(key, out var L);
			replayByKey.TryGetValue(key, out var R);
			if (L == null) { missingLeft++; table.AddRow(Markup.Escape(key), "-", "[red]missing[/]", "-", "-", R?.AdjustedAvgPrice?.ToString("F2") ?? "-", "-"); continue; }
			if (R == null) { missingRight++; table.AddRow(Markup.Escape(key), L.Qty.ToString(), L.InitialAvgPrice?.ToString("F2") ?? "-", "[red]missing[/]", L.AdjustedAvgPrice?.ToString("F2") ?? "-", "-", "-"); continue; }
			var initMatch = Math.Abs((L.InitialAvgPrice ?? 0m) - (R.InitialAvgPrice ?? 0m)) < 0.01m;
			var adjMatch = Math.Abs((L.AdjustedAvgPrice ?? 0m) - (R.AdjustedAvgPrice ?? 0m)) < 0.01m;
			if (initMatch && adjMatch) { matches++; continue; }
			diffs++;
			var delta = (R.AdjustedAvgPrice ?? 0m) - (L.AdjustedAvgPrice ?? 0m);
			table.AddRow(Markup.Escape(key), L.Qty.ToString(), L.InitialAvgPrice?.ToString("F2") ?? "-", R.InitialAvgPrice?.ToString("F2") ?? "-", L.AdjustedAvgPrice?.ToString("F2") ?? "-", R.AdjustedAvgPrice?.ToString("F2") ?? "-", (delta >= 0m ? "+" : "") + delta.ToString("F2"));
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine($"[bold]Summary:[/] {matches} match, {diffs} diff, {missingLeft} only-in-replay, {missingRight} only-in-legacy.");
		await Task.CompletedTask;
		return 0;
	}
}
```

- [ ] **Step 2: Register the command in `Program.cs`**

In the `app.Configure` block (around line 39), after `config.AddCommand<ReportCommand>("report");`, add:

```csharp
config.AddCommand<DiffPositionsCommand>("diff-positions");
```

- [ ] **Step 3: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 4: Run the command; observe all positions show as "only-in-legacy"**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -20
```

Expected: every open position shows as missing from replay (since `Execute` returns empty). Summary line reads `X match, 0 diff, 0 only-in-replay, N only-in-legacy` where N > 0.

- [ ] **Step 5: Commit**

```bash
git add DiffPositionsCommand.cs Program.cs
git commit -m "diff-positions: add CLI subcommand for legacy-vs-replay comparison"
```

---

## Phase 1B — State machine (Tasks 4–9)

### Task 4: Event builder + dispatcher stub

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Add `BuildEventsPerUnderlying` and `ReplayUnderlying` methods; wire into `Execute`**

In `PositionReplay.cs`, replace the empty `Execute` body with:

```csharp
public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
	Execute(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
{
	var eventsPerUnderlying = BuildEventsPerUnderlying(allTrades);

	var allLineages = new List<Lineage>();
	int lineageIdCounter = 0;
	foreach (var (underlying, events) in eventsPerUnderlying)
	{
		var active = new List<Lineage>();
		foreach (var evt in events)
			ApplyEvent(active, evt, underlying, ref lineageIdCounter);
		allLineages.AddRange(active);
	}

	return EmitRows(allLineages);
}

/// <summary>Groups trades into Events (strategy orders share ParentStrategySeq; standalones are each their own event),
/// keyed by underlying, sorted chronologically within each underlying.</summary>
private static Dictionary<string, List<Event>> BuildEventsPerUnderlying(List<Trade> allTrades)
{
	var byUnderlying = new Dictionary<string, List<Event>>(StringComparer.Ordinal);

	// Strategy orders: group option-leg trades by ParentStrategySeq. Skip the OptionStrategy parent trade row itself —
	// the parent's cash is derived from the legs. Also groups same-timestamp stock+option trades (covered calls etc.)
	// when they share a ParentStrategySeq.
	var legsByParentSeq = allTrades
		.Where(t => t.ParentStrategySeq.HasValue && t.Asset != Asset.OptionStrategy)
		.GroupBy(t => t.ParentStrategySeq!.Value);
	foreach (var group in legsByParentSeq)
	{
		var trades = group.OrderBy(t => t.Seq).ToList();
		var underlying = ExtractUnderlying(trades[0]);
		var evt = new Event { Timestamp = trades.Min(t => t.Timestamp), ParentStrategySeq = group.Key, Trades = trades };
		if (!byUnderlying.TryGetValue(underlying, out var list)) { list = new List<Event>(); byUnderlying[underlying] = list; }
		list.Add(evt);
	}

	// Standalones: trades without ParentStrategySeq, not strategy parents.
	foreach (var t in allTrades.Where(t => !t.ParentStrategySeq.HasValue && t.Asset != Asset.OptionStrategy))
	{
		var underlying = ExtractUnderlying(t);
		var evt = new Event { Timestamp = t.Timestamp, ParentStrategySeq = null, Trades = new List<Trade> { t } };
		if (!byUnderlying.TryGetValue(underlying, out var list)) { list = new List<Event>(); byUnderlying[underlying] = list; }
		list.Add(evt);
	}

	foreach (var (_, list) in byUnderlying)
		list.Sort((a, b) => a.Timestamp != b.Timestamp ? a.Timestamp.CompareTo(b.Timestamp) : a.Trades[0].Seq.CompareTo(b.Trades[0].Seq));

	return byUnderlying;
}

/// <summary>Extracts the underlying ticker from a trade (stock uses MatchKey; option uses the root of the OCC symbol).</summary>
private static string ExtractUnderlying(Trade t)
{
	if (t.Asset == Asset.Stock) return t.MatchKey;
	var parsed = MatchKeys.ParseOption(t.MatchKey);
	return parsed?.parsed.Root ?? t.MatchKey;
}

/// <summary>Applies one event to the active-lineage list. Rules are added in subsequent tasks; for now this is a no-op.</summary>
private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	// Rules added in Tasks 5–8.
}

/// <summary>Emits PositionRow output from finalized lineages. Filled out in Task 10.</summary>
private static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
	EmitRows(List<Lineage> lineages)
{
	return (new List<PositionRow>(), new Dictionary<int, StrategyAdjustment>(), new Dictionary<string, List<NetDebitTrade>>());
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Run diff-positions; confirm scaffolding still produces empty replay output**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -5
```

Expected: summary still reads `0 only-in-replay, N only-in-legacy`.

- [ ] **Step 4: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: add event builder and per-underlying processing scaffold"
```

---

### Task 5: Implement Rule 4 — strategy order event

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Add `ApplyStrategyOrderEvent` + route from `ApplyEvent`**

In `PositionReplay.cs`, replace the `ApplyEvent` stub with:

```csharp
private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	if (evt.IsStrategyOrder) ApplyStrategyOrderEvent(active, evt, underlying, ref lineageIdCounter);
	// Standalone rules added in Tasks 6–8.
}

private static void ApplyStrategyOrderEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	// Determine which active lineages this event touches (any event leg matches an open leg).
	var touchedLineages = new HashSet<Lineage>();
	foreach (var t in evt.Trades)
	{
		foreach (var lin in active)
		{
			if (lin.OpenLegs.ContainsKey(t.MatchKey))
				touchedLineages.Add(lin);
		}
	}

	Lineage target;
	if (touchedLineages.Count == 0)
	{
		// Zero touched → new lineage with all event legs.
		target = new Lineage
		{
			Id = ++lineageIdCounter,
			Underlying = underlying,
			Multiplier = evt.Trades[0].Multiplier,
			FirstEntryTimestamp = evt.Timestamp
		};
		active.Add(target);
	}
	else if (touchedLineages.Count == 1)
	{
		target = touchedLineages.First();
	}
	else
	{
		// Multiple touched: oldest by FirstEntryTimestamp wins (deterministic tiebreaker). Log for audit.
		target = touchedLineages.OrderBy(l => l.FirstEntryTimestamp).First();
		Console.Error.WriteLine($"[PositionReplay] Warning: event at {evt.Timestamp} touches {touchedLineages.Count} lineages on {underlying}; assigned to oldest (id={target.Id}).");
	}

	ApplyEventToLineage(target, evt, isNewLineage: touchedLineages.Count == 0);
}

/// <summary>Updates a lineage's open legs and running cash for one event. For a new lineage, also sets FirstEntry*.</summary>
private static void ApplyEventToLineage(Lineage lin, Event evt, bool isNewLineage)
{
	// Compute event's total cash impact: Σ over legs of (side_sign × qty × price × multiplier), where side_sign = +1 for Buy, −1 for Sell.
	decimal eventCash = 0m;
	int eventQty = 0;
	foreach (var t in evt.Trades)
	{
		var signedCash = (t.Side == Side.Buy ? 1m : -1m) * t.Qty * t.Price * t.Multiplier;
		eventCash += signedCash;
		eventQty = Math.Max(eventQty, t.Qty); // for strategy orders, all legs share qty
		lin.TradeHistory.Add(new NetDebitTrade(t.Timestamp, t.Instrument, t.Side, t.Qty, t.Price, signedCash));
	}

	lin.RunningCash += eventCash;

	if (isNewLineage)
	{
		lin.FirstEntryCash = eventCash;
		lin.FirstEntryQty = eventQty;
	}

	// Apply each leg: match existing open leg (reduce/add) or add as new leg.
	foreach (var t in evt.Trades)
	{
		var signedQty = t.Side == Side.Buy ? t.Qty : -t.Qty;
		if (lin.OpenLegs.TryGetValue(t.MatchKey, out var existing))
		{
			var newSigned = (existing.Side == Side.Buy ? existing.Qty : -existing.Qty) + signedQty;
			if (newSigned == 0)
				lin.OpenLegs.Remove(t.MatchKey);
			else
				lin.OpenLegs[t.MatchKey] = (newSigned > 0 ? Side.Buy : Side.Sell, Math.Abs(newSigned));
		}
		else
		{
			lin.OpenLegs[t.MatchKey] = (t.Side, t.Qty);
		}
	}

	// UnitQty = min of leg qtys across open legs (balanced after strategy orders in the common case).
	lin.UnitQty = lin.OpenLegs.Values.Count > 0 ? lin.OpenLegs.Values.Min(v => v.Qty) : 0;
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Smoke test — replay still returns empty rows because `EmitRows` is still a stub**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -3
```

Expected: still `0 only-in-replay, N only-in-legacy` (EmitRows is implemented in Task 10).

- [ ] **Step 4: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: implement Rule 4 — strategy order event processing"
```

---

### Task 6: Implement Rule 1 — standalone reduce

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Add `ApplyStandaloneEvent` + `TryApplyReduce`**

In `PositionReplay.cs`, update `ApplyEvent` to handle standalones, and add the reduce logic:

```csharp
private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	if (evt.IsStrategyOrder) { ApplyStrategyOrderEvent(active, evt, underlying, ref lineageIdCounter); return; }
	ApplyStandaloneEvent(active, evt, underlying, ref lineageIdCounter);
}

private static void ApplyStandaloneEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	var t = evt.Trades[0];

	// Rule 1: if the trade's matchKey exists in an active lineage with OPPOSITE direction, this is a reduce.
	foreach (var lin in active)
	{
		if (!lin.OpenLegs.TryGetValue(t.MatchKey, out var existing)) continue;
		if (existing.Side != t.Side)
		{
			ApplyEventToLineage(lin, evt, isNewLineage: false);
			return;
		}
	}

	// Rules 2 and 3 handled in later tasks.
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: implement Rule 1 — standalone reduce"
```

---

### Task 7: Implement Rule 3 — standalone new-leg + orphan matching

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Extend `ApplyStandaloneEvent` with new-leg path**

In `PositionReplay.cs`, update `ApplyStandaloneEvent`:

```csharp
private static void ApplyStandaloneEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	var t = evt.Trades[0];

	// Rule 1: reduce existing open leg (opposite direction).
	foreach (var lin in active)
	{
		if (!lin.OpenLegs.TryGetValue(t.MatchKey, out var existing)) continue;
		if (existing.Side != t.Side)
		{
			ApplyEventToLineage(lin, evt, isNewLineage: false);
			return;
		}
	}

	// Rule 2 handled in Task 8.

	// Rule 3: new-leg trade. Search orphans in the same bucket (underlying + call/put) with UnitQty == trade.Qty.
	var bucket = GetLegBucket(t);
	var orphansInBucket = active
		.Where(lin => lin.OpenLegs.Count == 1 && lin.UnitQty == t.Qty && LineageBucket(lin) == bucket)
		.ToList();

	if (orphansInBucket.Count == 1)
	{
		ApplyEventToLineage(orphansInBucket[0], evt, isNewLineage: false);
		return;
	}

	// Zero or multiple orphans: spawn new standalone lineage.
	var newLineage = new Lineage
	{
		Id = ++lineageIdCounter,
		Underlying = underlying,
		Multiplier = t.Multiplier,
		FirstEntryTimestamp = evt.Timestamp
	};
	active.Add(newLineage);
	ApplyEventToLineage(newLineage, evt, isNewLineage: true);
}

/// <summary>Bucket key: stock vs per-call-put. Two standalone legs in different buckets cannot match as orphan.</summary>
private static string GetLegBucket(Trade t)
{
	if (t.Asset == Asset.Stock) return "stock";
	var parsed = MatchKeys.ParseOption(t.MatchKey);
	return parsed?.parsed.CallPut == "C" ? "call" : "put";
}

/// <summary>A lineage's bucket is the bucket of its first (and in single-leg lineages, only) open leg.</summary>
private static string LineageBucket(Lineage lin)
{
	if (lin.OpenLegs.Count == 0) return "stock"; // deactivated; bucket irrelevant
	var firstKey = lin.OpenLegs.Keys.First();
	if (firstKey.StartsWith("option:", StringComparison.Ordinal))
	{
		var parsed = MatchKeys.ParseOption(firstKey);
		return parsed?.parsed.CallPut == "C" ? "call" : "put";
	}
	return "stock";
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: implement Rule 3 — standalone new-leg with orphan qty-match"
```

---

### Task 8: Implement Rule 2 — standalone add (same direction)

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Extend `ApplyStandaloneEvent` with the same-direction-add branch between Rule 1 and Rule 3**

In `PositionReplay.cs`, find the comment `// Rule 2 handled in Task 8.` inside `ApplyStandaloneEvent` and replace it with:

```csharp
	// Rule 2: standalone add (same direction as an existing open leg).
	foreach (var lin in active)
	{
		if (!lin.OpenLegs.TryGetValue(t.MatchKey, out var existing)) continue;
		if (existing.Side == t.Side)
		{
			if (lin.OpenLegs.Count == 1)
			{
				// Single-leg target: grow qty.
				ApplyEventToLineage(lin, evt, isNewLineage: false);
			}
			else
			{
				// Multi-leg target: adding would break balance. Spawn a new standalone lineage for the add.
				var spawn = new Lineage
				{
					Id = ++lineageIdCounter,
					Underlying = underlying,
					Multiplier = t.Multiplier,
					FirstEntryTimestamp = evt.Timestamp
				};
				active.Add(spawn);
				ApplyEventToLineage(spawn, evt, isNewLineage: true);
			}
			return;
		}
	}
```

The full `ApplyStandaloneEvent` now ordering is: Rule 1 (reduce) → Rule 2 (add same direction) → Rule 3 (new leg).

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: implement Rule 2 — standalone add same direction"
```

---

### Task 9: Imbalance auto-split

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Add `RebalanceLineage` + invoke after every `ApplyEventToLineage` mutation**

In `PositionReplay.cs`, at the very end of `ApplyEventToLineage` (after `lin.UnitQty = ...`), append:

```csharp
	// Post-event invariant: every multi-leg lineage must be balanced (all open legs share one qty).
	// Any imbalance is split off immediately — keeping the lineage balanced at the common-qty minimum
	// and spawning a new standalone lineage for the excess, with proportional cash allocation.
	// RebalanceLineage appends any spawned lineages to the active list (via an out parameter pattern
	// we implement by having the state-machine rules observe the active list after each event).
}
```

Then add the helper method and wire it in by restructuring to pass `active` + id counter through:

Change `ApplyEventToLineage` signature to take the active list and lineage-id counter by reference (since splits can spawn new lineages):

```csharp
private static void ApplyEventToLineage(Lineage lin, Event evt, bool isNewLineage, List<Lineage> active, ref int lineageIdCounter)
{
	// (existing body through UnitQty calculation)

	// Post-event imbalance auto-split.
	RebalanceLineage(lin, evt, active, ref lineageIdCounter);
}

/// <summary>
/// If `lin` has any open leg whose qty exceeds the minimum leg qty, split the excess into a new
/// standalone lineage. Proportionally allocates cash: the split lineage gets (excess/origUnit) × cash.
/// Original lineage's RunningCash and FirstEntryCash are scaled down by (min/origUnit).
/// </summary>
private static void RebalanceLineage(Lineage lin, Event causingEvent, List<Lineage> active, ref int lineageIdCounter)
{
	if (lin.OpenLegs.Count < 2) return;
	var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
	var maxQty = lin.OpenLegs.Values.Max(v => v.Qty);
	if (minQty == maxQty) return; // balanced

	// For each leg whose qty > minQty, split off the excess as a new standalone lineage.
	var imbalancedKeys = lin.OpenLegs.Where(kv => kv.Value.Qty > minQty).Select(kv => kv.Key).ToList();
	var origUnit = lin.UnitQty;

	foreach (var key in imbalancedKeys)
	{
		var (side, qty) = lin.OpenLegs[key];
		var excess = qty - minQty;

		// Proportional cash: spawn gets (excess / origUnit) of original cash values.
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
		// Copy a portion of trade history as a single synthetic split marker; downstream AdjustmentReportBuilder
		// reads this list for the "how we got here" display.
		spawn.TradeHistory.Add(new NetDebitTrade(causingEvent.Timestamp, $"[split from lineage {lin.Id}]", Side.Buy, excess, 0m, spawn.RunningCash));
		active.Add(spawn);

		// Original lineage keeps the minQty on this leg; cash scales by complementary ratio.
		lin.OpenLegs[key] = (side, minQty);
	}

	var keepRatio = (decimal)minQty / origUnit;
	lin.RunningCash *= keepRatio;
	lin.FirstEntryCash *= keepRatio;
	lin.FirstEntryQty = minQty;
	lin.UnitQty = minQty;
}
```

Update callers of `ApplyEventToLineage` to pass `active` and `ref lineageIdCounter`. Callers are:

- `ApplyStrategyOrderEvent` (one call)
- `ApplyStandaloneEvent` (three call sites: Rule 1, Rule 2 single-leg, Rule 2 multi-leg spawn, Rule 3 orphan join, Rule 3 new lineage)

Each call site already has `active` and `lineageIdCounter` in scope. Update each `ApplyEventToLineage(lin, evt, ...)` call to `ApplyEventToLineage(lin, evt, ..., active, ref lineageIdCounter)`.

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: imbalance auto-split with proportional cash allocation"
```

---

## Phase 1C — Output (Tasks 10–14)

### Task 10: `EmitRows` — Lineage → `PositionRow`

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Replace the `EmitRows` stub with a full implementation**

In `PositionReplay.cs`, replace the empty `EmitRows` with:

```csharp
private static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
	EmitRows(List<Lineage> lineages)
{
	var rows = new List<PositionRow>();
	var adjustments = new Dictionary<int, StrategyAdjustment>();
	var singleLegStandalones = new Dictionary<string, List<NetDebitTrade>>();

	foreach (var lin in lineages)
	{
		if (lin.OpenLegs.Count == 0) continue;

		var parentAdj = lin.UnitQty > 0 ? lin.RunningCash / (lin.UnitQty * lin.Multiplier) : 0m;
		var parentInit = lin.FirstEntryQty > 0 ? lin.FirstEntryCash / (lin.FirstEntryQty * lin.Multiplier) : 0m;

		// Single-leg lineage: emit one PositionRow, no parent.
		if (lin.OpenLegs.Count == 1)
		{
			var (matchKey, leg) = lin.OpenLegs.First();
			var (asset, optionKind, expiry, instrument) = ResolveLegMetadata(matchKey, lin);
			rows.Add(new PositionRow(
				Instrument: instrument,
				Asset: asset,
				OptionKind: optionKind,
				Side: leg.Side,
				Qty: leg.Qty,
				AvgPrice: Math.Abs(parentAdj),
				Expiry: expiry,
				IsStrategyLeg: false,
				InitialAvgPrice: Math.Abs(parentInit),
				AdjustedAvgPrice: Math.Abs(parentAdj),
				MatchKey: matchKey
			));

			if (lin.TradeHistory.Count > 1)
				singleLegStandalones[matchKey] = lin.TradeHistory;
			continue;
		}

		// Multi-leg lineage: emit parent + one leg row per open leg.
		var strategyKind = ClassifyLineage(lin);
		var parentInstrument = BuildParentInstrument(lin);
		var longestExpiry = ResolveLongestExpiry(lin);
		var parentSide = lin.RunningCash >= 0m ? Side.Buy : Side.Sell;

		rows.Add(new PositionRow(
			Instrument: parentInstrument,
			Asset: Asset.OptionStrategy,
			OptionKind: strategyKind,
			Side: parentSide,
			Qty: lin.UnitQty,
			AvgPrice: Math.Abs(parentAdj),
			Expiry: longestExpiry,
			IsStrategyLeg: false,
			InitialAvgPrice: Math.Abs(parentInit),
			AdjustedAvgPrice: Math.Abs(parentAdj)
		));

		// Per-leg rows: InitialAvgPrice = leg's own entry price (from trade history filtered by matchKey);
		// AdjustedAvgPrice = init + apportioned delta so signed sum equals parent's adj.
		var legEntryPrices = ComputeLegEntryPrices(lin);
		var legInitSum = 0m;
		foreach (var (mk, leg) in lin.OpenLegs)
		{
			var entryPrice = legEntryPrices.GetValueOrDefault(mk, 0m);
			legInitSum += (leg.Side == Side.Buy ? 1m : -1m) * entryPrice;
		}
		var perLegAdjDelta = Math.Abs(parentAdj) * (parentSide == Side.Buy ? 1m : -1m) - legInitSum;

		// Allocate the entire delta to a single "target leg" so per-leg signed sum equals parent adj.
		// Convention: prefer the first Buy leg (matches legacy ReconcileLegPricesToParent); for
		// credit-only structures (e.g., short strangles) fall back to the first Sell leg.
		var longLegKey = lin.OpenLegs.FirstOrDefault(kv => kv.Value.Side == Side.Buy).Key
			?? lin.OpenLegs.First().Key;

		foreach (var (mk, leg) in lin.OpenLegs.OrderByDescending(kv => kv.Key))
		{
			var (asset, optionKind, expiry, instrument) = ResolveLegMetadata(mk, lin);
			var initPrice = legEntryPrices.GetValueOrDefault(mk, 0m);
			var adjPrice = (mk == longLegKey) ? initPrice + perLegAdjDelta : initPrice;
			rows.Add(new PositionRow(
				Instrument: instrument,
				Asset: asset,
				OptionKind: optionKind,
				Side: leg.Side,
				Qty: leg.Qty,
				AvgPrice: initPrice,
				Expiry: expiry,
				IsStrategyLeg: true,
				InitialAvgPrice: initPrice,
				AdjustedAvgPrice: adjPrice,
				MatchKey: mk
			));
		}

		adjustments[lin.Id] = new StrategyAdjustment(lin.TradeHistory, lin.RunningCash, null, lin.FirstEntryCash);
	}

	return (rows, adjustments, singleLegStandalones);
}

/// <summary>Classifies the lineage's open-leg shape into a strategy kind label (Diagonal, Calendar, etc.).</summary>
private static string ClassifyLineage(Lineage lin)
{
	var parsedLegs = lin.OpenLegs.Keys
		.Select(mk => MatchKeys.ParseOption(mk)?.parsed)
		.Where(p => p != null)
		.ToList();
	if (parsedLegs.Count == 0) return "Stock";
	var distinctExpiries = parsedLegs.Select(p => p!.ExpiryDate).Distinct().Count();
	var distinctStrikes = parsedLegs.Select(p => p!.Strike).Distinct().Count();
	var distinctCallPut = parsedLegs.Select(p => p!.CallPut).Distinct().Count();
	return ParsingHelpers.ClassifyStrategyKind(parsedLegs.Count, distinctExpiries, distinctStrikes, distinctCallPut);
}

private static DateTime ResolveLongestExpiry(Lineage lin)
{
	return lin.OpenLegs.Keys
		.Select(mk => MatchKeys.ParseOption(mk)?.parsed.ExpiryDate)
		.Where(d => d.HasValue)
		.Select(d => d!.Value)
		.DefaultIfEmpty(DateTime.MinValue)
		.Max();
}

private static string BuildParentInstrument(Lineage lin)
{
	var longestExpiry = ResolveLongestExpiry(lin);
	return $"{lin.Underlying} {Formatters.FormatOptionDate(longestExpiry)}";
}

private static (Asset asset, string optionKind, DateTime? expiry, string instrument) ResolveLegMetadata(string matchKey, Lineage lin)
{
	if (matchKey.StartsWith("option:", StringComparison.Ordinal))
	{
		var parsed = MatchKeys.ParseOption(matchKey);
		if (parsed != null)
		{
			var p = parsed.Value.parsed;
			return (Asset.Option, ParsingHelpers.CallPutDisplayName(p.CallPut), p.ExpiryDate, Formatters.FormatOptionDisplay(p.Root, p.ExpiryDate, p.Strike));
		}
	}
	return (Asset.Stock, "-", null, lin.Underlying);
}

private static Dictionary<string, decimal> ComputeLegEntryPrices(Lineage lin)
{
	// For each open leg, compute its weighted-average entry price from the lineage's trade history.
	// Entry = trades whose matchKey is this leg's matchKey AND whose direction matches the leg's current side
	// (this excludes reducing / roll-close trades on the same matchKey that already left via Rule 1).
	var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
	foreach (var (mk, leg) in lin.OpenLegs)
	{
		var relevant = lin.TradeHistory.Where(h => h.Instrument != $"[split from lineage {lin.Id}]" && SameMatchKey(h, mk, leg.Side)).ToList();
		if (relevant.Count == 0) { result[mk] = 0m; continue; }
		var totalQty = relevant.Sum(h => h.Qty);
		var weighted = relevant.Sum(h => h.Price * h.Qty) / Math.Max(totalQty, 1);
		result[mk] = weighted;
	}
	return result;
}

private static bool SameMatchKey(NetDebitTrade t, string mk, Side openSide)
{
	// NetDebitTrade doesn't carry matchKey directly; infer from Instrument. For option legs the instrument
	// includes ticker + expiry + strike; match against the lineage's open-leg matchKey.
	var parsed = MatchKeys.ParseOption(mk);
	if (parsed == null) return false;
	var expected = Formatters.FormatOptionDisplay(parsed.Value.parsed.Root, parsed.Value.parsed.ExpiryDate, parsed.Value.parsed.Strike);
	return t.Instrument == expected && t.Side == openSide;
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Run diff-positions; observe partial replay output now appears**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -15
```

Expected: summary shows some `match` and some `diff` entries. No hard crashes. Some rows may still show `only-in-legacy` for exotic cases handled in later tasks (expiries, stock combos).

- [ ] **Step 4: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: emit PositionRows with parent, legs, apportionment, and adjustments dict"
```

---

### Task 11: Invariant assertions

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Add `AssertInvariants` + call it after every event and at end of replay**

In `PositionReplay.cs`:

```csharp
/// <summary>Called at the end of every event's state transition and at end of replay.
/// Throws with diagnostic state dump if any invariant is violated — those indicate state-machine bugs,
/// not valid runtime state.</summary>
private static void AssertInvariants(IEnumerable<Lineage> active, string context)
{
	foreach (var lin in active)
	{
		if (lin.OpenLegs.Count == 0) continue;

		// Inv1: every open leg has positive qty.
		foreach (var (mk, leg) in lin.OpenLegs)
			if (leg.Qty <= 0)
				throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} has open leg {mk} with non-positive qty {leg.Qty} (context: {context})");

		// Inv2: multi-leg lineages are balanced.
		if (lin.OpenLegs.Count > 1)
		{
			var qtys = lin.OpenLegs.Values.Select(v => v.Qty).Distinct().ToList();
			if (qtys.Count > 1)
				throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} is imbalanced: legs have qtys [{string.Join(",", qtys)}] (context: {context})");
		}

		// Inv3: UnitQty > 0 for active lineages.
		if (lin.UnitQty <= 0)
			throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} has non-positive UnitQty {lin.UnitQty} (context: {context})");

		// Inv4: RunningCash is finite.
		if (decimal.IsNegativeInfinity(lin.RunningCash) || decimal.IsPositiveInfinity(lin.RunningCash))
			throw new InvalidOperationException($"Invariant: lineage {lin.Id} on {lin.Underlying} has non-finite RunningCash {lin.RunningCash} (context: {context})");
	}
}
```

Invoke at end of `ApplyEvent`:

```csharp
private static void ApplyEvent(List<Lineage> active, Event evt, string underlying, ref int lineageIdCounter)
{
	if (evt.IsStrategyOrder) ApplyStrategyOrderEvent(active, evt, underlying, ref lineageIdCounter);
	else ApplyStandaloneEvent(active, evt, underlying, ref lineageIdCounter);

	// Deactivate any lineage whose open legs are all zero.
	active.RemoveAll(lin => lin.OpenLegs.Count == 0);

	AssertInvariants(active, $"after event at {evt.Timestamp}");
}
```

Also invoke at end of `Execute`:

```csharp
foreach (var (underlying, events) in eventsPerUnderlying)
{
	var active = new List<Lineage>();
	foreach (var evt in events)
		ApplyEvent(active, evt, underlying, ref lineageIdCounter);
	AssertInvariants(active, $"end of replay for {underlying}");
	allLineages.AddRange(active);
}
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Run diff-positions; confirm no invariant throws**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -10
```

Expected: diff table renders, no `InvalidOperationException` in output. If any invariants fail, the exception message identifies which lineage triggered the issue — that's a bug in an earlier task, fix it before proceeding.

- [ ] **Step 4: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: add invariant assertions at event and replay boundaries"
```

---

### Task 12: Expiry handling — port from `AdjustForExpiredStrategyLegs`

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Inspect the current `AdjustForExpiredStrategyLegs` for its ITM cash formula**

Run: `grep -n "expired\|intrinsic\|Expiry.*<" /mnt/c/dev/WebullAnalytics/StrategyGrouper.cs | head -20`

Expected: this surfaces the ITM-at-expiry cash-impact formula used by the legacy code. Read lines 652–743 to understand the exact formula used; the rewrite must match it verbatim.

- [ ] **Step 2: Add `ApplyExpiries` called once per underlying before end-of-replay**

In `PositionReplay.cs`, add:

```csharp
/// <summary>
/// Ports AdjustForExpiredStrategyLegs semantics: any open leg whose expiry is past the evaluation date
/// gets a synthetic terminal event. OTM → silent removal (no cash). ITM → synthetic assignment with
/// intrinsic cash impact matching the legacy formula at StrategyGrouper.cs:652–743.
///
/// Exact ITM formula: the legacy code synthesizes a close-trade at intrinsic value. For a long call ITM
/// (spot > strike at expiry): synthesize a sell at (spot - strike) per share, credit lineage. For a short
/// call ITM: synthesize a buy at (spot - strike), debit. For long put ITM: synthesize sell at (strike -
/// spot). For short put ITM: synthesize buy at (strike - spot). Spot at expiry is unknown for historical
/// expiries (same limitation today) — the legacy code treats unknown spot as OTM; we inherit that.
/// </summary>
private static void ApplyExpiries(List<Lineage> active, DateTime evaluationDate)
{
	foreach (var lin in active.ToList())
	{
		var expiredLegs = lin.OpenLegs
			.Where(kv => {
				var parsed = MatchKeys.ParseOption(kv.Key);
				return parsed.HasValue && parsed.Value.parsed.ExpiryDate.Date < evaluationDate.Date;
			})
			.Select(kv => kv.Key)
			.ToList();
		foreach (var mk in expiredLegs)
		{
			// Legacy behavior: without spot-at-expiry data, treat as OTM and silently remove.
			lin.OpenLegs.Remove(mk);
			lin.TradeHistory.Add(new NetDebitTrade(evaluationDate, $"[expired OTM: {mk}]", Side.Sell, 0, 0m, 0m));
		}

		if (lin.OpenLegs.Count == 0) continue;

		// Rebalance if expiry removed a leg asymmetrically.
		if (lin.OpenLegs.Count > 0 && lin.OpenLegs.Values.Select(v => v.Qty).Distinct().Count() > 1)
		{
			var minQty = lin.OpenLegs.Values.Min(v => v.Qty);
			lin.UnitQty = minQty;
			// Note: expiry-induced imbalance is atypical because expiries usually affect the short leg
			// (earlier expiry) of a diagonal/calendar. Per Option-A we don't proportionally split here —
			// we just reduce UnitQty to the surviving shared qty. The lineage becomes effectively single-leg.
		}
	}

	active.RemoveAll(lin => lin.OpenLegs.Count == 0);
}
```

In `Execute`, call it after per-underlying replay:

```csharp
var allLineages = new List<Lineage>();
int lineageIdCounter = 0;
var evaluationDate = EvaluationDate.Today;
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

- [ ] **Step 3: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 4: Run diff-positions; confirm expired-leg positions stop showing as only-in-legacy**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -10
```

Expected: fewer `only-in-legacy` entries; some expired positions now appear as matches or diffs in replay output.

- [ ] **Step 5: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: handle expiry with OTM-default (matches legacy AdjustForExpiredStrategyLegs)"
```

---

### Task 13: Stock + option multiplier handling

**Files:**
- Modify: `PositionReplay.cs`

- [ ] **Step 1: Track share counts for stock legs, convert to option-equivalent qty at event application time**

In `PositionReplay.cs`, modify `ApplyEventToLineage` to handle stock legs specially:

Find the leg-application loop inside `ApplyEventToLineage`:

```csharp
foreach (var t in evt.Trades)
{
	var signedQty = t.Side == Side.Buy ? t.Qty : -t.Qty;
	...
}
```

Replace with:

```csharp
foreach (var t in evt.Trades)
{
	// For stock legs, the multiplier difference matters: 100 shares = 1 option-equivalent qty.
	// Internally we track the option-equivalent qty in OpenLegs (so matching/balance work uniformly),
	// and the actual share count in StockShareCount for downstream display.
	int qtyInLineageUnits;
	if (t.Asset == Asset.Stock)
	{
		qtyInLineageUnits = t.Qty / 100; // assume 100-share lots align with option contract count
		lin.StockShareCount[t.MatchKey] = lin.StockShareCount.GetValueOrDefault(t.MatchKey) + (t.Side == Side.Buy ? t.Qty : -t.Qty);
	}
	else
	{
		qtyInLineageUnits = t.Qty;
	}
	var signedQty = t.Side == Side.Buy ? qtyInLineageUnits : -qtyInLineageUnits;

	if (lin.OpenLegs.TryGetValue(t.MatchKey, out var existing))
	{
		var newSigned = (existing.Side == Side.Buy ? existing.Qty : -existing.Qty) + signedQty;
		if (newSigned == 0)
			lin.OpenLegs.Remove(t.MatchKey);
		else
			lin.OpenLegs[t.MatchKey] = (newSigned > 0 ? Side.Buy : Side.Sell, Math.Abs(newSigned));
	}
	else
	{
		lin.OpenLegs[t.MatchKey] = (t.Side, qtyInLineageUnits);
	}
}
```

Also update `ResolveLegMetadata` in `EmitRows` to use the share count for stock-leg display:

```csharp
private static (Asset asset, string optionKind, DateTime? expiry, string instrument) ResolveLegMetadata(string matchKey, Lineage lin)
{
	if (matchKey.StartsWith("option:", StringComparison.Ordinal))
	{
		var parsed = MatchKeys.ParseOption(matchKey);
		if (parsed != null)
		{
			var p = parsed.Value.parsed;
			return (Asset.Option, ParsingHelpers.CallPutDisplayName(p.CallPut), p.ExpiryDate, Formatters.FormatOptionDisplay(p.Root, p.ExpiryDate, p.Strike));
		}
	}
	return (Asset.Stock, "-", null, lin.Underlying);
}
```

And for stock single-leg lineages, emit the share count as `Qty` rather than the option-equivalent:

In the single-leg emission path inside `EmitRows`, after the `var (asset, ...) = ResolveLegMetadata(...)` line, add:

```csharp
var displayQty = asset == Asset.Stock && lin.StockShareCount.TryGetValue(matchKey, out var shares) ? Math.Abs(shares) : leg.Qty;
```

Then use `displayQty` in the `PositionRow(Qty: displayQty, ...)` line.

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -3`

Expected: `0 Error(s)`

- [ ] **Step 3: Run diff-positions; confirm covered-call / collar positions now appear correctly in replay**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tail -10
```

Expected: stock+option mixed strategies show in replay output with proper share counts in the stock leg row.

- [ ] **Step 4: Commit**

```bash
git add PositionReplay.cs
git commit -m "PositionReplay: handle stock + option multipliers; preserve share count for display"
```

---

### Task 14: Full diff-positions validation run (user-driven)

**Files:**
- None modified; validation task only.

- [ ] **Step 1: Run diff-positions and capture full output**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- diff-positions 2>&1 | tee /tmp/wa_position_replay_diff.txt
```

- [ ] **Step 2: Review every position marked as `diff`, `only-in-legacy`, or `only-in-replay`**

For each entry, classify as one of:

- **intended fix** — new `adj` matches your running-cash math (`wa report` old vs Option-A math). Legacy was wrong. No action.
- **unexpected regression** — legacy was right, replay is wrong. File an issue; we fix the state-machine rule that produced the wrong output.
- **different semantics but both defensible** — discuss individually.

For any `only-in-replay` or `only-in-legacy` entries: these mean one backend didn't emit a position the other did. That's usually a scope bug — likely the rules missed a specific trade pattern.

- [ ] **Step 3: If regressions found, stop and file precise bug reports**

Rollback is always available:

```bash
git reset --hard pre-strategygrouper-rewrite
```

If rolling back, preserve the plan and spec files for the next attempt.

- [ ] **Step 4: If all diffs are intended fixes, record completion**

No commit for this task; user records acceptance.

---

## Phase 3 — Cleanup (Task 15, user-triggered)

### Task 15: Retire legacy `StrategyGrouper` (deferred)

**This task is only run after the user explicitly approves Phase 2 validation results.**

**Files:**
- Delete: `StrategyGrouper.cs`
- Modify: `PositionTracker.cs` (remove dispatch block, make replay unconditional)
- Delete: `DiffPositionsCommand.cs`
- Modify: `Program.cs` (remove diff-positions registration)
- Modify: `Models.cs` (delete `StrategyGroup` and `PositionEntry` if nothing else references them — verify via grep first)

- [ ] **Step 1: Verify nothing outside `StrategyGrouper.cs` references its private types**

```bash
grep -rn "StrategyGroup\b\|PositionEntry\b" /mnt/c/dev/WebullAnalytics/ --include='*.cs' | grep -v StrategyGrouper.cs
```

Expected: no matches. If any match exists, it must be migrated or deleted before Step 2.

- [ ] **Step 2: Simplify `PositionTracker.BuildPositionRows`**

Replace the dispatch block with a direct call to `PositionReplay.Execute`. The method body becomes:

```csharp
public static (List<PositionRow> rows, Dictionary<int, StrategyAdjustment> adjustments, Dictionary<string, List<NetDebitTrade>> singleLegStandalones)
	BuildPositionRows(Dictionary<string, List<Lot>> positions, Dictionary<string, Trade> tradeIndex, List<Trade> allTrades)
{
	return PositionReplay.Execute(positions, tradeIndex, allTrades);
}
```

- [ ] **Step 3: Delete files**

```bash
rm /mnt/c/dev/WebullAnalytics/StrategyGrouper.cs
rm /mnt/c/dev/WebullAnalytics/DiffPositionsCommand.cs
```

- [ ] **Step 4: Remove command registration in `Program.cs`**

Delete the line `config.AddCommand<DiffPositionsCommand>("diff-positions");` from `Program.cs`.

- [ ] **Step 5: Remove unused types from `Models.cs`**

Delete the `StrategyGroup` class (lines 154–161) and the `PositionEntry` record (line 143) if Step 1's grep confirmed nothing else references them.

- [ ] **Step 6: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.csproj 2>&1 | tail -5`

Expected: `Build succeeded.` and `0 Error(s)`.

- [ ] **Step 7: Smoke test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- report --view simplified 2>&1 | head -20
```

Expected: report output matches the pre-cleanup `wa report` output (same adj-basis numbers as the replay backend produced in Phase 2).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "StrategyGrouper: remove legacy, make PositionReplay unconditional"
```

---

## Out of Scope

- Changes to JSONL / CSV parsing.
- Changes to `PositionTracker.ComputeReport` (fee attribution, realized P&L, raw lots).
- Changes to `Trade`, `Lot`, or `PositionRow` record shapes.
- Any changes to `wa trade place`, `wa analyze`, `wa ai`, or display commands beyond the inherent propagation of corrected adj-basis values.
- A persistent user-linkage sidecar (rejected during brainstorming; qty-match rule replaces this need).
- Splitting Phase 1 into multiple plans — even though the rewrite is substantial, the tasks are independently verifiable and the dispatch flag keeps the legacy path safe throughout.
