$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$case = Join-Path $repo ('TestResults/dsh-package-evidence/' + [Guid]::NewGuid().ToString('N'))
$zipRoot = Join-Path $case 'zip'
$seed = Join-Path $zipRoot 'tools/dsh-seed'
New-Item -ItemType Directory -Path $seed -Force | Out-Null
foreach ($file in @('package.json', 'package-lock.json')) {
    Copy-Item -LiteralPath (Join-Path $repo "tools/dsh-seed/$file") -Destination $seed
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $case 'candidate.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($zipRoot, $package)
$report = Join-Path $case 'candidate.json'
$version = (Get-Content -LiteralPath (Join-Path $seed 'package.json') -Raw | ConvertFrom-Json).dependencies.'@deepseek-ai/dsh'
$lockSha = (Get-FileHash -LiteralPath (Join-Path $seed 'package-lock.json') -Algorithm SHA256).Hash
@{ version = $version; seedLockSha256 = $lockSha; status = 'test fixture' } | ConvertTo-Json | Set-Content -LiteralPath $report -Encoding UTF8
$recorder = Join-Path $PSScriptRoot 'record-dsh-package.ps1'
& $recorder -Mode Candidate -Package $package -Report $report
$data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$acceptance = Join-Path $case 'acceptance'
New-Item -ItemType Directory -Path $acceptance | Out-Null
# Synthetic records only: no actual clean-machine acceptance is claimed.
foreach ($registry in @('official', 'npmmirror')) {
    @{ packageSha256 = $data.packageSha256; seedLockSha256 = $lockSha; installedVersion = $version;
        registry = $registry; cleanWindowsConfirmed = $true; readyConfirmed = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $acceptance "$registry.json") -Encoding UTF8
}
& $recorder -Mode Verify -Package $package -Report $report -AcceptanceDirectory $acceptance
$mirror = Join-Path $acceptance 'npmmirror.json'
$bad = Get-Content -LiteralPath $mirror -Raw | ConvertFrom-Json
$bad.packageSha256 = '0' * 64
$bad | ConvertTo-Json | Set-Content -LiteralPath $mirror -Encoding UTF8
try {
    & $recorder -Mode Verify -Package $package -Report $report -AcceptanceDirectory $acceptance
    throw 'Expected mismatched acceptance rejection'
} catch {
    if ($_.Exception.Message -notmatch 'Acceptance does not match') { throw }
}
try {
    & $recorder -Mode Accept -Package $package -Report $report -Registry official -AcceptanceDirectory $acceptance
    throw 'Expected missing manual attestation rejection'
} catch {
    if ($_.Exception.Message -notmatch 'explicit clean Windows') { throw }
}
$global:LASTEXITCODE = 0
Write-Host 'DSH package identity and acceptance binding tests passed (synthetic ZIP only).'

. (Join-Path $PSScriptRoot 'dsh-clean-windows.ps1')
$redirector = 'C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_test\AppInstallerPythonRedirector.exe'
function New-AliasFixture {
    param([string]$Family, [string]$Application, [string]$Target)
    $text = [Text.Encoding]::Unicode.GetBytes("$Family`0$Application`0$Target`0Desktop`0")
    $bytes = New-Object byte[] ($text.Length + 12)
    [BitConverter]::GetBytes([uint32]2147483675).CopyTo($bytes, 0)
    [BitConverter]::GetBytes([uint16]($text.Length + 4)).CopyTo($bytes, 4)
    [BitConverter]::GetBytes([uint32]3).CopyTo($bytes, 8)
    $text.CopyTo($bytes, 12)
    return ,$bytes
}
$family = 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe'
$valid = New-AliasFixture $family "$family!PythonRedirector" $redirector
if (-not (Test-DshAppInstallerAliasData $valid $redirector)) { throw 'App Installer placeholder was not recognized' }
foreach ($invalid in @(
    (New-AliasFixture 'PythonSoftwareFoundation.Python_abc' 'Python!App' 'C:\Program Files\WindowsApps\Python\python.exe'),
    (New-AliasFixture $family "$family!OtherApp" $redirector),
    (New-AliasFixture $family "$family!PythonRedirector" 'C:\Other\AppInstallerPythonRedirector.exe'),
    ([byte[]]@(1, 2, 3))
)) {
    if (Test-DshAppInstallerAliasData $invalid $redirector) { throw 'Real/unknown Python alias was incorrectly exempted' }
}
# Mock discovery only: placeholder followed by a real interpreter must still fail.
& {
    function Get-DshDevelopmentCommands {
        [pscustomobject]@{Name='python.exe'; Source='stub'}
        [pscustomobject]@{Name='python.exe'; Source='real'}
    }
    function Test-DshAppInstallerPlaceholder { param($Path) return $Path -eq 'stub' }
    try { Assert-DshNoDevelopmentCommands; throw 'Expected real interpreter rejection' }
    catch { if ($_.Exception.Message -notmatch 'Development command detected') { throw } }
}
Write-Host 'Python alias identity and shadowed-interpreter tests passed.'
