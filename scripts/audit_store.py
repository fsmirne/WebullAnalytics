#!/usr/bin/env python3
"""Per-ticker completeness/defect audit of quotes.db — generalized from the SPY-only audit_fullperiod_data.py
(which is hardcoded to root='SPY' + SPY's $1 grid / ±10% band / 5-15,30-45 DTE). Data checks only (no engine/fills).

  H1 missing sessions   - no rows for a trading day (engine silently skips it)
  H2 truncated sessions - a session's minute coverage stops early (partial pull)
  H3 thin strikes       - fewer strikes than a UNIFORM grid implies. HEURISTIC: variable grids (QQQ $1->$5,
                          SPXW $5->$20 far out) legitimately look "thin" vs uniform, so treat as informational.
  H4 missing back expiry- multi-DTE only: no 30-45 DTE expiry quoted => opener structurally dead (N/A for 0-DTE)
  H5 crossed/zero NBBO  - torn-book share, sampled at 10:00
  H7 per-expiry window  - EMPTY / internal HOLE / TRUNCATED tail. **HOLES are the band-drift signature**: a
                          sealed expiry blind on a session because spot drifted outside the ±band pull window
                          (the SPY finding; expected WORSE for QQQ, which crashed harder).

Read-only; run Windows-native (python.exe). Usage:
  audit_store.py --root QQQ                      # multi-DTE, $1 grid
  audit_store.py --root XSP  --zero-dte           # 0-DTE, $1 grid
  audit_store.py --root SPXW --zero-dte --strike-step 5
"""
import argparse, bisect, csv, datetime, os, sqlite3
from collections import defaultdict
from pathlib import Path

ap = argparse.ArgumentParser()
ap.add_argument('--root', required=True)
ap.add_argument('--since', default='2022-01-01')
ap.add_argument('--until', default='2026-07-28')
ap.add_argument('--zero-dte', action='store_true', help='0-DTE root (XSP/SPXW): probe the same-day expiry, skip H4 back-check')
ap.add_argument('--strike-step', type=float, default=1.0)
ap.add_argument('--band', type=float, default=0.10)
ap.add_argument('--db', default=None)
a = ap.parse_args()
ROOT = a.root.upper()

DATA = Path(os.environ.get('WA_DATA_DIR') or (Path(os.environ['LOCALAPPDATA']) / 'WebullAnalytics' / 'data' if os.environ.get('LOCALAPPDATA')
       else next(iter(sorted(Path('/mnt/c/Users').glob('*/AppData/Local/WebullAnalytics/data'))), 'MISSING')))
DB = Path(a.db) if a.db else DATA / 'quotes.db'
RTH_MIN = 380
def d8(s): return int(s.replace('-', ''))
def iso(v): s = str(v); return f'{s[:4]}-{s[4:6]}-{s[6:8]}'

# spot + session calendar from the root's own EOD history
spot = {r['date']: float(r.get('close') or r.get('Close')) for r in csv.DictReader(open(DATA / 'history' / f'{ROOT}.csv')) if a.since <= r['date'] <= a.until}
sessions = sorted(spot)
con = sqlite3.connect(f'file:{DB.as_posix()}?mode=ro', uri=True)
print(f'== audit {ROOT} {a.since}..{a.until}: {len(sessions)} sessions | {"0-DTE" if a.zero_dte else "multi-DTE"} step=${a.strike_step:g} band={a.band:.0%} ==')

row = con.execute("select distinct expiry from quotes where root=? and expiry between ? and ?",
                  (ROOT, d8(a.since), d8((datetime.date.fromisoformat(a.until) + datetime.timedelta(days=70)).isoformat()))).fetchall()
all_exps = sorted(x[0] for x in row)
print(f'listed expiries in store: {len(all_exps)}')

def exp_near(day_iso, lo, hi):
    d0 = datetime.date.fromisoformat(day_iso)
    return [e for e in all_exps if lo <= (datetime.date.fromisoformat(iso(e)) - d0).days <= hi]

missing, trunc, thin, no_front, no_back = [], [], [], [], []
for day in sessions:
    d = d8(day)
    if a.zero_dte:
        fronts = [e for e in all_exps if e == d]; backs = []
        if not fronts: no_front.append(day); continue
        probe = fronts[0]
    else:
        fronts, backs = exp_near(day, 5, 15), exp_near(day, 30, 45)
        if not fronts or not backs: (no_front if not fronts else no_back).append(day); continue
        probe = fronts[len(fronts) // 2]
    minutes, strikes = con.execute("select count(distinct time_sec), count(distinct strike_milli) from quotes where root=? and expiry=? and date=?", (ROOT, probe, d)).fetchone()
    if minutes == 0:
        cands = fronts + backs
        if not any(con.execute("select 1 from quotes where root=? and expiry=? and date=? limit 1", (ROOT, e, d)).fetchone() for e in cands):
            missing.append(day); continue
        minutes, strikes = max((con.execute("select count(distinct time_sec), count(distinct strike_milli) from quotes where root=? and expiry=? and date=?", (ROOT, e, d)).fetchone() for e in cands), key=lambda t: t[0])
    if minutes < RTH_MIN and minutes not in range(200, 220): trunc.append((day, minutes))
    expected = (2 * a.band * spot[day]) / a.strike_step
    if strikes < 0.6 * expected: thin.append((day, strikes, round(expected)))
    if not a.zero_dte and not any(con.execute("select 1 from quotes where root=? and expiry=? and date=? limit 1", (ROOT, e, d)).fetchone() for e in backs): no_back.append(day)

print(f'H1 missing sessions: {len(missing)} {missing[:8]}')
print(f'H2 truncated sessions (<{RTH_MIN}min, not early-close): {len(trunc)} {trunc[:8]}')
print(f'H3 thin-strike sessions (<60% uniform, INFORMATIONAL): {len(thin)} {thin[:6]}')
if a.zero_dte:
    print(f'H1 sessions lacking a same-day (0-DTE) expiry: {len(no_front)} {no_front[:8]}')
else:
    print(f'H4 sessions lacking front 5-15 DTE: {len(no_front)} {no_front[:8]}')
    print(f'H4 sessions lacking back 30-45 DTE: {len(no_back)} {no_back[:8]}')

per_year = defaultdict(lambda: [0, 0])
for day in sessions[::max(1, len(sessions) // 120)]:
    cand = [e for e in all_exps if e == d8(day)] if a.zero_dte else exp_near(day, 30, 45)
    if not cand: continue
    e = cand[len(cand) // 2]
    for bid, ask in con.execute("select bid, ask from quotes where root=? and expiry=? and date=? and time_sec=36000", (ROOT, e, d8(day))):
        per_year[day[:4]][1] += 1
        if bid > ask or (bid == 0 and ask == 0): per_year[day[:4]][0] += 1
print('H5 crossed/zero NBBO @10:00 (sampled):', {y: f'{c}/{n}' for y, (c, n) in sorted(per_year.items())})

print('\n== H7 per-expiry window completeness (HOLES = band-drift) ==')
def dcount(x, y):
    return bisect.bisect_right(sessions, iso(y)) - bisect.bisect_left(sessions, iso(x))
empty, holes, trunc7 = [], [], []
last_d8 = d8(sessions[-1])
try:
    kh = defaultdict(set)
    for r, e_, dd in con.execute("select root, expiry, date from known_holes where root=?", (ROOT,)): kh[e_].add(dd)
except sqlite3.OperationalError:
    kh = defaultdict(set)
kh_excl = 0
for e in all_exps:
    n, lo, hi = con.execute("select count(distinct date), min(date), max(date) from quotes where root=? and expiry=? and date between ? and ?", (ROOT, e, d8(a.since), d8(a.until))).fetchone()
    if n == 0:
        if e <= last_d8 or con.execute("select 1 from sealed where root=? and expiry=?", (ROOT, e)).fetchone(): empty.append(e)
        continue
    span = dcount(lo, hi)
    known = sum(1 for dd in kh.get(e, ()) if lo <= dd <= hi); kh_excl += known
    if n < span - known: holes.append((e, span - known - n))
    gap = dcount(hi, min(e, last_d8)) - 1
    if gap > 1: trunc7.append((e, iso(hi), gap))
if kh_excl: print(f'H7 known-hole days excluded (vendor-proven): {kh_excl}')
print(f'H7 EMPTY expiries: {len(empty)} {[iso(x) for x in empty[:8]]}')
print(f'H7 internal HOLES: {len(holes)} {[(iso(e), m) for e, m in holes[:10]]}')
print(f'H7 TRUNCATED tails: {len(trunc7)} {[(iso(e), h, g) for e, h, g in trunc7[:8]]}')
