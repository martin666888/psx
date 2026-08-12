[CmdletBinding()]
param(
    [switch]$Coverage,
    [string]$NodePath = "",
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$VitestArguments
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$testResultsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "TestResults\web"))
$testRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "tests\PSX.Web.Tests"))
$guardPath = Join-Path $PSScriptRoot "run-guarded-vitest.ps1"
$progressReporter = Join-Path $repositoryRoot "tests\PSX.Web.Tests\test\progressReporter.js"
$blobDirectory = [IO.Path]::GetFullPath((Join-Path $testResultsRoot "vitest-blobs-$PID"))

if (-not $blobDirectory.StartsWith($testResultsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Vitest blob directory escaped TestResults/web."
}

[IO.Directory]::CreateDirectory($blobDirectory) | Out-Null

try {
    $testFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $testRoot "test") -Recurse -Filter "*.test.js" -File |
            Sort-Object FullName
    )
    if ($testFiles.Count -eq 0) {
        throw "No Web test files were discovered."
    }

    for ($index = 0; $index -lt $testFiles.Count; $index++) {
        $testFile = $testFiles[$index]
        $testFilePath = [IO.Path]::GetFullPath($testFile.FullName)
        $testRootPrefix = $testRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $testFilePath.StartsWith($testRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Web test file escaped the test root."
        }
        $relativeTestFile = $testFilePath.Substring($testRootPrefix.Length).Replace('\', '/')
        $blobPath = Join-Path $blobDirectory ("file-{0:D3}.json" -f ($index + 1))
        $arguments = @(
            $relativeTestFile,
            "--reporter=blob",
            "--reporter=$progressReporter",
            "--outputFile.blob=$blobPath"
        )
        if ($Coverage) {
            $arguments += "--coverage"
        }
        $arguments += $VitestArguments

        Write-Host (
            "[web tests] Running file {0}/{1}: {2}." -f ($index + 1), $testFiles.Count, $relativeTestFile
        ) -ForegroundColor Cyan
        & $guardPath -NodePath $NodePath -ProcessMemoryLimitMB 1024 -JobMemoryLimitMB 1536 @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Web test file $relativeTestFile failed with exit code $LASTEXITCODE."
        }
    }

    $mergeArguments = @("--mergeReports=$blobDirectory")
    if ($Coverage) {
        $mergeArguments += "--coverage"
    }
    Write-Host "[web tests] Merging $($testFiles.Count) sequential file reports." -ForegroundColor Cyan
    & $guardPath -NodePath $NodePath -ProcessMemoryLimitMB 1024 -JobMemoryLimitMB 1536 @mergeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Web test report merge failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $blobDirectory -PathType Container) {
        Remove-Item -LiteralPath $blobDirectory -Recurse -Force
    }
}
