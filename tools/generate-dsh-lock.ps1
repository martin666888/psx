# Generates repository-trusted, hash-pinned DSH update lockfiles under
# tools/dsh-locks/ so PSX clients install updates with `npm ci` only and
# never resolve dependency versions on the user's machine.
#
# ISOLATION CONTRACT (supply-chain boundary):
#   The lock-solve phase runs `npm install --package-lock-only
#   --ignore-scripts`, which never executes third-party package scripts and
#   is safe on any machine. The smoke phase runs `npm ci` WITH lifecycle
#   scripts against not-yet-reviewed third-party code. Any run that writes
#   catalog entries must therefore execute entirely inside a disposable
#   environment: a one-time Windows x64 VM or a dedicated CI runner without
#   signing keys, npm tokens or GitHub tokens, using a standard-privilege
#   account, destroyed after the run. Artifacts reach the repository only
#   through Git review. The release/signing machine only performs static
#   verification (tools/build-release.ps1) and never runs package scripts.
#
# Same-version SRI changes are security events: existing entries are never
# regenerated, and a changed official dist.integrity for a bundled version
# aborts the run for manual investigation instead of updating the catalog.
#
# Usage:
#   Spike / dry run (solve + checks only, never writes the catalog):
#     powershell -ExecutionPolicy Bypass -File tools/generate-dsh-lock.ps1 `
#       -Version 0.1.1-rc.1 -NoSmoke
#   Full generation (isolated environment only):
#     powershell -ExecutionPolicy Bypass -File tools/generate-dsh-lock.ps1 `
#       -Version 0.1.1-rc.1 -Isolated
#   Re-run the launch smoke for every existing entry:
#     powershell -ExecutionPolicy Bypass -File tools/generate-dsh-lock.ps1 `
#       -Version 0.0.0 -Isolated -FullResmoke   # -Version is ignored here
[CmdletBinding()]
param(
    # One or more versions, comma/semicolon separated (Windows PowerShell -File
    # cannot bind arrays, so the script splits the raw string itself).
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$Isolated,
    [switch]$NoSmoke,
    [int]$Keep = 5,
    [switch]$FullResmoke,
    [string]$NpmCache = "",
    [int]$TimeoutMinutes = 60
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest

# ---- pins (keep in sync with tools/build-release.ps1) ----
$PortableNodeVersion = "v22.23.1"
$PortableNodeArchive = "node-$PortableNodeVersion-win-x64.zip"
$PortableNodeUrl = "https://nodejs.org/dist/$PortableNodeVersion/$PortableNodeArchive"
$PortableNodeExpectedSha = "7df0bc9375723f4a86b3aa1b7cc73342423d9677a8df4538aca31a049e309c29"

$DshPackageName = "@deepseek-ai/dsh"
$DshSeedVersion = "0.1.1-rc.2"
$OfficialRegistry = "https://registry.npmjs.org/"
$SmokeReadyTimeoutSeconds = 120
$SmokeKillGraceSeconds = 30
$MaxEntries = 16
$MaxBlocked = 10
$BlockedReasons = @("smoke_failed", "official_integrity_mismatch", "sri_conflict")

if ($Keep -lt 1 -or $Keep -gt 5) {
    throw "-Keep must be between 1 and 5 (catalog keeps at most five approved versions)."
}
if ($NoSmoke -and $FullResmoke) {
    throw "-NoSmoke and -FullResmoke cannot be combined."
}
if (-not $Isolated -and -not $NoSmoke) {
    throw @"
Refusing to run: the smoke phase executes unreviewed third-party lifecycle
scripts via npm ci. Either pass -NoSmoke for a dry run (solve + checks only,
nothing is written), or run the full generation inside the disposable
environment described in the ISOLATION CONTRACT at the top of this script and
pass -Isolated to confirm it.
"@
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$LocksDirectory = Join-Path $RepoRoot "tools\dsh-locks"
$LocksVersionsDirectory = Join-Path $LocksDirectory "locks"
$CatalogPath = Join-Path $LocksDirectory "catalog.json"
$SeedDirectory = Join-Path $RepoRoot "tools\dsh-seed"
$WorkRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "TestResults\dsh-lock-gen"))
if ([string]::IsNullOrWhiteSpace($NpmCache)) {
    $NpmCache = [IO.Path]::GetFullPath((Join-Path $RepoRoot "TestResults\dsh-lock-npm-cache"))
}
$SemverTool = Join-Path $RepoRoot "tools\dsh-semver.mjs"
$BuildCacheDir = [IO.Path]::GetFullPath((Join-Path $RepoRoot "bin\build-cache"))

# idealTree resolution for the DSH dependency graph peaks above Node's
# ~2 GB default heap cap and dies with "JavaScript heap out of memory" on
# otherwise adequate machines. Raise the ceiling for every node/npm child
# (solve, ci, launch smoke); it is a limit, not an allocation, so smaller
# workloads are unaffected. Respect an operator-provided NODE_OPTIONS.
if ($env:NODE_OPTIONS -notmatch "max-old-space-size") {
    $env:NODE_OPTIONS = ("--max-old-space-size=6144 " + [string]$env:NODE_OPTIONS).Trim()
}

# ---- toolchain: pinned portable Node (never the system npm) ----
function Resolve-Toolchain {
    $repoNode = Join-Path $RepoRoot "tools\node\node.exe"
    $toolchainRoot = Join-Path $WorkRoot "toolchain"
    if (Test-Path -LiteralPath $repoNode -PathType Leaf) {
        Write-Host "    Toolchain: repository portable Node $PortableNodeVersion"
        return @{ Node = $repoNode; NpmCli = (Join-Path (Split-Path -Parent $repoNode) "node_modules\npm\bin\npm-cli.js") }
    }

    $archivePath = Join-Path $BuildCacheDir $PortableNodeArchive
    New-Item -ItemType Directory -Path $BuildCacheDir -Force | Out-Null
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        Write-Host "    Downloading $PortableNodeUrl"
        Invoke-WebRequest -Uri $PortableNodeUrl -OutFile $archivePath -UseBasicParsing
    }
    $actualSha = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualSha, $PortableNodeExpectedSha, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archivePath -Force
        throw "Portable Node SHA-256 mismatch. Expected $PortableNodeExpectedSha but got $actualSha."
    }

    if (Test-Path -LiteralPath $toolchainRoot) {
        Remove-Item -Recurse -Force $toolchainRoot
    }
    New-Item -ItemType Directory -Path $toolchainRoot -Force | Out-Null
    Expand-Archive -Path $archivePath -DestinationPath $toolchainRoot -Force
    $extractedRoot = Get-ChildItem -Directory $toolchainRoot | Select-Object -First 1
    if ($extractedRoot) {
        Get-ChildItem $extractedRoot.FullName -Force | Move-Item -Destination $toolchainRoot -Force
        Remove-Item -Recurse -Force $extractedRoot.FullName
    }
    $nodeExe = Join-Path $toolchainRoot "node.exe"
    if (-not (Test-Path -LiteralPath $nodeExe -PathType Leaf)) {
        throw "Portable Node extraction failed: node.exe not found under $toolchainRoot"
    }
    Write-Host "    Toolchain: downloaded portable Node $PortableNodeVersion (SHA verified)"
    return @{ Node = $nodeExe; NpmCli = (Join-Path $toolchainRoot "node_modules\npm\bin\npm-cli.js") }
}

function ConvertTo-ArgumentString {
    param([string[]]$Arguments)
    $parts = foreach ($argument in $Arguments) {
        if ($argument -match '\s') { '"{0}"' -f $argument } else { $argument }
    }
    return ($parts -join " ")
}

function Invoke-NodeProcess {
    <#Runs node.exe and waits for exit under a bounded timeout. On timeout the
    whole process tree is killed via taskkill /T /F so nothing outlives the
    run. Returns @{ ExitCode; TimedOut; Stdout; Stderr; }.#>
    param(
        [string]$NodePath,
        [string[]]$NodeArguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $NodePath
    $startInfo.Arguments = ConvertTo-ArgumentString $NodeArguments
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    # Clean machines have no node on PATH, but package lifecycle scripts
    # (koffi's install step, node-gyp helpers) invoke bare `node` through
    # cmd.exe. Expose the pinned portable Node directory to every child.
    $nodeDirectory = Split-Path -Parent $NodePath
    $childPath = $startInfo.EnvironmentVariables["Path"]
    if ([string]::IsNullOrEmpty($childPath)) { $childPath = $env:PATH }
    $startInfo.EnvironmentVariables["Path"] = "$nodeDirectory;$childPath"

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start: $NodePath"
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        & taskkill /PID $process.Id /T /F | Out-Null
        $process.WaitForExit($SmokeKillGraceSeconds * 1000) | Out-Null
    }
    return @{
        ExitCode = $process.ExitCode
        TimedOut = $timedOut
        Stdout = $stdoutTask.Result
        Stderr = $stderrTask.Result
    }
}

function Compare-SemVer2 {
    param([string]$Left, [string]$Right)
    $result = (& $script:Toolchain.Node $SemverTool $Left $Right)
    if ($LASTEXITCODE -ne 0) {
        throw "dsh-semver.mjs rejected a version: $Left vs $Right"
    }
    return [int]$result
}

function Test-ValidSemVer {
    param([string]$Value)
    & $script:Toolchain.Node $SemverTool $Value $Value | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Get-FileSha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function ConvertTo-LfText {
    param([string]$Text)
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Write-LfTextFile {
    param([string]$Path, [string]$Text)
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, (ConvertTo-LfText -Text $Text), $utf8NoBom)
}

function Normalize-LfTextFile {
    param([string]$Path)
    Write-LfTextFile -Path $Path -Text ([IO.File]::ReadAllText($Path))
}

function Write-UpdatePackageJson {
    <#Same synthetic root manifest shape DshWebRuntime.WriteUpdatePackageJson
    produces on the client; the client later copies this file verbatim.#>
    param([string]$Directory, [string]$DshVersion)
    $manifest = @"
{
  "name": "psx-dsh-runtime",
  "version": "0.1.0",
  "private": true,
  "description": "PSX-managed DeepSeek Harness runtime update.",
  "dependencies": {
    "@deepseek-ai/dsh": "$DshVersion"
  }
}
"@
    Write-LfTextFile -Path (Join-Path $Directory "package.json") -Text $manifest
}

function Read-Catalog {
    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
        return @{ Entries = @(); Blocked = @() }
    }
    $catalog = ConvertFrom-Json (Get-Content -LiteralPath $CatalogPath -Raw)
    if ($null -ne $catalog -and $catalog.PSObject.Properties["schemaVersion"] -and [int]$catalog.schemaVersion -ne 1) {
        throw "Unsupported catalog schemaVersion: $($catalog.schemaVersion)"
    }
    $entries = @()
    if ($catalog.PSObject.Properties["entries"] -and $null -ne $catalog.entries) {
        $entries = @($catalog.entries)
    }
    $blocked = @()
    if ($catalog.PSObject.Properties["blockedVersions"] -and $null -ne $catalog.blockedVersions) {
        $blocked = @($catalog.blockedVersions)
    }
    return @{ Entries = $entries; Blocked = $blocked }
}

function Test-BlockedVersion {
    param([object[]]$Blocked, [string]$Value)
    foreach ($entry in $Blocked) {
        if ([string]::Equals($entry.version, $Value, [StringComparison]::Ordinal)) {
            return $entry.reason
        }
    }
    return $null
}

function Assert-ExistingEntryIntegrityUnchanged {
    <#The "existing entries are never regenerated" promise is not a skip:
    the live official dist.integrity must still be compared against the
    recorded dshSri, so a same-version byte change is detected as the
    security event the trust model requires.#>
    param([object]$ExistingEntry)

    $version = [string]$ExistingEntry.version
    $officialSri = Get-OfficialDshIntegrity -DshVersion $version
    if (-not [string]::Equals([string]$ExistingEntry.dshSri, $officialSri, [StringComparison]::Ordinal)) {
        Write-Host "    SECURITY EVENT: official dist.integrity for $version differs from the recorded catalog dshSri." -ForegroundColor Red
        Write-Host "    Treat this as a registry anomaly or a compromised republish; investigate manually." -ForegroundColor Red
        Write-Host ""
        Write-Host "    Required remediation (via a reviewed PR, after the investigation concludes):" -ForegroundColor Yellow
        Write-Host "      1. Remove version $version from catalog.json entries AND delete" -ForegroundColor Yellow
        Write-Host "         tools/dsh-locks/locks/$version/." -ForegroundColor Yellow
        Write-Host "      2. Only if a review decision to reject the new bytes was made, add" -ForegroundColor Yellow
        Write-Host ('         {{ "version": "{0}", "reason": "sri_conflict" }}' -f $version) -ForegroundColor Yellow
        Write-Host "         to blockedVersions. Never keep one version in both lists." -ForegroundColor Yellow
        Write-Host "      3. Update `$DshLockCatalogExpectedSha in tools/build-release.ps1." -ForegroundColor Yellow
        throw "Aborting; the catalog is never refreshed to accept new bytes for an existing version."
    }
    Write-Host "    Already bundled; official dist.integrity still matches the recorded dshSri. Skipping." -ForegroundColor Yellow
}

function Write-SuggestedBlockedEntry {
    param([string]$DshVersion, [string]$Reason)
    Write-Host ""
    Write-Host "    Suggested catalog decision (add by hand after review, never automatically):" -ForegroundColor Yellow
    Write-Host ('      {{ "version": "{0}", "reason": "{1}" }}' -f $DshVersion, $Reason) -ForegroundColor Yellow
    Write-Host "    Append it to blockedVersions in tools/dsh-locks/catalog.json via a reviewed PR." -ForegroundColor Yellow
}

function Test-LockPreconditions {
    <#Best-effort structural prechecks in PowerShell; the authoritative gate
    is DshLockCatalogTests + DshLockValidator on the committed artifact.#>
    param([string]$LockPath, [string]$DshVersion)

    $lockText = Get-Content -LiteralPath $LockPath -Raw
    if ($lockText -notmatch '"lockfileVersion"\s*:\s*3') {
        throw "Generated lockfile is not lockfileVersion 3."
    }
    if ($lockText -notmatch ('"@deepseek-ai/dsh"\s*:\s*"{0}"' -f [Regex]::Escape($DshVersion))) {
        throw "Generated lockfile root dependency is not pinned to $DshVersion."
    }
    $foreignResolved = [Regex]::Matches($lockText, '"resolved"\s*:\s*"(?!https://registry\.npmjs\.org/)[^"]+"')
    if ($foreignResolved.Count -gt 0) {
        throw "Generated lockfile contains $($foreignResolved.Count) resolved URL(s) outside the official origin."
    }
    $dshEntry = [Regex]::Match($lockText, ('"node_modules/@deepseek-ai/dsh"\s*:\s*\{{[^{{}}]*?"version"\s*:\s*"{0}"[^{{}}]*?"integrity"\s*:\s*"(sha512-[A-Za-z0-9+/=]+)"' -f [Regex]::Escape($DshVersion)))
    if (-not $dshEntry.Success) {
        throw "Generated lockfile has no integrity-pinned node_modules/@deepseek-ai/dsh entry."
    }
    return $dshEntry.Groups[1].Value
}

function Get-OfficialDshIntegrity {
    param([string]$DshVersion, [int]$TimeoutSeconds = 30)
    $uri = "https://registry.npmjs.org/@deepseek-ai/dsh/$([Uri]::EscapeDataString($DshVersion))"
    $response = Invoke-RestMethod -Uri $uri -TimeoutSec $TimeoutSeconds
    if ($null -eq $response -or $null -eq $response.dist -or [string]::IsNullOrWhiteSpace($response.dist.integrity)) {
        throw "Official metadata for $DshPackageName@$DshVersion has no dist.integrity."
    }
    return [string]$response.dist.integrity
}

function Test-DshReadyUrl {
    <#Mirrors DshWebRuntimeSupervisor.TryAcceptReadyUrl: only the loopback
    HTTP origin DSH is allowed to bind, no userinfo.#>
    param([string]$Candidate)
    $parsed = $null
    if (-not [Uri]::TryCreate($Candidate, [UriKind]::Absolute, [ref]$parsed)) {
        return $false
    }
    if (-not [string]::Equals($parsed.Scheme, "http", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    if (-not [string]::Equals($parsed.Host, "127.0.0.1", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    if (-not [string]::IsNullOrEmpty($parsed.UserInfo)) {
        return $false
    }
    return $true
}

function Invoke-DshLaunchSmoke {
    <#Installs the candidate exactly the way the PSX client does (npm ci with
    lifecycle scripts), launches `dsh web` on loopback the way
    DshWebRuntimeSupervisor does, waits for a validated ready URL, probes it,
    then tears the whole process tree down. Every failure rejects the
    version from the catalog.#>
    param([string]$SmokeDirectory)

    $ciArguments = @(
        $script:Toolchain.NpmCli, "ci",
        "--registry=$OfficialRegistry",
        "--replace-registry-host=npmjs",
        "--omit=dev", "--include=optional", "--engine-strict",
        "--no-audit", "--no-fund",
        "--cache=$NpmCache",
        "--userconfig=$(Join-Path $SmokeDirectory 'npmrc-user')"
    )
    $ciStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $ci = Invoke-NodeProcess -NodePath $script:Toolchain.Node -NodeArguments $ciArguments `
        -WorkingDirectory $SmokeDirectory -TimeoutSeconds ($TimeoutMinutes * 60)
    $ciStopwatch.Stop()
    if ($ci.TimedOut) {
        throw "Smoke npm ci timed out after $TimeoutMinutes minutes."
    }
    if ($ci.ExitCode -ne 0) {
        throw "Smoke npm ci failed (exit $($ci.ExitCode)): $($ci.Stderr)"
    }
    Write-Host ("      npm ci: {0:n0}s" -f $ciStopwatch.Elapsed.TotalSeconds)

    $entryPath = Join-Path $SmokeDirectory "node_modules\@deepseek-ai\dsh\lib\bin.js"
    if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
        throw "Smoke install is missing the DSH entry file (lib/bin.js)."
    }

    $stdoutPath = Join-Path $SmokeDirectory "smoke-server.out.log"
    $stderrPath = Join-Path $SmokeDirectory "smoke-server.err.log"
    $server = Start-Process -FilePath $script:Toolchain.Node `
        -ArgumentList @($entryPath, "web", "--host", "127.0.0.1", "--port", "0", "--no-open") `
        -WorkingDirectory $SmokeDirectory `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
        -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($SmokeReadyTimeoutSeconds)
    $readyUrl = $null
    try {
        while ([DateTime]::UtcNow -lt $deadline -and -not $server.HasExited) {
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $stdoutPath) {
                $output = Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue
                if ($null -ne $output) {
                    $urlMatch = [Regex]::Match($output, 'https?://[^\s"''<>]+')
                    if ($urlMatch.Success -and (Test-DshReadyUrl -Candidate $urlMatch.Value)) {
                        $readyUrl = $urlMatch.Value
                        break
                    }
                }
            }
        }
        if ($null -eq $readyUrl) {
            $tail = ""
            if (Test-Path -LiteralPath $stderrPath) {
                $tail = (Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue)
            }
            throw "Smoke server never reported a valid loopback ready URL within $SmokeReadyTimeoutSeconds seconds. stderr: $tail"
        }
        $probe = Invoke-WebRequest -Uri $readyUrl -UseBasicParsing -TimeoutSec 15
        if ([int]$probe.StatusCode -ge 400) {
            throw "Smoke ready URL probe returned HTTP $($probe.StatusCode)."
        }
        Write-Host "      dsh web ready: $readyUrl (HTTP $($probe.StatusCode))"
    }
    finally {
        & taskkill /PID $server.Id /T /F | Out-Null
        if (-not $server.WaitForExit($SmokeKillGraceSeconds * 1000)) {
            throw "Smoke server process tree did not exit within $SmokeKillGraceSeconds seconds after taskkill."
        }
    }
}

function Copy-SeedNpmrc {
    param([string]$Destination)
    $seedNpmrc = Join-Path $SeedDirectory ".npmrc"
    if (-not (Test-Path -LiteralPath $seedNpmrc -PathType Leaf)) {
        throw "Missing tools/dsh-seed/.npmrc."
    }
    Copy-Item -LiteralPath $seedNpmrc -Destination (Join-Path $Destination ".npmrc") -Force
}

function New-SolveWorkspace {
    param([string]$DshVersion)
    $workDirectory = Join-Path $WorkRoot ([Uri]::EscapeDataString($DshVersion))
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -Recurse -Force $workDirectory
    }
    New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
    Write-UpdatePackageJson -Directory $workDirectory -DshVersion $DshVersion
    Copy-SeedNpmrc -Destination $workDirectory
    # Ignore any maintainer-level npm user config so registry/auth settings on
    # the generation machine cannot influence the solved tree.
    New-Item -ItemType File -Path (Join-Path $workDirectory "npmrc-user") -Force | Out-Null
    return $workDirectory
}

function Invoke-LockSolve {
    param([string]$DshVersion)
    # Reuse a solve that already completed on this machine (e.g. a smoke
    # failure interrupted the previous run): Test-LockPreconditions and the
    # official-integrity comparison below still gate the reused files, so
    # tampering in between cannot slip through.
    $resumeDirectory = Join-Path $WorkRoot ([Uri]::EscapeDataString($DshVersion))
    if ((Test-Path -LiteralPath (Join-Path $resumeDirectory "package-lock.json") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $resumeDirectory "package.json") -PathType Leaf)) {
        Write-Host "    Reusing the existing solve output for $DshVersion."
        Normalize-LfTextFile -Path (Join-Path $resumeDirectory "package.json")
        Normalize-LfTextFile -Path (Join-Path $resumeDirectory "package-lock.json")
        return $resumeDirectory
    }
    $workDirectory = New-SolveWorkspace -DshVersion $DshVersion
    $arguments = @(
        $script:Toolchain.NpmCli, "install", "$DshPackageName@$DshVersion",
        "--save-exact", "--omit=dev", "--include=optional", "--engine-strict",
        "--no-audit", "--no-fund", "--package-lock-only", "--ignore-scripts",
        "--registry=$OfficialRegistry",
        "--cache=$NpmCache",
        "--userconfig=$(Join-Path $workDirectory 'npmrc-user')"
    )
    Write-Host "    Solving $DshPackageName@$DshVersion (full idealTree resolution; this is the slow step)..."
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-NodeProcess -NodePath $script:Toolchain.Node -NodeArguments $arguments `
        -WorkingDirectory $workDirectory -TimeoutSeconds ($TimeoutMinutes * 60)
    $stopwatch.Stop()
    Write-Host ("    Solve took {0:n0}s (exit $($result.ExitCode))" -f $stopwatch.Elapsed.TotalSeconds)
    if ($result.TimedOut) {
        throw "Lock solve for $DshVersion timed out after $TimeoutMinutes minutes."
    }
    if ($result.ExitCode -ne 0) {
        throw "Lock solve for $DshVersion failed (exit $($result.ExitCode)): $($result.Stderr)"
    }
    Normalize-LfTextFile -Path (Join-Path $workDirectory "package.json")
    Normalize-LfTextFile -Path (Join-Path $workDirectory "package-lock.json")
    return $workDirectory
}

function New-SmokeWorkspace {
    param([string]$SolveDirectory, [string]$DshVersion)
    $smokeDirectory = Join-Path $SolveDirectory "smoke"
    # A resumed run may find the previous attempt's half-installed tree here;
    # remove it (npm/Defender can briefly retain handles, hence the retries)
    # so the fresh npm ci starts clean.
    if (Test-Path -LiteralPath $smokeDirectory) {
        $removed = $false
        foreach ($delay in @(0, 200, 500, 1000, 2000, 4000)) {
            if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
            try {
                Remove-Item -Recurse -Force $smokeDirectory -ErrorAction Stop
                $removed = $true
                break
            } catch [IOException], [UnauthorizedAccessException] { }
        }
        if (-not $removed) {
            throw "Could not clean the previous smoke workspace: $smokeDirectory"
        }
    }
    New-Item -ItemType Directory -Path $smokeDirectory -Force | Out-Null
    foreach ($name in @("package.json", "package-lock.json", ".npmrc")) {
        Copy-Item -LiteralPath (Join-Path $SolveDirectory $name) -Destination (Join-Path $smokeDirectory $name) -Force
    }
    New-Item -ItemType File -Path (Join-Path $smokeDirectory "npmrc-user") -Force | Out-Null
    return $smokeDirectory
}

function New-ResmokeWorkspace {
    <#Assembles a solve-shaped directory from an already-bundled entry so the
    launch smoke can be replayed against committed artifacts.#>
    param([string]$DshVersion)
    $solveDirectory = Join-Path $WorkRoot ("resmoke-" + [Uri]::EscapeDataString($DshVersion))
    if (Test-Path -LiteralPath $solveDirectory) {
        Remove-Item -Recurse -Force $solveDirectory
    }
    New-Item -ItemType Directory -Path $solveDirectory -Force | Out-Null
    foreach ($name in @("package.json", "package-lock.json", ".npmrc")) {
        $source = Join-Path $LocksVersionsDirectory "$DshVersion\$name"
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Catalog entry $DshVersion is missing $name under tools/dsh-locks/locks/."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $solveDirectory $name) -Force
    }
    return $solveDirectory
}

# ---- main ----
Write-Host "==> DSH lock generation" -ForegroundColor Cyan
Write-Host "    Mode: $(if ($NoSmoke) { "dry run (-NoSmoke; catalog is not written)" } elseif ($FullResmoke) { "full resmoke" } else { "full generation (isolated)" })"

$script:Toolchain = Resolve-Toolchain
$npmVersion = (& $script:Toolchain.Node $script:Toolchain.NpmCli --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not determine the bundled npm version."
}
Write-Host "    npm: $npmVersion (from pinned Node $PortableNodeVersion)"

foreach ($seedFile in @("package.json", "package-lock.json", ".npmrc")) {
    if (-not (Test-Path -LiteralPath (Join-Path $SeedDirectory $seedFile) -PathType Leaf)) {
        throw "Missing tools/dsh-seed/$seedFile."
    }
}

$catalog = Read-Catalog
$catalogEntryVersions = @($catalog.Entries | ForEach-Object { [string]$_.version })
$catalogBlocked = @($catalog.Blocked)

if ($FullResmoke) {
    foreach ($entry in $catalog.Entries) {
        $version = [string]$entry.version
        Write-Host "==> Resmoke $version" -ForegroundColor Cyan
        Assert-ExistingEntryIntegrityUnchanged -ExistingEntry $entry
        $solveDirectory = New-ResmokeWorkspace -DshVersion $version
        $smokeDirectory = New-SmokeWorkspace -SolveDirectory $solveDirectory -DshVersion $version
        Invoke-DshLaunchSmoke -SmokeDirectory $smokeDirectory
    }
    Write-Host "==> Full resmoke passed for $(@($catalog.Entries).Count) entr(ies)." -ForegroundColor Green
    return
}

$newEntries = @()
foreach ($requestedVersion in (@($Version -split "[,;]") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })) {
    Write-Host "==> Version $requestedVersion" -ForegroundColor Cyan
    if (-not (Test-ValidSemVer -Value $requestedVersion)) {
        throw "$requestedVersion is not a valid SemVer 2 string."
    }
    if ((Compare-SemVer2 -Left $requestedVersion -Right $DshSeedVersion) -lt 0) {
        throw "$requestedVersion is older than the seed ($DshSeedVersion); the catalog never carries pre-seed versions."
    }
    if ($catalogEntryVersions -contains $requestedVersion) {
        $existingEntry = @($catalog.Entries | Where-Object { [string]$_.version -eq $requestedVersion })[0]
        Assert-ExistingEntryIntegrityUnchanged -ExistingEntry $existingEntry
        continue
    }
    $blockedReason = Test-BlockedVersion -Blocked $catalogBlocked -Value $requestedVersion
    if ($null -ne $blockedReason) {
        throw "$requestedVersion is blocked by a review decision ($blockedReason); unblocking requires a dedicated review."
    }

    $solveDirectory = Invoke-LockSolve -DshVersion $requestedVersion
    $lockPath = Join-Path $solveDirectory "package-lock.json"
    $lockSri = Test-LockPreconditions -LockPath $lockPath -DshVersion $requestedVersion

    $officialSri = Get-OfficialDshIntegrity -DshVersion $requestedVersion
    if (-not [string]::Equals($lockSri, $officialSri, [StringComparison]::Ordinal)) {
        Write-Host "    SECURITY EVENT: official dist.integrity for $requestedVersion differs from the solved lock." -ForegroundColor Red
        Write-Host "    Treat this as a registry anomaly or a compromised republish; investigate manually." -ForegroundColor Red
        Write-SuggestedBlockedEntry -DshVersion $requestedVersion -Reason "official_integrity_mismatch"
        throw "Aborting; the catalog is never refreshed to accept new bytes for an existing version."
    }
    Write-Host "    Official dist.integrity matches the solved lock SRI."

    if (-not $NoSmoke) {
        $smokeDirectory = New-SmokeWorkspace -SolveDirectory $solveDirectory -DshVersion $requestedVersion
        Write-Host "    Smoke: npm ci + dsh web launch (third-party scripts execute)"
        Invoke-DshLaunchSmoke -SmokeDirectory $smokeDirectory
    }

    $newEntries += @{
        Version = $requestedVersion
        SolveDirectory = $solveDirectory
        DshSri = $officialSri
        LockSha256 = $null
        PackageSha256 = $null
    }
}

if ($NoSmoke) {
    Write-Host ""
    Write-Host "==> Dry run complete (nothing written)." -ForegroundColor Green
    foreach ($entry in $newEntries) {
        $lockPath = Join-Path $entry.SolveDirectory "package-lock.json"
        $packagePath = Join-Path $entry.SolveDirectory "package.json"
        Write-Host ("    {0}: lockSha256={1} packageSha256={2}" -f $entry.Version,
            (Get-FileSha256Hex -Path $lockPath), (Get-FileSha256Hex -Path $packagePath))
    }
    return
}

# ---- write artifacts + catalog ----
New-Item -ItemType Directory -Path $LocksVersionsDirectory -Force | Out-Null

$mergedEntries = @()
foreach ($existing in $catalog.Entries) {
    $version = [string]$existing.version
    $mergedEntries += [pscustomobject][ordered]@{
        version = $version
        lockSha256 = [string]$existing.lockSha256
        packageSha256 = [string]$existing.packageSha256
        lockfileVersion = [int]$existing.lockfileVersion
        generatedByNpm = [string]$existing.generatedByNpm
        dshSri = [string]$existing.dshSri
        smokePassed = [bool]$existing.smokePassed
    }
}
foreach ($entry in $newEntries) {
    $versionDirectory = Join-Path $LocksVersionsDirectory $entry.Version
    New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $entry.SolveDirectory "package.json") `
        -Destination (Join-Path $versionDirectory "package.json") -Force
    Copy-Item -LiteralPath (Join-Path $entry.SolveDirectory "package-lock.json") `
        -Destination (Join-Path $versionDirectory "package-lock.json") -Force
    $lockSha = Get-FileSha256Hex -Path (Join-Path $versionDirectory "package-lock.json")
    $packageSha = Get-FileSha256Hex -Path (Join-Path $versionDirectory "package.json")
    $mergedEntries += [pscustomobject][ordered]@{
        version = $entry.Version
        lockSha256 = $lockSha
        packageSha256 = $packageSha
        lockfileVersion = 3
        generatedByNpm = $npmVersion
        dshSri = $entry.DshSri
        smokePassed = $true
    }
}

# Newest first (via the shared SemVer module), then retain only the newest
# $Keep entries and prune their lock directories so locks/ always mirrors the
# surviving entry set.
$orderedEntries = New-Object System.Collections.ArrayList
foreach ($candidate in $mergedEntries) {
    $inserted = $false
    for ($index = 0; $index -lt $orderedEntries.Count; $index++) {
        if ((Compare-SemVer2 -Left $candidate.version -Right $orderedEntries[$index].version) -gt 0) {
            $orderedEntries.Insert($index, $candidate)
            $inserted = $true
            break
        }
    }
    if (-not $inserted) {
        $orderedEntries.Add($candidate) | Out-Null
    }
}
$keptEntries = @($orderedEntries | Select-Object -First $Keep)
if ($orderedEntries.Count -gt $Keep) {
    Write-Host "    Pruning $($orderedEntries.Count - $Keep) entr(ies) beyond -Keep $Keep."
}
$keptVersions = @($keptEntries | ForEach-Object { $_.version })
foreach ($pruned in @($orderedEntries | Select-Object -Skip $Keep)) {
    $prunedDirectory = Join-Path $LocksVersionsDirectory ([string]$pruned.version)
    if (Test-Path -LiteralPath $prunedDirectory) {
        Remove-Item -Recurse -Force $prunedDirectory
    }
}

$blockedJson = @()
foreach ($blocked in $catalogBlocked) {
    $blockedJson += [pscustomobject][ordered]@{
        version = [string]$blocked.version
        reason = [string]$blocked.reason
    }
}

$catalogJson = [pscustomobject][ordered]@{
    schemaVersion = 1
    entries = $keptEntries
    blockedVersions = $blockedJson
}
$catalogText = ConvertTo-Json -InputObject $catalogJson -Depth 6
Write-LfTextFile -Path $CatalogPath -Text ($catalogText + "`n")

$catalogSha = Get-FileSha256Hex -Path $CatalogPath
Write-Host ""
Write-Host "==> Catalog written: $CatalogPath" -ForegroundColor Green
Write-Host "    Entries: $(@($keptEntries).Count); blocked: $(@($blockedJson).Count)"
Write-Host "    catalog.json SHA-256: $catalogSha"
Write-Host "    Next: update `$DshLockCatalogExpectedSha in tools/build-release.ps1 to this value," -ForegroundColor Yellow
Write-Host "    then run the Fast suite (DshLockCatalogTests gates the committed artifact)." -ForegroundColor Yellow
