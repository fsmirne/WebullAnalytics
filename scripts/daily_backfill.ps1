<#
.SYNOPSIS
	Daily ThetaData refresh of the canonical data stores — native-Windows PowerShell port of daily_backfill.sh.

.DESCRIPTION
	Runs the same four steps as daily_backfill.sh, but as native Windows processes so the SQLite writer
	(this script's Python helpers) and the reader (the wa backtest, a Windows process) share ONE OS's file
	locking + WAL shared-memory. That is the whole point of this port: on a WSL setup the backfill is a
	Linux process writing quotes.db over /mnt/c while the backtest reads it as a Windows process — WAL's
	-shm coordination does NOT interoperate across the WSL<->Windows 9p boundary, so a concurrent read can
	hit "disk I/O error / database disk image is malformed". Keeping both sides native-Windows fixes that.

	Steps (same order/semantics as the .sh):
	  1/4  wa ai history   -> daily closes + intraday tape for the strategy tickers (run FIRST)
	  2/5  --quotes        -> data/quotes.db (minute NBBO, per-expiry DELETE+INSERT, WAL)
	  3/5  --ohlcv         -> data/quotes.db `ohlcv` table (minute trade OHLCV, same band/DTE, own seals)
	  4/5  --run           -> data/oi/<TICKER>/<date>.jsonl (EOD open interest + back-solved IV)
	  5/5  verify          -> SQL coverage + crossed-quote scan of quotes.db (no network)

	Use daily_backfill.sh on true Linux/macOS/WSL; use this daily_backfill.ps1 as the default on Windows 11.
	Requires native Windows Python on PATH (python) and the wa.exe executable (published alongside, or on PATH).

.PARAMETER Start
	Extend the quotes+OI pull floor back for a one-off history fill (YYYY-MM-DD). Sealed data is still skipped.

.PARAMETER End
	Last day to pull (YYYY-MM-DD). Defaults to ET-today past 19:00 ET, else ET-yesterday — the date
	gates run on the ET clock (the trading calendar), not local time.

.PARAMETER Tickers
	Scope the quotes/ohlcv roots with per-ticker DTE, e.g. 'SPY:60','XSP:0'. Default = the daily set.

.PARAMETER OiTickers
	Scope the OI roots (bare names, no DTE — OI is a daily full-chain snapshot, not DTE-windowed like
	quotes/ohlcv, so backfill_thetadata.py's --run mode ignores per-ticker :DTE tokens entirely). Default
	= the traded roots plus SPX, the untraded legacy monthly root whose OI/IV is needed to fix SPXW GEX
	on monthly expiries.

.PARAMETER HistoryTickers
	Scope the `wa ai history` step (bare names, no DTE). Default = SPY XSP SPXW QQQ.

.PARAMETER Steps
	Which of the five steps to run (default all): history,quotes,ohlcv,oi,verify. Kept byte-identical in meaning
	to daily_backfill.sh --steps. E.g. -Steps history runs ONLY the history refresh (no ThetaData session,
	no quotes.db write — safe to run while a separate quote pull is in progress). -Steps quotes,oi,verify
	skips history.

.PARAMETER Verify
	Scope the verify roots (bare names). Default = SPXW XSP SPY QQQ.

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

$PY = "python"                                  # native Windows Python on PATH
$Script = Join-Path $ScriptDir "backfill_thetadata.py"
$Importer = Join-Path $ScriptDir "import_quotes_sqlite.py"
$Conc = 2

# --- Ticker sets (defaults = the daily set; -Tickers / -Verify override, matching the .sh env knobs). --------
if (-not $Tickers -or $Tickers.Count -eq 0) {
	if ($env:BACKFILL_TICKERS) { $Tickers = $env:BACKFILL_TICKERS -split '\s+' }
	else { $Tickers = @('SPXW:0','XSP:0','SPY:60','QQQ:60') }
}
# OI is a daily-snapshot instrument (one full-chain capture/day, not a DTE-windowed pull like
# quotes/ohlcv): backfill_thetadata.py's --run mode ignores per-ticker :DTE tokens entirely and always
# uses one global --max-dte across every ticker passed, so bare names are all this step needs or accepts
# meaningfully. SPX (legacy AM-settled root) is included but untraded: on a standard-monthly (3rd Friday)
# expiry real open interest splits across SPX and SPXW (see ParsingHelpers.AggregationRoots) - without
# SPX's OI backfilled too, GEX/max-pain/strike-ladder factors only ever see half that date's book. No
# minute-NBBO quotes/ohlcv pull for it - OI (+ the EOD-solved IV alongside it) is all ComputeGex needs.
if (-not $OiTickers -or $OiTickers.Count -eq 0) {
	if ($env:BACKFILL_OI_TICKERS) { $OiTickers = $env:BACKFILL_OI_TICKERS -split '\s+' }
	else { $OiTickers = @('SPXW','XSP','SPY','QQQ','SPX') }
}
if (-not $Verify -or $Verify.Count -eq 0) {
	if ($env:BACKFILL_VERIFY) { $Verify = $env:BACKFILL_VERIFY -split '\s+' }
	else { $Verify = @('SPXW','XSP','SPY','QQQ') }
}

# --- Step selection (default all five; identical semantics to daily_backfill.sh --steps). --------------------
if (-not $Steps -or $Steps.Count -eq 0) {
	if ($env:BACKFILL_STEPS) { $Steps = $env:BACKFILL_STEPS -split '[,\s]+' }
	else { $Steps = @('history','quotes','ohlcv','oi','verify') }
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
	$HistoryList = @('SPY','XSP','SPXW','QQQ')
}

# --- Resolve the wa executable (published alongside this script by install.bat; else PATH). ------------------
if (Test-Path -LiteralPath (Join-Path $ScriptDir "wa.exe")) { $WA = Join-Path $ScriptDir "wa.exe" }
else { $WA = "wa" }

# --- Date window, judged on the ET clock (the trading calendar) — local time is irrelevant, so runs from ----
# any timezone behave identically. ThetaData finalizes a session ~17:15 ET, so past 19:00 ET the pull may
# include ET-today; earlier it stops at ET-yesterday. -End / BACKFILL_END overrides. (Mirrors daily_backfill.sh.)
$EtTz = [System.TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time')
$EtNow = [System.TimeZoneInfo]::ConvertTime([DateTime]::UtcNow, $EtTz)
$EndOverride = $End                                          # -End takes precedence over BACKFILL_END,
if (-not $EndOverride) { $EndOverride = $env:BACKFILL_END }  # and (like the .sh) caps the OI window too
if ($EndOverride) { $End = $EndOverride }
elseif ($EtNow.Hour -ge 19) { $End = $EtNow.ToString('yyyy-MM-dd') }
else { $End = $EtNow.AddDays(-1).ToString('yyyy-MM-dd') }

# OI lags one session behind the evening gate: OCC publishes a session's open interest the NEXT morning (ET),
# and ThetaData's wildcard-expiration EOD/OI requests reject the current day outright. ET-yesterday's OI is
# only safe once that ET morning has passed (>= 09:00 ET); before that (e.g. a post-midnight-ET run) stop one
# day earlier — pulling it too soon would seal pre-publication OI.
$EndOi = $EndOverride
if (-not $EndOi) {
	if ($EtNow.Hour -ge 9) { $EndOi = $EtNow.AddDays(-1).ToString('yyyy-MM-dd') }
	else { $EndOi = $EtNow.AddDays(-2).ToString('yyyy-MM-dd') }
}

# Historical backfill floor (-Start / BACKFILL_START). Unset => backfill_thetadata.py's own default.
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
Write-Host "[$(Get-Ts)] === daily data update: ai history ($($HistoryList -join ' ')), quotes+ohlcv ${startNote}through $End, oi through $EndOi, verify ==="

foreach ($t in $HistoryList) {
	Invoke-Step "(1/5) ai history $t" $WA @('ai','history',$t)
}

if (Has-Step 'quotes') {
	$quotesArgs = @($Script,'--quotes','--tickers') + $Tickers + @('--end',$End) + $StartOpt + @('--concurrency',"$Conc")
	Invoke-Step "(2/5) minute-NBBO quotes -> data/quotes.db" $PY $quotesArgs
}

if (Has-Step 'ohlcv') {
	$ohlcvArgs = @($Script,'--ohlcv','--tickers') + $Tickers + @('--end',$End) + $StartOpt + @('--concurrency',"$Conc")
	Invoke-Step "(3/5) minute trade OHLCV -> data/quotes.db" $PY $ohlcvArgs
}

if (Has-Step 'oi') {
	$oiArgs = @($Script,'--run','--tickers') + $OiTickers + @('--end',$EndOi) + $StartOpt + @('--concurrency',"$Conc")
	Invoke-Step "(4/5) EOD open interest -> data/oi" $PY $oiArgs
}

if (Has-Step 'verify') {
	Invoke-Step "(5/5) quote-store coverage + integrity" $PY @($Importer,'--root',($Verify -join ','),'--verify')
}

if ($script:rc -eq 0) {
	Write-Host "[$(Get-Ts)] === ALL OK ==="
} else {
	Write-Host "[$(Get-Ts)] === COMPLETED WITH FAILURES (see above) ==="
}
exit $script:rc
