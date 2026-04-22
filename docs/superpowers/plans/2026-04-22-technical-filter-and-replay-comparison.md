# Technical Filter + Replay Comparison Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a composite technical bias signal (SMA/RSI/momentum) that hard-blocks `OpportunisticRollRule` when market direction conflicts with the position, and improve the replay agreement classifier to four-way match/divergent/partial/miss with inline fill display.

**Architecture:** `TechnicalBias` + `TechnicalIndicators` compute a weighted composite score per ticker from daily closes; both live and replay pipelines inject a `TechnicalSignals` dict into `EvaluationContext`; `OpportunisticRollRule` reads it and returns null when adverse. `ClassifyAgreement` in `ReplayRunner` matches proposed OCC symbols against actual fills; the result is rendered inline after each proposal.

**Tech Stack:** C# 12, Spectre.Console (console output only), existing `HistoricalPriceCache` (Yahoo daily closes, disk-cached)

---

## File structure

| File | Change |
|------|--------|
| `AI/TechnicalBias.cs` | **Create** — record + `IsAdverse` |
| `AI/TechnicalIndicators.cs` | **Create** — pure-math SMA/RSI/momentum + `Compute` |
| `AI/AIConfig.cs` | **Modify** — add `TechnicalFilterConfig` class; add `TechnicalFilter` property to `OpportunisticRollConfig`; add validation |
| `AI/EvaluationContext.cs` | **Modify** — add `TechnicalSignals` parameter to record |
| `AI/Replay/HistoricalPriceCache.cs` | **Modify** — add `GetRecentClosesAsync(ticker, count, asOf, ct)` |
| `AI/AIPipelineHelper.cs` | **Modify** — add `ComputeTechnicalSignalsAsync` shared helper |
| `AI/AICommand.cs` | **Modify** — create `HistoricalPriceCache`, compute signals, pass to context |
| `AI/WatchLoop.cs` | **Modify** — create `HistoricalPriceCache` before loop, compute signals each tick |
| `AI/Replay/ReplayRunner.cs` | **Modify** — accept `HistoricalPriceCache` in constructor; compute signals per step; improved `ClassifyAgreement`; inline fill rendering |
| `ai-config.example.json` | **Modify** — add `technicalFilter` block under `opportunisticRoll` |

---

## Task 1: `TechnicalBias` record

**Files:**
- Create: `AI/TechnicalBias.cs`

- [ ] **Step 1: Create `AI/TechnicalBias.cs`.**

```csharp
namespace WebullAnalytics.AI;

internal sealed record TechnicalBias(
	decimal Score,
	decimal SmaScore,
	decimal RsiScore,
	decimal MomentumScore)
{
	/// <summary>Returns true when this signal conflicts with the position's directional risk.
	/// Calls are adverse when bullish (price likely to breach short call). Puts are adverse when bearish.</summary>
	public bool IsAdverse(string callPut, decimal bullishBlockThreshold, decimal bearishBlockThreshold) =>
		callPut == "C" ? Score >= bullishBlockThreshold : Score <= bearishBlockThreshold;
}
```

- [ ] **Step 2: Build to verify compilation.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/TechnicalBias.cs
git commit -m "Add TechnicalBias record"
```

---

## Task 2: `TechnicalIndicators` — pure math

**Files:**
- Create: `AI/TechnicalIndicators.cs`

- [ ] **Step 1: Create `AI/TechnicalIndicators.cs`.**

`closes` is always ordered oldest-first. All methods return null when there is insufficient data.

```csharp
namespace WebullAnalytics.AI;

internal static class TechnicalIndicators
{
	/// <summary>(SMA5 / SMA20 − 1) clamped to [−1, +1]. Requires ≥ 20 closes.</summary>
	public static decimal? ComputeSmaScore(IReadOnlyList<decimal> closes)
	{
		if (closes.Count < 20) return null;
		var sma5 = closes.Skip(closes.Count - 5).Average();
		var sma20 = closes.Skip(closes.Count - 20).Average();
		if (sma20 == 0m) return null;
		return Math.Clamp(sma5 / sma20 - 1m, -1m, 1m);
	}

	/// <summary>Wilder RSI(14) normalized: (RSI − 50) / 50. Requires ≥ 15 closes (14 changes).</summary>
	public static decimal? ComputeRsiScore(IReadOnlyList<decimal> closes)
	{
		if (closes.Count < 15) return null;

		var changes = new List<decimal>(closes.Count - 1);
		for (int i = 0; i < closes.Count - 1; i++)
			changes.Add(closes[i + 1] - closes[i]);

		// Seed: simple average of first 14 gains/losses.
		var seedGain = changes.Take(14).Where(c => c > 0).DefaultIfEmpty(0m).Average();
		var seedLoss = changes.Take(14).Where(c => c < 0).Select(c => -c).DefaultIfEmpty(0m).Average();

		var avgGain = seedGain;
		var avgLoss = seedLoss;

		// Wilder smoothing on remaining changes.
		for (int i = 14; i < changes.Count; i++)
		{
			avgGain = (avgGain * 13m + Math.Max(0m, changes[i])) / 14m;
			avgLoss = (avgLoss * 13m + Math.Max(0m, -changes[i])) / 14m;
		}

		if (avgLoss == 0m) return 1m; // all gains, maximally bullish
		var rs = avgGain / avgLoss;
		var rsi = 100m - (100m / (1m + rs));
		return (rsi - 50m) / 50m;
	}

	/// <summary>N-day % return clamped to [−1, +1]. Requires ≥ days + 1 closes.</summary>
	public static decimal? ComputeMomentumScore(IReadOnlyList<decimal> closes, int days)
	{
		if (closes.Count < days + 1) return null;
		var current = closes[closes.Count - 1];
		var prior = closes[closes.Count - 1 - days];
		if (prior == 0m) return null;
		return Math.Clamp(current / prior - 1m, -1m, 1m);
	}

	/// <summary>Weighted composite of all three indicators. Returns null when no indicator has enough data.</summary>
	public static TechnicalBias? Compute(IReadOnlyList<decimal> closes, TechnicalFilterConfig config)
	{
		var smaScore = ComputeSmaScore(closes);
		var rsiScore = ComputeRsiScore(closes);
		var momentumScore = ComputeMomentumScore(closes, config.MomentumDays);

		var weightedSum = 0m;
		var totalWeight = 0m;

		if (smaScore.HasValue && config.SmaWeight > 0m) { weightedSum += smaScore.Value * config.SmaWeight; totalWeight += config.SmaWeight; }
		if (rsiScore.HasValue && config.RsiWeight > 0m) { weightedSum += rsiScore.Value * config.RsiWeight; totalWeight += config.RsiWeight; }
		if (momentumScore.HasValue && config.MomentumWeight > 0m) { weightedSum += momentumScore.Value * config.MomentumWeight; totalWeight += config.MomentumWeight; }

		if (totalWeight == 0m) return null;

		return new TechnicalBias(
			Score: weightedSum / totalWeight,
			SmaScore: smaScore ?? 0m,
			RsiScore: rsiScore ?? 0m,
			MomentumScore: momentumScore ?? 0m);
	}
}
```

- [ ] **Step 2: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/TechnicalIndicators.cs
git commit -m "Add TechnicalIndicators (SMA/RSI/momentum)"
```

---

## Task 3: Config — `TechnicalFilterConfig`

**Files:**
- Modify: `AI/AIConfig.cs`

- [ ] **Step 1: Add `TechnicalFilterConfig` class at the bottom of `AI/AIConfig.cs`, before the closing brace of the file.**

Add this class after `RollShortOnExpiryConfig`:

```csharp
internal sealed class TechnicalFilterConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Number of daily closes to fetch. Must be ≥ 20 (required for SMA20).</summary>
	[JsonPropertyName("lookbackDays")] public int LookbackDays { get; set; } = 20;
	[JsonPropertyName("smaWeight")] public decimal SmaWeight { get; set; } = 1.0m;
	[JsonPropertyName("rsiWeight")] public decimal RsiWeight { get; set; } = 1.0m;
	[JsonPropertyName("momentumWeight")] public decimal MomentumWeight { get; set; } = 1.0m;
	[JsonPropertyName("momentumDays")] public int MomentumDays { get; set; } = 5;
	/// <summary>Composite score threshold above which call positions are blocked from rolling.</summary>
	[JsonPropertyName("bullishBlockThreshold")] public decimal BullishBlockThreshold { get; set; } = 0.25m;
	/// <summary>Composite score threshold below which put positions are blocked from rolling.</summary>
	[JsonPropertyName("bearishBlockThreshold")] public decimal BearishBlockThreshold { get; set; } = -0.25m;
}
```

- [ ] **Step 2: Add `TechnicalFilter` property to `OpportunisticRollConfig`.**

In `OpportunisticRollConfig`, after the existing `StrikeStep` property, add:

```csharp
[JsonPropertyName("technicalFilter")] public TechnicalFilterConfig TechnicalFilter { get; set; } = new();
```

- [ ] **Step 3: Add validation in `AIConfigLoader.Validate`.**

In the `Validate` method, after the `rr` (rollShortOnExpiry) block and before `return null`, add:

```csharp
var tf = c.Rules.OpportunisticRoll.TechnicalFilter;
if (tf.LookbackDays < 20) return $"rules.opportunisticRoll.technicalFilter.lookbackDays: must be ≥ 20, got {tf.LookbackDays}";
if (tf.SmaWeight < 0m) return $"rules.opportunisticRoll.technicalFilter.smaWeight: must be ≥ 0, got {tf.SmaWeight}";
if (tf.RsiWeight < 0m) return $"rules.opportunisticRoll.technicalFilter.rsiWeight: must be ≥ 0, got {tf.RsiWeight}";
if (tf.MomentumWeight < 0m) return $"rules.opportunisticRoll.technicalFilter.momentumWeight: must be ≥ 0, got {tf.MomentumWeight}";
if (tf.MomentumDays < 1) return $"rules.opportunisticRoll.technicalFilter.momentumDays: must be ≥ 1, got {tf.MomentumDays}";
```

- [ ] **Step 4: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit.**

```
git add AI/AIConfig.cs
git commit -m "AIConfig: add TechnicalFilterConfig for OpportunisticRollRule"
```

---

## Task 4: `EvaluationContext` — add `TechnicalSignals`

**Files:**
- Modify: `AI/EvaluationContext.cs`
- Modify: `AI/AICommand.cs` (constructor call site)
- Modify: `AI/WatchLoop.cs` (constructor call site)
- Modify: `AI/Replay/ReplayRunner.cs` (constructor call site)

- [ ] **Step 1: Add `TechnicalSignals` to `EvaluationContext` in `AI/EvaluationContext.cs`.**

Add the new doc-comment param and positional record parameter. The full updated record:

```csharp
/// <summary>
/// Snapshot of state passed to every rule on every tick.
/// Immutable; one instance per tick.
/// </summary>
/// <param name="Now">Logical clock for this evaluation. Live: DateTime.Now. Replay: the historical step.</param>
/// <param name="OpenPositions">All currently-open positions grouped by strategy (keyed by position key).</param>
/// <param name="UnderlyingPrices">Spot prices for each ticker under management.</param>
/// <param name="Quotes">Per-leg option quotes by OCC symbol.</param>
/// <param name="AccountCash">Free cash available (before applying reserve).</param>
/// <param name="AccountValue">Total account value (cash + positions marked to market).</param>
/// <param name="TechnicalSignals">Composite technical bias per ticker. Missing entry = neutral (no block).</param>
internal sealed record EvaluationContext(
	DateTime Now,
	IReadOnlyDictionary<string, OpenPosition> OpenPositions,
	IReadOnlyDictionary<string, decimal> UnderlyingPrices,
	IReadOnlyDictionary<string, OptionContractQuote> Quotes,
	decimal AccountCash,
	decimal AccountValue,
	IReadOnlyDictionary<string, TechnicalBias> TechnicalSignals
);
```

- [ ] **Step 2: Fix constructor call in `AI/AICommand.cs` (line ~110).**

Change:
```csharp
var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
```
To (passing an empty dict as a temporary placeholder — the real signal injection happens in Task 6):
```csharp
var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, new Dictionary<string, TechnicalBias>());
```

- [ ] **Step 3: Fix constructor call in `AI/WatchLoop.cs` (line ~94).**

Change:
```csharp
var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
```
To:
```csharp
var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, new Dictionary<string, TechnicalBias>());
```

- [ ] **Step 4: Fix constructor call in `AI/Replay/ReplayRunner.cs` (line ~47).**

Change:
```csharp
var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue);
```
To:
```csharp
var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, new Dictionary<string, TechnicalBias>());
```

- [ ] **Step 5: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Commit.**

```
git add AI/EvaluationContext.cs AI/AICommand.cs AI/WatchLoop.cs AI/Replay/ReplayRunner.cs
git commit -m "EvaluationContext: add TechnicalSignals property"
```

---

## Task 5: `HistoricalPriceCache.GetRecentClosesAsync`

**Files:**
- Modify: `AI/Replay/HistoricalPriceCache.cs`

- [ ] **Step 1: Add the new method to `HistoricalPriceCache`, before the closing brace of the class.**

```csharp
/// <summary>Returns the last <paramref name="count"/> daily closes on or before <paramref name="asOf"/>,
/// oldest-first. Returns fewer than <paramref name="count"/> entries if the cache has less data.</summary>
public async Task<IReadOnlyList<decimal>> GetRecentClosesAsync(string ticker, int count, DateTime asOf, CancellationToken cancellation)
{
	var map = await LoadOrFetchAsync(ticker, cancellation);
	return map
		.Where(kv => kv.Key.Date <= asOf.Date)
		.OrderByDescending(kv => kv.Key)
		.Take(count)
		.OrderBy(kv => kv.Key)
		.Select(kv => kv.Value)
		.ToList();
}
```

- [ ] **Step 2: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/Replay/HistoricalPriceCache.cs
git commit -m "HistoricalPriceCache: add GetRecentClosesAsync"
```

---

## Task 6: `AIPipelineHelper.ComputeTechnicalSignalsAsync`

**Files:**
- Modify: `AI/AIPipelineHelper.cs`

- [ ] **Step 1: Add `ComputeTechnicalSignalsAsync` to `AIPipelineHelper`.**

Add the following method to the `AIPipelineHelper` static class, after `FetchQuotesWithHypotheticals`:

```csharp
/// <summary>Fetches recent daily closes per ticker and computes a composite technical bias.
/// Returns an empty dict when filter is disabled. Missing tickers (insufficient data) are omitted —
/// rules treat a missing entry as neutral.</summary>
public static async Task<IReadOnlyDictionary<string, TechnicalBias>> ComputeTechnicalSignalsAsync(
	IReadOnlySet<string> tickers,
	Replay.HistoricalPriceCache priceCache,
	TechnicalFilterConfig filter,
	DateTime asOf,
	CancellationToken cancellation)
{
	var result = new Dictionary<string, TechnicalBias>(StringComparer.OrdinalIgnoreCase);
	if (!filter.Enabled) return result;
	foreach (var ticker in tickers)
	{
		var closes = await priceCache.GetRecentClosesAsync(ticker, filter.LookbackDays, asOf, cancellation);
		var bias = TechnicalIndicators.Compute(closes, filter);
		if (bias != null) result[ticker] = bias;
	}
	return result;
}
```

- [ ] **Step 2: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/AIPipelineHelper.cs
git commit -m "AIPipelineHelper: add ComputeTechnicalSignalsAsync"
```

---

## Task 7: Pipeline injection — `AICommand` (live, once)

**Files:**
- Modify: `AI/AICommand.cs`

- [ ] **Step 1: In `AIOnceCommand.ExecuteAsync`, create the price cache and compute signals before building context.**

After the `tickerSet` declaration and before `var openPositions = ...`, add:

```csharp
var priceCache = new Replay.HistoricalPriceCache();
```

Then replace the temporary placeholder context construction (from Task 4) with:

```csharp
var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
	tickerSet, priceCache, config.Rules.OpportunisticRoll.TechnicalFilter, now, cancellation);

var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
```

- [ ] **Step 2: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/AICommand.cs
git commit -m "AICommand: inject TechnicalSignals into EvaluationContext"
```

---

## Task 8: Pipeline injection — `WatchLoop` (live, watch)

**Files:**
- Modify: `AI/WatchLoop.cs`

- [ ] **Step 1: In `AIWatchCommand.ExecuteAsync`, create the price cache once before the loop and compute signals each tick.**

After the `sink` declaration and before the `while` loop, add:

```csharp
var priceCache = new Replay.HistoricalPriceCache();
```

Inside the `try` block of the while loop, replace the temporary placeholder context construction (from Task 4) with:

```csharp
var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
	tickerSet, priceCache, config.Rules.OpportunisticRoll.TechnicalFilter, now, cancellation);

var ctx = new EvaluationContext(now, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
```

- [ ] **Step 2: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/WatchLoop.cs
git commit -m "WatchLoop: inject TechnicalSignals into EvaluationContext each tick"
```

---

## Task 9: Pipeline injection — `ReplayRunner` (replay)

**Files:**
- Modify: `AI/Replay/ReplayRunner.cs`
- Modify: `AI/AICommand.cs` (pass priceCache to ReplayRunner constructor)

- [ ] **Step 1: Add `_priceCache` field and update `ReplayRunner` constructor.**

Add the field at the top of the class:

```csharp
private readonly HistoricalPriceCache _priceCache;
```

Update the constructor signature and body:

```csharp
public ReplayRunner(AIConfig config, ReplayPositionSource positions, ReplayQuoteSource quotes, List<Trade> allTrades, HistoricalPriceCache priceCache)
{
	_config = config;
	_positions = positions;
	_quotes = quotes;
	_allTrades = allTrades;
	_priceCache = priceCache;
}
```

- [ ] **Step 2: In `ReplayRunner.RunAsync`, replace the placeholder context construction with signal computation.**

Replace the temporary placeholder context (from Task 4) with:

```csharp
var technicalSignals = await AIPipelineHelper.ComputeTechnicalSignalsAsync(
	tickerSet, _priceCache, _config.Rules.OpportunisticRoll.TechnicalFilter, step, cancellation);

var ctx = new EvaluationContext(step, openPositions, quoteSnapshot.Underlyings, quoteSnapshot.Options, cash, accountValue, technicalSignals);
```

- [ ] **Step 3: Pass `priceCache` into the `ReplayRunner` constructor in `AIReplayCommand.ExecuteAsync` in `AI/AICommand.cs`.**

Change:
```csharp
var runner = new Replay.ReplayRunner(config, positions, quotes, trades);
```
To:
```csharp
var runner = new Replay.ReplayRunner(config, positions, quotes, trades, priceCache);
```

- [ ] **Step 4: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit.**

```
git add AI/Replay/ReplayRunner.cs AI/AICommand.cs
git commit -m "ReplayRunner: inject TechnicalSignals into EvaluationContext per step"
```

---

## Task 10: `OpportunisticRollRule` — directional filter

**Files:**
- Modify: `AI/Rules/OpportunisticRollRule.cs`

- [ ] **Step 1: Add the technical filter block in `Evaluate`, after `ScenarioEngine.Classify` and before `ScenarioEngine.Evaluate`.**

The current code has this sequence:
```csharp
var kind = ScenarioEngine.Classify(legInfos);
if (kind is ScenarioEngine.StructureKind.Unsupported or ScenarioEngine.StructureKind.Vertical or ScenarioEngine.StructureKind.SingleShort)
    return null;

if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m) return null;
```

After the `spot` check, insert:

```csharp
var callPut = legInfos.First(l => !l.IsLong).Parsed.CallPut;
if (_config.TechnicalFilter.Enabled
	&& ctx.TechnicalSignals.TryGetValue(position.Ticker, out var bias)
	&& bias.IsAdverse(callPut, _config.TechnicalFilter.BullishBlockThreshold, _config.TechnicalFilter.BearishBlockThreshold))
	return null;
```

- [ ] **Step 2: Include the bias score in the proposal rationale when a signal is present.**

Find the existing `rationale` local variable assignment near the end of `Evaluate`:

```csharp
var rationale = $"optimizer: {topFundable.Name} projects ${topPerDay:+0.00;-0.00}/ct/day vs hold ${holdPerDay:+0.00;-0.00}/ct/day (Δ ${improvementPerDayPerContract:+0.00;-0.00}/ct/day over {topFundable.DaysToTarget}d). {topFundable.Rationale}";
```

Replace with:

```csharp
ctx.TechnicalSignals.TryGetValue(position.Ticker, out var biasForRationale);
var biasText = biasForRationale != null ? $" technicalBias={biasForRationale.Score:+0.00;-0.00}" : "";
var rationale = $"optimizer: {topFundable.Name} projects ${topPerDay:+0.00;-0.00}/ct/day vs hold ${holdPerDay:+0.00;-0.00}/ct/day (Δ ${improvementPerDayPerContract:+0.00;-0.00}/ct/day over {topFundable.DaysToTarget}d). {topFundable.Rationale}{biasText}";
```

- [ ] **Step 3: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit.**

```
git add AI/Rules/OpportunisticRollRule.cs
git commit -m "OpportunisticRollRule: block on adverse technical bias"
```

---

## Task 11: Improved `ClassifyAgreement` (four-way)

**Files:**
- Modify: `AI/Replay/ReplayRunner.cs`

- [ ] **Step 1: Replace `ClassifyAgreement` in `ReplayRunner` with the four-way implementation.**

Replace the existing method:

```csharp
private string ClassifyAgreement(ManagementProposal p, DateTime step)
{
	var sameDay = _allTrades.Where(t => t.Timestamp.Date == step.Date && t.MatchKey.Contains(p.Ticker, StringComparison.OrdinalIgnoreCase)).ToList();
	if (sameDay.Count == 0) return "miss";
	return "partial";
}
```

With:

```csharp
private string ClassifyAgreement(ManagementProposal p, DateTime step)
{
	var tickerTrades = _allTrades
		.Where(t => t.Timestamp.Date == step.Date
			&& MatchKeys.GetTicker(t.MatchKey)?.Equals(p.Ticker, StringComparison.OrdinalIgnoreCase) == true)
		.ToList();

	if (tickerTrades.Count == 0) return "miss";
	if (p.Legs.Count == 0) return "partial"; // alert-only proposal, no legs to match

	// Build a lookup of OCC symbol → trade side for same-day option fills on this ticker.
	var tradedBySymbol = tickerTrades
		.Where(t => t.Asset == Asset.Option)
		.ToDictionary(
			t => t.MatchKey.StartsWith("option:", StringComparison.OrdinalIgnoreCase) ? t.MatchKey[7..] : t.MatchKey,
			t => t.Side,
			StringComparer.OrdinalIgnoreCase);

	// Partial: same ticker traded, but none of the proposed OCC symbols appear in fills.
	var hasOverlap = p.Legs.Any(leg => tradedBySymbol.ContainsKey(leg.Symbol));
	if (!hasOverlap) return "partial";

	// Match: every proposed leg has an exact fill (same symbol + same direction).
	var isMatch = p.Legs.All(leg =>
		tradedBySymbol.TryGetValue(leg.Symbol, out var tradedSide)
		&& (leg.Action == "buy" ? tradedSide == Side.Buy : tradedSide == Side.Sell));

	return isMatch ? "match" : "divergent";
}
```

- [ ] **Step 2: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit.**

```
git add AI/Replay/ReplayRunner.cs
git commit -m "ReplayRunner: four-way agreement classifier (match/divergent/partial/miss)"
```

---

## Task 12: Inline fill display in replay output

**Files:**
- Modify: `AI/Replay/ReplayRunner.cs`

- [ ] **Step 1: Add `RenderFillAnnotation` method to `ReplayRunner`.**

Add this private method after `ClassifyAgreement`:

```csharp
private void RenderFillAnnotation(ManagementProposal proposal, DateTime step, string agreement)
{
	var fills = _allTrades
		.Where(t => t.Timestamp.Date == step.Date
			&& MatchKeys.GetTicker(t.MatchKey)?.Equals(proposal.Ticker, StringComparison.OrdinalIgnoreCase) == true
			&& t.Asset == Asset.Option)
		.Take(3)
		.ToList();

	string fillText;
	if (fills.Count == 0)
	{
		fillText = "no fills on this position";
	}
	else
	{
		var parts = fills.Select(t =>
		{
			var sym = t.MatchKey.StartsWith("option:", StringComparison.OrdinalIgnoreCase) ? t.MatchKey[7..] : t.MatchKey;
			return $"{(t.Side == Side.Buy ? "BUY" : "SELL")} {sym} x{t.Qty}";
		});
		fillText = string.Join(", ", parts);
	}

	var tag = agreement switch
	{
		"match" => "[green]match[/]",
		"divergent" => "[yellow]divergent[/]",
		"partial" => "[blue]partial[/]",
		_ => "[dim]miss[/]"
	};
	AnsiConsole.MarkupLine($"  [dim]↳ actual: {Markup.Escape(fillText)}  {tag}[/]");
}
```

- [ ] **Step 2: Call `RenderFillAnnotation` after `agreementCounts[agreement]++` in `RunAsync`.**

Find the existing loop body:

```csharp
foreach (var r in results)
{
	sink.Emit(r.Proposal, r.IsRepeat);
	ruleFireCounts[r.Proposal.Rule] = (ruleFireCounts.TryGetValue(r.Proposal.Rule, out var n) ? n : 0) + 1;

	var agreement = ClassifyAgreement(r.Proposal, step);
	agreementCounts[agreement]++;
}
```

Replace with:

```csharp
foreach (var r in results)
{
	sink.Emit(r.Proposal, r.IsRepeat);
	ruleFireCounts[r.Proposal.Rule] = (ruleFireCounts.TryGetValue(r.Proposal.Rule, out var n) ? n : 0) + 1;

	var agreement = ClassifyAgreement(r.Proposal, step);
	agreementCounts[agreement]++;
	RenderFillAnnotation(r.Proposal, step, agreement);
}
```

- [ ] **Step 3: Build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit.**

```
git add AI/Replay/ReplayRunner.cs
git commit -m "ReplayRunner: render inline fill annotation after each replay proposal"
```

---

## Task 13: Config example + smoke test

**Files:**
- Modify: `ai-config.example.json`

- [ ] **Step 1: Add `technicalFilter` to the `opportunisticRoll` section in `ai-config.example.json`.**

Replace the existing `opportunisticRoll` block (if present) or add it under `rules`:

```json
"opportunisticRoll": {
    "enabled": true,
    "minImprovementPerDayPerContract": 0.50,
    "ivDefaultPct": 40,
    "strikeStep": 0.50,
    "technicalFilter": {
        "enabled": true,
        "lookbackDays": 20,
        "smaWeight": 1.0,
        "rsiWeight": 1.0,
        "momentumWeight": 1.0,
        "momentumDays": 5,
        "bullishBlockThreshold": 0.25,
        "bearishBlockThreshold": -0.25
    }
}
```

- [ ] **Step 2: Final build.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build -nologo -v quiet
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Smoke-test replay with technical filter active.**

```
"/mnt/c/Program Files/dotnet/dotnet.exe" run --no-build -- ai replay --since 2026-04-21 --until 2026-04-21
```

Expected output structure:
- Disclaimer block renders
- Any `OpportunisticRollRule` proposals now include `technicalBias=±X.XX` in rationale
- Each proposal has an `↳ actual: ...  [match|divergent|partial|miss]` line beneath it
- Summary counts are non-zero if positions existed on 2026-04-21

- [ ] **Step 4: Commit.**

```
git add ai-config.example.json
git commit -m "ai-config.example.json: add technicalFilter block under opportunisticRoll"
```
