"""Can a richer pre-open / intraday forecast of realized variance beat the scorer's RealizedVolModel (EWMA half-life 10)?

Two targets, both what CandidateScorer's scenario grid consumes through RealizedVolModel.VarianceTo:
  DC2   - variance over the next 5 sessions from before today's open: sum of (ln open/prev close)^2 + (ln close/open)^2.
  0DTE  - variance from an intraday checkpoint to the close: sum of squared 5-minute log returns on the 1-min tape.
Walk-forward by year (fit on earlier years only), log-linear regressions, forecast = exp(fit + s^2/2). Loss = QLIKE.
Significance = Diebold-Mariano with Newey-West HAC. Usage:
  python scripts/realized_vol_forecast_study.py <data dir> <cascade-study 09:31 SPY jsonl>
"""
import json
import math
import os
import sys
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd

DATA, GEX = sys.argv[1], sys.argv[2]
SMEAR = "--lognormal" not in sys.argv
ET = ZoneInfo("America/New_York")
HALF_LIFE, HISTORY = 10.0, 60          # RealizedVolModel constants
TEST_YEARS = [2023, 2024, 2025, 2026]
CHECKPOINTS = ["09:45", "10:30", "12:00"]


def daily():
    d = pd.read_csv(f"{DATA}/history/SPY.csv", parse_dates=["date"]).sort_values("date").reset_index(drop=True)
    d["gap2"] = np.log(d.open / d.close.shift(1)) ** 2
    d["sess2"] = np.log(d.close / d.open) ** 2
    d["park"] = np.log(d.high / d.low) ** 2 / (4 * math.log(2))
    decay = 0.5 ** (1 / HALF_LIFE)
    on, se = [np.nan] * len(d), [np.nan] * len(d)
    for i in range(len(d)):                     # model built strictly before day i: returns i-60 .. i-1
        lo = max(1, i - HISTORY)
        if i - lo < 1: continue
        w = decay ** np.arange(i - lo)[::-1]   # most recent return weight 1
        on[i] = float(np.sum(w * d.gap2.values[lo:i]) / w.sum())
        se[i] = float(np.sum(w * d.sess2.values[lo:i]) / w.sum())
    d["ewma_on"], d["ewma_se"] = on, se
    d["day2"] = d.gap2 + d.sess2
    for col in ("day2", "park"):               # HAR components, known before today's open
        d[f"{col}_1"] = d[col].shift(1)
        d[f"{col}_5"] = d[col].shift(1).rolling(5).mean()
        d[f"{col}_22"] = d[col].shift(1).rolling(22).mean()
    d["rv5"] = sum(d.day2.shift(-k) for k in range(5))
    for name in ("VIX", "VIX9D", "VIX1D"):
        v = pd.read_csv(f"{DATA}/history/{name}.csv", parse_dates=["date"])[["date", "close"]].rename(columns={"close": name})
        d = d.merge(v, on="date", how="left")
        d[name] = d[name].shift(1)             # prior close only
    return d


def gex():
    rows = []
    if not os.path.exists(GEX): return pd.DataFrame(columns=["date", "net_gex", "short_gamma"]).astype({"date": "datetime64[ns]"})
    for line in open(GEX):
        r = json.loads(line)
        t = r["reading"]["terrain"]
        rows.append({"date": pd.Timestamp(r["date"]), "net_gex": t.get("netGexFractionMulti"), "short_gamma": float((t.get("front") or {}).get("shortGamma", False))})
    return pd.DataFrame(rows)


def intraday(dates):
    """Per session: 5-min price samples from the open; returns (today gap known), RV so far and RV remaining per checkpoint."""
    out = []
    for dt in dates:
        p = f"{DATA}/intraday/SPY/{dt:%Y-%m-%d}.csv"
        if not os.path.exists(p): continue
        t = pd.read_csv(p)
        et = pd.to_datetime(t.timestamp_utc, utc=True).dt.tz_convert(ET)
        t["m"] = et.dt.hour * 60 + et.dt.minute
        t = t[(t.m >= 570) & (t.m < 960)]
        if len(t) < 380: continue              # skip half-days and gappy sessions
        px = [t.open.iloc[0]] + [t[t.m < 570 + 5 * k].close.iloc[-1] for k in range(1, 79)]
        r2 = np.diff(np.log(px)) ** 2           # 78 five-minute returns
        row = {"date": dt, "rv_day": r2.sum()}
        for cp in CHECKPOINTS:
            k = (int(cp[:2]) * 60 + int(cp[3:]) - 570) // 5
            row[f"so_far_{cp}"], row[f"rem_{cp}"] = r2[:k].sum(), r2[k:].sum()
        out.append(row)
    return pd.DataFrame(out)


def qlike(rv, f):
    x = rv / f
    return x - np.log(x) - 1


def dm(dloss, lag):
    d = np.asarray(dloss, float); n = len(d); m = d.mean(); e = d - m
    v = e @ e / n
    for k in range(1, lag + 1): v += 2 * (1 - k / (lag + 1)) * (e[k:] @ e[:-k]) / n
    return m / math.sqrt(v / n)


def walk_forward(df, target, feats, base, label, lag):
    """Log-linear fit of the target on `feats` per test year (train = earlier years), QLIKE vs `base` forecast."""
    res = []
    for year in TEST_YEARS:
        train = df[(df.year < year)].dropna(subset=[target] + feats)
        test = df[df.year == year].dropna(subset=[target, base] + feats)
        if len(train) < 100 or len(test) == 0: continue
        X = np.column_stack([np.ones(len(train))] + [train[f] for f in feats])
        y = np.log(train[target])
        b, *_ = np.linalg.lstsq(X, y, rcond=None)
        resid = y - X @ b
        Xt = np.column_stack([np.ones(len(test))] + [test[f] for f in feats])
        # Back to variance: Duan's smearing factor mean(exp(residual)) — the conditional mean without assuming normal log residuals
        # (exp(s^2/2) over-corrects under the fat-tailed residuals realized variance has).
        f = np.exp(Xt @ b) * (np.mean(np.exp(resid)) if SMEAR else np.exp(np.var(resid) / 2))
        res.append(pd.DataFrame({"year": year, "lm": qlike(test[target].values, f), "lb": qlike(test[target].values, test[base].values)}))
    if not res:
        print(f"  {label:44s} (no data)"); return
    r = pd.concat(res)
    out = []
    for name, part in (("2023-24", r.year <= 2024), ("2025-26", r.year >= 2025)):
        x = r[part]
        gain = 1 - x.lm.mean() / x.lb.mean()
        out.append(f"{name}: QLIKE {x.lm.mean():.3f} vs base {x.lb.mean():.3f} ({gain:+.1%}, DM t {dm(x.lb - x.lm, lag):+.2f}, n {len(x)})")
    per_year = " ".join(f"{y}:{1 - g.lm.mean() / g.lb.mean():+.0%}" for y, g in r.groupby("year"))
    print(f"  {label:44s} " + " | ".join(out) + f"  [{per_year}]")


def main():
    d = daily().merge(gex(), on="date", how="left")
    d["year"] = d.date.dt.year
    d = d[d.year >= 2022].copy()
    d["b0_dc2"] = 5 * (d.ewma_on + d.ewma_se)
    lg = lambda c: np.log(d[c].clip(lower=1e-10))
    for c in ("b0_dc2", "day2_1", "day2_5", "day2_22", "park_1", "park_5", "ewma_se", "ewma_on"): d[f"l_{c}"] = lg(c)
    for v in ("VIX", "VIX9D", "VIX1D"): d[f"l_{v}"] = np.log((d[v] / 100) ** 2)

    print("=== DC2 target: next-5-session realized variance, forecast before today's open (SPY)")
    walk_forward(d, "rv5", ["l_b0_dc2"], "b0_dc2", "B0' rescaled EWMA (bias only)", 5)
    walk_forward(d, "rv5", ["l_day2_1", "l_day2_5", "l_day2_22"], "b0_dc2", "D1 HAR", 5)
    walk_forward(d, "rv5", ["l_day2_1", "l_day2_5", "l_day2_22", "l_park_1", "l_park_5"], "b0_dc2", "D2 HAR + range", 5)
    walk_forward(d, "rv5", ["l_day2_1", "l_day2_5", "l_day2_22", "l_park_1", "l_park_5", "l_VIX", "l_VIX9D"], "b0_dc2", "D3 HAR + range + VIX/VIX9D", 5)
    walk_forward(d, "rv5", ["l_day2_1", "l_day2_5", "l_day2_22", "l_park_1", "l_park_5", "l_VIX", "l_VIX9D", "net_gex", "short_gamma"], "b0_dc2", "D4 D3 + net GEX + short gamma", 5)

    i = intraday(d.date)
    z = d.merge(i, on="date", how="inner")
    print(f"\n=== 0DTE target: realized variance from checkpoint to close, 5-min returns (SPY, {len(z)} full sessions)")
    for cp in CHECKPOINTS:
        mins = 960 - (int(cp[:2]) * 60 + int(cp[3:]))
        z[f"b0_{cp}"] = z.ewma_se * mins / 390
        z[f"l_b0_{cp}"] = np.log(z[f"b0_{cp}"])
        z[f"l_sofar_{cp}"] = np.log(z[f"so_far_{cp}"].clip(lower=1e-10))
        z["l_gap2"] = np.log(z.gap2.clip(lower=1e-10))
        print(f"  -- {cp} ET")
        walk_forward(z, f"rem_{cp}", [f"l_b0_{cp}"], f"b0_{cp}", "Z1 seasonality/bias-rescaled EWMA", 1)
        walk_forward(z, f"rem_{cp}", [f"l_b0_{cp}", f"l_sofar_{cp}", "l_gap2"], f"b0_{cp}", "Z2 Z1 + realized so far + gap", 1)
        walk_forward(z, f"rem_{cp}", [f"l_b0_{cp}", f"l_sofar_{cp}", "l_gap2", "l_VIX1D", "l_VIX", "net_gex", "short_gamma"], f"b0_{cp}", "Z3 Z2 + VIX1D/VIX + net GEX + short gamma", 1)


if __name__ == "__main__":
    main()
