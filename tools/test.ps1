[CmdletBinding()]
param(
    [ValidateSet("Unit", "Frontend", "Integration", "Desktop", "Fast", "Full")]
    [string]$Suite = "Fast",
    [switch]$ForceFrontendRestore,
    [switch]$SkipDependencyAudit
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
$npmStatePath = Join-Path $resultsDirectory "npm-state.json"
$webResults = Join-Path $resultsDirectory "web"
$webSummaryPath = Join-Path $webResults "test-summary.json"
$frontendToolchain = $null

$minimumTests = @{
    Unit = 389
    Integration = 112
    Desktop = 2
    Fast = 501
    Full = 503
    Frontend = 300
}

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

function Get-FrontendToolchain {
    if ($null -ne $script:frontendToolchain) {
        return $script:frontendToolchain
    }

    $pathNodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    $pathNode = if ($null -eq $pathNodeCommand) { $null } else { $pathNodeCommand.Source }
    $nodeCandidates = @(
        $pathNode,
        (Join-Path $repoRoot "TestResults\node22\node-v22.23.1-win-x64\node.exe"),
        (Join-Path $repoRoot "bin\release-staging\tools\node\node.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    $nodePath = $null
    foreach ($candidate in $nodeCandidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        $version = & $candidate --version
        if ($LASTEXITCODE -eq 0 -and $version -match '^v22\.') {
            $nodePath = [IO.Path]::GetFullPath($candidate)
            break
        }
    }
    if ($null -eq $nodePath) {
        throw "Frontend gates require Node 22. No Node 22 executable was found on PATH or in TestResults/release-staging."
    }

    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    $npmCandidates = @(
        (Join-Path (Split-Path -Parent $nodePath) "node_modules\npm\bin\npm-cli.js"),
        $(if ($null -eq $npmCommand) { $null } else {
            Join-Path (Split-Path -Parent $npmCommand.Source) "node_modules\npm\bin\npm-cli.js"
        })
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    $npmCli = $npmCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $npmCli) {
        throw "npm-cli.js was not found beside Node 22 or the configured npm.cmd."
    }

    $nodeVersion = (& $nodePath --version).TrimStart('v')
    $npmVersion = (& $nodePath $npmCli --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query npm with the selected Node 22 runtime."
    }
    $script:frontendToolchain = [pscustomobject]@{
        NodePath = $nodePath
        NpmCli = [IO.Path]::GetFullPath($npmCli)
        NodeVersion = $nodeVersion
        NodeMajor = [int]($nodeVersion.Split('.')[0])
        NpmVersion = $npmVersion
    }
    return $script:frontendToolchain
}

function Invoke-Npm {
    param(
        [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )
    $toolchain = Get-FrontendToolchain
    & $toolchain.NodePath $toolchain.NpmCli @Arguments
}

function Initialize-FrontendDependencies {
    $toolchain = Get-FrontendToolchain
    if ($toolchain.NodeMajor -ne 22) {
        throw "Frontend gates require Node 22; resolved $($toolchain.NodeVersion)."
    }

    $lockPath = Join-Path $repoRoot "package-lock.json"
    $lockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $restoreRequired = [bool]$ForceFrontendRestore
    $reason = if ($restoreRequired) { "-ForceFrontendRestore was specified" } else { "" }

    if (-not $restoreRequired) {
        if (-not (Test-Path -LiteralPath $npmStatePath -PathType Leaf)) {
            $restoreRequired = $true
            $reason = "npm state stamp is missing"
        } else {
            try {
                $state = Get-Content -LiteralPath $npmStatePath -Raw | ConvertFrom-Json
                $requiredProperties = @("lockSha256", "nodeMajor", "npmVersion", "restoredAtUtc")
                $hasProperties = $requiredProperties | ForEach-Object {
                    $state.PSObject.Properties.Name -contains $_
                } | Where-Object { -not $_ } | Measure-Object
                if ($hasProperties.Count -gt 0 -or
                    $state.lockSha256 -ne $lockHash -or
                    [int]$state.nodeMajor -ne $toolchain.NodeMajor -or
                    [string]$state.npmVersion -ne $toolchain.NpmVersion) {
                    $restoreRequired = $true
                    $reason = "npm state stamp does not match the lockfile or toolchain"
                }
            } catch {
                $restoreRequired = $true
                $reason = "npm state stamp is unreadable"
            }
        }
    }

    if (-not $restoreRequired) {
        & $toolchain.NodePath $toolchain.NpmCli ls --prefix $repoRoot --depth=0 --no-audit --no-fund *> $null
        if ($LASTEXITCODE -ne 0) {
            $restoreRequired = $true
            $reason = "npm ls reported an inconsistent dependency tree"
        }
    }

    if ($restoreRequired) {
        Write-Host "    Frontend restore required: $reason" -ForegroundColor DarkYellow
        Invoke-Checked "Restore pinned frontend dependencies with Node 22" {
            Invoke-Npm ci --prefix $repoRoot --cache $npmCache --no-audit --no-fund
        }
        $state = [ordered]@{
            lockSha256 = $lockHash
            nodeMajor = $toolchain.NodeMajor
            nodeVersion = $toolchain.NodeVersion
            npmVersion = $toolchain.NpmVersion
            restoredAtUtc = [DateTime]::UtcNow.ToString("o")
        }
        New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
        [IO.File]::WriteAllText(
            $npmStatePath,
            (($state | ConvertTo-Json) + [Environment]::NewLine),
            [Text.UTF8Encoding]::new($false))
    } else {
        Write-Host "==> Frontend dependencies match package-lock.json; npm ci skipped" -ForegroundColor Cyan
    }
}

function Assert-CoberturaCoverage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][double]$MinimumLinePercent,
        [Parameter(Mandatory)][double]$MinimumBranchPercent
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Coverage report was not produced at $Path."
    }
    [xml]$coverage = Get-Content -LiteralPath $Path
    $linePercent = [double]$coverage.coverage.'line-rate' * 100
    $branchPercent = [double]$coverage.coverage.'branch-rate' * 100
    Write-Host ("    C# coverage: line {0:N2}%, branch {1:N2}%" -f $linePercent, $branchPercent)
    if ($linePercent -lt $MinimumLinePercent -or $branchPercent -lt $MinimumBranchPercent) {
        throw "C# coverage regressed below line $MinimumLinePercent% / branch $MinimumBranchPercent%."
    }
}

function Assert-WebResults {
    if (-not (Test-Path -LiteralPath $webSummaryPath -PathType Leaf)) {
        throw "Vitest JSON summary was not produced at $webSummaryPath."
    }
    $summary = Get-Content -LiteralPath $webSummaryPath -Raw | ConvertFrom-Json
    if (-not $summary.success -or [int]$summary.numFailedTests -ne 0 -or
        [int]$summary.numTotalTests -lt $minimumTests.Frontend) {
        throw "Web test gate failed: total=$($summary.numTotalTests), failed=$($summary.numFailedTests), expected at least $($minimumTests.Frontend)."
    }

    $coveragePath = Join-Path $webResults "coverage\coverage-summary.json"
    if (-not (Test-Path -LiteralPath $coveragePath -PathType Leaf)) {
        throw "Web coverage summary was not produced at $coveragePath."
    }
    $coverage = Get-Content -LiteralPath $coveragePath -Raw | ConvertFrom-Json
    $requirements = [ordered]@{ lines = 78; statements = 75; functions = 75; branches = 65 }
    foreach ($metric in $requirements.Keys) {
        $actual = [double]$coverage.total.$metric.pct
        $minimum = [double]$requirements[$metric]
        if ($actual -lt $minimum) {
            throw "Web $metric coverage $actual% is below the $minimum% gate."
        }
    }
    Write-Host "    Web tests: $($summary.numPassedTests)/$($summary.numTotalTests); coverage gates passed."
}

function Invoke-DotNetTests {
    param(
        [string]$Filter,
        [int]$MinimumExpectedTests
    )

    New-Item -ItemType Directory -Path $dotnetResults -Force | Out-Null
    $trxPath = Join-Path $dotnetResults "PSX.$Suite.trx"
    $coveragePath = Join-Path $dotnetResults "coverage.cobertura.xml"
    Remove-Item -LiteralPath $trxPath, $coveragePath -Force -ErrorAction SilentlyContinue
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
    if ($LASTEXITCODE -ne 0) {
        return
    }

    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "TRX report was not produced at $trxPath."
    }
    [xml]$trx = Get-Content -LiteralPath $trxPath
    $counters = $trx.TestRun.ResultSummary.Counters
    $total = [int]$counters.total
    if ($total -lt $MinimumExpectedTests) {
        throw "Runner discovered $total tests; expected at least $MinimumExpectedTests."
    }
    Write-Host "    Runner tests: $($counters.passed)/$total."
    if ($Suite -in @("Fast", "Full")) {
        Assert-CoberturaCoverage -Path $coveragePath -MinimumLinePercent 67 -MinimumBranchPercent 55
    }
}

function Invoke-FrontendTests {
    Invoke-Checked "Type-check the WebView frontend" {
        Invoke-Npm run typecheck:web --prefix $repoRoot
    }
    Invoke-Checked "Lint the WebView frontend" {
        Invoke-Npm run lint:web --prefix $repoRoot
    }
    Invoke-Checked "Verify the committed wwwroot/app output is fresh" {
        Invoke-Npm run verify:web --prefix $repoRoot
    }
    Invoke-Checked "Run frontend tests with production-code coverage" {
        # V8 coverage retains the instrumented module graph until reporting.
        # Keep the run hard-bounded, but give that single Node process enough
        # headroom to finish instead of failing near the default 2 GiB cap.
        Remove-Item -LiteralPath $webSummaryPath -Force -ErrorAction SilentlyContinue
        $toolchain = Get-FrontendToolchain
        powershell -ExecutionPolicy Bypass -File (
            Join-Path $repoRoot "tools\run-guarded-vitest.ps1"
        ) -NodePath $toolchain.NodePath -ProcessMemoryLimitMB 2560 -JobMemoryLimitMB 3072 --coverage
    }
    Assert-WebResults
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

    if ($runFrontend) {
        New-Item -ItemType Directory -Path $npmCache, $webResults -Force | Out-Null
        Initialize-FrontendDependencies
    }

    if ($runDotNet) {
        Invoke-Checked "Build application, probes, Fake ACP Agent, Fake npm, and tests" {
            dotnet build $solution --configuration Release
        }

        switch ($Suite) {
            "Unit" {
                Invoke-Checked "Run C# unit tests with coverage" {
                    Invoke-DotNetTests -Filter "TestCategory=Unit" -MinimumExpectedTests $minimumTests.Unit
                }
            }
            "Integration" {
                Invoke-Checked "Run ACP and persistence integration tests with coverage" {
                    Invoke-DotNetTests -Filter "TestCategory=Integration" -MinimumExpectedTests $minimumTests.Integration
                }
            }
            "Desktop" {
                Invoke-Checked "Run Windows desktop and ConPTY tests with coverage" {
                    Invoke-DotNetTests -Filter "TestCategory=Desktop" -MinimumExpectedTests $minimumTests.Desktop
                }
            }
            "Fast" {
                Invoke-Checked "Run C# unit and integration tests with coverage" {
                    Invoke-DotNetTests -Filter "(TestCategory=Unit)|(TestCategory=Integration)" -MinimumExpectedTests $minimumTests.Fast
                }
            }
            "Full" {
                Invoke-Checked "Run all C# tests, including desktop probes, with coverage" {
                    Invoke-DotNetTests -Filter "" -MinimumExpectedTests $minimumTests.Full
                }
            }
        }
    }

    if ($runFrontend) {
        Invoke-FrontendTests
    }

    if ($runFormatting) {
        Invoke-Checked "Verify .NET formatting" {
            dotnet format $solution --verify-no-changes
        }
    }

    if ($runRelease) {
        Invoke-Checked "Build and validate the portable release package" {
            powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\build-release.ps1")
        }
        Invoke-Checked "Run the packaged React browser smoke" {
            powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\smoke-release.ps1")
        }
        $toolchain = Get-FrontendToolchain
        Invoke-Checked "Prepare locked Chromium for Full visual checks" {
            powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\prepare-playwright.ps1") -NodePath $toolchain.NodePath
        }
        Invoke-Checked "Run Full visual and accessibility checks" {
            & $toolchain.NodePath (Join-Path $repoRoot "tools\screenshot-baseline.mjs")
        }
        if ($SkipDependencyAudit) {
            Write-Warning "Full diagnostics passed, release gate incomplete: dependency audit was skipped."
        } else {
            Invoke-Checked "Audit NuGet and npm dependencies" {
                powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "tools\audit-dependencies.ps1") -NodePath $toolchain.NodePath
            }
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
