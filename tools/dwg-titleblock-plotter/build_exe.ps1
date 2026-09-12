# Build DWGTitleblockPlotter.exe (ASCII-only; PS 5.1 mangles non-BOM UTF-8).
# Use Continue, not Stop: PyInstaller writes INFO to stderr and PS 5.1 wraps
# native stderr as a terminating error under Stop. Each critical step below has
# an explicit $LASTEXITCODE / Test-Path guard instead.
$ErrorActionPreference = 'Continue'
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

# Build the accoreconsole plotting plugin and bundle its DLL into the EXE.
$PluginProject = Join-Path $Project 'CadPlotPlugin\CadPlotPlugin.csproj'
$PluginOutput = Join-Path $Project 'CadPlotPlugin\bin\hotload'
dotnet build $PluginProject -c Release -o $PluginOutput
if ($LASTEXITCODE -ne 0) { throw 'CAD plot plugin build failed' }
$PluginDll = Join-Path $PluginOutput 'CadPlotPlugin.dll'

if ((-not (Test-Path -LiteralPath (Join-Path $Deps 'PyInstaller'))) -or `
    (-not (Test-Path -LiteralPath (Join-Path $Deps 'PySide6'))) -or `
    (-not (Test-Path -LiteralPath (Join-Path $Deps 'win32com')))) {
    & $Python -m pip install --target $Deps -r (Join-Path $Project 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
}

# pywin32 installed via --target does not run pywin32.pth, so wire up the
# module + DLL search paths manually (see 02 project pitfalls).
$Win32 = Join-Path $Deps 'win32'
$Win32Lib = Join-Path $Deps 'win32\lib'
$Pythonwin = Join-Path $Deps 'pythonwin'
$Pywin32System32 = Join-Path $Deps 'pywin32_system32'
$env:PYTHONPATH = ($Deps, $Win32, $Win32Lib, $Pythonwin) -join ';'
$env:PATH = "$Pywin32System32;$env:PATH"

& $Python -m PyInstaller --noconfirm --clean --onefile --windowed `
    --name 'DWGTitleblockPlotter' `
    --icon (Join-Path $Project 'app.ico') `
    --distpath $Dist `
    --workpath $Work `
    --specpath $Work `
    --paths $Win32 `
    --paths $Win32Lib `
    --hidden-import 'win32com.client' `
    --hidden-import 'pythoncom' `
    --hidden-import 'pywintypes' `
    --add-data "$PluginDll;." `
    --add-data "$(Join-Path $Project 'app.ico');." `
    (Join-Path $Project 'titleblock_plotter.py')
if ($LASTEXITCODE -ne 0) { throw 'EXE build failed' }

Write-Host "Done: $(Join-Path $Dist 'DWGTitleblockPlotter.exe')"
