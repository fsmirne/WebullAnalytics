#!/usr/bin/env python3
"""Replay a day's AI proposals to hold-to-expiry settlement P&L using the
captured 0DTE chain for the settlement spot.

Each proposal is treated as an independent per-minute entry: open the proposed
structure at that tick's engine-computed net (cashImpactPerContract, a mid-based
fill) and hold to 16:00 PM-settlement. SPXW is European cash-settled, so the
realized exit value of each leg is its intrinsic at the closing index print —
no exit bid/ask needed, which is exactly right for 0DTE held to expiry.

    pnl_per_contract = cashImpactPerContract + 100 * sum(sign * intrinsic(K, S_close))

where sign = +1 for a bought leg, -1 for a sold leg. Entry slippage is not
modeled (mid fills), matching the engine's realizedExpectancy slippage=0.

Usage: replay_proposals.py <YYYY-MM-DD>
"""
import json
import re
import sys
import statistics
from collections import defaultdict

DATA = "/mnt/c/Users/USER/AppData/Local/WebullAnalytics/data"
MULT = 100  # SPX/SPXW contract multiplier
SYM = re.compile(r"^([A-Z]+)(\d{6})([CP])(\d{8})$")
# Config-change cutover: morning directional regime vs afternoon intraday-only regime.
CUTOVER = "12:50"


def intrinsic(cp, strike, spot):
    return max(0.0, spot - strike) if cp == "C" else max(0.0, strike - spot)


def settlement_spot(date):
    """SPX close = the 16:00:00 snapshot spot (PM settlement reference)."""
    best = None
    for line in open(f"{DATA}/chain-snapshots/SPXW/{date}.jsonl"):
        if not line.strip():
            continue
        d = json.loads(line)
        if d["underlyingPrice"] is None:
            continue
        t = d["tsEt"][11:19]
        if t <= "16:00:30":
            best = (t, d["underlyingPrice"])
    return best


def pnl_per_contract(prop, spot):
    """Hold-to-expiry P&L for one contract of the proposed structure."""
    settle = 0.0
    for leg in prop["legs"]:
        m = SYM.match(leg["symbol"])
        if not m:
            return None
        cp, strike = m.group(3), int(m.group(4)) / 1000.0
        sign = 1 if leg["action"] == "buy" else -1
        settle += sign * intrinsic(cp, strike, spot)
    return prop["cashImpactPerContract"] + MULT * settle


def main():
    date = sys.argv[1] if len(sys.argv) > 1 else "2026-05-29"
    st, spot = settlement_spot(date)
    print(f"Settlement: {date} spot={spot:.2f} (from {st} snapshot)\n")

    rows = []
    violations = 0
    for line in open(f"{DATA}/ai-proposals.jsonl"):
        if not line.strip():
            continue
        p = json.loads(line)
        if not p.get("ts", "").startswith(date):
            continue
        pnl = pnl_per_contract(p, spot)
        if pnl is None:
            continue
        # Bound check: realized P&L must sit inside [maxLoss, maxProfit].
        lo, hi = p.get("maxLoss", -1e9), p.get("maxProfit", 1e9)
        if not (lo - 1.0 <= pnl <= hi + 1.0):
            violations += 1
        rows.append({
            "t": p["ts"][11:19], "struct": p["structure"],
            "regime": "morning-directional" if p["ts"][11:16] < CUTOVER else "afternoon-intraday",
            "pnl": pnl, "final": p.get("finalScore") or 0.0,
        })

    def summarize(label, items):
        if not items:
            print(f"{label}: (none)")
            return
        pnls = [r["pnl"] for r in items]
        wins = sum(1 for x in pnls if x > 0)
        print(f"{label}: n={len(items):3d}  win%={100*wins/len(items):5.1f}  "
              f"avg={statistics.mean(pnls):+8.2f}  median={statistics.median(pnls):+8.2f}  "
              f"total={sum(pnls):+10.2f}  [min {min(pnls):+.0f}, max {max(pnls):+.0f}]")

    print("=== by structure ===")
    bystruct = defaultdict(list)
    for r in rows:
        bystruct[r["struct"]].append(r)
    for s in sorted(bystruct):
        summarize(f"  {s:18s}", bystruct[s])

    print("\n=== by regime (per-contract, hold-to-close) ===")
    byregime = defaultdict(list)
    for r in rows:
        byregime[r["regime"]].append(r)
    for rg in ("morning-directional", "afternoon-intraday"):
        summarize(f"  {rg:20s}", byregime[rg])
    summarize(f"  {'ALL':20s}", rows)

    print(f"\nbound-check violations (pnl outside [maxLoss,maxProfit]): {violations}/{len(rows)}")


if __name__ == "__main__":
    main()
