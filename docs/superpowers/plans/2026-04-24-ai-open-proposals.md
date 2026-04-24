# AI Open-Proposal Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `wa ai once` and `wa ai watch` to emit probability-weighted, cash-sized opening-trade proposals across long calendars/diagonals, short verticals, and single long calls/puts, soft-weighted by the existing technical bias signal.

**Architecture:** A new `AI/Open/` subsystem peer to the management-rule subsystem. `CandidateEnumerator` produces structure skeletons per ticker; `CandidateScorer` computes EV, P(profit), and a bias-adjusted score per candidate using the existing `OptionMath` helpers; `OpenCandidateEvaluator` orchestrates enumerate → phase-3 quote fetch → score → rank → cash-tag → dedup. A new `OpenProposalSink` writes to the existing JSONL log (with a `"type":"open"` discriminator) and to the console. A new xUnit test project validates the math directly against hand-computed values.

**Tech Stack:** .NET 10, C#, existing `OptionMath` (Black-Scholes), `Spectre.Console.Cli`, System.Text.Json, xUnit (new).

**Spec:** `docs/superpowers/specs/2026-04-24-ai-open-proposals-design.md`

**Build/test commands (WSL):**
- Build: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`
- Run tests: `"/mnt/c/Program Files/dotnet/dotnet.exe" test`
- Run CLI: `"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics -- ai once --ignore-market-hours`

---

## File Structure

**Test helper:** `OptionContractQuote` in `Models.cs` is a **positional record** requiring all constructor args. Tests use a helper `TestQuote.Q(bid, ask, iv)` defined in `WebullAnalytics.Tests/TestQuote.cs` to construct it concisely. This helper is added in Task 1 alongside the project setup.

**New files:**
- `AI/Open/OpenProposal.cs` — `OpenProposal` record, `OpenStructureKind` enum.
- `AI/Open/OpenerConfig.cs` — config types (`OpenerConfig`, per-structure subconfigs) and validation.
- `AI/Open/CandidateSkeleton.cs` — intermediate type emitted by the enumerator (structure + legs, no quotes yet).
- `AI/Open/CandidateEnumerator.cs` — pure enumeration of skeletons per ticker.
- `AI/Open/CandidateScorer.cs` — BS-based scoring producing `OpenProposal` from a skeleton + quotes + bias.
- `AI/Open/OpenCandidateEvaluator.cs` — orchestrator with phase-3 quote fetch, ranking, cash sizing, dedup.
- `AI/Open/OpenProposalSink.cs` — JSONL + console output, with per-fingerprint dedup state.
- `AI/Open/DirectionalFit.cs` — static helper mapping `OpenStructureKind` to fit sign (+1/0/−1).
- `AI/Open/OpenerExpiryHelpers.cs` — `ThirdFridayInMonth`, `NextWeeklyExpiriesInRange`, `MonthlyExpiriesInRange`.
- `WebullAnalytics.Tests/WebullAnalytics.Tests.csproj` — xUnit test project.
- `WebullAnalytics.Tests/AI/Open/OpenerExpiryHelpersTests.cs`
- `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorTests.cs`
- `WebullAnalytics.Tests/AI/Open/CandidateScorerTests.cs`
- `WebullAnalytics.Tests/AI/Open/OpenCandidateEvaluatorTests.cs`
- `WebullAnalytics.Tests/AI/Open/OpenerConfigValidationTests.cs`
- `WebullAnalytics.sln` — solution file binding the exe and the test project.

**Modified files:**
- `AI/AIConfig.cs:44-51` — add `Opener` property to `AIConfig`. `AIConfigLoader.Validate` gains opener bounds checks.
- `AI/AICommand.cs:10-45, 92-125` — add `--no-open-proposals` to `AISubcommandSettings`; wire `OpenCandidateEvaluator` into `AIOnceCommand.ExecuteAsync`.
- `AI/WatchLoop.cs:55-125` — wire `OpenCandidateEvaluator` into the tick body.
- `AI/Output/ProposalSink.cs:34-54` — add `"type":"management"` discriminator to the management JSONL schema.
- `ai-config.example.json` — add fully-populated `opener` block.

---

## Task 1: Add xUnit test project and solution

**Files:**
- Create: `WebullAnalytics.Tests/WebullAnalytics.Tests.csproj`
- Create: `WebullAnalytics.Tests/Smoke/BuildSmokeTests.cs`
- Create: `WebullAnalytics.Tests/TestQuote.cs`
- Create: `WebullAnalytics.sln`

- [ ] **Step 1: Create the test project csproj**

Create `WebullAnalytics.Tests/WebullAnalytics.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\WebullAnalytics.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="WebullAnalytics.Tests" />
  </ItemGroup>

</Project>
```

Note: the exe uses `internal` for AI types. Add `<InternalsVisibleTo Include="WebullAnalytics.Tests"/>` to **the exe csproj**, not the test csproj. The block above is included by mistake — remove it before saving. Corrected test csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\WebullAnalytics.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Make exe internals visible to test project**

Modify `WebullAnalytics.csproj` — add inside an `<ItemGroup>`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="WebullAnalytics.Tests" />
  </ItemGroup>
```

The final csproj becomes:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>wa</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CsvHelper" Version="33.1.0" />
    <PackageReference Include="EPPlus" Version="7.6.0" />
    <PackageReference Include="Spectre.Console" Version="0.54.0" />
    <PackageReference Include="Spectre.Console.Cli" Version="0.53.1" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="data\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="WebullAnalytics.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Write a smoke test**

Create `WebullAnalytics.Tests/Smoke/BuildSmokeTests.cs`:

```csharp
using Xunit;

namespace WebullAnalytics.Tests.Smoke;

public class BuildSmokeTests
{
    [Fact]
    public void ProjectReferenceCompiles()
    {
        // Touches an internal type from the main exe to validate InternalsVisibleTo.
        var cfg = new WebullAnalytics.AI.AIConfig();
        Assert.NotNull(cfg);
    }
}
```

- [ ] **Step 3b: Add the test quote helper**

Create `WebullAnalytics.Tests/TestQuote.cs`:

```csharp
using WebullAnalytics;

namespace WebullAnalytics.Tests;

/// <summary>Compact constructor for OptionContractQuote in tests. Only sets the fields tests need.</summary>
internal static class TestQuote
{
    public static OptionContractQuote Q(decimal bid, decimal ask, decimal? iv = null) =>
        new OptionContractQuote(
            ContractSymbol: "",
            LastPrice: null,
            Bid: bid,
            Ask: ask,
            Change: null,
            PercentChange: null,
            Volume: null,
            OpenInterest: null,
            ImpliedVolatility: iv);
}
```

- [ ] **Step 4: Create the solution file**

Run from repo root:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" new sln -n WebullAnalytics
"/mnt/c/Program Files/dotnet/dotnet.exe" sln add WebullAnalytics.csproj
"/mnt/c/Program Files/dotnet/dotnet.exe" sln add WebullAnalytics.Tests/WebullAnalytics.Tests.csproj
```

- [ ] **Step 5: Run the test to verify the harness works**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test`

Expected: one test passes — `BuildSmokeTests.ProjectReferenceCompiles`.

- [ ] **Step 6: Commit**

```bash
git add WebullAnalytics.csproj WebullAnalytics.sln WebullAnalytics.Tests/
git commit -m "add xUnit test project and solution"
```

---

## Task 2: Add opener config types with validation

**Files:**
- Create: `AI/Open/OpenerConfig.cs`
- Modify: `AI/AIConfig.cs` (add `Opener` property, extend `Validate`)
- Create: `WebullAnalytics.Tests/AI/Open/OpenerConfigValidationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `WebullAnalytics.Tests/AI/Open/OpenerConfigValidationTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerConfigValidationTests
{
    private static AIConfig MinimalValidConfig() => new()
    {
        Tickers = new() { "GME" }
    };

    [Fact]
    public void DefaultConfigIsValid()
    {
        var cfg = MinimalValidConfig();
        Assert.Null(AIConfigLoader.Validate(cfg));
    }

    [Fact]
    public void TopNPerTickerMustBePositive()
    {
        var cfg = MinimalValidConfig();
        cfg.Opener.TopNPerTicker = 0;
        Assert.Contains("opener.topNPerTicker", AIConfigLoader.Validate(cfg) ?? "");
    }

    [Fact]
    public void DirectionalFitWeightMustBeNonNegative()
    {
        var cfg = MinimalValidConfig();
        cfg.Opener.DirectionalFitWeight = -0.1m;
        Assert.Contains("opener.directionalFitWeight", AIConfigLoader.Validate(cfg) ?? "");
    }

    [Fact]
    public void ShortVerticalDeltaBoundsInRange()
    {
        var cfg = MinimalValidConfig();
        cfg.Opener.Structures.ShortVertical.ShortDeltaMin = -0.1m;
        Assert.Contains("shortVertical.shortDeltaMin", AIConfigLoader.Validate(cfg) ?? "");
    }

    [Fact]
    public void LongCalendarDteMaxMustBeAtLeastMin()
    {
        var cfg = MinimalValidConfig();
        cfg.Opener.Structures.LongCalendar.ShortDteMin = 10;
        cfg.Opener.Structures.LongCalendar.ShortDteMax = 5;
        Assert.Contains("longCalendar.shortDteMax", AIConfigLoader.Validate(cfg) ?? "");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenerConfigValidationTests`

Expected: compile failure — `AIConfig` has no `Opener` property.

- [ ] **Step 3: Create the opener config types**

Create `AI/Open/OpenerConfig.cs`:

```csharp
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class OpenerConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("topNPerTicker")] public int TopNPerTicker { get; set; } = 5;
    [JsonPropertyName("maxCandidatesPerStructurePerTicker")] public int MaxCandidatesPerStructurePerTicker { get; set; } = 8;
    [JsonPropertyName("maxQtyPerProposal")] public int MaxQtyPerProposal { get; set; } = 10;
    [JsonPropertyName("directionalFitWeight")] public decimal DirectionalFitWeight { get; set; } = 0.5m;
    [JsonPropertyName("profitBandPct")] public decimal ProfitBandPct { get; set; } = 5.0m;
    [JsonPropertyName("ivDefaultPct")] public decimal IvDefaultPct { get; set; } = 40m;
    [JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0.50m;
    [JsonPropertyName("structures")] public OpenerStructuresConfig Structures { get; set; } = new();
}

internal sealed class OpenerStructuresConfig
{
    [JsonPropertyName("longCalendar")] public OpenerCalendarLikeConfig LongCalendar { get; set; } = new();
    [JsonPropertyName("longDiagonal")] public OpenerCalendarLikeConfig LongDiagonal { get; set; } = new();
    [JsonPropertyName("shortVertical")] public OpenerShortVerticalConfig ShortVertical { get; set; } = new();
    [JsonPropertyName("longCallPut")] public OpenerLongCallPutConfig LongCallPut { get; set; } = new();
}

internal sealed class OpenerCalendarLikeConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
    [JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
    [JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
    [JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
}

internal sealed class OpenerShortVerticalConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
    [JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
    [JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2 };
    [JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
    [JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.30m;
}

internal sealed class OpenerLongCallPutConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("dteMin")] public int DteMin { get; set; } = 21;
    [JsonPropertyName("dteMax")] public int DteMax { get; set; } = 60;
    [JsonPropertyName("deltaMin")] public decimal DeltaMin { get; set; } = 0.30m;
    [JsonPropertyName("deltaMax")] public decimal DeltaMax { get; set; } = 0.60m;
}
```

- [ ] **Step 4: Wire Opener into AIConfig and extend Validate**

In `AI/AIConfig.cs`, inside `AIConfig` add after the `Rules` property:

```csharp
        [JsonPropertyName("opener")] public OpenerConfig Opener { get; set; } = new();
```

In `AIConfigLoader.Validate`, before the final `return null;`, add:

```csharp
        var op = c.Opener;
        if (op.TopNPerTicker < 1) return $"opener.topNPerTicker: must be ≥ 1, got {op.TopNPerTicker}";
        if (op.MaxCandidatesPerStructurePerTicker < 1) return $"opener.maxCandidatesPerStructurePerTicker: must be ≥ 1, got {op.MaxCandidatesPerStructurePerTicker}";
        if (op.MaxQtyPerProposal < 1) return $"opener.maxQtyPerProposal: must be ≥ 1, got {op.MaxQtyPerProposal}";
        if (op.DirectionalFitWeight < 0m) return $"opener.directionalFitWeight: must be ≥ 0, got {op.DirectionalFitWeight}";
        if (op.ProfitBandPct <= 0m || op.ProfitBandPct > 50m) return $"opener.profitBandPct: must be in (0, 50], got {op.ProfitBandPct}";
        if (op.IvDefaultPct <= 0m) return $"opener.ivDefaultPct: must be > 0, got {op.IvDefaultPct}";
        if (op.StrikeStep <= 0m) return $"opener.strikeStep: must be > 0, got {op.StrikeStep}";

        var lc = op.Structures.LongCalendar;
        if (lc.ShortDteMin < 0) return $"opener.structures.longCalendar.shortDteMin: must be ≥ 0, got {lc.ShortDteMin}";
        if (lc.ShortDteMax < lc.ShortDteMin) return $"opener.structures.longCalendar.shortDteMax: must be ≥ shortDteMin, got {lc.ShortDteMax}";
        if (lc.LongDteMin < 0) return $"opener.structures.longCalendar.longDteMin: must be ≥ 0, got {lc.LongDteMin}";
        if (lc.LongDteMax < lc.LongDteMin) return $"opener.structures.longCalendar.longDteMax: must be ≥ longDteMin, got {lc.LongDteMax}";

        var ld = op.Structures.LongDiagonal;
        if (ld.ShortDteMin < 0) return $"opener.structures.longDiagonal.shortDteMin: must be ≥ 0, got {ld.ShortDteMin}";
        if (ld.ShortDteMax < ld.ShortDteMin) return $"opener.structures.longDiagonal.shortDteMax: must be ≥ shortDteMin, got {ld.ShortDteMax}";
        if (ld.LongDteMin < 0) return $"opener.structures.longDiagonal.longDteMin: must be ≥ 0, got {ld.LongDteMin}";
        if (ld.LongDteMax < ld.LongDteMin) return $"opener.structures.longDiagonal.longDteMax: must be ≥ longDteMin, got {ld.LongDteMax}";

        var sv = op.Structures.ShortVertical;
        if (sv.DteMin < 0) return $"opener.structures.shortVertical.dteMin: must be ≥ 0, got {sv.DteMin}";
        if (sv.DteMax < sv.DteMin) return $"opener.structures.shortVertical.dteMax: must be ≥ dteMin, got {sv.DteMax}";
        if (sv.WidthSteps.Count == 0) return "opener.structures.shortVertical.widthSteps: must have at least one value";
        foreach (var w in sv.WidthSteps)
            if (w < 1) return $"opener.structures.shortVertical.widthSteps: each value must be ≥ 1, got {w}";
        if (sv.ShortDeltaMin <= 0m || sv.ShortDeltaMin >= 1m) return $"opener.structures.shortVertical.shortDeltaMin: must be in (0, 1), got {sv.ShortDeltaMin}";
        if (sv.ShortDeltaMax <= sv.ShortDeltaMin || sv.ShortDeltaMax >= 1m) return $"opener.structures.shortVertical.shortDeltaMax: must be in (shortDeltaMin, 1), got {sv.ShortDeltaMax}";

        var lcp = op.Structures.LongCallPut;
        if (lcp.DteMin < 0) return $"opener.structures.longCallPut.dteMin: must be ≥ 0, got {lcp.DteMin}";
        if (lcp.DteMax < lcp.DteMin) return $"opener.structures.longCallPut.dteMax: must be ≥ dteMin, got {lcp.DteMax}";
        if (lcp.DeltaMin <= 0m || lcp.DeltaMin >= 1m) return $"opener.structures.longCallPut.deltaMin: must be in (0, 1), got {lcp.DeltaMin}";
        if (lcp.DeltaMax <= lcp.DeltaMin || lcp.DeltaMax >= 1m) return $"opener.structures.longCallPut.deltaMax: must be in (deltaMin, 1), got {lcp.DeltaMax}";
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenerConfigValidationTests`

Expected: all 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add AI/Open/OpenerConfig.cs AI/AIConfig.cs WebullAnalytics.Tests/AI/Open/OpenerConfigValidationTests.cs
git commit -m "add opener config types and validation"
```

---

## Task 3: Update ai-config.example.json

**Files:**
- Modify: `ai-config.example.json`

- [ ] **Step 1: Add opener block to the example config**

Modify `ai-config.example.json` — add a comma after the `rules: {...}` closing brace and append:

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

- [ ] **Step 2: Verify example parses as valid config**

Run:

```bash
cd /mnt/c/dev/WebullAnalytics
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics -- ai once --config ai-config.example.json --tickers SPY --ignore-market-hours 2>&1 | head -5
```

Expected: parse error is **not** about `opener.*` fields (any downstream error is fine at this point — we only want to confirm the JSON parses and validates).

- [ ] **Step 3: Commit**

```bash
git add ai-config.example.json
git commit -m "add opener section to ai-config example"
```

---

## Task 4: Add OpenProposal record, StructureKind enum, and DirectionalFit helper

**Files:**
- Create: `AI/Open/OpenProposal.cs`
- Create: `AI/Open/DirectionalFit.cs`
- Create: `WebullAnalytics.Tests/AI/Open/DirectionalFitTests.cs`

- [ ] **Step 1: Write the failing test**

Create `WebullAnalytics.Tests/AI/Open/DirectionalFitTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class DirectionalFitTests
{
    [Theory]
    [InlineData(OpenStructureKind.LongCall, 1)]
    [InlineData(OpenStructureKind.ShortPutVertical, 1)]
    [InlineData(OpenStructureKind.LongPut, -1)]
    [InlineData(OpenStructureKind.ShortCallVertical, -1)]
    [InlineData(OpenStructureKind.LongCalendar, 0)]
    [InlineData(OpenStructureKind.LongDiagonal, 0)]
    public void FitSignMatchesSpecTable(OpenStructureKind kind, int expected)
    {
        Assert.Equal(expected, DirectionalFit.SignFor(kind));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter DirectionalFitTests`

Expected: compile failure — types don't exist.

- [ ] **Step 3: Create `OpenProposal.cs`**

Create `AI/Open/OpenProposal.cs`:

```csharp
namespace WebullAnalytics.AI;

internal enum OpenStructureKind
{
    LongCalendar,
    LongDiagonal,
    ShortPutVertical,
    ShortCallVertical,
    LongCall,
    LongPut
}

/// <summary>
/// Output of the opener pipeline. One proposal per candidate that survives scoring and ranking.
/// Peer to ManagementProposal — there is no PositionKey because no position exists.
/// </summary>
/// <param name="Ticker">Underlying symbol.</param>
/// <param name="StructureKind">Which structure family this proposal belongs to.</param>
/// <param name="Legs">Opening legs in OCC notation. Reuses ProposalLeg from the management side.</param>
/// <param name="Qty">Sized to available cash. 0 when CashReserveBlocked.</param>
/// <param name="DebitOrCreditPerContract">Negative = debit paid; positive = credit received (dollars per contract, i.e. ×100).</param>
/// <param name="MaxProfitPerContract">Positive dollars. For unlimited-profit structures (long call/put), taken as projected profit at +2σ grid point.</param>
/// <param name="MaxLossPerContract">Negative dollars (loss magnitude).</param>
/// <param name="CapitalAtRiskPerContract">Debit for longs/calendars; (width×100 − credit) for short verticals. Always ≥ 0.</param>
/// <param name="Breakevens">Underlying price levels where P&amp;L crosses zero at the target date.</param>
/// <param name="ProbabilityOfProfit">[0, 1] from Black-Scholes with neutral drift.</param>
/// <param name="ExpectedValuePerContract">From the 5-point scenario grid, dollars.</param>
/// <param name="DaysToTarget">DTE of the leg whose expiry defines the target evaluation date.</param>
/// <param name="RawScore">EV / max(1, DaysToTarget) / CapitalAtRiskPerContract.</param>
/// <param name="BiasAdjustedScore">RawScore × (1 + α · bias · fit).</param>
/// <param name="DirectionalFit">+1 / 0 / −1 from the structure-fit table.</param>
/// <param name="Rationale">Human-readable line; see spec for format.</param>
/// <param name="Fingerprint">sha1-hex of (ticker | kind | sorted(legs) | qty) — used for cross-tick dedup.</param>
/// <param name="CashReserveBlocked">True when sizing fell to 0 contracts due to the cash reserve.</param>
/// <param name="CashReserveDetail">"free $X, requires $Y per contract" when blocked; null otherwise.</param>
internal sealed record OpenProposal(
    string Ticker,
    OpenStructureKind StructureKind,
    IReadOnlyList<ProposalLeg> Legs,
    int Qty,
    decimal DebitOrCreditPerContract,
    decimal MaxProfitPerContract,
    decimal MaxLossPerContract,
    decimal CapitalAtRiskPerContract,
    IReadOnlyList<decimal> Breakevens,
    decimal ProbabilityOfProfit,
    decimal ExpectedValuePerContract,
    int DaysToTarget,
    decimal RawScore,
    decimal BiasAdjustedScore,
    int DirectionalFit,
    string Rationale,
    string Fingerprint,
    bool CashReserveBlocked = false,
    string? CashReserveDetail = null
);
```

- [ ] **Step 4: Create `DirectionalFit.cs`**

Create `AI/Open/DirectionalFit.cs`:

```csharp
namespace WebullAnalytics.AI;

internal static class DirectionalFit
{
    /// <summary>Returns +1 (bullish fit), −1 (bearish fit), or 0 (neutral) for the given structure.</summary>
    public static int SignFor(OpenStructureKind kind) => kind switch
    {
        OpenStructureKind.LongCall => 1,
        OpenStructureKind.ShortPutVertical => 1,
        OpenStructureKind.LongPut => -1,
        OpenStructureKind.ShortCallVertical => -1,
        OpenStructureKind.LongCalendar => 0,
        OpenStructureKind.LongDiagonal => 0,
        _ => 0
    };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter DirectionalFitTests`

Expected: all 6 theory cases pass.

- [ ] **Step 6: Commit**

```bash
git add AI/Open/OpenProposal.cs AI/Open/DirectionalFit.cs WebullAnalytics.Tests/AI/Open/DirectionalFitTests.cs
git commit -m "add OpenProposal type and directional-fit helper"
```

---

## Task 5: Add expiry helpers

**Files:**
- Create: `AI/Open/OpenerExpiryHelpers.cs`
- Create: `WebullAnalytics.Tests/AI/Open/OpenerExpiryHelpersTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/OpenerExpiryHelpersTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerExpiryHelpersTests
{
    [Fact]
    public void ThirdFridayInApril2026Is2026_04_17()
    {
        Assert.Equal(new DateTime(2026, 4, 17), OpenerExpiryHelpers.ThirdFridayInMonth(2026, 4));
    }

    [Fact]
    public void ThirdFridayInMay2026Is2026_05_15()
    {
        Assert.Equal(new DateTime(2026, 5, 15), OpenerExpiryHelpers.ThirdFridayInMonth(2026, 5));
    }

    [Fact]
    public void NextWeeklyExpiriesInRangeReturnsFridaysOnly()
    {
        var asOf = new DateTime(2026, 4, 20); // Monday
        var result = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte: 3, maxDte: 10).ToList();
        Assert.All(result, d => Assert.Equal(DayOfWeek.Friday, d.DayOfWeek));
        Assert.All(result, d => Assert.InRange((d - asOf.Date).Days, 3, 10));
    }

    [Fact]
    public void NextWeeklyExpiriesInRangeFromMondayIncludesFriday()
    {
        var asOf = new DateTime(2026, 4, 20); // Monday; Friday = 2026-04-24, DTE = 4
        var result = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte: 3, maxDte: 10).ToList();
        Assert.Contains(new DateTime(2026, 4, 24), result);
    }

    [Fact]
    public void MonthlyExpiriesInRangeReturnsThirdFridays()
    {
        var asOf = new DateTime(2026, 4, 1);
        var result = OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, minDte: 0, maxDte: 60).ToList();
        Assert.Contains(new DateTime(2026, 4, 17), result);
        Assert.Contains(new DateTime(2026, 5, 15), result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenerExpiryHelpersTests`

Expected: compile failure — `OpenerExpiryHelpers` doesn't exist.

- [ ] **Step 3: Create the helper**

Create `AI/Open/OpenerExpiryHelpers.cs`:

```csharp
namespace WebullAnalytics.AI;

internal static class OpenerExpiryHelpers
{
    /// <summary>Returns the 3rd-Friday date in the given month. No holiday adjustment — standard monthly expiries.</summary>
    public static DateTime ThirdFridayInMonth(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var firstFridayOffset = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(firstFridayOffset + 14);
    }

    /// <summary>Enumerates all Fridays strictly after <paramref name="asOf"/> whose DTE lands in [minDte, maxDte].</summary>
    public static IEnumerable<DateTime> NextWeeklyExpiriesInRange(DateTime asOf, int minDte, int maxDte)
    {
        var start = asOf.Date.AddDays(minDte);
        var end = asOf.Date.AddDays(maxDte);
        // Find the first Friday on or after `start`.
        var firstFridayOffset = ((int)DayOfWeek.Friday - (int)start.DayOfWeek + 7) % 7;
        for (var d = start.AddDays(firstFridayOffset); d <= end; d = d.AddDays(7))
            yield return d;
    }

    /// <summary>Enumerates 3rd-Friday monthlies whose DTE falls in [minDte, maxDte], ordered by date.</summary>
    public static IEnumerable<DateTime> MonthlyExpiriesInRange(DateTime asOf, int minDte, int maxDte)
    {
        var start = asOf.Date.AddDays(minDte);
        var end = asOf.Date.AddDays(maxDte);
        var year = start.Year;
        var month = start.Month;
        // Look at most 3 months ahead of `end` to guarantee coverage.
        var stopYear = end.Year;
        var stopMonth = end.Month + 1;
        if (stopMonth > 12) { stopMonth -= 12; stopYear++; }
        while (year < stopYear || (year == stopYear && month <= stopMonth))
        {
            var third = ThirdFridayInMonth(year, month);
            if (third >= start && third <= end) yield return third;
            month++;
            if (month > 12) { month = 1; year++; }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenerExpiryHelpersTests`

Expected: all 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/OpenerExpiryHelpers.cs WebullAnalytics.Tests/AI/Open/OpenerExpiryHelpersTests.cs
git commit -m "add opener expiry enumeration helpers"
```

---

## Task 6: Add CandidateSkeleton type

**Files:**
- Create: `AI/Open/CandidateSkeleton.cs`

- [ ] **Step 1: Create the type**

Create `AI/Open/CandidateSkeleton.cs`:

```csharp
namespace WebullAnalytics.AI;

/// <summary>
/// Intermediate type produced by CandidateEnumerator. Contains only structural information —
/// no quotes, no scoring. Consumed by CandidateScorer which runs the BS math against current quotes.
/// </summary>
/// <param name="Ticker">Underlying symbol.</param>
/// <param name="StructureKind">Which structure family.</param>
/// <param name="Legs">Opening legs (buy/sell × OCC × qty=1). Scorer multiplies out per-contract numbers.</param>
/// <param name="TargetExpiry">The date used as the "target" for scoring: short-leg expiry for calendars/diagonals and short verticals; the leg's own expiry for long call/put.</param>
internal sealed record CandidateSkeleton(
    string Ticker,
    OpenStructureKind StructureKind,
    IReadOnlyList<ProposalLeg> Legs,
    DateTime TargetExpiry
);
```

- [ ] **Step 2: Verify build**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add AI/Open/CandidateSkeleton.cs
git commit -m "add CandidateSkeleton intermediate type"
```

---

## Task 7: CandidateEnumerator — long calendars and diagonals

**Files:**
- Create: `AI/Open/CandidateEnumerator.cs` (partial — calendars/diagonals first)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorCalendarTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorCalendarTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorCalendarTests
{
    private static OpenerConfig DefaultCfg() => new()
    {
        StrikeStep = 1.0m,
        Structures = new OpenerStructuresConfig
        {
            LongCalendar = new OpenerCalendarLikeConfig { Enabled = true, ShortDteMin = 3, ShortDteMax = 10, LongDteMin = 21, LongDteMax = 60 },
            LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
            ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
            LongCallPut = new OpenerLongCallPutConfig { Enabled = false }
        }
    };

    [Fact]
    public void CalendarProducesBothCallAndPutVariants()
    {
        var asOf = new DateTime(2026, 4, 20); // Monday
        var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, DefaultCfg()).ToList();
        Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongCalendar && s.Legs.Any(l => l.Symbol.Contains("C00015")));
        Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongCalendar && s.Legs.Any(l => l.Symbol.Contains("P00015")));
    }

    [Fact]
    public void CalendarLegsUseMatchingStrikes()
    {
        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, DefaultCfg())
            .Where(s => s.StructureKind == OpenStructureKind.LongCalendar).ToList();
        Assert.NotEmpty(skeletons);
        foreach (var s in skeletons)
        {
            Assert.Equal(2, s.Legs.Count);
            var p0 = ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!;
            var p1 = ParsingHelpers.ParseOptionSymbol(s.Legs[1].Symbol)!;
            Assert.Equal(p0.Strike, p1.Strike);
            Assert.Equal(p0.CallPut, p1.CallPut);
            Assert.NotEqual(p0.ExpiryDate, p1.ExpiryDate);
        }
    }

    [Fact]
    public void DiagonalUsesOffsetStrikes()
    {
        var cfg = DefaultCfg();
        cfg.Structures.LongCalendar.Enabled = false;
        cfg.Structures.LongDiagonal.Enabled = true;

        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, cfg)
            .Where(s => s.StructureKind == OpenStructureKind.LongDiagonal).ToList();
        Assert.NotEmpty(skeletons);
        foreach (var s in skeletons)
        {
            var shortLeg = s.Legs.First(l => l.Action == "sell");
            var longLeg = s.Legs.First(l => l.Action == "buy");
            var ps = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
            var pl = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol)!;
            Assert.Equal(1.0m, Math.Abs(ps.Strike - pl.Strike));
        }
    }

    [Fact]
    public void DisabledStructureProducesNothing()
    {
        var cfg = DefaultCfg();
        cfg.Structures.LongCalendar.Enabled = false;
        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("GME", spot: 15.0m, asOf, cfg).ToList();
        Assert.Empty(skeletons);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateEnumeratorCalendarTests`

Expected: compile failure — `CandidateEnumerator` doesn't exist.

- [ ] **Step 3: Create the enumerator with calendar + diagonal support**

Create `AI/Open/CandidateEnumerator.cs`:

```csharp
namespace WebullAnalytics.AI;

internal static class CandidateEnumerator
{
    public static IEnumerable<CandidateSkeleton> Enumerate(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg)
    {
        if (cfg.Structures.LongCalendar.Enabled)
            foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongCalendar, OpenStructureKind.LongCalendar))
                yield return sk;

        if (cfg.Structures.LongDiagonal.Enabled)
            foreach (var sk in EnumerateCalendarLike(ticker, spot, asOf, cfg, cfg.Structures.LongDiagonal, OpenStructureKind.LongDiagonal))
                yield return sk;
    }

    private static IEnumerable<CandidateSkeleton> EnumerateCalendarLike(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg, OpenerCalendarLikeConfig sCfg, OpenStructureKind kind)
    {
        var shortExps = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, sCfg.ShortDteMin, sCfg.ShortDteMax).ToList();
        var longExps = OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, sCfg.LongDteMin, sCfg.LongDteMax).ToList();
        if (shortExps.Count == 0 || longExps.Count == 0) yield break;

        var step = cfg.StrikeStep;
        foreach (var shortStrike in StrikeGrid(spot, step))
        {
            // Skip strikes that are ITM by more than one step on either side (bad entry for a debit calendar).
            foreach (var callPut in new[] { "C", "P" })
            {
                if (callPut == "C" && shortStrike < spot - step) continue;
                if (callPut == "P" && shortStrike > spot + step) continue;

                foreach (var shortExp in shortExps)
                    foreach (var longExp in longExps)
                    {
                        if (longExp <= shortExp) continue;

                        if (kind == OpenStructureKind.LongCalendar)
                        {
                            var longStrike = shortStrike;
                            yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
                        }
                        else
                        {
                            // Diagonal: long strike one step above or below short strike.
                            foreach (var longStrike in new[] { shortStrike - step, shortStrike + step })
                            {
                                if (longStrike <= 0m) continue;
                                yield return BuildSpread(ticker, kind, shortExp, longExp, shortStrike, longStrike, callPut);
                            }
                        }
                    }
            }
        }
    }

    /// <summary>Five distinct strikes centered on spot: floor(spot/step)*step, ceil(spot/step)*step, and ±1 step around each.</summary>
    internal static IReadOnlyList<decimal> StrikeGrid(decimal spot, decimal step)
    {
        var atmBelow = Math.Floor(spot / step) * step;
        var atmAbove = Math.Ceiling(spot / step) * step;
        var set = new HashSet<decimal>
        {
            atmBelow,
            atmAbove,
            atmBelow - step,
            atmAbove + step,
            atmBelow - 2m * step
        };
        return set.Where(s => s > 0m).OrderBy(s => s).ToList();
    }

    private static CandidateSkeleton BuildSpread(string ticker, OpenStructureKind kind, DateTime shortExp, DateTime longExp, decimal shortStrike, decimal longStrike, string callPut)
    {
        var shortSym = MatchKeys.OccSymbol(ticker, shortExp, shortStrike, callPut);
        var longSym = MatchKeys.OccSymbol(ticker, longExp, longStrike, callPut);
        var legs = new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        };
        return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: shortExp);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateEnumeratorCalendarTests`

Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateEnumerator.cs WebullAnalytics.Tests/AI/Open/CandidateEnumeratorCalendarTests.cs
git commit -m "enumerate long-calendar and long-diagonal candidates"
```

---

## Task 8: CandidateEnumerator — short verticals

**Files:**
- Modify: `AI/Open/CandidateEnumerator.cs` (add vertical enumeration)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorVerticalTests.cs`

Short verticals are delta-based — the enumerator needs access to BS delta, which requires an IV input. Since the enumerator is "pure" (no quote snapshot), it uses `cfg.IvDefaultPct` as the IV proxy for strike selection only. The scorer later refines with live IV.

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorVerticalTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorVerticalTests
{
    private static OpenerConfig Cfg() => new()
    {
        StrikeStep = 1.0m,
        IvDefaultPct = 40m,
        Structures = new OpenerStructuresConfig
        {
            LongCalendar = new OpenerCalendarLikeConfig { Enabled = false },
            LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
            ShortVertical = new OpenerShortVerticalConfig
            {
                Enabled = true,
                DteMin = 3, DteMax = 10,
                WidthSteps = new() { 1, 2 },
                ShortDeltaMin = 0.15m, ShortDeltaMax = 0.30m
            },
            LongCallPut = new OpenerLongCallPutConfig { Enabled = false }
        }
    };

    [Fact]
    public void ProducesBothCallAndPutSides()
    {
        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg()).ToList();
        Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.ShortPutVertical);
        Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.ShortCallVertical);
    }

    [Fact]
    public void PutCreditSpreadShortBelowSpot()
    {
        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg())
            .Where(s => s.StructureKind == OpenStructureKind.ShortPutVertical).ToList();
        foreach (var s in skeletons)
        {
            var shortLeg = s.Legs.First(l => l.Action == "sell");
            var parsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
            Assert.True(parsed.Strike < 500m, $"short strike {parsed.Strike} should be below spot 500 for put credit");
        }
    }

    [Fact]
    public void CallCreditSpreadShortAboveSpot()
    {
        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg())
            .Where(s => s.StructureKind == OpenStructureKind.ShortCallVertical).ToList();
        foreach (var s in skeletons)
        {
            var shortLeg = s.Legs.First(l => l.Action == "sell");
            var parsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
            Assert.True(parsed.Strike > 500m, $"short strike {parsed.Strike} should be above spot 500 for call credit");
        }
    }

    [Fact]
    public void WidthMatchesConfiguredSteps()
    {
        var asOf = new DateTime(2026, 4, 20);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg()).ToList();
        foreach (var s in skeletons.Where(x => x.StructureKind is OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical))
        {
            var shortLeg = s.Legs.First(l => l.Action == "sell");
            var longLeg = s.Legs.First(l => l.Action == "buy");
            var ps = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol)!;
            var pl = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol)!;
            var width = Math.Abs(ps.Strike - pl.Strike);
            Assert.Contains(width, new[] { 1.0m, 2.0m });
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateEnumeratorVerticalTests`

Expected: tests run but fail — vertical enumeration not implemented.

- [ ] **Step 3: Add vertical enumeration to CandidateEnumerator**

In `AI/Open/CandidateEnumerator.cs`, in the `Enumerate` method, append before the last `}` of the method:

```csharp
        if (cfg.Structures.ShortVertical.Enabled)
            foreach (var sk in EnumerateShortVerticals(ticker, spot, asOf, cfg))
                yield return sk;
```

Then add these methods before the final `}` of the class:

```csharp
    private static IEnumerable<CandidateSkeleton> EnumerateShortVerticals(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg)
    {
        var sCfg = cfg.Structures.ShortVertical;
        var exps = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, sCfg.DteMin, sCfg.DteMax).ToList();
        if (exps.Count == 0) yield break;

        var iv = cfg.IvDefaultPct / 100m;
        var step = cfg.StrikeStep;

        foreach (var exp in exps)
        {
            var years = Math.Max(1, (exp.Date - asOf.Date).Days) / 365.0;

            // Put credit side (bullish): short strike below spot.
            foreach (var shortStrike in StrikesBelowSpot(spot, step, count: 8))
            {
                var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "P"));
                if (delta < sCfg.ShortDeltaMin || delta > sCfg.ShortDeltaMax) continue;
                foreach (var widthSteps in sCfg.WidthSteps)
                {
                    var longStrike = shortStrike - widthSteps * step;
                    if (longStrike <= 0m) continue;
                    yield return BuildVertical(ticker, OpenStructureKind.ShortPutVertical, exp, shortStrike, longStrike, "P");
                }
            }

            // Call credit side (bearish): short strike above spot.
            foreach (var shortStrike in StrikesAboveSpot(spot, step, count: 8))
            {
                var delta = Math.Abs(OptionMath.Delta(spot, shortStrike, years, OptionMath.RiskFreeRate, iv, "C"));
                if (delta < sCfg.ShortDeltaMin || delta > sCfg.ShortDeltaMax) continue;
                foreach (var widthSteps in sCfg.WidthSteps)
                {
                    var longStrike = shortStrike + widthSteps * step;
                    yield return BuildVertical(ticker, OpenStructureKind.ShortCallVertical, exp, shortStrike, longStrike, "C");
                }
            }
        }
    }

    private static IEnumerable<decimal> StrikesBelowSpot(decimal spot, decimal step, int count)
    {
        var k = Math.Floor(spot / step) * step;
        for (int i = 0; i < count; i++)
        {
            if (k <= 0m) yield break;
            yield return k;
            k -= step;
        }
    }

    private static IEnumerable<decimal> StrikesAboveSpot(decimal spot, decimal step, int count)
    {
        var k = Math.Ceiling(spot / step) * step;
        for (int i = 0; i < count; i++)
        {
            yield return k;
            k += step;
        }
    }

    private static CandidateSkeleton BuildVertical(string ticker, OpenStructureKind kind, DateTime exp, decimal shortStrike, decimal longStrike, string callPut)
    {
        var shortSym = MatchKeys.OccSymbol(ticker, exp, shortStrike, callPut);
        var longSym = MatchKeys.OccSymbol(ticker, exp, longStrike, callPut);
        var legs = new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        };
        return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: exp);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateEnumeratorVerticalTests`

Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateEnumerator.cs WebullAnalytics.Tests/AI/Open/CandidateEnumeratorVerticalTests.cs
git commit -m "enumerate short-vertical candidates via delta band"
```

---

## Task 9: CandidateEnumerator — single long calls and puts

**Files:**
- Modify: `AI/Open/CandidateEnumerator.cs` (add single-leg enumeration)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorLongCallPutTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateEnumeratorLongCallPutTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateEnumeratorLongCallPutTests
{
    private static OpenerConfig Cfg() => new()
    {
        StrikeStep = 1.0m,
        IvDefaultPct = 40m,
        Structures = new OpenerStructuresConfig
        {
            LongCalendar = new OpenerCalendarLikeConfig { Enabled = false },
            LongDiagonal = new OpenerCalendarLikeConfig { Enabled = false },
            ShortVertical = new OpenerShortVerticalConfig { Enabled = false },
            LongCallPut = new OpenerLongCallPutConfig
            {
                Enabled = true, DteMin = 21, DteMax = 60, DeltaMin = 0.30m, DeltaMax = 0.60m
            }
        }
    };

    [Fact]
    public void ProducesBothLongCallAndLongPut()
    {
        var asOf = new DateTime(2026, 4, 1);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg()).ToList();
        Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongCall);
        Assert.Contains(skeletons, s => s.StructureKind == OpenStructureKind.LongPut);
    }

    [Fact]
    public void LongCallIsSingleBuyLeg()
    {
        var asOf = new DateTime(2026, 4, 1);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg())
            .Where(s => s.StructureKind == OpenStructureKind.LongCall).ToList();
        foreach (var s in skeletons)
        {
            Assert.Single(s.Legs);
            Assert.Equal("buy", s.Legs[0].Action);
            var parsed = ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!;
            Assert.Equal("C", parsed.CallPut);
        }
    }

    [Fact]
    public void LongPutIsSingleBuyLeg()
    {
        var asOf = new DateTime(2026, 4, 1);
        var skeletons = CandidateEnumerator.Enumerate("SPY", spot: 500m, asOf, Cfg())
            .Where(s => s.StructureKind == OpenStructureKind.LongPut).ToList();
        foreach (var s in skeletons)
        {
            Assert.Single(s.Legs);
            Assert.Equal("buy", s.Legs[0].Action);
            var parsed = ParsingHelpers.ParseOptionSymbol(s.Legs[0].Symbol)!;
            Assert.Equal("P", parsed.CallPut);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateEnumeratorLongCallPutTests`

Expected: tests fail (no long-call/put output).

- [ ] **Step 3: Add long call/put enumeration**

In `AI/Open/CandidateEnumerator.cs`, in the `Enumerate` method, append before the last `}`:

```csharp
        if (cfg.Structures.LongCallPut.Enabled)
            foreach (var sk in EnumerateLongCallPut(ticker, spot, asOf, cfg))
                yield return sk;
```

Add the method:

```csharp
    private static IEnumerable<CandidateSkeleton> EnumerateLongCallPut(string ticker, decimal spot, DateTime asOf, OpenerConfig cfg)
    {
        var sCfg = cfg.Structures.LongCallPut;
        var exps = OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, sCfg.DteMin, sCfg.DteMax).Take(2).ToList();
        if (exps.Count == 0) yield break;

        var iv = cfg.IvDefaultPct / 100m;
        var step = cfg.StrikeStep;

        foreach (var exp in exps)
        {
            var years = Math.Max(1, (exp.Date - asOf.Date).Days) / 365.0;

            foreach (var callPut in new[] { "C", "P" })
            {
                // Widen outward from ATM on both sides and accept the first strikes whose delta lands in the band.
                foreach (var strike in StrikesAroundSpot(spot, step, count: 10))
                {
                    var delta = Math.Abs(OptionMath.Delta(spot, strike, years, OptionMath.RiskFreeRate, iv, callPut));
                    if (delta < sCfg.DeltaMin || delta > sCfg.DeltaMax) continue;

                    var sym = MatchKeys.OccSymbol(ticker, exp, strike, callPut);
                    var legs = new[] { new ProposalLeg("buy", sym, 1) };
                    var kind = callPut == "C" ? OpenStructureKind.LongCall : OpenStructureKind.LongPut;
                    yield return new CandidateSkeleton(ticker, kind, legs, TargetExpiry: exp);
                }
            }
        }
    }

    private static IEnumerable<decimal> StrikesAroundSpot(decimal spot, decimal step, int count)
    {
        var below = StrikesBelowSpot(spot, step, count).ToList();
        var above = StrikesAboveSpot(spot, step, count).ToList();
        return below.Concat(above).Distinct();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateEnumeratorLongCallPutTests`

Expected: all 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateEnumerator.cs WebullAnalytics.Tests/AI/Open/CandidateEnumeratorLongCallPutTests.cs
git commit -m "enumerate long-call and long-put candidates via delta band"
```

---

## Task 10: Scoring scaffolding — CandidateScorer skeleton + raw/bias score

**Files:**
- Create: `AI/Open/CandidateScorer.cs` (skeleton + score arithmetic)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateScorerBiasTests.cs`

- [ ] **Step 1: Write the failing test**

Create `WebullAnalytics.Tests/AI/Open/CandidateScorerBiasTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerBiasTests
{
    [Fact]
    public void RawScoreInvariantToBiasWhenFitIsZero()
    {
        var raw = 0.005m;
        var withoutBias = CandidateScorer.BiasAdjust(raw, bias: 0m, fit: 0, alpha: 0.5m);
        var withBias = CandidateScorer.BiasAdjust(raw, bias: 0.8m, fit: 0, alpha: 0.5m);
        Assert.Equal(withoutBias, withBias);
        Assert.Equal(raw, withBias);
    }

    [Fact]
    public void PositiveBiasBoostsPositiveFit()
    {
        var raw = 0.010m;
        var adjusted = CandidateScorer.BiasAdjust(raw, bias: 0.4m, fit: 1, alpha: 0.5m);
        // 0.010 × (1 + 0.5 × 0.4 × 1) = 0.010 × 1.20 = 0.012
        Assert.Equal(0.012m, adjusted);
    }

    [Fact]
    public void NegativeBiasCutsPositiveFit()
    {
        var raw = 0.010m;
        var adjusted = CandidateScorer.BiasAdjust(raw, bias: -0.4m, fit: 1, alpha: 0.5m);
        // 0.010 × (1 + 0.5 × −0.4 × 1) = 0.010 × 0.80 = 0.008
        Assert.Equal(0.008m, adjusted);
    }

    [Fact]
    public void NegativeBiasBoostsNegativeFit()
    {
        var raw = 0.010m;
        var adjusted = CandidateScorer.BiasAdjust(raw, bias: -0.4m, fit: -1, alpha: 0.5m);
        Assert.Equal(0.012m, adjusted);
    }

    [Fact]
    public void RawScoreFormulaHonorsZeroDaysClamp()
    {
        // EV 100, days 0 → clamped to 1; capitalAtRisk 200 → 100 / 1 / 200 = 0.5
        Assert.Equal(0.5m, CandidateScorer.ComputeRawScore(ev: 100m, daysToTarget: 0, capitalAtRisk: 200m));
    }

    [Fact]
    public void RawScoreZeroWhenCapitalAtRiskZero()
    {
        Assert.Equal(0m, CandidateScorer.ComputeRawScore(ev: 50m, daysToTarget: 5, capitalAtRisk: 0m));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerBiasTests`

Expected: compile failure — `CandidateScorer` doesn't exist.

- [ ] **Step 3: Create the scorer skeleton**

Create `AI/Open/CandidateScorer.cs`:

```csharp
namespace WebullAnalytics.AI;

internal static class CandidateScorer
{
    /// <summary>Score formula: EV / max(1, days) / capitalAtRisk. Returns 0 when capitalAtRisk ≤ 0.</summary>
    public static decimal ComputeRawScore(decimal ev, int daysToTarget, decimal capitalAtRisk)
    {
        if (capitalAtRisk <= 0m) return 0m;
        var days = Math.Max(1, daysToTarget);
        return ev / days / capitalAtRisk;
    }

    /// <summary>BiasAdjustedScore = raw × (1 + α · bias · fit). fit = 0 yields raw unchanged regardless of bias.</summary>
    public static decimal BiasAdjust(decimal raw, decimal bias, int fit, decimal alpha)
    {
        var factor = 1m + alpha * bias * fit;
        return raw * factor;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerBiasTests`

Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateScorer.cs WebullAnalytics.Tests/AI/Open/CandidateScorerBiasTests.cs
git commit -m "add score arithmetic helpers to CandidateScorer"
```

---

## Task 11: Log-normal scenario grid helper

**Files:**
- Modify: `AI/Open/CandidateScorer.cs` (add `BuildScenarioGrid`)
- Create: `WebullAnalytics.Tests/AI/Open/ScenarioGridTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/ScenarioGridTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class ScenarioGridTests
{
    [Fact]
    public void GridHasFivePoints()
    {
        var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
        Assert.Equal(5, grid.Count);
    }

    [Fact]
    public void WeightsSumToOne()
    {
        var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
        var sum = grid.Sum(p => p.Weight);
        Assert.InRange((double)sum, 0.999, 1.001);
    }

    [Fact]
    public void MiddlePointEqualsSpot()
    {
        var grid = CandidateScorer.BuildScenarioGrid(spot: 100m, ivAnnual: 0.40m, years: 30.0 / 365.0);
        var mid = grid[2];
        Assert.Equal(100m, mid.SpotAtExpiry);
    }

    [Fact]
    public void EndpointsAreSpotTimesExpPlusMinus2Sigma()
    {
        var spot = 100m;
        var iv = 0.40m;
        var years = 30.0 / 365.0;
        var sigma = (decimal)((double)iv * Math.Sqrt(years));
        var grid = CandidateScorer.BuildScenarioGrid(spot, iv, years);
        var expected_lo = spot * (decimal)Math.Exp((double)(-2m * sigma));
        var expected_hi = spot * (decimal)Math.Exp((double)(+2m * sigma));
        Assert.InRange((double)grid[0].SpotAtExpiry, (double)(expected_lo - 0.01m), (double)(expected_lo + 0.01m));
        Assert.InRange((double)grid[4].SpotAtExpiry, (double)(expected_hi - 0.01m), (double)(expected_hi + 0.01m));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter ScenarioGridTests`

Expected: compile failure — `BuildScenarioGrid` / `ScenarioPoint` don't exist.

- [ ] **Step 3: Add the grid helper**

In `AI/Open/CandidateScorer.cs`, add inside the class:

```csharp
    /// <summary>One point in the 5-point log-normal scenario grid used to compute EV at target expiry.</summary>
    public readonly record struct ScenarioPoint(decimal SpotAtExpiry, decimal Weight);

    /// <summary>
    /// Builds 5 scenario points at S_T ∈ {spot·e^(−2σ), spot·e^(−σ), spot, spot·e^(+σ), spot·e^(+2σ)}
    /// where σ = ivAnnual · √years. Weights = log-normal density at each point, renormalized to sum to 1.
    /// Neutral drift.
    /// </summary>
    public static IReadOnlyList<ScenarioPoint> BuildScenarioGrid(decimal spot, decimal ivAnnual, double years)
    {
        var sigma = (double)ivAnnual * Math.Sqrt(Math.Max(1e-9, years));
        var multipliers = new[] { -2.0, -1.0, 0.0, 1.0, 2.0 };
        var points = new ScenarioPoint[5];

        // Unnormalized log-normal density at each z-point: φ(z) = (1/√(2π)) · e^(−z²/2). Weights are the densities.
        double[] rawWeights = new double[5];
        double totalWeight = 0;
        for (int i = 0; i < 5; i++)
        {
            var z = multipliers[i];
            rawWeights[i] = Math.Exp(-z * z / 2.0);
            totalWeight += rawWeights[i];
        }
        for (int i = 0; i < 5; i++)
        {
            var sT = (decimal)((double)spot * Math.Exp(multipliers[i] * sigma));
            var w = (decimal)(rawWeights[i] / totalWeight);
            points[i] = new ScenarioPoint(sT, w);
        }
        return points;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter ScenarioGridTests`

Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateScorer.cs WebullAnalytics.Tests/AI/Open/ScenarioGridTests.cs
git commit -m "add log-normal scenario grid for EV computation"
```

---

## Task 12: Scoring — long call / long put

**Files:**
- Modify: `AI/Open/CandidateScorer.cs` (add `ScoreLongCallPut`, internal helpers)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateScorerLongCallPutTests.cs`

This task computes the first structure's full proposal. The input is a `CandidateSkeleton` (single buy leg) plus a mini-snapshot of quotes and bias; the output is a populated `OpenProposal`.

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateScorerLongCallPutTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerLongCallPutTests
{
    private const decimal Alpha = 0.5m;

    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = Alpha, ProfitBandPct = 5m };

    [Fact]
    public void LongCallBreakevenIsStrikePlusDebit()
    {
        var asOf = new DateTime(2026, 4, 1);
        var exp = new DateTime(2026, 5, 15);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);

        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };

        var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;

        Assert.Single(p.Breakevens);
        // Breakeven = strike + ask (pay the ask on long)
        Assert.Equal(505.10m, p.Breakevens[0]);
    }

    [Fact]
    public void LongCallCapitalAtRiskEqualsDebit()
    {
        var asOf = new DateTime(2026, 4, 1);
        var exp = new DateTime(2026, 5, 15);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        Assert.Equal(510m, p.CapitalAtRiskPerContract);   // 5.10 × 100
        Assert.Equal(-510m, p.DebitOrCreditPerContract);  // negative = debit
    }

    [Fact]
    public void LongCallPopIsProbSpotGreaterThanBreakeven()
    {
        var asOf = new DateTime(2026, 4, 1);
        var exp = new DateTime(2026, 5, 1); // 30 DTE
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(5.00m, 5.00m, 0.40m)
        };
        var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        // Breakeven 505, spot 500 at 30 DTE with 40% IV: POP < 0.5, positive, bounded
        Assert.InRange((double)p.ProbabilityOfProfit, 0.30, 0.50);
    }

    [Fact]
    public void LongCallDirectionalFitIsPositiveOne()
    {
        var asOf = new DateTime(2026, 4, 1);
        var exp = new DateTime(2026, 5, 1);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(5.00m, 5.00m, 0.40m)
        };
        var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0.40m, Cfg())!;
        Assert.Equal(1, p.DirectionalFit);
        Assert.NotEqual(p.RawScore, p.BiasAdjustedScore);
    }

    [Fact]
    public void LongPutBreakevenIsStrikeMinusDebit()
    {
        var asOf = new DateTime(2026, 4, 1);
        var exp = new DateTime(2026, 5, 1);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "P");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongPut, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(3.90m, 4.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        Assert.Equal(495.90m, p.Breakevens[0]);
        Assert.Equal(-1, p.DirectionalFit);
    }

    [Fact]
    public void MissingQuoteReturnsNull()
    {
        var asOf = new DateTime(2026, 4, 1);
        var exp = new DateTime(2026, 5, 1);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>(); // empty
        var p = CandidateScorer.ScoreLongCallPut(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg());
        Assert.Null(p);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerLongCallPutTests`

Expected: compile failure — `ScoreLongCallPut` doesn't exist.

- [ ] **Step 3: Implement `ScoreLongCallPut` and shared helpers**

In `AI/Open/CandidateScorer.cs`, add inside the class:

```csharp
    /// <summary>Resolves IV from live quote → config default, as a decimal fraction (e.g. 0.40).</summary>
    public static decimal ResolveIv(string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal defaultPct)
    {
        if (quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility.HasValue && q.ImpliedVolatility.Value > 0m)
            return q.ImpliedVolatility.Value;
        return defaultPct / 100m;
    }

    /// <summary>Looks up bid/ask, returning null if any leg lacks a usable two-sided quote.</summary>
    public static (decimal bid, decimal ask)? TryLiveBidAsk(string symbol, IReadOnlyDictionary<string, OptionContractQuote> quotes)
    {
        if (!quotes.TryGetValue(symbol, out var q)) return null;
        if (!q.Bid.HasValue || !q.Ask.HasValue) return null;
        if (q.Bid.Value < 0m || q.Ask.Value <= 0m) return null;
        return (q.Bid.Value, q.Ask.Value);
    }

    /// <summary>sha1-hex fingerprint of (ticker | kind | sorted legs | qty). Stable across ticks.</summary>
    public static string ComputeFingerprint(string ticker, OpenStructureKind kind, IReadOnlyList<ProposalLeg> legs, int qty)
    {
        var sortedLegs = string.Join("|", legs
            .Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}")
            .OrderBy(s => s, StringComparer.Ordinal));
        var payload = $"{ticker}|{kind}|{sortedLegs}|{qty}";
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static OpenProposal? ScoreLongCallPut(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg)
    {
        var leg = skel.Legs[0]; // single buy leg
        var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
        if (parsed == null) return null;

        var quote = TryLiveBidAsk(leg.Symbol, quotes);
        if (quote == null) return null;
        var (_, ask) = quote.Value;

        var years = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days) / 365.0;
        var iv = ResolveIv(leg.Symbol, quotes, cfg.IvDefaultPct);

        var debitPerShare = ask;
        var debitPerContract = debitPerShare * 100m;
        var breakeven = parsed.CallPut == "C" ? parsed.Strike + debitPerShare : parsed.Strike - debitPerShare;

        // POP = P(S_T > breakeven) for call; P(S_T < breakeven) for put, computed under log-normal neutral drift via BS d2.
        var pop = LogNormalProbability(parsed.CallPut == "C" ? Direction.Above : Direction.Below, spot, breakeven, years, (double)iv);

        // EV via scenario grid.
        var grid = BuildScenarioGrid(spot, iv, years);
        decimal ev = 0m;
        decimal maxProfit = 0m, maxLoss = -debitPerContract; // worst case = lose whole debit if finishes OTM
        foreach (var pt in grid)
        {
            var intrinsic = parsed.CallPut == "C"
                ? Math.Max(0m, pt.SpotAtExpiry - parsed.Strike)
                : Math.Max(0m, parsed.Strike - pt.SpotAtExpiry);
            var pnl = intrinsic * 100m - debitPerContract;
            ev += pt.Weight * pnl;
            if (pnl > maxProfit) maxProfit = pnl;
        }

        var capitalAtRisk = debitPerContract;
        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        var fit = DirectionalFit.SignFor(skel.StructureKind);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight);

        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: skel.Legs,
            Qty: 1,
            DebitOrCreditPerContract: -debitPerContract,
            MaxProfitPerContract: maxProfit,
            MaxLossPerContract: maxLoss,
            CapitalAtRiskPerContract: capitalAtRisk,
            Breakevens: new[] { breakeven },
            ProbabilityOfProfit: pop,
            ExpectedValuePerContract: ev,
            DaysToTarget: daysToTarget,
            RawScore: rawScore,
            BiasAdjustedScore: biasAdj,
            DirectionalFit: fit,
            Rationale: "",       // populated by OpenCandidateEvaluator after final sizing/blocked tag
            Fingerprint: fp
        );
    }

    internal enum Direction { Above, Below }

    /// <summary>
    /// P(S_T &gt; level) or P(S_T &lt; level) under log-normal neutral drift:
    /// d2 = (ln(S/K) − σ²·T/2) / (σ·√T); P(S_T &gt; K) = N(d2); P(S_T &lt; K) = 1 − N(d2).
    /// </summary>
    public static decimal LogNormalProbability(Direction dir, decimal spot, decimal level, double years, double ivAnnual)
    {
        if (level <= 0m || spot <= 0m || years <= 0 || ivAnnual <= 0) return 0m;
        var sigmaSqrtT = ivAnnual * Math.Sqrt(years);
        var d2 = (Math.Log((double)spot / (double)level) - 0.5 * ivAnnual * ivAnnual * years) / sigmaSqrtT;
        var N_d2 = OptionMath.NormalCdf(d2);
        return (decimal)(dir == Direction.Above ? N_d2 : 1.0 - N_d2);
    }
```

This references `OptionMath.NormalCdf` and `OptionMath.RiskFreeRate`. Verify these exist by running:

```bash
grep -n "NormalCdf\|RiskFreeRate" /mnt/c/dev/WebullAnalytics/OptionMath.cs
```

Expected: both are present. If `NormalCdf` is named differently (e.g. `Phi`, `CND`), substitute the actual name in the code above.

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerLongCallPutTests`

Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateScorer.cs WebullAnalytics.Tests/AI/Open/CandidateScorerLongCallPutTests.cs
git commit -m "score long call / put candidates"
```

---

## Task 13: Scoring — short verticals

**Files:**
- Modify: `AI/Open/CandidateScorer.cs` (add `ScoreShortVertical`)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateScorerShortVerticalTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateScorerShortVerticalTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerShortVerticalTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m };

    private static (CandidateSkeleton skel, Dictionary<string, OptionContractQuote> quotes) PutCreditSpread()
    {
        // SPY put credit: sell 495P / buy 494P, 7 DTE, assume bid/ask mid yields 0.60 / 0.20 credit
        var asOf = new DateTime(2026, 4, 20);
        var exp = new DateTime(2026, 4, 24); // 4 DTE (Mon → Fri)
        var shortSym = MatchKeys.OccSymbol("SPY", exp, 495m, "P");
        var longSym = MatchKeys.OccSymbol("SPY", exp, 494m, "P");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.ShortPutVertical, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(0.60m, 0.65m, 0.40m),
            [longSym] = TestQuote.Q(0.18m, 0.22m, 0.40m)
        };
        return (skel, quotes);
    }

    [Fact]
    public void PutCreditSpreadCreditIsShortBidMinusLongAsk()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        // credit = (0.60 − 0.22) × 100 = 38
        Assert.Equal(38m, p.DebitOrCreditPerContract);
    }

    [Fact]
    public void PutCreditSpreadCapitalAtRiskIsWidthMinusCredit()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        // width × 100 − credit = 1.0 × 100 − 38 = 62
        Assert.Equal(62m, p.CapitalAtRiskPerContract);
    }

    [Fact]
    public void PutCreditSpreadMaxProfitIsCredit()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(38m, p.MaxProfitPerContract);
    }

    [Fact]
    public void PutCreditSpreadMaxLossIsNegativeRisk()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(-62m, p.MaxLossPerContract);
    }

    [Fact]
    public void PutCreditSpreadBreakevenIsShortStrikeMinusCreditPerShare()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(494.62m, p.Breakevens[0]); // 495 - 0.38
    }

    [Fact]
    public void PutCreditSpreadDirectionalFitIsPositiveOne()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(1, p.DirectionalFit);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerShortVerticalTests`

Expected: compile failure — `ScoreShortVertical` doesn't exist.

- [ ] **Step 3: Implement `ScoreShortVertical`**

In `AI/Open/CandidateScorer.cs`, add inside the class:

```csharp
    public static OpenProposal? ScoreShortVertical(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg)
    {
        var shortLeg = skel.Legs.First(l => l.Action == "sell");
        var longLeg = skel.Legs.First(l => l.Action == "buy");
        var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
        var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
        if (shortParsed == null || longParsed == null) return null;

        var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
        var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
        if (shortQ == null || longQ == null) return null;

        // Credit execution assumes short fills at bid, long fills at ask (conservative).
        var creditPerShare = shortQ.Value.bid - longQ.Value.ask;
        if (creditPerShare <= 0m) return null; // not a credit spread at these quotes

        var creditPerContract = creditPerShare * 100m;
        var width = Math.Abs(shortParsed.Strike - longParsed.Strike);
        var capitalAtRisk = width * 100m - creditPerContract;
        if (capitalAtRisk <= 0m) return null;

        var maxProfit = creditPerContract;
        var maxLoss = -capitalAtRisk;

        var isCall = skel.StructureKind == OpenStructureKind.ShortCallVertical;
        var breakeven = isCall
            ? shortParsed.Strike + creditPerShare   // call credit: loses if S_T > short + credit
            : shortParsed.Strike - creditPerShare;  // put credit: loses if S_T < short − credit

        var years = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days) / 365.0;
        var iv = ResolveIv(shortLeg.Symbol, quotes, cfg.IvDefaultPct);

        // POP = P(S_T inside profitable side of breakeven).
        var pop = LogNormalProbability(isCall ? Direction.Below : Direction.Above, spot, breakeven, years, (double)iv);

        // EV via scenario grid — payoff at expiry is piecewise linear.
        var grid = BuildScenarioGrid(spot, iv, years);
        decimal ev = 0m;
        foreach (var pt in grid)
        {
            var pnl = VerticalPnLAtExpiry(pt.SpotAtExpiry, shortParsed.Strike, longParsed.Strike, creditPerContract, isCall);
            ev += pt.Weight * pnl;
        }

        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        var fit = DirectionalFit.SignFor(skel.StructureKind);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight);
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: skel.Legs,
            Qty: 1,
            DebitOrCreditPerContract: creditPerContract,   // positive = credit received
            MaxProfitPerContract: maxProfit,
            MaxLossPerContract: maxLoss,
            CapitalAtRiskPerContract: capitalAtRisk,
            Breakevens: new[] { breakeven },
            ProbabilityOfProfit: pop,
            ExpectedValuePerContract: ev,
            DaysToTarget: daysToTarget,
            RawScore: rawScore,
            BiasAdjustedScore: biasAdj,
            DirectionalFit: fit,
            Rationale: "",
            Fingerprint: fp
        );
    }

    private static decimal VerticalPnLAtExpiry(decimal sT, decimal shortStrike, decimal longStrike, decimal creditPerContract, bool isCall)
    {
        if (isCall)
        {
            // Call credit: short above long. Profit = credit when S_T ≤ short. Loss ramps to −(width − credit) at S_T ≥ long.
            var shortPayoff = Math.Max(0m, sT - shortStrike) * 100m;
            var longPayoff = Math.Max(0m, sT - longStrike) * 100m;
            return creditPerContract - shortPayoff + longPayoff;
        }
        else
        {
            // Put credit: short below spot, long further below. Profit = credit when S_T ≥ short. Loss ramps down.
            var shortPayoff = Math.Max(0m, shortStrike - sT) * 100m;
            var longPayoff = Math.Max(0m, longStrike - sT) * 100m;
            return creditPerContract - shortPayoff + longPayoff;
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerShortVerticalTests`

Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateScorer.cs WebullAnalytics.Tests/AI/Open/CandidateScorerShortVerticalTests.cs
git commit -m "score short-vertical credit-spread candidates"
```

---

## Task 14: Scoring — long calendars and diagonals

**Files:**
- Modify: `AI/Open/CandidateScorer.cs` (add `ScoreCalendarOrDiagonal`)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateScorerCalendarTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateScorerCalendarTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerCalendarTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m };

    [Fact]
    public void CalendarDebitIsLongAskMinusShortBid()
    {
        var asOf = new DateTime(2026, 4, 20);
        var shortExp = new DateTime(2026, 4, 24); // 4 DTE
        var longExp = new DateTime(2026, 5, 15);   // 25 DTE
        var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
        var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: shortExp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
            [longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        // debit = long_ask − short_bid = 5.10 − 1.50 = 3.60 per share → 360 per contract
        Assert.Equal(-360m, p.DebitOrCreditPerContract);
        Assert.Equal(360m, p.CapitalAtRiskPerContract);
    }

    [Fact]
    public void CalendarDirectionalFitIsZero()
    {
        var asOf = new DateTime(2026, 4, 20);
        var shortExp = new DateTime(2026, 4, 24);
        var longExp = new DateTime(2026, 5, 15);
        var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
        var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: shortExp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
            [longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0.80m, Cfg())!;
        Assert.Equal(0, p.DirectionalFit);
        Assert.Equal(p.RawScore, p.BiasAdjustedScore); // no bias adjustment when fit = 0
    }

    [Fact]
    public void CalendarPopIsProbInProfitBand()
    {
        var asOf = new DateTime(2026, 4, 20);
        var shortExp = new DateTime(2026, 4, 24);
        var longExp = new DateTime(2026, 5, 15);
        var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
        var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: shortExp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
            [longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        // POP = P(|S_T − 500| / 500 < 0.05) — meaningful non-zero value
        Assert.InRange((double)p.ProbabilityOfProfit, 0.15, 0.90);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerCalendarTests`

Expected: compile failure — `ScoreCalendarOrDiagonal` doesn't exist.

- [ ] **Step 3: Implement `ScoreCalendarOrDiagonal`**

In `AI/Open/CandidateScorer.cs`, add inside the class:

```csharp
    public static OpenProposal? ScoreCalendarOrDiagonal(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg)
    {
        var shortLeg = skel.Legs.First(l => l.Action == "sell");
        var longLeg = skel.Legs.First(l => l.Action == "buy");
        var shortParsed = ParsingHelpers.ParseOptionSymbol(shortLeg.Symbol);
        var longParsed = ParsingHelpers.ParseOptionSymbol(longLeg.Symbol);
        if (shortParsed == null || longParsed == null) return null;

        var shortQ = TryLiveBidAsk(shortLeg.Symbol, quotes);
        var longQ = TryLiveBidAsk(longLeg.Symbol, quotes);
        if (shortQ == null || longQ == null) return null;

        var debitPerShare = longQ.Value.ask - shortQ.Value.bid;
        if (debitPerShare <= 0m) return null;

        var debitPerContract = debitPerShare * 100m;
        var capitalAtRisk = debitPerContract;

        var shortYears = Math.Max(1, (shortParsed.ExpiryDate.Date - asOf.Date).Days) / 365.0;
        var longAtShortYears = Math.Max(1, (longParsed.ExpiryDate.Date - shortParsed.ExpiryDate.Date).Days) / 365.0;
        var ivShort = ResolveIv(shortLeg.Symbol, quotes, cfg.IvDefaultPct);
        var ivLong = ResolveIv(longLeg.Symbol, quotes, cfg.IvDefaultPct);

        // POP = P(|S_T − K_short| / spot < profitBandPct / 100)
        var band = spot * cfg.ProfitBandPct / 100m;
        var popUpper = LogNormalProbability(Direction.Below, spot, shortParsed.Strike + band, shortYears, (double)ivShort);
        var popLower = LogNormalProbability(Direction.Below, spot, shortParsed.Strike - band, shortYears, (double)ivShort);
        var pop = popUpper - popLower;
        if (pop < 0m) pop = 0m;

        var grid = BuildScenarioGrid(spot, ivShort, shortYears);
        decimal ev = 0m;
        decimal maxProfit = decimal.MinValue;
        decimal maxLossPoint = decimal.MaxValue;
        foreach (var pt in grid)
        {
            // Long leg valued via BS at short expiry with IV_long; short leg is intrinsic.
            var longBS = OptionMath.BlackScholes(pt.SpotAtExpiry, longParsed.Strike, longAtShortYears, OptionMath.RiskFreeRate, ivLong, longParsed.CallPut);
            var shortIntrinsic = longParsed.CallPut == "C"
                ? Math.Max(0m, pt.SpotAtExpiry - shortParsed.Strike)
                : Math.Max(0m, shortParsed.Strike - pt.SpotAtExpiry);
            var positionValue = (longBS - shortIntrinsic) * 100m;
            var pnl = positionValue - debitPerContract;
            ev += pt.Weight * pnl;
            if (pnl > maxProfit) maxProfit = pnl;
            if (pnl < maxLossPoint) maxLossPoint = pnl;
        }
        if (maxLossPoint > -debitPerContract) maxLossPoint = -debitPerContract;

        var daysToTarget = Math.Max(1, (skel.TargetExpiry.Date - asOf.Date).Days);
        var rawScore = ComputeRawScore(ev, daysToTarget, capitalAtRisk);
        var fit = DirectionalFit.SignFor(skel.StructureKind);
        var biasAdj = BiasAdjust(rawScore, bias, fit, cfg.DirectionalFitWeight);
        var fp = ComputeFingerprint(skel.Ticker, skel.StructureKind, skel.Legs, qty: 1);

        return new OpenProposal(
            Ticker: skel.Ticker,
            StructureKind: skel.StructureKind,
            Legs: skel.Legs,
            Qty: 1,
            DebitOrCreditPerContract: -debitPerContract,
            MaxProfitPerContract: maxProfit,
            MaxLossPerContract: maxLossPoint,
            CapitalAtRiskPerContract: capitalAtRisk,
            Breakevens: Array.Empty<decimal>(),   // calendar breakevens require numeric root-finding; omitted in phase 1
            ProbabilityOfProfit: pop,
            ExpectedValuePerContract: ev,
            DaysToTarget: daysToTarget,
            RawScore: rawScore,
            BiasAdjustedScore: biasAdj,
            DirectionalFit: fit,
            Rationale: "",
            Fingerprint: fp
        );
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerCalendarTests`

Expected: all 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateScorer.cs WebullAnalytics.Tests/AI/Open/CandidateScorerCalendarTests.cs
git commit -m "score long-calendar and long-diagonal candidates"
```

---

## Task 15: Scorer dispatch + rationale string

**Files:**
- Modify: `AI/Open/CandidateScorer.cs` (add `Score` dispatcher and `BuildRationale`)
- Create: `WebullAnalytics.Tests/AI/Open/CandidateScorerDispatchTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/CandidateScorerDispatchTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerDispatchTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m };

    [Fact]
    public void ScoreDispatchesLongCall()
    {
        var exp = new DateTime(2026, 5, 15);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(4.90m, 5.00m, 0.40m)
        };
        var p = CandidateScorer.Score(skel, spot: 500m, asOf: new DateTime(2026, 4, 1), quotes, bias: 0m, Cfg());
        Assert.NotNull(p);
        Assert.Equal(OpenStructureKind.LongCall, p!.StructureKind);
    }

    [Fact]
    public void RationaleMentionsStructureAndScore()
    {
        var exp = new DateTime(2026, 5, 15);
        var sym = MatchKeys.OccSymbol("SPY", exp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCall, new[] { new ProposalLeg("buy", sym, 1) }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [sym] = TestQuote.Q(4.90m, 5.00m, 0.40m)
        };
        var p = CandidateScorer.Score(skel, spot: 500m, asOf: new DateTime(2026, 4, 1), quotes, bias: 0.40m, Cfg());
        var rationale = CandidateScorer.BuildRationale(p!, bias: 0.40m, cfg: Cfg());
        Assert.Contains("LongCall", rationale);
        Assert.Contains("POP", rationale);
        Assert.Contains("+20", rationale); // 0.5 × 0.4 × 1 = 0.20 → +20% boost
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerDispatchTests`

Expected: compile failure — `Score` and `BuildRationale` don't exist.

- [ ] **Step 3: Add dispatch and rationale**

In `AI/Open/CandidateScorer.cs`, add inside the class:

```csharp
    public static OpenProposal? Score(CandidateSkeleton skel, decimal spot, DateTime asOf, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal bias, OpenerConfig cfg) => skel.StructureKind switch
    {
        OpenStructureKind.LongCall or OpenStructureKind.LongPut => ScoreLongCallPut(skel, spot, asOf, quotes, bias, cfg),
        OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => ScoreShortVertical(skel, spot, asOf, quotes, bias, cfg),
        OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal => ScoreCalendarOrDiagonal(skel, spot, asOf, quotes, bias, cfg),
        _ => null
    };

    public static string BuildRationale(OpenProposal p, decimal bias, OpenerConfig cfg)
    {
        var cashSide = p.DebitOrCreditPerContract >= 0m
            ? $"credit ${p.DebitOrCreditPerContract:F2}"
            : $"debit ${-p.DebitOrCreditPerContract:F2}";

        var biasEffectPct = cfg.DirectionalFitWeight * bias * p.DirectionalFit * 100m;
        var biasTag = p.DirectionalFit == 0
            ? $"[tech {bias:+0.00;-0.00}, fit 0 → no adjustment]"
            : $"[tech {bias:+0.00;-0.00}, fit {p.DirectionalFit:+0;-0} → {biasEffectPct:+0;-0}% {(biasEffectPct >= 0 ? "boost" : "cut")}]";

        var beStr = p.Breakevens.Count > 0 ? $"BE ${string.Join("/", p.Breakevens.Select(b => b.ToString("F2")))}, " : "";

        return $"{p.StructureKind} — {cashSide}, maxProfit ${p.MaxProfitPerContract:F2}, maxLoss ${-p.MaxLossPerContract:F2}, {beStr}POP {p.ProbabilityOfProfit * 100m:F0}%, EV ${p.ExpectedValuePerContract:F2}, score {p.BiasAdjustedScore:F4} {biasTag}";
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter CandidateScorerDispatchTests`

Expected: both tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/CandidateScorer.cs WebullAnalytics.Tests/AI/Open/CandidateScorerDispatchTests.cs
git commit -m "add scorer dispatch and rationale string builder"
```

---

## Task 16: OpenCandidateEvaluator — orchestrator, cash sizing, ranking

**Files:**
- Create: `AI/Open/OpenCandidateEvaluator.cs`
- Create: `WebullAnalytics.Tests/AI/Open/OpenCandidateEvaluatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/OpenCandidateEvaluatorTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenCandidateEvaluatorTests
{
    private sealed class StaticQuoteSource : IQuoteSource
    {
        private readonly QuoteSnapshot _snapshot;
        public StaticQuoteSource(QuoteSnapshot s) { _snapshot = s; }
        public Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> symbols, IReadOnlySet<string> tickers, CancellationToken ct)
            => Task.FromResult(_snapshot);
    }

    private static AIConfig BuildConfig(OpenerConfig opener) => new()
    {
        Tickers = new() { "SPY" },
        Opener = opener,
        CashReserve = new CashReserveConfig { Mode = "absolute", Value = 0m }
    };

    private static EvaluationContext BuildContext(decimal cash, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> quotes) => new(
        Now: new DateTime(2026, 4, 20, 10, 0, 0),
        OpenPositions: new Dictionary<string, OpenPosition>(),
        UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = spot },
        Quotes: quotes,
        AccountCash: cash,
        AccountValue: cash,
        TechnicalSignals: new Dictionary<string, TechnicalBias>()
    );

    [Fact]
    public async Task NoQuotesReturnsNoProposals()
    {
        var cfg = BuildConfig(new OpenerConfig());
        var ctx = BuildContext(cash: 10000m, spot: 500m, quotes: new Dictionary<string, OptionContractQuote>());
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal>()));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.Empty(proposals);
    }

    [Fact]
    public async Task TopNPerTickerIsEnforced()
    {
        var cfg = BuildConfig(new OpenerConfig { TopNPerTicker = 2 });
        // Build a rich quote set so many candidates score non-zero. Use a minimal fake that returns the same quote for any requested symbol with nonzero bid/ask/IV.
        var quotes = new FakeUniversalQuotes(0.40m);
        var ctx = BuildContext(cash: 100000m, spot: 500m, quotes: quotes);
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPY"] = 500m }));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.True(proposals.Count <= 2, $"expected ≤ 2 proposals, got {proposals.Count}");
    }

    [Fact]
    public async Task ZeroCashBlocksSizing()
    {
        var cfg = BuildConfig(new OpenerConfig { TopNPerTicker = 5 });
        var quotes = new FakeUniversalQuotes(0.40m);
        var ctx = BuildContext(cash: 0m, spot: 500m, quotes: quotes);
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPY"] = 500m }));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.All(proposals, p => Assert.True(p.CashReserveBlocked));
        Assert.All(proposals, p => Assert.Equal(0, p.Qty));
    }

    [Fact]
    public async Task MaxQtyIsClamped()
    {
        var cfg = BuildConfig(new OpenerConfig { TopNPerTicker = 5, MaxQtyPerProposal = 3 });
        var quotes = new FakeUniversalQuotes(0.40m);
        var ctx = BuildContext(cash: 1_000_000m, spot: 500m, quotes: quotes);
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPY"] = 500m }));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.True(p.Qty <= 3));
    }

    /// <summary>Fake quote dictionary: returns a constant 1.00/1.10 quote with the given IV for every requested symbol.</summary>
    private sealed class FakeUniversalQuotes : IReadOnlyDictionary<string, OptionContractQuote>
    {
        private readonly decimal _iv;
        public FakeUniversalQuotes(decimal iv) { _iv = iv; }
        public OptionContractQuote this[string key] => TestQuote.Q(1.00m, 1.10m, _iv);
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<OptionContractQuote> Values => Array.Empty<OptionContractQuote>();
        public int Count => 0;
        public bool ContainsKey(string key) => true;
        public IEnumerator<KeyValuePair<string, OptionContractQuote>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, OptionContractQuote>>().GetEnumerator();
        public bool TryGetValue(string key, out OptionContractQuote value) { value = this[key]; return true; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenCandidateEvaluatorTests`

Expected: compile failure — `OpenCandidateEvaluator` doesn't exist.

- [ ] **Step 3: Implement the evaluator**

Create `AI/Open/OpenCandidateEvaluator.cs`:

```csharp
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>
/// Orchestrates the opener pipeline: enumerate skeletons → phase-3 quote fetch → score → rank per ticker.
/// Produces a flat, ranked list of OpenProposal across all configured tickers, capped at topNPerTicker each.
/// </summary>
internal sealed class OpenCandidateEvaluator
{
    private readonly AIConfig _config;
    private readonly IQuoteSource _quotes;

    public OpenCandidateEvaluator(AIConfig config, IQuoteSource quotes)
    {
        _config = config;
        _quotes = quotes;
    }

    public async Task<IReadOnlyList<OpenProposal>> EvaluateAsync(EvaluationContext ctx, CancellationToken cancellation)
    {
        var cfg = _config.Opener;
        if (!cfg.Enabled) return Array.Empty<OpenProposal>();

        var tickerSet = new HashSet<string>(_config.Tickers, StringComparer.OrdinalIgnoreCase);
        var output = new List<OpenProposal>();

        // Phase A: enumerate across all tickers.
        var allSkeletons = new List<CandidateSkeleton>();
        foreach (var ticker in _config.Tickers)
        {
            if (!ctx.UnderlyingPrices.TryGetValue(ticker, out var spot) || spot <= 0m) continue;
            allSkeletons.AddRange(CandidateEnumerator.Enumerate(ticker, spot, ctx.Now, cfg));
        }
        if (allSkeletons.Count == 0) return Array.Empty<OpenProposal>();

        // Phase B (phase-3 quote fetch): pull any symbols not already in ctx.Quotes.
        var neededSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skel in allSkeletons)
            foreach (var leg in skel.Legs)
                if (!ctx.Quotes.ContainsKey(leg.Symbol)) neededSymbols.Add(leg.Symbol);

        var mergedQuotes = new Dictionary<string, OptionContractQuote>(ctx.Quotes, StringComparer.OrdinalIgnoreCase);
        if (neededSymbols.Count > 0)
        {
            var extra = await _quotes.GetQuotesAsync(ctx.Now, neededSymbols, tickerSet, cancellation);
            foreach (var (k, v) in extra.Options) mergedQuotes[k] = v;
        }

        // Phase C: score per ticker.
        var reserve = CashReserveHelper.ComputeReserve(_config.CashReserve.Mode, _config.CashReserve.Value, ctx.AccountValue);
        var freeCash = Math.Max(0m, ctx.AccountCash - reserve);

        foreach (var tickerGroup in allSkeletons.GroupBy(s => s.Ticker))
        {
            if (!ctx.UnderlyingPrices.TryGetValue(tickerGroup.Key, out var spot) || spot <= 0m) continue;
            ctx.TechnicalSignals.TryGetValue(tickerGroup.Key, out var biasSignal);
            var bias = biasSignal?.Score ?? 0m;

            var scoredByStructure = new Dictionary<OpenStructureKind, List<OpenProposal>>();

            foreach (var skel in tickerGroup)
            {
                var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, bias, cfg);
                if (p == null) continue;
                if (!scoredByStructure.TryGetValue(p.StructureKind, out var list))
                    scoredByStructure[p.StructureKind] = list = new List<OpenProposal>();
                list.Add(p);
            }

            // Per-structure top-N truncation.
            var survivors = new List<OpenProposal>();
            foreach (var list in scoredByStructure.Values)
            {
                survivors.AddRange(list
                    .OrderByDescending(p => p.BiasAdjustedScore)
                    .Take(cfg.MaxCandidatesPerStructurePerTicker));
            }

            // Apply cash sizing.
            for (int i = 0; i < survivors.Count; i++)
                survivors[i] = ApplyCashSizing(survivors[i], freeCash, cfg, bias);

            // Per-ticker top-N.
            var topForTicker = survivors.OrderByDescending(p => p.BiasAdjustedScore).Take(cfg.TopNPerTicker);
            output.AddRange(topForTicker);
        }

        return output;
    }

    private static OpenProposal ApplyCashSizing(OpenProposal p, decimal freeCash, OpenerConfig cfg, decimal bias)
    {
        if (p.CapitalAtRiskPerContract <= 0m)
            return p with { Rationale = CandidateScorer.BuildRationale(p, bias, cfg) };

        var maxQty = (int)Math.Floor(freeCash / p.CapitalAtRiskPerContract);
        OpenProposal updated;
        if (maxQty >= 1)
        {
            var qty = Math.Min(maxQty, cfg.MaxQtyPerProposal);
            updated = p with { Qty = qty };
        }
        else
        {
            updated = p with
            {
                Qty = 0,
                CashReserveBlocked = true,
                CashReserveDetail = $"free ${freeCash:F0}, requires ${p.CapitalAtRiskPerContract:F0} per contract"
            };
        }
        return updated with
        {
            Rationale = CandidateScorer.BuildRationale(updated, bias, cfg),
            Fingerprint = CandidateScorer.ComputeFingerprint(updated.Ticker, updated.StructureKind, updated.Legs, updated.Qty)
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenCandidateEvaluatorTests`

Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/OpenCandidateEvaluator.cs WebullAnalytics.Tests/AI/Open/OpenCandidateEvaluatorTests.cs
git commit -m "add OpenCandidateEvaluator orchestrator"
```

---

## Task 17: OpenProposalSink — JSONL + console + dedup

**Files:**
- Create: `AI/Open/OpenProposalSink.cs`
- Create: `WebullAnalytics.Tests/AI/Open/OpenProposalSinkTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `WebullAnalytics.Tests/AI/Open/OpenProposalSinkTests.cs`:

```csharp
using Xunit;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenProposalSinkTests
{
    private static OpenProposal MakeProposal(decimal score, string fingerprint) => new OpenProposal(
        Ticker: "SPY",
        StructureKind: OpenStructureKind.LongCall,
        Legs: new[] { new ProposalLeg("buy", "SPY   260515C00500000", 1) },
        Qty: 1,
        DebitOrCreditPerContract: -500m,
        MaxProfitPerContract: 1000m,
        MaxLossPerContract: -500m,
        CapitalAtRiskPerContract: 500m,
        Breakevens: new[] { 505m },
        ProbabilityOfProfit: 0.45m,
        ExpectedValuePerContract: 25m,
        DaysToTarget: 30,
        RawScore: score,
        BiasAdjustedScore: score,
        DirectionalFit: 1,
        Rationale: "test rationale",
        Fingerprint: fingerprint
    );

    [Fact]
    public void WriteJsonlAppendsLine()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "quiet" }, mode: "once");
            sink.Emit(MakeProposal(0.01m, "fp1"));
            sink.Flush();
            var contents = File.ReadAllLines(tmp);
            Assert.Single(contents);
            Assert.Contains("\"type\":\"open\"", contents[0]);
            Assert.Contains("\"ticker\":\"SPY\"", contents[0]);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void RepeatSameFingerprintStillAppendsJsonl()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "quiet" }, mode: "once");
            sink.Emit(MakeProposal(0.01m, "fp1"));
            sink.Emit(MakeProposal(0.01m, "fp1"));
            sink.Flush();
            var contents = File.ReadAllLines(tmp);
            Assert.Equal(2, contents.Length);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void IsRepeatReturnsTrueForUnchangedScore()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "quiet" }, mode: "once");
            Assert.False(sink.IsRepeat(MakeProposal(0.01m, "fp1")));
            sink.Emit(MakeProposal(0.01m, "fp1"));
            Assert.True(sink.IsRepeat(MakeProposal(0.01m, "fp1")));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void IsRepeatReturnsFalseWhenScoreMovesByTenPercent()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using var sink = new OpenProposalSink(new LogConfig { Path = tmp, ConsoleVerbosity = "quiet" }, mode: "once");
            sink.Emit(MakeProposal(0.01m, "fp1"));
            Assert.False(sink.IsRepeat(MakeProposal(0.0111m, "fp1"))); // +11%
            Assert.True(sink.IsRepeat(MakeProposal(0.0105m, "fp1")));  // +5% — still repeat
        }
        finally { File.Delete(tmp); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenProposalSinkTests`

Expected: compile failure — `OpenProposalSink` doesn't exist.

- [ ] **Step 3: Implement the sink**

Create `AI/Open/OpenProposalSink.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace WebullAnalytics.AI.Output;

/// <summary>
/// Writes open proposals to JSONL and console. Keeps per-fingerprint score history for console dedup.
/// JSONL always gets the entry; console suppresses repeats unless the bias-adjusted score has moved
/// by ≥ 10% since last emission.
/// </summary>
internal sealed class OpenProposalSink : IDisposable
{
    private readonly StreamWriter _file;
    private readonly LogConfig _log;
    private readonly string _mode;
    private readonly Dictionary<string, decimal> _lastScoreByFingerprint = new();

    public OpenProposalSink(LogConfig log, string mode)
    {
        _log = log;
        _mode = mode;
        var path = Program.ResolvePath(log.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public bool IsRepeat(OpenProposal p)
    {
        if (!_lastScoreByFingerprint.TryGetValue(p.Fingerprint, out var last)) return false;
        var abs = Math.Abs(p.BiasAdjustedScore - last);
        var threshold = Math.Abs(last) * 0.10m;
        return abs < threshold;
    }

    public void Emit(OpenProposal p)
    {
        var repeat = IsRepeat(p);
        WriteJsonl(p);
        if (!repeat || _log.ConsoleVerbosity == "debug") WriteConsole(p);
        _lastScoreByFingerprint[p.Fingerprint] = p.BiasAdjustedScore;
    }

    public void Flush() => _file.Flush();

    private void WriteJsonl(OpenProposal p)
    {
        var record = new
        {
            type = "open",
            ts = DateTime.Now.ToString("o"),
            mode = _mode,
            ticker = p.Ticker,
            structure = p.StructureKind.ToString(),
            legs = p.Legs.Select(l => new { action = l.Action, symbol = l.Symbol, qty = l.Qty }),
            qty = p.Qty,
            cashImpactPerContract = p.DebitOrCreditPerContract,
            maxProfit = p.MaxProfitPerContract,
            maxLoss = p.MaxLossPerContract,
            capitalAtRisk = p.CapitalAtRiskPerContract,
            pop = p.ProbabilityOfProfit,
            ev = p.ExpectedValuePerContract,
            daysToTarget = p.DaysToTarget,
            rawScore = p.RawScore,
            biasAdjustedScore = p.BiasAdjustedScore,
            directionalFit = p.DirectionalFit,
            breakevens = p.Breakevens,
            rationale = p.Rationale,
            fingerprint = p.Fingerprint,
            cashReserveBlocked = p.CashReserveBlocked,
            cashReserveDetail = p.CashReserveDetail
        };
        _file.WriteLine(JsonSerializer.Serialize(record));
    }

    private void WriteConsole(OpenProposal p)
    {
        var color = p.StructureKind switch
        {
            OpenStructureKind.ShortPutVertical or OpenStructureKind.ShortCallVertical => "green",
            OpenStructureKind.LongCall or OpenStructureKind.LongPut => "cyan",
            OpenStructureKind.LongCalendar or OpenStructureKind.LongDiagonal => "magenta",
            _ => "white"
        };
        var blocked = p.CashReserveBlocked ? " [yellow]⚠ blocked[/]" : "";
        AnsiConsole.MarkupLine($"[bold {color}]{p.StructureKind}[/] [grey]{p.Ticker}[/] x{p.Qty}{blocked}");
        var legsText = string.Join(", ", p.Legs.Select(l => $"{l.Action.ToUpperInvariant()} {l.Symbol} x{l.Qty}"));
        AnsiConsole.MarkupLine($"  {Markup.Escape(legsText)}");
        AnsiConsole.MarkupLine($"  [italic]{Markup.Escape(p.Rationale)}[/]");
        if (p.CashReserveBlocked && p.CashReserveDetail != null)
            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(p.CashReserveDetail)}[/]");
        AnsiConsole.WriteLine();
    }

    public void Dispose() => _file.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --filter OpenProposalSinkTests`

Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add AI/Open/OpenProposalSink.cs WebullAnalytics.Tests/AI/Open/OpenProposalSinkTests.cs
git commit -m "add OpenProposalSink with JSONL log and console dedup"
```

---

## Task 18: Add type discriminator to management JSONL + `--no-open-proposals` flag

**Files:**
- Modify: `AI/Output/ProposalSink.cs:34-54`
- Modify: `AI/AICommand.cs:10-45`

- [ ] **Step 1: Add `"type": "management"` to ProposalSink JSONL**

In `AI/Output/ProposalSink.cs`, modify the `WriteJsonl` method's anonymous object to add a `type` field at the top:

```csharp
    private void WriteJsonl(ManagementProposal p)
    {
        var record = new
        {
            type = "management",
            ts = DateTime.Now.ToString("o"),
            rule = p.Rule,
            ticker = p.Ticker,
            positionKey = p.PositionKey,
            proposal = new
            {
                type = p.Kind.ToString().ToLowerInvariant(),
                legs = p.Legs.Select(l => new { action = l.Action, symbol = l.Symbol, qty = l.Qty }),
                netDebit = p.NetDebit
            },
            rationale = p.Rationale,
            cashReserveBlocked = p.CashReserveBlocked,
            cashReserveDetail = p.CashReserveDetail,
            mode = _mode
        };
        _file.WriteLine(JsonSerializer.Serialize(record));
    }
```

- [ ] **Step 2: Add `--no-open-proposals` to base settings**

In `AI/AICommand.cs`, inside `AISubcommandSettings`, add a new option above `Validate`:

```csharp
    [CommandOption("--no-open-proposals")]
    [Description("Disable the opening-proposal pass for this run; management rules still run.")]
    public bool NoOpenProposals { get; set; }
```

- [ ] **Step 3: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add AI/Output/ProposalSink.cs AI/AICommand.cs
git commit -m "add type discriminator to management JSONL and --no-open-proposals flag"
```

---

## Task 19: Wire opener into `ai once`

**Files:**
- Modify: `AI/AICommand.cs:92-125`

- [ ] **Step 1: Update `AIOnceCommand.ExecuteAsync`**

In `AI/AICommand.cs`, replace the body of `AIOnceCommand.ExecuteAsync` with:

```csharp
    public override async Task<int> ExecuteAsync(CommandContext context, AIOnceSettings settings, CancellationToken cancellation)
    {
        var config = AIContext.ResolveConfig(settings);
        if (config == null) return 1;

        var positions = AIContext.BuildLivePositionSource(config);
        var quotes = AIContext.BuildLiveQuoteSource(config);

        var tickerSet = new HashSet<string>(config.Tickers, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;

        var priceCache = new Replay.HistoricalPriceCache();

        var openPositions = await positions.GetOpenPositionsAsync(now, tickerSet, cancellation);
        var (cash, accountValue) = await positions.GetAccountStateAsync(now, cancellation);

        var quoteSnapshot = await AIPipelineHelper.FetchQuotesWithHypotheticals(openPositions, tickerSet, now, quotes, config, cancellation);

        var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
            tickerSet, priceCache, config.Rules.OpportunisticRoll.TechnicalFilter, now, cancellation);

        var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
        var evaluator = new RuleEvaluator(RuleEvaluator.BuildRules(config), config);

        using var sink = new ProposalSink(config.Log, mode: "once");
        var results = evaluator.Evaluate(ctx);
        foreach (var r in results) sink.Emit(r.Proposal, r.IsRepeat);

        var openCount = 0;
        if (config.Opener.Enabled && !settings.NoOpenProposals)
        {
            using var openSink = new OpenProposalSink(config.Log, mode: "once");
            var openEvaluator = new OpenCandidateEvaluator(config, quotes);
            var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
            foreach (var p in openResults) openSink.Emit(p);
            openCount = openResults.Count;
        }

        AnsiConsole.MarkupLine($"[dim]Tick complete: {openPositions.Count} position(s), {results.Count} mgmt proposal(s), {openCount} open proposal(s) emitted[/]");
        return 0;
    }
```

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`

Expected: build succeeds.

- [ ] **Step 3: Smoke test**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics -- ai once --config ai-config.example.json --tickers SPY --ignore-market-hours 2>&1 | tail -20`

Expected: either a list of open proposals with numeric scores, or an explanation of why none fired (no live quotes available in the example environment). The process exits cleanly with no unhandled exceptions.

- [ ] **Step 4: Commit**

```bash
git add AI/AICommand.cs
git commit -m "wire opener into ai once"
```

---

## Task 20: Wire opener into `ai watch`

**Files:**
- Modify: `AI/WatchLoop.cs:55-125`

- [ ] **Step 1: Update `AIWatchCommand.ExecuteAsync`**

In `AI/WatchLoop.cs`, modify `AIWatchCommand.ExecuteAsync`:

- Add, after `using var sink = new ProposalSink(...)`:

```csharp
        OpenProposalSink? openSink = null;
        OpenCandidateEvaluator? openEvaluator = null;
        if (config.Opener.Enabled && !settings.NoOpenProposals)
        {
            openSink = new OpenProposalSink(config.Log, mode: "watch");
            openEvaluator = new OpenCandidateEvaluator(config, quotes);
        }
```

- In the `try` block of the tick, after the existing `foreach (var r in results) { ... }` line, add:

```csharp
                    if (openEvaluator != null && openSink != null)
                    {
                        var openResults = await openEvaluator.EvaluateAsync(ctx, cancellation);
                        foreach (var p in openResults) { openSink.Emit(p); proposalsEmitted++; }
                    }
```

- Before the final `return 0;`, add:

```csharp
            openSink?.Dispose();
```

- Change `using var sink = new ProposalSink(...)` to `using var sink = new ProposalSink(...);` (unchanged if already a `using` statement) — make sure `openSink` is disposed alongside `sink`. Because `openSink` is created conditionally and isn't a `using`, the explicit dispose before return handles it. If an exception escapes the loop, it will be disposed by finalizer; this is acceptable for phase 1.

- [ ] **Step 2: Build to verify**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build`

Expected: build succeeds.

- [ ] **Step 3: Smoke test**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics -- ai watch --config ai-config.example.json --tickers SPY --ignore-market-hours --duration 10s --tick 5 2>&1 | tail -20`

Expected: two tick cycles; final line shows `Loop exited. ticks=2 proposals=<n> failures=0` with no unhandled exceptions.

- [ ] **Step 4: Commit**

```bash
git add AI/WatchLoop.cs
git commit -m "wire opener into ai watch"
```

---

## Task 21: Final verification and full test suite run

**Files:**
- None (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test`

Expected: all tests pass (aim ≈ 40+ tests across the new files plus the existing smoke test).

- [ ] **Step 2: Verify git log is clean and commits are bite-sized**

Run: `git log --oneline master..HEAD`

Expected: ~20 focused commit messages, one per task, readable history.

- [ ] **Step 3: Verify the JSONL log gets both streams**

Run a short watch cycle and inspect:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project WebullAnalytics -- ai watch --config ai-config.example.json --tickers SPY --ignore-market-hours --duration 10s --tick 5
grep -c '"type":"open"' data/ai-proposals.log
grep -c '"type":"management"' data/ai-proposals.log
```

Expected: `"type":"open"` appears when open proposals fire; `"type":"management"` appears when management rules fire.

- [ ] **Step 4: Done**

Report completion to the user, pointing at the spec, plan, and commit range.

---

## Self-Review

**Spec coverage:**
- Architecture & components (6 files under `AI/Open/`): Tasks 2, 4, 5, 6, 7–9, 10–15, 16, 17 — all present.
- `OpenProposal` record with exact fields listed: Task 4.
- Directional-fit table: Task 4 (tests) + Task 10 (score arithmetic).
- Candidate enumeration for all three structure families, all config-bounded: Tasks 7, 8, 9.
- Scoring with IV resolution, cash-impact tables, POP formulas, 5-point grid EV, raw + bias-adjusted score: Tasks 10–15.
- Cash sizing with `maxQtyPerProposal` cap and `CashReserveBlocked` tag: Task 16.
- Rank + truncate (per-structure then per-ticker): Task 16.
- Dedup across ticks with 10% score-movement override: Task 17.
- Config schema with bounds validation: Tasks 2, 3.
- JSONL + console output with `type` discriminator on both streams: Tasks 17, 18.
- `--no-open-proposals` CLI override + `opener.enabled` config override: Tasks 18, 19, 20.
- Pipeline hook in `ai once` and `ai watch`: Tasks 19, 20.

**Placeholder scan:** No TODO/TBD/"implement later" strings. All code blocks are complete.

**Type consistency:**
- `OpenStructureKind` enum values consistent across all uses: `LongCall`, `LongPut`, `ShortPutVertical`, `ShortCallVertical`, `LongCalendar`, `LongDiagonal`.
- `OpenProposal` constructor parameter order matches record declaration throughout.
- `CandidateScorer` method signatures (`ScoreLongCallPut`, `ScoreShortVertical`, `ScoreCalendarOrDiagonal`, `Score`, `BuildRationale`, `BuildScenarioGrid`, `ComputeRawScore`, `BiasAdjust`, `ResolveIv`, `TryLiveBidAsk`, `ComputeFingerprint`, `LogNormalProbability`) consistent between definition and call sites.
- `OpenerConfig` field names match JSON property names and C# property names throughout.
- `IQuoteSource.GetQuotesAsync` signature matches existing interface (verified by reading `AI/Sources/IQuoteSource.cs`).
