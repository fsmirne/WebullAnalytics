"""Study 5C-b (registered 2026-08-19, gate PASSED): signed flow of CONFIRMED unusual builds predicts
next-session SPY direction — alone and interacted with net-GEX regime. Frozen design; this script computes
exactly the registered spec and its placebos, and prints verdicts against the registered kill criteria.

Per session T (SPY, 2025-01+ where ohlcv coverage is complete):
  builds  = contracts with expiry > T, day-T volume >= max(250, 2 x OI_T), and dOI(T->T+1) >= 2 x max(OI_T,1)
  signing = per-minute quote rule (ohlcv close vs same-minute NBBO mid; unsigned band 0.25 x spread)
  S_T     = sum over builds of net-signed volume x BS delta x 100 x spot   [delta-notional; PRIMARY]
            (premium-weighted variant reported as secondary)
  regime  = sign of chain net GEX (call - put dollar gamma) from the day's snapshot
Targets: r1 = close(T+1)/close(T)-1 [primary]; o2c(T+1); range(T+1) = (high-low)/close(T) [secondary endpoint].
Controls: r_T, 5-day momentum, 5-day realized vol, VIX close, dVIX. Placebos: day-shuffle and per-build
sign-shuffle (1000 each) must BOTH be beaten at p < 0.05 with |effect| >= 4bp/session per 1 sd of S.

Windows-native (quotes.db rule). Read-only.
Usage: python research\\signed_flow\\study_5cb.py
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


def norm_cdf(x):
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


def bs_delta_gamma(spot, strike, t_years, iv, is_call):
    if iv <= 0 or t_years <= 0 or spot <= 0 or strike <= 0:
        return None, None
    d1 = (math.log(spot / strike) + (RATE + 0.5 * iv * iv) * t_years) / (iv * math.sqrt(t_years))
    delta = norm_cdf(d1) if is_call else norm_cdf(d1) - 1.0
    gamma = math.exp(-0.5 * d1 * d1) / math.sqrt(2 * math.pi) / (spot * iv * math.sqrt(t_years))
    return delta, gamma


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


def sign_contract(conn, expiry, strike_milli, right, date_int):
    """Net-signed and total signed volume for one contract's session via the registered quote rule."""
    bars = conn.execute("SELECT time_sec, close, volume FROM ohlcv WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=?",
                        (expiry, date_int, strike_milli, right)).fetchall()
    if not bars:
        return 0, 0
    nbbo = dict((t, (b, a)) for t, b, a in conn.execute(
        "SELECT time_sec, bid, ask FROM quotes WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=?",
        (expiry, date_int, strike_milli, right)))
    net = signed = 0
    for t, close, vol in bars:
        ba = nbbo.get(t)
        if not ba or ba[0] <= 0 or ba[1] <= 0:
            continue
        mid, spread = (ba[0] + ba[1]) / 2.0, ba[1] - ba[0]
        if abs(close - mid) < 0.25 * spread:
            continue
        s = 1 if close > mid else -1
        net += s * vol
        signed += vol
    return net, signed


def main():
    data_dir = resolve_data_dir()
    days = sorted(p.stem for p in (data_dir / "oi" / "SPY").glob("????-??-??.jsonl") if p.stem >= "2025-01-01")

    hist = {}
    for line in (data_dir / "history" / "SPY.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        hist[f[0]] = (float(f[1]), float(f[2]), float(f[3]), float(f[4]))  # o,h,l,c
    vix = {}
    for line in (data_dir / "history" / "VIX.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        vix[f[0]] = float(f[4])

    conn = sqlite3.connect(f"file:{data_dir / 'quotes.db'}?mode=ro", uri=True)
    rows = []           # per-session dict
    build_contribs = {}  # day -> list of signed delta-notional contributions (for the sign-shuffle placebo)
    for i in range(len(days) - 1):
        T, T1 = days[i], days[i + 1]
        if T not in hist or T1 not in hist or T not in vix:
            continue
        spot, snap = load_snapshot(data_dir, T)
        _, nxt = load_snapshot(data_dir, T1)
        if not spot or not snap or not nxt:
            continue
        date_int = int(T.replace("-", ""))
        exp_floor = date_int
        s_dn = s_prem = 0.0
        contribs = []
        n_builds = 0
        for key, (vol, oi, iv) in snap.items():
            expiry, strike_m, right = key
            if expiry <= exp_floor or vol < 250 or vol < 2 * max(oi, 1):
                continue
            oi1 = nxt.get(key, (0, None, 0))[1]
            if oi1 is None or (oi1 - oi) < 2 * max(oi, 1):
                continue
            net, signed = sign_contract(conn, expiry, strike_m, right, date_int)
            if signed == 0:
                continue
            n_builds += 1
            t_years = max(1, ((expiry // 10000 - int(T[:4])) * 365 + (expiry // 100 % 100 - int(T[5:7])) * 30 + (expiry % 100 - int(T[8:10])))) / 365.0
            delta, _ = bs_delta_gamma(spot, strike_m / 1000.0, t_years, iv, right == "C")
            if delta is not None:
                dn = net * delta * 100 * spot
                s_dn += dn
                contribs.append(dn)
            s_prem += net  # premium variant uses net contracts (spot-scale-free); reported secondary

        # Chain net-GEX regime sign from the same snapshot.
        net_gex = 0.0
        for (expiry, strike_m, right), (vol, oi, iv) in snap.items():
            if oi <= 0 or expiry < exp_floor:
                continue
            t_years = max(1, (expiry % 100 - int(T[8:10])) + (expiry // 100 % 100 - int(T[5:7])) * 30 + (expiry // 10000 - int(T[:4])) * 365) / 365.0
            _, gamma = bs_delta_gamma(spot, strike_m / 1000.0, t_years, iv, right == "C")
            if gamma is not None:
                net_gex += (1 if right == "C" else -1) * gamma * oi * 100 * spot

        o1, h1, l1, c1 = hist[T1]
        c0 = hist[T][3]
        r_t = c0 / hist[days[i - 1]][3] - 1 if i >= 1 and days[i - 1] in hist else 0.0
        mom5 = c0 / hist[days[i - 5]][3] - 1 if i >= 5 and days[i - 5] in hist else 0.0
        rets5 = [hist[days[j]][3] / hist[days[j - 1]][3] - 1 for j in range(max(1, i - 4), i + 1) if days[j] in hist and days[j - 1] in hist]
        rv5 = float(np.std(rets5)) if len(rets5) >= 3 else 0.0
        dvix = vix[T] - vix.get(days[i - 1], vix[T]) if i >= 1 else 0.0
        rows.append({"day": T, "S": s_dn, "n_builds": n_builds, "regime": 1.0 if net_gex >= 0 else -1.0,
                     "r1": c1 / c0 - 1, "o2c": c1 / o1 - 1, "range1": (h1 - l1) / c0,
                     "r_t": r_t, "mom5": mom5, "rv5": rv5, "vix": vix[T], "dvix": dvix})
        build_contribs[T] = contribs

    n = len(rows)
    print(f"sessions with data: {n}  ({rows[0]['day']}..{rows[-1]['day']})  mean builds/day {np.mean([r['n_builds'] for r in rows]):.1f}")
    S = np.array([r["S"] for r in rows])
    sd = S.std() or 1.0
    Sn = S / sd
    C = np.column_stack([np.ones(n)] + [[r[k] for r in rows] for k in ("r_t", "mom5", "rv5", "vix", "dvix")])

    def fit(y, X):
        beta, res, *_ = np.linalg.lstsq(X, y, rcond=None)
        pred = X @ beta
        ss = ((y - y.mean()) ** 2).sum()
        return beta, 1 - ((y - pred) ** 2).sum() / ss if ss > 0 else 0.0

    def evaluate(target, label):
        y = np.array([r[target] for r in rows])
        _, r2c = fit(y, C)
        Xs = np.column_stack([C, Sn])
        beta, r2s = fit(y, Xs)
        b = beta[-1]
        # Placebo 1: day-shuffle S.
        rng = random.Random(20260819)
        hits = 0
        for _ in range(1000):
            perm = Sn.copy()
            rng.shuffle(perm)
            bb, _ = fit(y, np.column_stack([C, perm]))
            if abs(bb[-1]) >= abs(b):
                hits += 1
        p_day = hits / 1000
        # Placebo 2: per-build sign-shuffle -> rebuild S.
        hits2 = 0
        for _ in range(1000):
            Sp = np.array([sum(c * rng.choice((-1, 1)) for c in build_contribs[r["day"]]) for r in rows]) / sd
            bb, _ = fit(y, np.column_stack([C, Sp]))
            if abs(bb[-1]) >= abs(b):
                hits2 += 1
        p_sign = hits2 / 1000
        hit = float(np.mean(np.sign(S[S != 0]) == np.sign(y[S != 0]))) if (S != 0).any() else float("nan")
        print(f"\n[{label}] b={b * 1e4:+.2f} bp per 1sd S | incremental R2 {r2s - r2c:+.4f} | day-shuffle p={p_day:.3f} | sign-shuffle p={p_sign:.3f} | sign hit-rate {hit:.1%}")
        survives = abs(b) * 1e4 >= 4.0 and p_day < 0.05 and p_sign < 0.05
        print(f"    registered kill line (>=4bp, both placebos p<0.05): {'SURVIVES' if survives else 'KILLED'}")
        return b, survives

    evaluate("r1", "PRIMARY direction: next-session close-to-close")
    evaluate("o2c", "direction: next-session open-to-close")

    # Regime interaction (registered): S x regime term added to controls + S — held to the SAME kill line
    # and placebos as the main term (a registered interaction is not a license for a free multiple test).
    y = np.array([r["r1"] for r in rows])
    regime = np.array([r["regime"] for r in rows])
    inter = Sn * regime
    beta, r2i = fit(y, np.column_stack([C, Sn, inter]))
    b_int = beta[-1]
    _, r2_no_int = fit(y, np.column_stack([C, Sn]))
    rng = random.Random(20260819)
    hits = hits2 = 0
    for _ in range(1000):
        perm = Sn.copy()
        rng.shuffle(perm)
        bb, _ = fit(y, np.column_stack([C, perm, perm * regime]))
        if abs(bb[-1]) >= abs(b_int):
            hits += 1
        Sp = np.array([sum(c * rng.choice((-1, 1)) for c in build_contribs[r["day"]]) for r in rows]) / sd
        bb, _ = fit(y, np.column_stack([C, Sp, Sp * regime]))
        if abs(bb[-1]) >= abs(b_int):
            hits2 += 1
    print(f"\n[regime interaction on r1] b_S={beta[-2] * 1e4:+.2f}bp b_SxGEX={b_int * 1e4:+.2f}bp | incremental R2 {r2i - r2_no_int:+.4f} | day-shuffle p={hits / 1000:.3f} | sign-shuffle p={hits2 / 1000:.3f} | neg-gamma days {sum(1 for r in rows if r['regime'] < 0)}/{n}")
    print(f"    registered kill line: {'SURVIVES' if abs(b_int) * 1e4 >= 4.0 and hits / 1000 < 0.05 and hits2 / 1000 < 0.05 else 'KILLED'}")

    evaluate("range1", "SECONDARY range: next-session (high-low)/close")


if __name__ == "__main__":
    main()
