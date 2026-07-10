<#
.SYNOPSIS
    Build a redistributable PSX release zip.

.DESCRIPTION
    Produces {name}-{version}-win-x64.zip containing:
      - PSX.exe, PSX.dll and all self-contained .NET runtime files (win-x64)
      - wwwroot/, psx.ini, theme-presets/   (copied from publish output)
      - tools/node/                          (portable Node 22.17.0, downloaded)
      - runtime/acp-current/                 (ACP runtime pre-installed at build time)

    The output zip is self-contained for end users: unzip and double-click
    PSX.exe. No .NET / Node / Claude Code install is required at runtime.
    WebView2 Runtime can be bundled by passing -WebView2FixedRuntimePath.
    Otherwise machines without it will see a prompt asking the user to install
    it manually.

    This script does NOT install Node on the build machine. The
    portable Node is downloaded as a .zip archive and extracted with
    PowerShell's built-in Expand-Archive (no third-party tools required).

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER OutputDirectory
    Where the final zip lands. Defaults to .\releases.

.PARAMETER SkipNodeDownload
    If set, skips downloading the portable Node archive. Useful for offline
    builds or when 7z is not available; the script will then skip the Node
    copy step entirely.

.PARAMETER SkipAcpInstall
    If set, skips the npm ci install of runtime/acp-current. Useful when the
    directory is already populated, e.g. from a previous build or a
    source-controlled cache.

.PARAMETER WebView2FixedRuntimePath
    Optional path to an extracted WebView2 Fixed Version Runtime directory.
    The directory must contain msedgewebview2.exe, either directly or in a
    nested Microsoft.WebView2.FixedVersionRuntime.* folder. When provided, the
    runtime is copied into runtime/webview2-fixed/ in the release package.

.EXAMPLE
    pwsh tools/build-release.ps1
    pwsh tools/build-release.ps1 -Configuration Debug -OutputDirectory .\out\
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = ".\bin\releases",
    [switch]$SkipNodeDownload,
    [switch]$SkipAcpInstall,
    [string]$WebView2FixedRuntimePath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ---- pin everything for reproducible builds ----
$PortableNodeVersion = "v22.17.0"
$PortableNodeArchive = "node-$PortableNodeVersion-win-x64.zip"
$PortableNodeUrl = "https://nodejs.org/dist/$PortableNodeVersion/$PortableNodeArchive"
$PortableNodeExpectedSha = $null  # Node 22.17.0 zip SHA-256; populated by maintainer

# ---- locate repo root ----
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..")
$PublishOutput = Join-Path $RepoRoot ".\bin\publish\win-x64-self-contained"
$StagingDir = Join-Path $RepoRoot ".\bin\release-staging"
$BuildCacheDir = Join-Path $RepoRoot ".\bin\build-cache"

function Resolve-WebView2FixedRuntimeDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $resolved = Resolve-Path $Path -ErrorAction Stop
    $item = Get-Item $resolved
    if (-not $item.PSIsContainer) {
        throw "WebView2FixedRuntimePath must be an extracted directory, not a file: $Path"
    }

    $directExe = Join-Path $item.FullName "msedgewebview2.exe"
    if (Test-Path $directExe) {
        return $item.FullName
    }

    $runtimeExe = Get-ChildItem -Path $item.FullName -Filter "msedgewebview2.exe" -Recurse -File |
        Select-Object -First 1
    if ($null -eq $runtimeExe) {
        throw "msedgewebview2.exe was not found under WebView2FixedRuntimePath: $Path"
    }

    return $runtimeExe.DirectoryName
}

Write-Host "==> PSX release build" -ForegroundColor Cyan
Write-Host "    Repo root:        $RepoRoot"
Write-Host "    Configuration:   $Configuration"
Write-Host "    Publish output:  $PublishOutput"
Write-Host "    Staging dir:     $StagingDir"
Write-Host "    Output dir:      $OutputDirectory"
if (-not [string]::IsNullOrWhiteSpace($WebView2FixedRuntimePath)) {
    Write-Host "    WebView2 fixed:  $WebView2FixedRuntimePath"
}
Write-Host ""

# ---- step 1: dotnet publish ----
Write-Host "==> [1/5] dotnet publish (self-contained, win-x64, non-single-file)" -ForegroundColor Cyan
if (Test-Path $PublishOutput) {
    Remove-Item -Recurse -Force $PublishOutput
}
$publishProfile = Join-Path $RepoRoot "Properties\PublishProfiles\win-x64-self-contained.pubxml"
if (-not (Test-Path $publishProfile)) {
    throw "Publish profile not found: $publishProfile. Did you forget to add it?"
}
dotnet publish $RepoRoot `
    -c $Configuration `
    /p:PublishProfile=win-x64-self-contained `
    -o $PublishOutput | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
Write-Host ""

# ---- step 2: stage publish output ----
Write-Host "==> [2/5] Stage publish output" -ForegroundColor Cyan
if (Test-Path $StagingDir) {
    Remove-Item -Recurse -Force $StagingDir
}
New-Item -ItemType Directory -Path $StagingDir | Out-Null
Copy-Item -Recurse -Force (Join-Path $PublishOutput "*") $StagingDir
Write-Host "    Staged $((Get-ChildItem -Recurse $StagingDir | Measure-Object).Count) files"
Write-Host ""

# ---- step 3: portable Node ----
$nodeDir = Join-Path $StagingDir "tools\node"
$npmCliPath = Join-Path $nodeDir "node_modules\npm\bin\npm-cli.js"

if ($SkipNodeDownload) {
    Write-Host "==> [3/5] Skipping portable Node (-SkipNodeDownload)" -ForegroundColor Yellow
} else {
    Write-Host "==> [3/5] Download + extract portable Node $PortableNodeVersion" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $nodeDir -Force | Out-Null

    $archivePath = Join-Path $BuildCacheDir $PortableNodeArchive
    New-Item -ItemType Directory -Path $BuildCacheDir -Force | Out-Null
    if (-not (Test-Path $archivePath)) {
        Write-Host "    Downloading $PortableNodeUrl"
        Invoke-WebRequest -Uri $PortableNodeUrl -OutFile $archivePath -UseBasicParsing
    } else {
        Write-Host "    Reusing cached archive: $archivePath"
    }

    Write-Host "    Extracting zip to $nodeDir"
    Expand-Archive -Path $archivePath -DestinationPath $nodeDir -Force

    # zip extracts to node-vXX.YY.Z-win-x64/ — flatten to tools/node/ directly
    $extractedRoot = Get-ChildItem -Directory $nodeDir | Select-Object -First 1
    if ($extractedRoot) {
        Get-ChildItem $extractedRoot.FullName -Force | Move-Item -Destination $nodeDir -Force
        Remove-Item -Recurse -Force $extractedRoot.FullName
    }

    # Record the version so AcpRuntimeManager can sanity-check at startup.
    Set-Content -Path (Join-Path $nodeDir "node-version.txt") -Value $PortableNodeVersion -NoNewline
    Write-Host "    Installed Node into staging/tools/node/; wrote node-version.txt"
}
Write-Host ""

# ---- step 4: pre-install ACP runtime ----
Write-Host "==> [4/5] Pre-install ACP runtime into staging/runtime/acp-current/" -ForegroundColor Cyan
$acpCurrentDir = Join-Path $StagingDir "runtime\acp-current"
$seedSrc = Join-Path $RepoRoot "tools\acp-seed"

if (-not (Test-Path $seedSrc)) {
    throw "tools/acp-seed not found in repo; this is required to build the ACP runtime."
}

if ($SkipAcpInstall) {
    Write-Host "    Skipping npm ci (-SkipAcpInstall)" -ForegroundColor Yellow
} elseif (-not (Test-Path $npmCliPath)) {
    Write-Host "    npm CLI not found; skipping ACP install. The package must be built with Node available." -ForegroundColor Yellow
} else {
    New-Item -ItemType Directory -Path $acpCurrentDir -Force | Out-Null

    # Seed manifest files only; node_modules will be created by npm ci.
    foreach ($name in @("package.json", "package-lock.json", ".npmrc")) {
        $source = Join-Path $seedSrc $name
        if (Test-Path $source) {
            Copy-Item -Path $source -Destination (Join-Path $acpCurrentDir $name) -Force
            Write-Host "    Seeded $name"
        }
    }

    $nodeExe = Join-Path $nodeDir "node.exe"
    if (-not (Test-Path $nodeExe)) {
        throw "node.exe not found in staging/tools/node/; cannot run npm ci."
    }

    Write-Host "    Running npm ci (this may take a minute)..."
    $npmProcess = Start-Process -FilePath $nodeExe `
        -ArgumentList "`"$npmCliPath`"", "ci", "--include=optional", "--no-audit", "--no-fund" `
        -WorkingDirectory $acpCurrentDir `
        -Wait -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $acpCurrentDir "npm-ci.stdout.log") `
        -RedirectStandardError (Join-Path $acpCurrentDir "npm-ci.stderr.log")

    if ($npmProcess.ExitCode -ne 0) {
        $stderr = if (Test-Path (Join-Path $acpCurrentDir "npm-ci.stderr.log")) {
            Get-Content (Join-Path $acpCurrentDir "npm-ci.stderr.log") -Raw
        } else { "(no stderr captured)" }
        throw "npm ci failed with exit code $($npmProcess.ExitCode).`n$stderr"
    }

    $adapterPath = Join-Path $acpCurrentDir "node_modules\@agentclientprotocol\claude-agent-acp\dist\index.js"
    $claudeCodePath = Join-Path $acpCurrentDir "node_modules\@anthropic-ai\claude-agent-sdk-win32-x64\claude.exe"
    if (-not (Test-Path $adapterPath)) {
        throw "ACP adapter entry point missing after npm ci: $adapterPath"
    }
    if (-not (Test-Path $claudeCodePath)) {
        throw "Bundled Claude Code binary missing after npm ci: $claudeCodePath"
    }

    Write-Host "    ACP runtime ready: $adapterPath"
}
Write-Host ""

# ---- step 5: optional WebView2 Fixed Version runtime ----
$fixedWebView2Source = Resolve-WebView2FixedRuntimeDirectory $WebView2FixedRuntimePath
if ($fixedWebView2Source) {
    Write-Host "==> [5/5] Bundle WebView2 Fixed Version runtime" -ForegroundColor Cyan
    $fixedWebView2Target = Join-Path $StagingDir "runtime\webview2-fixed"
    if (Test-Path $fixedWebView2Target) {
        Remove-Item -Recurse -Force $fixedWebView2Target
    }
    New-Item -ItemType Directory -Path $fixedWebView2Target -Force | Out-Null
    Copy-Item -Recurse -Force (Join-Path $fixedWebView2Source "*") $fixedWebView2Target
    Write-Host "    Copied fixed runtime from $fixedWebView2Source"
} else {
    Write-Host "==> [5/5] WebView2 runtime is NOT bundled" -ForegroundColor Yellow
    Write-Host "    Users without WebView2 will see a prompt asking them to install it."
}
Write-Host ""

# ---- produce zip ----
Write-Host "==> Packaging zip" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Read version from PSX.csproj
$csproj = Get-Content (Join-Path $RepoRoot "PSX.csproj") -Raw
$assemblyVersionMatches = [regex]::Matches($csproj, 'AssemblyVersion[^"]*"([^"]+)"')
$version = if ($assemblyVersionMatches.Count -gt 0) {
    $assemblyVersionMatches[0].Groups[1].Value
} else { "0.0.0" }

# Fall back to Directory.Build.props or hardcode a sensible default
if ($version -eq "0.0.0") {
    $versionMatches = [regex]::Matches($csproj, '<Version>([^<]+)</Version>')
    if ($versionMatches.Count -gt 0) { $version = $versionMatches[0].Groups[1].Value }
}

$zipPath = Join-Path $OutputDirectory "PSX-$version-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Use .NET Compression for cross-platform zips; for Windows-only zips we
# could use Compress-Archive but that often produces non-standard headers.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $StagingDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false  # includeBaseDirectory: false — zip root should be PSX.exe directly
)

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "    Wrote $zipPath ($zipSize MB)"
Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
