#!/usr/bin/env python3
"""Derive the XSP intraday tape from the SPXW (real SPX) tape — an exact identity, not an approximation.

XSP is the Mini-SPX index: XSP = SPX/10 by definition, so every XSP minute bar is the SPXW bar divided by
ten. This fills XSP day-files for any session where SPXW has a tape and XSP doesn't (the deep 2022→2024-05
range arrives via pull_webull_spx_minutes.py + `wa ai history --import-webull-spx`). Existing XSP files are
never overwritten (the store-wide no-overwrite invariant).

Usage: derive_index_tapes.py [--start 2022-01-01] [--end 2024-05-13] [--dry-run]
"""
import argparse, csv, os, sys
from pathlib import Path


def data_dir():
    cands = [os.environ.get("WA_DATA_DIR")] + ([str(Path(os.environ["LOCALAPPDATA"]) / "WebullAnalytics" / "data")] if os.environ.get("LOCALAPPDATA") else [])
    for p in [Path(c) for c in cands if c] + sorted(Path("/mnt/c/Users").glob("*/AppData/Local/WebullAnalytics/data")):
        if p.is_dir(): return p
    sys.exit("FATAL: data dir not found (set WA_DATA_DIR)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start", default="2022-01-01")
    ap.add_argument("--end", default="2024-05-13")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    d = data_dir()
    made = skipped = 0
    for src in sorted((d / "intraday" / "SPXW").glob("*.csv")):
        if not (a.start <= src.stem <= a.end):
            continue
        out = d / "intraday" / "XSP" / f"{src.stem}.csv"
        if out.exists():
            skipped += 1
            continue
        if a.dry_run:
            made += 1
            continue
        with open(src) as fin, open(out, "w", newline="") as fout:
            r = csv.DictReader(fin)
            w = csv.writer(fout)
            w.writerow(["timestamp_utc", "open", "high", "low", "close", "volume"])
            for row in r:
                w.writerow([row["timestamp_utc"], f"{float(row['open'])/10:.2f}", f"{float(row['high'])/10:.2f}",
                            f"{float(row['low'])/10:.2f}", f"{float(row['close'])/10:.2f}", row["volume"]])
        made += 1
    print(f"{'would derive' if a.dry_run else 'derived'} {made} XSP day-file(s) from real SPXW, skipped {skipped} existing")


if __name__ == "__main__":
    main()
