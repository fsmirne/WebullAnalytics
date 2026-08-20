"""Study 7 (registered 2026-08-19): tail-selectivity of the needle. Train 2025 / holdout 2026 hard split;
mechanical greedy selector (<=3 quartile-threshold terms, maximizing train mean S2 pair excess, n>=100);
ONE holdout evaluation of the frozen selector on: E1 contract edge (pair excess, day-clustered placebo),
E2 economics (absolute return >= 1.5x entry half-spread), E3 direction (SPY next-session move in the
selected builds' net direction; hit>52% and day-clustered p<0.05). See campaign doc for the full text.

Windows-native, read-only. Usage: python research\\signed_flow\\study7_tail.py
"""
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

OCC = re.compile(r"^SPY(\d{6})([CP])(\d{8})$")
RATE = 0.043
TRAIN_END = "2025-12-31"


def bs_delta(spot, strike, t_years, iv, is_call):
    if iv <= 0 or t_years <= 0 or spot <= 0 or strike <= 0:
        return None
    d1 = (math.log(spot / strike) + (RATE + 0.5 * iv * iv) * t_years) / (iv * math.sqrt(t_years))
    nd1 = 0.5 * (1.0 + math.erf(d1 / math.sqrt(2.0)))
    return nd1 if is_call else nd1 - 1.0


def load_snapshot(data_dir, day):
    p = data_dir / "oi" / "SPY" / f"{day}.jsonl"
    if not p.exists():
        return None, None
    rec = json.loads(p.read_text().strip().splitlines()[-1])
    out = {}
    for o in rec.get("options", []):
        m = OCC.match(o.get("symbol") or "")
        if m:
            out[(int("20" + m.group(1)), int(m.group(3)), m.group(2))] = (o.get("volume") or 0, o.get("openInterest") or 0, o.get("iv") or 0.0)
    return rec.get("underlyingPrice"), out


def main():
    data_dir = resolve_data_dir()
    days = sorted(p.stem for p in (data_dir / "oi" / "SPY").glob("????-??-??.jsonl") if p.stem >= "2025-01-01")
    hist = {}
    for line in (data_dir / "history" / "SPY.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        hist[f[0]] = float(f[4])
    vix = {}
    for line in (data_dir / "history" / "VIX.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        vix[f[0]] = float(f[4])
    conn = sqlite3.connect(f"file:{data_dir / 'quotes.db'}?mode=ro", uri=True)
    LO, HI = 15 * 3600 + 1800, 16 * 3600

    def mid_at(exp, k, r, date_int):
        row = conn.execute("SELECT bid, ask FROM quotes WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=? "
                           "AND time_sec BETWEEN ? AND ? AND bid > 0 AND ask > 0 ORDER BY time_sec DESC LIMIT 1",
                           (exp, date_int, k, r, LO, HI)).fetchone()
        return ((row[0] + row[1]) / 2.0 / 10000.0, (row[1] - row[0]) / 2.0 / 10000.0) if row else (None, None)

    builds = []
    for i in range(len(days) - 1):
        T, T1 = days[i], days[i + 1]
        spot, snap = load_snapshot(data_dir, T)
        _, nxt = load_snapshot(data_dir, T1)
        if not spot or not snap or not nxt or T1 not in hist or T not in hist or T not in vix:
            continue
        d_int = int(T.replace("-", ""))
        deltas = {}
        day_builds = []
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if exp <= d_int:
                continue
            dy = max(1, (exp % 100 - d_int % 100) + (exp // 100 % 100 - d_int // 100 % 100) * 30 + (exp // 10000 - d_int // 10000) * 365)
            dl = bs_delta(spot, k / 1000.0, dy / 365.0, iv, r == "C")
            if dl is not None:
                deltas[(exp, k, r)] = (dl, dy)
        # Chain net GEX sign (regime feature).
        net_gex = 0.0
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if oi <= 0 or exp < d_int or (exp, k, r) not in deltas:
                continue
            dl, dy = deltas[(exp, k, r)]
            d1 = (math.log(spot / (k / 1000.0)) + (RATE + 0.5 * iv * iv) * dy / 365.0) / (iv * math.sqrt(dy / 365.0)) if iv > 0 else None
            if d1 is not None:
                g = math.exp(-0.5 * d1 * d1) / math.sqrt(2 * math.pi) / (spot * iv * math.sqrt(dy / 365.0))
                net_gex += (1 if r == "C" else -1) * g * oi
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if exp <= d_int or vol < 250 or vol < 2 * max(oi, 1) or (exp, k, r) not in deltas:
                continue
            oi1 = nxt.get((exp, k, r), (0, None, 0))[1]
            if oi1 is None or (oi1 - oi) < 2 * max(oi, 1):
                continue
            bars = conn.execute("SELECT time_sec, close, volume, trades FROM ohlcv WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=?",
                                (exp, d_int, k, r)).fetchall()
            if not bars:
                continue
            nbbo = dict((t, (b, a)) for t, b, a in conn.execute(
                "SELECT time_sec, bid, ask FROM quotes WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=?",
                (exp, d_int, k, r)))
            net = signed = trades = 0
            half = {}
            for t, close, v, tr in bars:
                trades += tr or 0
                half[t // 1800] = half.get(t // 1800, 0) + v
                ba = nbbo.get(t)
                if not ba or ba[0] <= 0 or ba[1] <= 0:
                    continue
                mid, spread = (ba[0] + ba[1]) / 2.0, ba[1] - ba[0]
                if abs(close - mid) < 0.25 * spread:
                    continue
                net += (1 if close > mid else -1) * v
                signed += v
            if signed == 0 or net <= 0 or net / signed < 0.2:
                continue
            b0, bhs = mid_at(exp, k, r, d_int)
            if b0 is None or b0 <= 0:
                continue
            dl, dy = deltas[(exp, k, r)]
            expiry_iso = f"{exp // 10000}-{exp // 100 % 100:02d}-{exp % 100:02d}"
            exp_close = hist.get(expiry_iso)
            if exp_close is None:
                continue
            settle = max(0.0, (exp_close - k / 1000.0) if r == "C" else (k / 1000.0 - exp_close))
            # Twin for E1.
            cands = sorted(((abs(abs(v0[0]) - abs(dl)), ck) for (ce, ck, cr), v0 in deltas.items()
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
            day_builds.append({
                "day": T, "next": T1, "right": r, "abs_ret": settle / b0 - 1, "excess": ex,
                "half_frac": (bhs or 0) / b0, "delta": dl,
                "f_vol": vol, "f_voloi": vol / max(oi, 1), "f_prem": vol * b0 * 100,
                "f_money": abs(k / 1000.0 / spot - 1.0), "f_dte": dy, "f_call": 1.0 if r == "C" else 0.0,
                "f_decis": net / signed, "f_print": vol / max(trades, 1),
                "f_conc": max(half.values()) / max(vol, 1), "f_iv": iv,
                "f_spread": 2 * (bhs or 0) / b0, "f_regime": 1.0 if net_gex >= 0 else -1.0,
                "f_doimult": (oi1 - oi) / max(oi, 1), "f_vix": vix[T],
            })
        for b in day_builds:
            b["f_ladder"] = sum(1 for o in day_builds if o["right"] == b["right"] and abs(o["f_money"] - b["f_money"]) <= 0.02)
        builds.extend(day_builds)

    train = [b for b in builds if b["day"] <= TRAIN_END]
    hold = [b for b in builds if b["day"] > TRAIN_END]
    print(f"builds: train {len(train)} ({len(set(b['day'] for b in train))} days), holdout {len(hold)} ({len(set(b['day'] for b in hold))} days)")

    feats = [k for k in builds[0] if k.startswith("f_")]
    # Descriptive train deciles (top vs bottom quartile mean excess) — TRAIN ONLY.
    print("\ntrain feature screen (mean S2 excess in top vs bottom quartile):")
    for f in feats:
        vals = sorted(b[f] for b in train)
        q1, q3 = vals[len(vals) // 4], vals[3 * len(vals) // 4]
        lo = [b["excess"] for b in train if b[f] <= q1]
        hi = [b["excess"] for b in train if b[f] >= q3]
        if lo and hi:
            print(f"  {f:10s} loQ {np.mean(lo) * 100:+7.2f}%  hiQ {np.mean(hi) * 100:+7.2f}%")

    # Mechanical greedy selector: conjunction of <= 3 terms (feature, side, quartile threshold), maximizing
    # train mean excess with n >= 100. Candidates = train quartiles per feature, both directions.
    def apply(sel, items):
        out = items
        for f, side, thr in sel:
            out = [b for b in out if (b[f] >= thr if side == ">=" else b[f] <= thr)]
        return out

    selector = []
    pool = train
    for _ in range(3):
        best = None
        for f in feats:
            vals = sorted(b[f] for b in pool)
            for q in (len(vals) // 4, len(vals) // 2, 3 * len(vals) // 4):
                for side in (">=", "<="):
                    cand = selector + [(f, side, vals[q])]
                    sub = apply(cand, train)
                    if len(sub) >= 100:
                        m = float(np.mean([b["excess"] for b in sub]))
                        if best is None or m > best[0]:
                            best = (m, cand, len(sub))
        if best is None or (selector and best[0] <= float(np.mean([b["excess"] for b in apply(selector, train)]))):
            break
        selector = best[1]
        pool = apply(selector, train)
    tr_sel = apply(selector, train)
    print(f"\nFROZEN SELECTOR: {selector}")
    print(f"train: n={len(tr_sel)} mean excess {np.mean([b['excess'] for b in tr_sel]) * 100:+.2f}% (all-train {np.mean([b['excess'] for b in train]) * 100:+.2f}%)")

    # ONE holdout evaluation.
    hs = apply(selector, hold)
    print(f"\n=== HOLDOUT (evaluated once) === selected n={len(hs)} of {len(hold)}")
    if len(hs) < 30:
        print("GLOBAL KILL: holdout n < 30 — NOT EVALUABLE")
        return
    rng = random.Random(20260819)
    by_day = {}
    for b in hs:
        by_day.setdefault(b["day"], []).append(b)
    dm = np.array([np.mean([b["excess"] for b in v]) for v in by_day.values()])
    m = dm.mean()
    p1 = sum(1 for _ in range(1000) if abs(np.mean(dm * np.array([rng.choice((-1, 1)) for _ in dm]))) >= abs(m)) / 1000
    absr = float(np.mean([b["abs_ret"] for b in hs]))
    hsf = float(np.mean([b["half_frac"] for b in hs]))
    print(f"E1 pair excess: mean {np.mean([b['excess'] for b in hs]) * 100:+.2f}% | day-clustered mean {m * 100:+.2f}% p={p1:.3f} over {len(dm)} days -> {'PASS' if m > 0 and p1 < 0.05 else 'KILL'}")
    print(f"E2 economics: mean ABSOLUTE return {absr * 100:+.2f}% vs 1.5x half-spread {1.5 * hsf * 100:.2f}% -> {'PASS' if absr >= 1.5 * hsf else 'KILL'}")
    # E3 direction: premium-weighted net direction per firing day vs SPY next-session return.
    sdays = sorted(by_day)
    dirhits, srets = [], []
    for d in sdays:
        w = sum((1 if b["right"] == "C" else -1) * b["f_prem"] for b in by_day[d])
        nxt_day = by_day[d][0]["next"]
        if d in hist and nxt_day in hist and w != 0:
            r1 = hist[nxt_day] / hist[d] - 1
            srets.append(math.copysign(1, w) * r1)
            dirhits.append(1.0 if math.copysign(1, w) * r1 > 0 else 0.0)
    sr = np.array(srets)
    p3 = sum(1 for _ in range(1000) if abs(np.mean(sr * np.array([rng.choice((-1, 1)) for _ in sr]))) >= abs(sr.mean())) / 1000
    print(f"E3 direction: firing days {len(sr)}, hit {np.mean(dirhits):.1%}, mean signed next-session return {sr.mean() * 1e4:+.1f}bp, p={p3:.3f} -> {'PASS' if np.mean(dirhits) > 0.52 and sr.mean() > 0 and p3 < 0.05 else 'KILL'}")
    ar = np.array(sorted(b["abs_ret"] for b in hs))
    exr = np.array(sorted(b["excess"] for b in hs))
    print(f"descriptive (frozen selector, holdout): abs-ret win {np.mean(ar > 0):.1%} median {np.median(ar) * 100:+.1f}% | top-5 pairs = {np.sort(exr)[-5:].sum() / max(1e-12, exr.sum()):.0%} of excess | rights C {sum(1 for b in hs if b['right'] == 'C')}/P {sum(1 for b in hs if b['right'] == 'P')} | median DTE {int(np.median([b['f_dte'] for b in hs]))} | median |moneyness| {np.median([b['f_money'] for b in hs]):.1%}")


if __name__ == "__main__":
    main()
