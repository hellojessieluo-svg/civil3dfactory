$ErrorActionPreference = 'Stop'
$Project = Split-Path -Parent $MyInvocation.MyCommand.Path
$Python = (Get-Command python -ErrorAction SilentlyContinue).Source   # python 3.10+ on PATH
$Deps = Join-Path $Project '.build-deps'
$Dist = Join-Path $Project 'output'
$Work = Join-Path $Project '.build'
$env:PYTHONUTF8 = '1'
$env:PIP_PROGRESS_BAR = 'off'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'

if (-not (Test-Path -LiteralPath $Python)) {
    throw "Build Python not found: $Python"
}

$PluginProject = Join-Path $Project 'CadBatchPlugin\CadBatchPlugin.csproj'
$PluginOutput = Join-Path $Project 'CadBatchPlugin\bin\hotload'
dotnet build $PluginProject -c Release -o $PluginOutput
if ($LASTEXITCODE -ne 0) { throw 'CAD plugin build failed' }
$PluginDll = Join-Path $PluginOutput 'CadBatchPlugin.dll'

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

Write-Host "Done: $(Join-Path $Dist 'DWGAttributeEditor.exe')"
