<#
_confirm_strategy_family_sweep.ps1 - Post-delta-band-fix (2026-08-25) re-validation of the SV/SV2 strategy
family, now widened to also cover the CompleteCondorRule variants and the ironCondor/ironButterfly opener
structures. Unlike _confirm_candidates_full_history.ps1 (which sweeps stopLoss/TP/weight PARAMETERS on top
of one structure), this script compares already-built, independent --strategy layers:
  SV       - live baseline: shortVertical, no stop-loss
  SV2      - live: shortVertical + stopLoss 40% of max loss
  testcc   - SV  + CompleteCondorRule (converts a winning single-sided vertical into an iron condor)
  testcc2  - SV2 + CompleteCondorRule
  testic   - shortVertical replaced by the ironCondor opener structure (45-60 DTE, narrowed widthSteps)
  testib   - shortVertical replaced by the ironButterfly opener structure (45-60 DTE, narrowed wingSteps)

Two lenses per candidate, matching the original campaign's Phase 3 / Phase 5 methodology:
  A) sizing-neutral, FULL 2022-2026 history, --lots 1  - isolates structural edge across a real stress
     cycle (2023-style regime included), uncontaminated by compounding.
  B) compounded, FRESH-START 2025-2026, $50k start, real sizing - the "what would my account actually do
     right now" answer for the current regime.

Runs against the PINNED .sweep-bin build (never the live install) so this sweep can't be perturbed by
-- and can't perturb -- whatever is currently installed for live trading.
#>

param(
  [string]$FreshSince = '2025-01-01',
  [string]$FullSince = '2022-01-01',
  [string]$Until = (Get-Date).ToString('yyyy-MM-dd'),
  [double]$StartingCash = 50000,
  [string]$Ticker = 'SPY',
  [string[]]$Candidates = @('SV', 'SV2', 'testcc', 'testcc2', 'testic', 'testib'),
  [string]$RunDirName = 'strategy_family_sweep_20260826'
)

$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot
$Wa = "$RepoRoot\.sweep-bin\wa.dll"
if (-not (Test-Path $Wa)) { Write-Host "FATAL: $Wa not found. Build it first: dotnet build WebullAnalytics.csproj -c Release -o .sweep-bin"; exit 1 }

$RunDir = Join-Path $RepoRoot $RunDirName
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$NeutralCsv = Join-Path $RunDir 'results_sizing_neutral.csv'
$CompoundCsv = Join-Path $RunDir 'results_compounded.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

function Get-FillsStats {
  param([string]$Path)
  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; DistinctPositions = 0 }
  if (-not (Test-Path $Path)) { return $empty }
  $lineagePnl = @{}; $lineageClosed = @{}
  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageClosed[$lid] = $false }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    if ($f.kind -eq 'Close' -or $f.kind -eq 'Expire') { $lineageClosed[$lid] = $true }
  }
  $distinctPositions = $lineagePnl.Count
  $closedPnl = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] } | ForEach-Object { $lineagePnl[$_] })
  $trades = $closedPnl.Count
  if ($trades -eq 0) { $empty.DistinctPositions = $distinctPositions; return $empty }
  $wins = @($closedPnl | Where-Object { $_ -gt 0 }).Count
  $losses = @($closedPnl | Where-Object { $_ -le 0 }).Count
  $total = ($closedPnl | Measure-Object -Sum).Sum
  $grossWin = (($closedPnl | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($closedPnl | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf = if ($grossLoss -ne 0) { [math]::Round($grossWin / [math]::Abs($grossLoss), 2) } else { [double]::PositiveInfinity }
  return [ordered]@{
    Trades = $trades; Wins = $wins; Losses = $losses; WinRate = [math]::Round($wins / $trades, 3)
    ProfitFactor = $pf; TotalPnl = [math]::Round($total, 2); AvgPnl = [math]::Round($total / $trades, 2)
    DistinctPositions = $distinctPositions
  }
}

function Get-CompoundedStats {
  param([string]$CellLog)
  $endingEquity = (Select-String -Path $CellLog -Pattern 'Ending equity\s+.*?\$([\d,\.]+)' | Select-Object -Last 1)
  $endingEquityVal = if ($endingEquity) { [double]($endingEquity.Matches[0].Groups[1].Value -replace ',', '') } else { $null }
  $maxDD = (Select-String -Path $CellLog -Pattern '([\d\.]+)%\s+worst' | Select-Object -Last 1)
  $maxDDVal = if ($maxDD) { [double]$maxDD.Matches[0].Groups[1].Value } else { $null }
  $pfLine = (Select-String -Path $CellLog -Pattern 'Profit factor\s+\S*\s+([\d\.]+|\S+)\s*\S*$' | Select-Object -Last 1)
  $pfVal = if ($pfLine) { $pfLine.Matches[0].Groups[1].Value } else { $null }
  $totalPnlLine = (Select-String -Path $CellLog -Pattern 'Total P&L\s+.*?\$?(-?[\d,\.]+)\s*\(' | Select-Object -Last 1)
  $totalPnlVal = if ($totalPnlLine) { [double]($totalPnlLine.Matches[0].Groups[1].Value -replace ',', '') } else { $null }
  return [ordered]@{ EndingEquity = $endingEquityVal; MaxDrawdownPct = $maxDDVal; ProfitFactor = $pfVal; TotalPnl = $totalPnlVal }
}

Log "=== strategy family sweep: $($Candidates -join ', ') ==="
Log "Phase A: sizing-neutral full-history ($FullSince..$Until, --lots 1)"
Log "Phase B: compounded fresh-start ($FreshSince..$Until, `$$StartingCash start)"

$neutralResults = New-Object System.Collections.ArrayList
$compoundResults = New-Object System.Collections.ArrayList

foreach ($name in $Candidates) {
  # --- Phase A: sizing-neutral, full history ---
  $fillsPath = Join-Path $RunDir ("fills_neutral_" + $name + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_neutral_" + $name + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  Log ("$name -> Phase A (sizing-neutral, full history) running")
  & dotnet $Wa ai backtest $Ticker --strategy $name --since $FullSince --until $Until --lots 1 --fills-jsonl $fillsPath *>&1 |
    Tee-Object -FilePath $cellLog | Out-Null
  $sw.Stop()
  $stats = Get-FillsStats -Path $fillsPath
  $row = [PSCustomObject]@{
    Name = $name; DistinctPositions = $stats.DistinctPositions; Trades = $stats.Trades; Wins = $stats.Wins; Losses = $stats.Losses
    WinRate = $stats.WinRate; ProfitFactor = $stats.ProfitFactor; TotalPnl = $stats.TotalPnl; AvgPnl = $stats.AvgPnl
    Elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
  [void]$neutralResults.Add($row)
  Log ("  Phase A -> positions={0} trades={1} wins={2} losses={3} wr={4:P0} PF={5} totalP&L={6:N2} took={7}s" -f `
    $row.DistinctPositions, $row.Trades, $row.Wins, $row.Losses, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.Elapsed)
  $neutralResults | Export-Csv -Path $NeutralCsv -NoTypeInformation -Force

  # --- Phase B: compounded, fresh-start ---
  $fillsPathB = Join-Path $RunDir ("fills_compound_" + $name + '.jsonl')
  $cellLogB = Join-Path $RunDir ("run_compound_" + $name + '.log')
  $swB = [System.Diagnostics.Stopwatch]::StartNew()
  Log ("$name -> Phase B (compounded, fresh-start) running")
  & dotnet $Wa ai backtest $Ticker --strategy $name --since $FreshSince --until $Until --starting-cash $StartingCash --fills-jsonl $fillsPathB *>&1 |
    Tee-Object -FilePath $cellLogB | Out-Null
  $swB.Stop()
  $cstats = Get-CompoundedStats -CellLog $cellLogB
  $rowB = [PSCustomObject]@{
    Name = $name; EndingEquity = $cstats.EndingEquity; MaxDrawdownPct = $cstats.MaxDrawdownPct
    ProfitFactor = $cstats.ProfitFactor; TotalPnl = $cstats.TotalPnl; Elapsed = [math]::Round($swB.Elapsed.TotalSeconds, 1)
  }
  [void]$compoundResults.Add($rowB)
  Log ("  Phase B -> endingEquity={0} maxDD={1}% PF={2} totalP&L={3} took={4}s" -f `
    $rowB.EndingEquity, $rowB.MaxDrawdownPct, $rowB.ProfitFactor, $rowB.TotalPnl, $rowB.Elapsed)
  $compoundResults | Export-Csv -Path $CompoundCsv -NoTypeInformation -Force
}

Log "=== sweep complete ==="
Log "sizing-neutral results: $NeutralCsv"
Log "compounded results:     $CompoundCsv"
Log ""
Log "--- sizing-neutral leaderboard (full history, PF desc) ---"
$neutralResults | Sort-Object { if ($_.ProfitFactor -eq [double]::PositiveInfinity) { 9999 } else { [double]$_.ProfitFactor } } -Descending | ForEach-Object {
  Log ("  {0,-10} positions={1,4} trades={2,4} PF={3,6} totalP&L={4,12:N2} wr={5:P0}" -f $_.Name, $_.DistinctPositions, $_.Trades, $_.ProfitFactor, $_.TotalPnl, $_.WinRate)
}
Log ""
Log "--- compounded leaderboard (fresh-start, ending equity desc) ---"
$compoundResults | Where-Object { $null -ne $_.EndingEquity } | Sort-Object { [double]$_.EndingEquity } -Descending | ForEach-Object {
  Log ("  {0,-10} endingEquity=`${1,12:N2} maxDD={2,5}% PF={3,6}" -f $_.Name, $_.EndingEquity, $_.MaxDrawdownPct, $_.ProfitFactor)
}
