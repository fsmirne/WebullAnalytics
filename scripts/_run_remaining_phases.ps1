<#
_run_remaining_phases.ps1 - Chains the phases queued after the (wrong-base) stage 2 run finishes:
  1. Compounding comparison: SL40/TP75 vs SL60/TP75 at --starting-cash 50000, real sizing (no --lots)
  2. Corrected Stage 2: coordinate-wise weight sweep with SL60/cur/TP75 as the base (not the frequency-
     optimized SL40/TP30 the first stage-2 run used)
  3. Full 2022-2026 confirmation of the best candidates found so far, including corrected stage 2's winner
  4. minScoreToOpen validation: re-runs phase 3's actual winning config with --min-score-to-open 0 instead
     of the live config's -10 (fully permissive) floor, over the same full 2022-2026 window, to check
     whether -10 is doing real work or 0 (only ever opening positive-EV-scored candidates) is just as good.
     A quick pre-check on the existing full-history baseline found 7/160 opens (4.4%) had a negative score,
     and all 6 of those distinct positions were winners (+$1,846 total) — informative but too small a
     sample (6 trades) to conclude from directly, hence this controlled same-config A/B instead.

Waits for the currently-running (wrong-base) stage 2 driver to finish before starting, so nothing overlaps
on quotes.db.
#>

$ErrorActionPreference = 'Stop'
$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path (Join-Path $RepoRoot 'remaining_phases.log') -Value $line
}

function Wait-ForNoBacktestProcess {
  while ((Get-Process -Name dotnet -ErrorAction SilentlyContinue) -or (Get-Process -Name wa -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 20
  }
}

Log "=== remaining-phases driver starting ==="
Log "waiting for the in-progress (wrong-base) stage 2 sweep to finish..."
Wait-ForNoBacktestProcess
Log "no backtest process running — proceeding."

Log "=== phase 1: compounding comparison (SL40/TP75 vs SL60/TP75, \$50000 real sizing) ==="
& "$RepoRoot\scripts\_compare_compounded.ps1" *>&1 | Tee-Object -FilePath (Join-Path $RepoRoot 'phase1_compounded.log')

Log "=== phase 2: corrected stage 2 (weight sweep on SL60/cur/TP75 base) ==="
$stage2bRunId = 'sv-weights-sweep-corrected-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
& "$RepoRoot\scripts\backtest_sv_weights_sweep.ps1" -RunId $stage2bRunId -BaseSlPct 60 -BaseDelta cur -BaseTp 75 `
  -Wa "$RepoRoot\.sweep-bin\wa.dll" *>&1 | Tee-Object -FilePath (Join-Path $RepoRoot 'phase2_stage2_corrected.log')

$stage2bWinnerPath = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$stage2bRunId\winner.json"
if (Test-Path $stage2bWinnerPath) {
  Log ("corrected stage 2 winner: {0}" -f (Get-Content $stage2bWinnerPath -Raw))
} else {
  Log "corrected stage 2 produced no winner.json — continuing to phase 3 without a stage2_winner candidate."
  $stage2bWinnerPath = ''
}

Log "=== phase 3: full 2022-2026 confirmation of top candidates ==="
& "$RepoRoot\scripts\_confirm_candidates_full_history.ps1" -Stage2WinnerJson $stage2bWinnerPath *>&1 |
  Tee-Object -FilePath (Join-Path $RepoRoot 'phase3_full_history_confirm.log')

Log "=== phase 4: minScoreToOpen validation (0 vs the live config's -10) on phase 3's actual winner ==="
$phase3WinnerPath = Join-Path $RepoRoot 'full_history_confirm\winner.json'
if (-not (Test-Path $phase3WinnerPath)) {
  Log "phase 3 produced no winner.json — skipping phase 4."
} else {
  $w = Get-Content $phase3WinnerPath -Raw | ConvertFrom-Json
  Log ("phase 3 winner: {0} (-10 results: trades={1} PF={2} totalP&L={3})" -f $w.Name, $w.Trades, $w.ProfitFactor, $w.TotalPnl)

  $DataDir = Join-Path $env:LOCALAPPDATA 'WebullAnalytics\data'
  $BaseConfigPath = Join-Path $DataDir 'ai-config.SPY.SV.json'
  $cfg = (Get-Content $BaseConfigPath -Raw) | ConvertFrom-Json
  $slEnabled = [bool]$w.SlEnabled
  $cfg.rules.stopLoss.enabled = $slEnabled
  if ($slEnabled) { $cfg.rules.stopLoss.pctOfMaxLoss = [double]$w.SlPct }
  $cfg.rules.takeProfit.profitTargetPctOfPremium = [double]$w.TpPct
  $cfg.opener.structures.shortVertical.shortDeltaMin = [double]$w.DeltaMin
  $cfg.opener.structures.shortVertical.shortDeltaMax = [double]$w.DeltaMax
  $cfg.opener.structures.shortVertical.dteMin = 45
  $cfg.opener.structures.shortVertical.dteMax = 60
  if ($w.WeightName -and $w.WeightName -ne '') {
    if ($w.WeightName -eq 'balanceRrExponent') { $cfg.opener.balanceRrExponent = [double]$w.WeightValue }
    else { $cfg.opener.weights.($w.WeightName) = [double]$w.WeightValue }
  }
  $tag = 'SVconfirm_phase3winner_msto0'
  $configPath = Join-Path $DataDir "ai-config.SPY.$tag.json"
  ($cfg | ConvertTo-Json -Depth 20) | Set-Content -Path $configPath -Encoding utf8

  $fillsPath = Join-Path $RepoRoot 'phase4_msto0.jsonl'
  $cellLog = Join-Path $RepoRoot 'phase4_msto0.txt'
  Remove-Item $fillsPath, $cellLog -ErrorAction SilentlyContinue
  Log "running phase 3's winner with --min-score-to-open 0 over the full 2022-2026 window..."
  & dotnet "$RepoRoot\.sweep-bin\wa.dll" ai backtest SPY --lots 1 --since 2022-01-01 --strategy $tag `
    --min-score-to-open 0 --fills-jsonl $fillsPath *>&1 | Tee-Object -FilePath $cellLog | Out-Null
  Remove-Item $configPath -ErrorAction SilentlyContinue

  # Same closed-lifecycles PF/P&L parse as the other scripts.
  $lineagePnl = @{}; $lineageClosed = @{}
  if (Test-Path $fillsPath) {
    Get-Content $fillsPath | ForEach-Object {
      if ([string]::IsNullOrWhiteSpace($_)) { return }
      $f = $_ | ConvertFrom-Json
      $lid = [string]$f.lineage
      if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageClosed[$lid] = $false }
      $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
      if ($f.kind -eq 'Close' -or $f.kind -eq 'Expire') { $lineageClosed[$lid] = $true }
    }
  }
  $closedPnl = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] } | ForEach-Object { $lineagePnl[$_] })
  $trades0 = $closedPnl.Count
  $grossWin0 = (($closedPnl | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss0 = (($closedPnl | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
  $pf0 = if ($grossLoss0 -and $grossLoss0 -ne 0) { [math]::Round($grossWin0 / [math]::Abs($grossLoss0), 2) } elseif ($grossWin0 -gt 0) { [double]::PositiveInfinity } else { 0.0 }
  $total0 = ($closedPnl | Measure-Object -Sum).Sum

  Log ("=== minScoreToOpen comparison for {0} ===" -f $w.Name)
  Log ("  -10 (live default): trades={0} PF={1} totalP&L={2}" -f $w.Trades, $w.ProfitFactor, $w.TotalPnl)
  Log ("  0   (positive-EV only): trades={0} PF={1} totalP&L={2}" -f $trades0, $pf0, [math]::Round($total0, 2))
  $verdict = if ([math]::Round($total0,2) -ge [double]$w.TotalPnl) { "0 is at least as good -> minScoreToOpen can safely be raised to 0" } else { "-10 outperforms 0 on this config -> the negative-score opens are net contributors, keep -10" }
  Log ("  verdict: $verdict")
}

Log "=== phase 5: compounded confirmation of ALL phase 3 candidates (not just the sizing-neutral winner) ==="
Log "justification: phase 1 showed SL40/TP75 beat SL60/TP75 under compounding despite LOSING on sizing-neutral total P&L -- the sizing-neutral ranking and the real-money ranking are not guaranteed to match, so every phase-3 candidate gets re-checked here."
$phase3ResultsCsv = Join-Path $RepoRoot 'full_history_confirm\results.csv'
if (-not (Test-Path $phase3ResultsCsv)) {
  Log "phase 3 produced no results.csv -- skipping phase 5."
} else {
  & "$RepoRoot\scripts\_compound_confirm_candidates.ps1" -ResultsCsvPath $phase3ResultsCsv *>&1 |
    Tee-Object -FilePath (Join-Path $RepoRoot 'phase5_compound_all_candidates.log')
}

Log "=== remaining-phases driver finished (compounding comparison + corrected stage 2 + full-history confirmation + minScoreToOpen validation + all-candidate compounded confirmation all complete) ==="
