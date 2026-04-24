# Project Restructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize 43 flat `.cs` files at the repo root into 11 purpose-oriented folders that mirror the existing `AI/` convention, with one namespace per folder (except `Core/` which keeps the root namespace). No behavior changes.

**Architecture:** Folder-by-folder moves in dependency order (leaves before consumers), with namespace declarations updated in each moved file and `using` statements added to every consumer outside the moved folder. Each folder move is one commit. All 63 tests must pass after every commit.

**Tech Stack:** .NET 10, C#, `git mv` for rename history preservation.

**Spec:** `docs/superpowers/specs/2026-04-24-project-restructure-design.md`

**Build/test commands (WSL):**
- Build: `"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln`
- Test: `"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln`
- Smoke CLI: `"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- ai once --no-open-proposals`

**Standard procedure for every folder-move task:**

1. Create the folder (if it does not already exist).
2. `git mv` each file into the new folder.
3. Update the `namespace` declaration in each moved file (only when the folder gets its own namespace; `Core/` keeps `WebullAnalytics`).
4. Run `dotnet build WebullAnalytics.sln`. Expect `CS0246` / `CS0103` errors in consumer files that need a new `using` line.
5. Add `using WebullAnalytics.<FolderName>;` to each consumer file listed in the task.
6. Run `dotnet build` again — must be clean (0 warnings, 0 errors).
7. Run `dotnet test WebullAnalytics.sln` — must pass all 63 tests.
8. Commit with a single descriptive message.

**Important: `using` insertion location.** Place new `using` statements alphabetically within the existing `using` block at the top of the file, above the `namespace` declaration. Do not reformat any other lines.

**Important: namespace changes are a single-line edit.** The file has exactly one `namespace` declaration near the top. Change only that line — no other edits in the moved files are required (or permitted) by this plan.

---

## Task 0: Baseline verification

**Files:** none.

- [ ] **Step 1: Confirm clean master state**

Run:
```bash
git status
git log --oneline -3
```

Expected: `nothing to commit, working tree clean` (the untracked `WebullAnalytics.slnx` is acceptable). HEAD should be `c8d6fcb` (spec commit) or a later commit that includes the restructure spec.

- [ ] **Step 2: Confirm baseline build + tests**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected:
- Build: `0 Warning(s), 0 Error(s)`.
- Tests: `Passed! - Failed: 0, Passed: 63, Skipped: 0, Total: 63`.

If either fails, stop and escalate — the restructure plan assumes a clean baseline.

---

## Task 1: `Core/` (root namespace, no code changes — just file moves)

**Files moved into `Core/`:**

- `Models.cs`
- `MatchKeys.cs`
- `EvaluationDate.cs`
- `MarketCalendar.cs`
- `ParsingHelpers.cs`

**Namespace:** these files keep `namespace WebullAnalytics;` — no namespace change.

- [ ] **Step 1: Create the folder and move files**

Run:
```bash
mkdir -p Core
git mv Models.cs Core/Models.cs
git mv MatchKeys.cs Core/MatchKeys.cs
git mv EvaluationDate.cs Core/EvaluationDate.cs
git mv MarketCalendar.cs Core/MarketCalendar.cs
git mv ParsingHelpers.cs Core/ParsingHelpers.cs
```

- [ ] **Step 2: Confirm no source edits are needed**

Because these files stay in the root `WebullAnalytics` namespace, no other file's `using` statements change. Verify:

```bash
grep -n "^namespace " Core/*.cs
```

Expected: every file shows `namespace WebullAnalytics;`.

- [ ] **Step 3: Build + test**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 4: Commit**

```bash
git commit -m "move core domain types into Core/"
```

---

## Task 2: `Pricing/` (namespace `WebullAnalytics.Pricing`)

**Files moved:**
- `OptionMath.cs`
- `BjerksundStensland.cs`

**Namespace changes:** both files change `namespace WebullAnalytics;` → `namespace WebullAnalytics.Pricing;`

**Consumers needing `using WebullAnalytics.Pricing;` added:**

- `AnalyzeCommand.cs`
- `AnalyzePositionCommand.cs`
- `BreakEvenAnalyzer.cs`
- `CombinedBreakEvenAnalyzer.cs`
- `ReportCommand.cs`
- `TableBuilder.cs`
- `TimeDecayGridBuilder.cs`
- `AI/ProfitProjector.cs`
- `AI/ScenarioEngine.cs`
- `AI/Open/CandidateEnumerator.cs`
- `AI/Open/CandidateScorer.cs`
- `AI/Replay/IVBackSolver.cs`
- `AI/Rules/OpportunisticRollRule.cs`
- `AI/Sources/ReplayQuoteSource.cs`

- [ ] **Step 1: Move files**

```bash
mkdir -p Pricing
git mv OptionMath.cs Pricing/OptionMath.cs
git mv BjerksundStensland.cs Pricing/BjerksundStensland.cs
```

- [ ] **Step 2: Update namespaces**

In `Pricing/OptionMath.cs`, change:
```csharp
namespace WebullAnalytics;
```
to:
```csharp
namespace WebullAnalytics.Pricing;
```

In `Pricing/BjerksundStensland.cs`, change:
```csharp
namespace WebullAnalytics;
```
to:
```csharp
namespace WebullAnalytics.Pricing;
```

- [ ] **Step 3: Confirm build fails as expected**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | grep -E "CS0246|CS0103" | head -10
```

Expected: several errors about `OptionMath`, `BjerksundStensland`, `RiskFreeRate`, `BlackScholes` being unrecognized.

- [ ] **Step 4: Add `using WebullAnalytics.Pricing;` to each consumer**

For each of the 14 consumer files listed at the top of this task, add `using WebullAnalytics.Pricing;` to the `using` block, preserving alphabetical order.

For example, `AnalyzeCommand.cs` currently starts with:
```csharp
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
```

After the edit it becomes:
```csharp
using System.ComponentModel;
using WebullAnalytics.Pricing;
using Spectre.Console;
using Spectre.Console.Cli;
```

(Standard C# convention: `System.*` first, then third-party, then project. `WebullAnalytics.Pricing` goes with project usings. If the file already has a `using WebullAnalytics.<Something>;` block, insert alphabetically within that block instead.)

Apply the same change to each of the other 13 consumer files.

- [ ] **Step 5: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 6: Commit**

```bash
git commit -m "move OptionMath and BjerksundStensland into Pricing/"
```

---

## Task 3: `IO/` (namespace `WebullAnalytics.IO`)

**Files moved:**
- `CsvParser.cs`
- `JsonlParser.cs`
- `ExcelExporter.cs`
- `TextFileExporter.cs`

**Namespace changes:** all four files change `namespace WebullAnalytics;` → `namespace WebullAnalytics.IO;`

**Consumers needing `using WebullAnalytics.IO;` added:**

- `AnalyzePositionCommand.cs`
- `ReportCommand.cs`
- `TableBuilder.cs`

- [ ] **Step 1: Move files**

```bash
mkdir -p IO
git mv CsvParser.cs IO/CsvParser.cs
git mv JsonlParser.cs IO/JsonlParser.cs
git mv ExcelExporter.cs IO/ExcelExporter.cs
git mv TextFileExporter.cs IO/TextFileExporter.cs
```

- [ ] **Step 2: Update namespaces**

In each of the 4 moved files, change the single `namespace WebullAnalytics;` line to `namespace WebullAnalytics.IO;`.

- [ ] **Step 3: Add `using WebullAnalytics.IO;` to each consumer**

Add `using WebullAnalytics.IO;` to:
- `AnalyzePositionCommand.cs`
- `ReportCommand.cs`
- `TableBuilder.cs`

Preserve alphabetical order within the existing project `using` block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move CSV/JSONL/Excel/text parsers and exporters into IO/"
```

---

## Task 4: `Utils/` (namespace `WebullAnalytics.Utils`)

**Files moved:**
- `EnumExtensions.cs`
- `Formatters.cs`
- `JsonElementExtensions.cs`
- `TableBuilder.cs`
- `TableRenderer.cs`
- `TerminalHelper.cs`

**Namespace changes:** all six files change to `namespace WebullAnalytics.Utils;`.

**Consumers needing `using WebullAnalytics.Utils;` added** (union of consumers across these 6 types, excluding the 6 moved files themselves):

- `AnalyzeCommand.cs`
- `AnalyzePositionCommand.cs`
- `BreakEvenAnalyzer.cs`
- `CombinedBreakEvenAnalyzer.cs`
- `PositionReplay.cs`
- `ReportCommand.cs`
- `IO/CsvParser.cs` (uses `JsonElementExtensions`)
- `IO/ExcelExporter.cs` (uses `Formatters`)
- `IO/JsonlParser.cs` (uses `JsonElementExtensions`)
- `IO/TextFileExporter.cs` (uses `Formatters`)

- [ ] **Step 1: Move files**

```bash
mkdir -p Utils
git mv EnumExtensions.cs Utils/EnumExtensions.cs
git mv Formatters.cs Utils/Formatters.cs
git mv JsonElementExtensions.cs Utils/JsonElementExtensions.cs
git mv TableBuilder.cs Utils/TableBuilder.cs
git mv TableRenderer.cs Utils/TableRenderer.cs
git mv TerminalHelper.cs Utils/TerminalHelper.cs
```

- [ ] **Step 2: Update namespaces**

In each of the 6 moved files, change the single `namespace WebullAnalytics;` line to `namespace WebullAnalytics.Utils;`.

Note: `TableBuilder.cs` already has `using WebullAnalytics.Pricing;` from Task 2 — do not remove it.

- [ ] **Step 3: Add `using WebullAnalytics.Utils;` to each consumer**

Add to the 10 consumer files listed above. Preserve alphabetical ordering within the project `using` block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move formatting, enum, and table helpers into Utils/"
```

---

## Task 5: `Api/` (namespace `WebullAnalytics.Api`)

**Files moved:**
- `ApiClient.cs`
- `WebullOpenApiClient.cs`
- `WebullOptionsClient.cs`
- `YahooOptionsClient.cs`
- `OpenApiSigner.cs`
- `TokenStore.cs`

**Namespace changes:** all six files change to `namespace WebullAnalytics.Api;`.

**Consumers needing `using WebullAnalytics.Api;` added:**

- `AnalyzeCommand.cs`
- `FetchCommand.cs`
- `ReportCommand.cs`
- `TradeCommand.cs`
- `AI/Replay/HistoricalPriceCache.cs`
- `AI/Sources/LivePositionSource.cs`
- `AI/Sources/LiveQuoteSource.cs`

- [ ] **Step 1: Move files**

```bash
mkdir -p Api
git mv ApiClient.cs Api/ApiClient.cs
git mv WebullOpenApiClient.cs Api/WebullOpenApiClient.cs
git mv WebullOptionsClient.cs Api/WebullOptionsClient.cs
git mv YahooOptionsClient.cs Api/YahooOptionsClient.cs
git mv OpenApiSigner.cs Api/OpenApiSigner.cs
git mv TokenStore.cs Api/TokenStore.cs
```

- [ ] **Step 2: Update namespaces**

In each of the 6 moved files, change the single `namespace WebullAnalytics;` line to `namespace WebullAnalytics.Api;`.

- [ ] **Step 3: Add `using WebullAnalytics.Api;` to each consumer**

Add to the 7 consumer files listed above. Preserve alphabetical ordering within the project `using` block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move external API clients into Api/"
```

---

## Task 6: `Positions/` (namespace `WebullAnalytics.Positions`)

**Files moved:**
- `PositionTracker.cs`
- `PositionReplay.cs`
- `LegMerger.cs`
- `RollShape.cs`
- `SideInferrer.cs`
- `StrategyClassifier.cs`

**Namespace changes:** all six files change to `namespace WebullAnalytics.Positions;`.

**Consumers needing `using WebullAnalytics.Positions;` added:**

- `AdjustmentReportBuilder.cs`
- `AnalyzePositionCommand.cs`
- `CombinedBreakEvenAnalyzer.cs`
- `OrderRequestBuilder.cs`
- `ReportCommand.cs`
- `TimeDecayGridBuilder.cs`
- `TradeCommand.cs`
- `IO/JsonlParser.cs`
- `AI/Output/ProposalSink.cs`
- `AI/Sources/ReplayPositionSource.cs`

Plus (because the 6 Positions files reference each other, they do NOT need `using WebullAnalytics.Positions;` within the Positions/ folder — same namespace).

- [ ] **Step 1: Move files**

```bash
mkdir -p Positions
git mv PositionTracker.cs Positions/PositionTracker.cs
git mv PositionReplay.cs Positions/PositionReplay.cs
git mv LegMerger.cs Positions/LegMerger.cs
git mv RollShape.cs Positions/RollShape.cs
git mv SideInferrer.cs Positions/SideInferrer.cs
git mv StrategyClassifier.cs Positions/StrategyClassifier.cs
```

- [ ] **Step 2: Update namespaces**

In each of the 6 moved files, change the single `namespace WebullAnalytics;` line to `namespace WebullAnalytics.Positions;`.

Note: `PositionReplay.cs` already has `using WebullAnalytics.Utils;` from Task 4.

- [ ] **Step 3: Add `using WebullAnalytics.Positions;` to each consumer**

Add to the 10 consumer files listed above. Preserve alphabetical ordering within the project `using` block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move position-tracking domain into Positions/"
```

---

## Task 7: `Fetch/` (namespace `WebullAnalytics.Fetch`)

**Files moved:**
- `FetchCommand.cs`

**Namespace change:** `namespace WebullAnalytics;` → `namespace WebullAnalytics.Fetch;`

**Consumers needing `using WebullAnalytics.Fetch;` added:**
- `Program.cs` (registers `FetchCommand` with Spectre.Console)

- [ ] **Step 1: Move file**

```bash
mkdir -p Fetch
git mv FetchCommand.cs Fetch/FetchCommand.cs
```

- [ ] **Step 2: Update namespace**

In `Fetch/FetchCommand.cs`, change `namespace WebullAnalytics;` to `namespace WebullAnalytics.Fetch;`.

- [ ] **Step 3: Add using to Program.cs**

In `Program.cs`, add `using WebullAnalytics.Fetch;` to the using block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move FetchCommand into Fetch/"
```

---

## Task 8: `Sniff/` (namespace `WebullAnalytics.Sniff`)

**Files moved:**
- `SniffCommand.cs`
- `HeaderSniffer.cs`

**Namespace changes:** both → `namespace WebullAnalytics.Sniff;`

**Consumers needing `using WebullAnalytics.Sniff;` added:**
- `Program.cs` (registers `SniffCommand`)

- [ ] **Step 1: Move files**

```bash
mkdir -p Sniff
git mv SniffCommand.cs Sniff/SniffCommand.cs
git mv HeaderSniffer.cs Sniff/HeaderSniffer.cs
```

- [ ] **Step 2: Update namespaces**

Change `namespace WebullAnalytics;` → `namespace WebullAnalytics.Sniff;` in both files.

- [ ] **Step 3: Add using to Program.cs**

In `Program.cs`, add `using WebullAnalytics.Sniff;` to the using block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move Sniff command and HeaderSniffer into Sniff/"
```

---

## Task 9: `Trade/` (namespace `WebullAnalytics.Trade`)

**Files moved:**
- `TradeCommand.cs`
- `TradeConfig.cs`
- `TradeLegParser.cs`
- `OrderRequestBuilder.cs`

**Namespace changes:** all four → `namespace WebullAnalytics.Trade;`

**Consumers needing `using WebullAnalytics.Trade;` added:**

- `Program.cs` (registers all Trade subcommands)
- `AI/AICommand.cs` (references `TradeConfig`)

(Check with `grep`: if other files reference `TradeConfig` or the trade types, add the using there too. The plan's rule of "compiler-driven discovery via CS0246 errors" applies.)

- [ ] **Step 1: Move files**

```bash
mkdir -p Trade
git mv TradeCommand.cs Trade/TradeCommand.cs
git mv TradeConfig.cs Trade/TradeConfig.cs
git mv TradeLegParser.cs Trade/TradeLegParser.cs
git mv OrderRequestBuilder.cs Trade/OrderRequestBuilder.cs
```

- [ ] **Step 2: Update namespaces**

In each of the 4 moved files, change `namespace WebullAnalytics;` → `namespace WebullAnalytics.Trade;`.

Note: `TradeCommand.cs` already has `using WebullAnalytics.Api;` (Task 5) and `using WebullAnalytics.Positions;` (Task 6).

- [ ] **Step 3: Build, identify and add missing usings**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | grep -E "CS0246|CS0103" | head -20
```

For each file reported, add `using WebullAnalytics.Trade;` to its using block. At minimum expect:
- `Program.cs`
- `AI/AICommand.cs`

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move Trade command and helpers into Trade/"
```

---

## Task 10: `Report/` (namespace `WebullAnalytics.Report`)

**Files moved:**
- `ReportCommand.cs`
- `AdjustmentReportBuilder.cs`

**Namespace changes:** both → `namespace WebullAnalytics.Report;`

**Consumers needing `using WebullAnalytics.Report;` added:**

- `Program.cs` (registers `ReportCommand`)
- `AI/AICommand.cs` (references `ReportCommand.LoadTrades` in the replay path — see `AI/AICommand.cs:166`)
- `AI/Replay/ReplayRunner.cs` if it references `AdjustmentReportBuilder` (verify via grep in Step 3)

- [ ] **Step 1: Move files**

```bash
mkdir -p Report
git mv ReportCommand.cs Report/ReportCommand.cs
git mv AdjustmentReportBuilder.cs Report/AdjustmentReportBuilder.cs
```

- [ ] **Step 2: Update namespaces**

Change both files' `namespace WebullAnalytics;` → `namespace WebullAnalytics.Report;`.

Note: These files already have `using WebullAnalytics.Api;`, `using WebullAnalytics.IO;`, `using WebullAnalytics.Pricing;`, `using WebullAnalytics.Positions;`, `using WebullAnalytics.Utils;` from earlier tasks.

- [ ] **Step 3: Build, identify and add missing usings**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | grep -E "CS0246|CS0103" | head -20
```

For each file reported, add `using WebullAnalytics.Report;` to its using block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move Report command and AdjustmentReportBuilder into Report/"
```

---

## Task 11: `Analyze/` (namespace `WebullAnalytics.Analyze`)

**Files moved:**
- `AnalyzeCommand.cs`
- `AnalyzePositionCommand.cs`
- `BreakEvenAnalyzer.cs`
- `CombinedBreakEvenAnalyzer.cs`
- `TimeDecayGridBuilder.cs`

**Namespace changes:** all five → `namespace WebullAnalytics.Analyze;`

**Consumers needing `using WebullAnalytics.Analyze;` added:**

- `Program.cs` (registers `AnalyzeCommand`, `AnalyzePositionCommand`)
- `AI/ScenarioEngine.cs` (references `AnalyzeCommon` if it's one of the moved types — verify via grep in Step 3)

- [ ] **Step 1: Move files**

```bash
mkdir -p Analyze
git mv AnalyzeCommand.cs Analyze/AnalyzeCommand.cs
git mv AnalyzePositionCommand.cs Analyze/AnalyzePositionCommand.cs
git mv BreakEvenAnalyzer.cs Analyze/BreakEvenAnalyzer.cs
git mv CombinedBreakEvenAnalyzer.cs Analyze/CombinedBreakEvenAnalyzer.cs
git mv TimeDecayGridBuilder.cs Analyze/TimeDecayGridBuilder.cs
```

- [ ] **Step 2: Update namespaces**

Change each moved file's `namespace WebullAnalytics;` → `namespace WebullAnalytics.Analyze;`.

- [ ] **Step 3: Build, identify and add missing usings**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | grep -E "CS0246|CS0103" | head -20
```

For each file reported, add `using WebullAnalytics.Analyze;` to its using block.

- [ ] **Step 4: Build + test**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build WebullAnalytics.sln 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -3
```

Expected: build clean, all 63 tests pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "move Analyze commands and builders into Analyze/"
```

---

## Task 12: Final verification

**Files:** none modified; only verification.

- [ ] **Step 1: Verify repo root has only Program.cs**

Run:
```bash
ls *.cs
```

Expected: `Program.cs` is the only `.cs` file at the repo root.

- [ ] **Step 2: Verify folder structure**

Run:
```bash
ls -d */ | grep -v -E "^(bin|obj|data|docs|AI|WebullAnalytics\.Tests)/$"
```

Expected lines (order may vary):
```
Analyze/
Api/
Core/
Fetch/
IO/
Positions/
Pricing/
Report/
Sniff/
Trade/
Utils/
```

- [ ] **Step 3: Full test suite**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test WebullAnalytics.sln 2>&1 | tail -5
```

Expected: `Passed! - Failed: 0, Passed: 63, Skipped: 0, Total: 63`.

- [ ] **Step 4: Smoke-test the CLI**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics.csproj -- ai once --no-open-proposals 2>&1 | tail -5
```

Expected: `Tick complete: <n> position(s), <m> mgmt proposal(s), 0 open proposal(s) emitted` — no unhandled exceptions. (The `--no-open-proposals` flag keeps the smoke fast and avoids pulling the full Webull option chain.)

- [ ] **Step 5: Confirm git history**

Run:
```bash
git log --oneline c8d6fcb..HEAD
```

Expected: 11 commits (one per folder move task, Task 1 through Task 11), each with a clear message.

- [ ] **Step 6: Done — no final commit needed**

All work was committed per-task. Report completion to the user with the commit range.

---

## Self-Review

**Spec coverage:**

- Folder layout with each namespace defined → Tasks 1–11 each enact one row of the spec's Target Layout.
- Core/ keeps root namespace → Task 1 explicitly skips namespace changes.
- Pricing (not Math, to avoid `System.Math` collision) → Task 2 uses `WebullAnalytics.Pricing`.
- Tests mirror layout — not yet needed: no non-AI tests exist, and the spec notes only `AI/Open/` test files, which already mirror. No action required in this plan.
- Preserve behavior → every task gates on `dotnet test` passing.
- Rollback via git → each task is one commit.

**Placeholder scan:** No "TBD"/"TODO"/"implement later" strings. Every step shows the exact command or edit.

**Type consistency:** Namespace names (`WebullAnalytics.Pricing`, `WebullAnalytics.IO`, `WebullAnalytics.Utils`, `WebullAnalytics.Api`, `WebullAnalytics.Positions`, `WebullAnalytics.Fetch`, `WebullAnalytics.Sniff`, `WebullAnalytics.Trade`, `WebullAnalytics.Report`, `WebullAnalytics.Analyze`) used consistently across all task references.

**Risks acknowledged:**

- **Missed consumer:** If a task under-enumerates consumers needing a `using`, the compiler catches it in Step 3/4 (CS0246) and the implementer adds the missing using. The plan's Step 3 for Tasks 9–11 explicitly uses compile errors as the driver; earlier tasks pre-enumerate based on `grep` evidence.
- **IDE reformats:** The plan directs adding usings in alphabetical order inside the existing project-usings block. If a contributor lets an IDE autosort the whole file, no harm — the file still compiles.
- **`git mv` vs rename tracking:** `git mv` preserves rename history so `git log --follow` works across the move. Plain file move + delete loses this.
