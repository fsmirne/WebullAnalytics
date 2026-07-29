#!/usr/bin/env python3
"""Find sessions where held option strikes can drift OUTSIDE the quote store's per-day strike band.

The backfill keeps strikes within ±band (default 10%) of each session's spot. Entries are always in-band
when opened, but a position held through a fast market can carry its strikes outside later sessions' bands —
blinding backtest management exactly on crash days (SPY 2022-06-13/14: spot ~375, held 413C/415C vs a 412.5
band top). A session is AFFECTED when spot deviates more than `band − margin` from its trailing-window
extreme (default: 45 sessions ≈ the longest holding period, 9.5% vs the 10% band): some strike that was
in-band during the window is out-of-band today.

Prints affected sessions grouped into contiguous runs, each with a ready-to-run supplement command that
layers the wider ring (default ±15%) additively over the existing store — no DELETE, no re-seal, ~small.

Usage: band_drift_days.py <TICKER:DTE> [--band 0.10] [--margin 0.005] [--window 45] [--wide 0.15]
"""
import argparse, csv, os, sys
from datetime import date, timedelta
from pathlib import Path


def data_dir():
    cands = [os.environ.get("WA_DATA_DIR")] + ([str(Path(os.environ["LOCALAPPDATA"]) / "WebullAnalytics" / "data")] if os.environ.get("LOCALAPPDATA") else [])
    for p in [Path(c) for c in cands if c] + sorted(Path("/mnt/c/Users").glob("*/AppData/Local/WebullAnalytics/data")):
        if p.is_dir(): return p
    sys.exit("FATAL: data dir not found (set WA_DATA_DIR)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ticker", help="TICKER:DTE (DTE bounds the supplement pull commands, e.g. SPY:60)")
    ap.add_argument("--band", type=float, default=0.10, help="the band the store was pulled with (default 0.10)")
    ap.add_argument("--margin", type=float, default=0.005, help="safety margin subtracted from band (default 0.005 → flag at 9.5%% for a 10%% band)")
    ap.add_argument("--window", type=int, default=45, help="trailing sessions a position could have been opened in (default 45)")
    ap.add_argument("--wide", type=float, default=0.15, help="floor band for the suggested supplement commands (default 0.15)")
    ap.add_argument("--min-drift", type=float, default=0.0, help="only emit supplement commands for runs whose MAX drift exceeds this (e.g. 0.15 = crash windows only; shallow windows are still listed)")
    a = ap.parse_args()
    root = a.ticker.split(":")[0].upper()

    path = data_dir() / "history" / f"{root}.csv"
    rows = [(r["date"], float(r["close"])) for r in csv.DictReader(open(path)) if r.get("close")]
    rows.sort()
    thresh = a.band - a.margin

    affected = []
    for i, (d, c) in enumerate(rows):
        w = [x[1] for x in rows[max(0, i - a.window):i]]
        if not w: continue
        drift = max(max(w) / c - 1.0, 1.0 - min(w) / c)   # how far the trailing extreme sits from today's spot
        if drift > thresh:
            affected.append((d, round(drift * 100, 1)))

    if not affected:
        print(f"{root}: no sessions exceed {thresh:.1%} trailing-{a.window}-session drift — the ±{a.band:.0%} band covered every held strike.")
        return

    print(f"{root}: {len(affected)} affected session(s) (drift > {thresh:.1%} vs trailing {a.window} sessions):")
    runs, cur = [], [affected[0]]
    for prev, nxt in zip(affected, affected[1:]):
        if (date.fromisoformat(nxt[0]) - date.fromisoformat(prev[0])).days <= 5: cur.append(nxt)
        else: runs.append(cur); cur = [nxt]
    runs.append(cur)
    for run in runs:
        days = ", ".join(f"{d} ({p}%)" for d, p in run)
        print(f"  {days}")
    # Each window's supplement ring is sized to ITS OWN deepest drift (+2% headroom), floored at --wide — so a
    # shallow 11% window pulls a thin 10-13% ring while a 30% crash window pulls the full wide ring. Minimal
    # over-pull: only the affected windows get widened, and each only as far as it actually drifted.
    scope = f" for crash windows (max drift > {a.min_drift:.0%})" if a.min_drift else ""
    print(f"\nSupplement command(s){scope} (additive ring sized per-window to its max drift +2%, floor ±{a.wide:.0%}; no DELETE/re-seal):")
    skipped = 0
    for run in runs:
        run_max = max(p for _, p in run) / 100.0
        if run_max <= a.min_drift:
            skipped += 1
            continue
        band = max(a.wide, round(run_max + 0.02, 2))
        print(f"  python backfill_thetadata.py --quotes --supplement --band {band} --ticker {a.ticker} --start {run[0][0]} --end {run[-1][0]} --timeout 1200 --concurrency 2")
    if skipped: print(f"({skipped} shallow window(s) <= {a.min_drift:.0%} drift skipped — barely past the ±10% store band, left as-is)")


if __name__ == "__main__":
    main()
