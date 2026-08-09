[CmdletBinding()]
param(
    [string]$NodePath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$solution = Join-Path $repoRoot "PSX.slnx"

function Resolve-Node22 {
    if (-not [string]::IsNullOrWhiteSpace($NodePath)) {
        $candidates = @([IO.Path]::GetFullPath($NodePath))
    } else {
        $pathNode = Get-Command node.exe -ErrorAction SilentlyContinue
        $candidates = @(
            $(if ($null -eq $pathNode) { $null } else { $pathNode.Source }),
            (Join-Path $repoRoot "TestResults\node22\node-v22.23.1-win-x64\node.exe"),
            (Join-Path $repoRoot "bin\release-staging\tools\node\node.exe")
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $version = & $candidate --version
            if ($LASTEXITCODE -eq 0 -and $version -match '^v22\.') {
                return [IO.Path]::GetFullPath($candidate)
            }
        }
    }
    throw "Dependency audit requires the repository's Node 22 toolchain."
}

function Get-NpmCli {
    param([Parameter(Mandatory)][string]$ResolvedNodePath)
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path (Split-Path -Parent $ResolvedNodePath) "node_modules\npm\bin\npm-cli.js"),
        $(if ($null -eq $npmCommand) { $null } else {
            Join-Path (Split-Path -Parent $npmCommand.Source) "node_modules\npm\bin\npm-cli.js"
        })
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    $npmCli = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $npmCli) {
        throw "npm-cli.js was not found for the dependency audit."
    }
    return [IO.Path]::GetFullPath($npmCli)
}

function Get-VulnerabilityCount {
    param([AllowNull()]$Value)
    if ($null -eq $Value -or $Value -is [string]) {
        return 0
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [Management.Automation.PSCustomObject]) {
        $count = 0
        foreach ($item in $Value) {
            $count += Get-VulnerabilityCount $item
        }
        return $count
    }

    $total = 0
    foreach ($property in $Value.PSObject.Properties) {
        if ($property.Name -eq "vulnerabilities" -and $null -ne $property.Value) {
            $total += @($property.Value).Count
        } else {
            $total += Get-VulnerabilityCount $property.Value
        }
    }
    return $total
}

Push-Location $repoRoot
try {
    Write-Host "==> Audit NuGet packages" -ForegroundColor Cyan
    $nugetOutput = & dotnet list $solution package --vulnerable --include-transitive --format json --output-version 1 2>&1
    if ($LASTEXITCODE -ne 0) {
        $nugetOutput | Write-Host
        throw "NuGet vulnerability audit failed."
    }
    $nugetJson = ($nugetOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $nugetVulnerabilities = Get-VulnerabilityCount $nugetJson
    if ($nugetVulnerabilities -gt 0) {
        $nugetOutput | Write-Host
        throw "NuGet audit found $nugetVulnerabilities vulnerable package record(s)."
    }
    Write-Host "    NuGet audit found no known vulnerabilities."

    $node = Resolve-Node22
    $npmCli = Get-NpmCli $node
    Write-Host "==> Audit production npm dependencies" -ForegroundColor Cyan
    & $node $npmCli audit --prefix $repoRoot --omit=dev --audit-level=low --registry=https://registry.npmjs.org
    if ($LASTEXITCODE -ne 0) {
        throw "Production npm audit found a vulnerability or could not reach the official registry."
    }

    Write-Host "==> Audit complete npm dependency tree" -ForegroundColor Cyan
    & $node $npmCli audit --prefix $repoRoot --audit-level=high --registry=https://registry.npmjs.org
    if ($LASTEXITCODE -ne 0) {
        throw "Complete npm audit found a high/critical vulnerability or could not reach the official registry."
    }

    Write-Host "Dependency audit passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
