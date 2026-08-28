#!/usr/bin/env python3
"""GEX wall-as-barrier study (pre-registered: scripts/gex_wall_barrier_study.md).

Tests: is a put/call GEX wall (largest single-strike OI*gamma concentration) LESS likely to be
breached intraday than a distance-matched level on the same day? Reuses the OI/quotes/tape
loaders from gex_gravity_range_study.py (SPXW 0DTE) — no new data pipeline.

  python.exe scripts/gex_wall_barrier_study.py --data-dir <data> assemble   -> writes gex_wall_barrier_daily.csv
  python.exe scripts/gex_wall_barrier_study.py stats                       -> pre-registered gates
"""
import argparse, csv, math, os, random, sqlite3, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gex_gravity_range_study import (  # noqa: E402
    ET, load_tape, rth, solve_iv, bs_gamma, load_rates, rate_for, parse_oi_0dte, quotes_at,
)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_CSV = os.path.join(HERE, "gex_wall_barrier_daily.csv")


def walls_at(oi_map, mids, spot, T, r):
    """Full-range (uncapped) per-strike CallGex/PutGex, mirroring the fixed GexMatrix.FullCells /
    FindWalls — every strike with computable gamma contributes, no --max-strikes-style cap.
    Returns dict with put_wall/call_wall strikes, their Gex, second-best same-side Gex (for the
    wall-strength ratio), and the raw per-side maps (for the within-day distance-matched control).
    """
    call_gex, put_gex = {}, {}
    for (K, right), oi in oi_map.items():
        mid = mids.get((K, right))
        if mid is None:
            continue
        iv = solve_iv(spot, K, T, r, mid, right == "C")
        if iv is None:
            continue
        g = bs_gamma(spot, K, T, r, iv)
        w = oi * g * 100.0 * spot
        if w <= 0:
            continue
        if right == "C":
            call_gex[K] = call_gex.get(K, 0.0) + w
        else:
            put_gex[K] = put_gex.get(K, 0.0) + w

    def top2(d, side_filter):
        cands = sorted(((k, v) for k, v in d.items() if side_filter(k)), key=lambda kv: -kv[1])
        best = cands[0] if cands else None
        second = cands[1][1] if len(cands) > 1 else 0.0
        return best, second

    put_best, put_second = top2(put_gex, lambda k: k < spot)
    call_best, call_second = top2(call_gex, lambda k: k > spot)
    return {
        "put_wall": put_best[0] if put_best else None, "put_wall_gex": put_best[1] if put_best else 0.0, "put_second": put_second,
        "call_wall": call_best[0] if call_best else None, "call_wall_gex": call_best[1] if call_best else 0.0, "call_second": call_second,
        "call_gex": call_gex, "put_gex": put_gex,
    }


def within_day_control(side_gex, wall_strike, wall_dist, spot, low, high, is_put, tol=0.25):
    """Among OTHER same-side strikes on the SAME day within +-tol*wall_dist of wall_dist, what
    fraction got breached? This holds the day's actual realized vol/path fixed, so it isolates
    the wall's own effect from vol-regime confounds (the trap that killed gamma-target-level and
    gravity-distance without a matched control)."""
    lo_d, hi_d = wall_dist * (1 - tol), wall_dist * (1 + tol)
    breaches = []
    for K in side_gex:
        if K == wall_strike:
            continue
        d = (spot - K) if is_put else (K - spot)
        if d <= 0 or not (lo_d <= d <= hi_d):
            continue
        breached = (low <= K) if is_put else (high >= K)
        breaches.append(1.0 if breached else 0.0)
    return (sum(breaches) / len(breaches)) if breaches else None


def assemble(data_dir):
    oi_dir = os.path.join(data_dir, "oi", "SPXW")
    tape_dir = os.path.join(data_dir, "intraday", "SPXW")
    rates = load_rates(data_dir)
    conn = sqlite3.connect(os.path.join(data_dir, "quotes.db"))
    oi_days = sorted(f[:-6] for f in os.listdir(oi_dir) if f.endswith(".jsonl"))
    tape_days = sorted(f[:-4] for f in os.listdir(tape_dir) if f.endswith(".csv"))
    excl, rows_out = {}, []

    for d in oi_days:
        day_int = int(d.replace("-", ""))
        day_compact = d[2:4] + d[5:7] + d[8:10]
        tp = os.path.join(tape_dir, d + ".csv")
        if not os.path.exists(tp):
            excl[d] = "no tape"; continue
        bars = rth(load_tape(tp))
        if not bars:
            excl[d] = "empty rth tape"; continue
        pre935 = [b for b in bars if (b[0].hour, b[0].minute) <= (9, 34)]
        rest = [b for b in bars if (b[0].hour, b[0].minute) >= (9, 35)]
        if not pre935 or len(rest) < 60:
            excl[d] = "tape too thin around 9:35"; continue
        spot935 = pre935[-1][4]
        low = min(b[3] for b in rest)
        high = max(b[2] for b in rest)

        oi_map = parse_oi_0dte(os.path.join(oi_dir, d + ".jsonl"), day_compact)
        if not oi_map:
            excl[d] = "no 0dte oi"; continue
        mids, tsec = quotes_at(conn, day_int, (34500, 34560, 34440))
        if not mids:
            excl[d] = "no 9:35 quotes"; continue
        bars_last = bars[-1][0]
        early_close = (bars_last.hour, bars_last.minute) < (15, 30)
        close_et = 13 if early_close else 16
        T = max((close_et * 3600 - tsec) / 3600.0, 0.25) / 24.0 / 365.0
        r = rate_for(rates, d)

        w = walls_at(oi_map, mids, spot935, T, r)
        if w["put_wall"] is None or w["call_wall"] is None:
            excl[d] = "no computable wall on one side"; continue

        put_dist = spot935 - w["put_wall"]
        call_dist = w["call_wall"] - spot935
        put_breach = 1 if low <= w["put_wall"] else 0
        call_breach = 1 if high >= w["call_wall"] else 0
        put_ctrl = within_day_control(w["put_gex"], w["put_wall"], put_dist, spot935, low, high, is_put=True)
        call_ctrl = within_day_control(w["call_gex"], w["call_wall"], call_dist, spot935, low, high, is_put=False)

        # G5 diagnostic: is the wall just the nearest listed strike to spot on its side?
        nearest_put = max((k for k in w["put_gex"] if k < spot935), default=None)
        nearest_call = min((k for k in w["call_gex"] if k > spot935), default=None)

        rows_out.append({
            "date": d, "spot935": f"{spot935:.2f}",
            "put_wall": f"{w['put_wall']:.0f}", "put_dist": f"{put_dist:.2f}", "put_strength": f"{(w['put_wall_gex'] / w['put_second']) if w['put_second'] > 0 else 99:.2f}",
            "put_breach": put_breach, "put_ctrl_rate": ("" if put_ctrl is None else f"{put_ctrl:.4f}"),
            "put_is_nearest": int(nearest_put == w["put_wall"]) if nearest_put is not None else 0,
            "call_wall": f"{w['call_wall']:.0f}", "call_dist": f"{call_dist:.2f}", "call_strength": f"{(w['call_wall_gex'] / w['call_second']) if w['call_second'] > 0 else 99:.2f}",
            "call_breach": call_breach, "call_ctrl_rate": ("" if call_ctrl is None else f"{call_ctrl:.4f}"),
            "call_is_nearest": int(nearest_call == w["call_wall"]) if nearest_call is not None else 0,
            "early_close": int(early_close),
        })
        if len(rows_out) % 50 == 0:
            print(f"  {len(rows_out)} days assembled (through {d})", flush=True)

    with open(OUT_CSV, "w", newline="") as f:
        cw = csv.DictWriter(f, fieldnames=list(rows_out[0].keys()))
        cw.writeheader()
        cw.writerows(rows_out)
    print(f"wrote {len(rows_out)} rows -> {OUT_CSV}")
    print(f"exclusions ({len(excl)}):")
    for d, why in excl.items():
        print(f"  {d}: {why}")


def _ttest_1samp_mean0(diffs):
    import numpy as np
    diffs = np.asarray(diffs, dtype=float)
    n = len(diffs)
    if n < 2:
        return float("nan"), float("nan")
    mean = diffs.mean()
    se = diffs.std(ddof=1) / math.sqrt(n)
    t = mean / se if se > 0 else float("nan")
    return mean, t


def stats():
    rows = list(csv.DictReader(open(OUT_CSV, newline="")))
    rows = [r for r in rows if r["put_ctrl_rate"] and r["call_ctrl_rate"]]
    n = len(rows)
    print(f"n with matched control on both sides = {n}")

    for side in ("put", "call"):
        breach = [int(r[f"{side}_breach"]) for r in rows]
        ctrl = [float(r[f"{side}_ctrl_rate"]) for r in rows]
        dist = [float(r[f"{side}_dist"]) for r in rows]
        strength = [float(r[f"{side}_strength"]) for r in rows]
        is_nearest = [int(r[f"{side}_is_nearest"]) for r in rows]
        diffs = [b - c for b, c in zip(breach, ctrl)]
        mean_diff, t = _ttest_1samp_mean0(diffs)

        print(f"\n== {side.upper()} side (n={n}) ==")
        print(f"  wall breach rate      = {sum(breach) / n:.3f}")
        print(f"  matched-ctrl rate     = {sum(ctrl) / n:.3f}")
        print(f"  G1 pooled edge (wall - ctrl) = {mean_diff:+.4f}  t = {t:+.2f}  {'PASS' if (mean_diff < 0 and abs(t) >= 2) else 'FAIL'}")
        print(f"  median distance = {sorted(dist)[n // 2]:.1f} pts")
        print(f"  G5 wall == nearest-listed-strike on {sum(is_nearest)}/{n} days ({sum(is_nearest) / n * 100:.0f}%) -> {'FAIL (trivial)' if sum(is_nearest) / n > 0.5 else 'PASS (distinct from trivial control)'}")

        # G3: strength-threshold robustness
        strong_idx = [i for i, s in enumerate(strength) if s >= 1.5]
        if len(strong_idx) >= 20:
            sd = [diffs[i] for i in strong_idx]
            m2, t2 = _ttest_1samp_mean0(sd)
            print(f"  G3 strength>=1.5x (n={len(strong_idx)}): edge {m2:+.4f} t {t2:+.2f} {'(stronger, as predicted)' if m2 < mean_diff else '(not stronger — suspect)'}")
        else:
            print(f"  G3 strength>=1.5x: only n={len(strong_idx)}, too few to judge")

        # G4: sign-flip randomization test on the paired per-day differences. Re-pairing breach_i
        # with a shuffled ctrl_j is degenerate here (sum/mean of ctrl is permutation-invariant, so
        # mean(breach - ctrl_shuffled) == mean(breach) - mean(ctrl) on EVERY shuffle — it tests
        # nothing). The valid null for "is the mean paired difference 0" is that each day's sign is
        # equally likely +/-: randomly flip sign(diff_i) and recompute the mean, 2000x.
        rnd = random.Random(1234)
        perm_means = []
        for _ in range(2000):
            flipped = [d if rnd.random() < 0.5 else -d for d in diffs]
            perm_means.append(sum(flipped) / n)
        more_extreme = sum(1 for pm in perm_means if pm <= mean_diff) if mean_diff < 0 else sum(1 for pm in perm_means if pm >= mean_diff)
        p = more_extreme / len(perm_means)
        print(f"  G4 sign-flip p = {p:.4f} {'PASS' if p <= 0.05 else 'FAIL'}")

        # G2: sub-period consistency (quartiles by date order)
        q = n // 4
        print("  G2 sub-period edges (quartiles by date):")
        for i in range(4):
            lo, hi = i * q, (i + 1) * q if i < 3 else n
            sd = diffs[lo:hi]
            if sd:
                m3, t3 = _ttest_1samp_mean0(sd)
                print(f"    Q{i + 1} n={len(sd)}: edge {m3:+.4f} t {t3:+.2f}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["assemble", "stats"])
    ap.add_argument("--data-dir", default=None)
    a = ap.parse_args()
    if a.mode == "stats":
        stats()
    else:
        if not a.data_dir:
            sys.exit("--data-dir required for assemble")
        assemble(a.data_dir)
