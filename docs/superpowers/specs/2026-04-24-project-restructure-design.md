# Project Restructure Design

**Date:** 2026-04-24
**Status:** Proposed

## Summary

Reorganize the 43 flat `.cs` files at the repo root into purpose-oriented folders that mirror the existing `AI/` convention: one top-level folder per user-facing feature, plus dedicated folders for shared infrastructure. Each new top-level folder gets its own namespace (`WebullAnalytics.<Folder>`), matching how `AI/` already works. `Core/` is the one exception — it keeps the root `WebullAnalytics` namespace because its types form the fundamental domain vocabulary used everywhere.

The goal is purely organizational: no behavior changes, no new features, no deleted functionality. All 63 unit tests and the `wa` CLI must behave identically before and after.

## Goals / Non-goals

**Goals:**
- Group files by purpose so navigating the codebase is faster.
- Match the `AI/` pattern: feature folder = namespace boundary.
- Keep `WebullAnalytics.Tests/` structure in sync (tests live next to what they test).
- Preserve all existing behavior, CLI surface, and test pass rate.

**Non-goals:**
- No rewrites, renames of types, signature changes, or API redesigns.
- No behavior changes.
- No optimization of existing code beyond what a file move requires.
- No reshuffling of `AI/` internals — the existing layout under `AI/` is already correct.
- No change to the `Program.cs` entry point location — it stays at repo root.

## Target Layout

### Feature folders (one namespace each)

| Folder | Namespace | Files |
|---|---|---|
| `AI/` | `WebullAnalytics.AI` | (existing — unchanged by this spec) |
| `Analyze/` | `WebullAnalytics.Analyze` | `AnalyzeCommand`, `AnalyzePositionCommand`, `BreakEvenAnalyzer`, `CombinedBreakEvenAnalyzer`, `TimeDecayGridBuilder` |
| `Fetch/` | `WebullAnalytics.Fetch` | `FetchCommand` |
| `Report/` | `WebullAnalytics.Report` | `ReportCommand`, `AdjustmentReportBuilder` |
| `Sniff/` | `WebullAnalytics.Sniff` | `SniffCommand`, `HeaderSniffer` |
| `Trade/` | `WebullAnalytics.Trade` | `TradeCommand`, `TradeConfig`, `TradeLegParser`, `OrderRequestBuilder` |

### Shared infrastructure folders (one namespace each)

| Folder | Namespace | Files |
|---|---|---|
| `Api/` | `WebullAnalytics.Api` | `ApiClient`, `WebullOpenApiClient`, `WebullOptionsClient`, `YahooOptionsClient`, `OpenApiSigner`, `TokenStore` |
| `Pricing/` | `WebullAnalytics.Pricing` | `OptionMath`, `BjerksundStensland` |
| `IO/` | `WebullAnalytics.IO` | `CsvParser`, `JsonlParser`, `ExcelExporter`, `TextFileExporter` |
| `Positions/` | `WebullAnalytics.Positions` | `PositionTracker`, `PositionReplay`, `LegMerger`, `RollShape`, `SideInferrer`, `StrategyClassifier` |
| `Utils/` | `WebullAnalytics.Utils` | `EnumExtensions`, `Formatters`, `JsonElementExtensions`, `TableBuilder`, `TableRenderer`, `TerminalHelper` |
| `Core/` | `WebullAnalytics` *(root)* | `Models`, `MatchKeys`, `EvaluationDate`, `MarketCalendar`, `ParsingHelpers` |

### Root-level files (unmoved)

- `Program.cs` — application entry point; `ResolvePath`, `BaseDir`, `AppConfigPath`, etc. stay accessible via the root namespace.

## Namespace Strategy

Two rules:

1. **Every new top-level folder listed above (except `Core/`) becomes a namespace.** A file at `Trade/TradeCommand.cs` declares `namespace WebullAnalytics.Trade;`. This matches how `AI/AICommand.cs` uses `namespace WebullAnalytics.AI;`.

2. **`Core/` keeps the root namespace `WebullAnalytics`.** The types it contains (`Trade`, `Position`, `OptionContractQuote`, `Side`, `Asset`, `OptionParsed`, etc.) are the fundamental vocabulary used by every other file. Putting them in `WebullAnalytics.Core` would force a `using WebullAnalytics.Core;` in literally every other `.cs` file. Keeping them at root eliminates that churn.

Why `Pricing/` not `Math/`: `WebullAnalytics.Math` would collide with `System.Math`. `Pricing` is more descriptive anyway (options pricing).

## Tests Project Mirror

`WebullAnalytics.Tests/` gets parallel subfolders. Test files that exist today:

- `WebullAnalytics.Tests/AI/Open/*` — already aligned with `AI/Open/`, no change.
- `WebullAnalytics.Tests/Smoke/BuildSmokeTests.cs` — stays at `Smoke/`, a root-level test category.
- `WebullAnalytics.Tests/TestQuote.cs` — stays at root of the test project (cross-cutting helper).

No test files exist today that would need moving into new mirror folders (Trade, Report, Analyze, etc.), because none of those features currently have unit tests. When future tests are added, they land under the mirror folder for their feature.

## Cross-File Impact (Using Statements)

Every `.cs` file outside `Core/` and `AI/` that references a type which lived at the root namespace will need a new `using` statement after the move. For example:

Before:
```csharp
namespace WebullAnalytics;

internal static class SomeHelper
{
    public static void Use() => WebullOptionsClient.FetchOptionQuotesAsync(...);
}
```

After (if the helper stays at root — which won't happen post-restructure — but illustrative):
```csharp
using WebullAnalytics.Api;

namespace WebullAnalytics;

internal static class SomeHelper
{
    public static void Use() => WebullOptionsClient.FetchOptionQuotesAsync(...);
}
```

Every file will need to be audited for which of the new namespaces it touches and add the corresponding `using`s. The compiler is the reliable driver — after each batch of moves, `dotnet build` will surface missing usings; add them, re-build, repeat.

## Plan Shape (for the implementation-plan skill)

Moving 43 files in one commit would be unreviewable and risky. The implementation plan will sequence the moves by folder, in an order that minimizes intermediate breakage:

1. Start with **leaf-level shared infra** (`Pricing/`, `IO/`, `Utils/`, `Api/`) — these have no dependencies on the feature folders, so moving them first causes few upstream fixups.
2. Then **`Positions/`** — depends on `Core/` and `Pricing/` only.
3. Then **feature folders** one at a time (Fetch, Sniff, Trade, Report, Analyze) — each depends on `Core/`, `Api/`, `Pricing/`, `IO/`, `Positions/`, all already in place.
4. `Core/` files stay at root namespace, so they can be physically moved at any point — recommend moving them alongside step 1 since they don't need namespace changes, only file relocation.
5. After each step: `dotnet build WebullAnalytics.sln && dotnet test WebullAnalytics.sln` must pass. Commit per folder.
6. Final step: verify the `wa` CLI runs `ai once` end-to-end against live Webull (smoke).

## Testing Strategy

- **No new tests.** Existing 63 tests are the safety net. Each commit must leave all 63 passing.
- **Build as gate.** After every namespace change, `dotnet build` must be clean (0 warnings, 0 errors) before committing.
- **Smoke test at the end.** Run `wa ai once` to confirm runtime behavior is unchanged.

## Risk / Rollback

- Git commits per folder mean any bad step can be reverted with `git revert <sha>` or `git reset HEAD~1`.
- Namespace renames in C# are a well-understood mechanical transform — IDE tooling (or `sed` + `dotnet build`) can drive the changes reliably.
- The main risk is missing a `using` after a move, which the compiler catches immediately.

## Out of Scope for This Spec

- Refactoring types (renaming classes, extracting interfaces, splitting large files).
- Any behavior change.
- Introducing new features or tests.
- Restructuring `AI/` internals beyond what this spec already leaves in place.
