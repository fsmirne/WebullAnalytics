<#
_run_gap_fill_then_sweep.ps1 - Unattended driver: waits for the in-progress 2022-11/12 wide-band backfill
(window A) to finish, then runs the remaining wide-band supplement pulls (B-F) SEQUENTIALLY, then launches
the comprehensive SV frequency sweep. Built so the whole multi-hour/multi-day queue runs from ONE invocation
with no further intervention needed between steps.

Windows (all --supplement --band 0.20 --tickers SPY:60 --concurrency 2, matching the two already confirmed to
land real data at this band width):
  A (already running when this script starts) 2022-11-01 .. 2022-12-31
  B  2023-07-01 .. 2023-12-31   (merges the light July gap with the dense Nov/Dec cluster)
  C  2024-01-01 .. 2024-03-31
  D  2024-10-01 .. 2024-10-31
  E  2025-09-01 .. 2026-01-31   (merges Sep/Oct/Nov/Dec/Jan)
  F  2026-04-01 .. 2026-04-30

Each step is logged to its own file under the repo root (backfill_<tag>.log) and the driver aborts the WHOLE
chain (does not proceed to the sweep) if any backfill step exits non-zero — a partial/failed gap-fill
shouldn't feed a multi-hour sweep silently. Re-run this script to resume: it skips any window whose log
already ends with "worker finished its pass cleanly".

After A-F, the driver runs a full 2022-2026 VERIFICATION backtest and checks the actual gap-warning counts
per targeted month against baseline — a backfill exiting cleanly does NOT mean the gap closed (proven: window
A's initial 0.20-band pass still left specific held-position strikes blind). If any month is still gappy, the
driver ESCALATES the strike band (0.35 -> 0.50 -> 0.65) and re-pulls JUST the still-gappy months, then
re-verifies — repeating up to that 3-round ladder before giving up and aborting for a human look.

Run on Windows (leave the window open, or run detached — see the launcher):
  powershell -ExecutionPolicy Bypass -File .\scripts\_run_gap_fill_then_sweep.ps1
#>

$ErrorActionPreference = 'Stop'
$RepoRoot = 'C:\dev\WebullAnalytics'
Set-Location $RepoRoot
$env:PYTHONUTF8 = '1'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path (Join-Path $RepoRoot 'gap_fill_queue.log') -Value $line
}

function Wait-ForNoPython {
  while (Get-Process -Name python -ErrorAction SilentlyContinue) {
    Start-Sleep -Seconds 20
  }
}

function Run-BackfillWindow {
  param([string]$Tag, [string]$Start, [string]$End)
  $logPath = Join-Path $RepoRoot "backfill_$Tag.log"
  if ((Test-Path $logPath) -and (Select-String -Path $logPath -Pattern 'worker finished its pass cleanly' -Quiet)) {
    Log "window $Tag ($Start..$End) already completed (found in $logPath) — skipping"
    return $true
  }
  Log "window $Tag ($Start..$End) -> starting"
  Wait-ForNoPython
  & python.exe "$RepoRoot\scripts\backfill_thetadata.py" --quotes --tickers SPY:60 `
    --start $Start --end $End --supplement --band 0.20 --concurrency 2 *>&1 | Tee-Object -FilePath $logPath
  Wait-ForNoPython
  $ok = Select-String -Path $logPath -Pattern 'worker finished its pass cleanly' -Quiet
  if ($ok) { Log "window $Tag -> completed cleanly" }
  else { Log "window $Tag -> DID NOT complete cleanly, see $logPath — aborting the chain" }
  return [bool]$ok
}

Log "=== gap-fill-then-sweep driver starting ==="

Log "waiting for window A (2022-11/12, already running) to finish..."
Wait-ForNoPython
$aOk = Select-String -Path (Join-Path $RepoRoot 'backfill_2022_wide.log') -Pattern 'worker finished its pass cleanly' -Quiet
if (-not $aOk) { Log "window A did not end cleanly — aborting the chain."; exit 1 }
Log "window A confirmed complete."

$windows = @(
  @{ Tag = '2023_wide'; Start = '2023-07-01'; End = '2023-12-31' },
  @{ Tag = '2024q1_wide'; Start = '2024-01-01'; End = '2024-03-31' },
  @{ Tag = '2024oct_wide'; Start = '2024-10-01'; End = '2024-10-31' },
  @{ Tag = '2025_2026jan_wide'; Start = '2025-09-01'; End = '2026-01-31' },
  @{ Tag = '2026apr_wide'; Start = '2026-04-01'; End = '2026-04-30' }
)

foreach ($w in $windows) {
  $ok = Run-BackfillWindow -Tag $w.Tag -Start $w.Start -End $w.End
  if (-not $ok) { Log "aborting chain after failed window $($w.Tag)"; exit 1 }
}

# A backfill script exiting cleanly does NOT mean the gap actually closed — window A's own default-band
# (0.10) pull "succeeded" and still left a held position blind (a band-width problem, not a missing-window
# one; see scripts/quote_coverage_gaps_2022_2026.md). So before spending days on a sweep, re-run the full
# 2022-2026 backtest and confirm the SPECIFIC windows we targeted actually show fewer blind-day warnings —
# not just that the pull commands returned 0. If any window is still gappy at 0.20, escalate the band and
# re-pull JUST the still-gappy months, then re-verify — repeat up to the escalation ladder below before
# giving up. This loop is what makes the gap-fill self-correcting instead of a single guess-and-hope pass.

# Baseline counts from the ORIGINAL pre-backfill full-history run (bt_2022_v2.txt), the same run that
# identified these windows in the first place.
$Baseline = [ordered]@{
  '2022-11' = 13; '2022-12' = 7
  '2023-07' = 1
  '2023-11' = 12; '2023-12' = 12
  '2024-01' = 2; '2024-02' = 1; '2024-03' = 1
  '2024-10' = 4
  '2025-09' = 1; '2025-10' = 9
  '2025-11' = 1; '2025-12' = 6
  '2026-01' = 1
  '2026-04' = 12
}
# Escalation ladder beyond the initial 0.20 supplement pulls (0.10 default -> 0.20 already applied above).
$EscalationBands = @(0.35, 0.50, 0.65)

function Run-Verification {
  Log "running the full 2022-2026 verification backtest..."
  $verifyFills = Join-Path $RepoRoot 'gap_fill_verify.jsonl'
  $verifyLog = Join-Path $RepoRoot 'gap_fill_verify.txt'
  Remove-Item -Path $verifyFills, $verifyLog -ErrorAction SilentlyContinue
  Wait-ForNoPython
  & "$RepoRoot\.sweep-bin\wa.exe" ai backtest SPY --lots 1 --since 2022-01-01 --strategy SV `
    --fills-jsonl $verifyFills *>&1 | Tee-Object -FilePath $verifyLog | Out-Null
  $warnLines = Select-String -Path $verifyLog -Pattern '^⚠ (\d{4}-\d{2})-\d{2}:' -AllMatches
  $counts = @{}
  foreach ($m in $warnLines) {
    $ym = $m.Matches[0].Groups[1].Value
    if (-not $counts.ContainsKey($ym)) { $counts[$ym] = 0 }
    $counts[$ym]++
  }
  return @{ Counts = $counts; Log = $verifyLog; Fills = $verifyFills }
}

# Groups a sorted list of 'YYYY-MM' strings into contiguous {Start;End} month ranges, so adjacent still-gappy
# months get ONE re-pull instead of N separate ones (same rationale as the original window consolidation).
function Group-ContiguousMonths {
  param([string[]]$Months)
  $sorted = $Months | Sort-Object
  $groups = @()
  $groupStart = $null; $prevDt = $null
  foreach ($ym in $sorted) {
    $dt = [DateTime]::ParseExact($ym + '-01', 'yyyy-MM-dd', $null)
    if ($null -eq $groupStart) { $groupStart = $dt }
    elseif ($dt -ne $prevDt.AddMonths(1)) {
      $groups += [PSCustomObject]@{ Start = $groupStart.ToString('yyyy-MM-01'); End = $prevDt.AddMonths(1).AddDays(-1).ToString('yyyy-MM-dd') }
      $groupStart = $dt
    }
    $prevDt = $dt
  }
  if ($null -ne $groupStart) { $groups += [PSCustomObject]@{ Start = $groupStart.ToString('yyyy-MM-01'); End = $prevDt.AddMonths(1).AddDays(-1).ToString('yyyy-MM-dd') } }
  return $groups
}

Log "=== all backfill windows complete — verifying the gaps actually closed before starting the sweep ==="
$allClosedEnough = $false
$round = 0
$currentBandDescription = '0.20 (initial supplement pass)'

while ($true) {
  $verify = Run-Verification
  Log ("gap-close verification round {0} (band so far: {1}) — baseline -> after:" -f $round, $currentBandDescription)
  $stillGappy = @()
  foreach ($ym in $Baseline.Keys) {
    $before = $Baseline[$ym]
    $after = if ($verify.Counts.ContainsKey($ym)) { $verify.Counts[$ym] } else { 0 }
    $threshold = [math]::Max(1, [math]::Round($before * 0.2))
    $closedEnough = $after -le $threshold
    if (-not $closedEnough) { $stillGappy += $ym }
    $flag = if ($closedEnough) { 'OK' } else { 'STILL GAPPY' }
    Log ("  {0}: {1} -> {2}  [{3}]" -f $ym, $before, $after, $flag)
  }

  if ($stillGappy.Count -eq 0) { $allClosedEnough = $true; break }

  if ($round -ge $EscalationBands.Count) {
    Log ("=== exhausted the escalation ladder ({0}) — still gappy: {1}. Aborting the chain before the sweep." -f (($EscalationBands | ForEach-Object { "$_" }) -join ', '), ($stillGappy -join ', '))
    Log ("    See {0} for the full warning list. This didn't respond to widening the strike band, so it's likely" -f $verify.Log)
    Log "    not a band-width problem for these specific months — needs a human look (genuine ThetaData"
    Log "    unavailability? a different root cause?) rather than another automatic retry."
    exit 1
  }

  $band = $EscalationBands[$round]
  $currentBandDescription = "$currentBandDescription -> $band"
  Log ("still gappy: {0} — escalating to band={1} and re-pulling just these months (round {2}/{3})" -f ($stillGappy -join ', '), $band, ($round + 1), $EscalationBands.Count)
  $retryRanges = Group-ContiguousMonths -Months $stillGappy
  foreach ($r in $retryRanges) {
    $tag = "retry_r{0}_{1}" -f ($round + 1), ($r.Start -replace '-', '')
    Log ("  re-pull {0}..{1} at band={2}" -f $r.Start, $r.End, $band)
    $logPath = Join-Path $RepoRoot "backfill_$tag.log"
    Wait-ForNoPython
    & python.exe "$RepoRoot\scripts\backfill_thetadata.py" --quotes --tickers SPY:60 `
      --start $r.Start --end $r.End --supplement --band $band --concurrency 2 *>&1 | Tee-Object -FilePath $logPath
    Wait-ForNoPython
    $ok = Select-String -Path $logPath -Pattern 'worker finished its pass cleanly' -Quiet
    if ($ok) { Log ("    -> completed cleanly") }
    else { Log ("    -> DID NOT complete cleanly, see {0} — aborting the chain" -f $logPath); exit 1 }
  }
  $round++
}

Log "all targeted windows closed enough (<=20% of baseline warning count) — proceeding to the sweep."

Log "=== launching SV frequency sweep STAGE 1 (stopLoss x deltaBand x takeProfit) ==="
$stage1RunId = 'sv-freq-sweep-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
& "$RepoRoot\scripts\backtest_sv_frequency_sweep.ps1" -RunId $stage1RunId -Wa "$RepoRoot\.sweep-bin\wa.dll" *>&1 |
  Tee-Object -FilePath (Join-Path $RepoRoot 'sv_frequency_sweep_stage1_driver.log')

$winnerPath = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$stage1RunId\winner.json"
if (-not (Test-Path $winnerPath)) {
  Log "stage 1 produced no winner.json ($winnerPath not found) — aborting before stage 2."
  exit 1
}
Log ("stage 1 winner: {0}" -f (Get-Content $winnerPath -Raw))

Log "=== launching SV frequency sweep STAGE 2 (coordinate-wise scorer-weight sweep) ==="
& "$RepoRoot\scripts\backtest_sv_weights_sweep.ps1" -WinnerJson $winnerPath -Wa "$RepoRoot\.sweep-bin\wa.dll" *>&1 |
  Tee-Object -FilePath (Join-Path $RepoRoot 'sv_frequency_sweep_stage2_driver.log')

Log "=== gap-fill-then-sweep driver finished (both stages complete) ==="
