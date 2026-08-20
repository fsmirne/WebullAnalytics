"""Study 5-C audit gate (registered 2026-08-19 in the local campaign doc BEFORE any signal work):
sign-integrity of the coarse quote-rule signing (ohlcv minute-bar close vs same-minute NBBO mid) plus the
ΔOI reconciliation. This gate decides whether the signed-flow studies are evaluable at all — the registered
threshold is >= 70% of premium cleanly signable; below it we report NOT EVALUABLE and stop.

Signing convention (registered): buy if close > mid, sell if close < mid, with an unsigned band of
|close - mid| < 0.25 x spread (a strict-mid variant is reported as sensitivity). Weights are premium
(close x volume x multiplier-free — relative weights only, so the 100x cancels).

Modes:
  --mode builds   (SPY-style, 1DTE+): signable fraction over contracts expiring AFTER the session, plus the
                  ΔOI reconciliation on vol >= 250 contracts — |ΔOI| <= vol violations, corr(vol, |ΔOI|),
                  and |net signed| / vol on near-pure-open contracts (ΔOI >= 0.8 x vol).
  --mode intraday (SPXW-style, 0DTE): signable fraction over same-day-expiry bars (no ΔOI — the contracts
                  die the same day).

Runs WINDOWS-NATIVE (python.exe) per the quotes.db access rule. Read-only on the store.
Usage: python research\\signed_flow\\audit.py --root SPY --mode builds --sessions 24
"""
import argparse
import json
import os
import re
import sqlite3
import sys
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from import_quotes_sqlite import resolve_data_dir  # noqa: E402

OCC = re.compile(r"^([A-Z]+)(\d{6})([CP])(\d{8})$")


def oi_snapshot(data_dir: Path, root: str, day: str):
    """symbol -> (volume, oi) from the day's data/oi snapshot; None when absent."""
    p = data_dir / "oi" / root / f"{day}.jsonl"
    if not p.exists():
        return None
    rec = json.loads(p.read_text().strip().splitlines()[-1])
    out = {}
    for o in rec.get("options", []):
        m = OCC.match(o.get("symbol") or "")
        if m and m.group(1) == root:
            out[(int("20" + m.group(2)), int(m.group(4)), m.group(3))] = (o.get("volume") or 0, o.get("openInterest") or 0)
    return out


def session_bars(conn, root: str, date_int: int, expiries):
    """Joined (signed) bars for one session: per (expiry,strike,right,minute) -> close, volume, bid, ask.
    Queried per expiry so every lookup is a PK-prefix seek, never a root-wide scan."""
    rows = []
    for exp in expiries:
        bars = conn.execute("SELECT time_sec, strike_milli, right, close, volume FROM ohlcv WHERE root=? AND expiry=? AND date=?",
                            (root, exp, date_int)).fetchall()
        if not bars:
            continue
        nbbo = {(t, k, r): (b, a) for t, k, r, b, a in conn.execute(
            "SELECT time_sec, strike_milli, right, bid, ask FROM quotes WHERE root=? AND expiry=? AND date=?", (root, exp, date_int))}
        for t, k, r, close, vol in bars:
            ba = nbbo.get((t, k, r))
            rows.append((exp, k, r, close, vol, ba[0] if ba else None, ba[1] if ba else None))
    return rows


def audit_session(conn, data_dir, root, day_iso, next_iso, mode):
    date_int = int(day_iso.replace("-", ""))
    snap = oi_snapshot(data_dir, root, day_iso)
    nxt = oi_snapshot(data_dir, root, next_iso) if next_iso else None
    exps = conn.execute("SELECT DISTINCT expiry FROM ohlcv WHERE root=? AND expiry>=? AND expiry<=? AND date=?",
                        (root, date_int, date_int + 10000, date_int)).fetchall()
    exps = [e for (e,) in exps if (mode == "intraday") == (e == date_int)]
    bars = session_bars(conn, root, date_int, exps)
    if not bars:
        return None

    prem_buy = prem_sell = prem_unsigned = prem_nombbo = 0.0
    prem_buy_strict = prem_sell_strict = 0.0
    per_contract = {}
    for exp, k, r, close, vol, bid, ask in bars:
        prem = close / 10000.0 * vol
        c = per_contract.setdefault((exp, k, r), [0, 0, 0])  # [vol, netsigned, signedvol]
        c[0] += vol
        if bid is None or ask is None or bid <= 0 or ask <= 0:
            prem_nombbo += prem
            continue
        mid, spread = (bid + ask) / 2.0, ask - bid
        if close > mid:
            prem_buy_strict += prem
        elif close < mid:
            prem_sell_strict += prem
        if abs(close - mid) < 0.25 * spread:
            prem_unsigned += prem
        elif close > mid:
            prem_buy += prem
            c[1] += vol
            c[2] += vol
        else:
            prem_sell += prem
            c[1] -= vol
            c[2] += vol

    total = prem_buy + prem_sell + prem_unsigned + prem_nombbo
    if total <= 0:
        return None
    out = {"day": day_iso, "prem_total": total, "signable": (prem_buy + prem_sell) / total,
           "strict_signable": (prem_buy_strict + prem_sell_strict) / total, "no_nbbo": prem_nombbo / total,
           "contracts": len(per_contract)}
    if mode == "builds" and snap is not None and nxt is not None:
        viol = n = 0
        pure_open_conf = []
        vols, dois = [], []
        for key, (vol, net, sv) in per_contract.items():
            if vol < 250:
                continue
            oi0 = snap.get(key, (0, 0))[1]
            oi1 = nxt.get(key, (None, None))[1]
            if oi1 is None:
                continue
            doi = oi1 - oi0
            n += 1
            vols.append(vol)
            dois.append(abs(doi))
            if abs(doi) > vol:
                viol += 1
            if doi >= 0.8 * vol and sv > 0:
                pure_open_conf.append(abs(net) / sv)
        out.update({"recon_n": n, "recon_viol": viol,
                    "pure_open_n": len(pure_open_conf),
                    "pure_open_conf": sum(pure_open_conf) / len(pure_open_conf) if pure_open_conf else None,
                    "vols": vols, "dois": dois})
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--mode", choices=["builds", "intraday"], required=True)
    ap.add_argument("--sessions", type=int, default=24)
    args = ap.parse_args()

    data_dir = resolve_data_dir()
    days = sorted(p.stem for p in (data_dir / "oi" / args.root).glob("????-??-??.jsonl"))
    # Evenly-spaced sample across the whole span = automatic multi-regime coverage.
    step = max(1, (len(days) - 1) // max(1, args.sessions))
    picks = [(days[i], days[i + 1] if i + 1 < len(days) else None) for i in range(0, len(days) - 1, step)][:args.sessions]

    conn = sqlite3.connect(f"file:{data_dir / 'quotes.db'}?mode=ro", uri=True)
    results = []
    for day, nxt in picks:
        r = audit_session(conn, data_dir, args.root, day, nxt, args.mode)
        if r:
            results.append(r)
            print(f"{r['day']}  signable {r['signable']:6.1%}  (strict {r['strict_signable']:6.1%}, no-NBBO {r['no_nbbo']:5.1%})  contracts {r['contracts']:4d}"
                  + (f"  recon n={r['recon_n']} viol={r['recon_viol']} pure-open n={r['pure_open_n']} conf={r['pure_open_conf'] if r['pure_open_conf'] is None else round(r['pure_open_conf'], 3)}" if args.mode == "builds" and "recon_n" in r else ""))

    if not results:
        print("NO SESSIONS AUDITABLE — check store coverage")
        sys.exit(2)
    import statistics as st
    sig = [r["signable"] for r in results]
    print(f"\n=== {args.root} {args.mode}: {len(results)} sessions {results[0]['day']}..{results[-1]['day']} ===")
    print(f"premium-weighted signable: mean {st.mean(sig):.1%}  min {min(sig):.1%}  (strict-mid mean {st.mean(r['strict_signable'] for r in results):.1%})")
    if args.mode == "builds":
        allv = [v for r in results for v in r.get("vols", [])]
        alld = [d for r in results for d in r.get("dois", [])]
        viol = sum(r.get("recon_viol", 0) for r in results)
        n = sum(r.get("recon_n", 0) for r in results)
        if n and len(allv) > 2:
            mv, md = st.mean(allv), st.mean(alld)
            cov = sum((v - mv) * (d - md) for v, d in zip(allv, alld)) / (len(allv) - 1)
            corr = cov / (st.stdev(allv) * st.stdev(alld)) if st.stdev(allv) and st.stdev(alld) else float("nan")
            print(f"ΔOI reconciliation: n={n} contracts, |ΔOI|>vol violations={viol} ({viol / n:.2%}), corr(vol,|ΔOI|)={corr:.3f}")
        confs = [r["pure_open_conf"] for r in results if r.get("pure_open_conf") is not None]
        if confs:
            print(f"near-pure-open signing confidence |net|/signedvol: mean {st.mean(confs):.3f} over {sum(r.get('pure_open_n', 0) for r in results)} contracts")
    verdict = "PASS" if st.mean(sig) >= 0.70 and min(sig) >= 0.50 else "FAIL"
    print(f"AUDIT GATE ({args.mode}): {verdict}  (registered threshold: mean >= 70%)")
    sys.exit(0 if verdict == "PASS" else 1)


if __name__ == "__main__":
    main()
