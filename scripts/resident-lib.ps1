<#
  Shared function library for civil3dfactory.ps1 and scripts\resident.ps1.

  Functions only, no param block: a dot-sourced script with a param block would run
  that param block in the caller's scope and wipe the caller's $Dwg / $Task / $Result.

  Contents:
    - accoreconsole discovery (Civil 3D 2022-2027) and engine generation mapping
    - engine DLL resolution inside the installed bundle
    - per-run audit log context (logs\runs\<month>\<run-id>.jsonl)
    - resident accoreconsole management (named pipe protocol)

  Resident boundaries (same as cold start; the resident never saves for you):
    - in-memory changes are never written back to the host drawing; run save_dwg explicitly
    - when the resident exits (idle timeout / stop / crash) unsaved changes are discarded
    - dirty=true in status means there are unsaved changes; stop will lose them
    - one resident per drawing, one request at a time (a drawing must not be edited concurrently)
#>

$script:C3DFScriptRoot = $PSScriptRoot
$script:C3DFRepoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# Civil 3D discovery
# ---------------------------------------------------------------------------

<#
  Engine generation = which Contents\<generation>\ folder of the bundle to NETLOAD.
    2022-2024  net472        (.NET Framework 4.8 hosts)
    2025-2026  net8.0        (.NET 8 hosts)
    2027       net10.0       (.NET 10 host)
#>
function Get-C3DFGeneration {
  param([Parameter(Mandatory = $true)][int]$Year)
  if ($Year -ge 2022 -and $Year -le 2024) { return "2022-2024" }
  if ($Year -ge 2025 -and $Year -le 2026) { return "2025-2026" }
  if ($Year -eq 2027) { return "2027" }
  throw "Civil 3D $Year is not supported (supported: 2022-2027)."
}

<#
  Returns [pscustomobject]@{ Exe; Year; Generation }.
  Order: $env:C3DF_ACCORECONSOLE (year parsed from the path, or $env:C3DF_YEAR) ->
         newest installed "C:\Program Files\Autodesk\AutoCAD <year>\accoreconsole.exe" that also has Civil 3D (C3D\AeccDbMgd.dll).
#>
function Get-C3DFAcc {
  $exe = $env:C3DF_ACCORECONSOLE
  if ($exe) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "C3DF_ACCORECONSOLE points to a missing file: $exe" }
    $year = 0
    if ($env:C3DF_YEAR) { $year = [int]$env:C3DF_YEAR }
    elseif ($exe -match 'AutoCAD (\d{4})') { $year = [int]$matches[1] }
    if ($year -eq 0) { throw "Cannot tell the Civil 3D year from C3DF_ACCORECONSOLE; also set C3DF_YEAR (2022-2027)." }
    return [pscustomobject]@{ Exe = $exe; Year = $year; Generation = (Get-C3DFGeneration -Year $year) }
  }
  foreach ($y in 2027, 2026, 2025, 2024, 2023, 2022) {
    $dir = "C:\Program Files\Autodesk\AutoCAD $y"
    $candidate = Join-Path $dir "accoreconsole.exe"
    if ((Test-Path -LiteralPath $candidate) -and (Test-Path -LiteralPath (Join-Path $dir "C3D\AeccDbMgd.dll"))) {
      return [pscustomobject]@{ Exe = $candidate; Year = $y; Generation = (Get-C3DFGeneration -Year $y) }
    }
  }
  throw "No Civil 3D 2022-2027 installation found (looked for C:\Program Files\Autodesk\AutoCAD <year>\accoreconsole.exe with C3D\AeccDbMgd.dll). Set C3DF_ACCORECONSOLE to point at it."
}

# ---------------------------------------------------------------------------
# Engine (bundle) resolution
# ---------------------------------------------------------------------------

function Get-C3DFBundleDir {
  return (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\Civil3DFactory.bundle")
}

<#
  Returns [pscustomobject]@{ Dll; Manifest; ManifestPath; Generation }.
  The bundle is written by scripts\build.ps1 (or unpacked from a Release by install.ps1).
#>
function Resolve-C3DFPlugin {
  param([Parameter(Mandatory = $true)][string]$Generation)
  $bundle = Get-C3DFBundleDir
  $dll = Join-Path $bundle "Contents\$Generation\Civil3DFactory.dll"
  if (-not (Test-Path -LiteralPath $dll)) {
    throw "Engine not installed for Civil 3D generation $Generation ($dll). Run install.ps1 (or scripts\build.ps1) first."
  }
  $manifestPath = Join-Path $bundle "current-version.json"
  $manifest = $null
  if (Test-Path -LiteralPath $manifestPath) {
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $manifest = $null }
  }
  return [pscustomobject]@{
    Dll = [System.IO.Path]::GetFullPath($dll)
    Manifest = $manifest
    ManifestPath = $manifestPath
    Generation = $Generation
  }
}

# ---------------------------------------------------------------------------
# Audit log (one JSONL file per run under <repo>\logs\runs\<yyyy-MM>\)
# ---------------------------------------------------------------------------

function Write-C3DFAuditEvent {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Event,
    [string]$RunId,
    [hashtable]$Fields
  )
  try {
    $parent = Split-Path -Parent $Path
    if ($parent) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    $row = [ordered]@{
      timestamp = (Get-Date).ToString("o")
      event = $Event
      run_id = $RunId
    }
    if ($Fields) { foreach ($key in $Fields.Keys) { $row[$key] = $Fields[$key] } }
    ($row | ConvertTo-Json -Compress -Depth 8) | Add-Content -LiteralPath $Path -Encoding UTF8
  } catch {
    Write-Warning "audit logging failed: $($_.Exception.Message)"
  }
}

function New-C3DFRunContext {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)][string]$Mode,
    [string]$Dwg,
    [string]$Task,
    [string]$Result
  )
  $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
  $month = Get-Date -Format "yyyy-MM"
  $runId = "${stamp}_$([guid]::NewGuid().ToString('N').Substring(0,8))"
  $audit = Join-Path $script:C3DFRepoRoot "logs\runs\$month\$runId.jsonl"
  [System.IO.Directory]::CreateDirectory((Split-Path -Parent $audit)) | Out-Null
  $context = [pscustomobject]@{ RunId = $runId; AuditLog = $audit; Mode = $Mode }
  Write-C3DFAuditEvent -Path $audit -Event "host_start" -RunId $runId -Fields @{
    runner = $Mode; dwg = $Dwg; task_path = $Task; result_path = $Result
  }
  return $context
}

# ---------------------------------------------------------------------------
# Task text helpers
# ---------------------------------------------------------------------------

<#
  -Ops shorthand -> task JSON text.
    '[{"op":..}]'        JSON array, used as is
    '{"op":..}'          single JSON object, wrapped in an array
    'list_alignments'    bare op name
    'view outline'       op name + positional mode (only for view) or key=value pairs, e.g. 'set_scale name=1:500'
#>
function ConvertTo-C3DFTaskJson {
  param([Parameter(Mandatory = $true)][string]$Ops)
  $trimmed = $Ops.Trim()
  if ($trimmed.StartsWith("[")) { return '{"ops":' + $trimmed + '}' }
  if ($trimmed.StartsWith("{")) { return '{"ops":[' + $trimmed + ']}' }
  $tokens = @($trimmed -split '\s+' | Where-Object { $_ })
  $obj = [ordered]@{ op = $tokens[0] }
  for ($i = 1; $i -lt $tokens.Count; $i++) {
    $t = $tokens[$i]
    if ($t -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
      $k = $matches[1]; $v = $matches[2]
      if ($v -match '^-?\d+(\.\d+)?$') { $obj[$k] = [double]$v }
      elseif ($v -eq 'true' -or $v -eq 'false') { $obj[$k] = ($v -eq 'true') }
      else { $obj[$k] = $v }
    } elseif ($tokens[0] -eq 'view' -and -not $obj.Contains('mode')) {
      $obj['mode'] = $t
    } else {
      throw "Cannot parse -Ops token '$t'. Use key=value pairs, or pass a task JSON via -Task."
    }
  }
  return '{"ops":[' + (ConvertTo-Json -InputObject $obj -Compress -Depth 4) + ']}'
}

function ConvertTo-C3DFJsonText {
  param([string]$Value)
  if ($null -eq $Value) { return "null" }
  return (ConvertTo-Json -InputObject $Value -Compress)
}

<#
  A task with a "pipeline" section is a whole-drawing production run and must start from the
  clean on-disk state; an ops-only task is reconnaissance / a single step and wants the
  in-memory state reused.
#>
function Test-C3DFPipelineTask {
  param([Parameter(Mandatory = $true)][string]$TaskJson)
  try {
    $t = $TaskJson | ConvertFrom-Json
    return ($null -ne $t.PSObject.Properties['pipeline'])
  } catch { return $false }
}

# ---------------------------------------------------------------------------
# Resident accoreconsole
# ---------------------------------------------------------------------------

function Get-C3DFResidentDir {
  $dir = Join-Path $env:LOCALAPPDATA "Civil3DFactory\resident"
  [System.IO.Directory]::CreateDirectory($dir) | Out-Null
  return $dir
}

<#
  One resident per drawing: the pipe name is derived from the drawing's absolute path (lower-cased,
  Windows paths are case-insensitive) plus the user name so two users on one machine never collide.
#>
function Get-C3DFResidentContext {
  param([Parameter(Mandatory = $true)][string]$Dwg)
  if (-not (Test-Path -LiteralPath $Dwg)) { throw "Drawing not found: $Dwg" }
  $full = (Resolve-Path -LiteralPath $Dwg).Path
  $seed = "{0}|{1}" -f $env:USERNAME, $full.ToLowerInvariant()
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($seed)) } finally { $sha.Dispose() }
  $key = -join ($hash[0..5] | ForEach-Object { $_.ToString("x2") })
  $dir = Get-C3DFResidentDir
  [pscustomobject]@{
    Dwg   = $full
    Key   = $key
    Pipe  = "Civil3DFactory.$key"
    State = Join-Path $dir "$key.json"
    Log   = Join-Path $dir "$key.log"
    Scr   = Join-Path $dir "$key.scr"
  }
}

# Liveness = the named pipe exists. Directory.GetFiles on \\.\pipe\ is used instead of Test-Path,
# which routes pipe paths through the filesystem provider and throws on some names.
function Test-C3DFPipeExists {
  param([Parameter(Mandatory = $true)][string]$Pipe)
  try { return ([System.IO.Directory]::GetFiles("\\.\pipe\") -contains "\\.\pipe\$Pipe") } catch { return $false }
}

function Get-C3DFResidentState {
  param([Parameter(Mandatory = $true)]$Context)
  if (-not (Test-Path -LiteralPath $Context.State)) { return $null }
  try { return (Get-Content -LiteralPath $Context.State -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

function Test-C3DFProcessAlive {
  param([int]$ProcessId)
  if (-not $ProcessId) { return $false }
  try { $null = Get-Process -Id $ProcessId -ErrorAction Stop; return $true } catch { return $false }
}

# A resident that died (hard crash, taskkill) leaves its state file behind; clear it so list/status stop reporting a ghost.
function Clear-C3DFStaleResident {
  param([Parameter(Mandatory = $true)]$Context)
  if (Test-C3DFPipeExists -Pipe $Context.Pipe) { return $false }
  $state = Get-C3DFResidentState -Context $Context
  if ($null -eq $state) { return $false }
  if (Test-C3DFProcessAlive -ProcessId ([int]$state.pid)) { return $false }
  try { Remove-Item -LiteralPath $Context.State -Force -ErrorAction Stop } catch { }
  return $true
}

function Test-C3DFResidentAlive {
  param([Parameter(Mandatory = $true)]$Context)
  return (Test-C3DFPipeExists -Pipe $Context.Pipe)
}

<#
  Send one request line, receive one JSON line.
  The request body is assembled by hand (not ConvertTo-Json) because the task section is already JSON text.
  No read timeout: an op may legitimately run for ten minutes.
#>
function Invoke-C3DFResidentRequest {
  param(
    [Parameter(Mandatory = $true)]$Context,
    [Parameter(Mandatory = $true)][string]$RequestJson,
    [int]$ConnectTimeoutSec = 30
  )
  $client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $Context.Pipe, [System.IO.Pipes.PipeDirection]::InOut)
  try {
    try { $client.Connect($ConnectTimeoutSec * 1000) }
    catch [System.TimeoutException] {
      throw ("Cannot connect to resident pipe {0} ({1}s timeout). It may be busy or gone; see log: {2}" -f $Context.Pipe, $ConnectTimeoutSec, $Context.Log)
    }
    $enc = New-Object System.Text.UTF8Encoding($false)
    $bytes = $enc.GetBytes($RequestJson + "`n")
    $client.Write($bytes, 0, $bytes.Length)
    $client.Flush()
    $reader = New-Object System.IO.StreamReader($client, $enc)
    $line = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) { throw ("Resident gave no response (connection dropped). See log: {0}" -f $Context.Log) }
    return ($line | ConvertFrom-Json)
  } finally {
    try { $client.Dispose() } catch { }
  }
}

function Start-C3DFResident {
  param(
    [Parameter(Mandatory = $true)][string]$Dwg,
    [int]$IdleSec = 600,
    [int]$StartTimeoutSec = 240,
    [switch]$Quiet
  )
  $ctx = Get-C3DFResidentContext -Dwg $Dwg
  Clear-C3DFStaleResident -Context $ctx | Out-Null
  if (Test-C3DFResidentAlive -Context $ctx) {
    if (-not $Quiet) { Write-Host "Resident already running: $($ctx.Pipe)" }
    return $ctx
  }

  $acc = Get-C3DFAcc
  $plugin = Resolve-C3DFPlugin -Generation $acc.Generation

  # The script file may be read by accoreconsole for the resident's whole life (QUIT only runs after
  # C3DF-Serve returns), so it lives in the resident dir under the key name and is not deleted.
  Set-Content -Path $ctx.Scr -Encoding ascii -Value @("NETLOAD `"$($plugin.Dll)`"", "C3DF-Serve", "QUIT", "_Y")

  $env:C3DF_ROOT = $script:C3DFRepoRoot
  $env:C3DF_PIPE = $ctx.Pipe
  $env:C3DF_IDLE_SEC = [string]$IdleSec
  $env:C3DF_RESIDENT_STATE = $ctx.State
  $env:C3DF_RESIDENT_LOG = $ctx.Log
  $env:C3DF_RUNNER = "resident"
  $env:C3DF_PLUGIN_VERSION = [string]$plugin.Manifest.version
  $env:C3DF_PLUGIN_COMMIT = [string]$plugin.Manifest.git_short_commit
  # The resident must not inherit a single run's task/result paths; audit context is sent per request.
  $env:C3DF_TASK = $null
  $env:C3DF_RESULT = $null
  $env:C3DF_RUN_ID = $null
  $env:C3DF_AUDIT_LOG = $null

  # -WindowStyle Hidden, not -NoNewWindow: the resident must outlive the shell that started it and
  # must not steal the terminal's keyboard. accoreconsole with no console at all spins idle.
  $proc = Start-Process -FilePath $acc.Exe `
          -ArgumentList @("/i", "`"$($ctx.Dwg)`"", "/s", "`"$($ctx.Scr)`"", "/l", "en-US") `
          -WindowStyle Hidden -PassThru

  $t0 = Get-Date
  while (-not (Test-C3DFPipeExists -Pipe $ctx.Pipe)) {
    if ($proc.HasExited) { throw ("Resident process exited immediately (exit code {0}). See log: {1}" -f $proc.ExitCode, $ctx.Log) }
    if (((Get-Date) - $t0).TotalSeconds -gt $StartTimeoutSec) {
      try { $proc.Kill() } catch { }
      throw ("Resident did not come up within {0}s (pipe {1} never appeared). See log: {2}" -f $StartTimeoutSec, $ctx.Pipe, $ctx.Log)
    }
    Start-Sleep -Milliseconds 250
  }
  $sec = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
  if (-not $Quiet) { Write-Host "Resident ready pid=$($proc.Id) pipe=$($ctx.Pipe) cold start ${sec}s" }
  return $ctx
}

# Restart: stop, then start again; the drawing is re-read from disk and in-memory changes are discarded.
function Reset-C3DFResident {
  param(
    [Parameter(Mandatory = $true)][string]$Dwg,
    [int]$IdleSec = 600,
    [int]$StartTimeoutSec = 240,
    [int]$ConnectTimeoutSec = 30,
    [switch]$Quiet
  )
  $ctx = Get-C3DFResidentContext -Dwg $Dwg
  if (Test-C3DFResidentAlive -Context $ctx) {
    Stop-C3DFResident -Dwg $Dwg -ConnectTimeoutSec $ConnectTimeoutSec | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-C3DFPipeExists -Pipe $ctx.Pipe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-C3DFPipeExists -Pipe $ctx.Pipe) {
      throw ("Old resident did not release pipe {0} within 60s; refusing to start a new one. See log: {1}" -f $ctx.Pipe, $ctx.Log)
    }
  }
  return (Start-C3DFResident -Dwg $Dwg -IdleSec $IdleSec -StartTimeoutSec $StartTimeoutSec -Quiet:$Quiet)
}

function Get-C3DFResidentStatus {
  param([Parameter(Mandatory = $true)][string]$Dwg, [int]$ConnectTimeoutSec = 30)
  $ctx = Get-C3DFResidentContext -Dwg $Dwg
  Clear-C3DFStaleResident -Context $ctx | Out-Null
  if (-not (Test-C3DFResidentAlive -Context $ctx)) {
    return [pscustomobject]@{ alive = $false; pipe = $ctx.Pipe; dwg = $ctx.Dwg; log = $ctx.Log }
  }
  $resp = Invoke-C3DFResidentRequest -Context $ctx -RequestJson '{"cmd":"status"}' -ConnectTimeoutSec $ConnectTimeoutSec
  return $resp.resident
}

function Stop-C3DFResident {
  param([Parameter(Mandatory = $true)][string]$Dwg, [int]$ConnectTimeoutSec = 30)
  $ctx = Get-C3DFResidentContext -Dwg $Dwg
  Clear-C3DFStaleResident -Context $ctx | Out-Null
  if (-not (Test-C3DFResidentAlive -Context $ctx)) {
    return [pscustomobject]@{ stopped = $false; reason = "no resident running"; pipe = $ctx.Pipe }
  }
  $resp = Invoke-C3DFResidentRequest -Context $ctx -RequestJson '{"cmd":"close"}' -ConnectTimeoutSec $ConnectTimeoutSec
  [pscustomobject]@{ stopped = $true; pipe = $ctx.Pipe; dwg = $ctx.Dwg; dirty = $resp.resident.dirty; served = $resp.resident.served }
}

function Get-C3DFResidentList {
  $dir = Get-C3DFResidentDir
  Get-ChildItem -LiteralPath $dir -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object {
    $state = $null
    try { $state = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    if ($null -eq $state) { return }
    [pscustomobject]@{
      alive    = (Test-C3DFPipeExists -Pipe $state.pipe) -and (Test-C3DFProcessAlive -ProcessId ([int]$state.pid))
      pid      = $state.pid
      state    = $state.state
      dirty    = $state.dirty
      served   = $state.served
      idle_sec = $state.idle_sec
      dwg      = $state.dwg
      pipe     = $state.pipe
    }
  }
}

<#
  Run one task through the resident (started on demand). Result structure is identical to a cold run.
  -FreshWhenDirty: restart first when the resident already holds unsaved changes, so a production run
  starts from the clean on-disk state.
#>
function Invoke-C3DFResidentOps {
  param(
    [Parameter(Mandatory = $true)][string]$Dwg,
    [Parameter(Mandatory = $true)][string]$TaskJson,
    [string]$ResultPath,
    [string]$RunId,
    [string]$AuditLog,
    [int]$IdleSec = 600,
    [int]$StartTimeoutSec = 240,
    [int]$ConnectTimeoutSec = 30,
    [switch]$FreshWhenDirty,
    [switch]$Quiet
  )
  $ctx = Get-C3DFResidentContext -Dwg $Dwg
  Clear-C3DFStaleResident -Context $ctx | Out-Null
  $started = $false
  $reloaded = $false
  if (-not (Test-C3DFResidentAlive -Context $ctx)) {
    $ctx = Start-C3DFResident -Dwg $Dwg -IdleSec $IdleSec -StartTimeoutSec $StartTimeoutSec -Quiet:$Quiet
    $started = $true
  } elseif ($FreshWhenDirty) {
    $st = Invoke-C3DFResidentRequest -Context $ctx -RequestJson '{"cmd":"status"}' -ConnectTimeoutSec $ConnectTimeoutSec
    if ($st.resident.dirty) {
      if (-not $Quiet) { Write-Host "Resident holds unsaved changes -> restarting so this run starts from the on-disk state." }
      $ctx = Reset-C3DFResident -Dwg $Dwg -IdleSec $IdleSec -StartTimeoutSec $StartTimeoutSec -ConnectTimeoutSec $ConnectTimeoutSec -Quiet:$Quiet
      $reloaded = $true
    }
  }

  # Requests are newline-framed, so the task must be flattened to one line. Literal newlines in
  # valid JSON only occur between tokens, so replacing CR/LF with a space is lossless.
  $flatTask = $TaskJson.TrimStart([char]0xFEFF).Trim() -replace "[\r\n]+", " "
  $parts = @(
    '"cmd":"ops"',
    ('"run_id":' + (ConvertTo-C3DFJsonText $RunId)),
    ('"audit_log":' + (ConvertTo-C3DFJsonText $AuditLog)),
    ('"result_path":' + (ConvertTo-C3DFJsonText $ResultPath)),
    ('"task":' + $flatTask)
  )
  $requestJson = '{' + ($parts -join ',') + '}'
  $resp = Invoke-C3DFResidentRequest -Context $ctx -RequestJson $requestJson -ConnectTimeoutSec $ConnectTimeoutSec
  # Protocol failure (request not accepted) is thrown; op failure (ran but errored) is returned in the result.
  if ($resp.PSObject.Properties.Name -contains "error" -and $resp.error) {
    throw ("Resident rejected the request: {0} ({1}). See log: {2}" -f $resp.error, $resp.type, $ctx.Log)
  }
  Add-Member -InputObject $resp -NotePropertyName "cold_started" -NotePropertyValue $started -Force
  Add-Member -InputObject $resp -NotePropertyName "reloaded" -NotePropertyValue $reloaded -Force
  return $resp
}
