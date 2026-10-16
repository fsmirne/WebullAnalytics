#!/usr/bin/env python3
"""Break-even hedge overlay study for DC2 long diagonals — "buy cheap options at the break-even strike as insurance".

Replays a baseline `wa ai backtest --fills-jsonl` ledger and, for every Open fill, buys `ratio × qty` single options struck at the
diagonal's short-expiry break-even (snapped to the nearest listed strike), held until the lineage's final fill. The diagonal's own
P&L is untouched (the hedge never changes the engine's exit decisions — a pure overlay), so the delta vs baseline is exactly the hedge's
P&L. Prices are real ThetaData minute NBBO from quotes.db, looked up the way QuoteStoreCache does (latest print at-or-before the minute,
≤ 5 min stale; empty/crossed prints = missing). Buy at mid + slip, sell at mid − slip, `fee` per contract per side; a hedge whose
expiry is the exit date and exit is 16:00 settles at intrinsic.

Break-evens: diagonal value at the short expiry = long leg Black-Scholes (sticky-strike IV solved from its open mid, escrowed-discrete
SPY dividends, ^IRX rate of the open date) − short intrinsic; BE = where that equals the per-share debit paid (incl. slippage).

Sides:   same     = call diagonal → call at the UPPER BE; put diagonal → put at the LOWER BE (the proposal as stated)
         opposite = call diagonal → put at the LOWER BE; put diagonal → call at the UPPER BE
         both     = both of the above
Expiry:  short = the diagonal's short-leg expiry (cheapest); long = its long-leg expiry
Offset:  $ moved from the BE toward spot before snapping (0 = at the BE; the 10-15 guard's buffer was ~$2)

MUST run under Windows python.exe (quotes.db is NTFS/WAL — never WSL python over /mnt/c). Pass Windows paths. Example:
    python.exe scripts\\be_hedge_study.py --fills %LOCALAPPDATA%\\WebullAnalytics\\sweeps\\<run>\\fills.jsonl --sides same,opposite,both --expiries short,long --offsets 0,2 --csv-dir %LOCALAPPDATA%\\WebullAnalytics\\sweeps\\be_hedge
"""
import argparse
import bisect
import csv
import functools
import json
import math
import os
import re
import sqlite3
import sys
from collections import defaultdict
from datetime import date, datetime
from pathlib import Path

DATA = Path(os.environ.get('WA_DATA_DIR') or (Path(os.environ['LOCALAPPDATA']) / 'WebullAnalytics' / 'data' if os.environ.get('LOCALAPPDATA') else next(iter(sorted(Path('/mnt/c/Users').glob('*/AppData/Local/WebullAnalytics/data'))), 'MISSING-set-WA_DATA_DIR')))
OCC = re.compile(r'^([A-Z]+)(\d{6})([CP])(\d{8})$')
MAX_STALE_SEC = 5 * 60
SETTLE_SEC = 16 * 3600


def parse_occ(sym):
    m = OCC.match(sym)
    root, ymd, cp, strike = m.groups()
    return root, date(2000 + int(ymd[:2]), int(ymd[2:4]), int(ymd[4:6])), cp, int(strike) / 1000.0


def ymd_int(d):
    return d.year * 10000 + d.month * 100 + d.day


def ncdf(x):
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


def bs(spot, strike, t, r, iv, cp):
    if t <= 0 or iv <= 0:
        return max(0.0, spot - strike) if cp == 'C' else max(0.0, strike - spot)
    d1 = (math.log(spot / strike) + (r + iv * iv / 2.0) * t) / (iv * math.sqrt(t))
    d2 = d1 - iv * math.sqrt(t)
    if cp == 'C':
        return spot * ncdf(d1) - strike * math.exp(-r * t) * ncdf(d2)
    return strike * math.exp(-r * t) * ncdf(-d2) - spot * ncdf(-d1)


def solve_iv(price, spot, strike, t, r, cp):
    lo, hi = 0.005, 4.0
    if bs(spot, strike, t, r, hi, cp) < price:
        return None
    for _ in range(100):
        mid = (lo + hi) / 2.0
        if bs(spot, strike, t, r, mid, cp) < price:
            lo = mid
        else:
            hi = mid
    return (lo + hi) / 2.0


def load_dividends(ticker):
    path = DATA / 'dividends' / f'{ticker}.csv'
    if not path.exists():
        return []
    return [(date.fromisoformat(r['ex_date']), float(r['amount'])) for r in csv.DictReader(open(path))]


def load_rates():
    rows = sorted((date.fromisoformat(r['date']), float(r['rate'])) for r in csv.DictReader(open(DATA / 'rates' / 'IRX.csv')))
    return [d for d, _ in rows], [v for _, v in rows]


def rate_on(rates, d):
    i = bisect.bisect_right(rates[0], d) - 1
    return rates[1][max(i, 0)]


def years(d0, d1):
    return max(0.0, (d1 - d0).days) / 365.0


def div_pv(divs, eval_date, expiry, r):
    """Mirrors OptionMath.DividendPresentValue: ex-dates in (eval_date, expiry], discounted to eval_date."""
    return sum(a * math.exp(-r * years(eval_date, ex)) for ex, a in divs if eval_date < ex <= expiry and a > 0)


class QuoteStore:
    """Point lookups against quotes.db with QuoteStoreCache semantics (latest print at-or-before the minute, ≤ 5 min stale)."""

    def __init__(self, path):
        self.conn = sqlite3.connect(f'file:{path}?mode=ro', uri=True, timeout=60)
        self.strike_cache = {}

    def nbbo(self, root, expiry, d, strike, cp, sec, max_stale=MAX_STALE_SEC):
        row = self.conn.execute('SELECT time_sec, bid, ask FROM quotes WHERE root=? AND expiry=? AND date=? AND strike_milli=? AND right=? AND time_sec<=? ORDER BY time_sec DESC LIMIT 1',
                                (root, ymd_int(expiry), ymd_int(d), int(round(strike * 1000)), cp, sec)).fetchone()
        if row is None or sec - row[0] > max_stale:
            return None
        bid, ask = row[1] / 10000.0, row[2] / 10000.0
        if (bid <= 0 and ask <= 0) or (bid > 0 and ask > 0 and ask < bid):
            return None
        return (bid + ask) / 2.0 if bid > 0 and ask > 0 else max(bid, ask)

    def strikes(self, root, expiry, d, cp):
        key = (root, expiry, d, cp)
        if key not in self.strike_cache:
            rows = self.conn.execute('SELECT DISTINCT strike_milli FROM quotes WHERE root=? AND expiry=? AND date=? AND right=?', (root, ymd_int(expiry), ymd_int(d), cp)).fetchall()
            self.strike_cache[key] = sorted(r[0] / 1000.0 for r in rows)
        return self.strike_cache[key]


def load_trades(path, since, until):
    """Closed lineages only (an open-at-until lineage is a pure debit — see reference_fills_jsonl_closed_only)."""
    by_lineage = defaultdict(list)
    for line in open(path):
        if line.strip():
            f = json.loads(line)
            by_lineage[f['lineage']].append(f)
    trades = []
    for lin, rows in by_lineage.items():
        opens = [f for f in rows if f['kind'] == 'Open']
        exits = [f for f in rows if f['kind'] != 'Open']
        if not opens or not exits:
            continue
        if (since and opens[0]['ts'][:10] < since) or (until and opens[0]['ts'][:10] > until):
            continue
        trades.append({'lineage': lin, 'opens': opens, 'exit': rows[-1], 'pnl': sum(f['net'] - f['fees'] for f in rows)})
    trades.sort(key=lambda t: (t['exit']['ts'], t['lineage']))
    return trades


def diag_legs(open_fill):
    short = next(l for l in open_fill['legs'] if l['side'] == 'Sell')
    long = next(l for l in open_fill['legs'] if l['side'] == 'Buy')
    return parse_occ(short['sym']), parse_occ(long['sym']), long['price']


def excess_at_short_expiry(short_k, cp, long_k, long_iv, t_rem, r, div_after, debit, s):
    """Diagonal value per share at the short expiry, less the debit paid — positive inside the break-evens."""
    long_val = bs(max(s - div_after, 0.01), long_k, t_rem, r, long_iv, cp)
    short_intr = max(0.0, s - short_k) if cp == 'C' else max(0.0, short_k - s)
    return long_val - short_intr - debit


def bisect_root(f, lo, hi):
    flo = f(lo)
    for _ in range(80):
        mid = (lo + hi) / 2.0
        if (f(mid) > 0) == (flo > 0):
            lo, flo = mid, f(mid)
        else:
            hi = mid
    return (lo + hi) / 2.0


def break_evens(open_fill, divs, rates):
    """(lower, upper) short-expiry break-evens per share; either is None when the payoff never crosses the debit on that side."""
    (_, short_exp, cp, short_k), (_, long_exp, _, long_k), long_mid = diag_legs(open_fill)
    d0 = date.fromisoformat(open_fill['ts'][:10])
    spot, r = open_fill['spot'], rate_on(rates, d0)
    debit = -open_fill['net'] / 100.0 / open_fill['qty']
    long_iv = solve_iv(long_mid, max(spot - div_pv(divs, d0, long_exp, r), 0.01), long_k, years(d0, long_exp), r, cp)
    if long_iv is None:
        return None, None
    excess = functools.partial(excess_at_short_expiry, short_k, cp, long_k, long_iv, years(short_exp, long_exp), r, div_pv(divs, short_exp, long_exp, r), debit)
    grid = [spot * (0.6 + 0.8 * i / 800.0) for i in range(801)]
    vals = [excess(s) for s in grid]
    peak = max(range(len(grid)), key=lambda i: vals[i])
    if vals[peak] <= 0:
        return None, None
    lower = bisect_root(excess, grid[0], grid[peak]) if vals[0] < 0 else None
    upper = bisect_root(excess, grid[peak], grid[-1]) if vals[-1] < 0 else None
    return lower, upper


def hedge_legs(open_fill, lower, upper, side):
    """[(cp, target_strike)] for the variant's side rule."""
    cp = diag_legs(open_fill)[0][2]
    same = ('C', upper) if cp == 'C' else ('P', lower)
    opposite = ('P', lower) if cp == 'C' else ('C', upper)
    return {'same': [same], 'opposite': [opposite], 'both': [same, opposite]}[side]


def pick_strike(store, root, expiry, d, cp, target, spot, sec):
    """Nearest listed strike to target with a live quote at sec; ties go further OTM (cheaper). Returns (strike, mid) or (None, None)."""
    otm_sign = 1 if cp == 'C' else -1
    ranked = sorted(store.strikes(root, expiry, d, cp), key=lambda k: (abs(k - target), -otm_sign * (k - spot)))
    for k in ranked[:4]:
        mid = store.nbbo(root, expiry, d, k, cp, sec)
        if mid is not None:
            return k, mid
    return None, None


def ts_parts(ts):
    dt = datetime.fromisoformat(ts)
    return dt.date(), dt.hour * 3600 + dt.minute * 60 + dt.second


def hedge_exit_value(store, root, expiry, strike, cp, exit_fill):
    """(per-share value, settled, stale) at the lineage's final fill."""
    d, sec = ts_parts(exit_fill['ts'])
    if expiry <= d and sec >= SETTLE_SEC:
        s = exit_fill['spot']
        return (max(0.0, s - strike) if cp == 'C' else max(0.0, strike - s)), True, False
    mid = store.nbbo(root, expiry, d, strike, cp, sec)
    if mid is not None:
        return mid, False, False
    mid = store.nbbo(root, expiry, d, strike, cp, sec, max_stale=sec)
    return (mid if mid is not None else 0.0), False, True


def run_variant(trades, store, divs, rates, side, expiry_mode, offset, ratio, slip, fee):
    out, skipped, stale = [], 0, 0
    for t in trades:
        hedge_pnl, hedge_cost, notes = 0.0, 0.0, []
        for o in t['opens']:
            lower, upper = break_evens(o, divs, rates)
            (root, short_exp, _, _), (_, long_exp, _, _), _ = diag_legs(o)
            expiry = short_exp if expiry_mode == 'short' else long_exp
            d, sec = ts_parts(o['ts'])
            qty = ratio * o['qty']
            for cp, target in hedge_legs(o, lower, upper, side):
                if target is None:
                    skipped += 1
                    notes.append(f'{cp}:noBE')
                    continue
                target = target - offset if cp == 'C' else target + offset
                k, mid = pick_strike(store, root, expiry, d, cp, target, o['spot'], sec)
                if k is None:
                    skipped += 1
                    notes.append(f'{cp}:noQuote@{target:.1f}')
                    continue
                buy = mid + slip
                value, settled, was_stale = hedge_exit_value(store, root, expiry, k, cp, t['exit'])
                stale += was_stale
                sell = value if settled else max(0.0, value - slip)
                fees = fee * qty * (1 if settled else 2)
                hedge_cost += buy * 100 * qty
                hedge_pnl += (sell - buy) * 100 * qty - fees
                notes.append(f'{cp}{k:g}@{buy:.2f}->{sell:.2f}')
        out.append({**t, 'hedge_pnl': hedge_pnl, 'hedge_cost': hedge_cost, 'notes': ' '.join(notes)})
    return out, skipped, stale


def stats(pnls):
    wins = sum(p for p in pnls if p > 0)
    losses = -sum(p for p in pnls if p < 0)
    peak = eq = dd = 0.0
    for p in pnls:
        eq += p
        peak = max(peak, eq)
        dd = max(dd, peak - eq)
    return {'n': len(pnls), 'pnl': sum(pnls), 'pf': wins / losses if losses else float('inf'), 'win': sum(p > 0 for p in pnls) / len(pnls) if pnls else 0.0, 'dd': dd}


def fmt(s):
    return f"${s['pnl']:>10,.0f}  PF {s['pf']:.2f}  win {s['win']:.0%}  maxDD ${s['dd']:,.0f}"


def report(label, rows, skipped, stale):
    base = [r['pnl'] for r in rows]
    hedged = [r['pnl'] + r['hedge_pnl'] for r in rows]
    paid = [r for r in rows if r['hedge_cost'] > 0]
    losers = [r for r in rows if r['pnl'] < 0]
    print(f'\n=== {label} ===  (hedge skipped: {skipped} legs, stale exit marks: {stale})')
    print(f'  baseline: {fmt(stats(base))}')
    print(f'  hedged:   {fmt(stats(hedged))}')
    print(f"  hedge: premium ${sum(r['hedge_cost'] for r in rows):,.0f}, net ${sum(r['hedge_pnl'] for r in rows):,.0f}, paid off on {sum(r['hedge_pnl'] > 0 for r in paid)}/{len(paid)} trades")
    print(f"  on baseline losers ({len(losers)}): loss ${sum(r['pnl'] for r in losers):,.0f}, hedge recovered ${sum(r['hedge_pnl'] for r in losers):,.0f}; on winners: hedge ${sum(r['hedge_pnl'] for r in rows if r['pnl'] >= 0):,.0f}")
    by_year = defaultdict(list)
    for r in rows:
        by_year[r['exit']['ts'][:4]].append(r)
    for y in sorted(by_year):
        yr = by_year[y]
        print(f"    {y}: base {fmt(stats([r['pnl'] for r in yr]))}  |  hedged {fmt(stats([r['pnl'] + r['hedge_pnl'] for r in yr]))}")


def write_csv(path, rows):
    with open(path, 'w', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['lineage', 'open_ts', 'exit_ts', 'exit_rule', 'spot', 'base_pnl', 'hedge_cost', 'hedge_pnl', 'hedged_pnl', 'legs'])
        for r in rows:
            o, e = r['opens'][0], r['exit']
            w.writerow([r['lineage'], o['ts'], e['ts'], e.get('rule') or e['kind'], o['spot'], f"{r['pnl']:.2f}", f"{r['hedge_cost']:.2f}", f"{r['hedge_pnl']:.2f}", f"{r['pnl'] + r['hedge_pnl']:.2f}", r['notes']])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--fills', required=True, help='baseline --fills-jsonl ledger (Windows path)')
    ap.add_argument('--db', default=str(DATA / 'quotes.db'))
    ap.add_argument('--ratio', type=float, default=0.5, help='hedge contracts per diagonal contract')
    ap.add_argument('--sides', default='same', help='comma list of same|opposite|both')
    ap.add_argument('--expiries', default='short', help='comma list of short|long')
    ap.add_argument('--offsets', default='0', help='comma list of $ offsets from the BE toward spot')
    ap.add_argument('--slip', type=float, default=0.02, help='per-share slippage on each hedge buy and sell')
    ap.add_argument('--fee', type=float, default=0.05, help='per-contract fee per side')
    ap.add_argument('--since')
    ap.add_argument('--until')
    ap.add_argument('--csv-dir', help='write one per-trade CSV per variant here')
    args = ap.parse_args()

    trades = load_trades(args.fills, args.since, args.until)
    ticker = trades[0]['opens'][0]['ticker']
    divs, rates = load_dividends(ticker), load_rates()
    store = QuoteStore(args.db)
    print(f'{len(trades)} closed lineages from {args.fills}; ratio {args.ratio}, slip {args.slip}, fee {args.fee}')
    for side in args.sides.split(','):
        for expiry_mode in args.expiries.split(','):
            for offset in (float(x) for x in args.offsets.split(',')):
                label = f'side={side} expiry={expiry_mode} offset={offset:g}'
                rows, skipped, stale = run_variant(trades, store, divs, rates, side, expiry_mode, offset, args.ratio, args.slip, args.fee)
                report(label, rows, skipped, stale)
                if args.csv_dir:
                    Path(args.csv_dir).mkdir(parents=True, exist_ok=True)
                    write_csv(Path(args.csv_dir) / f'be_hedge_{side}_{expiry_mode}_{offset:g}.csv', rows)
                sys.stdout.flush()


if __name__ == '__main__':
    main()
