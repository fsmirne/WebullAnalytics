#!/usr/bin/env python3
"""Snapshot one day's Schwab-captured rows out of the quote store before the daily backfill
rewrites them. The backfill replaces each unsealed expiration's CSV wholesale (atomic os.replace),
so the scraper's live rows for a date survive only until the next backfill run — snapshot them the
same day (ideally after the scraper exits at 16:05, so the snapshot covers the full session).
Re-running overwrites the snapshot with the fuller capture; rows are filtered to the target date.

Usage: python3 scripts/snapshot_schwab_day.py [--date YYYY-MM-DD]
Writes: <data>/quotes-schwab-snapshot-<date>/<TICKER>/<exp>.csv
Compare next day with: python3 scripts/validate_schwab_vs_thetadata.py SPY SPXW --date <date>
"""
import argparse
import csv
from datetime import date, datetime
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
from backfill_thetadata import resolve_data_dir  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--date", default=date.today().isoformat())
    args = ap.parse_args()

    data_dir = resolve_data_dir()
    src_root = data_dir / "quotes"
    dst_root = data_dir / f"quotes-schwab-snapshot-{args.date}"
    total = 0
    for tdir in sorted(src_root.iterdir()):
        if not tdir.is_dir():
            continue
        day_start = datetime.fromisoformat(args.date).timestamp()
        for f in sorted(tdir.glob("????-??-??.csv")):
            if f.stat().st_mtime < day_start:
                continue  # untouched since the target date -> cannot contain its rows; skips ~95% of files
            with open(f, newline="") as fh:
                r = csv.reader(fh)
                header = next(r, None)
                rows = [row for row in r if row and row[0] == args.date]
            if not rows:
                continue
            out = dst_root / tdir.name
            out.mkdir(parents=True, exist_ok=True)
            with open(out / f.name, "w", newline="") as fh:
                w = csv.writer(fh)
                w.writerow(header)
                w.writerows(rows)
            total += len(rows)
            print(f"{tdir.name}/{f.name}: {len(rows)} rows")
    print(f"snapshot total: {total} rows -> {dst_root}")


if __name__ == "__main__":
    main()
