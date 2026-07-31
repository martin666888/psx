[CmdletBinding()]
param(
    [ValidateSet("Unit", "Frontend", "Integration", "Desktop", "Fast", "Full")]
    [string]$Suite = "Fast"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$solution = Join-Path $repoRoot "PSX.slnx"
$resultsDirectory = Join-Path $repoRoot "TestResults"
$dotnetResults = Join-Path $resultsDirectory ("dotnet\" + $Suite.ToLowerInvariant())
$webProject = Join-Path $repoRoot "tests\PSX.Web.Tests"
$coverageSettings = Join-Path $repoRoot "tests\coverage.runsettings"
$npmCache = Join-Path $resultsDirectory "npm-cache"
$nugetPackages = Join-Path $resultsDirectory "nuget-packages"
$nugetHttpCache = Join-Path $resultsDirectory "nuget-http-cache"
$dotnetHome = Join-Path $resultsDirectory "dotnet-home"
$temporaryDirectory = Join-Path $resultsDirectory "temp"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE. Test artifacts were kept under $resultsDirectory."
    }
}

function Invoke-DotNetTests {
    param(
        [string]$Filter,
        [int]$MinimumExpectedTests
    )

    New-Item -ItemType Directory -Path $dotnetResults -Force | Out-Null
    $arguments = @(
        "test",
        "--solution", $solution,
        "--configuration", "Release",
        "--no-build",
        "--results-directory", $dotnetResults,
        "--minimum-expected-tests", $MinimumExpectedTests,
        "--coverage",
        "--coverage-output", "coverage.cobertura.xml",
        "--coverage-output-format", "cobertura",
        "--coverage-settings", $coverageSettings,
        "--report-trx",
        "--report-trx-filename", "PSX.$Suite.trx",
        "--diagnostic",
        "--diagnostic-verbosity", "Warning",
        "--diagnostic-output-directory", (Join-Path $dotnetResults "diagnostics")
    )
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments += @("--filter", $Filter)
    }

    & dotnet @arguments
}

function Invoke-FrontendTests {
    if ($null -eq (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Node.js is required for frontend tests. Install Node 22 or use the repository's portable Node."
    }
    if ($null -eq (Get-Command npm.cmd -ErrorAction SilentlyContinue)) {
        throw "npm is required for frontend tests."
    }

    New-Item -ItemType Directory -Path $npmCache -Force | Out-Null
    Invoke-Checked "Restore pinned frontend test dependencies" {
        # Root-level install: the repo-root npm workspace owns the single
        # lockfile and hoists shared dependencies (React, Vite, Vitest, types).
        npm.cmd ci --prefix $repoRoot --cache $npmCache --no-audit --no-fund
    }
    Invoke-Checked "Type-check the WebView frontend" {
        npm.cmd run typecheck:web --prefix $repoRoot
    }
    Invoke-Checked "Lint the WebView frontend" {
        npm.cmd run lint:web --prefix $repoRoot
    }
    Invoke-Checked "Verify the committed wwwroot/app output is fresh" {
        npm.cmd run verify:web --prefix $repoRoot
    }
    Invoke-Checked "Run frontend tests with production-code coverage" {
        # V8 coverage retains the instrumented module graph until reporting.
        # Keep the run hard-bounded, but give that single Node process enough
        # headroom to finish instead of failing near the default 2 GiB cap.
        powershell -ExecutionPolicy Bypass -File (
            Join-Path $repoRoot "tools\run-guarded-vitest.ps1"
        ) -ProcessMemoryLimitMB 2560 -JobMemoryLimitMB 3072 --coverage
    }
}

$runDotNet = $Suite -ne "Frontend"
$runFrontend = $Suite -in @("Frontend", "Fast", "Full")
$runFormatting = $Suite -in @("Fast", "Full")
$runRelease = $Suite -eq "Full"
$previousKeepArtifacts = $env:PSX_KEEP_TEST_ARTIFACTS
$previousNpmCache = $env:npm_config_cache
$previousNugetPackages = $env:NUGET_PACKAGES
$previousNugetHttpCache = $env:NUGET_HTTP_CACHE_PATH
$previousDotnetHome = $env:DOTNET_CLI_HOME
$previousSkipFirstTimeExperience = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$previousDotnetNoLogo = $env:DOTNET_NOLOGO
$previousTemp = $env:TEMP
$previousTmp = $env:TMP

Push-Location $repoRoot
try {
    # Every test fixture is rooted here. Keeping its files makes protocol and
    # process failures diagnosable without writing to the user's real .psx data.
    $env:PSX_KEEP_TEST_ARTIFACTS = "1"
    $env:npm_config_cache = $npmCache
    $env:NUGET_PACKAGES = $nugetPackages
    $env:NUGET_HTTP_CACHE_PATH = $nugetHttpCache
    $env:DOTNET_CLI_HOME = $dotnetHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:TEMP = $temporaryDirectory
    $env:TMP = $temporaryDirectory
    New-Item -ItemType Directory -Path $nugetPackages, $nugetHttpCache, $dotnetHome, $temporaryDirectory -Force | Out-Null

    if ($runFormatting) {
        Invoke-Checked "Verify .NET formatting" {
            dotnet format $solution --verify-no-changes
        }
    }

    if ($runDotNet) {
        Invoke-Checked "Build application, probes, Fake ACP Agent, Fake npm, and tests" {
            dotnet build $solution --configuration Release
        }

        switch ($Suite) {
            "Unit" {
                Invoke-Checked "Run C# unit tests with coverage" {
                    Invoke-DotNetTests -Filter "TestCategory=Unit" -MinimumExpectedTests 50
                }
            }
            "Integration" {
                Invoke-Checked "Run ACP and persistence integration tests with coverage" {
                    Invoke-DotNetTests -Filter "TestCategory=Integration" -MinimumExpectedTests 30
                }
            }
            "Desktop" {
                Invoke-Checked "Run Windows desktop and ConPTY tests with coverage" {
                    Invoke-DotNetTests -Filter "TestCategory=Desktop" -MinimumExpectedTests 2
                }
            }
            "Fast" {
                Invoke-Checked "Run C# unit and integration tests with coverage" {
                    Invoke-DotNetTests -Filter "(TestCategory=Unit)|(TestCategory=Integration)" -MinimumExpectedTests 85
                }
            }
            "Full" {
                Invoke-Checked "Run all C# tests, including desktop probes, with coverage" {
                    Invoke-DotNetTests -Filter "" -MinimumExpectedTests 90
                }
            }
        }
    }

    if ($runFrontend) {
        Invoke-FrontendTests
    }

    if ($runRelease) {
        Invoke-Checked "Build and validate the portable release package" {
            powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\build-release.ps1")
        }
        Invoke-Checked "Run the packaged React browser smoke" {
            powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\smoke-release.ps1")
        }
    }

    Write-Host "==> $Suite test suite passed" -ForegroundColor Green
    Write-Host "    Results: $resultsDirectory"
}
finally {
    $env:PSX_KEEP_TEST_ARTIFACTS = $previousKeepArtifacts
    $env:npm_config_cache = $previousNpmCache
    $env:NUGET_PACKAGES = $previousNugetPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousNugetHttpCache
    $env:DOTNET_CLI_HOME = $previousDotnetHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $previousSkipFirstTimeExperience
    $env:DOTNET_NOLOGO = $previousDotnetNoLogo
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}
