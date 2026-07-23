#!/usr/bin/env python3
"""Diff a quotes.db snapshot against the live store for one session date — the same-day-vs-next-day
ThetaData reproducibility check. Snapshot is created by copying all rows for a (root, date) into a
standalone db with the same schema (see sweeps/quotes_snapshot_20260721_sameday.db, taken 2026-07-22
before the next-day re-pull replaced the unsealed expiries).

Usage: diff_quotes_snapshot.py <snapshot.db> <YYYYMMDD-date> [root]
Reports per expiry: row counts, rows only in one side, strike-set drift (±10%-band membership), and
per-(minute,strike,right) bid/ask value diffs. Run it after any re-pull that touched the date.
"""
import os, sqlite3, sys
from pathlib import Path

snap_path, d8 = sys.argv[1], int(sys.argv[2])
root = sys.argv[3] if len(sys.argv) > 3 else 'SPY'
# WA_DATA_DIR -> LOCALAPPDATA (native Windows Python — the preferred host for quotes.db work: NTFS-native I/O and same-OS WAL locking as wa.exe) -> WSL /mnt/c fallback.
DATA = Path(os.environ.get('WA_DATA_DIR') or (Path(os.environ['LOCALAPPDATA']) / 'WebullAnalytics' / 'data' if os.environ.get('LOCALAPPDATA') else next(iter(sorted(Path('/mnt/c/Users').glob('*/AppData/Local/WebullAnalytics/data'))), 'MISSING-set-WA_DATA_DIR')))
store = sqlite3.connect(f'file:{(DATA / "quotes.db").as_posix()}?mode=ro', uri=True)
snap = sqlite3.connect(f'file:{snap_path}?mode=ro', uri=True)
exps = [r[0] for r in snap.execute("select distinct expiry from quotes order by expiry")]
tot_v = tot_rows = 0
for e in exps:
    a = {(r[0], r[1], r[2]): (r[3], r[4]) for r in snap.execute("select time_sec,strike_milli,right,bid,ask from quotes where expiry=?", (e,))}
    b = {(r[0], r[1], r[2]): (r[3], r[4]) for r in store.execute("select time_sec,strike_milli,right,bid,ask from quotes where root=? and expiry=? and date=?", (root, e, d8))}
    only_a, only_b = len(a.keys() - b.keys()), len(b.keys() - a.keys())
    sa = {k[1] for k in a}; sb = {k[1] for k in b}
    vdiff = [(k, a[k], b[k]) for k in a.keys() & b.keys() if a[k] != b[k]]
    tot_v += len(vdiff); tot_rows += len(a)
    maxd = max((max(abs(x[1][0] - x[2][0]), abs(x[1][1] - x[2][1])) for x in vdiff), default=0)
    print(f"exp {e}: rows snap={len(a)} store={len(b)} | only-snap {only_a} only-store {only_b} | strike-set +{len(sb - sa)}/-{len(sa - sb)} | value-diffs {len(vdiff)} (max {maxd} milli$)")
print(f"TOTAL: {tot_rows} snapshot rows, {tot_v} value diffs")
