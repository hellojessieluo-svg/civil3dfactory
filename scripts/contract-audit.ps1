<#
  Contract audit - compares the parameters each node declares in nodes\node.json with the
  parameter keys its implementation actually reads. -Help prints contracts, so they must be true.

    scripts\contract-audit.ps1                 all nodes
    scripts\contract-audit.ps1 -Node offset_alignment
    scripts\contract-audit.ps1 -Strict         exit 1 when any node drifts (used before a release)

  Heuristics:
    C#  nodes  - keys are taken from nodes\<id>\*.cs via Need(a,"key") / Get*(a,"key") / a["key"]
    PS  nodes  - the param() block of nodes\<id>\*.ps1 (parsed with the PowerShell AST),
                 PascalCase folded to the contract's snake_case (-OutputDirectory -> output_directory)
  Thin adapters whose implementation lives in engine\Ops*.cs yield no keys and are reported as [manual].
#>
param([string]$Node, [switch]$Strict)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nodes = Get-Content (Join-Path $root 'nodes\node.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($Node) { $nodes = @($nodes | Where-Object { $_.id -eq $Node }) }

$patterns = @(
  'Need\(\s*\w+\s*,\s*"([^"]+)"',
  'Get(?:String|Double|Bool|Int|Num)\w*\(\s*\w+\s*,\s*"([^"]+)"',
  '\b(?:a|args)\["([^"]+)"\]'
)

function ConvertTo-ContractKey { param([string]$Name) return ([regex]::Replace($Name, '(?<=[a-z0-9])(?=[A-Z])', '_')).ToLowerInvariant() }

# Contracts declare the drawing input as inputs.drawing(s); PowerShell nodes receive it as -Dwg. Same thing, not drift.
$dwgAliases = @('drawing', 'drawings', 'dwg')

$flagged = 0; $manual = 0
foreach ($n in $nodes) {
  $declared = @()
  foreach ($sect in @($n.inputs, $n.parameters)) { if ($sect) { $declared += $sect.PSObject.Properties.Name } }

  $used = New-Object System.Collections.Generic.HashSet[string]
  $dir = Join-Path $root ('nodes\' + $n.id)
  foreach ($f in @(Get-ChildItem $dir -Filter *.cs -Recurse -ErrorAction SilentlyContinue)) {
    $text = Get-Content $f.FullName -Raw -Encoding UTF8
    foreach ($p in $patterns) { foreach ($m in [regex]::Matches($text, $p)) { [void]$used.Add($m.Groups[1].Value) } }
  }
  foreach ($f in @(Get-ChildItem $dir -Filter *.ps1 -Recurse -ErrorAction SilentlyContinue)) {
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) { Write-Host ("[skip] {0}: {1} does not parse" -f $n.id, $f.Name) -ForegroundColor Red; continue }
    if ($null -eq $ast.ParamBlock) { continue }
    foreach ($p in $ast.ParamBlock.Parameters) { [void]$used.Add((ConvertTo-ContractKey $p.Name.VariablePath.UserPath)) }
  }
  if ($used.Contains('dwg')) {
    $declaredDwg = @($declared | Where-Object { $dwgAliases -contains $_ })
    if ($declaredDwg.Count -gt 0) { [void]$used.Remove('dwg'); [void]$used.Add($declaredDwg[0]) }
  }

  if ($used.Count -eq 0) {
    $manual++
    Write-Host ("[manual]  {0}  thin adapter, implementation lives in engine\Ops*.cs" -f $n.id) -ForegroundColor DarkGray
    continue
  }
  $undeclared = @($used | Where-Object { $declared -notcontains $_ } | Sort-Object)
  $unused = @($declared | Where-Object { -not $used.Contains($_) } | Sort-Object)
  if ($undeclared.Count -eq 0 -and $unused.Count -eq 0) {
    Write-Host ("[aligned] {0}" -f $n.id) -ForegroundColor Green
  } else {
    $flagged++
    Write-Host ("[drift]   {0}" -f $n.id) -ForegroundColor Yellow
    if ($undeclared.Count -gt 0) { Write-Host ("          read by the implementation but not declared: {0}" -f ($undeclared -join ', ')) -ForegroundColor Yellow }
    if ($unused.Count -gt 0) { Write-Host ("          declared but not read here (may be consumed by shared code): {0}" -f ($unused -join ', ')) }
  }
}
Write-Host ""
Write-Host ("{0} node(s): {1} drift, {2} manual" -f @($nodes).Count, $flagged, $manual)
if ($Strict -and $flagged -gt 0) { exit 1 }
exit 0
