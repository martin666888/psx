# Resolves the Node 22 toolchain used by the DSH probes, mirroring
# tools/test.ps1's candidate order: PATH node -> TestResults/node22 ->
# bin/release-staging. Dot-source this file; it sets $ProbeNodePath and
# $ProbeNpmCli in the caller's scope.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$pathNodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
$pathNode = if ($null -eq $pathNodeCommand) { $null } else { $pathNodeCommand.Source }
$nodeCandidates = @(
    $pathNode,
    (Join-Path $repoRoot 'TestResults\node22\node-v22.23.1-win-x64\node.exe'),
    (Join-Path $repoRoot 'bin\release-staging\tools\node\node.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$nodePath = $null
foreach ($candidate in $nodeCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $version = & $candidate --version
    if ($LASTEXITCODE -eq 0 -and $version -match '^v22\.') {
        $nodePath = [IO.Path]::GetFullPath($candidate)
        break
    }
}
if ($null -eq $nodePath) {
    throw 'DSH probes require Node 22 (PATH, TestResults/node22 or release-staging).'
}

$npmCli = Join-Path (Split-Path -Parent $nodePath) 'node_modules\npm\bin\npm-cli.js'
if (-not (Test-Path -LiteralPath $npmCli -PathType Leaf)) {
    throw "npm-cli.js not found beside Node 22: $npmCli"
}

$ProbeNodePath = $nodePath
$ProbeNpmCli = $npmCli
Write-Host "[toolchain] node: $nodePath ($(& $nodePath --version))"
