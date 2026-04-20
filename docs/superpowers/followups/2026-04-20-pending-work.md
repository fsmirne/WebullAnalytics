# Pending Follow-up Work

**Created:** 2026-04-20 (end of day)
**Next session:** pick any item to continue.

Ordered roughly by "unlocks the most value" first.

---

## 1. Wire `ProfitProjector` to `BreakEvenAnalyzer`

**Impact:** Re-enables `TakeProfitRule` so `ai once` can propose closing when MTM has captured a configured fraction of projected max profit. Currently the rule is disabled-by-default because `ProfitProjector.MaxForCurrentColumn` returns `null`.

**Where:** `AI/ProfitProjector.cs` is a stub. Needs to call `BreakEvenAnalyzer` to build the time-decay grid for the position and return the max value in today's date column.

**Complication:** `BreakEvenAnalyzer.AnalyzeGroup` is tightly coupled to `ReportCommand`'s pipeline (takes `PositionRow[]`, `AnalysisOptions`, etc.). Cleanest fix: extract an `AnalyzeBareLegs(legs, quotes, spot, iv, riskFreeRate)` overload that takes raw inputs and returns `BreakEvenResult` (including the grid). Then `ProfitProjector` calls it with `OpenPosition`-derived inputs.

**Once done:** re-enable `takeProfit` in `ai-config.json` defaults (currently `enabled: false`).

---

## 2. Historical price source for `ai replay`

**Impact:** `ai replay` currently can't price historical option legs because Yahoo's `v7/finance/download` endpoint returns 401 without auth. Users have to manually drop CSVs into `data/history/<TICKER>.csv`.

**Where:** `YahooOptionsClient.FetchHistoricalClosesAsync` (hits the 401), `AI/Replay/HistoricalPriceCache.cs` (reads CSVs, already accepts both 2-col and Yahoo 7-col formats).

**Options, best to worst:**
- **(a) Stooq free tier** — tried once, also requires API key now. Not viable without signup.
- **(b) Alpha Vantage free tier** — 25 requests/day, 5/minute. Viable for small tickers, needs API-key config.
- **(c) Polygon.io free tier** — 5 requests/minute, 2-year history. Best free option if the user accepts adding a new credential.
- **(d) Automate Yahoo via browser-captured cookies** — similar to how `sniff` handles the session API. Fragile.
- **(e) Keep the manual-CSV workflow** — document better, done.

Recommend (c) or (b). Adds one new config field (API key) and a new `IHistoricalPriceSource` implementation behind a feature flag.

---

## 3. Live interactive placement for `ai watch` (phase 2)

**Impact:** Currently `ai watch` is log-only. Proposals land in `data/ai-proposals.log`; you have to read them and run `trade place --submit` yourself. Phase 2 of the original spec was to add a `--interactive` flag that pauses on each proposal with a `[y/N]` prompt and submits via the existing `trade` pipeline on approval.

**Where:** `AI/WatchLoop.cs` (proposal handling), `AI/Output/ProposalSink.cs` (currently just logs). Add `ProposalSink.InteractivePromptAsync` that renders the proposal, asks `[y/N]`, converts `ManagementProposal.Legs` into the `--trades` syntax, and calls `TradePlaceCommand` internals.

**Complications:**
- `TradePlaceCommand` expects a CLI invocation, not programmatic. Extract its place-order logic into `TradeContext.PlaceOrderAsync(legs, limit, account)` that both the CLI and `ai watch --interactive` can call.
- Proposal's `NetDebit` is per-contract-of-total; the trade-place call needs the actual per-order limit. Be careful with sign conventions (credit vs debit).
- Partial-scenario proposals have an `ExecutedQty` that differs from the position's total `Qty`. Currently `ProposalLeg.Qty` reflects the executed quantity for partials, but double-check.

Add `--interactive` flag to `AIWatchSettings` and `AIOnceSettings`. Default off.

---

## 4. Project reorg (separate spec)

**Impact:** Repo now has 40+ flat files plus a growing `AI/` folder. A reorg would move existing files into `Commands/`, `Api/`, `Parsing/`, `Options/`, `Reporting/`, `Strategy/`, `Core/` subfolders, updating namespaces accordingly.

**Status:** Agreed during brainstorming to do as a separate spec before `AI/` grew, but never got specced. Now `AI/` is large and mixed-into-flat is harder to reverse.

**Where:** Pure mechanical file moves + namespace updates + csproj adjustments. No behavior change. One large PR that touches almost every file.

**Consider deferring** — the project works fine flat. Only do this when the mental cost of navigating 50+ root files exceeds the cost of the refactor.

---

## 5. Optional: auto-execute mode for `ai watch` (phase 3)

**Impact:** Beyond interactive — true auto-execution with safety caps (max proposals/day, max $ per action, kill-switch file, daily-P&L circuit breaker).

Only tackle after phase 2 (interactive) has been used in production for a while and the proposal quality is proven. Too risky to jump straight from log-only to full automation.

---

## 6. Minor: validate `ai replay` agreement heuristic

**Impact:** `ReplayRunner.ClassifyAgreement` currently uses a phase-1 heuristic (any same-day trade on the same ticker = "partial"). Real analysis would compare the rule's proposed legs against the actual executed legs and classify as match / partial / miss / divergent.

**Where:** `AI/Replay/ReplayRunner.cs:ClassifyAgreement`. Enrich with structural comparison.

Not urgent — replay is only valuable once #2 (historical prices) is working.

---

## 7. Minor: Webull `tradeapi.webullbroker.com` probe

**Impact:** During auth debugging we discovered a separate Webull API tier at `tradeapi.webullbroker.com` that routes differently. We didn't investigate further because `api.webull.com` (with the non-2FA key) works. But the sandbox test accounts map to `us-openapi-alb.uat.webullbroker.com`, so there's a URL inconsistency worth understanding.

Only worth revisiting if a future feature needs access to something not on `api.webull.com`.

---

## 8. Minor: `analyze position` → `ScenarioEngine` cleanup

**Impact:** When we added `ScenarioEngine` as a shared class, we left the duplicated scenario logic in `AnalyzePositionCommand.cs` untouched to avoid risk. The two copies are currently in sync but will drift.

**Where:** `AnalyzePositionCommand.cs` (`GenerateScenarios`, `GenerateSpreadScenarios`, `EmitFullAndPartial`, all the helpers). Refactor to call `ScenarioEngine.Evaluate` and adapt the output back to its rendering types.

Pure refactor — no behavior change expected.

---

## Open questions to answer before tackling the above

- **For #3 (interactive `--interactive`):** do you want the prompt on every proposal, or only when `cashReserveBlocked == false`? Rolling into a position you can't fund is a silent bug waiting to happen.
- **For #1 (TakeProfit wiring):** what's a sensible threshold — 40% of max-projected-profit (the documented default), or something tighter like 60%? Your trade history shows you often closed calendars at ~70%+ captured.
- **For #2 (historical prices):** are you willing to add a free API key (Polygon/Alpha Vantage) to `config.json`, or prefer to stay credential-free and keep the manual-CSV workflow?
