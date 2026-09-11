# Execute the actual release audit block with a fake registry, without packaging.
param([string]$NodePath = 'node')
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build-release.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Release script parse failed' }
$audit = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if ($RequireLatestDsh)')
}, $false)
if ($null -eq $audit) { throw 'Missing opt-in release audit' }
$block = [scriptblock]::Create($audit.Extent.Text)
$nodeExe = 'Invoke-FakeNode'
$npmCli = 'fake-npm'
$DshPinnedVersion = '1.0.0'
function Invoke-FakeNode {
    if ($args[0] -eq 'fake-npm') {
        $script:queries++
        $global:LASTEXITCODE = $script:registryExit
        return '["1.0.0","1.1.0-rc.1"]'
    }
    & $NodePath @args
}
foreach ($case in @(
    @{ required = $false; entries = @(); blocked = @(); exit = 1; fails = $false },
    @{ required = $true; entries = @('1.1.0-rc.1'); blocked = @(); exit = 0; fails = $false },
    @{ required = $true; entries = @(); blocked = @('1.1.0-rc.1'); exit = 0; fails = $false },
    @{ required = $true; entries = @(); blocked = @(); exit = 0; fails = $true },
    @{ required = $true; entries = @('1.1.0-rc.1'); blocked = @(); exit = 1; fails = $true }
)) {
    $RequireLatestDsh = $case.required
    $dshEntryVersions = $case.entries
    $dshBlockedVersions = $case.blocked
    $script:registryExit = $case.exit
    $script:queries = 0
    $failed = $false
    try { & $block } catch { $failed = $true }
    if ($failed -ne $case.fails -or $script:queries -ne [int]$case.required) { throw 'Release audit behavior mismatch' }
}
try {
    & (Join-Path $PSScriptRoot 'build-release.ps1') -RequireLatestDsh -SkipDshFreshnessCheck
    throw 'Expected parameter conflict'
} catch {
    if ($_.Exception.Message -notmatch 'cannot be combined') { throw }
}
$global:LASTEXITCODE = 0
Write-Host 'DSH release policy tests passed.'
