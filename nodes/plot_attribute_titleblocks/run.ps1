[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Dwg,
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [string]$Result,
  [string]$Log,
  [string]$PlotterExe
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$approvedPlotter = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\tools\dwg-titleblock-plotter\DWGTitleblockPlotter.exe"))
# Optional integrity pin. A maintainer may set this to the SHA256 of a reviewed DWGTitleblockPlotter.exe build;
# when left empty the hash is reported but not enforced.
$approvedSha256 = ""
$minimumPdfBytes = 10000
$started = Get-Date
$source = [IO.Path]::GetFullPath($Dwg)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($Result)) {
  $Result = Join-Path $outputRoot "plot_attribute_titleblocks.result.json"
}
if ([string]::IsNullOrWhiteSpace($Log)) {
  $Log = Join-Path $outputRoot "plot_attribute_titleblocks.log"
}
$resultPath = [IO.Path]::GetFullPath($Result)
$logPath = [IO.Path]::GetFullPath($Log)

function Write-NodeResult {
  param([hashtable]$Data)
  $parent = [IO.Path]::GetDirectoryName($resultPath)
  if (-not [string]::IsNullOrWhiteSpace($parent)) {
    [IO.Directory]::CreateDirectory($parent) | Out-Null
  }
  $Data | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

try {
  if (-not [IO.File]::Exists($source)) {
    throw "DWG not found: $source"
  }
  if (-not [string]::Equals([IO.Path]::GetExtension($source), ".dwg", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Input must be a DWG: $source"
  }
  $plotter = if ([string]::IsNullOrWhiteSpace($PlotterExe)) {
    $approvedPlotter
  } else {
    [IO.Path]::GetFullPath($PlotterExe)
  }
  if (-not [IO.File]::Exists($plotter)) {
    throw "Title-block plotter executable not found: $plotter"
  }
  $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $plotter).Hash
  if (-not [string]::IsNullOrWhiteSpace($approvedSha256) -and
      -not [string]::Equals($actualHash, $approvedSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Title-block plotter hash mismatch. Actual SHA256: $actualHash"
  }
  $acc = "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe"
  if (-not [IO.File]::Exists($acc)) {
    throw "accoreconsole (headless AutoCAD core) not found: $acc"
  }

  [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
  $lines = @(
    & $plotter $source $outputRoot 2>&1 |
      ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        $line
      }
  )
  $exitCode = $LASTEXITCODE
  $lines | Set-Content -LiteralPath $logPath -Encoding UTF8
  if ($exitCode -ne 0) {
    throw "Title-block plotter exited with code $exitCode; see log: $logPath"
  }

  $pdfs = @(Get-ChildItem -LiteralPath $outputRoot -Filter *.pdf -File -Recurse)
  if ($pdfs.Count -eq 0) {
    throw "No PDF was produced; the drawing may contain no title-block references recognised by the plotter."
  }
  $undersized = @($pdfs | Where-Object { $_.Length -lt $minimumPdfBytes })
  if ($undersized.Count -gt 0) {
    $names = ($undersized | ForEach-Object { "$($_.Name)=$($_.Length)B" }) -join ", "
    throw "Suspected blank or incomplete PDF detected: $names"
  }

  $payload = [ordered]@{
    ok = $true
    node = "plot_attribute_titleblocks"
    backend = "accoreconsole + CadPlotPlugin"
    source = $source
    output_directory = $outputRoot
    plotter = $plotter
    plotter_sha256 = $actualHash
    pdf_count = $pdfs.Count
    pdfs = @($pdfs | ForEach-Object {
      [ordered]@{ path = $_.FullName; bytes = $_.Length }
    })
    minimum_pdf_bytes = $minimumPdfBytes
    log = $logPath
    started_at = $started.ToString("o")
    finished_at = (Get-Date).ToString("o")
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  }
  Write-NodeResult -Data $payload
  $payload | ConvertTo-Json -Depth 10
}
catch {
  $payload = [ordered]@{
    ok = $false
    node = "plot_attribute_titleblocks"
    source = $source
    output_directory = $outputRoot
    error = $_.Exception.Message
    log = $logPath
    started_at = $started.ToString("o")
    failed_at = (Get-Date).ToString("o")
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  }
  Write-NodeResult -Data $payload
  throw
}

