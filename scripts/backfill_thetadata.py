#!/usr/bin/env python3
"""
Backfill option data (real OI + NBBO) from ThetaData into the canonical, source-
independent stores the C# backtest reads: data/oi/{TICKER}/{date}.jsonl (per-day chain
+ OI) and data/quotes/{TICKER}/{expiry}.csv (minute NBBO). Historical counterpart to the
live Schwab/Webull capture, which writes the SAME stores going forward — sources are
interchangeable (same format regardless of vendor).

Why this exists: massive.com gives option OHLCV but NO open interest and NO NBBO;
Schwab serves OI for equities but not for index options. ThetaData Value ($40/mo)
has both, for equities + index options, back ~4 years. See memory reference_thetadata_options.

Data source: the thetadata Python library (standalone/cloud, no Theta Terminal).
    pip install thetadata pandas            # Python 3.12+
Auth: a creds.txt (email on line 1, password on line 2) in the repo root, or pass
--creds /path/to/creds.txt, or set THETADATA_CREDENTIALS_FILE.

Endpoints (per docs):
  - option_history_eod(symbol, expiration="*", strike="*", start_date, end_date)
        -> OHLC + bid/ask (NBBO), one row per contract per day. NO open_interest.
  - option_history_open_interest(symbol, expiration="*", ...) -> open_interest.
We join the two on (expiration, strike, right, date).

Tickers carry their DTE on the CLI (no hardcoded ticker/DTE preference is committed):
pass NAME or NAME:DTE, e.g. `--tickers <EQUITY>:45 <INDEX>:0`. DTE without a `:N`
falls back to --max-dte.

USAGE — probe first to confirm the real DataFrame schema, then run:
    python3 scripts/backfill_thetadata.py --probe --ticker <TICKER>
    python3 scripts/backfill_thetadata.py --run --tickers <TICKER> [<TICKER> ...]
    python3 scripts/backfill_thetadata.py --validate --ticker <TICKER> --date YYYY-MM-DD

Writes the canonical data/oi (EOD --run) and data/quotes (--quotes) stores — the same
format the live capture and the backtest use, so a ThetaData backfill and a forward
Schwab/Webull capture are interchangeable in the same directory.
"""
import argparse
import json
import os
import sys
import threading
import zoneinfo
from datetime import date, datetime, timedelta
from pathlib import Path
import logging

NY = zoneinfo.ZoneInfo("America/New_York")
log = logging.getLogger("backfill")  # our own messages go here (not root), so we can quiet libraries


def _setup_logging():
    """Our messages (timestamped) to console + (when BF_LOG_FILE set) an append file. Third-party
    chatter (thetadata auth POSTs, grpc, urllib3) is suppressed unless BF_VERBOSE=1. Called at import so
    spawned children — which inherit BF_LOG_FILE/BF_VERBOSE via env — log to the SAME file at the same level."""
    verbose = os.environ.get("BF_VERBOSE") == "1"
    fmt = logging.Formatter("%(asctime)s %(message)s", "%Y-%m-%d %H:%M:%S")
    handlers = [logging.StreamHandler(sys.stdout)]
    path = os.environ.get("BF_LOG_FILE")
    if path:
        handlers.append(logging.FileHandler(path, mode="a", encoding="utf-8"))
    for h in handlers:
        h.setFormatter(fmt)
    log.handlers.clear()
    for h in handlers:
        log.addHandler(h)
    log.setLevel(logging.INFO)
    log.propagate = False  # don't double-emit via root
    root = logging.getLogger()
    if verbose:
        root.handlers.clear()
        for h in handlers:
            root.addHandler(h)
        root.setLevel(logging.DEBUG)  # show everything (auth, grpc, urllib3)
    else:
        root.setLevel(logging.WARNING)  # silence library INFO/DEBUG on console+file


_setup_logging()
# No default tickers/DTEs are committed — the caller supplies them on the CLI (NAME or NAME:DTE).
DEFAULT_START = date(2025, 1, 1)
DEFAULT_END = date.today()


def resolve_data_dir() -> Path:
    """Mirror of Program.ResolveBaseDir(): WA_DATA_DIR override, then
    $LOCALAPPDATA/WebullAnalytics/data, then scan /mnt/c/Users/*/AppData/Local."""
    override = os.environ.get("WA_DATA_DIR")
    if override:
        return Path(override)
    localappdata = os.environ.get("LOCALAPPDATA")
    if localappdata:
        p = Path(localappdata) / "WebullAnalytics" / "data"
        if p.exists():
            return p
    users_root = Path("/mnt/c/Users")
    if users_root.exists():
        for entry in users_root.iterdir():
            candidate = entry / "AppData" / "Local" / "WebullAnalytics" / "data"
            if candidate.exists():
                return candidate
    raise RuntimeError(
        "Cannot resolve data dir. Set WA_DATA_DIR, e.g. "
        "export WA_DATA_DIR=/mnt/c/Users/<you>/AppData/Local/WebullAnalytics/data"
    )


def make_client(creds: str | None):
    try:
        from thetadata import ThetaClient
    except ImportError:
        sys.exit("thetadata not installed. Run: pip install thetadata pandas  (Python 3.12+)")
    # Force pandas (library defaults to polars); pass creds file if given, else
    # the client looks for creds.txt / THETADATA_CREDENTIALS_FILE.
    if creds:
        return ThetaClient(creds_file=creds, dataframe_type="pandas")
    return ThetaClient(dataframe_type="pandas")


# ----- schema-tolerant column helpers (confirmed/adjusted via --probe) ---------

def pick_col(df, candidates: list[str]) -> str:
    for c in candidates:
        if c in df.columns:
            return c
    raise KeyError(f"none of {candidates} in columns {list(df.columns)}")


def norm_right(v) -> str:
    s = str(v).strip().upper()
    if s.startswith("C"):
        return "C"
    if s.startswith("P"):
        return "P"
    raise ValueError(f"unrecognized right value: {v!r}")


def norm_date(v) -> str:
    """Return YYYY-MM-DD from a date / datetime / 'YYYY-MM-DD...' / 'YYYYMMDD'."""
    if isinstance(v, (datetime, date)):
        return v.strftime("%Y-%m-%d")
    s = str(v)
    digits = s.replace("-", "").replace("/", "")[:8]
    if len(digits) == 8 and digits.isdigit():
        return f"{digits[:4]}-{digits[4:6]}-{digits[6:8]}"
    return s[:10]


def occ_symbol(root: str, expiration, right, strike) -> str:
    """Compact OCC: ROOT + YYMMDD + C/P + strike*1000 as 8 digits (e.g. XYZ260618C00750000)."""
    yymmdd = norm_date(expiration).replace("-", "")[2:]
    cp = norm_right(right)
    strike8 = f"{round(float(strike) * 1000):08d}"
    return f"{root}{yymmdd}{cp}{strike8}"


def et_timestamps(d: str) -> tuple[str, str]:
    """(tsUtc, tsEt) for an EOD snapshot stamped at 16:00 ET on date d (YYYY-MM-DD)."""
    y, m, day = (int(x) for x in d.split("-"))
    et = datetime(y, m, day, 16, 0, 0, tzinfo=NY)
    ts_et = et.strftime("%Y-%m-%dT%H:%M:%S%z")
    ts_et = ts_et[:-2] + ":" + ts_et[-2:]  # insert ':' in the offset (+0000 -> +00:00)
    utc = et.astimezone(zoneinfo.ZoneInfo("UTC"))
    ts_utc = utc.strftime("%Y-%m-%dT%H:%M:%S.%f0Z")
    return ts_utc, ts_et


def month_chunks(start: date, end: date):
    """Yield (chunk_start, chunk_end) covering [start, end] in <=1-month windows
    (safe even where the API allows longer; keeps memory/requests bounded)."""
    cur = start
    while cur <= end:
        nxt = (cur.replace(day=1) + timedelta(days=32)).replace(day=1)
        chunk_end = min(end, nxt - timedelta(days=1))
        yield cur, chunk_end
        cur = chunk_end + timedelta(days=1)


def coerce(val):
    """JSON-safe scalar: NaN/None -> None, numpy -> python."""
    if val is None:
        return None
    try:
        if val != val:  # NaN (python or numpy)
            return None
    except Exception:
        pass
    if hasattr(val, "item"):
        return val.item()
    return val


# ----- Black-Scholes implied vol (back-solved, since IV/greeks need Standard tier) ---
# Matches the backtest's own approach (OptionMath.ImpliedVol back-solves from price).
# Plain European BS, no dividend — fine for the gamma weighting the OI cache uses;
# DTE<=0 / price<=intrinsic come back NaN (those contracts then drop out of the cache).

def _norm_cdf(x):
    import numpy as np
    ax = np.abs(x) / np.sqrt(2.0)
    t = 1.0 / (1.0 + 0.3275911 * ax)
    poly = (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t
    erf = np.sign(x) * (1.0 - poly * np.exp(-ax * ax))
    return 0.5 * (1.0 + erf)


def _bs_price(S, K, T, r, sigma, cp):
    import numpy as np
    sig = np.maximum(sigma, 1e-9)
    tt = np.maximum(T, 1e-9)
    sqrt_t = np.sqrt(tt)
    d1 = (np.log(S / K) + (r + 0.5 * sig * sig) * tt) / (sig * sqrt_t)
    d2 = d1 - sig * sqrt_t
    call = S * _norm_cdf(d1) - K * np.exp(-r * tt) * _norm_cdf(d2)
    return np.where(cp > 0, call, call - S + K * np.exp(-r * tt))  # put via parity


def back_solve_iv(price, S, K, T, r, cp):
    """Vectorized bisection IV in [1e-4, 5]. NaN where T<=0, S/K invalid, or price<=intrinsic."""
    import numpy as np
    price, S, K, T, cp = (np.asarray(a, float) for a in (price, S, K, T, cp))
    intrinsic = np.where(cp > 0, np.maximum(S - K, 0.0), np.maximum(K - S, 0.0))
    valid = np.isfinite(price) & np.isfinite(S) & (T > 0) & (S > 0) & (K > 0) & (price > intrinsic + 1e-6)
    lo = np.full(price.shape, 1e-4)
    hi = np.full(price.shape, 5.0)
    Ssafe = np.where(valid, S, 1.0)  # keep BS finite on masked entries
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        pr = _bs_price(Ssafe, K, T, r, mid, cp)
        too_high = pr > price
        hi = np.where(too_high, mid, hi)
        lo = np.where(too_high, lo, mid)
    return np.where(valid, 0.5 * (lo + hi), np.nan)


# ----- probe -------------------------------------------------------------------

def probe(client, ticker: str):
    """Pull a tiny window and print the real schema so we confirm column names
    before trusting the full pull."""
    start = DEFAULT_END - timedelta(days=4)
    log.info(f"=== PROBE {ticker}  {start} .. {DEFAULT_END} ===")
    eod = thetacall(client.option_history_eod, symbol=ticker, expiration="*", strike="*",
                    start_date=start, end_date=DEFAULT_END)
    log.info(f"\n[EOD] shape={eod.shape}\ncolumns={list(eod.columns)}")
    log.info(eod.head(3).to_string())
    oi = thetacall(client.option_history_open_interest, symbol=ticker, expiration="*", strike="*",
                   start_date=start, end_date=DEFAULT_END)
    log.info(f"\n[OI] shape={oi.shape}\ncolumns={list(oi.columns)}")
    log.info(oi.head(3).to_string())
    log.info("\n(IV/greeks endpoints need the Standard tier; on Value we back-solve IV "
          "from the EOD mid via Black-Scholes — see back_solve_iv.)")


# column-name candidates (confirmed via --probe 2026-06-06).
# EOD has no plain date column: `created` is the per-row EOD-finalization timestamp
# (~17:15 ET on the trading day), so its date IS the trading date.
EOD_DATE = ["created", "date", "quote_date", "bar_date", "timestamp"]
EOD_EXP = ["expiration", "exp"]
EOD_STRIKE = ["strike"]
EOD_RIGHT = ["right", "type"]
EOD_BID = ["bid"]
EOD_ASK = ["ask"]
EOD_CLOSE = ["close", "last"]
EOD_VOLUME = ["volume"]
OI_DATE = ["timestamp", "date"]
OI_EXP = ["expiration", "exp"]
OI_STRIKE = ["strike"]
OI_RIGHT = ["right", "type"]
OI_OI = ["open_interest", "oi"]


_TRANSIENT = ("UNAVAILABLE", "http2 header with status: 5", "RST_STREAM", "Deadline",
              "Internal", "502", "503", "504", "GOAWAY",
              "ConnectTimeout", "ReadTimeout", "ConnectionError", "timed out",
              "Connection reset", "Connection refused")


def thetacall(fn, *args, **kwargs):
    """Call a ThetaData endpoint, retrying transient gRPC errors (502/UNAVAILABLE/...) with
    backoff. Returns None on NoDataFoundError (a legitimate empty result for that query — NOT an
    error; the caller's empty-check skips it). Non-transient errors propagate immediately;
    persistent transient errors re-raise after the retries (the watchdog then handles them)."""
    import time
    last = None
    for attempt in range(5):
        try:
            return fn(*args, **kwargs)
        except Exception as e:  # noqa
            name = type(e).__name__
            if name == "NoDataFoundError" or "No data found" in str(e):
                return None  # query legitimately empty (e.g. contract not quoted in this window)
            blob = f"{name}: {e}"  # match on the exception type name too (e.g. ConnectTimeout)
            if not any(t in blob for t in _TRANSIENT):
                raise
            last = e
            time.sleep(2.0 * (attempt + 1))  # 2,4,6,8s
    raise last


def fetch_underlying_closes(client, ticker: str, start: date, end: date) -> dict:
    """Best-effort daily underlying close for the IV back-solve. Tries the equity EOD feed first; on no
    data falls back to the index EOD feed (index options settle on the index — a trailing 'W' weekly root
    maps to its index, e.g. a weekly index root -> that index). Generic so it works for any equity OR
    index ticker without naming one. Returns {date_str: price}; empty on failure (underlyingPrice=null)."""
    def _closes(df):
        dc = pick_col(df, ["date", "created", "timestamp"])
        cc = pick_col(df, ["close", "price", "last"])
        return {norm_date(r[dc]): coerce(r[cc]) for _, r in df.iterrows()}
    try:
        df = thetacall(client.stock_history_eod, symbol=ticker, start_date=start, end_date=end)
        if df is not None and not df.empty:
            return _closes(df)
    except Exception:
        pass  # not an equity (or the equity feed errored) — try the index feed below
    try:
        idx = ticker[:-1] if ticker.endswith("W") else ticker
        df = thetacall(client.index_history_eod, symbol=idx, start_date=start, end_date=end)
        if df is not None and not df.empty:
            return _closes(df)
    except Exception as e:  # method name / entitlement uncertain — degrade gracefully
        log.info(f"  [warn] underlying close fetch failed for {ticker}: {e}; underlyingPrice=null")
    return {}


def existing_max_date(tdir: Path) -> str | None:
    """Latest YYYY-MM-DD day-file already written for a ticker (ISO strings sort lexically)."""
    days = [p.stem for p in tdir.glob("*.jsonl")]
    return max(days) if days else None


def process_one_chunk(client, ticker, cs, ce, max_dte, rate, out_root):
    """Pull + write one (ticker, month) chunk of daily EOD records. Returns day-file count.
    Self-contained (no shared state) so it can run inside a watchdog child process."""
    import numpy as np
    import pandas as pd
    tdir = out_root / ticker
    tdir.mkdir(parents=True, exist_ok=True)
    eod = thetacall(client.option_history_eod, symbol=ticker, expiration="*", strike="*",
                                    start_date=cs, end_date=ce, max_dte=max_dte)
    oi = thetacall(client.option_history_open_interest, symbol=ticker, expiration="*", strike="*",
                                             start_date=cs, end_date=ce, max_dte=max_dte)
    if eod is None or len(eod) == 0:
        log.info("    (no EOD rows)")
        return 0

    e_date, e_exp, e_strike, e_right = (pick_col(eod, EOD_DATE), pick_col(eod, EOD_EXP),
                                        pick_col(eod, EOD_STRIKE), pick_col(eod, EOD_RIGHT))
    e_bid, e_ask, e_close, e_vol = (pick_col(eod, EOD_BID), pick_col(eod, EOD_ASK),
                                    pick_col(eod, EOD_CLOSE), pick_col(eod, EOD_VOLUME))
    oi_map = {}
    if oi is not None and len(oi):
        o_date, o_exp, o_strike, o_right, o_oi = (pick_col(oi, OI_DATE), pick_col(oi, OI_EXP),
                                                  pick_col(oi, OI_STRIKE), pick_col(oi, OI_RIGHT),
                                                  pick_col(oi, OI_OI))
        for _, r in oi.iterrows():
            key = (norm_date(r[o_date]), norm_date(r[o_exp]), float(r[o_strike]), norm_right(r[o_right]))
            oi_map[key] = coerce(r[o_oi])

    unders = fetch_underlying_closes(client, ticker, cs, ce)

    # Back-solve IV from the EOD mid (IV/greeks endpoints need Standard tier; on Value we
    # derive it ourselves). REQUIRED: ChainSnapshotOiCache drops any contract with iv <= 0.
    dts = eod[e_date].map(norm_date)
    exp_s = eod[e_exp].map(norm_date)
    T = np.array([(date.fromisoformat(e) - date.fromisoformat(d)).days / 365.0
                  for e, d in zip(exp_s, dts)], dtype=float)
    S = pd.to_numeric(dts.map(unders.get), errors="coerce").to_numpy(dtype=float)
    K = pd.to_numeric(eod[e_strike], errors="coerce").to_numpy(dtype=float)
    bid = pd.to_numeric(eod[e_bid], errors="coerce")
    ask = pd.to_numeric(eod[e_ask], errors="coerce")
    close = pd.to_numeric(eod[e_close], errors="coerce")
    mid = np.where((bid > 0) & (ask > 0), (bid + ask) / 2.0, close)
    cp = eod[e_right].map(lambda x: 1.0 if norm_right(x) == "C" else -1.0).to_numpy(dtype=float)
    eod["_iv"] = back_solve_iv(mid, S, K, T, rate, cp)
    eod["_date"] = dts
    iv_ok = int(np.isfinite(eod["_iv"]).sum())
    log.info(f"    contracts={len(eod)} iv_solved={iv_ok} oi_rows={len(oi_map)} spot_days={len(unders)}")
    if iv_ok == 0:
        log.info("    [WARN] no IV solved (missing spot?) — contracts will be DROPPED by ChainSnapshotOiCache.")

    by_day = {}
    for _, r in eod.iterrows():
        d = r["_date"]
        key = (d, norm_date(r[e_exp]), float(r[e_strike]), norm_right(r[e_right]))
        by_day.setdefault(d, []).append((r, key))

    for d, rows in sorted(by_day.items()):
        ts_utc, ts_et = et_timestamps(d)
        options = [{
            "symbol": occ_symbol(ticker, key[1], key[3], key[2]),
            "bid": coerce(r[e_bid]), "ask": coerce(r[e_ask]), "last": coerce(r[e_close]),
            "volume": coerce(r[e_vol]), "openInterest": oi_map.get(key),
            "iv": coerce(r["_iv"]), "hv": None, "iv5": None,
        } for r, key in rows]
        record = {"tsUtc": ts_utc, "tsEt": ts_et, "ticker": ticker,
                  "underlyingPrice": unders.get(d), "options": options}
        with (tdir / f"{d}.jsonl").open("w") as f:  # one EOD record/day; overwrite (idempotent)
            f.write(json.dumps(record) + "\n")
    log.info(f"    wrote {len(by_day)} day file(s)")
    return len(by_day)


def _eod_chunk_worker(result_q, creds, ticker, cs, ce, max_dte, rate, out_root_str):
    """Child-process entry: build own client, pull+write one chunk, report via the queue."""
    try:
        client = make_client(creds)
        n = process_one_chunk(client, ticker, cs, ce, max_dte, rate, Path(out_root_str))
        result_q.put(("ok", n))
    except BaseException as e:  # report any failure (incl. SystemExit) to the parent
        result_q.put(("err", f"{type(e).__name__}: {e}"))


def run_resilient(target, args, label, timeout_s, retries):
    """Run target(result_q, *args) in a spawned child with a timeout; kill+retry on hang/error.
    Returns the worker payload on success, else None (rerun with --resume to retry the unit)."""
    import multiprocessing as mp
    ctx = mp.get_context("spawn")
    for attempt in range(1, retries + 2):
        q = ctx.Queue()
        p = ctx.Process(target=target, args=(q,) + tuple(args))
        p.start()
        p.join(timeout_s)
        if p.is_alive():
            p.terminate()
            p.join()
            log.info(f"    [timeout >{timeout_s}s] {label} attempt {attempt}/{retries + 1} — killed, retrying")
            continue
        try:
            status, payload = q.get_nowait()
        except Exception:
            status, payload = ("err", f"worker exited code={p.exitcode}, no result")
        if status == "ok":
            return payload
        log.info(f"    [error] {label} attempt {attempt}/{retries + 1}: {payload} — retrying")
    log.info(f"    [GIVE UP] {label} after {retries + 1} attempts — rerun with --resume to retry")
    return None


def run(tickers, start: date, end: date, out_root: Path, max_dte, rate, resume, creds, timeout_s, retries):
    # Each (ticker, month) chunk runs in a watchdog child process: a hung gRPC call is killed
    # and retried instead of stalling forever. max_dte=None = full chain (huge); a finite cap
    # bounds it to the near-dated chain the strategy + GEX/max-pain use.
    for ticker in tickers:
        tdir = out_root / ticker
        tdir.mkdir(parents=True, exist_ok=True)
        done_through = existing_max_date(tdir) if resume else None
        log.info(f"\n=== {ticker} -> {tdir}  (max_dte={max_dte}{', resume>' + done_through if done_through else ''}) ===")
        for cs, ce in month_chunks(start, end):
            if done_through and ce.isoformat() <= done_through:
                log.info(f"  chunk {cs} .. {ce}  [skip — already have through {done_through}]")
                continue
            log.info(f"  chunk {cs} .. {ce}  [{datetime.now():%H:%M:%S}]")
            run_resilient(_eod_chunk_worker, (creds, ticker, cs, ce, max_dte, rate, str(out_root)),
                          f"{ticker} {cs}..{ce}", timeout_s, retries)


# ===== minute NBBO quote pull (option_history_quote) ==========================
# The quote endpoint requires a SINGLE expiration per multi-day request, so the unit
# of work is one expiration (its whole DTE window), written to one file per expiration.
# Resume is driven by the per-ticker sealed.json manifest (see below), not file presence.
# Per-ticker DTE comes from the CLI (NAME:DTE or --max-dte) — never hardcoded here.
QUOTE_DATE = ["date", "quote_date", "created", "timestamp"]
QUOTE_TIME = ["ms_of_day", "time", "timestamp", "datetime"]


def first_present(df, names):
    for n in names:
        if n in df.columns:
            return n
    return None


def norm_minute(v):
    """Normalize a quote row's time to HH:MM:SS. Handles ms-of-day ints, datetimes, ISO strings."""
    try:
        if isinstance(v, (int, float)) and v == v:
            s = int(v) // 1000
            return f"{s // 3600:02d}:{(s % 3600) // 60:02d}:{s % 60:02d}"
    except Exception:
        pass
    if isinstance(v, datetime):
        return v.strftime("%H:%M:%S")
    s = str(v)
    if "T" in s or " " in s:
        return s.replace("T", " ").split(" ")[-1][:8]
    return s[:8]


def _expirations_from(obj):
    if hasattr(obj, "columns"):
        col = first_present(obj, ["expiration", "date", "expirations"]) or obj.columns[0]
        return [norm_date(v) for v in obj[col].tolist()]
    return [norm_date(v) for v in obj]


def windows(start: date, end: date, days: int):
    """Yield <=`days`-wide (calendar) sub-windows of [start, end]. Smaller windows make each minute-quote
    request small enough that ThetaData rarely stalls on it (large multi-day requests intermittently hang),
    and keep a true stall cheap to kill+retry. days>=1."""
    cur = start
    step = max(1, days)
    while cur <= end:
        w_end = min(end, cur + timedelta(days=step - 1))
        yield cur, w_end
        cur = w_end + timedelta(days=1)


def process_one_expiration(client, ticker, exp, dte, rate, out_root, gstart, gend, chunk_days):
    """Pull minute NBBO for one expiration across its DTE window (clamped to [gstart,gend]),
    keep ±10% strikes, write out_root/<ticker>/<exp>.csv. Returns row count."""
    import numpy as np  # noqa
    import pandas as pd
    exp_d = date.fromisoformat(exp) if isinstance(exp, str) else exp
    start_d = max(gstart, exp_d - timedelta(days=dte))
    end_d = min(gend, exp_d)
    if start_d > end_d:
        return 0

    unders = fetch_underlying_closes(client, ticker, start_d, end_d)
    parts = []
    saw_quotes = False
    for ms, me in windows(start_d, end_d, chunk_days):  # small windows: server stalls on large minute requests
        df = thetacall(client.option_history_quote, symbol=ticker, expiration=exp_d, interval="1m",
                       strike="*", start_date=ms, end_date=me)
        if df is None or len(df) == 0:
            continue
        saw_quotes = True
        if not unders:
            continue   # have quotes but no spot to band-filter -> resolved after the loop
        qd, qt = pick_col(df, QUOTE_DATE), pick_col(df, QUOTE_TIME)   # both = 'timestamp'
        qk, qr = pick_col(df, EOD_STRIKE), pick_col(df, EOD_RIGHT)
        qb, qa = pick_col(df, EOD_BID), pick_col(df, EOD_ASK)
        qbs, qas = first_present(df, ["bid_size"]), first_present(df, ["ask_size"])
        # Normalize ThetaData's END-of-bar minute stamp to our START-of-bar 09:30 convention by subtracting
        # 60s AT INGEST -- the same normalization WebullChartsClient.WebullBarShift applies for Webull
        # (09:31->09:30 first RTH, 16:00->15:59 last RTH; the 09:30 auction row -> 09:29). The store is then
        # start-of-bar on disk and QuoteStoreCache never shifts on read. Derive date from the shifted stamp
        # too (consistent; the -60s never crosses a day for RTH times).
        ts = pd.to_datetime(df[qt]) - pd.Timedelta(seconds=60)
        dts = ts.map(norm_date)
        spot = pd.to_numeric(dts.map(unders.get), errors="coerce")
        strike = pd.to_numeric(df[qk], errors="coerce")
        bidv = pd.to_numeric(df[qb], errors="coerce")
        askv = pd.to_numeric(df[qa], errors="coerce")
        keep = (spot.notna() & strike.notna()
                & ((strike / spot - 1.0).abs() <= 0.10)   # ±10% band
                & (bidv.notna() | askv.notna()))          # keep quoted minutes (0/0 auction stays; dropped on read)
        if not keep.any():
            continue
        parts.append(pd.DataFrame({
            "date": dts[keep],
            "time": ts[keep].map(norm_minute),
            "strike": strike[keep],
            "right": df.loc[keep, qr].map(norm_right),
            "bid": bidv[keep],
            "ask": askv[keep],
            "bid_size": df.loc[keep, qbs] if qbs else 0,
            "ask_size": df.loc[keep, qas] if qas else 0,
        }))
    # Quotes present but no underlying spot -> can't band-filter; treat as transient (raise -> retry).
    # No quotes AT ALL -> a non-trading day (e.g. the 2025-01-09 market closure) or an expiration with no
    # in-band contracts: return empty so it SEALS instead of being re-attempted forever as a "feed error".
    if saw_quotes and not unders:
        raise RuntimeError(f"quotes present but no underlying spot for {ticker} {start_d}..{end_d} (transient underlying feed?)")
    if not parts:
        return 0
    res = pd.concat(parts, ignore_index=True)
    tdir = out_root / ticker
    tdir.mkdir(parents=True, exist_ok=True)
    # Atomic write: a kill mid-write leaves only the .tmp, never a partial final file that
    # --resume would mistake for complete (size>0). os.replace is atomic on the same filesystem.
    final = tdir / f"{exp_d.isoformat()}.csv"
    tmp = tdir / f"{exp_d.isoformat()}.csv.tmp"
    res.to_csv(tmp, index=False)
    os.replace(tmp, final)
    log.info(f"    exp {exp_d} rows={len(res)} days={res['date'].nunique()} spot_days={len(unders)}")
    return len(res)


def _quote_expiry_worker(result_q, creds, ticker, exp, dte, rate, out_root_str, gstart_iso, gend_iso, chunk_days):
    try:
        client = make_client(creds)
        n = process_one_expiration(client, ticker, exp, dte, rate, Path(out_root_str),
                                   date.fromisoformat(gstart_iso), date.fromisoformat(gend_iso), chunk_days)
        result_q.put(("ok", n))
    except BaseException as e:
        result_q.put(("err", f"{type(e).__name__}: {e}"))


# ----- sealed-expiration manifest (mirrors wa-history's intraday sealed.json) --------------------------
# data/quotes/{ticker}/sealed.json = {"sealed": ["YYYY-MM-DD", ...]} lists expirations
# whose quote file is FINAL: the expiration has ELAPSED (exp < today, so no more live quotes can accrue)
# AND its whole window was in range (exp <= end, so the file wasn't truncated by `end`). Sealed
# expirations are trusted unconditionally and skipped on every later run; an UNSEALED file — a
# still-alive or future expiration near `end` — is re-pulled each run so the live frontier keeps filling
# in as days elapse. That is exactly what lets a "backfill near today" keep current contracts: future
# expirations ARE pulled (we need them for recent days' chains), just never sealed until they expire.
_SEAL_LOCK = threading.Lock()  # the threaded pull seals from worker threads → serialize manifest writes


def load_sealed(tdir: Path) -> set:
    p = tdir / "sealed.json"
    if not p.exists():
        return set()
    try:
        return set(json.loads(p.read_text()).get("sealed", []))
    except Exception:
        return set()  # unreadable manifest → treat all as unsealed (re-pull; never silently lose coverage)


def save_sealed(tdir: Path, sealed: set):
    p = tdir / "sealed.json"
    tmp = tdir / "sealed.json.tmp"
    tmp.write_text(json.dumps({"sealed": sorted(sealed)}, indent=2))
    os.replace(tmp, p)  # atomic


def should_seal(exp_iso: str, end_date: date) -> bool:
    """Seal only when the expiration is FINAL: elapsed (exp < today → no more live quotes) AND fully in
    range (exp <= end → the window wasn't cut short by `end`)."""
    e = date.fromisoformat(exp_iso)
    return e < date.today() and e <= end_date


def _seal_if_final(tdir: Path, exp_iso: str, end_date: date, sealed: set):
    """After a SUCCESSFUL pull of one expiration, add it to the manifest if it's final. Thread-safe."""
    if not should_seal(exp_iso, end_date):
        return
    with _SEAL_LOCK:
        if exp_iso not in sealed:
            sealed.add(exp_iso)
            save_sealed(tdir, sealed)


def _build_quote_work_with(client, tickers, start, end, out_root, dte_map):
    """List expirations per ticker (via the given client) and return [(ticker, exp, dte)] for those not
    yet SEALED. Unsealed expirations — including still-alive/future ones near `end` — are (re)pulled so
    the live frontier fills in as days elapse; sealed (final) ones are skipped."""
    work = []
    for ticker in tickers:
        dte = dte_map.get(ticker)
        if dte is None:
            log.info(f"  [error] no DTE for {ticker} (use {ticker}:DTE or --max-dte) — skipping ticker")
            continue
        tdir = out_root / ticker
        tdir.mkdir(parents=True, exist_ok=True)
        sealed = load_sealed(tdir)
        try:
            exps = _expirations_from(thetacall(client.option_list_expirations, ticker))
        except Exception as e:
            log.info(f"  [error] list expirations {ticker}: {e} — skipping ticker")
            continue
        hi = (end + timedelta(days=dte)).isoformat()
        rel = sorted(e for e in exps if start.isoformat() <= e <= hi)
        todo = [(ticker, e, dte) for e in rel if e not in sealed]
        log.info(f"=== {ticker} (dte={dte}): {len(rel)} expirations, {len(sealed)} sealed, {len(todo)} to pull ===")
        work.extend(todo)
    return work


def _threaded_quote_worker(creds, tickers, start_iso, end_iso, dte_map, rate, out_root_str, chunk_days, concurrency):
    """One worker PROCESS = ONE ThetaData session. `concurrency` clients share that session via
    existing_authorized_client (the lib's documented multi-client pattern), one per worker thread, so
    `concurrency` requests are in flight at once — the tier's real allowance. One pass over the unsealed
    expirations; the supervisor restarts us if a hung gRPC call wedges the process."""
    import concurrent.futures as cf
    import queue
    from thetadata import ThetaClient
    out_root = Path(out_root_str)
    start, end = date.fromisoformat(start_iso), date.fromisoformat(end_iso)
    base = make_client(creds)
    # Build work first: the listing calls force `base` to AUTHORIZE its session before we derive the
    # shared clients — otherwise each shared client could open its own session (→ Invalid session ID).
    work = _build_quote_work_with(base, tickers, start, end, out_root, dte_map)
    if not work:
        return
    sealed_by_ticker = {t: load_sealed(out_root / t) for t in tickers}  # seal final expirations as we finish them
    clients = [base] + [ThetaClient(existing_authorized_client=base, dataframe_type="pandas")
                        for _ in range(max(0, concurrency - 1))]
    pool = queue.Queue()
    for c in clients:
        pool.put(c)
    log.info(f"--- threaded pull: {len(work)} expirations, {concurrency} concurrent on one session ---")

    def do_one(item):
        ticker, e, dte = item
        c = pool.get()
        try:
            log.info(f"  exp {ticker} {e}")
            process_one_expiration(c, ticker, e, dte, rate, out_root, start, end, chunk_days)
            _seal_if_final(out_root / ticker, e, end, sealed_by_ticker[ticker])
        except BaseException as ex:  # one failed expiration shouldn't stop the pass; a later run retries it (not sealed)
            log.info(f"  [error] {ticker} exp {e}: {type(ex).__name__}: {ex}")
        finally:
            pool.put(c)

    with cf.ThreadPoolExecutor(max_workers=concurrency) as ex:
        list(ex.map(do_one, work))


def _run_quotes_threaded(tickers, start, end, out_root, dte_map, rate, creds, chunk_days, concurrency, stall_secs):
    """Supervisor: runs the threaded worker in a child process and restarts it if it STALLS (a hung
    gRPC call wedges a thread, holding a server slot until the process dies — restart clears it). The
    supervisor makes NO ThetaData calls (no second session). Stops after 3 cycles with no new files
    (remaining expirations have no data, or ThetaData is down → re-run --resume later)."""
    import multiprocessing as mp
    import time
    ctx = mp.get_context("spawn")

    def count_csv():
        return sum(1 for t in tickers if (out_root / t).exists() for _ in (out_root / t).glob("*.csv"))

    no_progress = 0
    while True:
        before = count_csv()
        p = ctx.Process(target=_threaded_quote_worker,
                        args=(creds, list(tickers), start.isoformat(), end.isoformat(),
                              dte_map, rate, str(out_root), chunk_days, concurrency))
        p.start()
        last_count, last_t = before, time.time()
        while p.is_alive():
            time.sleep(10)
            c = count_csv()
            if c > last_count:
                last_count, last_t = c, time.time()
            elif time.time() - last_t > stall_secs:
                log.info(f"[stall] no new file in {stall_secs}s — restarting worker (clears the wedged session)")
                p.terminate(); p.join(); break
        else:
            p.join()
        after = count_csv()
        if after > before:
            no_progress = 0
            log.info(f"[supervisor] cycle done, {after} files total — checking for remaining work")
        else:
            no_progress += 1
            if no_progress >= 3:
                log.info("[stop] 3 cycles with no new files — remaining expirations have no data, or ThetaData "
                         "is unavailable. Re-run --quotes (resume is implicit) later to retry.")
                return


def run_quotes(tickers, start, end, out_root, dte_map, rate, creds, timeout_s, retries, chunk_days, concurrency):
    # Resume is seal-driven: sealed (final) expirations are skipped, unsealed ones (re)pulled every run.
    if concurrency > 1:
        _run_quotes_threaded(tickers, start, end, out_root, dte_map, rate, creds,
                             chunk_days, concurrency, stall_secs=max(timeout_s, 300))
        return
    # Sequential: one expiration at a time via the run_resilient process watchdog (clean hang-kill).
    listing = make_client(creds)
    for ticker in tickers:
        dte = dte_map.get(ticker)
        if dte is None:
            log.info(f"  [error] no DTE for {ticker} (use {ticker}:DTE or --max-dte) — skipping ticker")
            continue
        tdir = out_root / ticker
        tdir.mkdir(parents=True, exist_ok=True)
        sealed = load_sealed(tdir)
        try:
            exps = _expirations_from(thetacall(listing.option_list_expirations, ticker))
        except Exception as e:
            log.info(f"  [error] list expirations {ticker}: {e} — skipping ticker")
            continue
        hi = (end + timedelta(days=dte)).isoformat()
        rel = sorted(e for e in exps if start.isoformat() <= e <= hi)
        todo = [e for e in rel if e not in sealed]
        log.info(f"=== {ticker} (dte={dte}) -> {tdir}: {len(rel)} expirations, {len(sealed)} sealed, {len(todo)} to pull ===")
        for e in todo:
            log.info(f"  exp {e}")
            ok = run_resilient(_quote_expiry_worker,
                               (creds, ticker, e, dte, rate, str(out_root), start.isoformat(), end.isoformat(), chunk_days),
                               f"{ticker} exp {e}", timeout_s, retries)
            if ok is not None:  # success (row count, possibly 0) → seal if final; give-up (None) leaves it unsealed
                _seal_if_final(tdir, e, end, sealed)


def quote_probe(client, ticker: str):
    """Pull one near expiration's minute quotes for a few days; print the real schema so we
    confirm the time/column names before the full pull."""
    exps = sorted(_expirations_from(thetacall(client.option_list_expirations, ticker)))
    today = date.today().isoformat()
    past = [e for e in exps if e < today]
    exp = past[-1] if past else exps[0]           # a recent EXPIRED contract → real data
    exp_d = date.fromisoformat(exp)
    log.info(f"=== QUOTE PROBE {ticker} exp={exp} (1m, last ~2 days) ===")
    df = thetacall(client.option_history_quote, symbol=ticker, expiration=exp_d, interval="1m",
                                     strike="*", start_date=exp_d - timedelta(days=2), end_date=exp_d)
    log.info(f"shape={df.shape}\ncolumns={list(df.columns)}")
    log.info(df.head(4).to_string())
    log.info("\nConfirm the time column (QUOTE_TIME candidates: ms_of_day/time/timestamp) and its format.")


def _tail_line(path: Path, nbytes: int = 8192) -> str:
    """Last non-empty line, read from the file's tail (O(1) — no full scan; files are tens of MB)."""
    with open(path, "rb") as f:
        f.seek(0, 2)
        f.seek(max(0, f.tell() - nbytes))
        chunk = f.read()
    parts = [ln for ln in chunk.split(b"\n") if ln.strip()]
    return parts[-1].decode("utf-8", "ignore") if parts else ""


def verify_quotes(out_root: Path, tickers, end: date):
    """Integrity scan of the quote store: per ticker, flag expiration CSVs that are empty, unparseable,
    or have a truncated last line (the detectable signs of a partial/interrupted write), plus stray .tmp
    files. Seek-based (header + first data row + last line only) — O(1) per file, so it stays fast on the
    multi-million-row mature expirations. Does NOT detect a clean-but-short file — but the atomic write
    (os.replace) prevents partial final files. Delete flagged files and re-run --quotes.
    Also RECONCILES sealed.json: any clean file whose expiration is final (elapsed & <= end) but not
    yet sealed is added to the manifest — so files pulled before sealing existed become authoritative
    (mirrors wa-history's intraday audit), and a later run won't needlessly re-pull them."""
    for ticker in tickers:
        tdir = out_root / ticker
        files = sorted(tdir.glob("*.csv")) if tdir.exists() else []
        tmps = sorted(tdir.glob("*.csv.tmp")) if tdir.exists() else []
        log.info(f"\n=== {ticker}: {len(files)} expiration files, {len(tmps)} stray .tmp ===")
        sealed = load_sealed(tdir)
        newly_sealed = 0
        flagged = []
        for f in files:
            try:
                with open(f, "rb") as fh:
                    ncol = fh.readline().count(b",") + 1   # header
                    has_data = bool(fh.readline().strip())  # first data row
                if not has_data:
                    flagged.append((f.name, "ZERO ROWS"))
                elif _tail_line(f).count(",") + 1 != ncol:
                    flagged.append((f.name, f"TRUNCATED last line ({_tail_line(f).count(',') + 1} of {ncol} cols)"))
                elif f.stem not in sealed and should_seal(f.stem, end):  # clean + final → seal
                    sealed.add(f.stem)
                    newly_sealed += 1
            except Exception as e:
                flagged.append((f.name, f"UNPARSEABLE: {e}"))
        if newly_sealed:
            save_sealed(tdir, sealed)
            log.info(f"  sealed {newly_sealed} newly-final expirations ({len(sealed)} total in sealed.json)")
        if flagged:
            log.info("  FLAGGED (delete these, then re-run --quotes):")
            for name, why in flagged:
                log.info(f"    {name}: {why}")
        else:
            log.info(f"  OK — {len(files)} files parse, last lines complete")
        if tmps:
            log.info(f"  stray .tmp (interrupted writes — safe to delete): {[t.name for t in tmps]}")


def validate(ticker: str, d: str, out_root: Path, data_dir: Path):
    """Compare backfilled OI vs an existing Webull/Schwab live snapshot for the same
    date, on contracts present in both. Reports match rate and worst diffs."""
    bf = out_root / ticker / f"{d}.jsonl"
    live = data_dir / "chain-snapshots" / ticker / f"{d}.jsonl"
    if not bf.exists():
        sys.exit(f"no backfill file {bf}")
    if not live.exists():
        sys.exit(f"no live snapshot {live} to compare against")
    bf_oi = {o["symbol"]: o["openInterest"] for o in json.loads(bf.read_text().splitlines()[0])["options"]}
    # live file is intraday (many lines); take the first snapshot of the day
    live_rec = json.loads(live.read_text().splitlines()[0])
    live_oi = {o["symbol"]: o.get("openInterest") for o in live_rec["options"]}
    common = [s for s in bf_oi if s in live_oi and bf_oi[s] is not None and live_oi[s] is not None]
    if not common:
        log.info("no overlapping contracts with non-null OI"); return
    exact = sum(1 for s in common if bf_oi[s] == live_oi[s])
    diffs = sorted(((abs((bf_oi[s] or 0) - (live_oi[s] or 0)), s, live_oi[s], bf_oi[s]) for s in common), reverse=True)
    log.info(f"{ticker} {d}: matched={len(common)} exact={exact} ({100*exact/len(common):.1f}%)")
    log.info("worst diffs (live vs backfill):")
    for delta, s, lv, bv in diffs[:8]:
        log.info(f"  {s}: live={lv} backfill={bv} Δ={bv-lv:+}")


def parse_ticker_specs(tokens, global_max_dte):
    """Parse CLI ticker tokens 'NAME' or 'NAME:DTE' into (names, dte_map). A token's ':DTE' wins; else the
    --max-dte fallback (if given). Tickers needing a DTE (--quotes) with neither are rejected by the caller."""
    names, dte_map = [], {}
    for tok in tokens:
        name, sep, d = tok.partition(":")
        name = name.upper()
        names.append(name)
        if sep:
            dte_map[name] = int(d)
        elif global_max_dte is not None:
            dte_map[name] = global_max_dte
    return names, dte_map


def main():
    ap = argparse.ArgumentParser(description="ThetaData -> chain-snapshots backfill")
    ap.add_argument("--probe", action="store_true", help="print EOD/OI DataFrame schema for one ticker")
    ap.add_argument("--run", action="store_true", help="run the daily EOD backfill")
    ap.add_argument("--quotes", action="store_true", help="run the minute-NBBO quote pull (+-10pct band, per-ticker DTE)")
    ap.add_argument("--quotes-probe", action="store_true", help="print minute-quote schema for one ticker")
    ap.add_argument("--validate", action="store_true", help="compare a backfilled date vs the live snapshot")
    ap.add_argument("--verify-quotes", action="store_true", help="integrity-scan the quote store for empty/truncated files + stray .tmp")
    ap.add_argument("--ticker", help="single ticker (NAME or NAME:DTE); overrides --tickers for ALL modes")
    ap.add_argument("--tickers", nargs="*", help="tickers as NAME or NAME:DTE (e.g. ABC:45 XYZ:0); required for --run/--quotes/--verify-quotes. No default — no ticker preference is committed.")
    ap.add_argument("--start", type=lambda s: date.fromisoformat(s), default=DEFAULT_START)
    ap.add_argument("--end", type=lambda s: date.fromisoformat(s), default=DEFAULT_END)
    ap.add_argument("--date", help="date (YYYY-MM-DD) for --validate")
    ap.add_argument("--max-dte", type=int, default=None,
                    help="cap captured expiries to N days out. EOD --run default 60. For --quotes this is the "
                         "DTE fallback for any ticker passed without an explicit NAME:DTE.")
    ap.add_argument("--quote-out", help="output root for --quotes (default: <data>/quotes)")
    ap.add_argument("--log", help="log file path for --run/--quotes (default: <data>/logs/backfill_<mode>_<ts>.log). Output is timestamped and tee'd to console + file.")
    ap.add_argument("--verbose", action="store_true", help="also show third-party library logs (thetadata auth, grpc, urllib3). Default: only our messages.")
    ap.add_argument("--concurrency", type=int, default=1,
                    help="--quotes: requests in flight at once, via threads sharing ONE session (ThetaData "
                         "allows one session/account). Set to your tier's concurrent-request limit (Value=2). "
                         ">1 uses a supervisor that restarts the worker if it stalls on a hung call.")
    ap.add_argument("--quote-chunk-days", type=int, default=7,
                    help="--quotes sub-window size in calendar days (default 7). Smaller = fewer ThetaData "
                         "stalls on large minute requests (each request is small); larger = fewer requests.")
    ap.add_argument("--resume", action="store_true",
                    help="--run (EOD): skip months already fully written. (--quotes resumes automatically via "
                         "the per-ticker sealed.json — sealed expirations are always skipped, unsealed re-pulled.)")
    ap.add_argument("--rate", type=float, default=0.045, help="risk-free rate for IV back-solve (default 0.045)")
    ap.add_argument("--timeout", type=int, default=600, help="per-chunk watchdog timeout in seconds (default 600); a hung gRPC call is killed and retried")
    ap.add_argument("--retries", type=int, default=3, help="retries per chunk after a timeout/error (default 3)")
    ap.add_argument("--creds", help="path to creds.txt (else creds.txt / THETADATA_CREDENTIALS_FILE)")
    ap.add_argument("--out", help="output root (default: <data>/oi staging dir)")
    args = ap.parse_args()
    # A single --ticker overrides the list for any mode. Tokens are NAME or NAME:DTE.
    raw = [args.ticker] if args.ticker else (args.tickers or [])
    tickers, dte_map = parse_ticker_specs(raw, args.max_dte)
    if not tickers and (args.run or args.quotes or args.verify_quotes or args.probe or args.quotes_probe or args.validate):
        sys.exit("specify tickers, e.g. --tickers ABC:45 XYZ:0  (or a single --ticker ABC:45)")

    if args.verbose:
        os.environ["BF_VERBOSE"] = "1"
        _setup_logging()

    data_dir = resolve_data_dir()
    out_root = Path(args.out) if args.out else data_dir / "oi"

    # File logging (timestamped, tee'd to console) for the long runs so they're auditable. Set
    # BF_LOG_FILE so spawned worker children inherit it and log to the SAME file. (Only the parent
    # runs main(); children configure via _setup_logging() at import using the inherited env var.)
    if (args.run or args.quotes) and not os.environ.get("BF_LOG_FILE"):
        mode = "quotes" if args.quotes else "eod"
        logdir = data_dir / "logs"
        logdir.mkdir(parents=True, exist_ok=True)
        os.environ["BF_LOG_FILE"] = args.log or str(logdir / f"backfill_{mode}_{datetime.now():%Y%m%d_%H%M%S}.log")
        _setup_logging()
        log.info(f"logging to {os.environ['BF_LOG_FILE']}")

    if args.validate:
        if not args.date:
            sys.exit("--validate needs --ticker and --date")
        validate(tickers[0], args.date, out_root, data_dir)
        return

    if args.verify_quotes:
        qout = Path(args.quote_out) if args.quote_out else data_dir / "quotes"
        verify_quotes(qout, tickers, args.end)
        return

    if args.probe:
        probe(make_client(args.creds), tickers[0])
    elif args.quotes_probe:
        quote_probe(make_client(args.creds), tickers[0])
    elif args.run:
        out_root.mkdir(parents=True, exist_ok=True)
        log.info(f"data dir = {data_dir}\noutput   = {out_root}  (staging; merge into chain-snapshots after validating)")
        # No shared client: each chunk's watchdog child builds its own (a gRPC channel
        # doesn't survive being inherited/killed cleanly).
        run(tickers, args.start, args.end, out_root, args.max_dte if args.max_dte is not None else 60,
            args.rate, args.resume, args.creds, args.timeout, args.retries)
    elif args.quotes:
        missing = [t for t in tickers if t not in dte_map]
        if missing:
            sys.exit(f"--quotes needs a DTE for {missing}: pass {missing[0]}:DTE (e.g. {missing[0]}:45) or --max-dte")
        qout = Path(args.quote_out) if args.quote_out else data_dir / "quotes"
        qout.mkdir(parents=True, exist_ok=True)
        log.info(f"data dir = {data_dir}\nquote out = {qout}  (minute NBBO ±10%, per-ticker DTE)")
        run_quotes(tickers, args.start, args.end, qout, dte_map, args.rate,
                   args.creds, args.timeout, args.retries, args.quote_chunk_days, args.concurrency)
    else:
        sys.exit("specify one of --probe / --quotes-probe / --run / --quotes / --validate")


if __name__ == "__main__":
    main()
