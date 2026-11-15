"""Sweep cascade-strategy knobs through `wa ai backtest` and rank the cells.

Each cell is a strategy layer: a base layer (e.g. ai-config.SPXW.CASCADE.json) with the cell's overrides deep-merged in. The cell is
written next to the base as ai-config.<TICKER>.<PREFIX><n>.json, backtested at fixed lots with a fills ledger, and deleted. The first
cell replays the cascade readings and stores them (data/cascade-cache); every later cell of the same build reads them back, so a
sweep costs one replay plus the trade management of each cell. Backtests run one at a time (they share the quote store).

A grid file is JSON: {"cells": [{"id": "bt_call_o1_t30", "setup": "bearTrap", "overrides": {...}}, ...]}. Overrides are merged into
the base layer; "setup" names the one cascade setup the cell enables (the others are switched off), so every trade in the cell's
ledger belongs to it.

Ranking follows the project's convention: total P&L ranks, profit factor gates quality. Every cell is also scored on a training span
and a held-out span (--split), and without its two best trades, so a cell that wins on one outlier day or only in-sample is visible.

Usage (Windows python, Windows paths — wa.exe does not translate WSL paths):
  python scripts\\cascade_sweep.py --wa <path to wa.exe> --data <data dir> --ticker SPXW --base CASCADE --grid grid.json \\
      --since 2022-01-03 --until 2026-11-13 --split 2025-01-01 --out <sweep output dir>
"""
import argparse
import copy
import csv
import json
import os
import statistics
import subprocess
import sys
import time
from datetime import datetime

SETUPS = ("bullTrap", "bearTrap", "bullCascade", "bearCascade")


def deep_merge(base, overrides):
    out = copy.deepcopy(base)
    for k, v in overrides.items():
        out[k] = deep_merge(out[k], v) if isinstance(v, dict) and isinstance(out.get(k), dict) else copy.deepcopy(v)
    return out


def lifecycles(path):
    """One record per position lineage that closed: entry date and net P&L after fees."""
    life = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            fill = json.loads(line)
            rec = life.setdefault(fill["lineage"], {"date": fill["ts"][:10], "pnl": 0.0, "closed": False})
            rec["pnl"] += fill["net"] - fill["fees"]
            if fill["kind"] != "Open":
                rec["closed"] = True
    return [r for r in life.values() if r["closed"]]


def stats(pnls):
    n = len(pnls)
    if n == 0:
        return {"n": 0, "total": 0.0, "pf": 0.0, "win": 0.0, "median": 0.0, "ex_best2": 0.0, "t": 0.0}
    wins = sum(p for p in pnls if p > 0)
    losses = -sum(p for p in pnls if p < 0)
    ordered = sorted(pnls, reverse=True)
    sd = statistics.stdev(pnls) if n > 1 else 0.0
    return {
        "n": n,
        "total": sum(pnls),
        "pf": wins / losses if losses else (999.0 if wins else 0.0),
        "win": sum(p > 0 for p in pnls) / n,
        "median": statistics.median(pnls),
        "ex_best2": sum(pnls) - sum(ordered[:2]),
        "t": (sum(pnls) / n) / (sd / n ** 0.5) if sd > 0 else 0.0,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--wa", required=True)
    ap.add_argument("--data", required=True, help="the data directory holding ai-config.<TICKER>.<BASE>.json")
    ap.add_argument("--ticker", required=True)
    ap.add_argument("--base", required=True, help="strategy token of the base layer")
    ap.add_argument("--grid", required=True)
    ap.add_argument("--since", required=True)
    ap.add_argument("--until", required=True)
    ap.add_argument("--split", required=True, help="first date of the held-out span (YYYY-MM-DD)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--prefix", default="SWEEP")
    ap.add_argument("--extra", default="", help="extra arguments for every backtest, e.g. \"--cadence 10\"")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    log_path = os.path.join(args.out, "sweep.log")
    log = open(log_path, "a", encoding="utf-8")

    def say(msg):
        line = f"{datetime.now():%H:%M:%S} {msg}"
        print(line, flush=True)
        log.write(line + "\n")
        log.flush()

    with open(os.path.join(args.data, f"ai-config.{args.ticker}.{args.base}.json"), encoding="utf-8") as f:
        base = json.load(f)
    with open(args.grid, encoding="utf-8") as f:
        cells = json.load(f)["cells"]

    results_path = os.path.join(args.out, "results.csv")
    done = set()
    if os.path.exists(results_path):
        with open(results_path, encoding="utf-8") as f:
            done = {row["id"] for row in csv.DictReader(f)}
    fields = ["id", "setup", "n", "total", "pf", "win", "median", "ex_best2", "t", "train_total", "train_pf", "test_total", "test_pf", "test_n", "years", "overrides"]
    results = open(results_path, "a", newline="", encoding="utf-8")
    writer = csv.DictWriter(results, fieldnames=fields)
    if not done:
        writer.writeheader()

    todo = [c for c in cells if c["id"] not in done]
    say(f"{len(cells)} cell(s), {len(done)} already done, {len(todo)} to run; log {log_path}")
    started = time.time()
    for i, cell in enumerate(todo, 1):
        layer = deep_merge(base, {"opener": {"cascade": {s: {"enabled": s == cell["setup"]} for s in SETUPS}}})
        layer = deep_merge(layer, cell.get("overrides", {}))
        token = f"{args.prefix}{i}"
        layer_path = os.path.join(args.data, f"ai-config.{args.ticker}.{token}.json")
        fills = os.path.join(args.out, f"{cell['id']}.jsonl")
        report = os.path.join(args.out, f"{cell['id']}.txt")
        with open(layer_path, "w", encoding="utf-8") as f:
            json.dump(layer, f, indent="\t")
        cmd = [args.wa, "ai", "backtest", args.ticker, "--strategy", token, "--since", args.since, "--until", args.until,
               "--lots", "1", "--fills-jsonl", fills, "--output", "text", "--output-path", report] + args.extra.split()
        t0 = time.time()
        try:
            run = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
        finally:
            os.remove(layer_path)
        if run.returncode != 0 or not os.path.exists(fills):
            say(f"[{i}/{len(todo)}] {cell['id']}: FAILED (exit {run.returncode}) {run.stderr.strip()[-300:]}")
            continue

        trades = lifecycles(fills)
        whole = stats([t["pnl"] for t in trades])
        train = stats([t["pnl"] for t in trades if t["date"] < args.split])
        test = stats([t["pnl"] for t in trades if t["date"] >= args.split])
        years = {}
        for t in trades:
            years[t["date"][:4]] = years.get(t["date"][:4], 0.0) + t["pnl"]
        writer.writerow({
            "id": cell["id"], "setup": cell["setup"], **{k: round(v, 2) if isinstance(v, float) else v for k, v in whole.items()},
            "train_total": round(train["total"], 2), "train_pf": round(train["pf"], 2),
            "test_total": round(test["total"], 2), "test_pf": round(test["pf"], 2), "test_n": test["n"],
            "years": " ".join(f"{y}:{v:+.0f}" for y, v in sorted(years.items())),
            "overrides": json.dumps(cell.get("overrides", {}), separators=(",", ":")),
        })
        results.flush()
        elapsed = time.time() - started
        eta = elapsed / i * (len(todo) - i)
        say(f"[{i}/{len(todo)} {i * 100 // len(todo)}%] {cell['id']}: n={whole['n']} total={whole['total']:+,.0f} PF={whole['pf']:.2f} "
            f"exBest2={whole['ex_best2']:+,.0f} test={test['total']:+,.0f} ({time.time() - t0:.0f}s; ETA {eta / 60:.0f} min)")
    results.close()
    say("done")


if __name__ == "__main__":
    sys.exit(main())
