<#
  civil3dfactory one-shot installer. Run once after cloning or unzipping a release; safe to rerun.

    powershell -ExecutionPolicy Bypass -File install.ps1 [-Build] [-BuildTools] [-SkipSkills] [-SkipCheck] [-Agents claude,codex,...]

  Five steps:
    1. probe    Civil 3D 2022-2027 (accoreconsole + C3D), .NET SDK (optional), Python (optional)
    2. engine   install the engine bundle into %APPDATA%\Autodesk\ApplicationPlugins\Civil3DFactory.bundle
                  from  ./Civil3DFactory.bundle (unzipped release)  ->  GitHub Releases download  ->  local build (-Build, needs .NET SDK)
    3. tools    put DWGAttributeEditor.exe / DWGTitleblockPlotter.exe under tools\<tool>\ (release download; -BuildTools builds them with Python)
    4. skills   copy SKILL.md and skills\*\SKILL.md into the user-level skill folders of the AI agents found on this machine
    5. check    run a read-only example against examples\channel-demo.dwg and report

  Nothing else is required on the machine: no Python, no .NET SDK, no AutoCAD SECURELOAD change.
#>
param(
  [switch]$Build,
  [switch]$BuildTools,
  [switch]$SkipSkills,
  [switch]$SkipCheck,
  [string[]]$Agents,
  [string]$Repo = "hellojessieluo-svg/civil3dfactory"
)
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = $PSScriptRoot
. (Join-Path $root "scripts\resident-lib.ps1")
function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "  + $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  ! $t" -ForegroundColor Yellow }
function Fetch([string]$Asset, [string]$OutFile) {
  $url = "https://github.com/$Repo/releases/latest/download/$Asset"
  try {
    Invoke-WebRequest -Uri $url -OutFile $OutFile -TimeoutSec 300 -ErrorAction Stop
    return $true
  } catch {
    Warn "download failed: $url ($($_.Exception.Message))"
    return $false
  }
}

# ---------- 1. probe ----------
Step "1/5 environment"
$acc = $null
try { $acc = Get-C3DFAcc; Ok "Civil 3D $($acc.Year) -> engine generation $($acc.Generation): $($acc.Exe)" }
catch { Warn $_.Exception.Message; Warn "Install Civil 3D 2022-2027 first; the engine cannot run without it." }
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$sdks = @()
if ($dotnet) { $sdks = @(& dotnet --list-sdks 2>$null | ForEach-Object { ($_ -split '\s')[0] }) }
if ($sdks.Count -gt 0) { Ok (".NET SDK: " + ($sdks -join ', ') + "  (maintainers only)") } else { Ok ".NET SDK: not installed (not needed; the engine comes prebuilt)" }
$py = Get-Command python -ErrorAction SilentlyContinue
if ($py) { Ok ("Python: " + ((& python --version 2>&1) -join '') + "  (only needed for -BuildTools)") } else { Ok "Python: not installed (not needed; the exe tools come prebuilt)" }
if (Test-Path (Join-Path $root '.git')) {
  & git -C $root config core.hooksPath scripts/githooks
  Ok "git pre-commit gate enabled (scripts/githooks)"
}

# ---------- 2. engine ----------
Step "2/5 engine -> $(Get-C3DFBundleDir)"
$bundle = Get-C3DFBundleDir
$pluginsDir = Split-Path -Parent $bundle
$installed = $false
$localBundle = Join-Path $root "Civil3DFactory.bundle"
if ($Build) {
  if ($sdks.Count -eq 0) { throw "-Build needs a .NET SDK (8 for Civil 3D 2022-2026, 10 for 2027)." }
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build.ps1')
  if ($LASTEXITCODE -ne 0) { throw "scripts\build.ps1 failed (exit $LASTEXITCODE)" }
  $installed = $true
} elseif (Test-Path -LiteralPath (Join-Path $localBundle "PackageContents.xml")) {
  New-Item -ItemType Directory -Force $pluginsDir | Out-Null
  if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }
  Copy-Item -Recurse -LiteralPath $localBundle -Destination $bundle
  Ok "copied the prebuilt bundle shipped next to install.ps1"
  $installed = $true
} else {
  $zip = Join-Path $env:TEMP "Civil3DFactory.bundle.zip"
  Write-Host "  downloading Civil3DFactory.bundle.zip from GitHub Releases..."
  if (Fetch "Civil3DFactory.bundle.zip" $zip) {
    $tmp = Join-Path $env:TEMP ("c3df_bundle_" + [guid]::NewGuid().ToString("N"))
    Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
    $src = Get-ChildItem -LiteralPath $tmp -Directory | Where-Object { Test-Path (Join-Path $_.FullName "PackageContents.xml") } | Select-Object -First 1
    if (-not $src) { throw "The downloaded zip has no Civil3DFactory.bundle\PackageContents.xml." }
    New-Item -ItemType Directory -Force $pluginsDir | Out-Null
    if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }
    Copy-Item -Recurse -LiteralPath $src.FullName -Destination $bundle
    Remove-Item -Recurse -Force $tmp, $zip -ErrorAction SilentlyContinue
    Ok "installed the release bundle"
    $installed = $true
  } elseif ($sdks.Count -gt 0) {
    Warn "falling back to a local build (.NET SDK found)"
    $tfm = @{ "2022-2024" = "net472"; "2025-2026" = "net8.0-windows"; "2027" = "net10.0-windows" }
    $buildArgs = @()
    if ($acc) { $buildArgs = @('-Targets', $tfm[$acc.Generation]) }   # only the generation this machine can run
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "scripts\build.ps1 failed (exit $LASTEXITCODE)" }
    $installed = $true
  } else {
    throw "No engine available: download failed and no .NET SDK to build with. Get Civil3DFactory.bundle.zip from https://github.com/$Repo/releases and unzip it next to install.ps1."
  }
}
if ($installed) {
  # record where the repo lives so the engine can find nodes\node.json when loaded inside the Civil 3D GUI
  $manifestPath = Join-Path $bundle "current-version.json"
  $manifest = @{}
  if (Test-Path -LiteralPath $manifestPath) { try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $manifest = @{} } }
  $m = [ordered]@{}
  foreach ($prop in $manifest.PSObject.Properties) { $m[$prop.Name] = $prop.Value }
  $m["source_path"] = $root
  ($m | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $manifestPath -Encoding UTF8
  $gens = @(Get-ChildItem -LiteralPath (Join-Path $bundle "Contents") -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
  Ok ("engine generations present: " + ($gens -join ', '))
  if ($acc -and ($gens -notcontains $acc.Generation)) { Warn "no engine build for Civil 3D $($acc.Year) (generation $($acc.Generation)); civil3dfactory.ps1 will not run until one is built or released." }
  Ok "restart Civil 3D if it is open (the bundle loads at startup)"
}

# ---------- 3. exe tools ----------
Step "3/5 exe tools (tools\)"
$tools = @(
  @{ Dir = "dwg-attribute-editor";   Exe = "DWGAttributeEditor.exe" },
  @{ Dir = "dwg-titleblock-plotter"; Exe = "DWGTitleblockPlotter.exe" },
  @{ Dir = "pkt-forge\bin\out";      Exe = "PktForge.exe" }
)
foreach ($t in $tools) {
  $dir = Join-Path $root "tools\$($t.Dir)"
  $exe = Join-Path $dir $t.Exe
  New-Item -ItemType Directory -Force $dir | Out-Null
  if ($BuildTools -and $t.Exe -eq "PktForge.exe") {
    if ($sdks.Count -eq 0) { Warn "PktForge.exe: -BuildTools needs a .NET SDK"; continue }
    & dotnet build (Join-Path $root 'tools\pkt-forge\PktForge.csproj') -c Release -o $dir -nologo -v minimal
    if ($LASTEXITCODE -eq 0) { Ok "PktForge.exe: built" } else { Warn "PktForge.exe: build failed" }
    continue
  }
  if ($BuildTools) {
    if (-not $py) { Warn "$($t.Exe): -BuildTools needs Python 3.10+ on PATH"; continue }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'build_exe.ps1')
    if ($LASTEXITCODE -ne 0) { Warn "$($t.Exe): build failed"; continue }
    $built = Join-Path $dir "output\$($t.Exe)"
    if (Test-Path -LiteralPath $built) { Copy-Item -LiteralPath $built -Destination $exe -Force; Ok "$($t.Exe): built" }
    continue
  }
  if (Test-Path -LiteralPath $exe) { Ok "$($t.Exe): present"; continue }
  $sibling = Join-Path $root $t.Exe
  if (Test-Path -LiteralPath $sibling) { Copy-Item -LiteralPath $sibling -Destination $exe -Force; Ok "$($t.Exe): copied from the release folder"; continue }
  Write-Host "  downloading $($t.Exe)..."
  if (Fetch $t.Exe $exe) { Ok "$($t.Exe): downloaded" }
  else { Warn "$($t.Exe): not available. Download it from https://github.com/$Repo/releases into tools\$($t.Dir)\, or run install.ps1 -BuildTools (needs Python)." }
}

# ---------- 4. skills ----------
Step "4/5 skills -> AI agents"
if ($SkipSkills) { Warn "skipped (-SkipSkills)" }
else {
  $targets = [ordered]@{
    claude   = Join-Path $HOME '.claude\skills'
    codex    = Join-Path $HOME '.agents\skills'
    cursor   = Join-Path $HOME '.cursor\skills'
    copilot  = Join-Path $HOME '.copilot\skills'
    windsurf = Join-Path $HOME '.windsurf\skills'
  }
  if (-not $Agents) {
    $Agents = @($targets.Keys | Where-Object { Test-Path (Split-Path $targets[$_] -Parent) })
    if ($Agents -notcontains 'claude') { $Agents = @('claude') + $Agents }
  }
  $mainSkill = Join-Path $root 'SKILL.md'
  $skillDirs = @()
  if (Test-Path (Join-Path $root 'skills')) {
    $skillDirs = @(Get-ChildItem (Join-Path $root 'skills') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'SKILL.md') })
  }
  if (-not (Test-Path $mainSkill)) { Warn "SKILL.md not found in the repo; nothing to install." }
  else {
    foreach ($a in $Agents) {
      if (-not $targets.Contains($a)) { Warn "unknown agent '$a' (choose from $($targets.Keys -join ', '))"; continue }
      $dest = $targets[$a]
      $main = Join-Path $dest 'civil3dfactory'
      New-Item -ItemType Directory -Force $main | Out-Null
      (Get-Content $mainSkill -Raw -Encoding UTF8) -replace '\{\{C3DF_ROOT\}\}', $root |
        Set-Content (Join-Path $main 'SKILL.md') -Encoding UTF8 -NoNewline
      foreach ($d in $skillDirs) {
        $sd = Join-Path $dest $d.Name
        New-Item -ItemType Directory -Force $sd | Out-Null
        (Get-Content (Join-Path $d.FullName 'SKILL.md') -Raw -Encoding UTF8) -replace '\{\{C3DF_ROOT\}\}', $root |
          Set-Content (Join-Path $sd 'SKILL.md') -Encoding UTF8 -NoNewline
      }
      Ok ("{0}: {1}  (civil3dfactory + {2} specialized)" -f $a, $dest, $skillDirs.Count)
    }
  }
}

# ---------- 5. self-check ----------
Step "5/5 self-check"
if ($SkipCheck) { Warn "skipped (-SkipCheck)" }
elseif (-not $acc) { Warn "skipped: no Civil 3D found" }
else {
  $demo = Join-Path $root 'examples\channel-demo.dwg'
  $task = Join-Path $root 'examples\corridor\01-recon.json'
  if ((Test-Path $demo) -and (Test-Path $task)) {
    $result = Join-Path $env:TEMP 'civil3dfactory.selfcheck.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'civil3dfactory.ps1') -Dwg $demo -Task $task -Result $result *> $null
    $ok = $false
    if (Test-Path $result) { try { $ok = [bool]((Get-Content $result -Raw -Encoding UTF8 | ConvertFrom-Json).ok) } catch { } }
    if ($ok) { Ok "read-only reconnaissance of examples\channel-demo.dwg passed (result: $result)" }
    else { Warn "self-check failed; see $result and logs\runs\. Most likely cause: no engine for Civil 3D $($acc.Year), or Civil 3D needs a restart." }
  } else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'civil3dfactory.ps1') -Help *> $null
    if ($LASTEXITCODE -eq 0) { Ok "engine answers -Help (examples\ not present, skipped the drawing check)" } else { Warn "civil3dfactory.ps1 -Help failed" }
  }
}

Step "done"
Write-Host @"
  Next:
    civil3dfactory.ps1 -Help                                         list every op
    civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -View outline  see what the demo drawing contains
    civil3dfactory.ps1 -Dwg examples\channel-demo.dwg -Task examples\corridor\02-build.json
  Agents: load the skill "civil3dfactory" (installed above), then the specialized civil3dfactory-* skill for the task.
"@
