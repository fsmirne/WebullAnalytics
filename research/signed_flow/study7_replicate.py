"""Study 7 replication (registered continuation, 2026-08-19): apply the FROZEN selector VERBATIM —
decisiveness <= 0.33825338253382536 AND vol >= 5034 AND IV <= 0.11450683646497142 — to new data, no
retraining, thresholds untouched. Endpoints per registration: E1 (selected builds' mean S2 pair excess > 0,
day-clustered sign-flip p < 0.05) and E2 (mean ABSOLUTE return to expiry >= 1.5 x mean entry half-spread).
NOT EVALUABLE if selected n < 30 (e.g. the SPY-scale IV term may select nothing on a higher-IV root — that
outcome is recorded as 'selector root-specific', not retrained around).

Usage: python research\\signed_flow\\study7_replicate.py --root QQQ --start 2022-01-01 --end 2026-08-17
       python research\\signed_flow\\study7_replicate.py --root SPY --start 2022-01-01 --end 2024-12-31
"""
import argparse
import json
import math
import random
import re
import sqlite3
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from import_quotes_sqlite import resolve_data_dir  # noqa: E402

SEL_DECIS, SEL_VOL, SEL_IV = 0.33825338253382536, 5034, 0.11450683646497142
RATE = 0.043


def bs_delta(spot, strike, t_years, iv, is_call):
    if iv <= 0 or t_years <= 0 or spot <= 0 or strike <= 0:
        return None
    d1 = (math.log(spot / strike) + (RATE + 0.5 * iv * iv) * t_years) / (iv * math.sqrt(t_years))
    nd1 = 0.5 * (1.0 + math.erf(d1 / math.sqrt(2.0)))
    return nd1 if is_call else nd1 - 1.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--start", required=True)
    ap.add_argument("--end", required=True)
    a = ap.parse_args()
    root = a.root.upper()
    data = resolve_data_dir()
    occ = re.compile(rf"^{root}(\d{{6}})([CP])(\d{{8}})$")
    days = sorted(p.stem for p in (data / "oi" / root).glob("????-??-??.jsonl") if a.start <= p.stem <= a.end)
    hist = {}
    for line in (data / "history" / f"{root}.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        hist[f[0]] = float(f[4])
    conn = sqlite3.connect(f"file:{data / 'quotes.db'}?mode=ro", uri=True)
    LO, HI = 15 * 3600 + 1800, 16 * 3600

    def snapshot(day):
        p = data / "oi" / root / f"{day}.jsonl"
        rec = json.loads(p.read_text().strip().splitlines()[-1])
        out = {}
        for o in rec.get("options", []):
            m = occ.match(o.get("symbol") or "")
            if m:
                out[(int("20" + m.group(1)), int(m.group(3)), m.group(2))] = (o.get("volume") or 0, o.get("openInterest") or 0, o.get("iv") or 0.0)
        return rec.get("underlyingPrice"), out

    def mid_at(exp, k, r, date_int):
        row = conn.execute(f"SELECT bid, ask FROM quotes WHERE root='{root}' AND expiry=? AND date=? AND strike_milli=? AND right=? "
                           "AND time_sec BETWEEN ? AND ? AND bid > 0 AND ask > 0 ORDER BY time_sec DESC LIMIT 1",
                           (exp, date_int, k, r, LO, HI)).fetchone()
        return ((row[0] + row[1]) / 2.0 / 10000.0, (row[1] - row[0]) / 2.0 / 10000.0) if row else (None, None)

    selected = []
    scanned_builds = 0
    for i in range(len(days) - 1):
        T, T1 = days[i], days[i + 1]
        spot, snap = snapshot(T)
        _, nxt = snapshot(T1)
        if not spot or not snap or not nxt:
            continue
        d_int = int(T.replace("-", ""))
        deltas = {}
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if exp <= d_int:
                continue
            dy = max(1, (exp % 100 - d_int % 100) + (exp // 100 % 100 - d_int // 100 % 100) * 30 + (exp // 10000 - d_int // 10000) * 365)
            dl = bs_delta(spot, k / 1000.0, dy / 365.0, iv, r == "C")
            if dl is not None:
                deltas[(exp, k, r)] = dl
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if exp <= d_int or vol < 250 or vol < 2 * max(oi, 1) or (exp, k, r) not in deltas:
                continue
            oi1 = nxt.get((exp, k, r), (0, None, 0))[1]
            if oi1 is None or (oi1 - oi) < 2 * max(oi, 1):
                continue
            # Frozen-selector pre-filters that need no minute data come first (vol, IV) — decisiveness last
            # since the minute join is the expensive part.
            if vol < SEL_VOL or iv > SEL_IV or iv <= 0:
                continue
            scanned_builds += 1
            bars = conn.execute(f"SELECT time_sec, close, volume FROM ohlcv WHERE root='{root}' AND expiry=? AND date=? AND strike_milli=? AND right=?",
                                (exp, d_int, k, r)).fetchall()
            if not bars:
                continue
            nbbo = dict((t, (b, aa)) for t, b, aa in conn.execute(
                f"SELECT time_sec, bid, ask FROM quotes WHERE root='{root}' AND expiry=? AND date=? AND strike_milli=? AND right=?", (exp, d_int, k, r)))
            net = signed = 0
            for t, close, v in bars:
                ba = nbbo.get(t)
                if not ba or ba[0] <= 0 or ba[1] <= 0:
                    continue
                mid, spr = (ba[0] + ba[1]) / 2.0, ba[1] - ba[0]
                if abs(close - mid) < 0.25 * spr:
                    continue
                net += (1 if close > mid else -1) * v
                signed += v
            if signed == 0 or net <= 0 or net / signed < 0.2 or net / signed > SEL_DECIS:
                continue
            bdl = deltas[(exp, k, r)]
            b0, bhs = mid_at(exp, k, r, d_int)
            if b0 is None or b0 <= 0:
                continue
            expiry_iso = f"{exp // 10000}-{exp // 100 % 100:02d}-{exp % 100:02d}"
            exp_close = hist.get(expiry_iso)
            if exp_close is None:
                continue
            settle = max(0.0, (exp_close - k / 1000.0) if r == "C" else (k / 1000.0 - exp_close))
            cands = sorted(((abs(abs(dl) - abs(bdl)), ck) for (ce, ck, cr), dl in deltas.items()
                            if ce == exp and cr == r and ck != k and snap[(ce, ck, cr)][0] < 2 * max(snap[(ce, ck, cr)][1], 1)), key=lambda x: x[0])
            ex = None
            for _, ck in cands[:5]:
                c0, _ = mid_at(exp, ck, r, d_int)
                if c0 and c0 > 0:
                    csettle = max(0.0, (exp_close - ck / 1000.0) if r == "C" else (ck / 1000.0 - exp_close))
                    ex = (settle / b0 - 1) - (csettle / c0 - 1)
                    break
            if ex is None:
                continue
            selected.append({"day": T, "excess": ex, "abs_ret": settle / b0 - 1, "half_frac": (bhs or 0) / b0})

    print(f"{root} {a.start}..{a.end}: {len(days)} sessions, {scanned_builds} vol/IV-eligible confirmed builds, SELECTED n={len(selected)}")
    if len(selected) < 30:
        print("NOT EVALUABLE (n < 30) — the frozen selector selects too little on this dataset (root-specific selector is the recorded reading if vol/IV eligibility was also thin)")
        return
    rng = random.Random(20260819)
    by_day = {}
    for b in selected:
        by_day.setdefault(b["day"], []).append(b["excess"])
    dm = np.array([np.mean(v) for v in by_day.values()])
    m = dm.mean()
    p1 = sum(1 for _ in range(1000) if abs(np.mean(dm * np.array([rng.choice((-1, 1)) for _ in dm]))) >= abs(m)) / 1000
    absr = float(np.mean([b["abs_ret"] for b in selected]))
    hs = float(np.mean([b["half_frac"] for b in selected]))
    ar = np.array(sorted(b["excess"] for b in selected))
    print(f"E1 pair excess: mean {np.mean([b['excess'] for b in selected]) * 100:+.2f}% | day-clustered {m * 100:+.2f}% over {len(dm)} days, p={p1:.3f} -> {'PASS' if m > 0 and p1 < 0.05 else 'KILL'}")
    print(f"E2 economics: mean ABSOLUTE return {absr * 100:+.2f}% vs 1.5x half-spread {1.5 * hs * 100:.2f}% -> {'PASS' if absr >= 1.5 * hs else 'KILL'}")
    print(f"descriptive: win {np.mean(np.array([b['abs_ret'] for b in selected]) > 0):.1%} | top-5 pairs = {np.sort(ar)[-5:].sum() / max(1e-12, ar.sum()) if ar.sum() > 0 else float('nan'):.0%} of excess")


if __name__ == "__main__":
    main()
