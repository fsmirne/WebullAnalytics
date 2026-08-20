"""Study 8 (registered 2026-08-19): does a prior-day confirmed PUT build below spot improve a mechanically
sold SPXW 0DTE call vertical? Identical structure every session (short first $5 strike >= open x 1.003,
long +20, entered at the first two-sided minute >= 09:31, settled at the close); signal = prior-session
put build into today's expiry (vol >= max(250, 2xOI), dOI >= 2x, strike <= open x 0.995). Endpoints: E1
signal-vs-nonsignal executable-P&L lift (label permutation), E2 the same beyond overnight-gap/prior-return/
VIX controls, E3 signal-day executable P&L > 0. See the campaign doc for kill lines.

Windows-native, read-only. Usage: python research\\signed_flow\\study8_vertical.py
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

OCC = re.compile(r"^SPXW(\d{6})([CP])(\d{8})$")


def snapshot(data_dir, day):
    p = data_dir / "oi" / "SPXW" / f"{day}.jsonl"
    if not p.exists():
        return None
    rec = json.loads(p.read_text().strip().splitlines()[-1])
    out = {}
    for o in rec.get("options", []):
        m = OCC.match(o.get("symbol") or "")
        if m:
            out[(int("20" + m.group(1)), int(m.group(3)), m.group(2))] = (o.get("volume") or 0, o.get("openInterest") or 0)
    return out


def main():
    data_dir = resolve_data_dir()
    days = sorted(p.stem for p in (data_dir / "oi" / "SPXW").glob("????-??-??.jsonl"))
    spxw = {}
    for line in (data_dir / "history" / "SPXW.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        spxw[f[0]] = (float(f[1]), float(f[4]))  # open, close
    vix = {}
    for line in (data_dir / "history" / "VIX.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        vix[f[0]] = float(f[4])
    conn = sqlite3.connect(f"file:{data_dir / 'quotes.db'}?mode=ro", uri=True)

    def leg(exp_int, k_milli, date_int):
        return conn.execute("SELECT time_sec, bid, ask FROM quotes WHERE root='SPXW' AND expiry=? AND date=? AND strike_milli=? "
                            "AND right='C' AND time_sec >= 34260 AND bid > 0 AND ask > 0 ORDER BY time_sec ASC LIMIT 1",
                            (exp_int, date_int, k_milli)).fetchone()

    sessions = []
    for i in range(1, len(days)):
        Tm1, T = days[i - 1], days[i]
        if T not in spxw or Tm1 not in spxw or Tm1 not in vix:
            continue
        prev = snapshot(data_dir, Tm1)
        cur = snapshot(data_dir, T)
        if prev is None or cur is None:
            continue
        open_t, close_t = spxw[T]
        exp_int = int(T.replace("-", ""))
        # Signal: prior-day confirmed put build into today's expiry, meaningfully below the open.
        sig_size = 0
        for (exp, k, r), (vol, oi) in prev.items():
            if exp != exp_int or r != "P" or k / 1000.0 > open_t * 0.995:
                continue
            if vol < 250 or vol < 2 * max(oi, 1):
                continue
            oi1 = cur.get((exp, k, r), (0, None))[1]
            if oi1 is None or (oi1 - oi) < 2 * max(oi, 1):
                continue
            sig_size += oi1 - oi
        # Structure: identical every session.
        k1 = int(math.ceil(open_t * 1.003 / 5.0) * 5) * 1000
        k2 = k1 + 20000
        l1, l2 = leg(exp_int, k1, exp_int), leg(exp_int, k2, exp_int)
        if not l1 or not l2 or abs(l1[0] - l2[0]) > 120:
            continue
        mid_credit = (l1[1] + l1[2]) / 2e4 - (l2[1] + l2[2]) / 2e4
        exec_credit = l1[1] / 1e4 - l2[2] / 1e4   # sell short at bid, buy long at ask
        if exec_credit <= 0:
            continue
        payout = max(0.0, close_t - k1 / 1000.0) - max(0.0, close_t - k2 / 1000.0)
        gap = open_t / spxw[Tm1][1] - 1.0
        prev_ret = spxw[Tm1][1] / spxw[days[i - 2]][1] - 1.0 if i >= 2 and days[i - 2] in spxw else 0.0
        sessions.append({"day": T, "sig": 1.0 if sig_size > 0 else 0.0, "sig_size": sig_size,
                         "pnl_mid": mid_credit - payout, "pnl": exec_credit - payout,
                         "win": 1.0 if payout == 0 else 0.0, "gap": gap, "prev_ret": prev_ret, "vix": vix[Tm1]})

    n = len(sessions)
    sig = [s for s in sessions if s["sig"]]
    non = [s for s in sessions if not s["sig"]]
    print(f"sessions {n} ({sessions[0]['day']}..{sessions[-1]['day']}), signal days {len(sig)}, non-signal {len(non)}")
    if len(sig) < 40:
        print("NOT EVALUABLE: signal fires on < 40 sessions")
        return
    ms, mn = np.mean([s["pnl"] for s in sig]), np.mean([s["pnl"] for s in non])
    diff = ms - mn
    rng = random.Random(20260819)
    labels = [s["sig"] for s in sessions]
    pnls = np.array([s["pnl"] for s in sessions])
    hits = 0
    for _ in range(1000):
        lab = labels[:]
        rng.shuffle(lab)
        lab = np.array(lab)
        d = pnls[lab == 1].mean() - pnls[lab == 0].mean()
        if abs(d) >= abs(diff):
            hits += 1
    print(f"win rates: signal {np.mean([s['win'] for s in sig]):.1%} vs non-signal {np.mean([s['win'] for s in non]):.1%} | mid-P&L means: {np.mean([s['pnl_mid'] for s in sig]):+.2f} vs {np.mean([s['pnl_mid'] for s in non]):+.2f}")
    print(f"E1 lift (executable): signal {ms:+.2f} vs non-signal {mn:+.2f} -> diff {diff:+.2f} pts/spread, permutation p={hits / 1000:.3f} -> {'PASS' if diff > 0 and hits / 1000 < 0.05 else 'KILL'}")
    X = np.column_stack([np.ones(n), labels, [s["gap"] for s in sessions], [s["prev_ret"] for s in sessions], [s["vix"] for s in sessions]])
    beta, *_ = np.linalg.lstsq(X, pnls, rcond=None)
    b_sig = beta[1]
    hits2 = 0
    for _ in range(1000):
        lab = labels[:]
        rng.shuffle(lab)
        Xp = X.copy()
        Xp[:, 1] = lab
        bb, *_ = np.linalg.lstsq(Xp, pnls, rcond=None)
        if abs(bb[1]) >= abs(b_sig):
            hits2 += 1
    print(f"E2 gap-controlled signal coef: {b_sig:+.2f} pts, permutation p={hits2 / 1000:.3f} -> {'PASS' if b_sig > 0 and hits2 / 1000 < 0.05 else 'KILL'}")
    print(f"E3 economics: signal-day mean executable P&L {ms:+.2f} pts -> {'PASS' if ms > 0 else 'KILL'}")
    big = sorted(sig, key=lambda s: -s["sig_size"])[:len(sig) // 4]
    print(f"descriptive: top-quartile signal size (n={len(big)}) mean executable P&L {np.mean([s['pnl'] for s in big]):+.2f}")


if __name__ == "__main__":
    main()
