<#
SweepCommon.ps1 - shared helpers for the backtest sweep scripts. Dot-source it: . (Join-Path $PSScriptRoot 'SweepCommon.ps1')

Older sweep scripts still carry their own inline Get-FillsStats copies (they have drifted slightly); new sweeps use this one.
#>

# Parse a fills.jsonl produced by `wa ai backtest --fills-jsonl`. Per-lineage P&L = sum(net) - sum(fees) over
# all fills with that lineage id. CLOSED lifecycles only (a Close or Expire fill in the lineage): a position
# still open at --until has paid its debit but realized nothing, so counting it makes the debit look like a
# pure loss and drags PF/WR — non-uniformly across cells, since the swept knob changes hold duration and thus
# the open-at-until count. Matches the renderer's closed-lifecycle summary. OpenAtEnd reports what was excluded.
function Get-FillsStats {
  param([string]$Path)

  $empty = [ordered]@{ Trades = 0; Wins = 0; Losses = 0; WinRate = 0.0; ProfitFactor = 0.0; TotalPnl = 0.0; AvgPnl = 0.0; BestPnl = 0.0; WorstPnl = 0.0; TotalFees = 0.0; OpenAtEnd = 0 }
  if (-not (Test-Path $Path)) { return $empty }

  $lineagePnl = @{}
  $lineageClosed = @{}
  $totalFees = 0.0

  Get-Content $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { $f = $_ | ConvertFrom-Json } catch { return }
    $lid = [string]$f.lineage
    if (-not $lineagePnl.ContainsKey($lid)) { $lineagePnl[$lid] = 0.0; $lineageClosed[$lid] = $false }
    $lineagePnl[$lid] += ([double]$f.net - [double]$f.fees)
    $totalFees += [double]$f.fees
    if ($f.kind -eq 'Close' -or $f.kind -eq 'Expire') { $lineageClosed[$lid] = $true }
  }

  $closedPnl = @($lineagePnl.Keys | Where-Object { $lineageClosed[$_] } | ForEach-Object { $lineagePnl[$_] })
  $openAtEnd = $lineagePnl.Count - $closedPnl.Count
  $trades = $closedPnl.Count
  if ($trades -eq 0) { return $empty }

  $wins   = @($closedPnl | Where-Object { $_ -gt 0 }).Count
  $losses = @($closedPnl | Where-Object { $_ -le 0 }).Count
  $total  = ($closedPnl | Measure-Object -Sum).Sum
  $best   = ($closedPnl | Measure-Object -Maximum).Maximum
  $worst  = ($closedPnl | Measure-Object -Minimum).Minimum
  $grossWin  = (($closedPnl | Where-Object { $_ -gt 0 }) | Measure-Object -Sum).Sum
  $grossLoss = (($closedPnl | Where-Object { $_ -le 0 }) | Measure-Object -Sum).Sum
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
    OpenAtEnd = $openAtEnd
  }
}
