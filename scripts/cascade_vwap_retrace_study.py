"""Tests the pattern "after a Bull/Bear Cascade confirms, price retraces to VWAP and then squeezes in the cascade's direction".

Usage: python3 scripts/cascade_vwap_retrace_study.py <study.jsonl> [--intraday DIR] [--times 09:40,...] [--list-recent N]

Pre-registration lives in scripts/cascade_vwap_retrace_study.md. Everything here is fixed before any outcome was computed.

Signal  : the FIRST checkpoint of a session where the reading's primaryRegime is BullCascade (s=+1) or BearCascade (s=-1) (the opener's rule: one entry per
          regime per session). t0 = checkpoint label T, spot0 = the reading's spot (close of the bar labeled T).
VWAP    : running TWAP of the RTH typical price (H+L+C)/3 from the 09:30 bar, what AI/SessionVwap gives a cash index (no volume).
Touch   : the first bar labeled after T, up to 15:00, whose low (s=+1) / high (s=-1) reaches the VWAP known at the start of the bar. A limit order at that
          VWAP fills there, or at the bar's open when the bar gaps through it. Entry price = that fill, entry time = the bar's label.
Outcome : Y_h = s * (price at entry + h minutes - entry) / EM for h in {30, 60} (close of the bar labeled entry+h-1; None when the session ends first)
          and to the session close, EM = the reading's front-expiry daily move. Per signal session Z_h = Y_h if touched else 0 (no touch = no trade).
Control : every session with no Bull/Bear Cascade confirmation at ANY checkpoint, read at the same checkpoint, price on the same side of VWAP as the signal's, and an
          extension s*(spot-VWAP)/EM within 0.10 EM of the signal's. Same touch rule, same outcome, same Z. A signal's control value is the mean over its controls.
Test    : paired difference Delta_i = Z_sig,i - mean Z_ctrl(match i); t over signals. IS = 2022-2024, OOS = 2025+. PRIMARY = Delta of Z_60 over all signals.
          It survives only when its IS |t| >= 2.9 and its OOS t has the same sign and |t| >= 1.65. Everything else is secondary and reported without a verdict.
"""
import argparse
import json
import math
import os
import zoneinfo
from datetime import datetime, time as dtime
from pathlib import Path

import numpy as np
import pandas as pd

ET = zoneinfo.ZoneInfo("America/New_York")
CASCADES = {"BullCascade": 1, "BearCascade": -1}
IS_END = "2024-12-31"
T_IS, T_OOS = 2.9, 1.65
MATCH_BAND = 0.10          # EM, extension matching bandwidth
MIN_CONTROLS = 10
LAST_ENTRY = dtime(15, 0)
HORIZONS = (30, 60, "close")
TRAILS = (0.15, 0.25, 0.35)  # trailing stop widths in daily EM
def _default_intraday() -> str:
    base = Path(os.environ.get("LOCALAPPDATA") or (Path.home() / "AppData" / "Local"))
    return str(base / "WebullAnalytics" / "data" / "intraday" / "SPXW")


DEFAULT_INTRADAY = _default_intraday()


def load_readings(path):
    rows = []
    with open(path) as f:
        for line in f:
            r = json.loads(line)
            rd = r["reading"]
            em = (rd["terrain"].get("front") or {}).get("dailyMove")
            rows.append({"date": r["date"], "time": r["time"], "spot": r["spot"], "em": em, "regime": rd["primaryRegime"], "score": rd["leadingSetup"]["score"]})
    df = pd.DataFrame(rows)
    df["oos"] = df["date"] > IS_END
    return df


_BARS = {}


def bars_for(intraday, date):
    """RTH 1-minute bars of a session as arrays, START-of-bar labels (09:30..15:59 ET)."""
    if date in _BARS:
        return _BARS[date]
    p = Path(intraday) / f"{date}.csv"
    out = None
    if p.exists():
        df = pd.read_csv(p)
        ts = pd.to_datetime(df["timestamp_utc"], utc=True).dt.tz_convert(ET)
        df["label"] = ts.dt.time
        df = df[(df["label"] >= dtime(9, 30)) & (df["label"] < dtime(16, 0))].reset_index(drop=True)
        if len(df):
            typical = ((df["high"] + df["low"] + df["close"]) / 3).to_numpy()
            out = {
                "label": df["label"].tolist(), "open": df["open"].to_numpy(), "high": df["high"].to_numpy(), "low": df["low"].to_numpy(),
                "close": df["close"].to_numpy(), "twap": np.cumsum(typical) / np.arange(1, len(df) + 1),
            }
    _BARS[date] = out
    return out


def minutes(t):
    return t.hour * 60 + t.minute


def checkpoint_index(b, T):
    """Index of the bar labeled T, or None."""
    m = minutes(T)
    for i, lab in enumerate(b["label"]):
        if minutes(lab) == m:
            return i
    return None


def extension(b, i, spot, em, s):
    """Signed distance of spot from the VWAP at bar i, in EM, positive when price is on the side the regime expects."""
    return s * (spot - b["twap"][i]) / em


def trail_exit(b, j, entry, s, em, width):
    """Conservative trailing-stop exit from entry on bar j (width in daily EM). Checks stop before updating peak on each bar."""
    n = len(b["label"])
    peak = max(entry, b["close"][j]) if s > 0 else min(entry, b["close"][j])
    dist = width * em
    for k in range(j + 1, n):
        if s > 0:
            stop = peak - dist
            if b["low"][k] <= stop:
                px = min(b["open"][k], stop)
                return (px - entry) / em
            if b["high"][k] > peak:
                peak = b["high"][k]
        else:
            stop = peak + dist
            if b["high"][k] >= stop:
                px = max(b["open"][k], stop)
                return (entry - px) / em
            if b["low"][k] < peak:
                peak = b["low"][k]
    return s * (b["close"][n - 1] - entry) / em


def mfe_mae(b, j, entry, s, em):
    """Max favorable excursion (>=0) and max adverse excursion (>=0) from bar j+1 to close, in daily EM."""
    n = len(b["label"])
    if j + 1 >= n:
        return 0.0, 0.0
    if s > 0:
        mfe = max(0.0, float(b["high"][j + 1 :].max() - entry) / em)
        mae = max(0.0, float(entry - b["low"][j + 1 :].min()) / em)
    else:
        mfe = max(0.0, float(entry - b["low"][j + 1 :].min()) / em)
        mae = max(0.0, float(b["high"][j + 1 :].max() - entry) / em)
    return mfe, mae


def touch_and_outcomes(b, i0, s, em):
    """First VWAP touch after bar i0 and the fixed-horizon + trailing-stop outcomes from it. None when there is no touch before LAST_ENTRY."""
    n = len(b["label"])
    for j in range(i0 + 1, n):
        if b["label"][j] > LAST_ENTRY:
            return None
        vwap = b["twap"][j - 1]
        reached = b["low"][j] <= vwap if s > 0 else b["high"][j] >= vwap
        if not reached:
            continue
        opened_through = b["open"][j] <= vwap if s > 0 else b["open"][j] >= vwap
        entry = b["open"][j] if opened_through else vwap
        out = {"touch_min": minutes(b["label"][j]) - minutes(b["label"][i0]), "entry": entry}
        for h in HORIZONS:
            if h == "close":
                px = b["close"][n - 1]
            else:
                k = j + h - 1
                px = b["close"][k] if k < n else None
            out[f"y{h}"] = None if px is None else s * (px - entry) / em
        mfe, mae = mfe_mae(b, j, entry, s, em)
        out["mfe"], out["mae"] = mfe, mae
        for w in TRAILS:
            out[f"ytrail_{w:.2f}"] = trail_exit(b, j, entry, s, em, w)
        # Did the move that follows the touch make a new session extreme in the regime's direction within 60 minutes?
        pre = b["high"][: j].max() if s > 0 else b["low"][: j].min()
        k_end = min(n, j + 60)
        post = b["high"][j:k_end].max() if s > 0 else b["low"][j:k_end].min()
        out["new_extreme60"] = bool(post > pre) if s > 0 else bool(post < pre)
        return out
    return None


def evaluate(b, i0, s, em):
    """Touch outcomes for an event at bar i0 (None entries when nothing was traded). Z_h counts a missing touch as 0."""
    ev = touch_and_outcomes(b, i0, s, em)
    res = {
        "touched": ev is not None,
        "touch_min": ev["touch_min"] if ev else np.nan,
        "new_extreme60": ev["new_extreme60"] if ev else np.nan,
        "mfe": ev["mfe"] if ev else np.nan,
        "mae": ev["mae"] if ev else np.nan,
    }
    for h in HORIZONS:
        y = ev.get(f"y{h}") if ev else None
        res[f"y{h}"] = np.nan if y is None else y
        res[f"z{h}"] = 0.0 if ev is None else (np.nan if y is None else y)
    for w in TRAILS:
        key = f"trail_{w:.2f}"
        y = ev.get(f"y{key}") if ev else None
        res[f"y{key}"] = np.nan if y is None else y
        res[f"z{key}"] = 0.0 if ev is None else y
        res[f"sig_{key}"] = trail_exit(b, i0, b["close"][i0], s, em, w)
    return res


def build_events(readings, intraday):
    cascade_days, seen, signals = set(), set(), []
    for r in readings.sort_values(["date", "time"]).itertuples():
        if r.regime not in CASCADES:
            continue
        cascade_days.add(r.date)
        if (r.date, r.regime) in seen:
            continue
        seen.add((r.date, r.regime))
        signals.append({"date": r.date, "time": r.time, "regime": r.regime, "s": CASCADES[r.regime], "spot": r.spot, "em": r.em, "oos": r.oos})
    sig_rows = []
    for sg in signals:
        b = bars_for(intraday, sg["date"])
        if b is None or not sg["em"]:
            continue
        i0 = checkpoint_index(b, dtime.fromisoformat(sg["time"]))
        if i0 is None:
            continue
        sig_rows.append({**sg, "ext": extension(b, i0, sg["spot"], sg["em"], sg["s"]), **evaluate(b, i0, sg["s"], sg["em"])})
    sig = pd.DataFrame(sig_rows)

    # Controls: sessions with no cascade confirmation at any checkpoint, read at the checkpoints signals fired at, keyed by (time, side of VWAP).
    controls = {}
    times = sorted(set(sig["time"])) if len(sig) else []
    pool = readings[readings["time"].isin(times) & readings["em"].notna() & ~readings["date"].isin(cascade_days)]
    for r in pool.itertuples():
        b = bars_for(intraday, r.date)
        if b is None:
            continue
        i0 = checkpoint_index(b, dtime.fromisoformat(r.time))
        if i0 is None:
            continue
        side = 1 if r.spot >= b["twap"][i0] else -1
        ext = extension(b, i0, r.spot, r.em, side)
        controls.setdefault((r.time, side), []).append({"date": r.date, "ext": ext, **evaluate(b, i0, side, r.em)})
    return sig, controls


def match(sig, controls):
    """Per signal: the mean of its matched controls' outcomes, or None when too few match."""
    cols = [f"z{h}" for h in HORIZONS] + [f"ztrail_{w:.2f}" for w in TRAILS] + ["touched", "new_extreme60", "mfe", "mae"]
    out = []
    for r in sig.itertuples():
        pool = [c for c in controls.get((r.time, r.s), []) if c["date"] != r.date and abs(c["ext"] - r.ext) <= MATCH_BAND]
        if len(pool) < MIN_CONTROLS:
            out.append({**{c: np.nan for c in cols}, "n_ctrl": len(pool)})
            continue
        row = {c: float(np.nanmean([float(p[c]) for p in pool])) for c in cols}
        row["n_ctrl"] = len(pool)
        out.append(row)
    return pd.DataFrame(out, index=sig.index).add_prefix("c_").rename(columns={"c_n_ctrl": "n_ctrl"})


def tstat(x):
    x = pd.Series(x).dropna()
    if len(x) < 3:
        return np.nan, np.nan, len(x)
    return x.mean(), x.mean() / (x.std(ddof=1) / math.sqrt(len(x))), len(x)


def fmt(m, t, n):
    return f"{m:+.3f} EM (t {t:+.2f}, n {n})" if n else "n 0"


def verdict(is_t, oos_t):
    ok = abs(is_t) >= T_IS and np.sign(oos_t) == np.sign(is_t) and abs(oos_t) >= T_OOS
    return "SURVIVES" if ok else "fails"


def report(sig, ctrl):
    df = sig.join(ctrl)
    print(f"\nsignals: {len(df)} sessions ({(df['s'] > 0).sum()} bull, {(df['s'] < 0).sum()} bear), IS {(~df['oos']).sum()} / OOS {df['oos'].sum()}")
    print(f"signals with >= {MIN_CONTROLS} matched controls: {df['c_z60'].notna().sum()}   median controls per signal: {df['n_ctrl'].median():.0f}")
    print(f"mean extension at the signal: {df['ext'].mean():+.2f} EM   (negative = price already on the wrong side of VWAP)")
    for label, part in (("ALL", df), ("IS", df[~df["oos"]]), ("OOS", df[df["oos"]])):
        print(f"\n--- {label} ---")
        print(f"  P(touch VWAP by 15:00): signal {part['touched'].mean():.1%}  control {part['c_touched'].mean():.1%}")
        t = part[part["touched"]]
        print(f"  touched only: median minutes to touch {t['touch_min'].median():.0f}; new session extreme within 60m after touch: signal {t['new_extreme60'].mean():.1%}  control {t['c_new_extreme60'].mean():.1%}")
        for h in HORIZONS:
            col = f"z{h}"
            d = (part[col] - part[f"c_{col}"]).dropna()
            print(f"  Z_{h}: signal {fmt(*tstat(part[col]))} | control {fmt(*tstat(part['c_' + col]))} | paired delta {fmt(*tstat(d))}")
    d_is, d_oos = (df[~df["oos"]]["z60"] - df[~df["oos"]]["c_z60"]), (df[df["oos"]]["z60"] - df[df["oos"]]["c_z60"])
    _, t_is, _ = tstat(d_is)
    _, t_oos, _ = tstat(d_oos)
    print(f"\nPRIMARY (paired delta of Z_60, all signals): IS t {t_is:+.2f}, OOS t {t_oos:+.2f} -> {verdict(t_is, t_oos)}")
    print("\nsecondary, by direction (no verdict):")
    for name, part in (("bull", df[df["s"] > 0]), ("bear", df[df["s"] < 0])):
        d = (part["z60"] - part["c_z60"]).dropna()
        print(f"  {name}: touch {part['touched'].mean():.1%}, Z_60 {fmt(*tstat(part['z60']))}, paired delta {fmt(*tstat(d))}")
    print("\nsecondary, entry at the VWAP touch vs entry at the signal (same signals, to the close, signed EM):")
    at_signal = df.apply(lambda r: r["s"] * (_BARS[r["date"]]["close"][-1] - r["spot"]) / r["em"] if _BARS.get(r["date"]) else np.nan, axis=1)
    print(f"  at the signal: {fmt(*tstat(at_signal))}   at the touch (0 when no touch): {fmt(*tstat(df['zclose']))}")
    print("\nsecondary, horizon-agnostic (MFE/MAE and trailing stop, all signals):")
    t_all = df[df["touched"]]
    if len(t_all):
        print(f"  touched-only MFE: signal {t_all['mfe'].mean():.3f} EM (med {t_all['mfe'].median():.3f}) | control {t_all['c_mfe'].mean():.3f} EM")
        print(f"  touched-only MAE: signal {t_all['mae'].mean():.3f} EM (med {t_all['mae'].median():.3f}) | control {t_all['c_mae'].mean():.3f} EM")
    for w in TRAILS:
        col = f"ztrail_{w:.2f}"
        d = (df[col] - df[f"c_{col}"]).dropna()
        print(
            f"  trail {w:.2f} EM: touch {fmt(*tstat(df[col]))} | ctrl {fmt(*tstat(df['c_' + col]))} "
            f"| delta {fmt(*tstat(d))} | at-signal {fmt(*tstat(df[f'sig_trail_{w:.2f}']))}"
        )


def list_recent(sig, n):
    print(f"\nlast {n} signals (descriptive, not a test):")
    cols = ["date", "time", "regime", "ext", "touched", "touch_min", "y30", "y60", "yclose", "new_extreme60"]
    print(sig.sort_values("date").tail(n)[cols].to_string(index=False, float_format=lambda v: f"{v:+.2f}"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("study")
    ap.add_argument("--intraday", default=DEFAULT_INTRADAY)
    ap.add_argument("--list-recent", type=int, default=0)
    args = ap.parse_args()
    readings = load_readings(args.study)
    print(f"{len(readings)} readings, {readings['date'].nunique()} sessions")
    sig, controls = build_events(readings, args.intraday)
    if sig.empty:
        print("no cascade signals")
        return
    ctrl = match(sig, controls)
    report(sig, ctrl)
    if args.list_recent:
        list_recent(sig, args.list_recent)


if __name__ == "__main__":
    main()
