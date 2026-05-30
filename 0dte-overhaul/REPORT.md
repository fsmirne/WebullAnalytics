# 0DTE Signal Overhaul — Results & Recommendation

**Branch:** `0dte-signal-overhaul` (do **not** merge to master without review — user decides).
**Date:** 2026-05-30. **Data:** real options (XSP 349 days, SPXW 352 days, 2025-01-02 → 2026-05-29).
**Method:** `wa ai backtest <T> --lots 1` (sizing-neutral, measures per-trade edge). Train = 2025, holdout = 2026. A change had to improve **both tickers** and survive the **2026 holdout** to count.

## TL;DR

The 0DTE flat-day gap was two real problems plus one latent bug:

1. **A false penalty.** Cash-settled European index options (SPX/SPXW/NDX/XSP/…) carry **zero early-assignment risk**, yet the scorer applied an assignment penalty (~0.10–0.21×) to their short-leg structures — burying every flat-day premium trade far below `minScoreToOpen`. **Fixed** (correctness, not tuning).
2. **The gate was mis-calibrated.** `minScoreToOpen 0.07` is effectively a directional-conviction gate; defined-risk credit/neutral structures rarely clear it. Lowering to **0.05** is a clean train-set optimum for both tickers (a peak, not an edge).
3. **No neutral structure was enabled.** Enabling a **0DTE iron condor** gives the engine a profitable flat-day trade. (The iron *butterfly* was tested and **rejected** — its short straddle gets run over; SPXW drawdowns blew out to 200%+.)

The DTE-aware intraday-tape weighting (the documented-but-unimplemented "DTE-weighted mix") was also built. It is **correct and general** but **inert for the current 0DTE-only configs** (at a single DTE the curve equals the flat weight), so it is shipped disabled-by-default and is not part of the recommended 0DTE config.

## Recommended configuration (per-ticker override, XSP & SPXW)

```
opener.minScoreToOpen: 0.07 → 0.05
opener.structures.ironCondor: { enabled: true, dteMin: 0, dteMax: 0,
    widthSteps: [2,4], bodyWidthSteps: [2,4,8], shortDeltaMin: 0.10, shortDeltaMax: 0.25 }
```
Unchanged: `weights.intradayTape 0.45`, `weights.biasDrift 1.0`, realizedExpectancy TP/SL `0.50/0.50`.
Ready-to-apply files: `ai-config.XSP.recommended.json`, `ai-config.SPXW.recommended.json`.

## Before / after (real data, `--lots 1`; deliverable config, no CLI overrides)

Profit factor:

| | XSP train | XSP holdout | SPXW train | SPXW holdout |
|---|---|---|---|---|
| **Baseline** (master) | 1.17 | 1.21 | 1.24 | 1.28 |
| **Overhaul** | **1.29** | **1.35** | **1.30** | **1.47** |

Full metrics (baseline → overhaul):

| | XSP train | XSP holdout | SPXW train | SPXW holdout |
|---|---|---|---|---|
| Profit factor | 1.17 → 1.29 | 1.21 → 1.35 | 1.24 → 1.30 | 1.28 → 1.47 |
| Expectancy/trade | $10.83 → $16.81 | $14.87 → $23.00 | $159.68 → $140.05 | $210.49 → $191.66 |
| Opens | 102 → 127 | 49 → 63 | 118 → 194 | 52 → 89 |
| Win rate | 41.2% → 45.7% | 38.8% → 47.6% | 42.4% → 45.4% | 44.2% → 53.9% |
| Max drawdown | 7.8% → 6.4% | 12.0% → 5.9% | 30.5% → 30.6% | 74.6% → 20.4% |

Both tickers improve PF on **both** windows. Win rate rises across the board and max drawdown drops sharply (SPXW holdout 74.6% → 20.4%) — the gain comes from better trade selection and more frequent defined-risk trades, not from taking more risk, so trending days are not degraded. SPXW expectancy/trade falls slightly because the strategy now trades ~70% more often (lower gate + condor) at a smaller but steadier per-trade edge — higher PF, far lower drawdown, higher total P&L.

## Why not the higher-PF-on-holdout cells?

`ic + ms=0.07` scores 1.54/1.49 on the holdout — higher than the recommendation — but **fails the train set** (1.15/1.11, below baseline). Selecting it would be fitting the holdout. The recommended `ms=0.05` is the train optimum *and* holds on the holdout, which is the robust choice. `ms` plateau (train, ic): 0.03→{1.23, 1.01}, 0.04→{1.23, 0.97}, **0.05→{1.29, 1.31}**, 0.06→{1.16, 1.21}, 0.07→{1.15, 1.11} (XSP, SPXW).

## Code changes (committed to branch)

1. `fcbd402` — DTE-aware intraday-tape blend weight (`opener.intradayTapeDteCurve`, `--intraday-w0`). Disabled by default; bit-identical to legacy when off (verified). Implements the mix documented on `IntradayBias`.
2. `3b6e94f` — no assignment-risk penalty on cash-settled European indexes; adds `OptionSettlement` as the single source of truth (backtest fee set now references it).

## Rejected / negative findings

- **Iron butterfly at 0DTE**: catastrophic on SPXW (DD 200%+); short straddle has no room. Out.
- **Raising intraday-tape weight** (w0 → 0.65–1.0): the tape is itself directional, so it swaps macro-direction for tape-direction rather than neutralizing — PF *fell* (XSP 0.84–0.88). 0.45 is optimal.
- **Looser TP/SL scoring** (0.75/0.75, 1.0/1.0): destroys trade selection (train PF 0.2–0.3). Keep 0.50/0.50.
- **Assignment fix alone** (no gate/structure change): big SPXW holdout win (1.62) but SPXW *train* regressed (1.24→1.13) — needed the `ms=0.05` + condor combo to improve both windows.
