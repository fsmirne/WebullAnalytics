#!/usr/bin/env python3
"""
Backfill data/intraday/{SPY,SPXW}/<date>.csv from the massive.com (Polygon-mirror)
1-min SPY dump and Yahoo Finance ^GSPC daily.

- SPY: written directly from the Polygon minute bars.
- SPXW: synthesized as SPY_minute * (today_GSPC_open / today_SPY_open_at_09:30).
        The today's-open anchor makes the 09:30 minute exactly equal ^GSPC.Open
        from the daily history, so the BacktestRunner minute loop and the legacy
        daily-bar paths agree on spot at the day's open.

Inputs:
- $REPO/SPY-1min-full.json (download once via the massive.com API; gitignored,
  not committed because it's both large and tied to a personal API key).

Outputs to <WA_DATA_DIR>/intraday/{SPY,SPXW}/<date>.csv where WA_DATA_DIR comes
from environment resolution (see resolve_data_dir below). Set WA_DATA_DIR
explicitly to override the auto-detected path.

Run from the repo root:
    python3 scripts/backfill_intraday_polygon.py
"""
import csv
import json
import os
import sys
import urllib.request
import zoneinfo
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SRC = REPO / "SPY-1min-full.json"

def resolve_data_dir() -> Path:
    """Mirror of Program.ResolveBaseDir() in C#: prefer WA_DATA_DIR override,
    then $LOCALAPPDATA/WebullAnalytics/data (set natively on Windows; can be
    set in WSL via $WSLENV), then scan /mnt/c/Users/*/AppData/Local for an
    existing WebullAnalytics/data — WSL's $USER is the Linux user, not the
    Windows user, so the env var alone isn't enough. Fails with a clear error
    if nothing resolves so no one accidentally writes to a wrong path."""
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
        "Cannot resolve data directory. Set WA_DATA_DIR to your WebullAnalytics data dir, "
        "e.g. export WA_DATA_DIR=/mnt/c/Users/<you>/AppData/Local/WebullAnalytics/data"
    )

DATA_DIR = resolve_data_dir()
DEST_ROOT = DATA_DIR / "intraday"
NY = zoneinfo.ZoneInfo("America/New_York")
UTC = timezone.utc

def load_spy_bars() -> list[dict]:
    print(f"Loading {SRC}")
    with SRC.open() as f:
        d = json.load(f)
    print(f"  {len(d['results']):,} bars")
    return d["results"]

def fetch_gspc_daily(start: date, end: date) -> dict[date, tuple[float, float]]:
    """Returns {date: (open, close)} per NY trading day for ^GSPC."""
    p1 = int(datetime(start.year, start.month, start.day, tzinfo=UTC).timestamp())
    p2 = int(datetime(end.year, end.month, end.day, 23, 59, tzinfo=UTC).timestamp())
    url = (
        f"https://query1.finance.yahoo.com/v8/finance/chart/%5EGSPC"
        f"?period1={p1}&period2={p2}&interval=1d"
    )
    print(f"Fetching ^GSPC daily {start} -> {end} from Yahoo")
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=30) as r:
        data = json.loads(r.read())
    result = data["chart"]["result"][0]
    timestamps = result["timestamp"]
    quote = result["indicators"]["quote"][0]
    opens = quote["open"]
    closes = quote["close"]
    out: dict[date, tuple[float, float]] = {}
    for ts, o, c in zip(timestamps, opens, closes):
        if o is None or c is None:
            continue
        d_ = datetime.fromtimestamp(ts, tz=NY).date()
        out[d_] = (float(o), float(c))
    print(f"  {len(out)} daily (open, close) pairs")
    return out

def group_by_et_date(bars: list[dict]) -> dict[date, list[dict]]:
    by_date: dict[date, list[dict]] = defaultdict(list)
    for b in bars:
        et = datetime.fromtimestamp(b["t"] / 1000, tz=NY)
        by_date[et.date()].append(b)
    return by_date

def spy_daily_close(bars: list[dict]) -> float | None:
    """Use the 15:59 ET bar's close as the daily close (last RTH minute)."""
    target = None
    for b in bars:
        et = datetime.fromtimestamp(b["t"] / 1000, tz=NY)
        if et.hour == 15 and et.minute == 59:
            return float(b["c"])
        # Track latest RTH bar as fallback (handles early-close days)
        if et.hour < 16:
            target = b
    return float(target["c"]) if target else None

def spy_open_at_0930(bars: list[dict]) -> float | None:
    """The OPEN of the 09:30 ET bar — the price at the first millisecond of RTH.
    This is the anchor we align ^GSPC.Open against so the synthesized SPXW @ 09:30
    matches the real SPX open exactly. Returns None if no 09:30 bar exists."""
    for b in bars:
        et = datetime.fromtimestamp(b["t"] / 1000, tz=NY)
        if et.hour == 9 and et.minute == 30:
            return float(b["o"])
    return None

def fmt_ts(ms: int) -> str:
    dt = datetime.fromtimestamp(ms / 1000, tz=UTC)
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")

def write_day_csv(path: Path, bars: list[dict], scale: float = 1.0) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["timestamp_utc", "open", "high", "low", "close", "volume"])
        for b in bars:
            w.writerow([
                fmt_ts(b["t"]),
                f"{b['o'] * scale:.4f}" if scale != 1.0 else f"{b['o']:.4f}",
                f"{b['h'] * scale:.4f}" if scale != 1.0 else f"{b['h']:.4f}",
                f"{b['l'] * scale:.4f}" if scale != 1.0 else f"{b['l']:.4f}",
                f"{b['c'] * scale:.4f}" if scale != 1.0 else f"{b['c']:.4f}",
                0 if scale != 1.0 else int(b["v"]),
            ])
    return len(bars)

def main() -> int:
    bars = load_spy_bars()
    by_date = group_by_et_date(bars)
    trading_days = sorted(by_date.keys())
    print(f"  {len(trading_days)} trading days: {trading_days[0]} -> {trading_days[-1]}")

    # Yahoo daily ^GSPC for prior-close lookups (start a few days before first trading day)
    gspc_start = trading_days[0] - timedelta(days=10)
    gspc_end = trading_days[-1] + timedelta(days=1)
    gspc = fetch_gspc_daily(gspc_start, gspc_end)

    # SPY 09:30 ET open per day, derived from minute data. This is the anchor we
    # align Yahoo's ^GSPC.Open against to get a synthesized SPXW curve whose 09:30
    # value matches the real SPX open exactly.
    spy_open_0930: dict[date, float] = {}
    for d_, day_bars in by_date.items():
        o = spy_open_at_0930(day_bars)
        if o is not None:
            spy_open_0930[d_] = o

    spy_dir = DEST_ROOT / "SPY"
    spxw_dir = DEST_ROOT / "SPXW"
    spy_dir.mkdir(parents=True, exist_ok=True)
    spxw_dir.mkdir(parents=True, exist_ok=True)

    spy_written = 0
    spxw_written = 0
    ratio_log: list[tuple[str, float, float, float]] = []
    missing_ratio = []

    for d_ in trading_days:
        day_bars = by_date[d_]
        # Write SPY CSV
        spy_path = spy_dir / f"{d_.isoformat()}.csv"
        write_day_csv(spy_path, day_bars, scale=1.0)
        spy_written += 1

        # Compute today-anchored SPXW ratio: real SPX open / SPY's 09:30 open. With this
        # scale, the synthesized SPXW at the 09:30 minute exactly equals ^GSPC.Open — which
        # is the same value the backtest engine reads from history/SPXW.csv (Yahoo daily).
        # Eliminates the spot-source mismatch where intraday SPXW @ 09:30 differed from
        # bar.Open by ~0.1–0.15% due to using yesterday's close ratio.
        spx_pair = gspc.get(d_)
        today_spy_open = spy_open_0930.get(d_)
        if spx_pair is None or not today_spy_open:
            missing_ratio.append(d_.isoformat())
            continue
        today_spx_open = spx_pair[0]
        ratio = today_spx_open / today_spy_open
        ratio_log.append((d_.isoformat(), today_spx_open, today_spy_open, ratio))

        spxw_path = spxw_dir / f"{d_.isoformat()}.csv"
        write_day_csv(spxw_path, day_bars, scale=ratio)
        spxw_written += 1

    print(f"\nSPY  files written: {spy_written}")
    print(f"SPXW files written: {spxw_written}")
    if missing_ratio:
        print(f"SPXW skipped (no anchor for date): {missing_ratio}")
    print(f"\nFirst 3 ratios (today_SPX_open / today_SPY_open_at_0930):")
    for d_, spx, spy, r in ratio_log[:3]:
        print(f"  {d_}: SPX_open={spx:.2f} SPY_0930_open={spy:.2f} ratio={r:.6f}")
    print(f"Last 3 ratios:")
    for d_, spx, spy, r in ratio_log[-3:]:
        print(f"  {d_}: SPX_open={spx:.2f} SPY_0930_open={spy:.2f} ratio={r:.6f}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
