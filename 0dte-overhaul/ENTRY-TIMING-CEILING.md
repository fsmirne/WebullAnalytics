# Entry-Timing Ceiling — Oracle vs Realistic, and the Signal Hunt

Follow-up to `ENTRY-AND-SIGNAL-FINDINGS.md`. Question: the realistic backtest enters at 09:30; the `--oracle` (lookahead) backtest does far better. Is the gap *which trades* it picks, or *when* it enters — and if timing, is there a tradeable signal for the entry?

## 1. Oracle vs realistic (XSP recommended, --lots 1, 2026 holdout)

| | realistic (09:30) | oracle (perfect timing) |
|---|---|---|
| Opens | 63 | 63 — **same days** |
| Same structure | — | **60/63 (95%)** |
| Win rate | 47.6% | 61.9% |
| Expectancy/trade | $23 | $333 |
| Profit factor | 1.35 | **9.07** |

**Day-selection and structure-selection are already oracle-grade.** 100% of the gap is *entry timing within the day* (and the resulting strike: 46% of "same structure" days differ only because a later entry → different ATM strike). For the 42 same-structure LongCall days, the oracle enters later (86%) and ~30% cheaper ($1.31→$0.92/sh), turning avg P&L $28→$427 and cutting the 23 realistic losers from −$122 to ~$0.

## 2. Is there a tradeable entry signal? — No (across the sample)

The vivid single example (2026-05-08: oracle bought at 14:55 into RSI 15, below the lower Bollinger band, on an 11× option-volume spike) **does not generalize.** Across all 42 oracle LongCall entries:

| feature at oracle's optimal entry | result | reading |
|---|---|---|
| RSI | median 51 (48th pctile-of-day) | random — *not* oversold |
| entries with RSI ≤ 25 | 0/31 | — |
| Bollinger BB% | median 48; below lower band 3/31 | random — *not* at the band |
| drawdown from session high | −0.07% (65th pctile) | enters **near highs**, not dips |
| position in day's range | 72 (upper third) | not buying the low |
| VWAP distance | ~0% | neutral |
| time-of-day | median 10:41 | mid-morning (05-08's 14:55 was atypical) |

The oracle enters mid-morning, near the session high, at neutral RSI/Bollinger. **No oscillator or price feature reproduces it.** The "oversold-dip" intuition was a single chart that's natural to find when you go looking for a dip; it is not how the oracle usually enters. What the oracle *actually* exploits is theta decay (later = cheaper, deterministic) plus lookahead selection of the pre-rally low — neither tradeable.

## 3. The one unclosed thread — option order-flow (volume)

Where per-contract intraday option volume exists, the bought call shows a **~18.8× volume spike** at the oracle's entry minute (8/11 ≥3×). But this is the weakest evidence here:
- **26% coverage** (11/42) — XSP options are sparsely backfilled.
- **Selection bias** — a contract is on disk largely *because* it traded, so "volume at entry" is partly circular.
- **Coincidence vs prediction** — measured at a lookahead-chosen minute; option volume spikes at many minutes, so "a spike occurred here" ≠ "spikes predict good entries."

Testing it honestly needs **full-chain intraday option volume** — so we can check whether the bought call's spike is *distinctive* vs the rest of the chain at that minute, and simulate acting on it *live*. That data doesn't exist yet for most days (the ongoing massive backfill is the path to it). SPXW, with a denser contract base, is the better test bed once backfilled.

## Verdict

The entry-timing gap (PF 1.35→9.07) is **real but lookahead** and maps onto **no** observable signal we tested (RSI, MACD, Bollinger, drawdown, VWAP, range-position, time-of-day). The only survivor is option order-flow volume, which is data-starved and likely circular — parked until full-chain intraday option volume exists, then test "enter the call on a *distinctive* volume spike" with no lookahead.

**The shippable win remains the structural per-ticker overhaul** (`FINAL-DESIGN.md`), already on master. The directional/timing program is closed except for the order-flow probe, which is data-gated.
