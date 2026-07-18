<#
backtest_tp_sweep.ps1 - 1D sweep over the SPY DC take-profit target (Target A: profitTargetPctOfDebit).

Modeled on backtest_sweep.ps1 (the GEX-signal sweep), reusing its fills.jsonl parser and CSV/ranking
machinery. The only axis here is the fixed-%-of-debit take-profit: close a position on any day once its
mark-to-market profit reaches N% of the entry debit.

WHY NOW: the TakeProfit "maxprofit-disable" bug (Target B fired at pctOfMaxProfit=1.0 despite "1.0 disables"
semantics) contaminated every earlier DC sweep. With that fixed, this isolates Target A: each cell passes
  --tp-debit N   (Target A = close at +N% of debit; N=0 turns Target A off)
  --tp 1.0       (Target B, the % -of-max-projected exit, DISABLED -> also makes its scorer EV-clamp a no-op)
so the ONLY thing changing across cells is the take-profit %. Cell N=0 (off) is the baseline: no take-profit
at all, exits handled solely by CloseBeforeShortExpiry. It answers "does taking profit help, and at what %?".

Sizing-neutral (--lots 1): closed-lifecycle P&L is additive, so PF / total / avg measure per-trade EDGE, not
a compounding curve. Rank here, then confirm the winner's drawdown at the real balance separately.

Runs SEQUENTIALLY (concurrent backtests contend on quotes.db). Nothing else may run a backtest while this is
live. Point -Wa at the FIXED binary: either the pinned dev build (…\.sweep-bin\wa.dll, needs -Dotnet) or the
installed wa.exe AFTER running install.bat to deploy the fix + the new --tp-debit flag.

Run on Windows (leave the window open):
  # against the pinned fixed build (no install.bat needed):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_tp_sweep.ps1 -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll'
  # or against the installed binary after install.bat:
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_tp_sweep.ps1
Watch progress in another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\tp-sweep-*\sweep.log" -Wait -Tail 20

RUNTIME (measured): DC is management-bound (many open positions repriced each day), so ScanStride barely helps.
~0.2 min per trading day + ~1.5 min startup => ~80 min/cell over the full 2025-01-02..now window. The 21-cell
grid is therefore ~28h — NOT a single overnight. To fit one night (~10h) either shorten the window (a ~6-month
window is ~28 min/cell => 21 cells ~10h) or trim the grid to the informative low range (off,5..50 = 11 cells),
then confirm the winner on the full window. The results.csv is written incrementally, so a long run can be
Ctrl-C'd and the remaining cells resumed with -TpDebits <the rest>.

PHASE 2 — after the --lots 1 sweep picks a winner N, re-run it WITH COMPOUNDING at the real balance to see the
equity curve + drawdown (sizing-neutral PF doesn't capture cash-starvation or compounding). One run, no --lots:
  wa ai backtest SPY --strategy DC --tp-debit N --tp 1.0 --starting-cash 50000 --show-fills --book-cmd

Customize:
  -TpDebits    Grid of take-profit %-of-debit values. Default: off + 5..100 step 5 (21 cells).
  -Since/-Until  Backtest window. Default: 2025-01-02 .. yesterday.
  -Ticker      Underlying. Default: SPY.
  -Lots        Contracts per trade. Default: 1 (sizing-neutral).
  -ScanStride  Open-scan minute stride. Default: 1. (Little effect on DC runtime; keep at 1 for fidelity.)
  -Wa          Path to wa.exe OR wa.dll (dll is run via -Dotnet). Default: installed wa on PATH / %LOCALAPPDATA%.
  -Dotnet      dotnet executable for running a .dll -Wa. Default: 'dotnet'.
  -RunId       Override the run-folder name.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). No strategy-layer files are created (the axis is a CLI
override), so the sweep reads only the frozen DC config; it is unaffected by the pinned binary being rebuilt.
#>

param(
  # Comma-separated string (NOT [double[]]): a [double[]] param binds "0,10,20" through the current culture,
  # where a comma is a thousands separator, collapsing it to the single number 1020 (and "0,10" -> 10). Parsing
  # the string ourselves with InvariantCulture is the only culture-safe way to accept a comma grid on the CLI.
  [string]$TpDebits = '0,5,10,15,20,25,30,35,40,45,50,55,60,65,70,75,80,85,90,95,100',
  [string]$Since = '2025-01-02',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [string]$Strategy = 'DC',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [string]$Wa = '',
  [string]$Dotnet = 'dotnet',
  [string]$RunId = ('tp-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# Parse the comma grid culture-safely (see the param comment).
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$Grid = $TpDebits -split '\s*,\s*' | Where-Object { $_ -ne '' } | ForEach-Object { [double]::Parse($_, $inv) }
if (-not $Grid -or @($Grid).Count -eq 0) { Write-Host "FATAL: -TpDebits parsed to an empty grid: '$TpDebits'"; exit 1 }
$Grid = @($Grid)

# Resolve wa: explicit -Wa wins; else PATH; else installed AppData binary.
if (-not $Wa) {
  $cmd = Get-Command wa -ErrorAction SilentlyContinue
  if ($cmd) { $Wa = $cmd.Source }
}
if (-not $Wa) {
  $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'
  if (Test-Path $candidate) { $Wa = $candidate }
}
if (-not $Wa -or -not (Test-Path $Wa)) {
  Write-Host "FATAL: wa binary not found. Pass -Wa 'C:\dev\WebullAnalytics\.sweep-bin\wa.dll' or install wa first."
  exit 1
}
$UseDotnet = $Wa.ToLower().EndsWith('.dll')

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Parse a fills.jsonl produced by `wa ai backtest --fills-jsonl`. Per-lineage P&L = sum(net) - sum(fees) over
# all fills with that lineage id. Total P&L = sum across lineages. Win rate = fraction of lineages > 0. Profit
# factor = gross wins / gross losses (the key cost-aware edge metric). Identical to backtest_sweep.ps1.
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0 }
  if (-not (Test-Path $Path)) { return $empty }

  $lineagePnl = @{}
  $totalFees = 0.0

  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0 }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $totalFees += [double]$f.fees
  }

  $trades = $lineagePnl.Count
  if ($trades -eq 0) { return $empty }

  $wins   = @($lineagePnl.Values | Where-Object { $_ -gt 0 }).Count
  $losses = @($lineagePnl.Values | Where-Object { $_ -le 0 }).Count
  $total  = ($lineagePnl.Values | Measure-Object -Sum).Sum
  $best   = ($lineagePnl.Values | Measure-Object -Maximum).Maximum
  $worst  = ($lineagePnl.Values | Measure-Object -Minimum).Minimum
  $grossWin  = (($lineagePnl.Values | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($lineagePnl.Values | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }

  return [ordered]@{
    Trades   = $trades
    Wins     = $wins
    Losses   = $losses
    WinRate  = [math]::Round($wins / $trades, 3)
    ProfitFactor = $pf
    TotalPnl = [math]::Round($total, 2)
    AvgPnl   = [math]::Round($total / $trades, 2)
    BestPnl  = [math]::Round($best, 2)
    WorstPnl = [math]::Round($worst, 2)
    TotalFees = [math]::Round($totalFees, 2)
  }
}

Log "=== take-profit (% of debit) backtest sweep ==="
Log ("wa: {0}{1}" -f $Wa, $(if ($UseDotnet) { " (via $Dotnet)" } else { "" }))
Log "ticker=$Ticker strategy=$Strategy since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log "grid: tpDebit=[$($Grid -join ', ')]  (Target B held OFF via --tp 1.0)"
Log "run dir: $RunDir"

$total = $Grid.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($td in $Grid) {
  $idx++
  $tag = "tp{0:000}" -f [int]$td
  $label = if ([int]$td -eq 0) { "off" } else { "+$([int]$td)%" }
  $fillsPath = Join-Path $RunDir ("fills_" + $tag + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $tag + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  Log ("[{0}/{1}] tpDebit={2} ({3}) -> running" -f $idx, $total, $td, $label)
  $args = @('ai','backtest',$Ticker,'--strategy',$Strategy,'--since',$Since,'--until',$Until,
            '--lots',$Lots,'--scan-stride',$ScanStride,'--tp-debit',$td,'--tp','1.0','--fills-jsonl',$fillsPath)
  if ($UseDotnet) { & $Dotnet $Wa @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  else            { & $Wa      @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  $rc = $LASTEXITCODE
  $sw.Stop()

  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    continue
  }

  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    TpDebit      = $td
    Trades       = $stats.Trades
    Wins         = $stats.Wins
    Losses       = $stats.Losses
    WinRate      = $stats.WinRate
    ProfitFactor = $stats.ProfitFactor
    TotalPnl     = $stats.TotalPnl
    AvgPnl       = $stats.AvgPnl
    BestPnl      = $stats.BestPnl
    WorstPnl     = $stats.WorstPnl
    TotalFees    = $stats.TotalFees
    Elapsed      = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> trades={0} wr={1:P0} PF={2} totalP&L={3:N2} avgP&L={4:N2} took={5}s" -f $row.Trades, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.AvgPnl, $row.Elapsed)

  # Write results incrementally so the CSV is usable mid-sweep if you Ctrl-C.
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

$baseline = $results | Where-Object { $_.TpDebit -eq 0 } | Select-Object -First 1
if ($baseline) {
  Write-Host ""
  Write-Host ("--- Baseline (take-profit off): PF={0} total={1:N2} avg={2:N2} trades={3} wr={4:P0} ---" -f $baseline.ProfitFactor, $baseline.TotalPnl, $baseline.AvgPnl, $baseline.Trades, $baseline.WinRate)
}

Write-Host ""
Write-Host "--- All cells by profit factor ---"
$results | Sort-Object -Property ProfitFactor -Descending | Format-Table TpDebit, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl -AutoSize
Write-Host ""
Write-Host "--- All cells by total P&L ---"
$results | Sort-Object -Property TotalPnl -Descending | Format-Table TpDebit, Trades, WinRate, ProfitFactor, TotalPnl, AvgPnl, WorstPnl -AutoSize
