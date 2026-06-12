#!/usr/bin/env python3
"""Validate the Schwab scraper's live minute-NBBO capture against ThetaData for the same session.

Workflow (ThetaData is T+1, and the daily backfill REWRITES each unsealed expiration's CSV):
  1. On capture day, snapshot the scraper's rows before they're overwritten:
       python3 scripts/snapshot_schwab_day.py            (or the inline copy loop this grew from)
     -> data/quotes-schwab-snapshot-<DATE>/<TICKER>/<exp>.csv
  2. Next day, AFTER the daily backfill has replaced that date's rows with ThetaData's version,
     compare snapshot (Schwab) against the live store (ThetaData):
       python3 scripts/validate_schwab_vs_thetadata.py SPY SPXW --date 2026-06-11

This compares exactly what backtest consumers read (QuoteStoreCache), not a raw API response.

TIMESTAMP SEMANTICS: the store's ThetaData row labeled T is the NBBO at the (T+1):00 boundary (raw
end-of-bar stamps, -60s ingest relabel; settled empirically 2026-06-11 on a 1.4M-row join). The
scraper originally stamped its fire minute (start-of-bar), one minute of label skew for the same
instant — CONVENTION CHANGED 2026-06-12: the scraper now labels fire-minute-minus-1, matching the
ThetaData convention exactly. Two comparisons are reported: same-label and instant-aligned
(Schwab@T+1 vs theta@T). For snapshots captured BEFORE 2026-06-12, instant-aligned is the true
feed-agreement number; from 2026-06-12 on, same-label is, and instant-aligned should degrade to
the ~$0.07-0.08 intra-minute drift floor — if it doesn't, the relabel regressed.
"""
import argparse
from datetime import date, datetime, timedelta
from pathlib import Path
import csv
import sys

sys.path.insert(0, str(Path(__file__).parent))
from backfill_thetadata import resolve_data_dir  # noqa: E402


def load_rows(root: Path, ticker: str, day: str, shift_minutes: int = 0):
    """Rows for `day` across <root>/<ticker>/<exp>.csv, keyed (exp, HH:MM, strike, right) with the
    key's minute label shifted by `shift_minutes` (used to undo the store's end->start-of-bar shift)."""
    tdir = root / ticker
    rows = {}
    day_start = datetime.fromisoformat(day).timestamp()
    for f in sorted(tdir.glob("????-??-??.csv")):
        if f.stat().st_mtime < day_start:
            continue  # untouched since the target date -> cannot contain its rows
        with open(f, newline="") as fh:
            r = csv.DictReader(fh)
            for row in r:
                if row["date"] != day:
                    continue
                t = datetime.strptime(row["time"][:5], "%H:%M") + timedelta(minutes=shift_minutes)
                key = (f.stem, t.strftime("%H:%M"), float(row["strike"]), row["right"].strip().upper()[:1])
                # 0 = "no quote on this side" (ThetaData's one-sided-book marker; Schwab leaves it
                # blank). QuoteStoreCache treats it as absent on read, so the comparison must too —
                # otherwise a one-sided deep-ITM book scores as a full-price disagreement.
                bid = float(row["bid"]) if row["bid"] not in ("", "nan") else None
                ask = float(row["ask"]) if row["ask"] not in ("", "nan") else None
                rows[key] = (bid if bid else None, ask if ask else None)
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tickers", nargs="+")
    ap.add_argument("--date", default=date.today().isoformat())
    ap.add_argument("--snapshot", default=None, help="Schwab snapshot root (default: <data>/quotes-schwab-snapshot-<date>)")
    args = ap.parse_args()

    data_dir = resolve_data_dir()
    snap_root = Path(args.snapshot) if args.snapshot else data_dir / f"quotes-schwab-snapshot-{args.date}"
    store_root = data_dir / "quotes"
    if not snap_root.exists():
        sys.exit(f"snapshot root not found: {snap_root}")

    for ticker in args.tickers:
        schwab = load_rows(snap_root, ticker, args.date)
        theta = load_rows(store_root, ticker, args.date)
        if not schwab:
            print(f"== {ticker} {args.date}: no Schwab snapshot rows"); continue
        if not theta:
            print(f"== {ticker} {args.date}: no store rows — has the daily backfill pulled this date yet? (ThetaData is T+1)")
            continue

        print(f"== {ticker} {args.date}: schwab={len(schwab)} theta={len(theta)}")
        for mode, theta_rows in (("same-label (store semantics)", theta),
                                 ("instant-aligned (schwab@T+1 vs theta@T)", load_rows(store_root, ticker, args.date, shift_minutes=1))):
            joined = [(schwab[k], theta_rows[k]) for k in schwab.keys() & theta_rows.keys()]
            only_schwab = len(schwab.keys() - theta_rows.keys())
            only_theta = len(theta_rows.keys() - schwab.keys())
            print(f" - {mode}: joined={len(joined)} only-schwab={only_schwab} only-theta={only_theta}")
            if not joined:
                print("   nothing joined — check date/snapshot paths"); continue
            for label, side in (("bid", 0), ("ask", 1)):
                ds = sorted(abs(s[side] - t[side]) for s, t in joined if s[side] is not None and t[side] is not None)
                if not ds:
                    print(f"   {label}: no comparable rows"); continue
                n = len(ds)
                exact = sum(1 for x in ds if x == 0) / n
                within1 = sum(1 for x in ds if x <= 0.01) / n
                within5 = sum(1 for x in ds if x <= 0.05) / n
                print(f"   {label}: n={n}  exact={exact:.1%}  <=$0.01={within1:.1%}  <=$0.05={within5:.1%}  "
                      f"median=${ds[n // 2]:.3f}  p95=${ds[int(n * 0.95)]:.3f}  max=${ds[-1]:.2f}")


if __name__ == "__main__":
    main()
