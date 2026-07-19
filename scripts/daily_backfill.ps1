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
	  2/4  --quotes        -> data/quotes.db (minute NBBO, per-expiry DELETE+INSERT, WAL)
	  3/4  --run           -> data/oi/<TICKER>/<date>.jsonl (EOD open interest + back-solved IV)
	  4/4  verify          -> SQL coverage + crossed-quote scan of quotes.db (no network)

	Use daily_backfill.sh on true Linux/macOS/WSL; use this daily_backfill.ps1 as the default on Windows 11.
	Requires native Windows Python on PATH (python) and the wa.exe executable (published alongside, or on PATH).

.PARAMETER Start
	Extend the quotes+OI pull floor back for a one-off history fill (YYYY-MM-DD). Sealed data is still skipped.

.PARAMETER End
	Last day to pull (YYYY-MM-DD). Defaults to today on evening runs (>= 19:00), else yesterday.

.PARAMETER Tickers
	Scope the quotes/OI roots with per-ticker DTE, e.g. 'SPY:60','XSP:0'. Default = the daily set.

.PARAMETER HistoryTickers
	Scope the `wa ai history` step (bare names, no DTE). Default = SPY XSP SPXW QQQ.

.PARAMETER NoHistory
	Skip the `wa ai history` step entirely.

.PARAMETER Verify
	Scope the verify roots (bare names). Default = SPXW XSP SPY GME QQQ.

.EXAMPLE
	# Normal daily run
	./daily_backfill.ps1

.EXAMPLE
	# One-off history fill for SPY + QQQ, scoped verify, skipping the history step
	./daily_backfill.ps1 -Start 2022-01-01 -Tickers SPY:60,QQQ:60 -Verify SPY,QQQ -NoHistory
#>
[CmdletBinding()]
param(
	[string]$Start = "",
	[string]$End = "",
	[string[]]$Tickers,
	[string[]]$HistoryTickers,
	[switch]$NoHistory,
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
	else { $Tickers = @('SPXW:0','XSP:0','SPY:60','GME:60','QQQ:60') }
}
if (-not $Verify -or $Verify.Count -eq 0) {
	if ($env:BACKFILL_VERIFY) { $Verify = $env:BACKFILL_VERIFY -split '\s+' }
	else { $Verify = @('SPXW','XSP','SPY','GME','QQQ') }
}

# --- `wa ai history` scope (skip entirely with -NoHistory). --------------------------------------------------
if ($NoHistory) {
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

# --- Date window. ThetaData finalizes a session ~17:15 ET, so an evening run (>= 19:00 local) may include ----
# TODAY; earlier runs stop at yesterday. -End / BACKFILL_END overrides.
if (-not $End) { $End = $env:BACKFILL_END }
if (-not $End) {
	if ((Get-Date).Hour -ge 19) { $End = (Get-Date).ToString('yyyy-MM-dd') }
	else { $End = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd') }
}

# OI always stops at yesterday: OCC publishes a session's open interest the NEXT morning, and ThetaData's
# wildcard-expiration EOD/OI requests reject the current day outright. Today's OI lands on tomorrow's run.
$EndOi = $env:BACKFILL_END
if (-not $EndOi) { $EndOi = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd') }

# Historical backfill floor (-Start / BACKFILL_START). Unset => backfill_thetadata.py's own default.
$StartValue = $Start
if (-not $StartValue) { $StartValue = $env:BACKFILL_START }
$StartOpt = @()
if ($StartValue) { $StartOpt = @('--start', $StartValue) }

function Get-Ts { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }

$script:rc = 0
function Invoke-Step {
	param([string]$Label, [string]$Exe, [string[]]$Args)
	Write-Host "[$(Get-Ts)] $Label"
	& $Exe @Args
	$ec = $LASTEXITCODE
	if ($ec -ne 0) {
		Write-Host "[$(Get-Ts)] [FAIL] $Label (exit $ec)"
		$script:rc = 1
	}
}

$startNote = if ($StartValue) { "from $StartValue " } else { "" }
Write-Host "[$(Get-Ts)] === daily data update: ai history ($($HistoryList -join ' ')), quotes ${startNote}through $End, oi through $EndOi, verify ==="

foreach ($t in $HistoryList) {
	Invoke-Step "(1/4) ai history $t" $WA @('ai','history',$t)
}

$quotesArgs = @($Script,'--quotes','--tickers') + $Tickers + @('--end',$End) + $StartOpt + @('--concurrency',"$Conc")
Invoke-Step "(2/4) minute-NBBO quotes -> data/quotes.db" $PY $quotesArgs

$oiArgs = @($Script,'--run','--tickers') + $Tickers + @('--end',$EndOi) + $StartOpt + @('--concurrency',"$Conc")
Invoke-Step "(3/4) EOD open interest -> data/oi" $PY $oiArgs

Invoke-Step "(4/4) quote-store coverage + integrity" $PY @($Importer,'--root','SPY','--verify')

if ($script:rc -eq 0) {
	Write-Host "[$(Get-Ts)] === ALL OK ==="
} else {
	Write-Host "[$(Get-Ts)] === COMPLETED WITH FAILURES (see above) ==="
}
exit $script:rc
