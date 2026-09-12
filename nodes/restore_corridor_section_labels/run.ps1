param(
  [Parameter(Mandatory=$true)][string]$Dwg,
  [Parameter(Mandatory=$true)][string]$Out,
  [string]$Result,
  [switch]$Overwrite,
  [switch]$Visible
)

$ErrorActionPreference = "Stop"
$acad = $null
$doc = $null
$started = Get-Date
$source = [System.IO.Path]::GetFullPath($Dwg)
$target = [System.IO.Path]::GetFullPath($Out)
if ([string]::IsNullOrWhiteSpace($Result)) { $Result = $target + ".com-labels.result.json" }
$resultPath = [System.IO.Path]::GetFullPath($Result)

function Wait-AcadIdle {
  param([Parameter(Mandatory=$true)]$Application, [int]$TimeoutSeconds=300)
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    Start-Sleep -Milliseconds 500
    try {
      if ($Application.GetAcadState().IsQuiescent) { return }
    }
    catch {}
  } while ((Get-Date) -lt $deadline)
  throw "Civil 3D did not become idle within $TimeoutSeconds seconds."
}

function Invoke-ComRetry {
  param([Parameter(Mandatory=$true)][scriptblock]$Action, [int]$TimeoutSeconds=180)
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    try { return & $Action }
    catch [System.Runtime.InteropServices.COMException] {
      if ($_.Exception.HResult -ne -2147418111) { throw }
      Start-Sleep -Milliseconds 750
    }
  } while ((Get-Date) -lt $deadline)
  throw "Civil 3D kept rejecting COM calls for $TimeoutSeconds seconds."
}

function Wait-CommandComplete {
  param([Parameter(Mandatory=$true)]$Application,
        [Parameter(Mandatory=$true)]$Document,
        [int]$TimeoutSeconds=600)
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $earliest = (Get-Date).AddSeconds(2)
  do {
    Start-Sleep -Milliseconds 500
    try {
      $active = [int]$Document.GetVariable("CMDACTIVE")
      $names = [string]$Document.GetVariable("CMDNAMES")
      $idle = [bool]$Application.GetAcadState().IsQuiescent
      if ((Get-Date) -ge $earliest -and $idle -and $active -eq 0 -and
          [string]::IsNullOrWhiteSpace($names)) { return }
    }
    catch {}
  } while ((Get-Date) -lt $deadline)
  throw "CORRIDORSECTIONLABELSCONV did not finish within $TimeoutSeconds seconds."
}

function Write-Result {
  param([hashtable]$Data)
  $dir = [System.IO.Path]::GetDirectoryName($resultPath)
  if (-not [string]::IsNullOrWhiteSpace($dir)) {
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
  }
  $Data | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

try {
  if (-not [System.IO.File]::Exists($source)) { throw "Source DWG not found: $source" }
  if ([string]::Equals($source, $target, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Out must be a separate copy; in-place editing is not allowed."
  }
  $targetDir = [System.IO.Path]::GetDirectoryName($target)
  [System.IO.Directory]::CreateDirectory($targetDir) | Out-Null
  if ([System.IO.File]::Exists($target) -and -not $Overwrite) {
    throw "Output already exists. Pass -Overwrite to replace it: $target"
  }
  [System.IO.File]::Copy($source, $target, $true)
  $bytesBefore = [System.IO.FileInfo]::new($target).Length

  $acad = New-Object -ComObject "AutoCAD.Application.25"
  Wait-AcadIdle -Application $acad -TimeoutSeconds 180
  Invoke-ComRetry -Action { $acad.Visible = [bool]$Visible }
  $civil = Invoke-ComRetry -Action {
    $acad.GetInterfaceObject("AeccXUiLand.AeccApplication.13.7")
  }
  Wait-AcadIdle -Application $acad -TimeoutSeconds 180

  $doc = Invoke-ComRetry -Action { $acad.Documents.Open($target, $false) } -TimeoutSeconds 300
  Invoke-ComRetry -Action { $doc.Activate() }
  Wait-AcadIdle -Application $acad -TimeoutSeconds 300
  $doc.SetVariable("FILEDIA", 0)
  $doc.SetVariable("CMDECHO", 1)
  $dbmodBefore = [int]$doc.GetVariable("DBMOD")

  $doc.SendCommand("_.CORRIDORSECTIONLABELSCONV`n_ALL`n`n_C`n")
  Wait-CommandComplete -Application $acad -Document $doc -TimeoutSeconds 600
  $dbmodAfter = [int]$doc.GetVariable("DBMOD")
  Invoke-ComRetry -Action { $doc.Save() } -TimeoutSeconds 180
  Wait-AcadIdle -Application $acad -TimeoutSeconds 180
  $bytesAfter = [System.IO.FileInfo]::new($target).Length

  Write-Result -Data ([ordered]@{
    ok = $true
    source = $source
    output = $target
    command = "CORRIDORSECTIONLABELSCONV -> ALL -> C"
    civil = [string]$civil.Name
    dbmod_before = $dbmodBefore
    dbmod_after_command = $dbmodAfter
    bytes_before = $bytesBefore
    bytes_after = $bytesAfter
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  })
}
catch {
  Write-Result -Data ([ordered]@{
    ok = $false
    source = $source
    output = $target
    error = $_.Exception.Message
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  })
  throw
}
finally {
  if ($null -ne $doc) { try { $doc.Close($true) } catch {} }
  if ($null -ne $acad) { try { $acad.Quit() } catch {} }
  [GC]::Collect()
  [GC]::WaitForPendingFinalizers()
}
