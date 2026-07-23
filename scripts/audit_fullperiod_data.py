#!/usr/bin/env python3
"""Defect audit for the 2022-2026 full-period SPY DC campaign — data store AND engine output.

The 2022-2024 stretch backtests ~2x faster per session than 2025-2026. The benign explanation is strike
density (SPY's $1 grid inside the ±10% pull band holds ~half the strikes at half the spot); this script
exists to rule out the non-benign ones. Each check maps to a defect hypothesis:

  H1 missing sessions        - quotes.db has no rows for a trading day (engine silently skips it)
  H2 truncated sessions      - a session's minute coverage stops early (partial pull)
  H3 thin strikes            - strikes far below the ~0.2*spot the $1 grid implies (band mis-centered/partial)
  H4 missing expiries        - no front (5-15 DTE) or back (30-45 DTE) expiry quoted => opener structurally dead
  H5 torn/crossed books      - crossed-NBBO share materially worse in some years (pricing garbage in, garbage out)
  H6 engine short-circuits   - fills anomalies: near-zero debits (free-trade bug class), weekend/phantom
                               expiries, opens collapsing to zero in a regime, entry minutes drifting off 09:30

Run AFTER the backtest cells complete (quotes.db reads contend with a running backtest).
Usage: audit_fullperiod_data.py <sweep-dir> [since] [until]
"""
import bisect, csv, datetime, json, os, sqlite3, sys
from collections import Counter, defaultdict
from pathlib import Path

# WA_DATA_DIR -> LOCALAPPDATA (native Windows Python — the preferred host for quotes.db work: NTFS-native I/O and same-OS WAL locking as wa.exe) -> WSL /mnt/c fallback.
DATA = Path(os.environ.get('WA_DATA_DIR') or (Path(os.environ['LOCALAPPDATA']) / 'WebullAnalytics' / 'data' if os.environ.get('LOCALAPPDATA') else next(iter(sorted(Path('/mnt/c/Users').glob('*/AppData/Local/WebullAnalytics/data'))), 'MISSING-set-WA_DATA_DIR')))
SINCE = sys.argv[2] if len(sys.argv) > 2 else '2022-01-01'
UNTIL = sys.argv[3] if len(sys.argv) > 3 else '2026-07-20'
RTH_MIN = 380   # minutes; full session is 390, early-close ~210 - flag anything under this that isn't a known half-day

def d8(iso): return int(iso.replace('-', ''))
def iso(d8v): s = str(d8v); return f'{s[:4]}-{s[4:6]}-{s[6:8]}'

spy = {r['date']: float(r['close']) for r in csv.DictReader(open(DATA / 'history' / 'SPY.csv')) if SINCE <= r['date'] <= UNTIL}
sessions = sorted(spy)
con = sqlite3.connect(f'file:{(DATA / "quotes.db").as_posix()}?mode=ro', uri=True)

print(f'== audit {SINCE}..{UNTIL}: {len(sessions)} calendar sessions ==')

# candidate expiries: every weekday in range is a potential listing; probe cheaply via the (root,expiry) PK prefix
row = con.execute("select distinct expiry from quotes where root='SPY' and expiry between ? and ?", (d8(SINCE), d8((datetime.date.fromisoformat(UNTIL) + datetime.timedelta(days=70)).isoformat()))).fetchall()
all_exps = sorted(x[0] for x in row)
print(f'listed expiries in store: {len(all_exps)}')

# H1 - session coverage: a session is covered if ANY in-window expiry has rows that date. Probe the expiry
# nearest 10 DTE for each session first (fast path), widen on miss before declaring the session absent.
def exp_near(day_iso, lo, hi):
    d0 = datetime.date.fromisoformat(day_iso)
    cands = [e for e in all_exps if lo <= (datetime.date.fromisoformat(iso(e)) - d0).days <= hi]
    return cands

missing_sessions, trunc, thin, no_front, no_back, early_ok = [], [], [], [], [], set()
try:
    from importlib import import_module
    # NYSE half-days close 13:00 (210 min); tolerate via RTH check below rather than a calendar dependency
except Exception:
    pass

for day in sessions:
    d = d8(day)
    fronts, backs = exp_near(day, 5, 15), exp_near(day, 30, 45)
    if not fronts or not backs:
        (no_front if not fronts else no_back).append(day); continue
    probe = fronts[len(fronts) // 2]
    r = con.execute("select count(distinct time_sec), count(distinct strike_milli) from quotes where root='SPY' and expiry=? and date=?", (probe, d)).fetchone()
    minutes, strikes = r
    if minutes == 0:
        # widen: any expiry with rows this session?
        hit = any(con.execute("select 1 from quotes where root='SPY' and expiry=? and date=? limit 1", (e, d)).fetchone() for e in fronts + backs)
        if not hit: missing_sessions.append(day); continue
        minutes, strikes = max((con.execute("select count(distinct time_sec), count(distinct strike_milli) from quotes where root='SPY' and expiry=? and date=?", (e, d)).fetchone() for e in fronts + backs), key=lambda t: t[0])
    if minutes < RTH_MIN and minutes not in range(200, 220): trunc.append((day, minutes))   # 210 = legit early close
    expected = 0.2 * spy[day]           # $1-grid strikes inside the ±10% band
    if strikes < 0.6 * expected: thin.append((day, strikes, round(expected)))
    # H4 back-expiry presence check (front verified by the probe above)
    if not any(con.execute("select 1 from quotes where root='SPY' and expiry=? and date=? limit 1", (e, d)).fetchone() for e in backs): no_back.append(day)

print(f'H1 missing sessions: {len(missing_sessions)} {missing_sessions[:8]}')
print(f'H2 truncated sessions (<{RTH_MIN} min, not early-close): {len(trunc)} {trunc[:8]}')
print(f'H3 thin-strike sessions (<60% of 0.2*spot): {len(thin)} {thin[:8]}')
print(f'H4 sessions lacking front 5-15 DTE expiry: {len(no_front)} {no_front[:8]}')
print(f'H4 sessions lacking back 30-45 DTE expiry: {len(no_back)} {no_back[:8]}')

# H5 - crossed/zero NBBO share, sampled at 10:00 on the mid back expiry, ~24 sessions/year
per_year = defaultdict(lambda: [0, 0])
for day in sessions[::max(1, len(sessions) // 120)]:
    backs = exp_near(day, 30, 45)
    if not backs: continue
    e = backs[len(backs) // 2]
    for bid, ask in con.execute("select bid, ask from quotes where root='SPY' and expiry=? and date=? and time_sec=36000", (e, d8(day))):
        per_year[day[:4]][1] += 1
        if bid > ask or (bid == 0 and ask == 0): per_year[day[:4]][0] += 1
print('H5 crossed/zero NBBO share @10:00 (sampled):', {y: f'{c}/{n}' for y, (c, n) in sorted(per_year.items())})

# H7 - per-expiry window completeness. Session-level checks (H1) pass if ANY expiry covers a date, which
# hides a partially-pulled expiry (e.g. a straggler whose re-pull died mid-window: its own chain stops
# early or is empty while neighbors cover the sessions). Three signatures, chosen so late-listing weeklies
# (whose window legitimately STARTS late) don't false-positive:
#   EMPTY      - an expiry in the sealed manifest or the in-window listing set with zero rows
#   HOLE       - missing trading days strictly INSIDE the expiry's own [first, last] observed span
#   TRUNCATED  - last observed day earlier than min(expiry, last completed session) by > 1 trading day
print('\n== H7 per-expiry window completeness ==')
def dcount(a, b):   # trading days in [a, b] inclusive (d8 ints)
    lo = bisect.bisect_left(sessions, iso(a)); hi = bisect.bisect_right(sessions, iso(b))
    return hi - lo
empty, holes, trunc7 = [], [], []
last_session_d8 = d8(sessions[-1])
for e in all_exps:
    n, lo, hi = con.execute("select count(distinct date), min(date), max(date) from quotes where root='SPY' and expiry=? and date between ? and ?", (e, d8(SINCE), d8(UNTIL))).fetchone()
    if n == 0:
        if e <= last_session_d8 or con.execute("select 1 from sealed where root='SPY' and expiry=?", (e,)).fetchone(): empty.append(e)
        continue
    span = dcount(lo, hi)
    if n < span: holes.append((e, span - n))
    tail_target = min(e, last_session_d8)
    gap = dcount(hi, tail_target) - 1
    if gap > 1: trunc7.append((e, iso(hi), gap))
print(f'H7 EMPTY expiries: {len(empty)} {[iso(x) for x in empty[:8]]}')
print(f'H7 expiries with internal HOLES: {len(holes)} {[(iso(e), m) for e, m in holes[:8]]}')
print(f'H7 TRUNCATED tails: {len(trunc7)} {[(iso(e), h, g) for e, h, g in trunc7[:8]]}')

# H6 - fills-side anomalies (cell 1 output)
fills = Path(sys.argv[1]) / 'fills_DC_lots1.jsonl'
if fills.exists():
    opens = Counter(); zero = []; weekend = []; minutes = Counter(); debit_med = defaultdict(list)
    for line in open(fills):
        line = line.strip()
        if not line: continue
        f = json.loads(line)
        if f.get('kind') != 'Open': continue
        y = f['ts'][:4]; opens[y] += 1
        minutes[f['ts'][11:16]] += 1
        net = abs(float(f.get('net', 0))) / max(1, int(f.get('qty', 1))) / 100
        debit_med[y].append(net)
        if net < 0.10: zero.append((f['ts'][:10], round(net, 3)))
        for l in f['legs']:
            e = l['sym'][-15:-9]
            if datetime.date(2000 + int(e[:2]), int(e[2:4]), int(e[4:6])).weekday() > 4: weekend.append((f['ts'][:10], l['sym']))
    days_per_year = Counter(day[:4] for day in sessions)
    print('H6 opens/day by year:', {y: f'{opens[y]}/{days_per_year[y]} = {opens[y]/days_per_year[y]:.0%}' for y in sorted(days_per_year)})
    print('H6 median debit/share by year:', {y: round(sorted(v)[len(v) // 2], 2) for y, v in sorted(debit_med.items())})
    print('H6 near-zero debits (<$0.10/sh):', len(zero), zero[:5])
    print('H6 weekend-expiry legs:', len(weekend), weekend[:5])
    print('H6 entry minutes:', dict(minutes.most_common(5)))
else:
    print(f'H6 skipped - {fills} not present yet (cell still running?)')
