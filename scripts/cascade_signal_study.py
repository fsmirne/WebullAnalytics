"""Tests candidate filters on `wa analyze cascade-study` output: does any signal the app already computes separate cascade/trap
readings whose regime continues to the close from the ones that fail?

Usage: python3 scripts/cascade_signal_study.py <study.jsonl> [--times 09:50,10:30]

Target Y = sign x (close - spot) / dailyMove: the rest-of-session move in the leading regime's direction, in one-day expected
moves (sign +1 for Bull Cascade / Bear Trap, -1 for Bear Cascade / Bull Trap). IS = 2022-2024, OOS = 2025+. A filter survives
only when its IS difference has |t| >= 2.9 and its OOS difference has the same sign with t >= 1.65.
"""
import argparse
import json
import math

import numpy as np
import pandas as pd

SIGN = {"BullCascade": 1, "BearTrap": 1, "BearCascade": -1, "BullTrap": -1}
IS_END = "2024-12-31"
T_IS, T_OOS = 2.9, 1.65


def load(path):
    rows = []
    with open(path) as f:
        for line in f:
            r = json.loads(line)
            rd = r["reading"]
            t, i = rd["terrain"], rd["inputs"]
            tape = i.get("tape") or {}
            lead = rd["leadingSetup"]["regime"]
            em = (t.get("front") or {}).get("dailyMove")
            spot, close = r["spot"], r["close"]
            open_ = tape.get("sessionOpen")
            rows.append({
                "date": r["date"], "time": r["time"], "spot": spot, "close": close,
                "regime": rd["primaryRegime"], "lead": lead, "score": rd["leadingSetup"]["score"], "complete": rd["leadingSetup"]["complete"],
                "sign": SIGN[lead], "em": em,
                "y": SIGN[lead] * (close - spot) / em if em else np.nan,
                "otn_em": (spot - open_) / em if em and open_ else np.nan,
                "net_gex": t.get("netGexFractionMulti"), "front_short": (t.get("front") or {}).get("shortGamma"),
                "flips": tape.get("vwapFlips"), "held": tape.get("minutesHeldOnSide"), "gap_pct": tape.get("gapPct"),
                "flow": i.get("flowShare"), "pace": (i.get("volume") or {}).get("ratio"),
                "vix_now": i.get("vixNow"), "vix_open": i.get("vixOpen"), "vix_prior": i.get("vixPriorClose"),
                "smile_z": i.get("smileZScore20d"),
            })
    df = pd.DataFrame(rows)
    df["oos"] = df["date"] > IS_END
    df["cascade"] = df["lead"].isin(["BullCascade", "BearCascade"])
    return df


def tstat(x):
    x = x.dropna()
    if len(x) < 3:
        return np.nan, np.nan, len(x)
    return x.mean(), x.mean() / (x.std(ddof=1) / math.sqrt(len(x))), len(x)


def diff_t(a, b):
    a, b = a.dropna(), b.dropna()
    if len(a) < 3 or len(b) < 3:
        return np.nan, np.nan
    d = a.mean() - b.mean()
    return d, d / math.sqrt(a.var(ddof=1) / len(a) + b.var(ddof=1) / len(b))


FILTERS = {
    "H1 net GEX <= -30% (cascades)": lambda d: d["net_gex"] <= -0.30,
    "H2 0 VWAP flips": lambda d: d["flips"] == 0,
    "H3 VIX vs open AND close": lambda d: np.where(d["sign"] < 0, (d["vix_now"] > d["vix_open"]) & (d["vix_now"] > d["vix_prior"]), (d["vix_now"] < d["vix_open"]) & (d["vix_now"] < d["vix_prior"])),
    "H4 gap aligned >= 0.15%": lambda d: d["sign"] * d["gap_pct"] >= 0.15,
    "H5 flow >= 0.5 & pace >= 1.5": lambda d: (d["sign"] * d["flow"] >= 0.5) & (d["pace"] >= 1.5),
    "H6 stretch >= 0.75 EM": lambda d: d["otn_em"].abs() >= 0.75,
    "H7 skew aligned |z| >= 0.5": lambda d: np.where(d["sign"] < 0, d["smile_z"] >= 0.5, d["smile_z"] <= -0.5),
}


def fmt(m, t, n):
    return f"{m:+.3f} EM (t {t:+.2f}, n {n})" if n else "n 0"


def report(df, time):
    d = df[df["time"] == time]
    conf = d[d["regime"] != "Neutral"]
    print(f"\n=== {time} ET: {len(d)} sessions, {len(conf)} confirmed ({conf['cascade'].sum()} cascades, {(~conf['cascade']).sum()} traps)")
    mom = np.sign(d["otn_em"]) * (d["close"] - d["spot"]) / d["em"]
    for label, part in (("IS", ~d["oos"]), ("OOS", d["oos"])):
        print(f"  baseline momentum (all sessions, sign of open-to-now) {label}: {fmt(*tstat(mom[part]))}")
    for name, pop in (("all confirmed", conf), ("cascades", conf[conf["cascade"]]), ("traps", conf[~conf["cascade"]])):
        for label, part in (("IS", ~pop["oos"]), ("OOS", pop["oos"])):
            sub = pop[part]
            print(f"  {name:14s} {label:3s}: Y {fmt(*tstat(sub['y']))}, hit {(sub['y'] > 0).mean():.1%}")
    print("  filters (in vs out, confirmed readings):")
    for name, f in FILTERS.items():
        pop = conf[conf["cascade"]] if "cascades" in name else conf
        mask = pd.Series(np.asarray(f(pop), dtype=bool), index=pop.index)
        res = []
        for part in (~pop["oos"], pop["oos"]):
            ins, outs = pop[part & mask]["y"], pop[part & ~mask]["y"]
            dd, tt = diff_t(ins, outs)
            res.append((dd, tt, len(ins.dropna()), len(outs.dropna()), ins.mean(), (ins > 0).mean()))
        (d_is, t_is, n_in_is, n_out_is, m_in_is, h_is), (d_o, t_o, n_in_o, n_out_o, m_in_o, h_o) = res
        ok = (abs(t_is) >= T_IS) and (np.sign(t_o) == np.sign(t_is)) and (abs(t_o) >= T_OOS)
        print(f"    {name:32s} IS in {m_in_is:+.3f} (n {n_in_is}/{n_out_is}, hit {h_is:.0%}) diff {d_is:+.3f} t {t_is:+.2f} | OOS in {m_in_o:+.3f} (n {n_in_o}/{n_out_o}, hit {h_o:.0%}) diff {d_o:+.3f} t {t_o:+.2f}  {'SURVIVES' if ok else ''}")


def features(d):
    """Direction-signed candidates: positive = agrees with the leading regime's direction."""
    s = d["sign"]
    vix_open = (d["vix_now"] / d["vix_open"] - 1) * 100
    vix_close = (d["vix_now"] / d["vix_prior"] - 1) * 100
    return pd.DataFrame({
        "score": d["score"],
        "net_gex": -d["net_gex"],                 # more short gamma = larger
        "front_short": d["front_short"].astype(float),
        "flips": d["flips"],
        "held": d["held"],
        "gap": s * d["gap_pct"],
        "otn_em": s * d["otn_em"],
        "flow": s * d["flow"],
        "pace": d["pace"],
        "vix_open": -s * vix_open,                # VIX falling on a bull reading = agrees
        "vix_close": -s * vix_close,
        "vix_level": d["vix_prior"],
        "smile_z": -s * d["smile_z"],
        "trap": (~d["cascade"]).astype(float),
    }, index=d.index)


def explore(df, time):
    """Walk-forward L2 logistic regression on hit (Y > 0) over the confirmed readings: each year is predicted by a model fit on
    the years before it only. Reports out-of-sample AUC and the mean Y of the top vs bottom predicted tercile."""
    from sklearn.linear_model import LogisticRegression
    from sklearn.metrics import roc_auc_score
    from sklearn.pipeline import make_pipeline
    from sklearn.preprocessing import StandardScaler
    from sklearn.impute import SimpleImputer

    d = df[(df["time"] == time) & (df["regime"] != "Neutral")].dropna(subset=["y"]).copy()
    X, y = features(d), (d["y"] > 0).astype(int)
    year = d["date"].str[:4].astype(int)
    preds = pd.Series(np.nan, index=d.index)
    for yr in sorted(year.unique())[1:]:
        tr, te = year < yr, year == yr
        if tr.sum() < 40 or te.sum() == 0:
            continue
        m = make_pipeline(SimpleImputer(strategy="median"), StandardScaler(), LogisticRegression(C=0.3, max_iter=1000))
        m.fit(X[tr], y[tr])
        preds[te] = m.predict_proba(X[te])[:, 1]
    ok = preds.notna()
    print(f"\n=== explore {time}: walk-forward logistic, {ok.sum()} out-of-sample readings")
    if ok.sum() < 30:
        return
    print(f"  OOS AUC {roc_auc_score(y[ok], preds[ok]):.3f}")
    q = pd.qcut(preds[ok], 3, labels=["low", "mid", "high"])
    for lab in ("low", "mid", "high"):
        sub = d.loc[ok][q == lab]["y"]
        m, t, n = tstat(sub)
        print(f"  predicted {lab:4s}: Y {fmt(m, t, n)}, hit {(sub > 0).mean():.1%}")
    full = make_pipeline(SimpleImputer(strategy="median"), StandardScaler(), LogisticRegression(C=0.3, max_iter=1000)).fit(X, y)
    coefs = sorted(zip(X.columns, full[-1].coef_[0]), key=lambda kv: -abs(kv[1]))
    print("  full-sample standardized coefficients: " + ", ".join(f"{k} {v:+.2f}" for k, v in coefs))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--times", default="09:50,10:30")
    ap.add_argument("--explore", action="store_true")
    a = ap.parse_args()
    df = load(a.path)
    print(f"{df['date'].nunique()} sessions, {len(df)} rows, IS <= {IS_END}")
    for time in a.times.split(","):
        report(df, time)
        if a.explore:
            explore(df, time)


if __name__ == "__main__":
    main()
