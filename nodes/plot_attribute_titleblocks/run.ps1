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

$approvedPlotter = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\tools\dwg-titleblock-plotter\output\DWGTitleblockPlotter.exe"))
$approvedSha256 = "70B6311FEE029400E51C48F0C4D4F8F286C2DC5E6573A1C39EAA709E8588E9B2"
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
    throw "DWG 不存在：$source"
  }
  if (-not [string]::Equals([IO.Path]::GetExtension($source), ".dwg", [StringComparison]::OrdinalIgnoreCase)) {
    throw "输入必须是 DWG：$source"
  }
  $plotter = if ([string]::IsNullOrWhiteSpace($PlotterExe)) {
    $approvedPlotter
  } else {
    [IO.Path]::GetFullPath($PlotterExe)
  }
  if (-not [IO.File]::Exists($plotter)) {
    throw "找不到已验证的属性图框打印器：$plotter"
  }
  $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $plotter).Hash
  if (-not [string]::Equals($actualHash, $approvedSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "属性图框打印器哈希不符。实际 SHA256：$actualHash"
  }
  $acc = "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe"
  if (-not [IO.File]::Exists($acc)) {
    throw "找不到 ACC 无界面内核：$acc"
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
    throw "属性图框打印器退出码为 $exitCode；详见：$logPath"
  }

  $pdfs = @(Get-ChildItem -LiteralPath $outputRoot -Filter *.pdf -File -Recurse)
  if ($pdfs.Count -eq 0) {
    throw "未生成 PDF；图中可能没有块名包含 [图框] 的块。"
  }
  $undersized = @($pdfs | Where-Object { $_.Length -lt $minimumPdfBytes })
  if ($undersized.Count -gt 0) {
    $names = ($undersized | ForEach-Object { "$($_.Name)=$($_.Length)B" }) -join ", "
    throw "检测到疑似白纸或不完整 PDF：$names"
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

