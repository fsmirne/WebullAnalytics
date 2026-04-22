# `ai` Command Design

**Date:** 2026-04-19
**Status:** Proposed

## Summary

Add a new `ai` command to WebullAnalytics that continuously monitors open option positions during the market session and emits structured **proposals** (roll / take-profit / stop-loss / defensive-roll) to a log, based on a configurable rules engine. The command is read-only in phase 1: it never places orders, it only writes proposals the user reviews manually.

The feature ships as three subcommands sharing one evaluation engine:

- `ai watch` — foreground loop, ticks every N seconds during market hours until end-of-window or Ctrl-C.
- `ai once` — single evaluation pass; prints proposals and exits.
- `ai replay` — evaluate the rules against historical `orders.jsonl` data to validate how the rules would have behaved on past trades.

The rule set targets multi-day managed calendar and diagonal positions — short leg 2–7 DTE, long leg 2–8 weeks out, held 1–3 days and managed by rolling the short leg, taking profit when short-leg decay is captured, or cutting loss on adverse underlying moves. Short-dated directional verticals and 0DTE index patterns are explicitly out of scope for phase 1. The feature is ticker-agnostic: the user configures which tickers to monitor in `ai-config.json`.

## Goals / Non-goals

**Goals (phase 1):**
- Dry-run monitor for open calendar/diagonal positions on any user-configured tickers.
- Four management rules: `StopLoss`, `TakeProfit`, `DefensiveRoll`, `RollShortOnExpiry`.
- Structured JSONL proposal log + human-readable console output.
- Config-driven thresholds via `data/ai-config.json`.
- Replay against historical fills with side-by-side comparison of "what the rules would have proposed" vs "what was actually done."
- Informational cash-reserve tag on proposals that would violate a configured cash reserve.
- Reuse existing code: `PositionTracker`, `OptionMath`, `BjerksundStensland`, `YahooOptionsClient`, `WebullOptionsClient`, `WebullOpenApiClient`, `TimeDecayGridBuilder`, and the margin/funding model from `analyze roll`.

**Non-goals (phase 1):**
- No interactive placement (no `[y/N]` prompt, no path from the loop to the broker).
- No auto-execute.
- No entry proposals (no option-chain scanning).
- No IV-regime gating on rules.
- No notifications (desktop / push / Slack).
- No per-ticker rule overrides (all configured tickers share the same rule thresholds).
- No short-dated directional verticals, no 0DTE index patterns. Focus is on multi-day managed calendar/diagonal structures.
- No remote control / web UI.
- Rules apply symmetrically to calls and puts by construction (strike-aware logic), but put-specific threshold tuning is phase 2.

## Relationship to Existing Commands

`ai` is a new peer of `report`, `analyze`, `fetch`, `sniff`, and `trade`:

- **Reuses** the `PositionTracker`, strategy grouping, Black-Scholes, and margin model already used by `report` and `analyze`.
- **Shares** the quote fetchers (`YahooOptionsClient`, `WebullOptionsClient`) with `report` and `analyze`.
- **Reads from** Webull OpenAPI (the same authenticated path `trade` uses) for live position state.
- **Does not call** `trade` in phase 1. The separation is deliberate: phase 1 has no code path from the loop to the broker.

## Command Surface

```
wa ai watch   [options]                — foreground loop
wa ai once    [options]                — single pass, print, exit
wa ai replay  [options]                — historical replay
```

### Shared options (all subcommands)

| Option | Description |
|---|---|
| `--config <path>` | Path to `ai-config.json`. Default: `data/ai-config.json`. |
| `--tickers <list>` | Override config tickers (comma-separated). |
| `--output <format>` | `console` or `text`. Default: `console`. |
| `--output-path <path>` | Required with `--output text`. |
| `--api <source>` | Override `quoteSource` — `webull` or `yahoo`. |
| `--verbosity <level>` | `quiet`, `normal`, `debug`. Overrides config. |
| `--help`, `-h` | Show help. |

### `ai watch` options

| Option | Description |
|---|---|
| `--tick <seconds>` | Override `tickIntervalSeconds`. |
| `--duration <duration>` | Stop after N minutes/hours (e.g., `6h`, `90m`). Default: until market close. |
| `--ignore-market-hours` | Run regardless of clock (for testing). |

### `ai once` options

Inherits shared options only. No tick / duration.

### `ai replay` options

| Option | Description |
|---|---|
| `--since <date>` | YYYY-MM-DD. Default: earliest fill. |
| `--until <date>` | YYYY-MM-DD. Default: latest fill. |
| `--granularity <level>` | `daily` or `hourly`. Default: `daily` at 15:45 ET. |

## Architecture

### New `AI/` subfolder

Per the accompanying project-reorg spec, all new files live under `AI/`. The folder name uses Microsoft's two-letter-acronym convention (`AI` all-caps; contrasts with `Api`, `Csv`, etc., which stay PascalCase).

```
AI/
  AICommand.cs                     — Spectre.Console.Cli entry + subcommand dispatch
  AIConfig.cs                      — config model parsed from ai-config.json
  EvaluationContext.cs             — per-tick snapshot: positions, quotes, clock, cash
  ManagementProposal.cs            — structured output of any rule
  WatchLoop.cs                     — timer/tick mechanics, market-hours gating, shutdown
  Replay/
    ReplayRunner.cs                — walks history, rebuilds state, emits comparison report
    IVBackSolver.cs                — back-solves IV from historical fills for pricing
    HistoricalPriceCache.cs        — disk-cached daily closes from Yahoo
  Rules/
    IManagementRule.cs
    StopLossRule.cs
    TakeProfitRule.cs
    DefensiveRollRule.cs
    RollShortOnExpiryRule.cs
    ProposalFingerprint.cs         — idempotency dedup key
  Sources/
    IPositionSource.cs
    LivePositionSource.cs          — wraps WebullOpenApiClient
    ReplayPositionSource.cs        — rebuilds PositionTracker from orders.jsonl
    IQuoteSource.cs
    LiveQuoteSource.cs             — wraps YahooOptionsClient / WebullOptionsClient
    ReplayQuoteSource.cs           — Black-Scholes + IVBackSolver
  Output/
    ProposalSink.cs                — writes console + JSONL log
    ReplayReportRenderer.cs        — formats the replay comparison block
```

Files use the MS 2-letter acronym convention: `AICommand` and `AIConfig` keep `AI` uppercase; the folder name `AI/` matches. All class namespaces are `WebullAnalytics.AI` (with sub-namespaces `WebullAnalytics.AI.Rules`, `WebullAnalytics.AI.Sources`, etc., following folder structure).

### Per-tick pipeline

```
┌──────────────┐   ┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ Position     │ → │ Market data  │ → │ Rule         │ → │ Proposal     │
│ source       │   │ source       │   │ evaluator    │   │ sink         │
└──────────────┘   └──────────────┘   └──────────────┘   └──────────────┘
```

The evaluator consumes an `EvaluationContext` (positions, quotes, clock, cash) and returns `ManagementProposal[]`. The evaluator is identical in live and replay modes; only the source implementations differ. This allows replay to validate the exact same rule logic that will run live.

## Rule Set

Rules are evaluated per-position per-tick in priority order. The first rule to match for a given position produces a proposal; lower-priority rules are skipped for that position for that tick.

### Priority 1 — `StopLossRule`

- **Trigger:** position mark-to-market debit ≥ `maxDebitMultiplier` × initial debit, OR spot beyond the adjusted break-even by > `spotBeyondBreakevenPct`.
- **Action:** propose flat-close at current mid.
- **Rationale:** losses on calendar/diagonal positions typically compound when held through sustained adverse moves past break-even; an early, bounded stop prevents a single unfavorable move from dominating the period's P&L.

### Priority 2 — `TakeProfitRule`

- **Trigger:** position mark ≥ `pctOfMaxProfit` × max-projected-profit from the current-date column of the time-decay grid (via the existing `TimeDecayGridBuilder`).
- **Action:** propose flat-close at current mid.
- **Rationale:** calendar/diagonal winners typically realize most of their profit when short-leg theta decay is largely captured but before short-leg expiry creates roll or assignment risk. A percentage-of-max-profit rule fires in that zone and is directly comparable to the grid the user already visually scans via `report` and `analyze`.

### Priority 3 — `DefensiveRollRule`

- **Trigger:** spot within `spotWithinPctOfShortStrike` of short strike AND short DTE ≤ `triggerDTE`.
- **Action:** propose roll up-and-out to next weekly expiry at a strike stepped away from the threatened side by `strikeStep` dollars (e.g., for a threatened short call, the new short strike = current short strike + `strikeStep`).
- **Constraint:** the proposed roll must improve at least one of: produce a credit, reduce max-loss, OR reduce position delta. If no candidate satisfies any of these, the proposal is tagged `no-better-alternative` and written to the log with that rationale.

The rules are strike-aware by construction and apply symmetrically to call-side and put-side positions (a threatened short put triggers the same `DefensiveRollRule` with strikes stepped in the opposite direction). Put-specific tuning (different thresholds for puts vs calls) is deferred to phase 2.

### Priority 4 — `RollShortOnExpiryRule`

- **Trigger:** short DTE ≤ `triggerDTE` AND short mid ≤ `maxShortPremium` AND no higher-priority rule matched.
- **Action:** propose roll to next weekly, same strike.
- **Constraint:** roll net credit ≥ `minRollCredit`.

### Meta behavior: cash-reserve tag

Every proposal is funding-checked using the same math that backs `analyze roll --cash`. The implementation plan will extract that logic into a shared helper so both `AnalyzeCommand` and `AICommand` consume it; this is a mechanical refactor with no behavior change to `analyze`. Proposals that would leave free cash below the configured reserve get a `⚠ blocked by cash reserve ($Y free, requires $X)` tag. The proposal is still written to the log; the tag is informational in phase 1, not a gate (since no execution happens).

### Idempotency

Each proposal carries a fingerprint `(ruleName, positionKey, structurallyMaterialParams)`. On consecutive ticks, identical fingerprints with no material change (≤ $0.02 drift in proposed net, no position delta) are written to the JSONL log but suppressed from console output at `normal` verbosity. This prevents tick-spam while preserving an audit trail.

## Config Schema

New file: `data/ai-config.json`. Mirrors the pattern of `trade-config.json` — feature-specific, separate from `config.json`.

The `tickers` field has no baked-in default — it must be explicitly set in `ai-config.json` or supplied via `--tickers`. This keeps the feature ticker-agnostic and prevents silent assumptions about which symbols to monitor.

```json
{
  "tickers": ["GME"],
  "tickIntervalSeconds": 60,
  "marketHours": {
    "start": "09:30",
    "end": "16:00",
    "tz": "America/New_York"
  },
  "quoteSource": "webull",
  "positionSource": {
    "type": "openapi",
    "account": "default"
  },
  "cashReserve": {
    "mode": "percent",
    "value": 25
  },
  "log": {
    "path": "data/ai-proposals.log",
    "consoleVerbosity": "normal"
  },
  "rules": {
    "stopLoss": {
      "enabled": true,
      "maxDebitMultiplier": 1.5,
      "spotBeyondBreakevenPct": 3.0
    },
    "takeProfit": {
      "enabled": true,
      "pctOfMaxProfit": 40
    },
    "defensiveRoll": {
      "enabled": true,
      "spotWithinPctOfShortStrike": 1.0,
      "triggerDTE": 3,
      "strikeStep": 0.50
    },
    "rollShortOnExpiry": {
      "enabled": true,
      "triggerDTE": 2,
      "maxShortPremium": 0.10,
      "minRollCredit": 0.05
    }
  }
}
```

### Field semantics

- **`tickers`** — required. User-configured list of symbols to monitor. No default; an empty list or missing field is a startup error.
- **`tickIntervalSeconds`** — watch-loop tick period. Ignored by `once` and `replay`.
- **`marketHours`** — watch loop sleeps outside this window unless `--ignore-market-hours`.
- **`quoteSource`** — `webull` (default; richer IV data) or `yahoo` (free, session-stable fallback).
- **`positionSource.type`** — `openapi` for live; `jsonl` only valid under `ai replay`. `account` picks an entry from `trade-config.json`.
- **`cashReserve.mode`** — `percent` of total account value, or `absolute` dollar amount. Informational in phase 1.
- **`log.path`** — persistent JSONL log for machine parsing.
- **`log.consoleVerbosity`** — `quiet` / `normal` / `debug`.
- **`rules.*`** — one entry per rule. `enabled` plus rule-specific thresholds. Unknown rule keys log a warning and are ignored (forward compat).

### Resolution rules

- Missing `ai-config.json` → the `tickers` field is required, so missing config is a startup error with guidance to copy `ai-config.example.json`. Rule defaults apply when the file exists but omits rule sections.
- Missing rule key → that rule is enabled with defaults.
- Missing threshold field inside a rule → default applied.
- Invalid value (e.g., `triggerDTE: -1`, `pctOfMaxProfit: 150`) → fail fast at startup with the field name and valid range.

### Example file shipped

`ai-config.example.json` ships with every default explicitly written out as documentation. Users copy and trim.

## Replay Mechanics

Replay walks historical trade data and evaluates the same `IManagementRule[]` pipeline at each time step, producing a report that compares rule proposals against actual user actions.

### Time discretization

- Default: **daily at 15:45 ET** — 15 minutes before close, when most of the user's historical actions cluster.
- `--granularity hourly` available for higher resolution on days with multiple trades.

### Position reconstruction

At each step, rebuild `PositionTracker` state from all fills up to that instant. Reuses the existing `report` pipeline — no new parsing code.

### Quote synthesis

For each open position at each step, price every leg via Black-Scholes:

- **Underlying price:** daily close for the day, fetched from Yahoo via a new `HistoricalPriceCache` that lazily populates `data/history/<ticker>.csv` on first read and reuses the cache thereafter.
- **Strike, expiry, type:** from the position.
- **Risk-free rate:** reuse existing 13-week T-bill fetch.
- **IV:** `IVBackSolver` back-solves from the nearest real fill for that OCC symbol (same strike, expiry, type; closest timestamp). Between fills, IV is piecewise-constant. When no fill within 30 days exists for a symbol, the leg is priced at intrinsic and the proposal output flags the approximation.

This IV-anchoring is the core trick: we don't have historical option chains, but the user's fills calibrate IV on exactly the strikes they traded.

### Replay output

`ai replay` renders three blocks:

1. **Disclaimer** — explicit limitations (synthetic quotes, daily granularity, no bid/ask, commissions modeled).
2. **Proposal log** — chronological list of every proposal the rules would have emitted, with timestamp, ticker, position key, rule name, structural params, rationale, and cash-reserve tag if applicable.
3. **Agreement comparison** — for each day where rules fired AND the user actually traded that day/position, a side-by-side:
   - `Rule would have proposed: ...`
   - `User actually did: ...`
   - `Agreement: match | partial | miss | divergent`
4. **Summary** — counts per rule and match/divergent totals. Full counterfactual P&L ("what if you'd followed every proposal exactly") is deferred to phase 2 because it requires replaying divergent position paths, not just scoring fills. Phase 1 reports only agreement statistics.

## Error Handling

### Startup

- Config validation failure → print field + bound, exit non-zero.
- Missing OpenAPI credentials (watch/once) → print which `trade-config.json` account is needed, exit.
- Stale Webull headers (when `quoteSource: webull`) → exit with guidance to run `sniff`.

### Runtime (watch loop)

- Transient network failure on a tick → log error, skip tick, continue.
- **Circuit breaker:** N consecutive failures (default 5) → exit non-zero with diagnostic.
- Persistent auth failure (401/403) → exit immediately; don't burn rate limits retrying.
- Individual rule throws → log exception, skip that rule for that tick, continue. One bad rule does not kill the loop.
- SIGINT / Ctrl-C → flush log, print final summary (ticks run, proposals emitted, errors), exit 0.

### Replay

- Missing historical price (market holiday, data gap) → skip step with debug-level note; not an error.
- IV back-solve failure (deep ITM or no fill within 30 days) → flag in replay output, price at intrinsic, continue.

## Logging Format

Persistent log at `data/ai-proposals.log` is JSONL, one proposal per line:

```json
{
  "ts": "2026-04-19T14:30:00-04:00",
  "rule": "RollShortOnExpiry",
  "ticker": "GME",
  "positionKey": "GME_Calendar_25_20260515",
  "proposal": {
    "type": "roll",
    "legs": [...],
    "netDebit": -0.08
  },
  "rationale": "short mid 0.06 ≤ threshold 0.10, DTE 1",
  "cashReserveBlocked": false,
  "mode": "watch"
}
```

Console output is Spectre-formatted to match the visual style of `analyze roll` — colored tables, per-proposal headers, position-grouped blocks.

## Testing Strategy

The project does not have a test project today (per the `trade` spec). This design preserves pure-function seams so tests can be added later without refactoring:

- **Unit tests (future)** — each rule with synthetic `EvaluationContext` fixtures. Tests cover: trigger conditions, threshold boundaries, priority ordering, idempotency fingerprinting.
- **Integration tests (future)** — full pipeline with in-memory `IPositionSource` and `IQuoteSource` stubs.
- **Replay as self-test** — running `ai replay` against committed `data/orders.jsonl` is itself a smoke test: the rules must produce non-empty output and no crashes.

No broker integration tests in phase 1 — there is no path to the broker.

## Dependencies

### Sequencing

This spec depends on a separate prerequisite spec: **project reorganization** that moves existing files into the `Commands/`, `Api/`, `Parsing/`, `Options/`, `Reporting/`, `Strategy/`, and `Core/` subfolders. The reorg lands first as a pure mechanical move (namespaces updated, no behavior change). The `ai` command then adds its `AI/` subfolder alongside.

### New runtime dependencies

None. This design reuses existing dependencies (`Spectre.Console`, `Spectre.Console.Cli`, existing HTTP clients for Yahoo and Webull).

## Phase 2+ (out of scope)

Explicit roadmap for later specs:

- **Interactive placement** — `--interactive` flag; [y/N] prompt; approved proposals routed through the existing `trade` pipeline.
- **Auto-execute** — `--auto` with safety caps (max proposals per day, max $ per action, kill switch file, daily P&L circuit breaker).
- **Entry proposals** — option-chain scanner that proposes new calendar/diagonal openings based on IV regime and spot position relative to strike grid.
- **IV-regime gating** — only fire entry/roll rules when IV/HV ratio passes a configured band.
- **Multi-ticker rule overrides** — `tickerOverrides` block for per-ticker thresholds.
- **Notifications** — desktop / push / Slack delivery of proposals.
- **Hybrid LLM layer** — rules enumerate candidate actions; LLM ranks and filters; human or threshold approves. The eventual "AI" in the command name.
