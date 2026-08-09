[CmdletBinding()]
param(
    [string]$NodePath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$playwrightCli = Join-Path $repositoryRoot "node_modules\playwright\cli.js"
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

if (-not (Test-Path -LiteralPath $playwrightCli -PathType Leaf)) {
    throw "Playwright is not installed at $playwrightCli. Restore frontend dependencies first."
}

$browserRoot = Join-Path $repositoryRoot "TestResults\playwright-browsers"
New-Item -ItemType Directory -Path $browserRoot -Force | Out-Null
$env:PLAYWRIGHT_BROWSERS_PATH = $browserRoot

Write-Host "Preparing locked Playwright Chromium under $browserRoot using $nodeExecutable."
& $nodeExecutable $playwrightCli install chromium
if ($LASTEXITCODE -ne 0) {
    throw "Playwright Chromium installation failed with exit code $LASTEXITCODE."
}
