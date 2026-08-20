"""Study 6 (registered 2026-08-19): does a confirmed, net-BOUGHT unusual build's contract outperform a
matched non-build twin? Paired design — same day, same expiry, same right, nearest-|delta| control — so
market drift, IV regime and theta hit both legs; the difference isolates build-specific information.

Horizons: P  = T 15:30+ mid -> T+1 15:30+ mid (science, primary)
          S1 = T+1 first two-sided mid <= 09:45 -> T+1 close mid (tradeable frame)
          S2 = T mid -> expiry settlement intrinsic (theta reality)
Kill: paired excess <= 0, or pair-shuffle placebo p >= 0.05, or excess < mean half-spread at entry.

Windows-native (quotes.db rule), read-only. Usage: python research\\signed_flow\\study6_needle.py
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


def mid_at(conn, expiry, k, r, date_int, lo_sec, hi_sec, last=True):
    """Mid of the last (or first) two-sided NBBO minute in [lo_sec, hi_sec], plus its half-spread."""
    order = "DESC" if last else "ASC"
    row = conn.execute(f"SELECT bid, ask FROM quotes WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=? "
                       f"AND time_sec BETWEEN ? AND ? AND bid > 0 AND ask > 0 ORDER BY time_sec {order} LIMIT 1",
                       (expiry, date_int, k, r, lo_sec, hi_sec)).fetchone()
    if not row:
        return None, None
    return (row[0] + row[1]) / 2.0 / 10000.0, (row[1] - row[0]) / 2.0 / 10000.0


def net_sign(conn, expiry, k, r, date_int):
    bars = conn.execute("SELECT time_sec, close, volume FROM ohlcv WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=?",
                        (expiry, date_int, k, r)).fetchall()
    if not bars:
        return 0, 0
    nbbo = dict((t, (b, a)) for t, b, a in conn.execute(
        "SELECT time_sec, bid, ask FROM quotes WHERE root='SPY' AND expiry=? AND date=? AND strike_milli=? AND right=?",
        (expiry, date_int, k, r)))
    net = signed = 0
    for t, close, vol in bars:
        ba = nbbo.get(t)
        if not ba or ba[0] <= 0 or ba[1] <= 0:
            continue
        mid, spread = (ba[0] + ba[1]) / 2.0, ba[1] - ba[0]
        if abs(close - mid) < 0.25 * spread:
            continue
        net += (1 if close > mid else -1) * vol
        signed += vol
    return net, signed


def main():
    data_dir = resolve_data_dir()
    days = sorted(p.stem for p in (data_dir / "oi" / "SPY").glob("????-??-??.jsonl") if p.stem >= "2025-01-01")
    hist = {}
    for line in (data_dir / "history" / "SPY.csv").read_text().splitlines()[1:]:
        f = line.split(",")
        hist[f[0]] = float(f[4])
    conn = sqlite3.connect(f"file:{data_dir / 'quotes.db'}?mode=ro", uri=True)

    LO, HI = 15 * 3600 + 1800, 16 * 3600            # 15:30..16:00 close marks
    OLO, OHI = 9 * 3600 + 1860, 9 * 3600 + 2700     # 09:31..09:45 open marks
    pairs = []  # dicts: excess returns per horizon + moderators
    for i in range(len(days) - 1):
        T, T1 = days[i], days[i + 1]
        spot, snap = load_snapshot(data_dir, T)
        _, nxt = load_snapshot(data_dir, T1)
        if not spot or not snap or not nxt or T1 not in hist:
            continue
        d_int, d1_int = int(T.replace("-", "")), int(T1.replace("-", ""))
        # Pre-compute per-(expiry,right) candidate control lists with deltas.
        deltas = {}
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if exp <= d_int:
                continue
            dy = max(1, (exp % 100 - d_int % 100) + (exp // 100 % 100 - d_int // 100 % 100) * 30 + (exp // 10000 - d_int // 10000) * 365)
            dl = bs_delta(spot, k / 1000.0, dy / 365.0, iv, r == "C")
            if dl is not None:
                deltas[(exp, k, r)] = dl
        for (exp, k, r), (vol, oi, iv) in snap.items():
            if exp <= d_int or vol < 250 or vol < 2 * max(oi, 1):
                continue
            oi1 = nxt.get((exp, k, r), (0, None, 0))[1]
            if oi1 is None or (oi1 - oi) < 2 * max(oi, 1):
                continue
            net, signed = net_sign(conn, exp, k, r, d_int)
            if signed == 0 or net <= 0 or net / signed < 0.2:
                continue  # registered: net-BOUGHT, decisively
            bdl = deltas.get((exp, k, r))
            if bdl is None:
                continue
            # Nearest-|delta| same-day/expiry/right NON-build control with marks at both ends.
            cands = sorted(((abs(abs(dl) - abs(bdl)), ck) for (ce, ck, cr), dl in deltas.items()
                            if ce == exp and cr == r and ck != k and snap[(ce, ck, cr)][0] < 2 * max(snap[(ce, ck, cr)][1], 1)),
                           key=lambda x: x[0])
            b0, bhs = mid_at(conn, exp, k, r, d_int, LO, HI)
            if b0 is None or b0 <= 0:
                continue
            settle_close = hist.get(T1)
            expiry_iso = f"{exp // 10000}-{exp // 100 % 100:02d}-{exp % 100:02d}"
            exp_close = hist.get(expiry_iso)

            def h_returns(exp_, k_, r_):
                e0, _ = mid_at(conn, exp_, k_, r_, d_int, LO, HI)
                if e0 is None or e0 <= 0:
                    return None
                e1, _ = mid_at(conn, exp_, k_, r_, d1_int, LO, HI)
                if e1 is None and exp_ == d1_int and settle_close is not None:
                    e1 = max(0.0, (settle_close - k_ / 1000.0) if r_ == "C" else (k_ / 1000.0 - settle_close))
                o1, _ = mid_at(conn, exp_, k_, r_, d1_int, OLO, OHI, last=False)
                s2 = max(0.0, (exp_close - k_ / 1000.0) if r_ == "C" else (k_ / 1000.0 - exp_close)) if exp_close is not None else None
                rp = (e1 / e0 - 1) if e1 is not None else None
                rs1 = (e1 / o1 - 1) if (e1 is not None and o1 and o1 > 0) else None
                rs2 = (s2 / e0 - 1) if s2 is not None else None
                return rp, rs1, rs2

            br = h_returns(exp, k, r)
            if br is None:
                continue
            cr_ = None
            for _, ck in cands[:5]:
                cr_ = h_returns(exp, ck, r)
                if cr_ is not None and cr_[0] is not None:
                    break
            if cr_ is None or br[0] is None or cr_[0] is None:
                continue
            pairs.append({"day": T, "right": r, "disp": abs(k / 1000.0 / spot - 1.0),
                          "bS2": br[2], "cS2": cr_[2],
                          "exP": br[0] - cr_[0],
                          "exS1": (br[1] - cr_[1]) if (br[1] is not None and cr_[1] is not None) else None,
                          "exS2": (br[2] - cr_[2]) if (br[2] is not None and cr_[2] is not None) else None,
                          "half_spread_frac": bhs / b0 if bhs else 0.0})

    print(f"pairs: {len(pairs)} over {len(set(p['day'] for p in pairs))} sessions")
    rng = random.Random(20260819)

    def test(key, label):
        ex = [p[key] for p in pairs if p[key] is not None]
        if len(ex) < 30:
            print(f"[{label}] n={len(ex)} — too few pairs")
            return
        ex = np.array(ex)
        m = ex.mean()
        hits = sum(1 for _ in range(1000) if abs(np.mean(ex * np.array([rng.choice((-1, 1)) for _ in ex]))) >= abs(m))
        hs = float(np.mean([p["half_spread_frac"] for p in pairs]))
        print(f"[{label}] n={len(ex)} paired excess {m * 100:+.2f}% per $ premium | median {np.median(ex) * 100:+.2f}% | win {np.mean(ex > 0):.1%} | placebo p={hits / 1000:.3f} | mean half-spread {hs * 100:.1f}%")
        print(f"    kill line (excess > 0, p < 0.05, excess >= half-spread): {'SURVIVES' if m > 0 and hits / 1000 < 0.05 and m >= hs else 'KILLED'}")

    test("exP", "PRIMARY T close -> T+1 close")
    test("exS1", "S1 tradeable T+1 open -> T+1 close")
    test("exS2", "S2 T close -> expiry settlement")

    # Robustness the pair-level placebo cannot provide: pairs cluster within sessions (~dozens/day sharing
    # one market path and vol surface), and a low win rate + big mean = tail-driven — so the defensible unit
    # is the DAY. Day-clustered sign-flip placebo, winsorized mean, and concentration diagnostics.
    def robust(key, label):
        by_day = {}
        allx = []
        for p in pairs:
            if p[key] is not None:
                by_day.setdefault(p["day"], []).append(p[key])
                allx.append(p[key])
        dm = np.array([np.mean(v) for v in by_day.values()])
        m = dm.mean()
        hits = sum(1 for _ in range(1000) if abs(np.mean(dm * np.array([rng.choice((-1, 1)) for _ in dm]))) >= abs(m))
        ax = np.array(sorted(allx))
        w = np.clip(ax, ax[int(0.01 * len(ax))], ax[int(0.99 * len(ax)) - 1]).mean()
        top10 = np.sort(ax)[-10:].sum() / max(1e-12, ax.sum())
        pos_days = float(np.mean(dm > 0))
        print(f"[{label} ROBUST] day-clustered n={len(dm)} days, mean-of-day-means {m * 100:+.2f}%, day-flip p={hits / 1000:.3f}, positive days {pos_days:.1%}, winsorized(1%) pair mean {w * 100:+.2f}%, top-10 pairs = {top10:.0%} of total excess")

    robust("exP", "PRIMARY")
    robust("exS2", "S2 expiry")
    bs2 = np.array([p["bS2"] for p in pairs if p["bS2"] is not None and p["cS2"] is not None])
    cs2 = np.array([p["cS2"] for p in pairs if p["bS2"] is not None and p["cS2"] is not None])
    print(f"[S2 absolute legs] build mean {bs2.mean() * 100:+.1f}% (win {np.mean(bs2 > 0):.1%}) vs control mean {cs2.mean() * 100:+.1f}% (win {np.mean(cs2 > 0):.1%}) per $ premium held to expiry")
    for lab, sel in [("calls", lambda p: p["right"] == "C"), ("puts", lambda p: p["right"] == "P")]:
        ex = np.array([p["exP"] for p in pairs if sel(p) and p["exP"] is not None])
        if len(ex) > 10:
            print(f"  [moderator {lab}] n={len(ex)} excess {ex.mean() * 100:+.2f}%")
    disp = sorted(p["disp"] for p in pairs)
    if len(disp) > 40:
        qs = [disp[len(disp) // 4], disp[len(disp) // 2], disp[3 * len(disp) // 4]]
        for qi, (lo, hi) in enumerate(zip([0] + qs, qs + [9])):
            ex = np.array([p["exP"] for p in pairs if lo <= p["disp"] < hi and p["exP"] is not None])
            if len(ex):
                print(f"  [moderator displacement Q{qi + 1} {lo:.1%}..{hi:.1%}] n={len(ex)} excess {ex.mean() * 100:+.2f}%")


if __name__ == "__main__":
    main()
