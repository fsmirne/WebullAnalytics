#!/usr/bin/env python3
"""Import the per-expiry NBBO quote CSVs into a single indexed SQLite store.

PROTOTYPE for evaluating SQLite as the canonical quote store. The DB mirrors the CSVs row-for-row, applying
the SAME row-validity filters the C# CSV reader (QuoteStoreCache.ExpiryQuotes.Load) applies, so the backtest
produces identical results when run with --quote-db. bid/ask are stored as their ORIGINAL text so the C#
side's decimal.Parse yields byte-identical values. The index on (root, expiry, date) lets the per-expiry
load fetch exactly the rows it needs, so the 45DTE tail SPY files carry is never read.

Usage:
  import_quotes_sqlite.py --db <out.db> --quotes-dir <dir> --root SPY[,XSP] [--since YYYY-MM-DD] [--until YYYY-MM-DD]

--since/--until filter which EXPIRY files (by filename) are imported. Omit for the whole store.
"""
import argparse
import glob
import os
import sqlite3
from datetime import date, timedelta
from pathlib import Path


def resolve_data_dir() -> Path:
    """Mirror of backfill_thetadata.resolve_data_dir / Program.ResolveBaseDir."""
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
    raise RuntimeError("Cannot resolve data dir. Set WA_DATA_DIR=/mnt/c/Users/<you>/AppData/Local/WebullAnalytics/data")


def parse_time_sec(hms):
    """Mirror of QuoteStoreCache.ParseTimeSec: 'HH:MM:SS' -> sec, or -1 if invalid."""
    p = hms.split(":")
    if len(p) < 2:
        return -1
    try:
        h = int(p[0]); mi = int(p[1])
    except ValueError:
        return -1
    s = 0
    if len(p) > 2:
        try:
            s = int(p[2])
        except ValueError:
            s = 0
    return h * 3600 + mi * 60 + s


def parse_int_loose(s):
    """Mirror of ParseIntLoose: tolerate '55', '55.0', ''."""
    dot = s.find(".")
    t = s[:dot] if dot >= 0 else s
    try:
        return int(t)
    except ValueError:
        return 0


# Canonical schema — the single source of truth for every writer (importer, backfill, scraper share this).
SCHEMA_SQL = (
    "CREATE TABLE IF NOT EXISTS quotes (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, "
    "strike_milli INTEGER, right TEXT, bid INTEGER, ask INTEGER, bid_size INTEGER, ask_size INTEGER, "
    "PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID"
)
# Same shape as `quotes` (SELECT * copies positionally): the backfill's chunked pull lands each window here,
# then promotes the finished expiry into `quotes` in one DELETE+copy transaction — an aborted pull must never
# leave the live table half-rebuilt (the first-window DELETE used to commit long before the later windows, so
# a 2026-08-16 abort left SPY 2026-08-21 truncated at 07-17 and the truncation traveled through backup/restore).
STAGING_SQL = SCHEMA_SQL.replace("quotes (", "quotes_staging (", 1)
SEALED_SQL = "CREATE TABLE IF NOT EXISTS sealed (root TEXT, expiry INTEGER, PRIMARY KEY (root, expiry)) WITHOUT ROWID"
# Interior trading days PROVEN absent at the vendor for an expiry (reproducible across pulls) — the
# seal-time completeness check accepts exactly these and nothing else. Lives IN the DB (like `sealed`,
# which replaced sealed.json) so the knowledge travels with the store; a fresh rebuild starts with an
# empty table and must re-prove gaps. Curated by hand: INSERT INTO known_holes VALUES('SPY', 20220926, 20220815);
KNOWN_HOLES_SQL = "CREATE TABLE IF NOT EXISTS known_holes (root TEXT, expiry INTEGER, date INTEGER, PRIMARY KEY (root, expiry, date)) WITHOUT ROWID"

# OHLCV sibling table (2026-08-18): per-minute trade bars from ThetaData option_history_ohlc — the TAPE
# beside the quotes table's BOOK. Deliberately its own table, never new columns on `quotes`: sealed quote
# rows are byte-final by policy, quote rows exist for every disseminated minute while trade bars exist only
# for traded minutes (volume > 0 is an ingest invariant here), and the two endpoints carry different label
# conventions — OHLC bars arrive START-of-bar labeled (validated 2026-08-18: the 09:30:00 bar carries the
# opening trades, and per-contract minute sums reconcile to the EOD day volume up to the overnight GTH
# session EOD includes), so the quotes pull's -60s relabel does NOT apply. RTH bars only. Prices (and vwap)
# share the quotes tick encoding (ten-thousandths); `trades` = the vendor's per-bar print count. Sealing and
# proven-hole bookkeeping are separate from quotes (`ohlcv_sealed`/`ohlcv_known_holes`) because volume
# completeness certifies a different pull than quote completeness.
OHLCV_SQL = (
    "CREATE TABLE IF NOT EXISTS ohlcv (root TEXT, expiry INTEGER, date INTEGER, time_sec INTEGER, "
    "strike_milli INTEGER, right TEXT, open INTEGER, high INTEGER, low INTEGER, close INTEGER, "
    "volume INTEGER, trades INTEGER, vwap INTEGER, "
    "PRIMARY KEY (root, expiry, date, strike_milli, right, time_sec)) WITHOUT ROWID"
)
OHLCV_STAGING_SQL = OHLCV_SQL.replace("ohlcv (", "ohlcv_staging (", 1)
OHLCV_SEALED_SQL = "CREATE TABLE IF NOT EXISTS ohlcv_sealed (root TEXT, expiry INTEGER, PRIMARY KEY (root, expiry)) WITHOUT ROWID"
OHLCV_KNOWN_HOLES_SQL = "CREATE TABLE IF NOT EXISTS ohlcv_known_holes (root TEXT, expiry INTEGER, date INTEGER, PRIMARY KEY (root, expiry, date)) WITHOUT ROWID"


def ymd_int(date_str):
    """'yyyy-MM-dd' -> 20260617 (the on-disk date encoding)."""
    return int(date_str[:4] + date_str[5:7] + date_str[8:10])


def encode_quote(root, expiry_int, date_str, time_str, strike, right, bid, ask, bid_size=0, ask_size=0):
    """Encode one quote into the canonical row tuple, or None only when the row is structurally invalid
    (bad time/strike/right) or carries NO numeric side at all (no quote disseminated that minute). The store
    is FAITHFUL to the vendor: one-sided books (bid 0/absent with a live ask — the far-OTM/expiry-day norm)
    and empty 0/0 auction rows are KEPT, with an absent side stored as 0 (an NBBO bid/ask of 0 IS absence).
    Policy lives in the READERS: the opener requires two-sided books (StrikeLadder/QuoteSanity/CandidateScorer
    gates), management prices exits from whatever side exists. History: until 2026-07-22 this encoder REQUIRED
    bid>0 AND ask>0, which silently annihilated one-sided books store-wide and left management rules blind on
    expiry-day buybacks (e.g. SPY 477C 2022-01-10, quoted 0.00x0.01 all day at the vendor, absent here).
    The single encoder used by every writer so the store is byte-identical regardless of source. bid/ask are
    penny-tick floats (possibly float-noisy) rounded to ten-thousandths; dates -> INTEGER yyyymmdd."""
    sec = parse_time_sec(str(time_str))
    if sec < 0:
        return None
    try:
        strike_milli = round(float(strike) * 1000)
    except (ValueError, TypeError):
        return None

    def tick(v):
        try:
            f = float(v)
        except (ValueError, TypeError):
            return None
        if f != f:  # NaN — that side wasn't disseminated
            return None
        return round(f * 10000)

    bid_t, ask_t = tick(bid), tick(ask)
    if bid_t is None and ask_t is None:
        return None
    bid_t, ask_t = bid_t if bid_t is not None else 0, ask_t if ask_t is not None else 0
    if bid_t < 0 or ask_t < 0:
        return None
    rt = str(right).strip().upper()
    cp = rt[0] if rt else "?"
    if cp not in ("C", "P"):
        return None
    return (root, expiry_int, ymd_int(date_str), sec, strike_milli, cp, bid_t, ask_t,
            parse_int_loose(str(bid_size)), parse_int_loose(str(ask_size)))


def encode_ohlcv(root, expiry_int, date_str, time_str, strike, right, o, h, l, c, volume, trades, vwap):
    """Encode one minute trade bar into the canonical ohlcv row tuple, or None when the bar is structurally
    invalid or carries no trades — ONLY traded minutes are stored (the vendor serves a dense minute grid with
    volume=0/NaN filler rows; persisting those would bloat the table with non-information a reader can infer).
    A traded bar without a numeric close is vendor noise and is dropped. Prices/vwap use the quotes table's
    ten-thousandths tick encoding; a NaN price side stores as 0 (absent)."""
    sec = parse_time_sec(str(time_str))
    if sec < 0:
        return None
    try:
        strike_milli = round(float(strike) * 1000)
    except (ValueError, TypeError):
        return None
    vol = parse_int_loose(str(volume))
    if vol <= 0:
        return None

    def tick(v):
        try:
            f = float(v)
        except (ValueError, TypeError):
            return None
        if f != f:  # NaN
            return None
        return round(f * 10000)

    ct = tick(c)
    if ct is None or ct < 0:
        return None
    rt = str(right).strip().upper()
    cp = rt[0] if rt else "?"
    if cp not in ("C", "P"):
        return None
    z = lambda t: t if t is not None and t >= 0 else 0
    return (root, expiry_int, ymd_int(date_str), sec, strike_milli, cp,
            z(tick(o)), z(tick(h)), z(tick(l)), ct, vol, parse_int_loose(str(trades)), z(tick(vwap)))


def connect_wal(db_path):
    """Open the canonical store in WAL mode with a generous busy-timeout — the multi-writer setting that lets
    the importer, the backfill, and the scraper all write while the backtest reads."""
    conn = sqlite3.connect(db_path, timeout=60)
    conn.execute("PRAGMA busy_timeout=60000")
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute(SCHEMA_SQL)
    conn.execute(STAGING_SQL)
    conn.execute(OHLCV_SQL)
    conn.execute(OHLCV_STAGING_SQL)
    return conn


def upsert_expiry(conn, root, expiry_int, rows):
    """Replace one expiry's rows (DELETE then INSERT). Used by the backfill's per-expiry write; idempotent."""
    rows = sorted(rows, key=lambda r: (r[2], r[4], r[5], r[3]))  # ascending PK within (root,expiry)
    conn.execute("DELETE FROM quotes WHERE root=? AND expiry=?", (root, expiry_int))
    conn.executemany("INSERT OR IGNORE INTO quotes VALUES (?,?,?,?,?,?,?,?,?,?)", rows)
    conn.commit()


def rows_from_csv(path, root, expiry_int):
    """Canonical rows from one expiry CSV via the shared encoder, sorted by the WITHOUT ROWID primary key."""
    out = []
    with open(path) as fh:
        next(fh, None)  # header
        for line in fh:
            c = line.rstrip("\n").split(",")
            if len(c) < 6:
                continue
            date = c[0]
            if len(date) != 10 or date[4] != "-" or date[7] != "-":
                continue  # expect yyyy-MM-dd
            row = encode_quote(root, expiry_int, date, c[1], c[2], c[3], c[4], c[5],
                               c[6] if len(c) > 6 else 0, c[7] if len(c) > 7 else 0)
            if row is not None:
                out.append(row)
    out.sort(key=lambda r: (r[2], r[4], r[5], r[3]))  # date, strike, right, time → ascending PK within (root,expiry)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=None, help="SQLite path (default <data-dir>/quotes.db)")
    ap.add_argument("--quotes-dir", default=None, help="dir containing <root>/<expiry>.csv (default <data-dir>/quotes)")
    ap.add_argument("--root", default=None, help="comma-separated roots, e.g. SPY,XSP (required for import)")
    ap.add_argument("--since", default=None, help="import expiry files >= this date (filename)")
    ap.add_argument("--until", default=None, help="import expiry files <= this date (filename)")
    ap.add_argument("--incremental", action="store_true",
                    help="upsert mode: keep the existing table and re-import each expiry (DELETE+INSERT) instead "
                         "of dropping/rebuilding. Used by the daily backfill sync to fold in new/changed expiries.")
    ap.add_argument("--verify", action="store_true", help="recent-window coverage + crossed-quote check (cheap, PK-indexed), then exit")
    ap.add_argument("--full", action="store_true", help="with --verify: scan the WHOLE table (slow) instead of just recent expiries")
    ap.add_argument("--analyze", action="store_true", help="run ANALYZE (rebuild query-planner stats) and exit — "
                    "the once-a-day full-table pass that import --incremental deliberately skips")
    args = ap.parse_args()

    data_dir = resolve_data_dir()
    db = args.db or str(data_dir / "quotes.db")
    quotes_dir = args.quotes_dir or str(data_dir / "quotes")

    conn = sqlite3.connect(db, timeout=60)
    conn.execute("PRAGMA busy_timeout=60000")  # wait for the lock (multi-writer world) instead of erroring
    if args.verify:
        verify(conn, full=args.full)
        return
    if args.analyze:
        print("analyzing (full-table stats) ...", flush=True)
        conn.execute("ANALYZE"); conn.commit(); conn.close()
        print("done: ANALYZE")
        return
    if not args.root:
        ap.error("--root is required for import (omit it only with --verify or --analyze)")
    conn.execute("PRAGMA journal_mode=WAL" if args.incremental else "PRAGMA journal_mode=OFF")
    conn.execute("PRAGMA synchronous=OFF")
    conn.execute("PRAGMA temp_store=MEMORY")
    conn.execute("PRAGMA cache_size=-1048576")  # ~1 GB page cache
    # WITHOUT ROWID: the PK B-tree IS the table, clustered on (root, expiry, date, ...) — the backtest's
    # lookup key — so there's no separate index to store or maintain. dates are INTEGER yyyymmdd; bid/ask
    # are scaled integers in ten-thousandths (price = value / 10000). No duplicate (date,strike,right,time)
    # rows exist in the source (verified), so the unique PK drops nothing.
    if not args.incremental:
        conn.execute("DROP TABLE IF EXISTS quotes")
    conn.execute(SCHEMA_SQL)

    # Gather the expiry files to import first (across all roots, after since/until filtering) so we can
    # show an "x/total: " progress counter on each imported line.
    jobs = []
    for root in [r.strip().upper() for r in args.root.split(",")]:
        files = sorted(glob.glob(os.path.join(quotes_dir, root, "*.csv")))
        for path in files:
            expiry = os.path.basename(path)[:-4]
            if len(expiry) != 10:
                continue
            if args.since and expiry < args.since:
                continue
            if args.until and expiry > args.until:
                continue
            jobs.append((root, path, expiry, int(expiry[:4] + expiry[5:7] + expiry[8:10])))

    total = 0
    njobs = len(jobs)
    for i, (root, path, expiry, expiry_int) in enumerate(jobs, 1):
        batch = rows_from_csv(path, root, expiry_int)
        # Incremental: replace this expiry's rows so a re-pulled/finalized expiry is updated in place.
        if args.incremental:
            conn.execute("DELETE FROM quotes WHERE root=? AND expiry=?", (root, expiry_int))
        conn.executemany("INSERT OR IGNORE INTO quotes VALUES (?,?,?,?,?,?,?,?,?,?)", batch)
        conn.commit()
        total += len(batch)
        print(f"  {i}/{njobs}: {root}/{expiry}: {len(batch):,} rows (cum {total:,})", flush=True)

    if args.incremental:
        # Don't re-scan the whole multi-GB table to restat a few new expiries — fold the WAL back into the
        # main file instead (fast) and leave stats alone. Run ANALYZE separately (--analyze) end-of-day.
        conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    else:
        print("analyzing ...", flush=True)
        conn.execute("ANALYZE")
    conn.commit()
    conn.close()
    print(f"done: {total:,} rows -> {db}")


def verify(conn, full=False, recent_days=21):
    """Coverage + crossed-quote integrity check. Default (daily) is CHEAP: per root it scans only the recent
    expiries (expiry >= today−recent_days) via the WITHOUT-ROWID PK — confirming coverage advanced and no
    crossed quotes in what the backfill just touched — instead of a full-table scan that crawls over drvfs.
    Pass full=True for the whole-table report (end-of-day, alongside --analyze)."""
    try:
        roots = [r[0] for r in conn.execute("SELECT DISTINCT root FROM quotes ORDER BY root")]
    except sqlite3.OperationalError as e:
        print(f"verify: no quotes table ({e})"); return

    if full:
        print("[full-table scan]")
        print(f"{'root':6} {'rows':>14} {'trade-days':>11} {'min':>10} {'max':>10} {'crossed':>9}")
        for root in roots:
            n, d, lo, hi, x = conn.execute(
                "SELECT COUNT(*), COUNT(DISTINCT date), MIN(date), MAX(date), "
                "SUM(CASE WHEN bid > ask THEN 1 ELSE 0 END) FROM quotes WHERE root=?", (root,)).fetchone()
            print(f"{root:6} {n:>14,} {d:>11,} {str(lo):>10} {str(hi):>10} {(x or 0):>9,}")
        return

    # Cheap = pure PK seeks, no scan: a date-window still pulls SPY's fat 60-DTE forward expiries (tens of
    # millions of rows). Instead, per root: MAX(expiry) (rightmost of the root's PK range), then MAX(date)
    # within that expiry (rightmost of that sub-range), then count/crossed on just that one (expiry,date)
    # slice — one day's chain, a few thousand rows. Confirms coverage advanced + the freshest slice is sane.
    print("[freshest-slice check]")
    print(f"{'root':6} {'max_expiry':>11} {'latest_date':>11} {'slice_rows':>11} {'crossed':>9}")
    for root in roots:
        maxexp = conn.execute("SELECT MAX(expiry) FROM quotes WHERE root=?", (root,)).fetchone()[0]
        if maxexp is None:
            print(f"{root:6} {'(empty)':>11}"); continue
        maxdate = conn.execute("SELECT MAX(date) FROM quotes WHERE root=? AND expiry=?", (root, maxexp)).fetchone()[0]
        n, x = conn.execute(
            "SELECT COUNT(*), SUM(CASE WHEN bid > ask THEN 1 ELSE 0 END) "
            "FROM quotes WHERE root=? AND expiry=? AND date=?", (root, maxexp, maxdate)).fetchone()
        print(f"{root:6} {str(maxexp):>11} {str(maxdate):>11} {(n or 0):>11,} {(x or 0):>9,}")


if __name__ == "__main__":
    main()
