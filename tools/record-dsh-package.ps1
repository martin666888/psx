<#
Candidate: bind the actual ZIP and build inputs after Fast/build succeed.
Accept: record MANUAL clean-Windows/Ready attestations, independently for each registry.
Verify: check the same ZIP, both acceptance reports and the accepted source inputs.
This records evidence; it cannot prove a VM was clean or that a human saw Ready.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Candidate', 'Accept', 'Verify')][string]$Mode,
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Report,
    [ValidateSet('official', 'npmmirror')][string]$Registry,
    [string]$RuntimeDirectory,
    [string]$AcceptanceDirectory,
    [switch]$CleanWindowsConfirmed,
    [switch]$ReadyConfirmed
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$data = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
$packageSha = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
try {
    $lockEntries = @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq 'tools/dsh-seed/package-lock.json' })
    if ($lockEntries.Count -ne 1) { throw 'ZIP must have exactly one DSH seed lock' }
    $lockEntry = $lockEntries[0]
    $stream = $lockEntry.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $lockSha = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hasher.Dispose() }
    $manifestEntries = @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq 'tools/dsh-seed/package.json' })
    if ($manifestEntries.Count -ne 1) { throw 'ZIP must have exactly one DSH seed manifest' }
    $manifestEntry = $manifestEntries[0]
    $reader = New-Object IO.StreamReader($manifestEntry.Open())
    try { $version = ($reader.ReadToEnd() | ConvertFrom-Json).dependencies.'@deepseek-ai/dsh' }
    finally { $reader.Dispose() }
} finally { $zip.Dispose() }
if ($lockSha -ne $data.seedLockSha256 -or $version -ne $data.version) { throw 'ZIP seed differs from reviewed candidate' }
if ($Mode -eq 'Candidate') {
    $sourceHashes = [ordered]@{}
    $files = @(git -C $repo ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate build inputs' }
    foreach ($file in $files) {
        $full = Join-Path $repo $file
        if (Test-Path -LiteralPath $full -PathType Leaf) { $sourceHashes[$file] = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
    }
    $data | Add-Member -Force NoteProperty packageSha256 $packageSha
    $data | Add-Member -Force NoteProperty buildInputHashes $sourceHashes
    $data.status = 'CI Fast and package build passed; clean Windows first-install acceptance pending'
    $data | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Report -Encoding UTF8
    return
}
if ($packageSha -ne $data.packageSha256) { throw 'ZIP changed: rebuild and repeat acceptance' }
if ([string]::IsNullOrWhiteSpace($AcceptanceDirectory)) { throw 'AcceptanceDirectory is required' }
if ($Mode -eq 'Accept') {
    if (-not $CleanWindowsConfirmed -or -not $ReadyConfirmed -or -not $Registry -or -not $RuntimeDirectory) {
        throw 'Require registry, runtime directory and explicit clean Windows / Ready confirmations'
    }
    . (Join-Path $PSScriptRoot 'dsh-clean-windows.ps1')
    Assert-DshNoDevelopmentCommands
    $installed = Get-Content -LiteralPath (Join-Path $RuntimeDirectory 'node_modules/@deepseek-ai/dsh/package.json') -Raw | ConvertFrom-Json
    if ($installed.version -ne $version) { throw 'Installed version differs from ZIP seed' }
    $installedLock = (Get-FileHash -LiteralPath (Join-Path $RuntimeDirectory 'package-lock.json') -Algorithm SHA256).Hash
    if ($installedLock -ne $lockSha) { throw 'Installed lock differs from ZIP seed' }
    New-Item -ItemType Directory -Path $AcceptanceDirectory -Force | Out-Null
    [ordered]@{ packageSha256 = $packageSha; seedLockSha256 = $lockSha; installedVersion = $version;
        registry = $Registry; cleanWindowsConfirmed = $true; readyConfirmed = $true;
        utc = [DateTime]::UtcNow.ToString('o'); evidence = 'manual acceptance on disposable clean Windows x64' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $AcceptanceDirectory "$Registry.json") -Encoding UTF8
    return
}
foreach ($source in $data.buildInputHashes.PSObject.Properties) {
    $full = [IO.Path]::GetFullPath((Join-Path $repo $source.Name))
    if (-not $full.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid source path' }
    if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $source.Value) { throw "Build input changed: $($source.Name)" }
}
$currentFiles = @(git -C $repo ls-files --cached --others --exclude-standard | Where-Object { Test-Path -LiteralPath (Join-Path $repo $_) -PathType Leaf })
if ($LASTEXITCODE -ne 0 -or (Compare-Object @($data.buildInputHashes.PSObject.Properties.Name) $currentFiles)) { throw 'Build input file set changed' }
foreach ($source in @('official', 'npmmirror')) {
    $acceptance = Get-Content -LiteralPath (Join-Path $AcceptanceDirectory "$source.json") -Raw | ConvertFrom-Json
    if ($acceptance.packageSha256 -ne $packageSha -or $acceptance.seedLockSha256 -ne $lockSha -or
        $acceptance.installedVersion -ne $version -or $acceptance.registry -ne $source -or
        $acceptance.cleanWindowsConfirmed -ne $true -or $acceptance.readyConfirmed -ne $true) { throw 'Acceptance does not match package' }
}
Write-Host 'Same candidate ZIP, both registry acceptances and accepted build inputs verified. Run remaining formal release checks against this ZIP.'
