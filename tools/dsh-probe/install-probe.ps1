# DSH P0 probe - Cluster 2: pinned npm install WITH lifecycle scripts
# (node-pty / koffi native modules) for the locked preview version, then
# validate entry / native artifacts / prebuilt evidence. This answers the
# veto question "no VS Build Tools required on a clean Windows box".
# Artifacts: TestResults/dsh-probe/ (install.log, install-err.log,
# install-report.json). Product code untouched.
param(
    [switch]$SkipInstall,
    # Local HTTP proxy for npm registry traffic (e.g. Clash on 7897).
    # Use -Proxy '' to disable.
    [string]$Proxy = 'http://127.0.0.1:7897'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'resolve-toolchain.ps1')
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$stateDir = Join-Path $repoRoot 'TestResults\dsh-probe'
$installDir = Join-Path $stateDir 'install'
$logFile = Join-Path $stateDir 'install.log'
$errFile = Join-Path $stateDir 'install-err.log'
$reportFile = Join-Path $stateDir 'install-report.json'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

$report = [ordered]@{
    probe = 'dsh-install'
    nodeVersion = (& $ProbeNodePath --version)
    proxy = if ($Proxy) { $Proxy } else { 'none' }
    installDir = $installDir
    started = (Get-Date).ToString('o')
}

if (-not $SkipInstall) {
    Write-Host "[probe] npm install (lifecycle scripts enabled) into $installDir"
    if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    @'
{
  "name": "psx-dsh-probe-install",
  "private": true,
  "version": "0.0.0",
  "dependencies": {
    "@deepseek-ai/dsh": "0.1.2-rc.1"
  }
}
'@ | Set-Content -Encoding UTF8 (Join-Path $installDir 'package.json')
    $npmrcLines = @('registry=https://registry.npmjs.org/')
    if ($Proxy) {
        $npmrcLines += "proxy=$Proxy"
        $npmrcLines += "https-proxy=$Proxy"
    }
    $npmrcLines | Set-Content -Encoding ASCII (Join-Path $installDir '.npmrc')
    $env:npm_config_cache = Join-Path $stateDir 'npm-cache'
    $env:npm_config_loglevel = 'info'
    if ($Proxy) {
        $env:HTTP_PROXY = $Proxy
        $env:HTTPS_PROXY = $Proxy
        $env:http_proxy = $Proxy
        $env:https_proxy = $Proxy
    }

    $proc = Start-Process -FilePath $ProbeNodePath `
        -ArgumentList @($ProbeNpmCli, 'install', '--no-audit', '--no-fund') `
        -WorkingDirectory $installDir `
        -NoNewWindow `
        -RedirectStandardOutput $logFile `
        -RedirectStandardError $errFile `
        -PassThru
    Write-Host "[probe] npm pid = $($proc.Id), waiting (up to 20 minutes)..."
    $exited = $proc.WaitForExit(1200000)
    if (-not $exited) {
        $proc.Kill($true)
        throw 'npm install timed out after 20 minutes.'
    }
    $report.exitCode = $proc.ExitCode
    if ($null -eq $report.exitCode) {
        # PowerShell's Start-Process + -NoNewWindow redirection loses the exit
        # code on some hosts; derive success from the npm logs themselves.
        $bothLogs = (Get-Content -Raw $logFile -ErrorAction SilentlyContinue) + (Get-Content -Raw $errFile -ErrorAction SilentlyContinue)
        $report.exitCode = if ($bothLogs -match 'npm info ok|added \d+ packages') { 0 } else { 1 }
        $report.exitCodeDerived = $true
    }
} else {
    # -SkipInstall: re-derive the install outcome from the previous npm logs.
    $bothLogs = (Get-Content -Raw $logFile -ErrorAction SilentlyContinue) + (Get-Content -Raw $errFile -ErrorAction SilentlyContinue)
    $report.exitCode = if ($bothLogs -match 'npm info ok|added \d+ packages') { 0 } else { 1 }
    $report.exitCodeDerived = $true
}

$dshDir = Join-Path $installDir 'node_modules\@deepseek-ai\dsh'
$entry = Join-Path $dshDir 'lib\bin.js'
$report.entryExists = Test-Path $entry
if (Test-Path (Join-Path $dshDir 'package.json')) {
    $pkg = Get-Content -Raw (Join-Path $dshDir 'package.json') | ConvertFrom-Json
    $report.dshVersion = $pkg.version
} else {
    $report.dshVersion = $null
}

$ptyNative = Get-ChildItem (Join-Path $installDir 'node_modules\node-pty') -Recurse -Filter '*.node' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
# koffi 3.x ships prebuilt binaries via its optionalDependencies
# (@koromix/koffi-<platform>); the cnoke install script consumes them without
# downloading or compiling when the platform package is present.
$koffiNative = Get-ChildItem (Join-Path $installDir 'node_modules\@koromix') -Recurse -Filter '*.node' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $koffiNative) {
    $koffiNative = Get-ChildItem (Join-Path $installDir 'node_modules\koffi') -Recurse -Filter '*.node' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
$report.nodePtyNative = [bool]$ptyNative
$report.koffiNative = [bool]$koffiNative

$logText = (Get-Content -Raw $logFile -ErrorAction SilentlyContinue) + (Get-Content -Raw $errFile -ErrorAction SilentlyContinue)
# Real compile activity only: node-gyp running a build prints 'gyp info',
# MSBuild prints its version banner, cl.exe emits diagnostics, and a failed
# prebuild-install warns. The 'npm info run ... || node-gyp rebuild' lines are
# command descriptions, not compile evidence.
$report.compiledLocally = [bool]($logText -match 'gyp info|MSBuild version|cl :|prebuild-install warn')
$report.prebuiltEvidence = [bool]($logText -match 'prebuilds/|prebuild-install|@koromix/koffi-win32-x64')

$files = Get-ChildItem $installDir -Recurse -File -ErrorAction SilentlyContinue
$report.installSizeMiB = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
$report.pass = ($report.exitCode -eq 0) -and $report.entryExists -and ($report.dshVersion -eq '0.1.2-rc.1') `
    -and $report.nodePtyNative -and $report.koffiNative -and (-not $report.compiledLocally)

$report | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $reportFile
Write-Host ($report | ConvertTo-Json -Depth 4)
if (-not $report.pass) { Write-Host '[probe] install-probe FAILED'; exit 1 }
Write-Host '[probe] install-probe PASSED'
