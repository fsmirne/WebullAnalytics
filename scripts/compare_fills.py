#!/usr/bin/env python3
"""Side-by-side compare of two `wa ai backtest --fills-jsonl` outputs — built to validate the quotes-only
price foundation (--source quotes) against the trade-bar baseline (--source bars) on the same window.

A fill line is {ts,ticker,key,kind,strategy,qty,net,fees,rule,lineage,legs[...]}. A trade = one lineage;
its realized P&L = sum(net - fees) over its fills (matching BacktestRunner's own cleanliness calc). It's
"finalized" once it has a Close or Expire fill.

Lineage ids are assigned per run, so they DON'T match across files — we compare distributions (counts, PF,
win rate, structure mix) and OPEN COVERAGE by (date, strategy): days a trade opened in one run but not the
other. A big coverage gap on the quotes side = legs dropped for want of a real NBBO within the staleness
window (the real-data analog of the old captured-bar provenance signal).

    python3 scripts/compare_fills.py <bars.jsonl> <quotes.jsonl>
"""
import json
import sys
from collections import defaultdict


def load(path):
    """Return (trades, opens_by_date_strategy). trades = lineage -> {pnl, strategy, entry, finalized, exits}."""
    by_lineage = defaultdict(lambda: {"pnl": 0.0, "strategy": None, "entry": None, "finalized": False, "exits": []})
    opens = defaultdict(int)  # (date, strategy) -> count
    with open(path) as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            f = json.loads(line)
            t = by_lineage[f["lineage"]]
            t["pnl"] += float(f["net"]) - float(f["fees"])
            kind = f["kind"]
            if kind == "Open":
                t["strategy"] = f["strategy"]
                t["entry"] = f["ts"][:10]
                opens[(f["ts"][:10], f["strategy"])] += 1
            if kind in ("Close", "Expire"):
                t["finalized"] = True
                t["exits"].append(f.get("rule") or kind)
    return by_lineage, opens


def summarize(trades):
    fin = [t for t in trades.values() if t["finalized"]]
    pnls = [t["pnl"] for t in fin]
    wins = [p for p in pnls if p > 0]
    losses = [p for p in pnls if p <= 0]
    gross_win = sum(wins)
    gross_loss = abs(sum(losses))
    mix = defaultdict(int)
    exits = defaultdict(int)
    for t in fin:
        mix[t["strategy"] or "?"] += 1
        for e in t["exits"]:
            exits[e] += 1
    return {
        "opens": sum(1 for t in trades.values() if t["entry"]),
        "finalized": len(fin),
        "total_pnl": sum(pnls),
        "avg_pnl": (sum(pnls) / len(pnls)) if pnls else 0.0,
        "win_rate": (len(wins) / len(pnls) * 100) if pnls else 0.0,
        "pf": (gross_win / gross_loss) if gross_loss > 0 else float("inf"),
        "mix": dict(mix),
        "exits": dict(exits),
    }


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    a_path, b_path = sys.argv[1], sys.argv[2]
    a_trades, a_opens = load(a_path)
    b_trades, b_opens = load(b_path)
    a, b = summarize(a_trades), summarize(b_trades)

    def row(label, av, bv, fmt="{}"):
        print(f"  {label:<18} {fmt.format(av):>16} {fmt.format(bv):>16}")

    print(f"\n{'':20}{'A (bars)':>16}{'B (quotes)':>16}")
    print(f"  {a_path}")
    print(f"  {b_path}\n")
    row("opens", a["opens"], b["opens"])
    row("finalized trades", a["finalized"], b["finalized"])
    row("total P&L", a["total_pnl"], b["total_pnl"], "{:,.2f}")
    row("avg P&L/trade", a["avg_pnl"], b["avg_pnl"], "{:,.2f}")
    row("win rate %", a["win_rate"], b["win_rate"], "{:.1f}")
    row("profit factor", a["pf"], b["pf"], "{:.2f}")

    print("\n  structure mix (finalized):")
    for s in sorted(set(a["mix"]) | set(b["mix"])):
        row(f"    {s}", a["mix"].get(s, 0), b["mix"].get(s, 0))

    print("\n  exits by rule:")
    for r in sorted(set(a["exits"]) | set(b["exits"])):
        row(f"    {r}", a["exits"].get(r, 0), b["exits"].get(r, 0))

    # Open-coverage diff: the key quotes-only signal — days/structures that opened in one run only.
    a_only = sorted(k for k in a_opens if k not in b_opens)
    b_only = sorted(k for k in b_opens if k not in a_opens)
    print(f"\n  open coverage: {len(set(a_opens) & set(b_opens))} shared (date,strategy); "
          f"{len(a_only)} bars-only, {len(b_only)} quotes-only")
    if a_only:
        print("    bars-only (likely dropped on quotes side = no real NBBO in band/staleness):")
        for d, s in a_only[:30]:
            print(f"      {d}  {s}")
        if len(a_only) > 30:
            print(f"      ... +{len(a_only) - 30} more")
    if b_only:
        print("    quotes-only (opened on quotes but not bars):")
        for d, s in b_only[:30]:
            print(f"      {d}  {s}")
        if len(b_only) > 30:
            print(f"      ... +{len(b_only) - 30} more")
    print()


if __name__ == "__main__":
    main()
