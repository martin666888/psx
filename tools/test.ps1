[CmdletBinding()]
param(
    [ValidateSet("Fast", "Full")]
    [string]$Suite = "Fast"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "PSX.slnx"
$resultsDirectory = Join-Path $repoRoot "TestResults"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    Write-Host "==> $Name"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try {
    Invoke-Checked "Verify .NET formatting" {
        dotnet format $solution --verify-no-changes
    }
    Invoke-Checked "Build PSX in Release" {
        dotnet build $solution -c Release
    }
    Invoke-Checked "Run automated tests with coverage" {
        dotnet test --solution $solution -c Release --no-build `
            --results-directory $resultsDirectory `
            --coverage `
            --coverage-output "coverage.cobertura.xml" `
            --coverage-output-format cobertura `
            --report-trx `
            --report-trx-filename "PSX.Tests.trx"
    }

    if ($Suite -eq "Full") {
        Invoke-Checked "Build the portable release package" {
            powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\build-release.ps1")
        }
    }
}
finally {
    Pop-Location
}
