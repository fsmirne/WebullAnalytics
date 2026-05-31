# Mean-Reversion Entry & Put-Credit-Only — Investigation & Verdict

Follow-up to `ENTRY-TIMING-CEILING.md`. After timing ideas were exhausted, two structural hypotheses were tested. Both **refuted**. Real data, `--lots 1`, train 2025 / holdout 2026, SPXW + XSP.

## 1. Intraday signal predictiveness (RSI / Bollinger), 2026

Raw underlying forward returns after intraday extremes:
- **Oversold dip** (RSI<30 & below lower BB): forward-to-close +0.059% (vs +0.026% baseline), 56% up. A *real but tiny* mean-reversion bounce.
- **Overbought ceiling** (RSI>70 & above upper BB): price *continues up* (+0.043%, only 43% down). In an up-trend regime, fading rallies is backwards.

So the symmetric "calls on dips / puts on ceilings" long-options idea is dead: the put/ceiling side is wrong, and the call/dip edge (+0.06%, 56%) is far too small to overcome 0DTE long-call theta.

## 2. Dip-triggered put-credit spread (built as `opener.entryTrigger`) — REFUTED

Hypothesis: sell a put-credit spread *on the dip* (richer credit from the vol spike), ride the bounce. Prototyped as an engine entry-trigger (RSI/Bollinger dip gate → put-credit short vertical only) **and then reverted** — it's not in the codebase. (Note: the engine already has RSI at the daily-bar level in `TechnicalIndicators`/`TechnicalBias`; it has no MACD and no intraday drawdown-from-session-high signal — those lived only in throwaway analysis scripts.)

Result vs entering the same put-credit spread at the open ("SV"):

| | SV @open | DIP trigger |
|---|---|---|
| SPXW train | PF 5.72 / $13,978 | PF 2.74 / $6,618 |
| SPXW holdout | PF 5.04 / $6,689 | PF 4.46 / $2,919 |
| XSP train | PF 1.26 / $384 | PF 0.94 / −$72 |
| XSP holdout | PF 2.50 / $609 | PF 0.52 / −$327 |

**Worse on every ticker and window.** The dip trigger is *backwards* for credit spreads: a put-credit spread wants calm/up days (collect full premium safely); waiting for a dip sells *into a falling market* (closer to the short strike) and *skips the steady-up days* that are the easiest wins. The richer credit doesn't compensate. Feature kept default-off; not recommended.

## 3. Managed put-credit-only at the open — looked great, FRAGILE under stress

Stripped to short-put-credit verticals only, entered at the open, with 50/50 management *firing* (87 closes vs the long calls' 0): SPXW **PF 5.04 / 96% win / 3% DD** (holdout), XSP 2.50. Looked like the best risk profile of the session — until stressed:

**Tail floor (stop disabled → ride to expiry, i.e. what a gap day forces):**

| | PF | DD | P&L |
|---|---|---|---|
| SPXW train | 0.63 | **119%** (ruin) | −$11,655 |
| SPXW holdout | 1.30 | 18% | +$2,743 |
| XSP train | 0.72 | 12% | −$1,207 |
| XSP holdout | 0.87 | 8% | −$239 |

**Without the stop it's a loser** (negative on 3 of 4 cells, SPXW train hits negative equity). The entire PF 5 was the 50% stop cutting losses — and that stop assumes a clean intraday fill it *won't* get on a gap/crash day (none of which are in the 2025–26 sample). Classic "pennies in front of a steamroller."

**Exit slippage (0.05/share):** SPXW survives (PF ~4.2, big credits absorb it); **XSP dies** (PF 2.50 → 1.03, its ~$11 credits can't absorb ~$10 of slippage).

## Verdict

Both refuted. The managed put-credit-only PF 5 is a mirage (stop-dependent, ruinous no-stop floor, XSP killed by slippage). Critically, **the shipped overhaul is *more* robust**: its put-credit spreads already ride to expiry (no stop dependence) and the mix stays positive because the long-call winners carry it — its 22% drawdown is *honest*, not an artifact of a stop that won't hold.

**Session-wide:** every alternative to the structural per-ticker overhaul — entry timing (delay, volume, VWAP, regime filter), mean-reversion longs, dip-triggered put-credit, and managed put-credit-only — failed a holdout, a control, or a stress test. The **per-ticker structural overhaul remains the one validated, robust result.**
