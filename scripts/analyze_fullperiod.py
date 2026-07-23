#!/usr/bin/env python3
"""Regime slicer for the full-period DC backtest (2022-01-01 →).

Reads a --fills-jsonl file from `wa ai backtest --lots 1`, groups fills into lineages, keeps CLOSED lineages
only (open-at-until lineages are pure debits, not losses — same discipline as backtest_dc_sweep.ps1), and
prints per-trade edge metrics sliced by: full period, in-sample vs out-of-sample (DC was tuned on 2025-2026;
2022-2024 is OOS), calendar year, VIX band at entry (prior-session close: <20 / 20-30 / >30), and trend
regime at entry (prior-session SPY close above/below its 200-dma). Entry-day activity rate is reported per
year so a regime that suppresses opens (e.g. a VIX gate in 2022) is visible rather than silently thin.

Usage: analyze_fullperiod.py <fills.jsonl>
"""
import csv, json, math, os, sys
from collections import defaultdict
from pathlib import Path

IS_CUTOVER = "2025-01-01"   # entries before this = OOS (untuned period), after = in-sample tuning window

def data_dir():
    cands = [os.environ.get("WA_DATA_DIR")] + ([str(Path(os.environ["LOCALAPPDATA"]) / "WebullAnalytics" / "data")] if os.environ.get("LOCALAPPDATA") else [])
    for p in [Path(c) for c in cands if c] + sorted(Path("/mnt/c/Users").glob("*/AppData/Local/WebullAnalytics/data")):
        if p.is_dir(): return p
    sys.exit("FATAL: AppData data dir not found (set WA_DATA_DIR)")

def load_daily(path, col="close"):
    out = {}
    with open(path) as f:
        for row in csv.DictReader(f):
            try: out[row["date"]] = float(row[col])
            except (KeyError, ValueError): pass
    return dict(sorted(out.items()))

def prior_close(series_dates, series, day):
    # most recent close strictly BEFORE the entry date (no same-day lookahead)
    lo, hi = 0, len(series_dates)
    while lo < hi:
        mid = (lo + hi) // 2
        if series_dates[mid] < day: lo = mid + 1
        else: hi = mid
    return series[series_dates[lo - 1]] if lo > 0 else None

def stats(vals):
    n = len(vals)
    if n == 0: return dict(n=0, wr=None, pf=None, avg=None, t=None, total=0.0, worst=None)
    wins = [v for v in vals if v > 0]; losses = [v for v in vals if v <= 0]
    gw, gl = sum(wins), abs(sum(losses))
    pf = (gw / gl) if gl > 0 else float("inf")
    total = sum(vals); mean = total / n
    t = None
    if n > 1:
        sd = math.sqrt(sum((v - mean) ** 2 for v in vals) / (n - 1))
        if sd > 0: t = mean * math.sqrt(n) / sd
    return dict(n=n, wr=len(wins) / n, pf=pf, avg=mean, t=t, total=total, worst=min(vals))

def prow(label, s, extra=""):
    fmt = lambda v, nd=2: f"{v:.{nd}f}" if isinstance(v, (int, float)) and not math.isinf(v) else ("inf" if isinstance(v, float) and math.isinf(v) else "—")
    print(f"  {label:<26}{s['n']:>6}  wr {fmt(s['wr'],3) if s['wr'] is not None else '—':>6}  PF {fmt(s['pf']):>6}  t {fmt(s['t']):>6}  avg {fmt(s['avg']):>8}  total {fmt(s['total']):>11}  worst {fmt(s['worst']):>9}  {extra}")

def main():
    fills_path = sys.argv[1]
    d = data_dir()
    vix = load_daily(d / "history" / "VIX.csv")
    spy = load_daily(d / "history" / "SPY.csv")
    vix_dates = list(vix.keys()); spy_dates = list(spy.keys())
    spy_vals = list(spy.values())
    ma200 = {}   # date -> 200-dma of close as of that date
    for i, day in enumerate(spy_dates):
        if i >= 199: ma200[day] = sum(spy_vals[i - 199:i + 1]) / 200

    lineages = defaultdict(lambda: dict(pnl=0.0, closed=False, entry=None))
    with open(fills_path) as f:
        for line in f:
            line = line.strip()
            if not line: continue
            try: r = json.loads(line)
            except json.JSONDecodeError: continue
            L = lineages[str(r.get("lineage"))]
            L["pnl"] += float(r.get("net", 0) or 0) - float(r.get("fees", 0) or 0)
            if r.get("kind") == "Open": L["entry"] = (r.get("ts") or "")[:10]
            else: L["closed"] = True

    closed = [(L["entry"], L["pnl"]) for L in lineages.values() if L["closed"] and L["entry"]]
    open_excluded = sum(1 for L in lineages.values() if not L["closed"])
    print(f"lineages: {len(lineages)}  closed: {len(closed)}  open-at-until excluded: {open_excluded}")
    if not closed: return

    def sliced(pred): return stats([p for e, p in closed if pred(e)])
    print("\n== full period ==")
    prow("ALL", sliced(lambda e: True))
    print(f"\n== OOS vs in-sample (cutover {IS_CUTOVER}) ==")
    prow("OOS 2022-2024 (untuned)", sliced(lambda e: e < IS_CUTOVER))
    prow("IS 2025-2026 (tuning)", sliced(lambda e: e >= IS_CUTOVER))
    print("\n== by calendar year ==")
    years = sorted({e[:4] for e, _ in closed})
    for y in years:
        n_entries = sum(1 for L in lineages.values() if (L["entry"] or "").startswith(y))
        n_days = sum(1 for day in spy_dates if day.startswith(y))
        rate = f"opens/day {n_entries}/{n_days} = {n_entries / n_days:.0%}" if n_days else ""
        prow(y, sliced(lambda e, y=y: e.startswith(y)), rate)
    print("\n== by VIX band at entry (prior-session close) ==")
    def vix_band(e):
        v = prior_close(vix_dates, vix, e)
        return None if v is None else ("<20" if v < 20 else "20-30" if v < 30 else ">30")
    for band in ("<20", "20-30", ">30"):
        prow(f"VIX {band}", sliced(lambda e, b=band: vix_band(e) == b))
    print("\n== by trend regime at entry (prior-session close vs 200-dma) ==")
    def trend(e):
        lo, hi = 0, len(spy_dates)
        while lo < hi:
            mid = (lo + hi) // 2
            if spy_dates[mid] < e: lo = mid + 1
            else: hi = mid
        if lo == 0: return None
        day = spy_dates[lo - 1]
        return None if day not in ma200 else ("above" if spy[day] >= ma200[day] else "below")
    for side in ("above", "below"):
        prow(f"{side} 200-dma", sliced(lambda e, s=side: trend(e) == s))

if __name__ == "__main__":
    main()
