<#
backtest_entrytime_sweep.ps1 - does delaying the opener's first entry beat opening at the first tick?

Each cell is "label|openAfter|cross": --open-after HH:mm withholds opens until that ET time ('' = 09:30 open), --cross N fills each leg N of its
half-spread past mid ('' = config fill model). Every cell passes --open-before 16:00 so the strategy's own latestEntryTimeEt cutoff (DC2: 11:00)
can't block late cells or differ between cells.

Sizing-neutral (--lots 1) so P&L is per-trade edge, not compounding. Runs SEQUENTIALLY (concurrent backtests contend on quotes.db). Keeps
Windows awake while running. Don't run install.bat while it's live (a mid-run binary swap crashes the summary).

Run on Windows with pwsh 7:
  pwsh -File .\scripts\backtest_entrytime_sweep.ps1
Watch progress:
  Get-Content "$env:LOCALAPPDATA\WebullAnalytics\sweeps\entrytime-*\sweep.log" -Wait -Tail 20
#>

param(
  # Comma-separated string, not [string[]]: `pwsh -File` hands an array argument over as a single string.
  [string]$Cells = 'base||,oa1100|11:00|,oa0945|09:45|,oa1300|13:00|,oa1015|10:15|,base_x25||0.25,oa1015_x25|10:15|0.25',
  [string]$Since = '2022-01-03',
  [string]$Until = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'),
  [string]$Ticker = 'SPY',
  [string]$Strategy = 'DC2',
  [int]$Lots = 1,
  [string]$Wa = (Join-Path $env:LOCALAPPDATA 'WebullAnalytics\wa.exe'),
  [string]$RunId = ('entrytime-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'SweepCommon.ps1')

if (-not (Test-Path $Wa)) { Write-Host "FATAL: wa not found at $Wa"; exit 1 }
$CellList = @($Cells -split '\s*,\s*' | Where-Object { $_ })

# ES_CONTINUOUS | ES_SYSTEM_REQUIRED: keep the machine from sleeping until this process exits.
Add-Type -Namespace Win32 -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[void][Win32.Power]::SetThreadExecutionState([uint32]'0x80000001')

$RunDir = Join-Path $env:LOCALAPPDATA "WebullAnalytics\sweeps\$RunId"
New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
$Log = Join-Path $RunDir 'sweep.log'
$ResultsCsv = Join-Path $RunDir 'results.csv'

function Log($msg) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  Write-Host $line
  Add-Content -Path $Log -Value $line
}

# Entry-time distribution of the Open fills: median entry clock time and the share opened in the first 5 minutes of the cell's window.
function Get-EntryTimeStats {
  param([string]$Path, [string]$OpenAfter)

  $opens = @(Get-Content $Path | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.kind -eq 'Open' } | ForEach-Object { ([datetime]$_.ts).TimeOfDay })
  if ($opens.Count -eq 0) { return [ordered]@{ Opens = 0; MedianEntry = ''; FirstFivePct = 0.0 } }
  $start = if ($OpenAfter) { [timespan]::Parse($OpenAfter) } else { [timespan]::Parse('09:30') }
  $sorted = $opens | Sort-Object
  $early = @($opens | Where-Object { $_ -lt $start.Add([timespan]::FromMinutes(5)) }).Count
  return [ordered]@{ Opens = $opens.Count; MedianEntry = $sorted[[int][math]::Floor($sorted.Count / 2)].ToString('hh\:mm'); FirstFivePct = [math]::Round($early / $opens.Count, 3) }
}

Log "=== entry-time sweep ==="
Log "wa=$Wa ticker=$Ticker strategy=$Strategy since=$Since until=$Until lots=$Lots"
Log "cells: $($CellList -join '  ')"
Log "run dir: $RunDir"

$idx = 0
foreach ($cell in $CellList) {
  $idx++
  $label, $openAfter, $cross = $cell -split '\|', 3
  $fillsPath = Join-Path $RunDir "fills_$label.jsonl"
  $cellLog = Join-Path $RunDir "run_$label.log"

  $waArgs = @('ai', 'backtest', $Ticker, '--strategy', $Strategy, '--since', $Since, '--until', $Until, '--lots', $Lots, '--open-before', '16:00', '--fills-jsonl', $fillsPath)
  if ($openAfter) { $waArgs += @('--open-after', $openAfter) }
  if ($cross) { $waArgs += @('--cross', $cross) }

  Log ("[{0}/{1}] {2} -> {3}" -f $idx, $CellList.Count, $label, ($waArgs -join ' '))
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  & $Wa @waArgs *>&1 | Tee-Object -FilePath $cellLog | Out-Null
  $rc = $LASTEXITCODE
  $sw.Stop()

  if ($rc -ne 0) {
    Log ("  -> rc={0} after {1:N1} min. last output: {2}" -f $rc, $sw.Elapsed.TotalMinutes, ((Get-Content $cellLog -Tail 6 -ErrorAction SilentlyContinue) -join ' | '))
    continue
  }

  $stats = Get-FillsStats -Path $fillsPath
  $entry = Get-EntryTimeStats -Path $fillsPath -OpenAfter $openAfter
  $row = [PSCustomObject]([ordered]@{ Label = $label; OpenAfter = $(if ($openAfter) { $openAfter } else { '09:30' }); Cross = $cross } + $stats + $entry + [ordered]@{ Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1) })
  $row | Export-Csv -Path $ResultsCsv -NoTypeInformation -Append
  Log ("  -> trades={0} PF={1} total=`${2} avg=`${3} WR={4} openAtEnd={5} medianEntry={6} first5min={7:P0} ({8:N1} min)" -f $stats.Trades, $stats.ProfitFactor, $stats.TotalPnl, $stats.AvgPnl, $stats.WinRate, $stats.OpenAtEnd, $entry.MedianEntry, $entry.FirstFivePct, $sw.Elapsed.TotalMinutes)
}

Log "=== done: $ResultsCsv ==="
