<#
backtest_dc_sweep.ps1 - one-axis-at-a-time sweep of the SPY DC strategy.

Ranks the frozen DC baseline (ai-config.SPY.DC.json, PF 2.31 / t +4.36 in the
pre-registered campaign) against single-change variant layers. Each variant is a
verbatim copy of DC with exactly ONE knob moved, so every cell is directly
comparable to the baseline (the same discipline as the campaign's Phase 5).

Variants swept (layers ai-config.SPY.<name>.json, all already written to
%LOCALAPPDATA%\WebullAnalytics\data):
  DC          baseline (short 3-10 / long 21-30, cal+diag, delta 0.3-0.7)
  DCcalonly   longDiagonal disabled  (does dropping diagonals help? they may not clear costs)
  DCdiagonly  longCalendar disabled  (diagonal-only expected to collapse to calendar level)
  DCdte3045   back month pushed to 30-45 (front unchanged -> wider theta gap)
  DCdte5_15   front 5-15 / back 30-45 (QuickDC's SHORTER bands already failed t<2)
  DCatm       delta band tightened to ATM 0.4-0.6 (ITM-forcing already lost)
  DCvolfit1   volatilityFit weight 1.0 (Phase 5 hinted it halved DD)
  DCdirfit05  directionalFit weight 0.5

Ranking is SIZING-NEUTRAL (--lots 1): terminal P&L is the additive sum of
per-trade results, so PF / t / avg measure per-trade EDGE, not a compounding
curve. Account size does NOT change this ranking -- pick the winner here, then
confirm its drawdown at the real balance (see PHASE 2 at the bottom).

Gate reference (from the pre-registered campaign, do not move the goalposts):
clean PF >= 1.2 AND per-trade t >= 2.0 AND >= 95% real-priced. A variant is only
interesting if it beats DC on PF *without* collapsing t -- and even then it is a
HYPOTHESIS needing fresh confirmation on QQQ before it can go live.

Run (leave the window open; runs sequentially -- concurrent backtests contend on
quotes.db, see the no-parallel-backtests rule):
  powershell -ExecutionPolicy Bypass -File .\scripts\backtest_dc_sweep.ps1
Watch progress from another window:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\dc-sweep-*\sweep.log" -Wait -Tail 20

RUNTIME: full window at stride 1 is ~13-37 min per cell (campaign-recalibrated).
8 cells => ~2-5 h. For a fast first read raise -ScanStride (same stride on every
cell keeps the ranking fair); confirm the winner at stride 1 before believing it.

Reads/writes PROD data (%LOCALAPPDATA%\WebullAnalytics). Each run creates a fresh
sweep folder.
#>

param(
  [string]$Since = '2025-01-02',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [int]$Lots = 1,
  [int]$ScanStride = 1,
  [string[]]$Strategies = @('DC', 'DCcalonly', 'DCdiagonly', 'DCdte3045', 'DCdte5_15', 'DCatm', 'DCvolfit1', 'DCdirfit05'),
  [string]$RunId = ('dc-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'

# When invoked as `powershell.exe -File ... -Strategies a,b,c` from an external shell, the comma list
# arrives as ONE string element, not an array. Split it back so callers can pass either form.
if ($Strategies.Count -eq 1 -and $Strategies[0] -match ',') { $Strategies = $Strategies[0] -split '\s*,\s*' }

# Resolve installed wa: PATH first, then AppData fallback -- runs against the
# deployed production binary, not a stale dev build.
$Wa = $null
$cmd = Get-Command wa -ErrorAction SilentlyContinue
if ($cmd) { $Wa = $cmd.Source }
if (-not $Wa) {
  $candidate = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'
  if (Test-Path $candidate) { $Wa = $candidate }
}
if (-not $Wa) {
  Write-Host "FATAL: 'wa' not found on PATH or in %LOCALAPPDATA%\WebullAnalytics. Install it first."
  exit 1
}

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Parse a fills.jsonl produced by `wa ai backtest --fills-jsonl`. Per-lineage P&L
# = sum(net) - sum(fees) over all fills sharing a lineageId. Win rate = fraction
# of CLOSED lineages with positive P&L. Profit factor = gross wins / gross losses
# (the cost-aware edge metric). TStat = per-trade one-sample t on the lineage-P&L
# array (mean * sqrt(n) / sd) -- the campaign's significance gate (t >= 2).
#
# CRITICAL: only CLOSED lineages count. A lineage still open at --until has only
# an Open fill (a pure debit) and would masquerade as a full loss. Because the
# longer-DTE variants leave MORE positions open at the window end, counting them
# would bias the DTE axis -- exactly the axis under test. Excluding open-only
# lineages reproduces the backtest's own "closed lifecycles" numbers exactly
# (validated: PF/win/expectancy match to the cent). Assumes ~100% real-priced
# provenance (true for SPY/QQQ DC); if a variant reports low provenance, trust
# the backtest's own clean-trade line over this reparse.
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; OpenExcluded = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TStat = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0 }
  if (-not (Test-Path $Path)) { return $empty }

  $lineagePnl = @{}
  $lineageFees = @{}
  $lineageClosed = @{}

  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageFees[$lid] = 0.0; $lineageClosed[$lid] = $false }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $lineageFees[$lid] += [double]$f.fees
    if ([string]$f.kind -ne 'Open') { $lineageClosed[$lid] = $true }   # any non-Open fill => settled
  }

  # Keep only settled lineages; open-only ones are unclosed at --until, not losses.
  $closedIds = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] })
  $openExcluded = $lineagePnl.Count - $closedIds.Count

  $trades = $closedIds.Count
  if ($trades -eq 0) { $empty.OpenExcluded = $openExcluded; return $empty }

  $vals     = @($closedIds | ForEach-Object { $lineagePnl[$_] })
  $totalFees = ($closedIds | ForEach-Object { $lineageFees[$_] } | Measure-Object -Sum).Sum
  $wins   = @($vals | Where-Object { $_ -gt 0 }).Count
  $losses = @($vals | Where-Object { $_ -le 0 }).Count
  $total  = ($vals | Measure-Object -Sum).Sum
  $best   = ($vals | Measure-Object -Maximum).Maximum
  $worst  = ($vals | Measure-Object -Minimum).Minimum
  $grossWin  = (($vals | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($vals | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }

  $mean = $total / $trades
  $t = 0.0
  if ($trades -gt 1) {
    $ss = ($vals | ForEach-Object { ($_ - $mean) * ($_ - $mean) } | Measure-Object -Sum).Sum
    $sd = [math]::Sqrt($ss / ($trades - 1))
    if ($sd -gt 0) { $t = [math]::Round($mean * [math]::Sqrt($trades) / $sd, 2) }
  }

  return [ordered]@{
    Trades   = $trades
    OpenExcluded = $openExcluded
    Wins     = $wins
    Losses   = $losses
    WinRate  = [math]::Round($wins / $trades, 3)
    ProfitFactor = $pf
    TStat    = $t
    TotalPnl = [math]::Round($total, 2)
    AvgPnl   = [math]::Round($total / $trades, 2)
    BestPnl  = [math]::Round($best, 2)
    WorstPnl = [math]::Round($worst, 2)
    TotalFees = [math]::Round($totalFees, 2)
  }
}

Log "=== SPY DC one-axis sweep ==="
Log "wa: $Wa"
Log "ticker=$Ticker since=$Since until=$Until lots=$Lots scanStride=$ScanStride"
Log "strategies: $($Strategies -join ', ')"
Log "run dir: $RunDir"

$total = $Strategies.Count
$idx = 0
$results = New-Object System.Collections.ArrayList

foreach ($s in $Strategies) {
  $idx++
  $fillsPath = Join-Path $RunDir ("fills_" + $s + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $s + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  Log ("[{0}/{1}] {2} -> running" -f $idx, $total, $s)
  & $Wa ai backtest $Ticker --strategy $s --since $Since --until $Until --lots $Lots --scan-stride $ScanStride `
    --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $cellLog | Out-Null

  $rc = $LASTEXITCODE
  $sw.Stop()
  if ($rc -ne 0) {
    $tailLines = (Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '
    Log ("  -> rc={0} (skipping stats). last output: {1}" -f $rc, $tailLines)
    continue
  }

  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    Strategy     = $s
    Trades       = $stats.Trades
    OpenExcluded = $stats.OpenExcluded
    Wins         = $stats.Wins
    Losses       = $stats.Losses
    WinRate      = $stats.WinRate
    ProfitFactor = $stats.ProfitFactor
    TStat        = $stats.TStat
    TotalPnl     = $stats.TotalPnl
    AvgPnl       = $stats.AvgPnl
    BestPnl      = $stats.BestPnl
    WorstPnl     = $stats.WorstPnl
    TotalFees    = $stats.TotalFees
    Elapsed      = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$results.Add($row)
  Log ("  -> closed={0} openExcl={1} wr={2:P0} PF={3} t={4} totalP&L={5:N2} avgP&L={6:N2} took={7}s" -f $row.Trades, $row.OpenExcluded, $row.WinRate, $row.ProfitFactor, $row.TStat, $row.TotalPnl, $row.AvgPnl, $row.Elapsed)

  # Write incrementally so the CSV is usable mid-sweep if you Ctrl-C.
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== Sweep complete ==="
Log "results: $ResultsCsv"

$baseline = $results | Where-Object { $_.Strategy -eq 'DC' } | Select-Object -First 1
if ($baseline) {
  Write-Host ""
  Write-Host ("--- Baseline DC: PF={0} t={1} total={2:N2} avg={3:N2} closed={4} wr={5:P0} ---" -f $baseline.ProfitFactor, $baseline.TStat, $baseline.TotalPnl, $baseline.AvgPnl, $baseline.Trades, $baseline.WinRate)
}

Write-Host ""
Write-Host "--- All variants by profit factor (baseline = DC) ---"
$results | Sort-Object -Property ProfitFactor -Descending | Format-Table Strategy, Trades, OpenExcluded, WinRate, ProfitFactor, TStat, TotalPnl, AvgPnl, WorstPnl -AutoSize
Write-Host ""
Write-Host "--- All variants by per-trade t (significance gate t >= 2) ---"
$results | Sort-Object -Property TStat -Descending | Format-Table Strategy, Trades, OpenExcluded, WinRate, ProfitFactor, TStat, TotalPnl, AvgPnl, WorstPnl -AutoSize

Write-Host ""
Write-Host "PHASE 2 (do NOT skip): a variant that tops PF here is a HYPOTHESIS, not a"
Write-Host "result. Before believing it, for the top 1-2 variants run:"
Write-Host "  # $50k equity curve + real drawdown at the live sizing caps:"
Write-Host "  wa ai backtest SPY --strategy <winner> --since $Since --until $Until --starting-cash 50000"
Write-Host "  # cross-vehicle confirm (QQQ is the ONLY tradeable alternative; Webull blocks index calendars):"
Write-Host "  wa ai backtest QQQ --strategy <winner> --since $Since --until $Until --lots 1 --scan-stride 1"
Write-Host "A winner must not collapse t, must hold DD within tolerance at 50k, and must"
Write-Host "keep same-sign expectancy on QQQ. Otherwise the frozen DC config stays."
