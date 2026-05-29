#!/usr/bin/env python3
"""Validate the backtest's (Open+Close)/2 option-pricing model against the
scraper's real per-minute chain snapshots.

The backtest prices a leg at the captured bar's time-midpoint (Open+Close)/2,
then back-solves IV (AI/Backtest/BacktestQuoteSource.cs). Its stated rationale:
the midpoint approximates the price ~30s into the minute (where live samples),
whereas bar.Open under-samples on trending minutes.

We now have two independent sources for the SAME contracts/minutes:
  - data/options/SPXW/<date>/<occ>.csv   -> real OHLC bars (the backtest input)
  - chain-snapshots/SPXW/<date>.jsonl    -> real bid/ask/last snapshots @ :00

For every (contract, minute) in both, compare each estimator to the snapshot's
quoted mid (bid+ask)/2 — the fair price a live order transacts near:
  model = (open+close)/2   (what the backtest uses)
  open  =  open            (the naive alternative the model claims to beat)
  close =  close
The head-to-head answers the model's central claim: is |model-mid| < |open-mid|?

Note the time skew: the snapshot is at the minute boundary (:00) while the bar
spans [:00,:60). So snap@T ~ bar.Open@T and snap@T+1 ~ bar.Close@T. We therefore
also compare model against the boundary-pair mid (snap_T+snap_{T+1})/2, which is
the apples-to-apples target for a time-midpoint estimator.

Usage: validate_midpoint.py <YYYY-MM-DD>
"""
import csv
import glob
import json
import os
import re
import statistics
import sys
from datetime import datetime, timezone

DATA = "/mnt/c/Users/Flavio/AppData/Local/WebullAnalytics/data"
SYM = re.compile(r"^([A-Z]+)(\d{6})([CP])(\d{8})$")


def utc_minute(dt_iso):
    return int(datetime.fromisoformat(dt_iso).timestamp()) // 60 * 60


def load_snapshots(date):
    """{utc_minute: {sym: {bid,ask,last}}} from the scraper jsonl."""
    out = {}
    for line in open(f"{DATA}/chain-snapshots/SPXW/{date}.jsonl"):
        if not line.strip():
            continue
        d = json.loads(line)
        if not d["options"]:
            continue
        m = utc_minute(d["tsUtc"].replace("Z", "+00:00"))
        out[m] = {o["symbol"]: o for o in d["options"]}
    return out


def load_bars(date):
    """{sym: {utc_minute: (open,close,vol)}} from data/options OHLC csvs."""
    out = {}
    for f in glob.glob(f"{DATA}/options/SPXW/{date}/*.csv"):
        occ = os.path.splitext(os.path.basename(f))[0]
        bars = {}
        for r in csv.DictReader(open(f)):
            try:
                t = int(datetime.fromisoformat(r["timestamp_utc"].replace("Z", "+00:00")).timestamp())
                bars[t // 60 * 60] = (float(r["open"]), float(r["close"]), float(r["volume"] or 0))
            except (ValueError, KeyError):
                continue
        if bars:
            out[occ] = bars
    return out


def stats(label, errs):
    if not errs:
        print(f"  {label:28s} (no overlap)")
        return
    mae = statistics.mean(abs(e) for e in errs)
    bias = statistics.mean(errs)
    print(f"  {label:28s} n={len(errs):5d}  bias={bias:+7.3f}  MAE={mae:6.3f}  median|e|={statistics.median(sorted(abs(e) for e in errs)):6.3f}")


def main():
    date = sys.argv[1] if len(sys.argv) > 1 else "2026-05-29"
    snaps = load_snapshots(date)
    bars = load_bars(date)
    print(f"snapshot minutes: {len(snaps)}   contracts with bars: {len(bars)}")
    if not bars:
        print(f"\nNo option bars at data/options/SPXW/{date}/ yet.")
        print("Pull them with:  wa ai history --partial   (some files land only after 5PM ET)")
        return

    # vs the :00 boundary mid (aligns with bar.Open — favors open by construction)
    model_e, open_e, close_e = [], [], []
    # vs the pair-mid (mid@T+mid@T+1)/2 ~ price at :30 — the time-correct target for a midpoint
    model_p, open_p, close_p = [], [], []
    model_wins_pair = 0
    pair_n = 0
    for occ, occ_bars in bars.items():
        for m, (o, c, vol) in occ_bars.items():
            snap = snaps.get(m, {}).get(occ)
            if not snap or snap.get("bid") is None or snap.get("ask") is None:
                continue
            mid = (snap["bid"] + snap["ask"]) / 2
            if mid <= 0:
                continue
            model = (o + c) / 2
            model_e.append(model - mid)
            open_e.append(o - mid)
            close_e.append(c - mid)
            nxt = snaps.get(m + 60, {}).get(occ)
            if nxt and nxt.get("bid") is not None and nxt.get("ask") is not None:
                pair = (mid + (nxt["bid"] + nxt["ask"]) / 2) / 2
                model_p.append(model - pair)
                open_p.append(o - pair)
                close_p.append(c - pair)
                pair_n += 1
                if abs(model - pair) < abs(o - pair):
                    model_wins_pair += 1

    print("\n=== vs :00 boundary mid (aligns with bar.Open — biased toward open) ===")
    stats("(open+close)/2  [MODEL]", model_e)
    stats("open", open_e)
    stats("close", close_e)
    print("\n=== vs pair-mid ~price@:30 (time-correct target for a midpoint estimator) ===")
    stats("(open+close)/2  [MODEL]", model_p)
    stats("open", open_p)
    stats("close", close_p)
    if pair_n:
        print(f"\nMODEL beats open vs pair-mid (|err| smaller): {model_wins_pair}/{pair_n} = {100*model_wins_pair/pair_n:.1f}%")

    # Targeted: the real iron condor legs vs their captured marks.
    print("\n=== iron condor legs: actual fill vs bar-model vs snapshot mid ===")
    for grp in (json.loads(l) for l in open(f"{DATA}/orders.jsonl") if l.strip()):
        for o in grp.get("orderList", []):
            sub = o.get("subSymbol", "")
            if "SPXW" not in o.get("symbol", "") or "29 May 26" not in sub:
                continue
            strike = float(o["symbol"].split("$")[1])
            cp = "C" if "Call" in sub else "P"
            occ = f"SPXW{datetime.fromisoformat('2026-05-29').strftime('%y%m%d')}{cp}{int(strike*1000):08d}"
            fill = float(o["filledPrice"])
            # fill at 13:43:47 EDT -> 17:43 UTC
            m = utc_minute("2026-05-29T17:43:00+00:00")
            snap = snaps.get(m, {}).get(occ)
            bar = bars.get(occ, {}).get(m)
            mid = (snap["bid"] + snap["ask"]) / 2 if snap and snap.get("bid") is not None else None
            model = (bar[0] + bar[1]) / 2 if bar else None
            print(f"  {o['action']:4s} {occ}  fill={fill:6.2f}  "
                  f"snap_mid={mid if mid is None else round(mid,2)}  bar_model={model if model is None else round(model,2)}")


if __name__ == "__main__":
    main()
