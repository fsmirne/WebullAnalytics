#!/usr/bin/env python3
"""SPXW 0DTE gravity-distance -> intraday volatility study (pre-registered: scripts/gex_gravity_range_study.md).

Assembles one row per session: 9:35 gravity distance + controls + rest-of-day range, then runs the
pre-registered stats. MUST run with Windows python.exe (quotes.db WAL; WSL sqlite corrupts).

  python.exe scripts/gex_gravity_range_study.py --data-dir <data> assemble   -> writes gravity_range_daily.csv next to this script
  python.exe scripts/gex_gravity_range_study.py --data-dir <data> validate   -> replicates gravity vs the data/gex/SPXW live-log days
  python.exe scripts/gex_gravity_range_study.py stats                        -> reads the csv, prints the pre-registered analyses
"""
import argparse, csv, json, math, os, sqlite3, sys
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

ET = ZoneInfo("America/New_York")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_CSV = os.path.join(HERE, "gravity_range_daily.csv")


def load_tape(path):
    """intraday csv -> list of (et_datetime_bar_start, o, h, l, c) sorted."""
    bars = []
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            ts = datetime.fromisoformat(row["timestamp_utc"].replace("Z", "+00:00")).astimezone(ET)
            bars.append((ts, float(row["open"]), float(row["high"]), float(row["low"]), float(row["close"])))
    bars.sort(key=lambda b: b[0])
    return bars


def rth(bars):
    return [b for b in bars if (b[0].hour, b[0].minute) >= (9, 30) and b[0].hour < 16]


def norm_cdf(x):
    return 0.5 * math.erfc(-x / math.sqrt(2.0))


def bs_price(S, K, T, r, sigma, is_call):
    if sigma <= 0 or T <= 0:
        intrinsic = max(S - K, 0.0) if is_call else max(K - S, 0.0)
        return intrinsic
    srt = sigma * math.sqrt(T)
    d1 = (math.log(S / K) + (r + 0.5 * sigma * sigma) * T) / srt
    d2 = d1 - srt
    if is_call:
        return S * norm_cdf(d1) - K * math.exp(-r * T) * norm_cdf(d2)
    return K * math.exp(-r * T) * norm_cdf(-d2) - S * norm_cdf(-d1)


def solve_iv(S, K, T, r, mid, is_call):
    intrinsic = max(S - K, 0.0) if is_call else max(K - S, 0.0)
    if mid <= intrinsic + 1e-9:
        return None
    lo, hi = 1e-4, 5.0
    if bs_price(S, K, T, r, hi, is_call) < mid:
        return None
    for _ in range(80):
        m = 0.5 * (lo + hi)
        if bs_price(S, K, T, r, m, is_call) < mid:
            lo = m
        else:
            hi = m
    return 0.5 * (lo + hi)


def bs_gamma(S, K, T, r, sigma):
    srt = sigma * math.sqrt(T)
    d1 = (math.log(S / K) + (r + 0.5 * sigma * sigma) * T) / srt
    return math.exp(-0.5 * d1 * d1) / math.sqrt(2 * math.pi) / (S * srt)


def load_rates(data_dir):
    rates = {}
    p = os.path.join(data_dir, "rates", "IRX.csv")
    with open(p, newline="") as f:
        for row in csv.DictReader(f):
            rates[row["date"]] = float(row["rate"])
    return rates


def rate_for(rates, d):
    for k in sorted(rates.keys(), reverse=True):
        if k <= d:
            return rates[k]
    return 0.04


def parse_oi_0dte(path, day_compact):
    """day-D OI snapshot -> {(strike, 'C'/'P'): oi} for expiry==D only. EOD iv field deliberately unused (lookahead)."""
    with open(path) as f:
        last = None
        for line in f:
            line = line.strip()
            if line:
                last = line
    snap = json.loads(last)
    out = {}
    for o in snap.get("options", []):
        sym = o.get("symbol", "")
        if not sym.startswith("SPXW") or len(sym) < 19:
            continue
        body = sym[4:]
        exp, right, strike_s = body[:6], body[6], body[7:]
        if exp != day_compact or right not in "CP":
            continue
        oi = int(o.get("openInterest") or 0)
        if oi > 0:
            out[(int(strike_s) / 1000.0, right)] = oi
    return out


def quotes_at(conn, day_int, time_secs):
    """(strike, right) -> mid at the first time_sec in time_secs that has rows. Two-sided books only."""
    for tsec in time_secs:
        rows = conn.execute("SELECT strike_milli, right, bid, ask FROM quotes WHERE root='SPXW' AND expiry=? AND date=? AND time_sec=?", (day_int, day_int, tsec)).fetchall()
        if rows:
            return {(sm / 1000.0, r): (b + a) / 2.0 / 10000.0 for sm, r, b, a in rows if b > 0 and a > 0}, tsec
    return {}, None


def gravity_at(oi_map, mids, spot, T, r, max_strikes=None):
    """Returns (gravity_strike, gross_by_strike, net_gex_dollars, dropped_near_oi_share, net_share).

    Gross dollar gamma per strike = sum over rights of OI*gamma*100*spot (constant scale; argmax-invariant).
    Net GEX ($ per 1% move) = sum of sign(call +, put -)*OI*gamma*100*spot^2*0.01 (prior net-GEX study convention).
    net_share = (callGex - putGex)/(callGex + putGex), the scale-free form of the n=1072 benchmark study.
    max_strikes: C#-parity approximation of the AnalyzeGexCommand --max-strikes display cap (argmax runs over the capped set, AnalyzeGexCommand.cs:1832-1858); we cap to the N strikes nearest spot.
    """
    gross, net = {}, 0.0
    call_w = put_w = 0.0
    near_oi_total = near_oi_dropped = 0
    for (K, right), oi in oi_map.items():
        near = abs(K - spot) / spot < 0.02
        if near:
            near_oi_total += oi
        mid = mids.get((K, right))
        if mid is None:
            if near:
                near_oi_dropped += oi
            continue
        iv = solve_iv(spot, K, T, r, mid, right == "C")
        if iv is None:
            if near:
                near_oi_dropped += oi
            continue
        g = bs_gamma(spot, K, T, r, iv)
        w = oi * g * 100.0 * spot
        gross[K] = gross.get(K, 0.0) + w
        net += (w if right == "C" else -w) * spot * 0.01
        if right == "C":
            call_w += w
        else:
            put_w += w
    if not gross:
        return None, gross, net, 1.0, 0.0
    pool = gross
    if max_strikes is not None and len(gross) > max_strikes:
        kept = sorted(gross.keys(), key=lambda K: abs(K - spot))[:max_strikes]
        pool = {K: gross[K] for K in kept}
    grav = max(pool.items(), key=lambda kv: kv[1])[0]
    dropped = near_oi_dropped / near_oi_total if near_oi_total else 0.0
    net_share = (call_w - put_w) / (call_w + put_w) if (call_w + put_w) > 0 else 0.0
    return grav, gross, net, dropped, net_share


def assemble(data_dir):
    oi_dir = os.path.join(data_dir, "oi", "SPXW")
    tape_dir = os.path.join(data_dir, "intraday", "SPXW")
    vix_dir = os.path.join(data_dir, "intraday", "VIX")
    rates = load_rates(data_dir)
    conn = sqlite3.connect(os.path.join(data_dir, "quotes.db"))
    oi_days = sorted(f[:-6] for f in os.listdir(oi_dir) if f.endswith(".jsonl"))
    tape_days = sorted(f[:-4] for f in os.listdir(tape_dir) if f.endswith(".csv"))
    excl = {}
    rows_out = []
    for d in oi_days:
        day_int = int(d.replace("-", ""))
        day_compact = d[2:4] + d[5:7] + d[8:10]
        tp = os.path.join(tape_dir, d + ".csv")
        if not os.path.exists(tp):
            excl[d] = "no tape"
            continue
        bars = rth(load_tape(tp))
        if not bars:
            excl[d] = "empty rth tape"
            continue
        prev = [t for t in tape_days if t < d]
        if not prev:
            excl[d] = "no prior session tape"
            continue
        prev_rth = rth(load_tape(os.path.join(tape_dir, prev[-1] + ".csv")))
        if not prev_rth:
            excl[d] = "empty prior rth"
            continue
        prior_close = prev_rth[-1][4]
        open930 = bars[0][1]
        pre935 = [b for b in bars if (b[0].hour, b[0].minute) <= (9, 34)]
        rest = [b for b in bars if (b[0].hour, b[0].minute) >= (9, 35)]
        if not pre935 or len(rest) < 60:
            excl[d] = "tape too thin around 9:35"
            continue
        spot935 = pre935[-1][4]
        rest_range = (max(b[2] for b in rest) - min(b[3] for b in rest)) / spot935 * 100.0
        closes = [spot935] + [b[4] for b in rest]
        rv = math.sqrt(sum(math.log(closes[i + 1] / closes[i]) ** 2 for i in range(len(closes) - 1))) * 100.0
        early_close = (bars[-1][0].hour, bars[-1][0].minute) < (15, 30)
        vix935 = ""
        vp = os.path.join(vix_dir, d + ".csv")
        if os.path.exists(vp):
            vb = [b for b in load_tape(vp) if (b[0].hour, b[0].minute) <= (9, 34) and (b[0].hour, b[0].minute) >= (9, 30)]
            if vb:
                vix935 = f"{vb[-1][4]:.2f}"
        oi_map = parse_oi_0dte(os.path.join(oi_dir, d + ".jsonl"), day_compact)
        if not oi_map:
            excl[d] = "no 0dte oi"
            continue
        mids, tsec = quotes_at(conn, day_int, (34500, 34560, 34440))
        if not mids:
            excl[d] = "no 9:35 quotes"
            continue
        close_et = 13 if early_close else 16
        T = max((close_et * 3600 - tsec) / 3600.0, 0.25) / 24.0 / 365.0
        r = rate_for(rates, d)
        grav, gross, net, dropped, net_share = gravity_at(oi_map, mids, spot935, T, r)
        if grav is None:
            excl[d] = "no computable gamma"
            continue
        grav_p, _, _, _, _ = gravity_at(oi_map, mids, spot935, 1.0 / 365.0, r, max_strikes=50)
        rows_out.append({"date": d, "spot935": f"{spot935:.2f}", "open930": f"{open930:.2f}", "prior_close": f"{prior_close:.2f}", "gravity": f"{grav:.0f}", "d_pts": f"{abs(grav - spot935):.2f}",
                         "gravity_parity": f"{grav_p:.0f}", "d_pts_parity": f"{abs(grav_p - spot935):.2f}", "net_share": f"{net_share:.4f}",
                         "net_gex_m": f"{net / 1e6:.1f}", "gap_pct": f"{abs(open930 - prior_close) / prior_close * 100:.4f}", "drive_pct": f"{abs(spot935 - open930) / open930 * 100:.4f}",
                         "rest_range_pct": f"{rest_range:.4f}", "rv_pct": f"{rv:.4f}", "vix935": vix935, "early_close": int(early_close), "dropped_near_oi": f"{dropped:.3f}", "n_struck": len(gross)})
        if len(rows_out) % 50 == 0:
            print(f"  {len(rows_out)} days assembled (through {d})", flush=True)
    with open(OUT_CSV, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows_out[0].keys()))
        w.writeheader()
        w.writerows(rows_out)
    print(f"wrote {len(rows_out)} rows -> {OUT_CSV}")
    print(f"exclusions ({len(excl)}):")
    for d, why in excl.items():
        print(f"  {d}: {why}")


def validate(data_dir):
    """Replicate the 0DTE gravity against the data/gex/SPXW live-log snapshots (same-minute quotes + same-day OI)."""
    gex_dir = os.path.join(data_dir, "gex", "SPXW")
    rates = load_rates(data_dir)
    conn = sqlite3.connect(os.path.join(data_dir, "quotes.db"))
    for fn in sorted(os.listdir(gex_dir)):
        if not fn.endswith(".jsonl"):
            continue
        d = fn[:-6]
        day_int = int(d.replace("-", ""))
        day_compact = d[2:4] + d[5:7] + d[8:10]
        with open(os.path.join(gex_dir, fn)) as f:
            for line in f:
                snap = json.loads(line)
                zero = next((e for e in snap.get("expiries", []) if e.get("expiry") == d), None)
                if zero is None:
                    continue
                ts = datetime.fromisoformat(snap["tsEt"])
                tsec = ts.hour * 3600 + ts.minute * 60
                oi_map = parse_oi_0dte(os.path.join(data_dir, "oi", "SPXW", d + ".jsonl"), day_compact)
                mids, used = quotes_at(conn, day_int, (tsec, tsec + 60, tsec - 60))
                if not mids or not oi_map:
                    print(f"{d} {snap['tsEt']}: no data (oi={len(oi_map)}, mids={len(mids)})")
                    continue
                spot = snap["spot"]
                T = max((16 * 3600 - used) / 3600.0, 0.25) / 24.0 / 365.0
                grav, gross, net, dropped, _ = gravity_at(oi_map, mids, spot, T, rate_for(rates, d))
                print(f"{d} {snap['tsEt']} spot={spot:.2f}: logged gravity={zero['gravity']} replicated={grav} (dropped_near={dropped:.2f}, strikes={len(gross)})")


# ---------- stats (numpy only; no sqlite -> safe anywhere) ----------

def ols_nw(y, X, lags=5):
    import numpy as np
    n, k = X.shape
    beta, *_ = np.linalg.lstsq(X, y, rcond=None)
    u = y - X @ beta
    XtX_inv = np.linalg.inv(X.T @ X)
    S = (X * u[:, None]).T @ (X * u[:, None])
    for l in range(1, lags + 1):
        w = 1.0 - l / (lags + 1.0)
        G = (X[l:] * u[l:, None]).T @ (X[:-l] * u[:-l, None])
        S += w * (G + G.T)
    cov = XtX_inv @ S @ XtX_inv
    se = np.sqrt(np.diag(cov))
    r2 = 1.0 - (u @ u) / ((y - y.mean()) @ (y - y.mean()))
    return beta, se, r2, u


def stats():
    import numpy as np
    rows = list(csv.DictReader(open(OUT_CSV, newline="")))
    rows = [r for r in rows if r["vix935"]]
    print(f"n with all fields = {len(rows)} (of {sum(1 for _ in csv.DictReader(open(OUT_CSV, newline='')))} assembled)")
    d = np.array([float(r["d_pts"]) for r in rows])
    rng = np.array([float(r["rest_range_pct"]) for r in rows])
    gap = np.array([float(r["gap_pct"]) for r in rows])
    drive = np.array([float(r["drive_pct"]) for r in rows])
    vix = np.array([float(r["vix935"]) for r in rows])
    net = np.array([float(r["net_gex_m"]) for r in rows])
    ec = np.array([int(r["early_close"]) for r in rows])
    dropped = np.array([float(r["dropped_near_oi"]) for r in rows])
    dates = [r["date"] for r in rows]
    prior_rng = np.concatenate([[np.median(rng)], rng[:-1]])
    eps = 1e-4

    def spearman(a, b):
        ra, rb = np.argsort(np.argsort(a)).astype(float), np.argsort(np.argsort(b)).astype(float)
        return np.corrcoef(ra, rb)[0, 1]

    print("\n== 1. Descriptive ==")
    print(f"D pts: median {np.median(d):.1f}  p25 {np.percentile(d, 25):.1f}  p75 {np.percentile(d, 75):.1f}  p95 {np.percentile(d, 95):.1f}  max {d.max():.0f}")
    print(f"base rates: D>50 -> {int((d > 50).sum())} days ({(d > 50).mean() * 100:.1f}%)   D<10 -> {int((d < 10).sum())} days ({(d < 10).mean() * 100:.1f}%)   10<=D<=50 -> {int(((d >= 10) & (d <= 50)).sum())}")
    print(f"raw Spearman(D, rest-range) = {spearman(d, rng):+.3f}")

    print("\n== 2. PRIMARY: OLS log(range) ~ log(D+1) + controls, Newey-West lag 5 ==")
    y = np.log(rng)
    names = ["const", "logD", "logGap", "logDrive", "logPriorRng", "logVIX", "netGEX_z"]
    Xf = np.column_stack([np.ones(len(y)), np.log(d + 1), np.log(gap + eps), np.log(drive + eps), np.log(prior_rng), np.log(vix), (net - net.mean()) / net.std()])
    b, se, r2f, _ = ols_nw(y, Xf)
    for i, nm in enumerate(names):
        print(f"  {nm:12s} beta {b[i]:+.4f}  se {se[i]:.4f}  t {b[i] / se[i]:+.2f}")
    Xc = np.delete(Xf, 1, axis=1)
    _, _, r2c, _ = ols_nw(y, Xc)
    print(f"  R2 full {r2f:.4f} | controls-only {r2c:.4f} | delta-R2 from D = {r2f - r2c:.4f}")

    print("\n== 3. User's buckets ==")
    for label, mask in (("D>50 (volatile claim)", d > 50), ("10<=D<=50 (middle)", (d >= 10) & (d <= 50)), ("D<10 (slow claim)", d < 10)):
        if mask.sum():
            print(f"  {label:24s} n={mask.sum():3d}  mean range {rng[mask].mean():.3f}%  median {np.median(rng[mask]):.3f}%")
    hi, lo = (d > 50).astype(float), (d < 10).astype(float)
    Xb = np.column_stack([np.ones(len(y)), hi, lo, np.log(gap + eps), np.log(drive + eps), np.log(prior_rng), np.log(vix), (net - net.mean()) / net.std()])
    bb, seb, _, _ = ols_nw(y, Xb)
    print(f"  regression-adjusted: D>50 dummy beta {bb[1]:+.4f} (t {bb[1] / seb[1]:+.2f})   D<10 dummy beta {bb[2]:+.4f} (t {bb[2] / seb[2]:+.2f})  [vs middle]")

    print("\n== 3b. C#-parity sensitivity (T floor 1/365 + 50-nearest-strike cap, AnalyzeGexCommand conventions) ==")
    if "d_pts_parity" in rows[0]:
        dp = np.array([float(r["d_pts_parity"]) for r in rows])
        ns = np.array([float(r["net_share"]) for r in rows])
        print(f"  D_parity pts: median {np.median(dp):.1f}  p95 {np.percentile(dp, 95):.1f}   base rates: D>50 {int((dp > 50).sum())}  D<10 {int((dp < 10).sum())}   Spearman(D_parity, range) {spearman(dp, rng):+.3f}   agree with primary gravity on {int((dp == d).sum())}/{len(d)} days")
        Xp = Xf.copy()
        Xp[:, 1] = np.log(dp + 1)
        bp, sep, r2p, _ = ols_nw(y, Xp)
        _, _, r2cp, _ = ols_nw(y, np.delete(Xp, 1, axis=1))
        print(f"  parity logD beta {bp[1]:+.4f} (t {bp[1] / sep[1]:+.2f})  dR2 {r2p - r2cp:+.4f}")
        Xn = Xf.copy()
        Xn[:, 6] = (ns - ns.mean()) / ns.std()
        bn, sen, _, _ = ols_nw(y, Xn)
        print(f"  net-share control form (benchmark-study convention) coefficient t {bn[6] / sen[6]:+.2f} (dollar form was reported above)")

    print("\n== 4. Sensitivity ==")
    for label, mask in (("excl early-close", ec == 0), ("excl dropped>20%", dropped <= 0.2), ("2025 only", np.array([x < "2026" for x in dates])), ("2026 only", np.array([x >= "2026" for x in dates]))):
        yb, Xb2 = y[mask], Xf[mask]
        b2, se2, r2a, _ = ols_nw(yb, Xb2)
        Xc2 = np.delete(Xb2, 1, axis=1)
        _, _, r2c2, _ = ols_nw(yb, Xc2)
        print(f"  {label:18s} n={mask.sum():3d}  logD beta {b2[1]:+.4f} (t {b2[1] / se2[1]:+.2f})  dR2 {r2a - r2c2:+.4f}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["assemble", "validate", "stats"])
    ap.add_argument("--data-dir", default=None)
    a = ap.parse_args()
    if a.mode == "stats":
        stats()
    else:
        if not a.data_dir:
            sys.exit("--data-dir required for assemble/validate")
        (assemble if a.mode == "assemble" else validate)(a.data_dir)
