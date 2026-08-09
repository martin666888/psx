[CmdletBinding()]
param(
    [string]$NodePath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$browserManifest = Join-Path $repositoryRoot "node_modules\playwright-core\browsers.json"
$pathNode = "<not checked>"

if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $pathNodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -ne $pathNodeCommand) {
        $pathNode = $pathNodeCommand.Source
    }

    $candidates = @(
        $pathNode,
        (Join-Path $repositoryRoot "TestResults\node22\node-v22.23.1-win-x64\node.exe"),
        (Join-Path $repositoryRoot "bin\release-staging\tools\node\node.exe")
    ) | Where-Object { $_ -ne "<not checked>" } | Select-Object -Unique
} else {
    $candidates = @([IO.Path]::GetFullPath($NodePath))
}

$nodeExecutable = $null
foreach ($candidate in $candidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }

    $candidateVersion = & $candidate --version
    if ($LASTEXITCODE -eq 0 -and $candidateVersion -match "^v22\.") {
        $nodeExecutable = $candidate
        break
    }
}

if ($null -eq $nodeExecutable) {
    throw (
        "Visual tests require Node 22, matching the frontend test gate. " +
        "No Node 22 executable was found on PATH or in the portable test/release locations. " +
        "The current PATH resolves to $pathNode."
    )
}

if (-not (Test-Path -LiteralPath $browserManifest -PathType Leaf)) {
    throw "Playwright browser manifest is not installed at $browserManifest. Restore frontend dependencies first."
}

$browserRoot = Join-Path $repositoryRoot "TestResults\playwright-browsers"
New-Item -ItemType Directory -Path $browserRoot -Force | Out-Null
$manifest = Get-Content -LiteralPath $browserManifest -Raw | ConvertFrom-Json
$chromium = $manifest.browsers | Where-Object { $_.name -eq "chromium" } | Select-Object -First 1
if ($null -eq $chromium -or [string]::IsNullOrWhiteSpace($chromium.revision) -or
    [string]::IsNullOrWhiteSpace($chromium.browserVersion)) {
    throw "Playwright browser manifest does not contain a usable Chromium descriptor."
}

$revision = [string]$chromium.revision
$browserVersion = [string]$chromium.browserVersion
$installDirectory = Join-Path $browserRoot ("chromium-" + $revision)
$browserExecutable = Join-Path $installDirectory "chrome-win64\chrome.exe"
$completionMarker = Join-Path $installDirectory "INSTALLATION_COMPLETE"
if ((Test-Path -LiteralPath $browserExecutable -PathType Leaf) -and
    (Test-Path -LiteralPath $completionMarker -PathType Leaf)) {
    Write-Host "Locked Playwright Chromium $browserVersion (revision $revision) is already prepared."
    exit 0
}
if (Test-Path -LiteralPath $installDirectory) {
    throw (
        "An incomplete browser directory exists at $installDirectory. " +
        "Remove that exact TestResults directory and run prepare:visual again."
    )
}

$downloadRoot = Join-Path $repositoryRoot "TestResults\playwright-downloads"
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
$archive = Join-Path $downloadRoot ("chromium-" + $browserVersion + "-win64.zip")
$downloadUrl = (
    "https://storage.googleapis.com/chrome-for-testing-public/" +
    $browserVersion + "/win64/chrome-win64.zip"
)

Write-Host (
    "Preparing locked Playwright Chromium $browserVersion (revision $revision) " +
    "under $browserRoot using Node $(& $nodeExecutable --version)."
)
Write-Host "Downloading the official Chrome for Testing archive (resumable): $downloadUrl"
& curl.exe --fail --location --retry 3 --retry-delay 2 --continue-at - `
    --output $archive $downloadUrl
if ($LASTEXITCODE -ne 0) {
    throw "Chromium download failed with exit code $LASTEXITCODE. The partial archive is retained for resume."
}

$temporaryDirectory = Join-Path $browserRoot ("chromium-" + $revision + "." + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $temporaryDirectory
$temporaryExecutable = Join-Path $temporaryDirectory "chrome-win64\chrome.exe"
if (-not (Test-Path -LiteralPath $temporaryExecutable -PathType Leaf)) {
    throw "The downloaded Chromium archive did not contain chrome-win64\chrome.exe."
}

Set-Content -LiteralPath (Join-Path $temporaryDirectory "INSTALLATION_COMPLETE") `
    -Value ("Playwright Chromium " + $browserVersion + " revision " + $revision) `
    -Encoding UTF8
Move-Item -LiteralPath $temporaryDirectory -Destination $installDirectory
Write-Host "Prepared $browserExecutable"
