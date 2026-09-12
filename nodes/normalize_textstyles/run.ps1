# Targets a Chinese font convention: text style "-<hei ti>" (name built from U+9ED1 U+4F53, TrueType SimHei) and "txt1" (gbenor.shx + gbcbig.shx big font). Style names and fonts are the node's purpose and stay literal.
param(
  [Parameter(Mandatory=$true)][string]$Dwg,
  [Parameter(Mandatory=$true)][string]$Out,
  [string]$Result,
  [switch]$Overwrite,
  [switch]$Visible,
  [int]$TimeoutSec = 300
)

$ErrorActionPreference = "Stop"
$acad = $null
$doc = $null
$keepAliveDoc = $null
$started = Get-Date
$source = [IO.Path]::GetFullPath($Dwg)
$target = [IO.Path]::GetFullPath($Out)
if ([string]::IsNullOrWhiteSpace($Result)) { $Result = $target + ".textstyles.result.json" }
$resultPath = [IO.Path]::GetFullPath($Result)
$heiStyleName = "-" + ([char]0x9ED1).ToString() + [char]0x4F53

function Wait-AcadIdle {
  param([Parameter(Mandatory=$true)]$Application, [int]$TimeoutSeconds=300)
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    Start-Sleep -Milliseconds 500
    try {
      if ($Application.GetAcadState().IsQuiescent) { return }
    } catch {}
  } while ((Get-Date) -lt $deadline)
  throw "AutoCAD did not become idle within $TimeoutSeconds seconds."
}

function Invoke-ComRetry {
  param([Parameter(Mandatory=$true)][scriptblock]$Action, [int]$TimeoutSeconds=180)
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    try { return & $Action }
    catch [Runtime.InteropServices.COMException] {
      if ($_.Exception.HResult -ne -2147418111) { throw }
      Start-Sleep -Milliseconds 750
    }
  } while ((Get-Date) -lt $deadline)
  throw "AutoCAD kept rejecting COM calls for $TimeoutSeconds seconds."
}

function Get-TtfFont {
  param([Parameter(Mandatory=$true)]$Style)
  $face = ""
  $bold = $false
  $italic = $false
  $charset = 0
  $pitch = 0
  $Style.GetFont([ref]$face, [ref]$bold, [ref]$italic, [ref]$charset, [ref]$pitch)
  return [ordered]@{
    typeface = $face
    bold = $bold
    italic = $italic
    charset = $charset
    pitch_and_family = $pitch
  }
}

function Get-TextStyleByName {
  param([Parameter(Mandatory=$true)]$Styles, [Parameter(Mandatory=$true)][string]$Name)
  for ($i = 0; $i -lt [int]$Styles.Count; $i++) {
    $item = $Styles.Item($i)
    if ([string]::Equals([string]$item.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
      return $item
    }
  }
  throw "Text style not found: $Name"
}

function Write-Result {
  param([hashtable]$Data)
  $dir = [IO.Path]::GetDirectoryName($resultPath)
  if (-not [string]::IsNullOrWhiteSpace($dir)) {
    [IO.Directory]::CreateDirectory($dir) | Out-Null
  }
  $Data | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

try {
  if (-not [IO.File]::Exists($source)) { throw "Source DWG not found: $source" }
  if ([string]::Equals($source, $target, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Out must be a separate copy; in-place editing is not allowed."
  }
  if ([IO.File]::Exists($target) -and -not $Overwrite) {
    throw "Output already exists. Pass -Overwrite to replace it: $target"
  }
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
  [IO.File]::Copy($source, $target, $true)
  $bytesBefore = [IO.FileInfo]::new($target).Length

  $acad = New-Object -ComObject "AutoCAD.Application.25"
  Wait-AcadIdle -Application $acad -TimeoutSeconds $TimeoutSec
  Invoke-ComRetry -Action { $acad.Visible = [bool]$Visible }
  $doc = Invoke-ComRetry -Action { $acad.Documents.Open($target, $false) } -TimeoutSeconds $TimeoutSec
  Invoke-ComRetry -Action { $doc.Activate() }
  Wait-AcadIdle -Application $acad -TimeoutSeconds $TimeoutSec

  $styles = Invoke-ComRetry -Action { $doc.TextStyles }
  if ($null -eq $styles) { throw "AutoCAD returned a null TextStyles collection." }
  $hei = Get-TextStyleByName -Styles $styles -Name $heiStyleName
  $txt1 = Get-TextStyleByName -Styles $styles -Name "txt1"

  Invoke-ComRetry -Action { $hei.SetFont("SimHei", $false, $false, 134, 34) }
  Invoke-ComRetry -Action { $hei.Width = 0.8 }

  Invoke-ComRetry -Action { $txt1.FontFile = "gbenor.shx" }
  Invoke-ComRetry -Action { $txt1.BigFontFile = "gbcbig.shx" }
  Invoke-ComRetry -Action { $txt1.Width = 0.8 }

  Invoke-ComRetry -Action { $doc.Regen(1) }
  Invoke-ComRetry -Action { $doc.Save() } -TimeoutSeconds $TimeoutSec
  Wait-AcadIdle -Application $acad -TimeoutSeconds $TimeoutSec

  # Some AutoCAD COM sessions invalidate the Documents collection once the only document is closed.
  # Keep a blank document alive so the target drawing can be closed and reopened for verification.
  $keepAliveDoc = Invoke-ComRetry -Action { $acad.Documents.Add() } -TimeoutSeconds $TimeoutSec
  Invoke-ComRetry -Action { $doc.Close($false) }
  $doc = $null

  # Reopen and verify persisted data. FontFile alone is not a valid TTF check.
  $doc = Invoke-ComRetry -Action { $acad.Documents.Open($target, $true) } -TimeoutSeconds $TimeoutSec
  Wait-AcadIdle -Application $acad -TimeoutSeconds $TimeoutSec
  $stylesCheck = Invoke-ComRetry -Action { $doc.TextStyles }
  if ($null -eq $stylesCheck) { throw "AutoCAD returned a null TextStyles collection during verification." }
  $heiCheck = Get-TextStyleByName -Styles $stylesCheck -Name $heiStyleName
  $txt1Check = Get-TextStyleByName -Styles $stylesCheck -Name "txt1"
  $font = Get-TtfFont -Style $heiCheck
  if ($font.typeface -ne "SimHei") {
    throw "$heiStyleName verification failed: expected SimHei, got $($font.typeface)."
  }
  if ($txt1Check.FontFile -notlike "*gbenor.shx" -or
      $txt1Check.BigFontFile -notlike "*gbcbig.shx") {
    throw "txt1 verification failed."
  }

  $payload = [ordered]@{
    ok = $true
    operation = "normalize_text_styles_data_layer"
    source = $source
    output = $target
    styles = @(
      [ordered]@{
        name = $heiStyleName
        typeface = $font.typeface
        font_file = [string]$heiCheck.FontFile
        big_font = [string]$heiCheck.BigFontFile
        width_factor = [double]$heiCheck.Width
        verification = "GetFont"
      },
      [ordered]@{
        name = "txt1"
        font_file = [string]$txt1Check.FontFile
        big_font = [string]$txt1Check.BigFontFile
        width_factor = [double]$txt1Check.Width
        verification = "FontFile+BigFontFile"
      }
    )
    bytes_before = $bytesBefore
    bytes_after = [IO.FileInfo]::new($target).Length
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  }
  Write-Result -Data $payload
  $payload | ConvertTo-Json -Depth 8

  Invoke-ComRetry -Action { $keepAliveDoc.Close($false) }
  $keepAliveDoc = $null
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
  if ($null -ne $doc) { try { $doc.Close($false) } catch {} }
  if ($null -ne $keepAliveDoc) { try { $keepAliveDoc.Close($false) } catch {} }
  if ($null -ne $acad) { try { $acad.Quit() } catch {} }
  [GC]::Collect()
  [GC]::WaitForPendingFinalizers()
}
