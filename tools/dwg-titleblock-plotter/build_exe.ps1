# Build DWGTitleblockPlotter.exe (ASCII-only script; PS 5.1 mangles non-BOM UTF-8).
# Steps: build the accoreconsole plugin -> install Python deps into .build-deps ->
# PyInstaller one-file EXE into output\ -> copy the EXE to the tool folder root,
# which is where install.ps1 and the nodes look for it.
# Use Continue, not Stop: PyInstaller writes INFO to stderr and PS 5.1 wraps
# native stderr as a terminating error under Stop. Each critical step below has
# an explicit $LASTEXITCODE / Test-Path guard instead.
$ErrorActionPreference = 'Continue'
$Project = Split-Path -Parent $MyInvocation.MyCommand.Path
$Python = (Get-Command python -ErrorAction SilentlyContinue).Source   # python 3.10+ on PATH
$Deps = Join-Path $Project '.build-deps'
$Dist = Join-Path $Project 'output'
$Work = Join-Path $Project '.build'
$ExeName = 'DWGTitleblockPlotter.exe'
$env:PYTHONUTF8 = '1'
$env:PIP_PROGRESS_BAR = 'off'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'

if (-not $Python -or -not (Test-Path -LiteralPath $Python)) {
    throw "Build Python not found on PATH (need python 3.10+)"
}

# Build the accoreconsole plotting plugin and bundle its DLL into the EXE.
$PluginProject = Join-Path $Project 'CadPlotPlugin\CadPlotPlugin.csproj'
$PluginOutput = Join-Path $Project 'CadPlotPlugin\bin\hotload'
dotnet build $PluginProject -c Release -o $PluginOutput
if ($LASTEXITCODE -ne 0) { throw 'CAD plot plugin build failed' }
$PluginDll = Join-Path $PluginOutput 'CadPlotPlugin.dll'

# Install PyInstaller + PySide6 + pywin32 into a private folder the first time only.
if ((-not (Test-Path -LiteralPath (Join-Path $Deps 'PyInstaller'))) -or `
    (-not (Test-Path -LiteralPath (Join-Path $Deps 'PySide6'))) -or `
    (-not (Test-Path -LiteralPath (Join-Path $Deps 'win32com')))) {
    & $Python -m pip install --target $Deps -r (Join-Path $Project 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
}

# pywin32 installed via --target does not run pywin32.pth, so wire up the
# module + DLL search paths manually.
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

# Copy the EXE from output\ to the tool folder root (install.ps1 and the nodes expect it there).
$Built = Join-Path $Dist $ExeName
if (-not (Test-Path -LiteralPath $Built)) { throw "EXE not found after build: $Built" }
Copy-Item -LiteralPath $Built -Destination (Join-Path $Project $ExeName) -Force

Write-Host "Done: $(Join-Path $Project $ExeName)"
