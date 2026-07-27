[CmdletBinding()]
param(
    [string]$ArchivePath = "",
    [int]$Port = 18123
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "TestResults\release-smoke"))
$unpackRoot = [IO.Path]::GetFullPath((Join-Path $resultsRoot "unpacked"))
$profileRoot = [IO.Path]::GetFullPath((Join-Path $resultsRoot "edge-profile"))
$domPath = Join-Path $resultsRoot "dom.html"
$serverOut = Join-Path $resultsRoot "server.out.log"
$serverErr = Join-Path $resultsRoot "server.err.log"
$edgeErr = Join-Path $resultsRoot "edge.err.log"

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $archive = Get-ChildItem (Join-Path $repoRoot "bin\releases\PSX-*-win-x64-portable.zip") |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $archive) {
        throw "No portable Release ZIP was found under bin/releases."
    }
    $ArchivePath = $archive.FullName
}
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    throw "Release archive does not exist: $ArchivePath"
}

if (Test-Path -LiteralPath $resultsRoot) {
    $resolvedResults = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $resultsRoot).Path)
    $expectedPrefix = [IO.Path]::GetFullPath((Join-Path $repoRoot "TestResults")) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedResults.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean smoke directory outside TestResults: $resolvedResults"
    }
    Remove-Item -LiteralPath $resolvedResults -Recurse -Force
}
New-Item -ItemType Directory -Path $resultsRoot, $unpackRoot, $profileRoot -Force | Out-Null
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $unpackRoot -Force

$wwwroot = Join-Path $unpackRoot "wwwroot"
if (-not (Test-Path -LiteralPath (Join-Path $wwwroot "app\index.html"))) {
    throw "Release archive does not contain wwwroot/app/index.html."
}
if (-not (Test-Path -LiteralPath (Join-Path $wwwroot "app\.vite\manifest.json") -PathType Leaf)) {
    throw "Release archive is missing wwwroot/app/.vite/manifest.json."
}

$server = Start-Process -FilePath "node.exe" `
    -ArgumentList @((Join-Path $repoRoot "tools\smoke-server.mjs"), $wwwroot, $Port) `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $serverOut `
    -RedirectStandardError $serverErr `
    -PassThru

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri ("http://127.0.0.1:" + $Port + "/") -UseBasicParsing -TimeoutSec 1
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
    if (-not $ready) {
        throw "Release smoke server did not become ready."
    }

    $edgeCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe")
    )
    $edge = $edgeCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($edge)) {
        throw "Microsoft Edge was not found; WebView2 browser smoke cannot run."
    }

    $arguments = @(
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        ("--user-data-dir=" + $profileRoot),
        "--virtual-time-budget=10000",
        "--dump-dom",
        ("http://127.0.0.1:" + $Port + "/app/index.html?smoke=react")
    )
    $edgeProcess = Start-Process -FilePath $edge `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -RedirectStandardOutput $domPath `
        -RedirectStandardError $edgeErr `
        -Wait `
        -PassThru
    if ($edgeProcess.ExitCode -ne 0) {
        throw "Edge smoke exited with code $($edgeProcess.ExitCode). See $edgeErr"
    }
    $dom = Get-Content -LiteralPath $domPath -Raw
    if ($dom -notmatch 'data-smoke-status="pass"') {
        $report = [regex]::Match($dom, '<pre id="react-smoke-report">(?<value>.*?)</pre>').Groups["value"].Value
        throw "React Release smoke failed. Report: $report"
    }
    Write-Host "[smoke] React islands mounted from the Release ZIP with zero browser errors." -ForegroundColor Green
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
