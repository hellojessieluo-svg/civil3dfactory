<#
  Build the engine for every Civil 3D generation and install it as an Autodesk bundle.
  Maintainers only (needs .NET SDKs); users get the prebuilt bundle from GitHub Releases via install.ps1.

    scripts\build.ps1                       build all targets the installed SDKs can build, install the bundle
    scripts\build.ps1 -Targets net8.0-windows
    scripts\build.ps1 -Release              also stage dist\ (bundle zip + exe tools) and tag v<version> when the tree is clean

  Targets (engine\Civil3DFactory.csproj):
    net472            -> Contents\2022-2024   Civil 3D 2022 / 2023 / 2024 (.NET Framework 4.8)
    net8.0-windows    -> Contents\2025-2026   Civil 3D 2025 / 2026       (.NET 8)
    net10.0-windows   -> Contents\2027        Civil 3D 2027              (.NET 10)

  The version is the <Version> element of the csproj; bump it there.
#>
param(
  [string[]]$Targets = @("net472", "net8.0-windows", "net10.0-windows"),
  [switch]$Release,
  [switch]$SkipInstall,
  [switch]$NoTag
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "engine\Civil3DFactory.csproj"
$outRoot = Join-Path $root "engine\bin\out"
$bundle = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\Civil3DFactory.bundle"

$generationOf = @{ "net472" = "2022-2024"; "net8.0-windows" = "2025-2026"; "net10.0-windows" = "2027" }
$sdkMajorOf   = @{ "net472" = 8; "net8.0-windows" = 8; "net10.0-windows" = 10 }   # net472 builds with the .NET 8 SDK (reference assemblies come from NuGet)

[xml]$csproj = Get-Content -LiteralPath $proj -Raw -Encoding UTF8
$version = [string]$csproj.Project.PropertyGroup.Version
if (-not $version) { $version = ([string]($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)) }
if ($version -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z.-]+)?$') { throw "csproj <Version> is not a semantic version: '$version'" }

$commit = "nogit"; $shortCommit = "nogit"; $branch = ""; $dirty = $true
$repoRoot = (& git -C $root rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -eq 0 -and $repoRoot) {
  $head = (& git -C $repoRoot rev-parse -q --verify HEAD 2>$null)
  if ($LASTEXITCODE -eq 0 -and $head) {
    $commit = $head.Trim()
    $shortCommit = $commit.Substring(0, 8)
    $branch = [string](& git -C $repoRoot branch --show-current 2>$null)
    $dirty = (@(& git -C $repoRoot status --porcelain -- (Join-Path $root "engine") (Join-Path $root "nodes"))).Count -gt 0
  }
}
$informational = "$version+$shortCommit"; if ($dirty) { $informational += ".dirty" }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw "dotnet SDK not found. Install .NET 8 SDK (and .NET 10 SDK for Civil 3D 2027): https://dotnet.microsoft.com/download" }
$sdks = @(& $dotnet --list-sdks 2>$null | ForEach-Object { [int](($_ -split '\.')[0]) })

$built = @()
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })   # "-Targets a,b" via powershell -File arrives as one string
foreach ($tfm in $Targets) {
  if (-not $generationOf.ContainsKey($tfm)) { throw "Unknown target '$tfm'. Use net472, net8.0-windows, net10.0-windows." }
  if ($sdks -notcontains $sdkMajorOf[$tfm]) {
    Write-Warning "Skipping $tfm - .NET $($sdkMajorOf[$tfm]) SDK is not installed."
    continue
  }
  $out = Join-Path $outRoot $tfm
  Write-Host "`n== build $tfm -> $out" -ForegroundColor Cyan
  # -p:TargetFrameworks=<tfm> restricts restore to this target, so a missing SDK for another target does not break this one.
  & $dotnet build $proj -c Release -o $out "-p:TargetFrameworks=$tfm" "-p:Version=$version" "-p:InformationalVersion=$informational" -nologo -v minimal
  if ($LASTEXITCODE -ne 0) { throw "Build failed for $tfm." }
  $built += [pscustomobject]@{ Tfm = $tfm; Generation = $generationOf[$tfm]; Out = $out }
}
if ($built.Count -eq 0) { throw "Nothing was built." }

# ---------- bundle ----------
function Copy-EngineOutput([string]$From, [string]$To) {
  New-Item -ItemType Directory -Force $To | Out-Null
  Get-ChildItem -LiteralPath $To -File | Remove-Item -Force
  # Autodesk assemblies are reference-only and must never ship; everything else the engine needs (System.Text.Json on net472) does.
  Get-ChildItem -LiteralPath $From -File | Where-Object {
    $_.Extension -in ".dll", ".json", ".pdb" -and $_.Name -notmatch '^(Ac|Aec|Autodesk)'
  } | Copy-Item -Destination $To -Force
}

function Write-Bundle([string]$BundleDir) {
  foreach ($b in $built) { Copy-EngineOutput -From $b.Out -To (Join-Path $BundleDir "Contents\$($b.Generation)") }
  [xml]$package = Get-Content -LiteralPath (Join-Path $root "engine\PackageContents.xml") -Raw -Encoding UTF8
  $package.ApplicationPackage.AppVersion = $version
  $package.Save((Join-Path $BundleDir "PackageContents.xml"))
  $manifest = [ordered]@{
    schema_version = 2
    version = $version
    informational_version = $informational
    git_commit = $commit
    git_short_commit = $shortCommit
    git_branch = $branch
    source_dirty = $dirty
    built_at = (Get-Date).ToString("o")
    generations = @($built | ForEach-Object { $_.Generation })
    source_path = $root
  }
  ($manifest | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $BundleDir "current-version.json") -Encoding UTF8
}

if (-not $SkipInstall) {
  Write-Host "`n== install bundle -> $bundle" -ForegroundColor Cyan
  Write-Bundle -BundleDir $bundle
  Write-Host "Installed  : $bundle  (restart Civil 3D if it is open)"
}

# ---------- release staging ----------
if ($Release) {
  $dist = Join-Path $root "dist"
  $stage = Join-Path $dist "Civil3DFactory.bundle"
  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
  New-Item -ItemType Directory -Force $dist | Out-Null
  Write-Bundle -BundleDir $stage
  $zip = Join-Path $dist "Civil3DFactory.bundle.zip"
  if (Test-Path $zip) { Remove-Item -Force $zip }
  Compress-Archive -Path $stage -DestinationPath $zip
  Write-Host "Release    : $zip"
  foreach ($tool in "dwg-attribute-editor\DWGAttributeEditor.exe", "dwg-titleblock-plotter\DWGTitleblockPlotter.exe") {
    $exe = Join-Path $root "tools\$tool"
    if (Test-Path -LiteralPath $exe) { Copy-Item -LiteralPath $exe -Destination $dist -Force; Write-Host "Release    : $(Join-Path $dist (Split-Path $tool -Leaf))" }
    else { Write-Warning "Missing $exe - run tools\<tool>\build_exe.ps1 before publishing the release." }
  }
  if (-not $NoTag -and $commit -ne "nogit") {
    $tag = "v$version"
    $existing = (& git -C $repoRoot rev-parse -q --verify "refs/tags/$tag" 2>$null)
    if ($dirty) { Write-Warning "Working tree is dirty; not tagging $tag. Commit first." }
    elseif ($existing -and $existing.Trim() -ne $commit) { Write-Warning "Tag $tag already points elsewhere; bump <Version> in the csproj." }
    elseif (-not $existing) { & git -C $repoRoot tag -a $tag -m "civil3dfactory $version" $commit; Write-Host "Tagged     : $tag" }
  }
}

Write-Host "Version    : $version ($informational)"
Write-Host ("Targets    : " + (($built | ForEach-Object { "$($_.Tfm) -> $($_.Generation)" }) -join ", "))
