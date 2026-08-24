<#
_confirm_candidates_full_history.ps1 - Runs a fixed list of candidate SV configs over the FULL 2022-01-01..now
window (--lots 1, sizing-neutral, matching how the original PF 2.99 baseline was established) — the point
being that every promising config found so far was discovered on the 2025-01-01..now sweep window, which is
calm and doesn't contain the 2023-H2 or 2025-H2 loss clusters. A config that only looks great on a stretch
with essentially zero independent adverse events (see: balanceRrExponent=0.5's "2 losses" being the same
correlated June-2025 cluster) isn't confirmed until it's been run against real stress periods too.

Candidates (edit the $Candidates array below to add/remove):
  baseline        - the live SV config, unmodified (SL off, delta cur, TP 75%) - the original reference point
  sl60_tp75       - Stage 1's actual best cell: same frequency as baseline, PF 4.87, P&L $9,438 (short window)
  sl40_tp75       - PF 6.01 on the short window, same frequency as baseline
  sl40_tp30_rr05  - the "wow" cell: SL40/TP30 (the frequency-optimized, WRONG base) + balanceRrExponent=0.5,
                    PF 6.25 on the short window, but its only 2 losses are one correlated event
  stage2_winner   - placeholder; the driver script fills this in dynamically from the corrected stage 2's
                    winner.json once that run completes (SL60/TP75 base + whatever weight change won)

Each cell writes its own temp strategy file, runs, and is deleted afterward. Runs SEQUENTIALLY.
#>

param(
  [string]$Since = '2022-01-01',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [string]$Wa = '',
  [string]$Dotnet = 'dotnet',
  [string]$Stage2WinnerJson = ''  # optional: path to corrected stage 2's winner.json, added as an extra candidate
)

$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot
if (-not $Wa) { $Wa = "$RepoRoot\.sweep-bin\wa.dll" }
$UseDotnet = $Wa.ToLower().EndsWith('.dll')

$DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
$BaseConfigPath = Join-Path $DataDir "ai-config.$Ticker.SV.json"
$BaseConfigRaw = Get-Content $BaseConfigPath -Raw

$RunDir = Join-Path $RepoRoot 'full_history_confirm'
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'confirm.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

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

function New-CellConfig {
  param([string]$Tag, [bool]$SlEnabled, [double]$SlPct, [double]$DeltaMin, [double]$DeltaMax, [double]$TpPct, [string]$WeightName, [double]$WeightValue)
  $cfg = $BaseConfigRaw | ConvertFrom-Json
  $cfg.rules.stopLoss.enabled = $SlEnabled
  if ($SlEnabled) { $cfg.rules.stopLoss.pctOfMaxLoss = $SlPct }
  $cfg.rules.takeProfit.profitTargetPctOfPremium = $TpPct
  $cfg.opener.structures.shortVertical.shortDeltaMin = $DeltaMin
  $cfg.opener.structures.shortVertical.shortDeltaMax = $DeltaMax
  $cfg.opener.structures.shortVertical.dteMin = 45
  $cfg.opener.structures.shortVertical.dteMax = 60
  if ($WeightName -eq 'balanceRrExponent') { $cfg.opener.balanceRrExponent = $WeightValue }
  elseif ($WeightName) { $cfg.opener.weights.$WeightName = $WeightValue }
  $path = Join-Path $DataDir "ai-config.$Ticker.$Tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $path -Encoding utf8
  return $path
}

$Candidates = @(
  [PSCustomObject]@{ Name = 'baseline';       SlEnabled = $false; SlPct = 0;    DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.75; WeightName = ''; WeightValue = 0 }
  [PSCustomObject]@{ Name = 'sl60_tp75';      SlEnabled = $true;  SlPct = 0.60; DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.75; WeightName = ''; WeightValue = 0 }
  [PSCustomObject]@{ Name = 'sl40_tp75';      SlEnabled = $true;  SlPct = 0.40; DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.75; WeightName = ''; WeightValue = 0 }
  [PSCustomObject]@{ Name = 'sl40_tp30_rr05'; SlEnabled = $true;  SlPct = 0.40; DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.30; WeightName = 'balanceRrExponent'; WeightValue = 0.5 }
  [PSCustomObject]@{ Name = 'sl60_tp75_rr05'; SlEnabled = $true;  SlPct = 0.60; DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.75; WeightName = 'balanceRrExponent'; WeightValue = 0.5 }
  [PSCustomObject]@{ Name = 'sl40_tp75_rr05'; SlEnabled = $true;  SlPct = 0.40; DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.75; WeightName = 'balanceRrExponent'; WeightValue = 0.5 }
)

if ($Stage2WinnerJson -and (Test-Path $Stage2WinnerJson)) {
  $w = Get-Content $Stage2WinnerJson -Raw | ConvertFrom-Json
  # stage 2's base is always SL60/cur/TP75 by construction (see backtest_sv_weights_sweep.ps1 -WinnerJson usage
  # in the driver); its Tag encodes which single weight (or 'baseline') won.
  if ($w.Weight -and $w.Weight -ne 'baseline') {
    $Candidates += [PSCustomObject]@{ Name = 'stage2_winner'; SlEnabled = $true; SlPct = 0.60; DeltaMin = 0.15; DeltaMax = 0.30; TpPct = 0.75; WeightName = $w.Weight; WeightValue = [double]$w.Value }
    Log ("added stage2_winner candidate: {0} = {1}" -f $w.Weight, $w.Value)
  } else {
    Log "stage 2's winner was the baseline weights (no single-weight change beat it) — not adding a separate stage2_winner candidate (sl60_tp75 already covers it)."
  }
}

Log "=== full-history (2022-2026) confirmation of top candidates ==="
Log ("candidates: {0}" -f (($Candidates | ForEach-Object { $_.Name }) -join ', '))

$results = New-Object System.Collections.ArrayList
foreach ($c in $Candidates) {
  $configPath = New-CellConfig -Tag "SVconfirm_$($c.Name)" -SlEnabled $c.SlEnabled -SlPct $c.SlPct `
    -DeltaMin $c.DeltaMin -DeltaMax $c.DeltaMax -TpPct $c.TpPct -WeightName $c.WeightName -WeightValue $c.WeightValue
  $fillsPath = Join-Path $RunDir ("fills_" + $c.Name + '.jsonl')
  $cellLog = Join-Path $RunDir ("run_" + $c.Name + '.log')
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  Log ("{0} -> running (full 2022-2026 window)" -f $c.Name)
  $args = @('ai','backtest',$Ticker,'--strategy',"SVconfirm_$($c.Name)",'--since',$Since,'--until',$Until,
            '--lots',1,'--fills-jsonl',$fillsPath)
  if ($UseDotnet) { & $Dotnet $Wa @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  else            { & $Wa      @args *>&1 | Tee-Object -FilePath $cellLog | Out-Null }
  $sw.Stop()
  Remove-Item -Path $configPath -ErrorAction SilentlyContinue

  $stats = Get-FillsStats -Path $fillsPath
  $realPricing = (Select-String -Path $cellLog -Pattern 'Real-bar pricing \(>0DTE legs\)\s+.*?(\d+\.\d)%' -AllMatches | Select-Object -Last 1)
  $row = [PSCustomObject]@{
    Name = $c.Name; DistinctPositions = $stats.DistinctPositions; Trades = $stats.Trades; Wins = $stats.Wins; Losses = $stats.Losses
    WinRate = $stats.WinRate; ProfitFactor = $stats.ProfitFactor; TotalPnl = $stats.TotalPnl; AvgPnl = $stats.AvgPnl
    Elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    SlEnabled = $c.SlEnabled; SlPct = $c.SlPct; DeltaMin = $c.DeltaMin; DeltaMax = $c.DeltaMax; TpPct = $c.TpPct
    WeightName = $c.WeightName; WeightValue = $c.WeightValue
  }
  [void]$results.Add($row)
  Log ("  -> positions={0} trades={1} wins={2} losses={3} wr={4:P0} PF={5} totalP&L={6:N2} took={7}s" -f `
    $row.DistinctPositions, $row.Trades, $row.Wins, $row.Losses, $row.WinRate, $row.ProfitFactor, $row.TotalPnl, $row.Elapsed)
  $results | Export-Csv -Path $ResultsCsv -NoTypeInformation -Force
}

Log "=== full-history confirmation complete ==="
Log "results: $ResultsCsv"
Log "compare against the ORIGINAL full-history baseline: 124 positions, PF 2.99, $16,831 total P&L (2022-01-01..2026-08-23, unmodified config)"
foreach ($r in $results) {
  Log ("  {0,-16} positions={1,4} PF={2,5} totalP&L={3,10} wins={4,3} losses={5,3}" -f $r.Name, $r.DistinctPositions, $r.ProfitFactor, $r.TotalPnl, $r.Wins, $r.Losses)
}

# Winner = highest TotalPnl among candidates (all of these were already pre-selected as promising, so no
# extra PF floor here). Written for phase 4 (minScoreToOpen validation) to consume directly.
$winner = $results | Sort-Object { [double]$_.TotalPnl } -Descending | Select-Object -First 1
if ($winner) {
  $winnerPath = Join-Path $RunDir 'winner.json'
  $winner | ConvertTo-Json | Set-Content -Path $winnerPath -Encoding utf8
  Log ("full-history winner written to {0}: {1} (totalP&L={2})" -f $winnerPath, $winner.Name, $winner.TotalPnl)
}
