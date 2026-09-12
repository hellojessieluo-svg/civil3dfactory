# Build DWGAttributeEditor.exe (ASCII-only script; PS 5.1 mangles non-BOM UTF-8).
# Steps: build the accoreconsole plugin -> install Python deps into .build-deps ->
# PyInstaller one-file EXE into output\ -> copy the EXE to the tool folder root,
# which is where install.ps1 and the nodes look for it.
$ErrorActionPreference = 'Stop'
$Project = Split-Path -Parent $MyInvocation.MyCommand.Path
$Python = (Get-Command python -ErrorAction SilentlyContinue).Source   # python 3.10+ on PATH
$Deps = Join-Path $Project '.build-deps'
$Dist = Join-Path $Project 'output'
$Work = Join-Path $Project '.build'
$ExeName = 'DWGAttributeEditor.exe'
$env:PYTHONUTF8 = '1'
$env:PIP_PROGRESS_BAR = 'off'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'

if (-not $Python -or -not (Test-Path -LiteralPath $Python)) {
    throw "Build Python not found on PATH (need python 3.10+)"
}

# Build the accoreconsole attribute plugin and bundle its DLL into the EXE.
$PluginProject = Join-Path $Project 'CadBatchPlugin\CadBatchPlugin.csproj'
$PluginOutput = Join-Path $Project 'CadBatchPlugin\bin\hotload'
dotnet build $PluginProject -c Release -o $PluginOutput
if ($LASTEXITCODE -ne 0) { throw 'CAD plugin build failed' }
$PluginDll = Join-Path $PluginOutput 'CadBatchPlugin.dll'

# Install PyInstaller + PySide6 into a private folder the first time only.
if ((-not (Test-Path -LiteralPath (Join-Path $Deps 'PyInstaller'))) -or `
    (-not (Test-Path -LiteralPath (Join-Path $Deps 'PySide6')))) {
    & $Python -m pip install --target $Deps -r (Join-Path $Project 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
}

$env:PYTHONPATH = $Deps
& $Python -m PyInstaller --noconfirm --clean --onefile --windowed `
    --name 'DWGAttributeEditor' `
    --distpath $Dist `
    --workpath $Work `
    --specpath $Work `
    --add-data "$PluginDll;." `
    (Join-Path $Project 'dwg_attribute_editor.py')
if ($LASTEXITCODE -ne 0) { throw 'EXE build failed' }

# Copy the EXE from output\ to the tool folder root (install.ps1 and the nodes expect it there).
$Built = Join-Path $Dist $ExeName
if (-not (Test-Path -LiteralPath $Built)) { throw "EXE not found after build: $Built" }
Copy-Item -LiteralPath $Built -Destination (Join-Path $Project $ExeName) -Force

Write-Host "Done: $(Join-Path $Project $ExeName)"
