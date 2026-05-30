# Long-Premium Losses — Investigation & Verdict

**Question (user):** most backtest losses look like long call/put trades that "never materialize" (theta bleed). Identify those days and fix the scoring. Use 2026-05-29 (yesterday) live tape as ground truth.

## 1. Loss attribution (overhaul config, real data, `--lots 1`, 2026 holdout)

| structure | n | net $ | win% | gross loss | % of all losses |
|---|---|---|---|---|---|
| **XSP — LongCall** | 42 | +1,169 | 45% | −2,817 | **68%** |
| XSP — LongPut | 7 | −195 | 29% | −794 | 19% |
| XSP — IronCondor | 12 | +422 | 67% | −514 | 12% |
| **SPXW — LongCall** | 36 | +9,304 | 44% | −24,619 | **69%** |
| SPXW — IronCondor | 8 | −880 | 50% | −3,316 | 9% |

**The user is right about the loss source:** long calls/puts are ~68–69% of gross losses on both tickers. **But they are also net-positive** — the biggest winners too. It's a positive-skew lottery: ~44% win rate, winners bigger than losers.

## 2. What separates winning vs losing longs

Realized **open→close** move on the trade day:

| | winners | losers |
|---|---|---|
| XSP median \|open→close\| | 0.58% | 0.22% |
| SPXW median \|open→close\| | 0.57% | 0.18% |
| intraday range (hi−lo), both | ~0.9% | ~0.9% |

Losers are **not** quiet days — they have the same intraday range. They **chop and close near the open** (no directional follow-through). Long premium needs the *close* to travel; on ~half the days it reverts.

## 3. Why this is hard

At the **09:30 decision minute** (the live entry), winning and losing longs are **near-identical**: same ATM strike, same premium, same breakeven, same bias. The discriminator (does the close follow through?) is unknowable at entry, and the intraday tape needs ~5 minutes to form — so at 09:30 there is essentially only the multi-day macro bias + overnight gap. No 09:30-available feature cleanly separates the two populations.

Root cause in code: `ScoreLongCallPut` applies **no** breakeven/expected-move factor and hard-codes `popFactor = 1` (low POP is "the shape" of a long, by design). The long's only real driver is the **bias-drifted scenario EV** — `BuildScenarioGrid` shifts the grid by `bias × biasDrift × σ`, granting directional EV that is only real if the bias is predictive. On a flat day with a stale-but-strong daily bias, that EV is spurious → theta bleed.

## 4. 2026-05-29 (yesterday, near-complete live tape) — the irreducible case

SPXW opened a 7575 ATM LongCall (paid $1,327), spot went 7575 → ~7580 (+0.07%, dead flat), call decayed to $506 → **−$822**. At 09:30 the LongCall scored 0.086 — it cleared even the old 0.07 gate. The macro bias was **strongly bullish** (stale daily trend) on a day that reverted: **high but wrong conviction.** Verified: the long-conviction gate at *every* weight (0.4, 0.6, 0.8) still fires this exact LongCall and loses the same $822 — because the gate preserves strong-conviction longs. Only forcing full intraday weight made the LongCall score go negative (the flat tape neutralized the stale macro), but that setting wrecks the aggregate (XSP PF → 0.84). **This day cannot be fixed by 09:30 scoring.**

## 5. The fix that helps the aggregate — long-conviction gate

`opener.longConvictionGate` (new): multiplies a long-premium score by a factor that is 1.0 at strong trade-aligned bias and falls to `1 − weight` as aligned conviction → 0. It trims the **weak-conviction tail** of longs (lets credit/neutral cover those days) while keeping strong-conviction longs. Disabled by default (weight 0 → bit-identical to before; verified). CLI `--long-conviction`.

PF vs the overhaul (lc=0.0), all current-binary, real data:

| long-conviction | XSP train | XSP hold | SPXW train | SPXW hold |
|---|---|---|---|---|
| 0.0 (overhaul) | 1.29 | 1.35 | **1.23** | 1.48 |
| **0.4** | **1.35** | 1.34 | **1.29** | 1.44 |
| 0.6 | 1.34 | 1.36 | 1.21 | 1.53 |
| 0.8 | 1.30 | 1.34 | 1.24 | 1.52 |

**What it buys:** at weight 0.4 it lifts both *train* cells (XSP 1.29→1.35, SPXW 1.23→1.29) and — importantly — **removes the overhaul's one regression**: overhaul-alone leaves SPXW train at 1.23, *below* the 1.24 master baseline; with the gate it's 1.29, above baseline. All four cells then beat baseline. Cost: small holdout dips (XSP −0.01, SPXW −0.04), both still well above baseline.

**Honest caveats:**
- The effect is **near the noise floor**. SPXW-train response across weights is non-monotonic (0.4→1.29, 0.6→1.21, 0.8→1.24), so 0.4 being best is partly luck. XSP-train is smoother.
- It does **not** fix strong-conviction misfires (2026-05-29). It only trims the weak-conviction tail.
- It cuts only ~4–8 opens (the marginal longs) — most long losses are irreducible coin-flips.

## 6. The real lever (next investigation, out of scope here)

The misfires are a **09:30 timing problem**, not a scoring-weight problem: the entry happens before the tape forms. The highest-value fix is **execution** — delay long-premium entry to ~09:40–09:45 and require the intraday tape to confirm the macro direction before buying premium (kill the trade if the early tape is flat/opposing). That would have caught 2026-05-29. It changes the trade model (you trade at the open today), so it needs your sign-off before I build it.

## Recommendation

Ship the gate **code** (disabled by default — sound, available knob). Treat `longConvictionGate.weight = 0.4` as an **optional** add-on: it makes the overhaul beat baseline on all four cells by fixing the SPXW-train regression, but it's marginal and doesn't address the strong-conviction misfires that dominate the worst days. The bigger win is the execution-timing change in §6. Files: `ai-config.{XSP,SPXW}.gate.json` carry the overhaul + gate@0.4.
