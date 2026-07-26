<#
.SYNOPSIS
    Build a redistributable PSX release zip.

.DESCRIPTION
    Produces PSX-{version}-win-x64-portable.zip containing:
      - PSX.exe, PSX.dll and all self-contained .NET runtime files (win-x64)
      - wwwroot/, psx.ini, theme-presets/   (copied from publish output)
      - tools/node/                          (portable Node 22.23.1, downloaded)
      - tools/acp-seed/                      (installed by the user on first Agent use)
      - tools/kimi/                          (Kimi Code ACP runtime, installed at
                                              build time from tools/kimi-seed/ via npm ci)

    The output zip is portable for end users: unzip and double-click PSX.exe.
    .NET and Node are bundled. Agent mode asks for confirmation before it
    downloads the ACP / Claude runtime from npm on first use.
    WebView2 Runtime can be bundled by passing -WebView2FixedRuntimePath.
    Otherwise machines without it will see a prompt asking the user to install
    it manually.

    This script does NOT install Node on the build machine. The
    portable Node is downloaded as a .zip archive and extracted with
    PowerShell's built-in Expand-Archive (no third-party tools required).

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER OutputDirectory
    Where the final zip lands. Defaults to .\bin\releases under the repository.

.PARAMETER SkipNodeDownload
    If set, network download is disabled and the already-cached Node archive
    must exist under bin/build-cache. The cached archive is still verified and
    extracted into the package.

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
    [string]$OutputDirectory = "",
    [switch]$SkipNodeDownload,
    [string]$WebView2FixedRuntimePath = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest

# ---- pin everything for reproducible builds ----
$PortableNodeVersion = "v22.23.1"
$PortableNodeArchive = "node-$PortableNodeVersion-win-x64.zip"
$PortableNodeUrl = "https://nodejs.org/dist/$PortableNodeVersion/$PortableNodeArchive"
$PortableNodeExpectedSha = "7df0bc9375723f4a86b3aa1b7cc73342423d9677a8df4538aca31a049e309c29"

# ---- locate repo root ----
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$ProjectPath = Join-Path $RepoRoot "PSX.csproj"
$PublishOutput = [IO.Path]::GetFullPath((Join-Path $RepoRoot "bin\publish\win-x64-self-contained"))
$StagingDir = [IO.Path]::GetFullPath((Join-Path $RepoRoot "bin\release-staging"))
$BuildCacheDir = [IO.Path]::GetFullPath((Join-Path $RepoRoot "bin\build-cache"))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [IO.Path]::GetFullPath((Join-Path $RepoRoot "bin\releases"))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDirectory))
} else {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
}

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
dotnet publish $ProjectPath `
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
$archivePath = Join-Path $BuildCacheDir $PortableNodeArchive
New-Item -ItemType Directory -Path $BuildCacheDir -Force | Out-Null
if ($SkipNodeDownload) {
    Write-Host "==> [3/5] Use cached portable Node $PortableNodeVersion" -ForegroundColor Cyan
    if (-not (Test-Path $archivePath)) {
        throw "-SkipNodeDownload requires the verified archive at: $archivePath"
    }
} else {
    Write-Host "==> [3/5] Download + extract portable Node $PortableNodeVersion" -ForegroundColor Cyan
    if (-not (Test-Path $archivePath)) {
        Write-Host "    Downloading $PortableNodeUrl"
        Invoke-WebRequest -Uri $PortableNodeUrl -OutFile $archivePath -UseBasicParsing
    } else {
        Write-Host "    Reusing cached archive: $archivePath"
    }
}

$actualSha = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if (-not [string]::Equals($actualSha, $PortableNodeExpectedSha, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $archivePath -Force
    throw "Portable Node SHA-256 mismatch. Expected $PortableNodeExpectedSha but got $actualSha. The cached archive was deleted."
}
Write-Host "    Verified SHA-256: $actualSha"

New-Item -ItemType Directory -Path $nodeDir -Force | Out-Null
Write-Host "    Extracting zip to $nodeDir"
Expand-Archive -Path $archivePath -DestinationPath $nodeDir -Force

# zip extracts to node-vXX.YY.Z-win-x64/ — flatten to tools/node/ directly
$extractedRoot = Get-ChildItem -Directory $nodeDir | Select-Object -First 1
if ($extractedRoot) {
    Get-ChildItem $extractedRoot.FullName -Force | Move-Item -Destination $nodeDir -Force
    Remove-Item -Recurse -Force $extractedRoot.FullName
}

Set-Content -Path (Join-Path $nodeDir "node-version.txt") -Value $PortableNodeVersion -NoNewline
Write-Host "    Installed Node into staging/tools/node/; wrote node-version.txt"
Write-Host ""

# ---- step 4: bundle Kimi Code ACP runtime (lockfile-driven, reproducible) ----
Write-Host "==> [4/5] Install Kimi Code ACP runtime into staging/tools/kimi" -ForegroundColor Cyan
$kimiSeedDir = Join-Path $RepoRoot "tools\kimi-seed"
$kimiTargetDir = Join-Path $StagingDir "tools\kimi"
foreach ($seedFile in @("package.json", "package-lock.json", ".npmrc")) {
    $seedSource = Join-Path $kimiSeedDir $seedFile
    if (-not (Test-Path -LiteralPath $seedSource -PathType Leaf)) {
        throw "Kimi seed file is missing: tools/kimi-seed/$seedFile"
    }
}
New-Item -ItemType Directory -Path $kimiTargetDir -Force | Out-Null
Copy-Item (Join-Path $kimiSeedDir "package.json") $kimiTargetDir -Force
Copy-Item (Join-Path $kimiSeedDir "package-lock.json") $kimiTargetDir -Force
Copy-Item (Join-Path $kimiSeedDir ".npmrc") $kimiTargetDir -Force

$nodeExe = Join-Path $nodeDir "node.exe"
$npmCli = Join-Path $nodeDir "node_modules\npm\bin\npm-cli.js"
# `npm ci` from the pinned lockfile is more reproducible than
# `npm install @moonshot-ai/kimi-code@<version>`. --include=optional pulls in
# the Windows-native components (node-pty, clipboard-win32-x64); the seed
# .npmrc constrains os/cpu so only win32-x64 packages are installed. Building
# node-pty's native addon requires the machine's C++ build toolchain.
& $nodeExe $npmCli ci --prefix $kimiTargetDir --omit=dev --include=optional --no-audit --no-fund | Out-Host
if ($LASTEXITCODE -ne 0) { throw "npm ci for Kimi Code failed (exit $LASTEXITCODE)" }
Write-Host "    Installed Kimi Code into staging/tools/kimi/"
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

# ---- validate public package contents ----
Write-Host "==> Validate public package contents" -ForegroundColor Cyan
$requiredFiles = @(
    "PSX.exe",
    "PSX.dll",
    "psx.ini",
    "LICENSE.txt",
    "THIRD-PARTY-NOTICES.md",
    "licenses\acp\LICENSE",
    "licenses\kimi\LICENSE",
    "licenses\kimi\THIRD-PARTY-NOTICES.md",
    "licenses\communitytoolkit.mvvm\License.md",
    "licenses\dotnet\LICENSE.txt",
    "licenses\microsoft.extensions\LICENSE.TXT",
    "licenses\webview2\LICENSE.txt",
    "licenses\react\LICENSE.txt",
    "wwwroot\index.html",
    "wwwroot\js\agent-app\entry.js",
    "wwwroot\vendor\xterm\LICENSE",
    "wwwroot\vendor\react\react-core.js",
    "wwwroot\vendor\react\react.js",
    "wwwroot\vendor\react\react-jsx-runtime.js",
    "wwwroot\vendor\react\react-dom.js",
    "wwwroot\vendor\react\react-dom-client.js",
    "wwwroot\vendor\react\vendor-manifest.json",
    "tools\node\node.exe",
    "tools\node\LICENSE",
    "tools\node\node_modules\npm\LICENSE",
    "tools\node\node_modules\npm\bin\npm-cli.js",
    "tools\acp-seed\package.json",
    "tools\acp-seed\package-lock.json",
    "tools\acp-seed\.npmrc",
    "tools\kimi\package.json",
    "tools\kimi\package-lock.json",
    "tools\kimi\node_modules\@moonshot-ai\kimi-code\package.json",
    "tools\kimi\node_modules\@moonshot-ai\kimi-code\dist\main.mjs"
)
foreach ($relativePath in $requiredFiles) {
    $fullPath = Join-Path $StagingDir $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required release file is missing: $relativePath"
    }
}

$forbiddenAcpRuntime = Join-Path $StagingDir "runtime\acp-current"
if (Test-Path -LiteralPath $forbiddenAcpRuntime) {
    throw "Public release must not contain runtime/acp-current."
}

$forbiddenFiles = @(Get-ChildItem -LiteralPath $StagingDir -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($StagingDir.Length).TrimStart('\').Replace('\', '/')
    $strayNodeModules = ($relative -match '(^|/)node_modules/') -and (-not $relative.StartsWith('tools/node/node_modules/', [StringComparison]::OrdinalIgnoreCase)) -and (-not $relative.StartsWith('tools/kimi/node_modules/', [StringComparison]::OrdinalIgnoreCase))
    # Source maps and TypeScript are forbidden only for the generated Agent
    # frontend. Portable Node/npm and vendor assets legitimately contain maps.
    $agentSourceArtifact = $relative.StartsWith('wwwroot/js/agent-app/', [StringComparison]::OrdinalIgnoreCase) -and $_.Extension -in @('.ts', '.map')
    $_.Name -ieq "claude.exe" `
        -or $_.Extension -in @(".log", ".tmp", ".binlog") `
        -or $agentSourceArtifact `
        -or $relative.StartsWith('frontend/', [StringComparison]::OrdinalIgnoreCase) `
        -or $strayNodeModules `
        -or $relative.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) `
        -or $relative.StartsWith('TestResults/', [StringComparison]::OrdinalIgnoreCase) `
        -or $_.Name -like 'PSX.Tests.*' `
        -or $_.Name -like 'PSX.TestAgent.*' `
        -or $_.Name -like 'PSX.TestNpm.*' `
        -or $_.Name -like 'PSX.DesktopProbe.*'
})
if ($forbiddenFiles.Count -gt 0) {
    $paths = ($forbiddenFiles | ForEach-Object { $_.FullName.Substring($StagingDir.Length).TrimStart('\') }) -join ", "
    throw "Public release contains forbidden files: $paths"
}
Write-Host "    Required files present; no Claude binary, installed ACP runtime, logs, or temp files found."
Write-Host ""

# ---- produce zip ----
Write-Host "==> Packaging zip" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Read version from PSX.csproj
$csproj = Get-Content $ProjectPath -Raw
$versionMatch = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) {
    throw "Project version is missing from $ProjectPath. Add a <Version> element before building a release."
}
$version = $versionMatch.Groups[1].Value.Trim()

$zipPath = Join-Path $OutputDirectory "PSX-$version-win-x64-portable.zip"
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

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($requiredFile in $requiredFiles) {
        $requiredEntry = $requiredFile.Replace('\', '/')
        if ($entryNames -notcontains $requiredEntry) {
            throw "Required file is missing from the completed zip: $requiredEntry"
        }
    }

    $forbiddenEntries = @($entryNames | Where-Object {
        $normalized = $_.Replace('\', '/')
        $leaf = [IO.Path]::GetFileName($normalized)
        $extension = [IO.Path]::GetExtension($normalized)
        $strayNodeModules = ($normalized -match '(^|/)node_modules/') -and (-not $normalized.StartsWith('tools/node/node_modules/', [StringComparison]::OrdinalIgnoreCase)) -and (-not $normalized.StartsWith('tools/kimi/node_modules/', [StringComparison]::OrdinalIgnoreCase))
        # Keep this scoped to Agent output for parity with the staging check;
        # Node/npm and vendor files can legitimately ship source maps.
        $agentSourceArtifact = $normalized.StartsWith('wwwroot/js/agent-app/', [StringComparison]::OrdinalIgnoreCase) -and $extension -in @('.ts', '.map')
        $normalized.StartsWith('runtime/acp-current/', [StringComparison]::OrdinalIgnoreCase) `
            -or $normalized.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) `
            -or $normalized.StartsWith('TestResults/', [StringComparison]::OrdinalIgnoreCase) `
            -or $normalized.StartsWith('frontend/', [StringComparison]::OrdinalIgnoreCase) `
            -or $agentSourceArtifact `
            -or $strayNodeModules `
            -or $leaf -ieq 'claude.exe' `
            -or $leaf -like 'PSX.Tests.*' `
            -or $leaf -like 'PSX.TestAgent.*' `
            -or $leaf -like 'PSX.TestNpm.*' `
            -or $leaf -like 'PSX.DesktopProbe.*' `
            -or $extension -in @('.log', '.tmp', '.binlog')
    })
    if ($forbiddenEntries.Count -gt 0) {
        throw "Completed zip contains forbidden entries: $($forbiddenEntries -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "    Wrote $zipPath ($zipSize MB)"
Write-Host "    SHA-256: $zipSha256"
Write-Host "    Verified completed zip contents."
Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
