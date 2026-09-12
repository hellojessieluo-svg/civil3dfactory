<#
  Blacklist gate - the pre-commit hook of this repository (scripts\githooks\pre-commit calls it).
  Also runnable by hand:
    scripts\gate.ps1            check the staged files
    scripts\gate.ps1 -All       check the whole working tree (run before a release)

  What is rejected (exit 1):
    1. the legacy two-letter engine prefix in any casing (the engine was renamed; nothing may still carry it)
    2. absolute paths of a developer machine (a drive letter followed by a user folder) - only the Autodesk program folder is allowed
    3. CJK characters (the repository is English only) - severity comes from gate.config.json ("error" | "warn")
    4. private terms listed in scripts\blacklist.local.txt (one per line, git-ignored, maintained by each maintainer)
    5. a new top-level entry that is not in the allowed root list (the root answers eight questions, no more)

  Binary files (dwg, pkt, exe, ico, png, zip) are skipped by the text scans but still subject to rule 5.
#>
param([switch]$All)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$root = Split-Path -Parent $PSScriptRoot
$fail = New-Object System.Collections.Generic.List[string]
$warn = New-Object System.Collections.Generic.List[string]

$config = @{ cjk = "warn" }
$configPath = Join-Path $PSScriptRoot "gate.config.json"
if (Test-Path -LiteralPath $configPath) {
  try { $cfg = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json; if ($cfg.cjk) { $config.cjk = [string]$cfg.cjk } } catch { }
}

$rootAllow = @('README.md', 'SKILL.md', 'LICENSE', 'CONTRIBUTING.md', 'civil3dfactory.ps1', 'install.ps1', '.gitignore', '.gitattributes',
               'engine', 'nodes', 'skills', 'examples', 'tools', 'scripts', 'docs')
$textExt = @('.cs', '.ps1', '.json', '.py', '.md', '.txt', '.xml', '.csproj', '.sh', '.yml', '.yaml', '.gitignore', '.gitattributes', '.lisp', '.lsp', '.scr')

if ($All) {
  $files = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
} else {
  $files = @(& git -C $root -c core.quotepath=false diff --cached --name-only --diff-filter=ACMR)
}
$files = @($files | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $root $_)) -and ($_ -ne 'scripts/gate.ps1') })   # the gate must not trip on its own rules

$private = @()
$privatePath = Join-Path $PSScriptRoot "blacklist.local.txt"
if (Test-Path -LiteralPath $privatePath) {
  $private = @(Get-Content -LiteralPath $privatePath -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}

$legacyRegex = [regex]('(?i)' + 'j' + 'l')
$pathRegex   = [regex]('(?i)\b[a-z]:' + '\\(?!Program Files\\Autodesk\\)[^\s"''<>|]+|\b[a-z]:/(?!Program Files/Autodesk/)[^\s"''<>|]+|\\\\Users\\|/Users/')
$cjkRegex  = [regex]'[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF]'

foreach ($rel in $files) {
  $full = Join-Path $root $rel
  $ext = [System.IO.Path]::GetExtension($rel).ToLowerInvariant()
  $name = [System.IO.Path]::GetFileName($rel)
  if (($textExt -notcontains $ext) -and ($textExt -notcontains $name)) { continue }
  $lines = Get-Content -LiteralPath $full -Encoding UTF8
  $n = 0
  $cjkHits = 0
  foreach ($line in $lines) {
    $n++
    if ($legacyRegex.IsMatch($line)) { $fail.Add(("{0}:{1}: legacy identifier: {2}" -f $rel, $n, $line.Trim())) }
    if ($pathRegex.IsMatch($line)) { $fail.Add(("{0}:{1}: developer-machine path: {2}" -f $rel, $n, $line.Trim())) }
    foreach ($term in $private) {
      if ($line.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $fail.Add(("{0}:{1}: private term '{2}'" -f $rel, $n, $term)) }
    }
    if ($cjkRegex.IsMatch($line)) { $cjkHits++ }
  }
  if ($cjkHits -gt 0) {
    $msg = ("{0}: {1} line(s) with CJK characters" -f $rel, $cjkHits)
    if ($config.cjk -eq "error") { $fail.Add($msg) } else { $warn.Add($msg) }
  }
}

# --- root hygiene
$topLevel = @($files | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
foreach ($t in $topLevel) {
  if ($rootAllow -notcontains $t) { $fail.Add(("root entry '{0}' is not allowed. Allowed: {1}" -f $t, ($rootAllow -join ', '))) }
}

if ($warn.Count -gt 0) {
  Write-Host ""
  $warn | ForEach-Object { Write-Host (" ! " + $_) -ForegroundColor DarkYellow }
}
if ($fail.Count -gt 0) {
  Write-Host ""
  Write-Host "== Blocked by scripts\gate.ps1 ==" -ForegroundColor Red
  $fail | Select-Object -First 200 | ForEach-Object { Write-Host (" x " + $_) -ForegroundColor Yellow }
  if ($fail.Count -gt 200) { Write-Host (" ... and {0} more" -f ($fail.Count - 200)) -ForegroundColor Yellow }
  Write-Host ""
  exit 1
}
Write-Host ("[gate] {0} file(s) checked, clean." -f $files.Count) -ForegroundColor Green
exit 0
