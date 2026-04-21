# Combined Break-Even Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new break-even panel to the `report` command that aggregates across all open positions on the same underlying ticker. Rendered only when a ticker has 2+ independent positions. Supports stock + options mixed. Uses earliest expiry for break-even math; grid spans to latest expiry.

**Architecture:** A new `CombinedBreakEvenAnalyzer` runs after the existing `BreakEvenAnalyzer`, groups positions by ticker, keeps tickers with 2+ units, merges shared legs via a new `LegMerger`, and produces standard `BreakEvenResult`s. Rendering reuses `TableBuilder.BuildBreakEvenPanel` unchanged. Two small additive overloads on `OptionMath` and `TimeDecayGridBuilder` accept per-leg signed quantities (needed because merged legs can have different net qtys).

**Tech Stack:** C# .NET 10, Spectre.Console. Reuses `OptionMath`, `TimeDecayGridBuilder`, `BreakEvenAnalyzer`, `MatchKeys`, `ParsingHelpers`.

**Spec:** `docs/superpowers/specs/2026-04-21-combined-breakeven-panel-design.md`

---

## Ground rules for this plan

- **No test framework exists in the repo.** Verification is by `dotnet build` and manual CLI runs with expected observable output. Each code-change task ends with a build step; the final task includes a manual end-to-end run against real data across all three output modes (console, text, Excel).
- **Deviation from spec Section 7 (Testing):** the spec describes `LegMerger` and `CombinedBreakEvenAnalyzer` unit tests plus an integration smoke test. Since the project has no test project and adding one is out of scope for this feature, those tests are replaced by: (a) compilation-based verification at each task boundary, (b) a manual end-to-end run that exercises every code path at Task 7 Step 6. If the user later wants formal unit tests, that should be a separate plan that introduces a test project with structured fixtures.
- **Build command:** `dotnet build` — `dotnet` is on PATH (`/c/Program Files/dotnet/dotnet`).
- **Style:** tabs for indent, file-scoped namespace, `internal` by default unless a type is part of the public surface, one primary type per file (small related records may co-locate with their consumer), collection expressions `[]` where idiomatic.
- **Git:** commit at the end of each task. Commit messages use the style of recent commits (imperative, concise).

---

## File structure (created in this plan)

| File | Purpose | Created in |
|---|---|---|
| `LegMerger.cs` | Flattens and merges option/stock legs across positions on one ticker. Contains the `MergedLeg` record and the `Merge` method. | Task 1 |
| `CombinedBreakEvenAnalyzer.cs` | Produces combined `BreakEvenResult`s per qualifying ticker. | Task 4 |

**Modified files:**

| File | Change | Task |
|---|---|---|
| `OptionMath.cs` | Add `StrategyPnLWithBsMixed` overload (per-leg signed qty). | Task 2 |
| `TimeDecayGridBuilder.cs` | Add `Build` overload (per-leg signed qty). | Task 3 |
| `TableRenderer.cs` | Render combined panels after individual panels. | Task 5 |
| `TextFileExporter.cs` | Render combined panels after individual panels. | Task 6 |
| `ExcelExporter.cs` | Export combined panels after individual panels. | Task 7 |

---

## Task 1: `LegMerger` — merge legs across positions

**Files:**
- Create: `LegMerger.cs`

### Context for this task

`PositionRow` (see `Models.cs:126`) fields we use:
- `Instrument` (string) — for stock, this is the ticker.
- `Asset` — `Stock`, `Option`, or `OptionStrategy`.
- `Side` — `Buy` or `Sell`.
- `Qty` (int) — share/contract count.
- `AvgPrice`, `InitialAvgPrice?`, `AdjustedAvgPrice?`.
- `Expiry` (DateTime?) — option expiry; null for stock.
- `IsStrategyLeg` (bool) — true when this row is a leg of a strategy (parent row precedes).
- `MatchKey` (string?) — e.g., `"stock:GME"`, `"option:GME260213C00025000"`.

Strategy parent rows (`Asset == OptionStrategy`) carry no leg data of their own — their legs are the subsequent `IsStrategyLeg == true` rows. We skip parent rows during flattening.

`OptionMath.GetPremium(row)` (see `OptionMath.cs`) returns `row.AdjustedAvgPrice ?? row.InitialAvgPrice ?? row.AvgPrice`. Merged-leg pricing must use the same convention.

- [ ] **Step 1: Baseline build.**

Run: `dotnet build`
Expected: successful build with 0 warnings / 0 errors at the current commit.

- [ ] **Step 2: Create `LegMerger.cs`.**

```csharp
using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// A single leg after netting across all positions on one ticker. Either an option leg
/// (with OptionParsed) or a stock leg (with Ticker set and Parsed = null).
/// </summary>
/// <param name="Ticker">Underlying ticker symbol.</param>
/// <param name="Parsed">Parsed OCC info for options; null for stock.</param>
/// <param name="Symbol">OCC symbol (for options); ticker (for stock).</param>
/// <param name="Side">Net side after signed-qty netting.</param>
/// <param name="Qty">Magnitude of net qty (always positive).</param>
/// <param name="Price">Weighted-average price (per share/contract).</param>
/// <param name="SourcePositionCount">Number of distinct source positions merged into this leg.</param>
internal record MergedLeg(
	string Ticker,
	OptionParsed? Parsed,
	string Symbol,
	Side Side,
	int Qty,
	decimal Price,
	int SourcePositionCount
)
{
	internal bool IsStock => Parsed == null;
}

/// <summary>
/// Flattens and merges legs across positions on a single ticker.
/// Groups by MatchKey, nets signed quantities, weighted-averages prices, drops zero-net legs.
/// </summary>
internal static class LegMerger
{
	/// <summary>
	/// Merges the given positions (assumed to share one ticker) into a list of net legs.
	/// Parent strategy rows (Asset == OptionStrategy) are skipped; their legs follow as
	/// IsStrategyLeg rows and are processed individually.
	/// </summary>
	internal static List<MergedLeg> Merge(IEnumerable<PositionRow> positions)
	{
		// (matchKey) -> (signedQty, signedQtyTimesPrice, parsed, symbol, ticker, sourceCount)
		var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);

		foreach (var row in positions)
		{
			if (row.Asset == Asset.OptionStrategy) continue;
			if (row.MatchKey == null) continue;

			var signedQty = row.Side == Side.Buy ? row.Qty : -row.Qty;
			var price = row.AdjustedAvgPrice ?? row.InitialAvgPrice ?? row.AvgPrice;

			if (row.Asset == Asset.Stock)
			{
				if (!buckets.TryGetValue(row.MatchKey, out var b))
					b = new Bucket(row.Instrument, parsed: null, symbol: row.Instrument);
				b.SignedQty += signedQty;
				b.SignedValue += signedQty * price;
				b.Sources.Add(row);
				buckets[row.MatchKey] = b;
				continue;
			}

			if (row.Asset == Asset.Option)
			{
				var parsedInfo = MatchKeys.ParseOption(row.MatchKey);
				if (parsedInfo == null) continue;
				var (parsed, symbol) = parsedInfo.Value;
				if (!buckets.TryGetValue(row.MatchKey, out var b))
					b = new Bucket(parsed.Root, parsed, symbol);
				b.SignedQty += signedQty;
				b.SignedValue += signedQty * price;
				b.Sources.Add(row);
				buckets[row.MatchKey] = b;
			}
		}

		var result = new List<MergedLeg>();
		foreach (var b in buckets.Values)
		{
			if (b.SignedQty == 0) continue;
			var price = b.SignedValue / b.SignedQty;
			var side = b.SignedQty > 0 ? Side.Buy : Side.Sell;
			var qty = Math.Abs(b.SignedQty);
			result.Add(new MergedLeg(
				Ticker: b.Ticker,
				Parsed: b.Parsed,
				Symbol: b.Symbol,
				Side: side,
				Qty: qty,
				Price: Math.Round(price, 4, MidpointRounding.AwayFromZero),
				SourcePositionCount: b.Sources.Count
			));
		}

		// Stable order: stock first, then options by expiry then strike.
		result.Sort((a, b) =>
		{
			if (a.IsStock != b.IsStock) return a.IsStock ? -1 : 1;
			if (a.IsStock) return 0;
			var exp = a.Parsed!.ExpiryDate.CompareTo(b.Parsed!.ExpiryDate);
			if (exp != 0) return exp;
			var strike = a.Parsed.Strike.CompareTo(b.Parsed.Strike);
			if (strike != 0) return strike;
			return string.CompareOrdinal(a.Parsed.CallPut, b.Parsed.CallPut);
		});

		return result;
	}

	private sealed class Bucket
	{
		internal string Ticker { get; }
		internal OptionParsed? Parsed { get; }
		internal string Symbol { get; }
		internal int SignedQty { get; set; }
		internal decimal SignedValue { get; set; }
		internal List<PositionRow> Sources { get; } = [];

		internal Bucket(string ticker, OptionParsed? parsed, string symbol)
		{
			Ticker = ticker;
			Parsed = parsed;
			Symbol = symbol;
		}
	}
}
```

- [ ] **Step 3: Build.**

Run: `dotnet build`
Expected: successful build. Compilation verifies the new file's syntax, imports, and references (`MatchKeys.ParseOption`, `PositionRow`, `OptionParsed`).

- [ ] **Step 4: Commit.**

```bash
git add LegMerger.cs
git commit -m "Add LegMerger for netting legs across positions"
```

---

## Task 2: `OptionMath.StrategyPnLWithBsMixed` overload

**Files:**
- Modify: `OptionMath.cs` (add new method, existing `StrategyPnLWithBs` untouched)

### Context

Existing `OptionMath.LegPnLWithBs` (`OptionMath.cs:67`) already accepts a per-call `qty` and `side`:

```csharp
internal static decimal LegPnLWithBs(decimal underlyingPrice, OptionParsed parsed, string symbol,
	Side side, int qty, decimal premium, DateTime evaluationDate, AnalysisOptions opts)
```

The existing `StrategyPnLWithBs` (`OptionMath.cs:96`) passes a single shared `qty` to every leg — which does not work for merged legs where each leg has its own net qty (e.g., long 2× $25 combined with short 3× $30). We add an overload that accepts per-leg signed qtys baked into `MergedLeg`.

- [ ] **Step 1: Add the new overload in `OptionMath.cs`.**

Insert this method immediately after `StrategyPnLWithBs` (after line 97):

```csharp
	/// <summary>
	/// Computes total P&L across merged legs where each leg has its own net qty.
	/// Used by the combined break-even analyzer. Unlike <see cref="StrategyPnLWithBs"/>,
	/// this does not assume a uniform per-leg quantity.
	/// </summary>
	internal static decimal StrategyPnLWithBsMixed(decimal underlyingPrice, List<MergedLeg> legs, DateTime evaluationDate, AnalysisOptions opts)
	{
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (leg.IsStock) continue;
			total += LegPnLWithBs(underlyingPrice, leg.Parsed!, leg.Symbol, leg.Side, leg.Qty, leg.Price, evaluationDate, opts);
		}
		return total;
	}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: successful build. The new method references `MergedLeg` (Task 1) and must be visible — both are `internal` in the same assembly, so this resolves.

- [ ] **Step 3: Commit.**

```bash
git add OptionMath.cs
git commit -m "OptionMath: add StrategyPnLWithBsMixed for per-leg qtys"
```

---

## Task 3: `TimeDecayGridBuilder.Build` per-leg-qty overload

**Files:**
- Modify: `TimeDecayGridBuilder.cs` (add new method; existing `Build` untouched)

### Context

The existing `Build` (`TimeDecayGridBuilder.cs:11`) takes a shared `qty` and `parentSide` and uses them for every leg. Merged legs have their own per-leg qty and side, so we add an overload. The new overload reuses the private `BuildDateColumns`, `BuildPriceRows`, and `ComputeSpreadMarketMid` helpers by calling them directly (they already exist in the file).

The new overload:
- Iterates merged option legs with their own qty/side.
- Accumulates total dollar P&L by summing `LegPnLWithBs(..., leg.Qty, ...)` per leg.
- Converts dollar P&L back to per-share contract value using a synthetic `qty = 1` frame so the grid's "value per share" column works (value = netPremium + totalDollarPnL / 100).
- Anchors the today column to market mid using the same existing helper, but computes market mid as the signed sum over merged legs (each leg contributes `legSignedQty × legMid` — no `parentSide` flip needed since signs are baked into legs).

Per-leg sub-grid cells (`LegValues` for the multi-leg display) are populated the same way as today: Black-Scholes contract value per share per leg, not scaled by qty.

- [ ] **Step 1: Add the overload in `TimeDecayGridBuilder.cs`.**

Insert this method after the closing brace of the existing `Build` method (i.e., after line 103):

```csharp
	/// <summary>
	/// Builds a 2D grid for merged legs where each leg carries its own signed quantity
	/// (as produced by <see cref="LegMerger"/>). Unlike the uniform-qty overload, signs
	/// and magnitudes are baked per leg, so no parentSide flip is applied.
	/// Stock legs are not rendered as grid rows; callers add stock P&L to net cells
	/// after this method returns.
	/// </summary>
	internal static TimeDecayGrid Build(List<MergedLeg> mergedLegs, decimal netPremium, DateTime latestExpiry, AnalysisOptions opts, decimal padding, decimal centerPrice, List<decimal> breakEvens, int maxColumns, decimal? underlyingPrice)
	{
		// Only option legs participate in the grid math; stock is a linear overlay added by the caller.
		var optionLegs = mergedLegs.Where(l => !l.IsStock).ToList();

		var dates = BuildDateColumns(latestExpiry, maxColumns);
		var strikes = optionLegs.Select(l => l.Parsed!.Strike).Distinct().ToList();
		var priceRows = BuildPriceRows(centerPrice, padding, breakEvens, strikes);

		var values = new decimal[priceRows.Count, dates.Count];
		var pnls = new decimal[priceRows.Count, dates.Count];
		var includeLegs = optionLegs.Count > 1;
		var legValues = includeLegs ? new decimal[optionLegs.Count, priceRows.Count, dates.Count] : null;

		for (int di = 0; di < dates.Count; di++)
		{
			var evalDate = dates[di];
			for (int pi = 0; pi < priceRows.Count; pi++)
			{
				var price = priceRows[pi];
				decimal totalDollarPnL = 0m;
				for (int li = 0; li < optionLegs.Count; li++)
				{
					var l = optionLegs[li];
					totalDollarPnL += OptionMath.LegPnLWithBs(price, l.Parsed!, l.Symbol, l.Side, l.Qty, l.Price, evalDate, opts);
					if (legValues != null)
						legValues[li, pi, di] = Math.Round(OptionMath.LegContractValueWithBs(price, l.Parsed!, l.Symbol, l.Side, evalDate, opts), 4);
				}

				// Synthetic qty=1 frame: value per share = netPremium + dollarPnL / 100.
				var value = netPremium + totalDollarPnL / 100m;
				values[pi, di] = Math.Round(value, 4);
				var displayValue = Math.Round(values[pi, di], 2, MidpointRounding.AwayFromZero);
				pnls[pi, di] = Math.Round((displayValue - netPremium) * 100m, 2);
			}
		}

		List<string>? legLabels = null;
		if (includeLegs)
		{
			legLabels = new List<string>(optionLegs.Count);
			foreach (var l in optionLegs)
			{
				var sideStr = l.Side == Side.Buy ? "L" : "S";
				legLabels.Add($"{sideStr}{l.Parsed!.CallPut}{l.Parsed!.Strike.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
			}
		}

		// Anchor the first column (today) to market bid/ask mid if all merged option legs have quotes.
		if (!opts.Theoretical && opts.OptionQuotes != null && underlyingPrice.HasValue)
		{
			var marketMid = ComputeMergedMarketMid(optionLegs, opts.OptionQuotes);
			if (marketMid.HasValue)
			{
				int closestRow = 0;
				var closestDist = decimal.MaxValue;
				for (int pi = 0; pi < priceRows.Count; pi++)
				{
					var dist = Math.Abs(priceRows[pi] - underlyingPrice.Value);
					if (dist < closestDist) { closestDist = dist; closestRow = pi; }
				}

				var bsValue = values[closestRow, 0];
				var adjustment = bsValue - marketMid.Value;
				if (adjustment != 0)
				{
					for (int pi = 0; pi < priceRows.Count; pi++)
					{
						values[pi, 0] = Math.Round(values[pi, 0] - adjustment, 4);
						var adjDisplayValue = Math.Round(values[pi, 0], 2);
						pnls[pi, 0] = Math.Round((adjDisplayValue - netPremium) * 100, 2);
					}
				}

				if (legValues != null)
				{
					for (int li = 0; li < optionLegs.Count; li++)
					{
						var l = optionLegs[li];
						if (!opts.OptionQuotes.TryGetValue(l.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue) continue;
						var legMid = (q.Bid.Value + q.Ask.Value) / 2m;
						var legBs = legValues[li, closestRow, 0];
						var legAdj = legBs - legMid;
						if (legAdj != 0)
						{
							for (int pi = 0; pi < priceRows.Count; pi++)
								legValues[li, pi, 0] = Math.Round(legValues[li, pi, 0] - legAdj, 4);
						}
					}
				}
			}
		}

		return new TimeDecayGrid(dates, priceRows, values, pnls, strikes, legValues, legLabels);
	}

	/// <summary>
	/// Signed market mid across merged option legs: each leg contributes qty × sign × mid,
	/// scaled back to a per-share value consistent with netPremium.
	/// </summary>
	private static decimal? ComputeMergedMarketMid(List<MergedLeg> legs, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		decimal total = 0m;
		foreach (var leg in legs)
		{
			if (!quotes.TryGetValue(leg.Symbol, out var q) || !q.Bid.HasValue || !q.Ask.HasValue)
				return null;
			var mid = (q.Bid.Value + q.Ask.Value) / 2m;
			var signed = leg.Side == Side.Buy ? mid : -mid;
			total += signed * leg.Qty;
		}
		return total;
	}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add TimeDecayGridBuilder.cs
git commit -m "TimeDecayGridBuilder: add overload for per-leg signed qtys"
```

---

## Task 4: `CombinedBreakEvenAnalyzer`

**Files:**
- Create: `CombinedBreakEvenAnalyzer.cs`

### Context

The combined analyzer:

1. Reuses the existing unit grouping from `BreakEvenAnalyzer` — each non-strategy-leg `PositionRow` starts a new unit; subsequent `IsStrategyLeg` rows attach to it.
2. Groups units by ticker (using `MatchKeys.GetTicker(row.MatchKey)` on the parent row of each unit).
3. For each ticker with 2+ units, flattens all rows (parent + legs) and calls `LegMerger.Merge`.
4. Produces a standard `BreakEvenResult` using the merged legs, the existing `OptionMath` helpers, the `StrategyPnLWithBsMixed` overload, and the `TimeDecayGridBuilder` per-leg-qty overload.

Title format (per spec): `"<TICKER> Combined — <leg1>, <leg2>, ..."`. Each `<legN>` is `"<side> <qty>× <kind> $<strike>"` for options and `"<qty> sh Stock"` for stock.

Details format: `"<N> positions · Earliest expiry <date> · DTE <n>"`, plus `" · mixed expiries"` when applicable.

- [ ] **Step 1: Create `CombinedBreakEvenAnalyzer.cs`.**

```csharp
using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// Produces an aggregated break-even panel per ticker that has 2+ open positions.
/// Legs shared across positions are merged (signed-qty netting, weighted-average price).
/// Max profit, max loss, and break-evens are evaluated at the earliest option expiry;
/// the time-decay grid spans to the latest expiry.
/// </summary>
public static class CombinedBreakEvenAnalyzer
{
	public static List<BreakEvenResult> Analyze(List<PositionRow> positionRows, AnalysisOptions opts, decimal padding = 2, int maxGridColumns = 7)
	{
		var units = GroupUnits(positionRows);
		var byTicker = new Dictionary<string, List<List<PositionRow>>>(StringComparer.Ordinal);
		foreach (var unit in units)
		{
			var parent = unit[0];
			if (parent.MatchKey == null) continue;
			var ticker = MatchKeys.GetTicker(parent.MatchKey);
			if (ticker == null) continue;
			if (!byTicker.TryGetValue(ticker, out var list))
			{
				list = [];
				byTicker[ticker] = list;
			}
			list.Add(unit);
		}

		var results = new List<BreakEvenResult>();
		foreach (var ticker in byTicker.Keys.OrderBy(k => k, StringComparer.Ordinal))
		{
			var unitsForTicker = byTicker[ticker];
			if (unitsForTicker.Count < 2) continue;

			var allRows = unitsForTicker.SelectMany(u => u).ToList();
			var merged = LegMerger.Merge(allRows);
			if (merged.Count == 0) continue; // fully offsetting portfolio

			var result = BuildResult(ticker, unitsForTicker.Count, merged, opts, padding, maxGridColumns);
			if (result != null) results.Add(result);
		}
		return results;
	}

	/// <summary>
	/// Groups rows into units the same way <see cref="BreakEvenAnalyzer"/> does:
	/// each non-leg row starts a new unit; subsequent IsStrategyLeg rows attach to it.
	/// </summary>
	private static List<List<PositionRow>> GroupUnits(List<PositionRow> rows)
	{
		var groups = new List<List<PositionRow>>();
		foreach (var row in rows)
		{
			if (!row.IsStrategyLeg)
				groups.Add([row]);
			else if (groups.Count > 0)
				groups[^1].Add(row);
		}
		return groups;
	}

	private static BreakEvenResult? BuildResult(string ticker, int unitCount, List<MergedLeg> merged, AnalysisOptions opts, decimal padding, int maxGridColumns)
	{
		var optionLegs = merged.Where(l => !l.IsStock).ToList();
		var stockLeg = merged.FirstOrDefault(l => l.IsStock);

		// Net premium: signed sum of leg costs. Options use ×100 multiplier; stock uses ×1 (already handled in LegPnLWithBs dollars).
		// For the BreakEvenResult.netPremium convention (per-share value), we mirror the existing analyzer:
		// netPremium = Σ signedQty × price / qtyUnit, but here legs have independent qtys, so we carry dollar totals.
		// The grid overload expects netPremium as a per-share figure anchored to qty=1; we compute it as:
		// netPremium = Σ_option (signedQty × price).
		decimal netPremium = 0m;
		foreach (var leg in optionLegs)
		{
			var signed = leg.Side == Side.Buy ? 1 : -1;
			netPremium += signed * leg.Qty * leg.Price;
		}

		// Title: "GME Combined — <leg descriptors>"
		var title = BuildTitle(ticker, merged);

		// Earliest / latest option expiry.
		DateTime? nearestExpiry = optionLegs.Count > 0 ? optionLegs.Min(l => l.Parsed!.ExpiryDate) : null;
		DateTime? latestExpiry = optionLegs.Count > 0 ? optionLegs.Max(l => l.Parsed!.ExpiryDate) : null;
		var hasMixedExpiries = optionLegs.Count > 0 && optionLegs.Select(l => l.Parsed!.ExpiryDate.Date).Distinct().Count() > 1;

		// Details line.
		var detailsParts = new List<string> { $"{unitCount} positions" };
		if (nearestExpiry.HasValue)
		{
			detailsParts.Add($"Earliest expiry {Formatters.FormatOptionDate(nearestExpiry.Value)}");
			var dte = (int)(nearestExpiry.Value.Date - EvaluationDate.Today).TotalDays;
			detailsParts.Add($"DTE {dte}");
		}
		if (hasMixedExpiries) detailsParts.Add("mixed expiries");
		var details = string.Join(" · ", detailsParts);

		int? daysToExpiry = nearestExpiry.HasValue ? (int)(nearestExpiry.Value.Date - EvaluationDate.Today).TotalDays : null;

		// Leg descriptions (flat list; stock first if present, then options).
		var legDescriptions = BuildLegDescriptions(merged, opts);

		// Stock P&L helper: dollars at a given underlying price.
		decimal StockDollarPnL(decimal s)
		{
			if (stockLeg == null) return 0m;
			var signed = stockLeg.Side == Side.Buy ? 1 : -1;
			return signed * stockLeg.Qty * (s - stockLeg.Price);
		}

		// Notable prices for the ladder.
		var spot = LookupUnderlyingPrice(ticker, opts);
		var strikes = optionLegs.Select(l => l.Parsed!.Strike).Distinct().OrderBy(x => x).ToList();
		var notablePrices = new List<decimal>(strikes);
		if (spot.HasValue) notablePrices.Add(spot.Value);
		notablePrices.AddRange(LookupExtraNotablePrices(ticker, opts));
		if (notablePrices.Count == 0 && stockLeg != null) notablePrices.Add(stockLeg.Price);
		if (notablePrices.Count == 0) return null;

		var centerPrice = strikes.Count > 0 ? strikes.Average() : (spot ?? stockLeg!.Price);
		var step = OptionMath.GetPriceStep(centerPrice);

		// IV requirement check: if any unexpired option leg lacks IV at the nearest expiry, emit a note and skip grid.
		string? note = null;
		bool canComputeBsAtNearest = nearestExpiry == null || HasIvForUnexpiredLegs(optionLegs, nearestExpiry.Value, opts);
		if (optionLegs.Count > 0 && !canComputeBsAtNearest && hasMixedExpiries)
		{
			note = "Break-even analysis requires implied volatility. Enable option-chain lookup with --api yahoo (or --api webull), or use the interactive IV override after the report renders.";
			return new BreakEvenResult(
				title, details, Qty: 1, BreakEvens: [], MaxProfit: null, MaxLoss: null,
				DaysToExpiry: daysToExpiry, PriceLadder: [], Note: note,
				Legs: legDescriptions,
				UnderlyingPrice: spot,
				OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(ticker, opts)
			);
		}

		// P&L function evaluated at the nearest expiry (where max P&L and break-evens are computed).
		DateTime pnlEvalDate = nearestExpiry.HasValue ? nearestExpiry.Value.Date + OptionMath.MarketClose : EvaluationDate.Today;
		Func<decimal, decimal> pnlFunc = s =>
			OptionMath.StrategyPnLWithBsMixed(s, merged, pnlEvalDate, opts) + StockDollarPnL(s);
		Func<decimal, decimal, decimal?> valueAt = (s, pnl) => null; // contract value isn't meaningful across mixed-qty legs.

		var ladder = OptionMath.BuildPriceLadder(notablePrices, step, pnlFunc, valueAt);

		var breakEvens = OptionMath.FindBreakEvensNumerically(ladder, pnlFunc);
		foreach (var be in breakEvens)
		{
			if (!ladder.Any(p => Math.Abs(p.UnderlyingPrice - be) < 0.005m))
				ladder.Add(new PricePnL(be, 0m, null));
		}
		ladder.Sort((a, b) => a.UnderlyingPrice.CompareTo(b.UnderlyingPrice));

		decimal? maxProfit = ladder.Max(p => p.PnL);
		decimal? maxLoss = Math.Abs(ladder.Min(p => p.PnL));

		ladder.Reverse();
		var chartData = OptionMath.BuildChartData(notablePrices, step, pnlFunc, valueAt);

		// Time-decay grid (spans to latest expiry).
		TimeDecayGrid? grid = null;
		if (optionLegs.Count > 0 && latestExpiry.HasValue && canComputeBsAtNearest)
		{
			var gridNotable = new List<decimal>(breakEvens);
			if (spot.HasValue) gridNotable.Add(spot.Value);
			gridNotable.AddRange(LookupExtraNotablePrices(ticker, opts));
			grid = TimeDecayGridBuilder.Build(merged, netPremium, latestExpiry.Value, opts, padding, centerPrice, gridNotable, maxGridColumns, spot);

			// Overlay stock P&L onto each grid cell's net P&L if stock is present.
			if (stockLeg != null)
			{
				for (int pi = 0; pi < grid.PriceRows.Count; pi++)
				{
					var cellPrice = grid.PriceRows[pi];
					var stockPnL = StockDollarPnL(cellPrice);
					for (int di = 0; di < grid.DateColumns.Count; di++)
						grid.PnLs[pi, di] = Math.Round(grid.PnLs[pi, di] + stockPnL, 2);
				}
			}
		}
		else if (hasMixedExpiries && nearestExpiry.HasValue)
		{
			note = "P&L estimated using Black-Scholes at earliest-expiry date. Actual results will vary with volatility.";
		}

		return new BreakEvenResult(
			Title: title,
			Details: details,
			Qty: 1,
			BreakEvens: breakEvens,
			MaxProfit: maxProfit,
			MaxLoss: maxLoss,
			DaysToExpiry: daysToExpiry,
			PriceLadder: ladder,
			Note: note,
			Legs: legDescriptions,
			ChartData: chartData,
			EarlyExercise: null,
			Grid: grid,
			UnderlyingPrice: spot,
			OriginalUnderlyingPrice: LookupOriginalUnderlyingPrice(ticker, opts)
		);
	}

	private static string BuildTitle(string ticker, List<MergedLeg> merged)
	{
		var descriptors = new List<string>();
		foreach (var leg in merged)
		{
			if (leg.IsStock)
			{
				var sideWord = leg.Side == Side.Buy ? "sh" : "sh short";
				descriptors.Add($"{leg.Qty} {sideWord} Stock");
			}
			else
			{
				var sideWord = leg.Side == Side.Buy ? "Long" : "Short";
				var kind = ParsingHelpers.CallPutDisplayName(leg.Parsed!.CallPut);
				descriptors.Add($"{sideWord} {leg.Qty}× {kind} ${Formatters.FormatQty(leg.Parsed.Strike)}");
			}
		}
		return $"{ticker} Combined — {string.Join(", ", descriptors)}";
	}

	private static List<string> BuildLegDescriptions(List<MergedLeg> merged, AnalysisOptions opts)
	{
		var legs = new List<string>();
		foreach (var leg in merged)
		{
			if (leg.IsStock)
			{
				var sideWord = leg.Side == Side.Buy ? "Long" : "Short";
				var line = $"Stock — {sideWord} {leg.Qty} sh @ ${leg.Price.ToString("N2", CultureInfo.InvariantCulture)}";
				legs.Add(line);
				continue;
			}

			var longShort = leg.Side == Side.Buy ? "Long" : "Short";
			var cpDisplay = ParsingHelpers.CallPutDisplayName(leg.Parsed!.CallPut);
			var desc = $"{longShort} {leg.Qty}× {cpDisplay} ${Formatters.FormatQty(leg.Parsed.Strike)} @ ${Formatters.FormatPrice(leg.Price, Asset.Option)}, Exp {Formatters.FormatOptionDate(leg.Parsed.ExpiryDate)}";

			var yahooInfo = TryFormatYahooQuote(leg.Symbol, opts);
			if (yahooInfo != null) desc += $" | {yahooInfo}";

			if (leg.SourcePositionCount > 1)
				desc += $" (merged from {leg.SourcePositionCount} positions)";

			legs.Add(desc);
		}
		return legs;
	}

	private static bool HasIvForUnexpiredLegs(List<MergedLeg> optionLegs, DateTime evaluationExpiry, AnalysisOptions opts)
	{
		if (opts.IvOverrides != null && optionLegs.Any(l => opts.IvOverrides.ContainsKey(l.Symbol))) return true;
		if (opts.OptionQuotes == null) return false;

		// Any leg whose expiry is strictly after the evaluation expiry needs IV.
		var unexpired = optionLegs.Where(l => l.Parsed!.ExpiryDate.Date > evaluationExpiry.Date).ToList();
		if (unexpired.Count == 0) return true;
		return unexpired.All(l => OptionMath.GetLegIv(l.Side, l.Symbol, opts).HasValue);
	}

	private static decimal? LookupUnderlyingPrice(string ticker, AnalysisOptions opts)
	{
		if (opts.UnderlyingPriceOverrides != null && opts.UnderlyingPriceOverrides.TryGetValue(ticker, out var overridePrice))
			return Math.Round(overridePrice, 2);
		if (opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(ticker, out var price))
			return Math.Round(price, 2);
		return null;
	}

	private static decimal? LookupOriginalUnderlyingPrice(string ticker, AnalysisOptions opts)
	{
		if (opts.UnderlyingPriceOverrides == null || !opts.UnderlyingPriceOverrides.ContainsKey(ticker)) return null;
		if (opts.UnderlyingPrices != null && opts.UnderlyingPrices.TryGetValue(ticker, out var price))
			return Math.Round(price, 2);
		return null;
	}

	private static List<decimal> LookupExtraNotablePrices(string ticker, AnalysisOptions opts)
	{
		if (opts.ExtraNotablePrices != null && opts.ExtraNotablePrices.TryGetValue(ticker, out var prices))
			return prices;
		return [];
	}

	private static string? TryFormatYahooQuote(string symbol, AnalysisOptions opts)
	{
		if (opts.OptionQuotes == null) return null;
		if (!opts.OptionQuotes.TryGetValue(symbol, out var quote)) return null;

		var parts = new List<string>();
		if (quote.LastPrice.HasValue) parts.Add($"Last ${Formatters.FormatPrice(quote.LastPrice.Value, Asset.Option)}");
		if (quote.Bid.HasValue) parts.Add($"Bid ${Formatters.FormatPrice(quote.Bid.Value, Asset.Option)}");
		if (quote.Ask.HasValue) parts.Add($"Ask ${Formatters.FormatPrice(quote.Ask.Value, Asset.Option)}");
		if (quote.ImpliedVolatility.HasValue)
			parts.Add($"IV {(quote.ImpliedVolatility.Value * 100m).ToString("N1", CultureInfo.InvariantCulture)}%");
		if (quote.HistoricalVolatility.HasValue)
			parts.Add($"HV {(quote.HistoricalVolatility.Value * 100m).ToString("N1", CultureInfo.InvariantCulture)}%");
		return parts.Count == 0 ? null : string.Join(" | ", parts);
	}
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: successful build. All referenced helpers (`OptionMath.GetPriceStep`, `OptionMath.BuildPriceLadder`, `OptionMath.BuildChartData`, `OptionMath.FindBreakEvensNumerically`, `Formatters.FormatQty`, `Formatters.FormatPrice`, `Formatters.FormatOptionDate`, `ParsingHelpers.CallPutDisplayName`, `EvaluationDate.Today`) exist in the project.

- [ ] **Step 3: Commit.**

```bash
git add CombinedBreakEvenAnalyzer.cs
git commit -m "Add CombinedBreakEvenAnalyzer for per-ticker aggregated breakevens"
```

---

## Task 5: Console integration (`TableRenderer`)

**Files:**
- Modify: `TableRenderer.cs` (lines 34-39 area)

### Context

`TableRenderer.RenderReport` currently calls `BreakEvenAnalyzer.Analyze` and renders each result with `BuildBreakEvenPanel`. We add a second call to `CombinedBreakEvenAnalyzer.Analyze` and interleave the combined results by ticker: for each ticker, its individual panels render first, then its combined panel (if any).

The merge-by-ticker is easy because both analyzers process the same position list in the same order. We extract each individual result's ticker from its title (titles start with `"<TICKER> …"`) — but that's brittle. A cleaner approach: iterate `positions` in order, render individual panels for each unit, and track the last-emitted ticker; when a new ticker starts, emit any combined panels for the previous ticker.

An even cleaner approach, which we use: run both analyses up front, then build a ticker-ordered render queue.

- [ ] **Step 1: Update `TableRenderer.RenderReport` to render combined panels.**

Replace the existing breakeven loop (lines 33-39 in the current file) with the block below. The rest of `RenderReport` is unchanged.

```csharp
				var maxGridColumns = ComputeMaxGridColumns(displayMode, showLegs);
				var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
				var combined = CombinedBreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
				var combinedByTicker = new Dictionary<string, BreakEvenResult>(StringComparer.Ordinal);
				foreach (var c in combined)
				{
					var ticker = ExtractTickerFromCombinedTitle(c.Title);
					if (ticker != null) combinedByTicker[ticker] = c;
				}

				string? lastTicker = null;
				foreach (var result in breakEvens)
				{
					var ticker = ExtractTickerFromTitle(result.Title);
					if (lastTicker != null && ticker != lastTicker && combinedByTicker.TryGetValue(lastTicker, out var prevCombined))
					{
						console.Write(TableBuilder.BuildBreakEvenPanel(prevCombined, displayMode: displayMode, showLegs: showLegs));
						console.WriteLine();
						combinedByTicker.Remove(lastTicker);
					}
					console.Write(TableBuilder.BuildBreakEvenPanel(result, displayMode: displayMode, showLegs: showLegs));
					console.WriteLine();
					lastTicker = ticker;
				}
				// Flush final ticker's combined panel.
				if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
				{
					console.Write(TableBuilder.BuildBreakEvenPanel(finalCombined, displayMode: displayMode, showLegs: showLegs));
					console.WriteLine();
				}
```

Then add these two helpers at the bottom of the `TableRenderer` class (above the closing brace):

```csharp
	/// <summary>
	/// Extracts the ticker from an individual break-even result title.
	/// Titles begin with the ticker followed by a space (e.g., "GME Long Call $25").
	/// </summary>
	private static string? ExtractTickerFromTitle(string title)
	{
		var space = title.IndexOf(' ');
		return space <= 0 ? null : title[..space];
	}

	/// <summary>
	/// Extracts the ticker from a combined-panel title: "&lt;TICKER&gt; Combined — ...".
	/// </summary>
	private static string? ExtractTickerFromCombinedTitle(string title)
	{
		var space = title.IndexOf(' ');
		return space <= 0 ? null : title[..space];
	}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: successful build.

- [ ] **Step 3: Manual verification — run the report against existing data.**

Find a real data file in the repo root. The project's usual invocations use `Webull_Orders_Records.csv` or the JSONL file.

Run: `dotnet run -- report --source csv`
(or whatever the user's usual report invocation is; see `ReportCommand.cs` for options).

Expected observable output:
- If any ticker has 2+ positions, a combined panel appears after that ticker's individual panels.
- Title matches `"<TICKER> Combined — …"` with all merged legs listed.
- Details line shows `"<N> positions · Earliest expiry … · DTE …"`.
- If only one ticker has a single position, no combined panel appears at all.

If no file currently has 2+ positions on one ticker, skip this manual verification and do it at the end after Task 7.

- [ ] **Step 4: Commit.**

```bash
git add TableRenderer.cs
git commit -m "TableRenderer: render combined breakeven panel per ticker"
```

---

## Task 6: Text file exporter integration

**Files:**
- Modify: `TextFileExporter.cs` (lines 45–51)

### Current code to replace

The current break-even block in `TextFileExporter.cs`:

```csharp
			var maxGridColumns = TableBuilder.ComputeMaxGridColumns(200, displayMode, showLegs);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			foreach (var result in breakEvens)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(result, Spectre.Console.BoxBorder.Ascii, TableBorder.Ascii, ascii: true, displayMode: displayMode, showLegs: showLegs));
				console.WriteLine();
			}
```

- [ ] **Step 1: Replace the block above with the interleaving version.**

In `TextFileExporter.cs`, replace the five-line block starting at line 45 with:

```csharp
			var maxGridColumns = TableBuilder.ComputeMaxGridColumns(200, displayMode, showLegs);
			var breakEvens = BreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			var combined = CombinedBreakEvenAnalyzer.Analyze(positions, opts, range, maxGridColumns);
			var combinedByTicker = new Dictionary<string, BreakEvenResult>(StringComparer.Ordinal);
			foreach (var c in combined)
			{
				var sp = c.Title.IndexOf(' ');
				if (sp > 0) combinedByTicker[c.Title[..sp]] = c;
			}

			void WriteResult(BreakEvenResult r)
			{
				console.Write(TableBuilder.BuildBreakEvenPanel(r, Spectre.Console.BoxBorder.Ascii, TableBorder.Ascii, ascii: true, displayMode: displayMode, showLegs: showLegs));
				console.WriteLine();
			}

			string? lastTicker = null;
			foreach (var result in breakEvens)
			{
				var sp = result.Title.IndexOf(' ');
				var ticker = sp > 0 ? result.Title[..sp] : null;
				if (lastTicker != null && ticker != lastTicker && combinedByTicker.TryGetValue(lastTicker, out var prev))
				{
					WriteResult(prev);
					combinedByTicker.Remove(lastTicker);
				}
				WriteResult(result);
				lastTicker = ticker;
			}
			if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
				WriteResult(finalCombined);
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: successful build.

- [ ] **Step 3: Commit.**

```bash
git add TextFileExporter.cs
git commit -m "TextFileExporter: include combined breakeven panels"
```

---

## Task 7: Excel exporter integration

**Files:**
- Modify: `ExcelExporter.cs` — method `ExportBreakEven`, lines 286–471

### Current structure

`ExportBreakEven` is structured as:

```csharp
private static void ExportBreakEven(ExcelWorksheet sheet, List<PositionRow> positionRows, AnalysisOptions opts)
{
    var results = BreakEvenAnalyzer.Analyze(positionRows, opts);        // line 288
    if (results.Count == 0) { ... return; }

    int row = 1;
    int chartIndex = 0;
    foreach (var result in results)                                      // line 297
    {
        int sectionStartRow = row;
        // ~170 lines of Excel cell writes, mutating `row` and `chartIndex`
        row += 2; // blank separator rows                                // line 467
    }

    sheet.Cells.AutoFitColumns();                                        // line 470
}
```

The loop body at lines 299–467 is long but it **only mutates two outer variables**: `row` and `chartIndex`. `sectionStartRow` is loop-local. This means the body can be extracted into a local function that captures `row` and `chartIndex` (C# local functions capture enclosing locals by reference semantics, so mutations inside persist outside).

- [ ] **Step 1: Refactor the existing `foreach` body into a local function.**

Keep the entire existing loop body unchanged; just move it into a local function declared immediately before the loop. After this step, the method should look like:

```csharp
private static void ExportBreakEven(ExcelWorksheet sheet, List<PositionRow> positionRows, AnalysisOptions opts)
{
    var results = BreakEvenAnalyzer.Analyze(positionRows, opts);
    if (results.Count == 0)
    {
        sheet.Cells[1, 1].Value = "No positions to analyze.";
        return;
    }

    int row = 1;
    int chartIndex = 0;

    void WriteResult(BreakEvenResult result)
    {
        int sectionStartRow = row;

        // ... (the existing body verbatim, lines 302–467 of the original file) ...
        // It writes to `sheet`, reads `result`, and mutates `row` and `chartIndex`.

        row += 2; // blank separator rows
    }

    foreach (var result in results)
        WriteResult(result);

    sheet.Cells.AutoFitColumns();
}
```

**Mechanical move:** select lines 299–467 (the inside of the old `foreach`, starting at `int sectionStartRow = row;` and ending at `row += 2; // blank separator rows`) and paste them verbatim into the body of `WriteResult`. Replace the old `foreach` body with `WriteResult(result);`.

- [ ] **Step 2: Build to confirm the refactor is behaviorally identical.**

Run: `dotnet build`
Expected: successful build. No logic changed yet — just moved into a local function.

- [ ] **Step 3: Commit the refactor separately.**

```bash
git add ExcelExporter.cs
git commit -m "ExcelExporter: extract breakeven row-writer into local function"
```

- [ ] **Step 4: Add combined-panel interleaving.**

Now update the method body again. Replace:

```csharp
    foreach (var result in results)
        WriteResult(result);
```

with:

```csharp
    var combined = CombinedBreakEvenAnalyzer.Analyze(positionRows, opts);
    var combinedByTicker = new Dictionary<string, BreakEvenResult>(StringComparer.Ordinal);
    foreach (var c in combined)
    {
        var sp = c.Title.IndexOf(' ');
        if (sp > 0) combinedByTicker[c.Title[..sp]] = c;
    }

    string? lastTicker = null;
    foreach (var result in results)
    {
        var sp = result.Title.IndexOf(' ');
        var ticker = sp > 0 ? result.Title[..sp] : null;
        if (lastTicker != null && ticker != lastTicker && combinedByTicker.TryGetValue(lastTicker, out var prev))
        {
            WriteResult(prev);
            combinedByTicker.Remove(lastTicker);
        }
        WriteResult(result);
        lastTicker = ticker;
    }
    if (lastTicker != null && combinedByTicker.TryGetValue(lastTicker, out var finalCombined))
        WriteResult(finalCombined);
```

- [ ] **Step 5: Build.**

Run: `dotnet build`
Expected: successful build.

- [ ] **Step 6: End-to-end manual verification across all three output modes.**

Run the report against real data that has 2+ positions on one ticker. If no such data currently exists:
- Option A: open a second position on a ticker that already has one (via Webull), OR
- Option B: point `--source csv` at a CSV that contains multiple positions on the same underlying.

Check the existing `report` command's output flags in `ReportCommand.cs` if the flag names below differ from reality; the user may need to adjust invocations.

Run:
1. Console: `dotnet run -- report`
2. Text:    `dotnet run -- report --output-text <path>`  (use the actual text-output flag in `ReportCommand.cs`)
3. Excel:   `dotnet run -- report --output-excel <path>` (use the actual excel-output flag in `ReportCommand.cs`)

Expected in each:
- Combined panel appears for each ticker with 2+ positions, positioned immediately after that ticker's individual panels.
- Title: `"<TICKER> Combined — <legs>"`.
- Details line: `"<N> positions · Earliest expiry <date> · DTE <n>"` (+ `"mixed expiries"` when applicable).
- Leg list shows all merged legs with prices and (if `--api` used) bid/ask/IV.
- Break-evens and max profit/loss reflect the *net* position across all positions on the ticker.
- If any merged option leg was shared across 2+ positions, its line ends with `"(merged from N positions)"`.

- [ ] **Step 7: Commit the interleaving change.**

```bash
git add ExcelExporter.cs
git commit -m "ExcelExporter: include combined breakeven panels"
```

---

## Self-review checklist (run before handoff)

- All seven tasks committed.
- No TBDs, TODOs, or placeholder comments left in code.
- `dotnet build` clean (0 warnings relevant to new code).
- Manual run on real data shows the combined panel for a ticker with 2+ positions; tickers with only 1 position show no combined panel.
- Combined panel math sanity-checks against manual computation for at least one ticker (pick a simple case — e.g., two long calls on the same underlying and strike; combined panel should equal a single long call with qty = sum).
