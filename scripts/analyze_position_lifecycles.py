#!/usr/bin/env python3
"""
Reconstruct option position lifecycles from the Webull orders CSV and attribute
realized P&L to the OPENING family (the strategy name on the first fill of each
lineage).

A "lineage" is an economic position traced from opening to close. An order
(strategy or standalone) adjusts an existing lineage iff any of its legs
matches an open leg of that lineage (same symbol). If no lineage is touched,
the order opens a new lineage — and that lineage's "opening family" is set
from the order's strategy-name label (Calendar, Diagonal, Vertical, etc.).

Only CLOSED lineages (sum of open-leg qtys == 0) have realized P&L. Still-open
positions are reported separately with their current running-cash so you can
see active-book status.

USAGE

    python3 scripts/analyze_position_lifecycles.py \
        --csv data/Webull_Orders_Records_Options.csv \
        --since 2026-01-01

    # Examples:
    python3 scripts/analyze_position_lifecycles.py                 # defaults to 2026-01-01+
    python3 scripts/analyze_position_lifecycles.py --since 2025-07-01
    python3 scripts/analyze_position_lifecycles.py --until 2026-04-01

NOTES

- Cash sign convention: positive running_cash means net cash PAID (debit); a closed
  lineage's realized P&L is `-running_cash` (credit received minus debit paid).
- Single-leg fills (Webull rows with both Name and Symbol populated on the parent)
  are IGNORED — this analyzer focuses on multi-leg strategies.
- Family labels come straight from the Webull Name column (e.g., "GME Calendar"
  becomes family "Calendar"). The ticker is the first whitespace token.
- Cross-family adjustments (e.g., Calendar → Diagonal) are detected via the
  family_path list on each lineage.
"""

import argparse
import csv
from collections import defaultdict
from datetime import datetime


def parse_row(row):
    """Return dict with normalized fields, or None if not a filled trade row."""
    if row['Status'] != 'Filled':
        return None
    try:
        qty = int(row['Filled'])
    except ValueError:
        return None
    if qty <= 0:
        return None
    try:
        price = float(row['Avg Price'])
    except ValueError:
        return None
    ft = row['Filled Time']
    if not ft:
        return None
    for fmt in ('%m/%d/%Y %H:%M:%S EDT', '%m/%d/%Y %H:%M:%S EST', '%m/%d/%Y %H:%M:%S'):
        try:
            dt = datetime.strptime(ft.strip(), fmt)
            break
        except ValueError:
            continue
    else:
        return None
    return {
        'name': row['Name'].strip(),
        'symbol': row['Symbol'].strip(),
        'side': row['Side'],
        'qty': qty,
        'price': price,
        'filled_time': dt,
    }


def load_orders(path):
    """Group CSV rows into orders. Each order is either a parent row (strategy name in
    Name column, Symbol empty) followed by per-leg rows (Name empty, Symbol populated),
    or a standalone single-leg fill (skipped here)."""
    orders = []
    with open(path) as f:
        rows = [parse_row(r) for r in csv.DictReader(f)]

    i = 0
    while i < len(rows):
        r = rows[i]
        if r is None:
            i += 1
            continue
        if r['name'] and not r['symbol']:
            parent = r
            legs = []
            j = i + 1
            while j < len(rows) and rows[j] is not None and not rows[j]['name']:
                legs.append(rows[j])
                j += 1
            if legs:
                tokens = parent['name'].split() if parent['name'] else []
                orders.append({
                    'family': tokens[-1] if tokens else 'Unknown',
                    'ticker': tokens[0] if tokens else '',
                    'filled_time': parent['filled_time'],
                    'legs': [{'symbol': l['symbol'], 'side': l['side'], 'qty': l['qty'], 'price': l['price']} for l in legs],
                    'total_qty': parent['qty'],
                })
            i = j
        else:
            i += 1  # standalone or orphan; skip

    orders.sort(key=lambda o: o['filled_time'])
    return orders


def lineage_touches_order(lineage, order):
    return any(leg['symbol'] in lineage['open_legs'] for leg in order['legs'])


def apply_order_to_lineage(lineage, order):
    """Mutate lineage: add the order's cash, update open legs (buy adds, sell reduces)."""
    cash = 0.0
    for leg in order['legs']:
        cash += (1 if leg['side'] == 'Buy' else -1) * leg['qty'] * leg['price'] * 100
        delta = leg['qty'] if leg['side'] == 'Buy' else -leg['qty']
        new_signed = lineage['open_legs'].get(leg['symbol'], 0) + delta
        if new_signed == 0:
            lineage['open_legs'].pop(leg['symbol'], None)
        else:
            lineage['open_legs'][leg['symbol']] = new_signed
    lineage['running_cash'] += cash
    lineage['order_count'] += 1
    lineage['total_qty'] += order['total_qty']


def build_lineages(orders):
    active = []
    closed = []
    for order in orders:
        touched = [lin for lin in active if lineage_touches_order(lin, order)]
        if not touched:
            lin = {
                'opening_family': order['family'],
                'opening_ticker': order['ticker'],
                'opening_time': order['filled_time'],
                'open_legs': {},
                'running_cash': 0.0,
                'order_count': 0,
                'total_qty': 0,
                'family_path': [],
            }
            active.append(lin)
            lin['family_path'].append(order['family'])
            apply_order_to_lineage(lin, order)
        else:
            lin = touched[0]
            if order['family'] not in lin['family_path']:
                lin['family_path'].append(order['family'])
            apply_order_to_lineage(lin, order)
            for other in touched[1:]:
                lin['running_cash'] += other['running_cash']
                for sym, q in other['open_legs'].items():
                    lin['open_legs'][sym] = lin['open_legs'].get(sym, 0) + q
                    if lin['open_legs'][sym] == 0:
                        lin['open_legs'].pop(sym)
                lin['order_count'] += other['order_count']
                lin['total_qty'] += other['total_qty']
                lin['family_path'].extend(f for f in other['family_path'] if f not in lin['family_path'])
                active.remove(other)

        if not lin['open_legs']:
            closed.append(lin)
            active.remove(lin)
    return closed, active


def summarize(closed, active):
    by_opening = defaultdict(lambda: {
        'n': 0, 'qty': 0, 'realized_cash': 0.0,
        'cross_family': 0, 'multi_order': 0, 'avg_orders': 0.0,
        'pnl_single': 0.0, 'pnl_rolled': 0.0, 'n_single': 0, 'n_rolled': 0,
    })
    for lin in closed:
        f = lin['opening_family']
        pnl = -lin['running_cash']
        b = by_opening[f]
        b['n'] += 1
        b['qty'] += lin['total_qty']
        b['realized_cash'] += pnl
        b['avg_orders'] += lin['order_count']
        if len(set(lin['family_path'])) > 1:
            b['cross_family'] += 1
        if lin['order_count'] > 2:
            b['multi_order'] += 1
            b['pnl_rolled'] += pnl
            b['n_rolled'] += 1
        else:
            b['pnl_single'] += pnl
            b['n_single'] += 1

    print(f"\n{'Opening family':<14} {'Closed':>7} {'Rolled':>7} {'CrossFam':>9} {'Qty':>8} {'Net P&L':>12} {'AvgOrds':>9}")
    print('-' * 74)
    total = 0.0
    for f, v in sorted(by_opening.items(), key=lambda x: -x[1]['realized_cash']):
        total += v['realized_cash']
        avg_orders = v['avg_orders'] / v['n'] if v['n'] else 0
        print(f"{f:<14} {v['n']:>7} {v['multi_order']:>7} {v['cross_family']:>9} {v['qty']:>8} {v['realized_cash']:>+12,.0f} {avg_orders:>9.1f}")
    print('-' * 74)
    print(f"{'TOTAL CLOSED':<14} {sum(v['n'] for v in by_opening.values()):>7} {'':>7} {'':>9} {'':>8} {total:>+12,.0f}")

    print('\nRolled vs single-shot by opening family:')
    for f, v in sorted(by_opening.items(), key=lambda x: -x[1]['realized_cash']):
        if v['n_single'] == 0 and v['n_rolled'] == 0:
            continue
        s_avg = v['pnl_single'] / v['n_single'] if v['n_single'] else 0
        r_avg = v['pnl_rolled'] / v['n_rolled'] if v['n_rolled'] else 0
        print(f"  {f:<14} single-shot: n={v['n_single']:<3} avg P&L={s_avg:+,.0f}  │  rolled: n={v['n_rolled']:<3} avg P&L={r_avg:+,.0f}")

    print(f'\nStill open: {len(active)} lineage(s)')
    for lin in active:
        path = ' → '.join(lin['family_path'])
        print(f"  opened {lin['opening_time'].date()} as {lin['opening_family']}: {lin['order_count']} order(s), path [{path}], running P&L ${-lin['running_cash']:+,.0f}")

    print('\nCross-family adjustment paths (closed only):')
    path_counts = defaultdict(int)
    path_pnl = defaultdict(float)
    for lin in closed:
        if len(set(lin['family_path'])) > 1:
            key = tuple(lin['family_path'])
            path_counts[key] += 1
            path_pnl[key] += -lin['running_cash']
    if not path_counts:
        print('  (none — all closed lineages stayed within a single family label)')
    for path, n in sorted(path_counts.items(), key=lambda x: -path_pnl[x[0]]):
        print(f"  {' → '.join(path):<40} n={n}  P&L={path_pnl[path]:+,.0f}")


def main():
    ap = argparse.ArgumentParser(description='Reconstruct position lifecycles from Webull CSV.')
    ap.add_argument('--csv', default='data/Webull_Orders_Records_Options.csv', help='Path to Webull orders CSV')
    ap.add_argument('--since', default='2026-01-01', help='YYYY-MM-DD lower bound (inclusive) on Filled Time')
    ap.add_argument('--until', default=None, help='YYYY-MM-DD upper bound (inclusive) on Filled Time')
    args = ap.parse_args()

    since = datetime.strptime(args.since, '%Y-%m-%d') if args.since else None
    until = datetime.strptime(args.until, '%Y-%m-%d') if args.until else None

    orders = load_orders(args.csv)
    if since:
        orders = [o for o in orders if o['filled_time'] >= since]
    if until:
        orders = [o for o in orders if o['filled_time'] <= until.replace(hour=23, minute=59, second=59)]
    print(f'Loaded {len(orders)} filled multi-leg orders from {args.since}{" to " + args.until if args.until else "+"}')

    closed, active = build_lineages(orders)
    print(f'Reconstructed {len(closed)} closed lineages, {len(active)} still open')
    summarize(closed, active)


if __name__ == '__main__':
    main()
