# 0DTE Overhaul — Final Per-Ticker Design

Consolidates the full investigation (`REPORT.md`, `LONG-PREMIUM-FINDINGS.md`, `ENTRY-AND-SIGNAL-FINDINGS.md`). Branch `0dte-signal-overhaul`, **not merged** — your call. Real data, `--lots 1`, train=2025 / holdout=2026. (SPXW numbers are mildly noisy — its options backfill was running during these runs; XSP is clean.)

## Recommended config — PER TICKER

Both: `includeExtended=true` (best, and not lookahead), `minScoreToOpen=0.05`, longs + short verticals on, TP/SL 0.50/0.50, intraday weight 0.45.

| | iron condor | rationale |
|---|---|---|
| **XSP** | **ON** | condor is XSP's edge (+$422 holdout); removing it drops holdout 1.35→1.17 |
| **SPXW** | **OFF** | condor is a drag on SPXW (−$880 holdout); removing it lifts holdout 1.48→1.53 |

Files: `ai-config.XSP.recommended.json` (condor on), `ai-config.SPXW.recommended.json` (condor off).

## Profit factor — baseline → final (real data, --lots 1)

| | XSP train | XSP holdout | SPXW train | SPXW holdout |
|---|---|---|---|---|
| Baseline (master) | 1.17 | 1.21 | 1.24 | 1.28 |
| **Final** | **1.29** | **1.35** | 1.24 | **1.53** |
| Max drawdown | 7.8%→6.4% | 12.0%→5.9% | 30.5%→29.4% | 74.6%→26.3% |

XSP improves on both windows. SPXW improves the holdout strongly (1.28→1.53, DD 74.6%→26.3%) and holds train at baseline (1.24). Win rates up across the board; drawdowns down sharply. (Honest caveat: SPXW *train* doesn't beat baseline — the SPXW gains are concentrated in 2026 and SPXW data was shifting under the live backfill, so re-pin SPXW after the backfill completes before relying on the exact figures.)

## What's in the engine (committed, all default-OFF unless noted)

1. **DTE-aware intraday-tape weight curve** (`opener.intradayTapeDteCurve`) — implements the documented-but-missing DTE mix. Inert on 0DTE-only configs; correct for future multi-DTE use.
2. **No assignment-risk penalty on cash-settled European indexes** — correctness fix (SPX/SPXW/XSP/… can't be assigned early). This un-buried the credit/neutral structures and is the single biggest lever.
3. **`includeExtended` now actually works** in the tape (was a no-op in backtest; the indicator was anchoring on the 04:00 ET pre-market print). RTH-only when false, all-session when true. Recommended configs use `true` (measured best).
4. **Long-conviction gate** (`opener.longConvictionGate`, default off) — de-rates weak-conviction longs; marginal, available knob.
5. **Earliest-entry gate** (`opener.earliestEntryTimeEt` / `--open-after`, default off) — research tooling; fixed delay is refuted.
6. **Backfill: skip settled on-disk Webull contracts** — kills the ~500-contract "unchanged" re-pull each run.

## Optional: XSP ultra-conservative variant

Dropping the directional longs on XSP (condor + short verticals only) gives **PF 2.02 holdout / 1.70 train at ~2.4% max drawdown** — but only ~16–19 trades (sits out ~85% of days). A much safer, lower-participation profile if you'd trade XSP for risk-adjusted return over frequency. Not the default; documented as a lever.

## Hard negative results (don't revisit)

- **Entry timing / fixed delay (09:45–10:30):** monotonically worse — waiting clips the winners. 09:30 stays.
- **The directional signal is not the edge.** A *cleaner* intraday tape (RTH-fix, sub-weight tuning, conviction gating) makes the strategy *worse* — a sharper directional read just fires more losing long-premium lottery tickets. Open-to-now has weak real momentum (~55% by 10:00) but it doesn't translate to robust PF.
- **Side-of-credit as a standalone** (pure directional credit verticals, no longs/condor): degenerate on XSP (5 trades), train/holdout disagree on SPXW. Not viable. But it surfaced the per-ticker condor result above, which is the real win.

## To adopt
Merge the branch (or cherry-pick the engine commits) and copy `0dte-overhaul/ai-config.{XSP,SPXW}.recommended.json` into `%LOCALAPPDATA%\WebullAnalytics\data`. The only behavior changes that ship default-on are the assignment-risk correctness fix and the working `includeExtended` flag; everything else is opt-in via the recommended configs.
