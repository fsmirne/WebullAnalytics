<#
.SYNOPSIS
	Daily ThetaData refresh of the canonical data stores — native-Windows PowerShell port of daily_backfill.sh.

.DESCRIPTION
	Runs the same steps as daily_backfill.sh, but as native Windows processes so the SQLite writer
	(wa data backfill) and the reader (the wa backtest, a Windows process) share ONE OS's file
	locking + WAL shared-memory. That is the whole point of this port: on a WSL setup the backfill is a
	Linux process writing quotes.db over /mnt/c while the backtest reads it as a Windows process — WAL's
	-shm coordination does NOT interoperate across the WSL<->Windows 9p boundary, so a concurrent read can
	hit "disk I/O error / database disk image is malformed". Keeping both sides native-Windows fixes that.

	Steps (same order/semantics as the .sh):
	  1/6  wa ai history             -> daily closes + intraday tape for the strategy tickers (run FIRST)
	  2/6  wa data backfill quotes   -> data/quotes.db (minute NBBO, per-expiry staged then promoted, WAL)
	  3/6  wa data backfill ohlcv    -> data/quotes.db `ohlcv` table (minute trade OHLCV, same band/DTE, own seals)
	  4/6  wa data backfill oi       -> data/oi/<TICKER>/<date>.jsonl (EOD open interest + back-solved IV)
	  5/6  wa data verify            -> SQL coverage + crossed-quote scan of quotes.db (no network)
	  6/6  wa data clock             -> data/clock/<TICKER>/ (close/09:45 calibration books, then refit the trading-time clock)

	Use daily_backfill.sh on true Linux/macOS/WSL; use this daily_backfill.ps1 as the default on Windows 11.
	Requires the wa.exe executable (published alongside, or on PATH).

.PARAMETER Start
	Extend the quotes+OI pull floor back for a one-off history fill (YYYY-MM-DD). Sealed data is still skipped.

.PARAMETER End
	Last day to pull (YYYY-MM-DD). Defaults to ET-today past 19:00 ET, else ET-yesterday — the date
	gates run on the ET clock (the trading calendar), not local time.

.PARAMETER Tickers
	Scope the quotes/ohlcv roots with per-ticker DTE, e.g. 'SPY:60','SPXW:0'. Default = the daily set
	(SPXW:0 SPY:60). QQQ is OPT-IN (dropped from every default 2026-10-08: no live strategy trades it, and
	its 60-DTE quote+ohlcv pull pushed the morning run past the open). Pull it on demand at 60 DTE (the QQQ.DC
	long leg is 30-45 DTE; a :30 pull truncates it):
	  ./daily_backfill.ps1 -Tickers QQQ:60 -OiTickers QQQ -Verify QQQ -HistoryTickers QQQ
	No gap accrues while it is off: the pull floor is `wa data backfill`'s default -Start and sealed data is
	skipped, so the next run that names QQQ fills everything since its last pull.
	XSP is OPT-IN too (dropped from every default 2026-11-09: SPXW is the 0DTE data source). Its ohlcv rows were
	deleted from the store; quotes and OI were kept and resume from their last pull when named:
	  ./daily_backfill.ps1 -Tickers XSP:0 -OiTickers XSP -Verify XSP -HistoryTickers XSP -Steps history,quotes,oi,verify

.PARAMETER OiTickers
	Scope the OI roots (bare names, no DTE — OI is a daily full-chain snapshot, not DTE-windowed like
	quotes/ohlcv, so `wa data backfill oi` takes bare names and one global --dte). Default
	= the traded roots plus SPX, the untraded legacy monthly root whose OI/IV is needed to fix SPXW GEX
	on monthly expiries.

.PARAMETER HistoryTickers
	Scope the `wa ai history` step (bare names, no DTE). Default = SPY SPXW.

.PARAMETER Steps
	Which of the six steps to run (default all): history,quotes,ohlcv,oi,verify,clock. Kept byte-identical in meaning
	to daily_backfill.sh --steps. E.g. -Steps history runs ONLY the history refresh (no ThetaData session,
	no quotes.db write — safe to run while a separate quote pull is in progress). -Steps quotes,oi,verify
	skips history.

.PARAMETER Verify
	Scope the verify roots (bare names). Default = SPXW SPY.

.EXAMPLE
	# Normal daily run
	./daily_backfill.ps1

.EXAMPLE
	# One-off history fill for SPY + QQQ, scoped verify, skipping the history step
	./daily_backfill.ps1 -Start 2022-01-01 -Tickers SPY:60,QQQ:60 -Verify SPY,QQQ -Steps quotes,oi,verify
#>
[CmdletBinding()]
param(
	[string]$Start = "",
	[string]$End = "",
	[string[]]$Tickers,
	[string[]]$OiTickers,
	[string[]]$HistoryTickers,
	[string[]]$Steps,
	[string[]]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"   # a failed step must not abort the others (mirrors set -uo pipefail + rc tracking)

$ScriptDir = $PSScriptRoot

# --- Prod data folder (LocalApplicationData), matching Program.cs's BaseDir resolution on Windows. ----------
# Honor an existing WA_DATA_DIR; otherwise %LOCALAPPDATA%\WebullAnalytics\data.
if (-not $env:WA_DATA_DIR) {
	if ($env:LOCALAPPDATA) {
		$env:WA_DATA_DIR = Join-Path $env:LOCALAPPDATA "WebullAnalytics\data"
	} else {
		Write-Warning "LOCALAPPDATA is unset and WA_DATA_DIR not provided — set WA_DATA_DIR to the WebullAnalytics data folder."
	}
}
$ProdData = $env:WA_DATA_DIR

# --- ThetaData auth: creds.txt in the data folder unless THETADATA_CREDENTIALS_FILE overrides it. -----------
if (-not $env:THETADATA_CREDENTIALS_FILE) {
	$env:THETADATA_CREDENTIALS_FILE = Join-Path $ProdData "creds.txt"
	if (-not (Test-Path -LiteralPath $env:THETADATA_CREDENTIALS_FILE)) {
		Write-Warning "creds not found at $($env:THETADATA_CREDENTIALS_FILE)"
	}
}

$Conc = 2

# --- Ticker sets (defaults = the daily set; -Tickers / -Verify override, matching the .sh env knobs). --------
if (-not $Tickers -or $Tickers.Count -eq 0) {
	if ($env:BACKFILL_TICKERS) { $Tickers = $env:BACKFILL_TICKERS -split '\s+' }
	else { $Tickers = @('SPXW:0','SPY:60') }
}
# OI is a daily-snapshot instrument (one full-chain capture/day, not a DTE-windowed pull like
# quotes/ohlcv): `wa data backfill oi` takes bare names and one global --dte across every ticker passed. SPX (legacy AM-settled root) is included but untraded: on a standard-monthly (3rd Friday)
# expiry real open interest splits across SPX and SPXW (see ParsingHelpers.AggregationRoots) - without
# SPX's OI backfilled too, GEX/max-pain/strike-ladder factors only ever see half that date's book. No
# minute-NBBO quotes/ohlcv pull for it - OI (+ the EOD-solved IV alongside it) is all ComputeGex needs.
if (-not $OiTickers -or $OiTickers.Count -eq 0) {
	if ($env:BACKFILL_OI_TICKERS) { $OiTickers = $env:BACKFILL_OI_TICKERS -split '\s+' }
	else { $OiTickers = @('SPXW','SPY','SPX') }
}
if (-not $Verify -or $Verify.Count -eq 0) {
	if ($env:BACKFILL_VERIFY) { $Verify = $env:BACKFILL_VERIFY -split '\s+' }
	else { $Verify = @('SPXW','SPY') }
}

# --- Step selection (default all six; identical semantics to daily_backfill.sh --steps). --------------------
if (-not $Steps -or $Steps.Count -eq 0) {
	if ($env:BACKFILL_STEPS) { $Steps = $env:BACKFILL_STEPS -split '[,\s]+' }
	else { $Steps = @('history','quotes','ohlcv','oi','verify','clock') }
}
$Steps = @($Steps | ForEach-Object { $_.ToLower() })
function Has-Step([string]$name) { return $Steps -contains $name }

# --- `wa ai history` scope. ----------------------------------------------------------------------------------
if (-not (Has-Step 'history')) {
	$HistoryList = @()
} elseif ($HistoryTickers -and $HistoryTickers.Count -gt 0) {
	$HistoryList = $HistoryTickers
} elseif ($env:BACKFILL_HISTORY_TICKERS) {
	$HistoryList = $env:BACKFILL_HISTORY_TICKERS -split '\s+'
} else {
	$HistoryList = @('SPY','SPXW')
}

# --- Resolve the wa executable (published alongside this script by install.bat; else PATH). ------------------
if (Test-Path -LiteralPath (Join-Path $ScriptDir "wa.exe")) { $WA = Join-Path $ScriptDir "wa.exe" }
else { $WA = "wa" }

# --- Date window, judged on the ET clock (the trading calendar) — local time is irrelevant, so runs from ----
# any timezone behave identically. ThetaData finalizes a session ~17:15 ET, so past 19:00 ET the pull may
# include ET-today; earlier it stops at ET-yesterday. -End / BACKFILL_END overrides. (Mirrors daily_backfill.sh.)
# Resolved FRESH at each step's own invocation, not once up front: history/quotes/ohlcv can run for hours,
# so a run started at 08:50 ET reaching the oi step at 10:15 ET must see 10:15's gate, not 08:50's — a stale
# snapshot silently pulled OI two days behind (08-26 instead of 08-27 on a run that crossed 09:00 ET mid-flight).
$EtTz = [System.TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time')
function Get-EtNow { [System.TimeZoneInfo]::ConvertTime([DateTime]::UtcNow, $EtTz) }
$EndOverride = $End                                          # -End takes precedence over BACKFILL_END,
if (-not $EndOverride) { $EndOverride = $env:BACKFILL_END }  # and (like the .sh) caps the OI window too

function Resolve-End {
	if ($EndOverride) { return $EndOverride }
	$now = Get-EtNow
	if ($now.Hour -ge 19) { return $now.ToString('yyyy-MM-dd') }
	else { return $now.AddDays(-1).ToString('yyyy-MM-dd') }
}

# OI lags one session behind the evening gate: OCC publishes a session's open interest the NEXT morning (ET),
# and ThetaData's wildcard-expiration EOD/OI requests reject the current day outright. ET-yesterday's OI is
# only safe once that ET morning has passed (>= 08:00 ET); before that (e.g. a post-midnight-ET run) stop one
# day earlier — pulling it too soon would seal pre-publication OI.
# Verified 2026-09-02: ThetaData's OI ingest batch lands 06:30:00-06:30:37 ET with zero variance across 10
# sampled sessions (08-19..09-01) — 08:00 keeps ~90min of buffer past that batch. (Mirrors daily_backfill.sh.)
function Resolve-EndOi {
	if ($EndOverride) { return $EndOverride }
	$now = Get-EtNow
	if ($now.Hour -ge 8) { return $now.AddDays(-1).ToString('yyyy-MM-dd') }
	else { return $now.AddDays(-2).ToString('yyyy-MM-dd') }
}

# Historical backfill floor (-Start / BACKFILL_START). Unset => `wa data backfill`'s own default.
$StartValue = $Start
if (-not $StartValue) { $StartValue = $env:BACKFILL_START }
$StartOpt = @()
if ($StartValue) { $StartOpt = @('--start', $StartValue) }

function Get-Ts { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }

$script:rc = 0
function Invoke-Step {
	param([string]$Label, [string]$Exe, [string[]]$CmdArgs)
	Write-Host "[$(Get-Ts)] $Label"
	& $Exe @CmdArgs
	$ec = $LASTEXITCODE
	if ($ec -ne 0) {
		Write-Host "[$(Get-Ts)] [FAIL] $Label (exit $ec)"
		$script:rc = 1
	}
}

$startNote = if ($StartValue) { "from $StartValue " } else { "" }
Write-Host "[$(Get-Ts)] === daily data update: ai history ($($HistoryList -join ' ')), quotes+ohlcv ${startNote}through $(Resolve-End) (as of now; re-resolved per step), oi through $(Resolve-EndOi) (as of now; re-resolved per step), verify, clock ==="

foreach ($t in $HistoryList) {
	Invoke-Step "(1/6) ai history $t" $WA @('ai','history',$t)
}

if (Has-Step 'quotes') {
	$quotesArgs = @('data','backfill','quotes','--tickers',($Tickers -join ','),'--end',(Resolve-End)) + $StartOpt + @('--concurrency',"$Conc")
	Invoke-Step "(2/6) minute-NBBO quotes -> data/quotes.db" $WA $quotesArgs
}

if (Has-Step 'ohlcv') {
	$ohlcvArgs = @('data','backfill','ohlcv','--tickers',($Tickers -join ','),'--end',(Resolve-End)) + $StartOpt + @('--concurrency',"$Conc")
	Invoke-Step "(3/6) minute trade OHLCV -> data/quotes.db" $WA $ohlcvArgs
}

if (Has-Step 'oi') {
	$oiArgs = @('data','backfill','oi','--tickers',($OiTickers -join ','),'--end',(Resolve-EndOi)) + $StartOpt
	Invoke-Step "(4/6) EOD open interest -> data/oi" $WA $oiArgs
}

if (Has-Step 'verify') {
	Invoke-Step "(5/6) quote-store coverage + integrity" $WA @('data','verify','--roots',($Verify -join ','))
}

if (Has-Step 'clock') {
	Invoke-Step "(6/6) trading-time clock calibration" $WA @('data','clock')
}

if ($script:rc -eq 0) {
	Write-Host "[$(Get-Ts)] === ALL OK ==="
} else {
	Write-Host "[$(Get-Ts)] === COMPLETED WITH FAILURES (see above) ==="
}
exit $script:rc
