<#
  Resident accoreconsole management - keep one drawing open in memory and send tasks over a named pipe.

  Usage:
    scripts\resident.ps1 -Action start  -Dwg <file.dwg> [-IdleSec 600]
    scripts\resident.ps1 -Action exec   -Dwg <file.dwg> -Ops 'list_alignments'
    scripts\resident.ps1 -Action exec   -Dwg <file.dwg> -Task task.json -Result result.json
    scripts\resident.ps1 -Action status -Dwg <file.dwg>
    scripts\resident.ps1 -Action reload -Dwg <file.dwg>     discard in-memory changes, reopen from disk
    scripts\resident.ps1 -Action stop   -Dwg <file.dwg>
    scripts\resident.ps1 -Action list

  civil3dfactory.ps1 -Resident takes the same route; the functions live in resident-lib.ps1.
#>
param(
  [ValidateSet("start", "stop", "reload", "status", "list", "exec", "ping")]
  [string]$Action = "status",
  [string]$Dwg,
  [string]$Task,
  [string]$Ops,
  [string]$Result,
  [int]$IdleSec = 600,
  [int]$StartTimeoutSec = 240,
  [int]$ConnectTimeoutSec = 30,
  [switch]$Reuse,
  [switch]$Quiet
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
. (Join-Path $PSScriptRoot "resident-lib.ps1")
$env:C3DF_ROOT = Split-Path -Parent $PSScriptRoot

switch ($Action) {
  "list" {
    $rows = @(Get-C3DFResidentList)
    if ($rows.Count -eq 0) { Write-Host "No resident is running." } else { $rows | Format-Table -AutoSize }
  }
  "start" {
    if (-not $Dwg) { throw "-Action start needs -Dwg" }
    $ctx = Start-C3DFResident -Dwg $Dwg -IdleSec $IdleSec -StartTimeoutSec $StartTimeoutSec -Quiet:$Quiet
    Get-C3DFResidentStatus -Dwg $ctx.Dwg -ConnectTimeoutSec $ConnectTimeoutSec | Format-List
  }
  "stop" {
    if (-not $Dwg) { throw "-Action stop needs -Dwg" }
    Stop-C3DFResident -Dwg $Dwg -ConnectTimeoutSec $ConnectTimeoutSec | Format-List
  }
  "reload" {
    if (-not $Dwg) { throw "-Action reload needs -Dwg" }
    $ctx = Reset-C3DFResident -Dwg $Dwg -IdleSec $IdleSec -StartTimeoutSec $StartTimeoutSec -ConnectTimeoutSec $ConnectTimeoutSec -Quiet:$Quiet
    Get-C3DFResidentStatus -Dwg $ctx.Dwg -ConnectTimeoutSec $ConnectTimeoutSec | Format-List
  }
  { $_ -in @("status", "ping") } {
    if (-not $Dwg) { throw "-Action $Action needs -Dwg" }
    Get-C3DFResidentStatus -Dwg $Dwg -ConnectTimeoutSec $ConnectTimeoutSec | Format-List
  }
  "exec" {
    if (-not $Dwg) { throw "-Action exec needs -Dwg" }
    if (-not $Task -and -not $Ops) { throw "Need -Task <task.json> or -Ops <op | json array>" }
    if ($Task) { $taskJson = Get-Content -LiteralPath $Task -Raw -Encoding UTF8 }
    else { $taskJson = ConvertTo-C3DFTaskJson -Ops $Ops }

    $logContext = New-C3DFRunContext -Mode "resident" -Dwg $Dwg -Task $Task -Result $Result
    $freshWhenDirty = (Test-C3DFPipelineTask -TaskJson $taskJson) -and (-not $Reuse)
    $t0 = Get-Date
    $resp = Invoke-C3DFResidentOps -Dwg $Dwg -TaskJson $taskJson -ResultPath $Result `
            -RunId $logContext.RunId -AuditLog $logContext.AuditLog `
            -IdleSec $IdleSec -StartTimeoutSec $StartTimeoutSec `
            -ConnectTimeoutSec $ConnectTimeoutSec -FreshWhenDirty:$freshWhenDirty -Quiet:$Quiet
    $sec = [math]::Round(((Get-Date) - $t0).TotalSeconds, 2)
    Write-C3DFAuditEvent -Path $logContext.AuditLog -Event "host_complete" -RunId $logContext.RunId -Fields @{
      ok = [bool]$resp.ok; runner = "resident"; cold_started = [bool]$resp.cold_started; reloaded = [bool]$resp.reloaded
      elapsed_seconds = $sec; resident_ms = $resp.ms
    }
    Write-Host "`n---- ${sec}s (resident $($resp.ms) ms, cold_started=$($resp.cold_started), reloaded=$($resp.reloaded)) ----"
    if ($Result -and (Test-Path -LiteralPath $Result)) {
      Get-Content -LiteralPath $Result -Encoding UTF8
      Write-Host "Result file: $Result"
    } else {
      $resp.result | ConvertTo-Json -Depth 64
    }
    if ($resp.resident.dirty) { Write-Host "! Unsaved changes in memory (dirty=true): run save_dwg to keep them; stop discards them." }
    if (-not $resp.ok) { exit 1 }
  }
}
