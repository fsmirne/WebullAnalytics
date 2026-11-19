"""Tests the contract-VWAP retrace hypothesis on 0DTE SPXW Cascade options:
"After a Bull/Bear Cascade confirms, the 0DTE option contract retraces to its own VWAP and then squeezes."

Matches ai-config.SPXW.CASCADE.json:
  - BullCascade: 0DTE LongCall, anchor=atm (ceil(spot/5)*5), offsetSteps=2 (+10 pts)
  - BearCascade: 0DTE LongPut,  anchor=atm (floor(spot/5)*5), offsetSteps=2 (-10 pts)
  - Trailing stop: 20% (plus 15%, 25%, 30% and hold-to-close for sensitivity)

Reads 1-minute traded option bars (open, high, low, close, volume, vwap) from quotes.db `ohlcv` table.
No intra-bar lookahead: touch checks bar low <= running VWAP known before the bar; trailing stop checks
bar low <= prior peak * (1 - trail) before updating peak.

Usage:
  python scripts/cascade_contract_vwap_study.py <study.jsonl> [--db PATH] [--list-recent N]
"""
import argparse
import json
import math
import os
import sqlite3
from pathlib import Path

import numpy as np
import pandas as pd

CASCADES = {"BullCascade": 1, "BearCascade": -1}
IS_END = "2024-12-31"
STRIKE_STEP = 5
OFFSET_STEPS = 2
RTH_START_SEC = 9 * 3600 + 30 * 60     # 09:30 ET
RTH_END_SEC = 16 * 3600                # 16:00 ET
LAST_ENTRY_SEC = 15 * 3600             # 15:00 ET
TRAIL_PCTS = (0.15, 0.20, 0.25, 0.30)
MIN_CONTROLS = 10
EXT_BAND = 0.15                        # +/- 15% contract extension above VWAP for control matching
def _default_db() -> str:
    base = Path(os.environ.get("LOCALAPPDATA") or (Path.home() / "AppData" / "Local"))
    return str(base / "WebullAnalytics" / "data" / "quotes.db")


DEFAULT_DB = _default_db()


def parse_hhmm_sec(hhmm: str) -> int:
    h, m = hhmm.split(":")
    return int(h) * 3600 + int(m) * 60


def ymd_int(date_str: str) -> int:
    return int(date_str.replace("-", ""))


def target_contract(spot: float, s: int):
    """0DTE strike and right matching CascadeOpener.TradeStrikes (anchor=atm, offsetSteps=2)."""
    if s > 0:
        anchor = math.ceil(spot / STRIKE_STEP) * STRIKE_STEP
        strike = anchor + OFFSET_STEPS * STRIKE_STEP
        return strike, "C"
    else:
        anchor = math.floor(spot / STRIKE_STEP) * STRIKE_STEP
        strike = anchor - OFFSET_STEPS * STRIKE_STEP
        return strike, "P"


def load_readings(path: str) -> pd.DataFrame:
    rows = []
    with open(path) as f:
        for line in f:
            r = json.loads(line)
            rd = r["reading"]
            rows.append({
                "date": r["date"],
                "time": r["time"],
                "spot": float(r["spot"]),
                "regime": rd["primaryRegime"],
                "score": rd["leadingSetup"]["score"],
            })
    df = pd.DataFrame(rows)
    df["oos"] = df["date"] > IS_END
    return df


def fetch_contract_bars(con: sqlite3.Connection, date_str: str, strike: int, right: str):
    """RTH 1-min OHLCV bars for 0DTE SPXW contract from quotes.db, with running session VWAP."""
    d_int = ymd_int(date_str)
    s_milli = int(round(strike * 1000))
    rows = con.execute(
        """
        SELECT time_sec, open, high, low, close, volume, vwap
        FROM ohlcv
        WHERE root = 'SPXW' AND expiry = ? AND date = ? AND strike_milli = ? AND right = ?
          AND time_sec >= ? AND time_sec < ?
        ORDER BY time_sec
        """,
        (d_int, d_int, s_milli, right, RTH_START_SEC, RTH_END_SEC),
    ).fetchall()
    if not rows:
        return None
    arr = np.array(rows, dtype=np.float64)
    t_sec = arr[:, 0].astype(np.int64)
    o = arr[:, 1] / 10000.0
    h = arr[:, 2] / 10000.0
    l = arr[:, 3] / 10000.0
    c = arr[:, 4] / 10000.0
    vol = arr[:, 5]
    bvwap = arr[:, 6] / 10000.0
    # Fallback to typical price when bar vwap is 0
    typ = (h + l + c) / 3.0
    bvwap = np.where(bvwap > 0, bvwap, typ)
    cum_pv = np.cumsum(bvwap * vol)
    cum_v = np.cumsum(vol)
    vwap = np.where(cum_v > 0, cum_pv / cum_v, c)
    return {"t": t_sec, "o": o, "h": h, "l": l, "c": c, "v": vol, "vwap": vwap}


def trail_exit_opt(b, j: int, entry: float, trail_pct: float) -> float:
    """Conservative percentage trailing stop on a long option from bar j. Returns fractional return."""
    if entry <= 0:
        return np.nan
    n = len(b["t"])
    peak = max(entry, b["c"][j])
    for k in range(j + 1, n):
        stop = peak * (1.0 - trail_pct)
        if b["l"][k] <= stop:
            px = min(b["o"][k], stop)
            return (px - entry) / entry
        if b["h"][k] > peak:
            peak = b["h"][k]
    return (b["c"][n - 1] - entry) / entry


def mfe_mae_opt(b, j: int, entry: float):
    if entry <= 0 or j + 1 >= len(b["t"]):
        return 0.0, 0.0
    mfe = max(0.0, (float(b["h"][j + 1 :].max()) - entry) / entry)
    mae = max(0.0, (entry - float(b["l"][j + 1 :].min())) / entry)
    return mfe, mae


def evaluate_contract(b, t0_sec: int):
    """Evaluate immediate entry at t0 vs waiting for retrace to contract VWAP (up to 15:00)."""
    # Find last bar at or before t0_sec
    idxs = np.where(b["t"] <= t0_sec)[0]
    if len(idxs) == 0:
        return None
    i0 = int(idxs[-1])
    # Require a recent bar within 5 minutes of checkpoint so price isn't stale
    if t0_sec - int(b["t"][i0]) > 300:
        return None
    sig_px = float(b["c"][i0])
    sig_vwap = float(b["vwap"][i0])
    if sig_px <= 0.05 or sig_vwap <= 0:
        return None

    ext = (sig_px - sig_vwap) / sig_vwap
    res = {
        "sig_px": sig_px,
        "sig_vwap": sig_vwap,
        "ext": ext,
        "above_vwap": sig_px > sig_vwap,
        "sig_close_ret": (float(b["c"][-1]) - sig_px) / sig_px,
    }
    for tp in TRAIL_PCTS:
        res[f"sig_t{int(tp*100)}"] = trail_exit_opt(b, i0, sig_px, tp)

    # Wait for first bar after i0 (up to 15:00) where low <= prior bar's running VWAP
    n = len(b["t"])
    touch_j = None
    touch_entry = None
    for j in range(i0 + 1, n):
        if b["t"][j] > LAST_ENTRY_SEC:
            break
        vwap_prev = float(b["vwap"][j - 1])
        if b["l"][j] <= vwap_prev:
            touch_j = j
            touch_entry = min(float(b["o"][j]), vwap_prev)
            break

    res["touched"] = touch_j is not None
    if touch_j is not None and touch_entry > 0.05:
        res["touch_min"] = (int(b["t"][touch_j]) - t0_sec) / 60.0
        res["touch_entry"] = touch_entry
        res["y_close"] = (float(b["c"][-1]) - touch_entry) / touch_entry
        res["z_close"] = res["y_close"]
        mfe, mae = mfe_mae_opt(b, touch_j, touch_entry)
        res["mfe"], res["mae"] = mfe, mae
        for tp in TRAIL_PCTS:
            r = trail_exit_opt(b, touch_j, touch_entry, tp)
            res[f"y_t{int(tp*100)}"] = r
            res[f"z_t{int(tp*100)}"] = r
    else:
        res["touch_min"] = np.nan
        res["touch_entry"] = np.nan
        res["y_close"] = np.nan
        res["z_close"] = 0.0
        res["mfe"], res["mae"] = np.nan, np.nan
        for tp in TRAIL_PCTS:
            res[f"y_t{int(tp*100)}"] = np.nan
            res[f"z_t{int(tp*100)}"] = 0.0
    return res


def tstat(x):
    x = pd.Series(x).dropna()
    if len(x) < 3:
        return np.nan, np.nan, len(x)
    s = x.std(ddof=1)
    if s == 0:
        return float(x.mean()), np.nan, len(x)
    return float(x.mean()), float(x.mean() / (s / math.sqrt(len(x)))), len(x)


def fmt_pct(m, t, n):
    if not n or np.isnan(m):
        return f"n {n}"
    return f"{m:+.1%} (t {t:+.2f}, n {n})"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("study")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--list-recent", type=int, default=0)
    args = ap.parse_args()

    readings = load_readings(args.study)
    con = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)

    # First confirmation per (date, regime)
    cascade_days, seen, signals = set(), set(), []
    for r in readings.sort_values(["date", "time"]).itertuples():
        if r.regime not in CASCADES:
            continue
        cascade_days.add(r.date)
        if (r.date, r.regime) in seen:
            continue
        seen.add((r.date, r.regime))
        signals.append({
            "date": r.date,
            "time": r.time,
            "regime": r.regime,
            "s": CASCADES[r.regime],
            "spot": r.spot,
            "oos": r.oos,
        })

    sig_rows = []
    for sg in signals:
        strike, right = target_contract(sg["spot"], sg["s"])
        b = fetch_contract_bars(con, sg["date"], strike, right)
        if b is None:
            continue
        ev = evaluate_contract(b, parse_hhmm_sec(sg["time"]))
        if ev is None:
            continue
        sig_rows.append({**sg, "strike": strike, "right": right, **ev})

    sig = pd.DataFrame(sig_rows)
    if sig.empty:
        print("no contract signals found")
        return

    # Matched controls: non-cascade days at the same checkpoints, same right (C or P), matched on contract ext above VWAP
    times = sorted(set(sig["time"]))
    rights_needed = {t: set(sig[sig["time"] == t]["s"]) for t in times}
    pool_df = readings[readings["time"].isin(times) & ~readings["date"].isin(cascade_days)]
    controls = {}
    for r in pool_df.itertuples():
        t_sec = parse_hhmm_sec(r.time)
        for s in rights_needed[r.time]:
            strike, right = target_contract(r.spot, s)
            b = fetch_contract_bars(con, r.date, strike, right)
            if b is None:
                continue
            ev = evaluate_contract(b, t_sec)
            if ev is None:
                continue
            controls.setdefault((r.time, s), []).append({"date": r.date, **ev})
    con.close()

    # Match controls by (time, direction) and contract extension above VWAP within +/- EXT_BAND
    match_cols = ["touched", "z_close", "mfe", "mae"] + [f"z_t{int(tp*100)}" for tp in TRAIL_PCTS]
    ctrl_rows = []
    for r in sig.itertuples():
        pool = [
            c for c in controls.get((r.time, r.s), [])
            if c["date"] != r.date and c["above_vwap"] == r.above_vwap and abs(c["ext"] - r.ext) <= EXT_BAND
        ]
        if len(pool) < MIN_CONTROLS:
            ctrl_rows.append({**{f"c_{c}": np.nan for c in match_cols}, "n_ctrl": len(pool)})
            continue
        row = {f"c_{c}": float(np.nanmean([float(p[c]) for p in pool])) for c in match_cols}
        row["n_ctrl"] = len(pool)
        ctrl_rows.append(row)
    df = sig.join(pd.DataFrame(ctrl_rows, index=sig.index))

    print(f"Contract VWAP Retrace Study (0DTE SPXW ATM+2 steps = +/-10 pts)")
    print(f"signals with contract data: {len(df)} ({(df['s']>0).sum()} bull C, {(df['s']<0).sum()} bear P), IS {(~df['oos']).sum()} / OOS {df['oos'].sum()}")
    print(f"at signal: {(df['above_vwap']).mean():.1%} above contract VWAP (mean ext {df['ext'].mean():+.1%}, med {df['ext'].median():+.1%})")
    print(f"matched controls (>= {MIN_CONTROLS}): {df['c_z_t20'].notna().sum()}   median controls: {df['n_ctrl'].median():.0f}")

    for label, part in (("ALL", df), ("ABOVE-VWAP-AT-SIG", df[df["above_vwap"]]), ("IS", df[~df["oos"]]), ("OOS", df[df["oos"]])):
        if part.empty:
            continue
        print(f"\n--- {label} (n={len(part)}) ---")
        print(f"  P(touch contract VWAP by 15:00): signal {part['touched'].mean():.1%}  control {part['c_touched'].mean():.1%}")
        t = part[part["touched"]]
        if len(t):
            print(f"  touched only: med min to touch {t['touch_min'].median():.0f} | MFE {t['mfe'].mean():+.1%} (med {t['mfe'].median():+.1%}) | MAE {t['mae'].mean():.1%} (med {t['mae'].median():.1%})")
        for tp in TRAIL_PCTS:
            k = int(tp * 100)
            col = f"z_t{k}"
            d = (part[col] - part[f"c_{col}"]).dropna()
            print(
                f"  trail {k}%: touch {fmt_pct(*tstat(part[col]))} | ctrl {fmt_pct(*tstat(part['c_' + col]))} "
                f"| delta {fmt_pct(*tstat(d))} | at-signal {fmt_pct(*tstat(part[f'sig_t{k}']))}"
            )
        d_cl = (part["z_close"] - part["c_z_close"]).dropna()
        print(
            f"  close   : touch {fmt_pct(*tstat(part['z_close']))} | ctrl {fmt_pct(*tstat(part['c_z_close']))} "
            f"| delta {fmt_pct(*tstat(d_cl))} | at-signal {fmt_pct(*tstat(part['sig_close_ret']))}"
        )

    print("\nby direction (20% trail, all signals):")
    for name, part in (("bull (LongCall)", df[df["s"] > 0]), ("bear (LongPut)", df[df["s"] < 0])):
        d = (part["z_t20"] - part["c_z_t20"]).dropna()
        print(
            f"  {name}: touch {part['touched'].mean():.1%}, touch_t20 {fmt_pct(*tstat(part['z_t20']))}, "
            f"delta {fmt_pct(*tstat(d))}, at-signal {fmt_pct(*tstat(part['sig_t20']))}"
        )

    if args.list_recent:
        print(f"\nlast {args.list_recent} signals:")
        cols = ["date", "time", "regime", "strike", "right", "sig_px", "ext", "touched", "touch_min", "y_t20", "sig_t20", "mfe"]
        print(df.sort_values("date").tail(args.list_recent)[cols].to_string(index=False, float_format=lambda v: f"{v:+.2f}"))


if __name__ == "__main__":
    main()
