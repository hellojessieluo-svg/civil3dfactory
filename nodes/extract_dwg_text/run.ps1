[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string[]]$Dwg,
  [Parameter(Mandatory=$true)][string]$Out,
  [string]$Result,
  [string]$Log,
  [switch]$KeepDxf,
  [string]$Python,
  [int]$TimeoutSec = 900
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$extractor = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\tools\dwg-text-extract\extract_dwg_text.py"))
$started = Get-Date
$target = [IO.Path]::GetFullPath($Out)
$outDir = [IO.Path]::GetDirectoryName($target)
if ([string]::IsNullOrWhiteSpace($Result)) { $Result = $target + ".result.json" }
if ([string]::IsNullOrWhiteSpace($Log))    { $Log    = $target + ".log" }
$resultPath = [IO.Path]::GetFullPath($Result)
$logPath = [IO.Path]::GetFullPath($Log)
$stdoutPath = $logPath + ".out"

function Write-NodeResult {
  param($Data)
  $dir = [IO.Path]::GetDirectoryName($resultPath)
  if (-not [string]::IsNullOrWhiteSpace($dir)) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
  $Data | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}

$sources = @()
try {
  if (-not [IO.File]::Exists($extractor)) { throw "Extractor script not found: $extractor" }
  foreach ($d in $Dwg) {
    $full = [IO.Path]::GetFullPath($d)
    if (-not [IO.File]::Exists($full)) { throw "DWG does not exist: $full" }
    if (-not [string]::Equals([IO.Path]::GetExtension($full), ".dwg", [StringComparison]::OrdinalIgnoreCase)) {
      throw "Input must be DWG: $full"
    }
    $sources += $full
  }
  if ($sources.Count -eq 0) { throw "No DWGs to process." }

  $py = if ([string]::IsNullOrWhiteSpace($Python)) {
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw "Python not found on PATH; specify -Python." }
    $cmd.Source
  } else {
    [IO.Path]::GetFullPath($Python)
  }
  & $py -c "import ezdxf" 2>$null | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Interpreter $py lacks ezdxf; run pip install ezdxf." }

  [IO.Directory]::CreateDirectory($outDir) | Out-Null
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($logPath)) | Out-Null

  $argList = @("`"$extractor`"", "-o", "`"$target`"")
  if ($KeepDxf) { $argList += "--keep-dxf" }
  foreach ($s in $sources) { $argList += "`"$s`"" }

  $env:PYTHONIOENCODING = "utf-8"
  $proc = Start-Process -FilePath $py -ArgumentList $argList -NoNewWindow -PassThru `
          -RedirectStandardOutput $stdoutPath -RedirectStandardError $logPath
  $null = $proc.Handle
  if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    $proc.Kill()
    throw "Extractor exceeded ${TimeoutSec}s; see: $logPath"
  }
  if ($proc.ExitCode -ne 0) { throw "Extractor exit code $($proc.ExitCode); see: $logPath" }
  if (-not [IO.File]::Exists($target)) { throw "Markdown was not generated: $target" }

  $lines = Get-Content -LiteralPath $target -Encoding UTF8
  $documents = @()
  $current = $null
  foreach ($line in $lines) {
    if ($line -match '^##\s+(.+?)\s*$') {
      if ($null -ne $current) { $documents += $current }
      $current = [ordered]@{ name = $Matches[1]; text_count = $null }
    }
    elseif ($null -ne $current -and $line -match '^>\s*(\d+)\s*text entries') {
      $current.text_count = [int]$Matches[1]
    }
  }
  if ($null -ne $current) { $documents += $current }

  $failed = @($documents | Where-Object { $null -eq $_.text_count } | ForEach-Object { $_.name })
  if ($failed.Count -gt 0) {
    throw "Could not extract these drawings (conversion failed or file missing): $($failed -join ', '); see: $logPath"
  }
  if ($documents.Count -ne $sources.Count) {
    throw "Output has $($documents.Count) sections but $($sources.Count) drawings were supplied: $target"
  }

  $total = 0
  foreach ($d in $documents) { $total += [int]$d.text_count }

  $payload = [ordered]@{
    ok = $true
    node = "extract_dwg_text"
    backend = "accoreconsole DXFOUT + ezdxf"
    sources = $sources
    markdown = $target
    documents = $documents
    document_count = $documents.Count
    text_count = $total
    keep_dxf = [bool]$KeepDxf
    python = $py
    log = $logPath
    started_at = $started.ToString("o")
    finished_at = (Get-Date).ToString("o")
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  }
  Write-NodeResult -Data $payload
  $payload | ConvertTo-Json -Depth 10
}
catch {
  Write-NodeResult -Data ([ordered]@{
    ok = $false
    node = "extract_dwg_text"
    sources = $sources
    markdown = $target
    error = $_.Exception.Message
    log = $logPath
    started_at = $started.ToString("o")
    failed_at = (Get-Date).ToString("o")
    elapsed_seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  })
  throw
}
finally {
  Remove-Item -LiteralPath $stdoutPath -ErrorAction SilentlyContinue
}
