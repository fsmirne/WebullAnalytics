# Entry-Timing & Intraday-Signal Investigation

Follow-up to `LONG-PREMIUM-FINDINGS.md`. Goal: stop the long-premium misfires by deciding *when* to enter and fixing the directional signal that drives them. Real data, `--lots 1`, train=2025 / holdout=2026. XSP is the clean read (chain complete); SPXW is noisy here because its options backfill was running concurrently.

## 1. Fixed entry delay — REFUTED

Withholding the open until a fixed ET time (09:45 … 10:30). Monotonically *worse*, especially on the holdout:

| open-after | XSP tr | XSP ho | SPXW tr | SPXW ho |
|---|---|---|---|---|
| **09:30** | 1.29 | **1.35** | 1.26 | **1.48** |
| 09:45 | 1.29 | 1.20 | 1.13 | 1.09 |
| 10:00 | 1.30 | 1.16 | 1.20 | 1.18 |
| 10:15 | 1.22 | 0.99 | 1.20 | 0.94 |
| 10:30 | 1.12 | 0.94 | 1.11 | 0.81 |

Waiting pays theta and clips the *winners* (the days the move is real need the full session); that cost outweighs avoiding some flat-day losers. **09:30 stays.** A fixed time is arbitrary anyway — the right idea was an adaptive signal, which led to §2. (Knob `--open-after` / `opener.earliestEntryTimeEt` is committed but disabled-by-default research tooling — not in any recommended config.)

## 2. The intraday tape was reading pre/post-market — fixed, but it reveals the signal isn't the edge

**The bug:** `IntradayTapeIndicators` took `todaysBars[0].Open` as "today's open," but the intraday CSVs carry **04:00–17:25 ET**, so the anchor was a *pre-market* print (verified on 2026-05-29: CSV starts 08:00Z = 04:00 ET; the "open" was the 04:00 bar, not 09:30). Gap, open-to-now and VWAP were all computed off extended-hours data. The `includeExtended` config flag was *supposed* to control this but was a **no-op in the backtest** (the bar cache returns every CSV row regardless). Two layers: the flag didn't work, and the prod config had it set `true`.

**The fix:** the indicator now honors `includeExtended` — RTH-only (09:30–16:00 ET; prior RTH close → today's RTH open → now) when `false` (the default and the documented intent), legacy all-session when `true`.

**The finding (the important part):** a *correct, cleaner* directional signal makes the strategy **worse**, not better.

| XSP (clean) | train | holdout |
|---|---|---|
| baseline | 1.17 | 1.21 |
| RTH-only tape (`includeExtended=false`) | 1.19 | 1.25 |
| extended tape (`includeExtended=true`, prior overhaul) | 1.29 | 1.35 |

The pre-market noise was *accidentally protective*: it weakened the directional read, so the engine fired fewer directional longs (the loss source — see `LONG-PREMIUM-FINDINGS.md`). A sharper signal → more directional-long lottery tickets → lower PF and more opens. Sweeping the tape weight (0.0–0.65) and adding the long-conviction gate (0.4–0.8) on the corrected signal **never** recovered the prior PF on any cell. **This strategy's edge is not intraday direction prediction** — feeding it a better directional signal hurts, because the bias channels into long-premium selection.

Note: the extended signal is **not lookahead** — pre-market prints are real and available at 09:30 — so the higher PF is genuinely achievable. It's a signal *choice*, not a cheat.

## Decision

- **RTH-only is the default** (`includeExtended=false`), honoring "the tape should not use pre/post-market data." Recommended configs set it explicitly. Honest cost: ~0.10 PF on XSP vs the extended signal. Still above baseline on both windows.
- **`includeExtended=true` recovers the +0.10 PF** if you decide the pre-market drift is a signal you want to use — one flag.
- The prior `REPORT.md` overhaul numbers (XSP 1.29/1.35) were measured with the extended tape; the RTH-only honest numbers are 1.19/1.25.
- **Bigger lesson:** stop trying to improve the *directional* signal. The edge is in structure/gating (iron condor, minScore, no-assignment-penalty) and in *not* taking marginal directional longs. Future work should target the long-premium channel directly (e.g. position-sizing directional longs down, or a hard cap on directional-long frequency), not a better bullish/bearish/neutral classifier.

## 3. Backfill: stop re-pulling settled Webull contracts (separate fix)

`wa options backfill` re-fetched ~599 Webull-routable contracts every run with zero writes ("unchanged") — they were past-expiry (settled, final history) but still carried a `derivativeId`, and the Webull path lacked the expired-and-on-disk skip the massive path has. Fixed: a Webull contract that's already on disk with a past expiry is skipped (mirrors massive), unless `--force`. Today's/future expiries and not-yet-captured contracts still fetch. This eliminates the "500+ contracts, nothing written" churn; the catalog itself was already converging (discover union dedupes; 0 new on repeat runs).
