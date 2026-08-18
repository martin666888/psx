[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$VitestArguments,
    [string]$NodePath = "",
    [ValidateRange(256, 16384)]
    [int]$ProcessMemoryLimitMB = 2048,
    [ValidateRange(512, 32768)]
    [int]$JobMemoryLimitMB = 3072
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($JobMemoryLimitMB -lt $ProcessMemoryLimitMB) {
    throw "JobMemoryLimitMB must be greater than or equal to ProcessMemoryLimitMB."
}

if (-not ("PsxVitestJob" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PsxVitestJob
{
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectLimitViolationInformation2 = 34;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_LIMIT_VIOLATION_INFORMATION_2
    {
        public uint LimitFlags;
        public uint ViolationLimitFlags;
        public ulong IoReadBytes;
        public ulong IoReadBytesLimit;
        public ulong IoWriteBytes;
        public ulong IoWriteBytesLimit;
        public long PerJobUserTime;
        public long PerJobUserTimeLimit;
        public ulong JobMemory;
        public ulong JobMemoryLimit;
        public uint RateControlTolerance;
        public uint RateControlToleranceLimit;
        public ulong JobLowMemoryLimit;
        public uint IoRateControlTolerance;
        public uint IoRateControlToleranceLimit;
        public uint NetRateControlTolerance;
        public uint NetRateControlToleranceLimit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    public static IntPtr CreateLimitedJob(
        ulong processMemoryLimitBytes,
        ulong jobMemoryLimitBytes)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new InvalidOperationException(
                "CreateJobObject failed: " + Marshal.GetLastWin32Error());

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_PROCESS_MEMORY |
            JOB_OBJECT_LIMIT_JOB_MEMORY |
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        limits.ProcessMemoryLimit = new UIntPtr(processMemoryLimitBytes);
        limits.JobMemoryLimit = new UIntPtr(jobMemoryLimitBytes);

        WriteInformation(job, limits);
        return job;
    }

    public static void Assign(IntPtr job, IntPtr process)
    {
        if (!AssignProcessToJobObject(job, process))
            throw new InvalidOperationException(
                "AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
    }

    public static ulong GetPeakJobMemory(IntPtr job)
    {
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                buffer,
                (uint)size,
                IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    "QueryInformationJobObject failed: " + Marshal.GetLastWin32Error());
            }

            var limits = (JOBOBJECT_EXTENDED_LIMIT_INFORMATION)
                Marshal.PtrToStructure(
                    buffer,
                    typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            return limits.PeakJobMemoryUsed.ToUInt64();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string GetLimitViolationReason(IntPtr job)
    {
        int size = Marshal.SizeOf(typeof(JOBOBJECT_LIMIT_VIOLATION_INFORMATION_2));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                job,
                JobObjectLimitViolationInformation2,
                buffer,
                (uint)size,
                IntPtr.Zero))
            {
                return "query_failed:" + Marshal.GetLastWin32Error();
            }

            var violation = (JOBOBJECT_LIMIT_VIOLATION_INFORMATION_2)
                Marshal.PtrToStructure(
                    buffer,
                    typeof(JOBOBJECT_LIMIT_VIOLATION_INFORMATION_2));
            bool processMemory =
                (violation.ViolationLimitFlags & JOB_OBJECT_LIMIT_PROCESS_MEMORY) != 0;
            bool jobMemory =
                (violation.ViolationLimitFlags & JOB_OBJECT_LIMIT_JOB_MEMORY) != 0;
            if (processMemory && jobMemory)
                return "process_and_job_memory";
            if (processMemory)
                return "process_memory";
            if (jobMemory)
                return "job_memory";
            return "none";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WriteInformation(
        IntPtr job,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits)
    {
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                buffer,
                (uint)size))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new InvalidOperationException(
                    "SetInformationJobObject failed: " + error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
'@
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$testRoot = Join-Path $repositoryRoot "tests\PSX.Web.Tests"
$vitestPath = Join-Path $repositoryRoot "node_modules\vitest\vitest.mjs"
$webResults = Join-Path $repositoryRoot "TestResults\web"
$progressPath = Join-Path $webResults "vitest-progress.json"
$diagnosticPath = Join-Path $webResults "guard-diagnostic.json"
$pathNode = "<not checked>"

function Get-VitestProcessTree {
    param([int]$RootProcessId)

    try {
        $processes = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, WorkingSetSize, PageFileUsage)
        $selected = New-Object System.Collections.Generic.List[object]
        $pending = New-Object System.Collections.Generic.Queue[uint32]
        $seen = New-Object "System.Collections.Generic.HashSet[uint32]"
        $pending.Enqueue([uint32]$RootProcessId)
        while ($pending.Count -gt 0) {
            $current = $pending.Dequeue()
            if (-not $seen.Add($current)) {
                continue
            }
            $entry = $processes | Where-Object { [uint32]$_.ProcessId -eq $current } | Select-Object -First 1
            if ($null -ne $entry) {
                $selected.Add([ordered]@{
                    processId = [int]$entry.ProcessId
                    parentProcessId = [int]$entry.ParentProcessId
                    name = [string]$entry.Name
                    workingSetBytes = [uint64]$entry.WorkingSetSize
                    commitBytes = [uint64]$entry.PageFileUsage * 1KB
                })
            }
            foreach ($child in $processes | Where-Object { [uint32]$_.ParentProcessId -eq $current }) {
                $pending.Enqueue([uint32]$child.ProcessId)
            }
        }
        return @($selected | Sort-Object { $_.processId })
    }
    catch {
        return @()
    }
}

function Read-VitestProgress {
    if (-not (Test-Path -LiteralPath $progressPath -PathType Leaf)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Write-GuardDiagnostic {
    param(
        [Parameter(Mandatory)]
        [string]$Reason,
        [int]$VitestExitCode,
        [string]$LimitViolation,
        [double]$PeakMemoryMB,
        [object[]]$ProcessTree,
        [string]$ErrorType = ""
    )

    try {
        [IO.Directory]::CreateDirectory($webResults) | Out-Null
        $progress = Read-VitestProgress
        $payload = [ordered]@{
            schemaVersion = 1
            reason = $Reason
            exitCode = $VitestExitCode
            limitViolation = $LimitViolation
            processMemoryLimitMB = $ProcessMemoryLimitMB
            jobMemoryLimitMB = $JobMemoryLimitMB
            peakProcessTreeCommitMB = $PeakMemoryMB
            lastStartedTestFile = if ($null -eq $progress) { $null } else { $progress.lastStartedFile }
            lastCompletedTestFile = if ($null -eq $progress) { $null } else { $progress.lastCompletedFile }
            processTree = @($ProcessTree)
            errorType = if ([string]::IsNullOrWhiteSpace($ErrorType)) { $null } else { $ErrorType }
        }
        $temporaryPath = "$diagnosticPath.$PID.tmp"
        [IO.File]::WriteAllText(
            $temporaryPath,
            (($payload | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $diagnosticPath, $null)
        } else {
            [IO.File]::Move($temporaryPath, $diagnosticPath)
        }
    }
    catch {
        Write-Warning "Unable to write Vitest guard diagnostic: $($_.Exception.GetType().Name)."
    }
}

if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $pathNodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -ne $pathNodeCommand) {
        $pathNode = $pathNodeCommand.Source
    }
    $portableTestNode = Join-Path $repositoryRoot (
        "TestResults\node22\node-v22.23.1-win-x64\node.exe")
    $releaseNode = Join-Path $repositoryRoot (
        "bin\release-staging\tools\node\node.exe")
    $candidates = @($pathNode, $portableTestNode, $releaseNode) |
        Where-Object { $_ -ne "<not checked>" } |
        Select-Object -Unique
} else {
    $candidates = @([IO.Path]::GetFullPath($NodePath))
}

$nodePath = $null
foreach ($candidate in $candidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }
    $candidateVersion = & $candidate --version
    if ($LASTEXITCODE -eq 0 -and $candidateVersion -match "^v22\.") {
        $nodePath = $candidate
        break
    }
}

if ($null -eq $nodePath) {
    throw (
        "Frontend tests require Node 22, matching CI. " +
        "No Node 22 executable was found on PATH or in the portable test/release locations. " +
        "The current PATH resolves to $pathNode."
    )
}

if (-not (Test-Path -LiteralPath $vitestPath -PathType Leaf)) {
    throw "Vitest is not installed at $vitestPath. Run npm ci first."
}

[IO.Directory]::CreateDirectory($webResults) | Out-Null
Remove-Item -LiteralPath $progressPath, $diagnosticPath -Force -ErrorAction SilentlyContinue

$processMemoryLimitBytes = [uint64]$ProcessMemoryLimitMB * 1MB
$jobMemoryLimitBytes = [uint64]$JobMemoryLimitMB * 1MB
$job = [PsxVitestJob]::CreateLimitedJob(
    $processMemoryLimitBytes,
    $jobMemoryLimitBytes)
$process = $null
$exitCode = 1
$peakMemoryMB = 0
$limitViolation = "none"
$lastProcessTree = @()
$runnerError = $null

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $nodePath
    $startInfo.WorkingDirectory = $testRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # Repository and test paths contain no spaces. Avoid ArgumentList here
    # because the script must also work in Windows PowerShell 5.1.
    $startInfo.Arguments = (@($vitestPath, "run") + $VitestArguments) -join " "

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start Vitest."
    }

    try {
        [PsxVitestJob]::Assign($job, $process.Handle)
    }
    catch {
        $process.Kill()
        throw
    }

    Write-Host (
        "[memory guard] Node $(& $nodePath --version), Vitest PID $($process.Id): " +
        "$ProcessMemoryLimitMB MB/process, " +
        "$JobMemoryLimitMB MB/process tree."
    ) -ForegroundColor Cyan

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $lastProcessTree = @(Get-VitestProcessTree -RootProcessId $process.Id)
    # CIM provides the parent/child relationship and commit figures that a
    # useful failure snapshot needs, but querying it every second noticeably
    # competes with the single Vitest worker. Keep the latest five-second
    # sample; an abnormal exit takes one final sample below when possible.
    while (-not $process.WaitForExit(5000)) {
        $snapshot = @(Get-VitestProcessTree -RootProcessId $process.Id)
        if ($snapshot.Count -gt 0) {
            $lastProcessTree = $snapshot
        }
    }
    $snapshot = @(Get-VitestProcessTree -RootProcessId $process.Id)
    if ($snapshot.Count -gt 0) {
        $lastProcessTree = $snapshot
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if (-not [string]::IsNullOrEmpty($stdout)) {
        Write-Host $stdout
    }
    if (-not [string]::IsNullOrEmpty($stderr)) {
        [Console]::Error.WriteLine($stderr)
    }
    $exitCode = $process.ExitCode
    $peakMemoryMB = [Math]::Round(
        [PsxVitestJob]::GetPeakJobMemory($job) / 1MB,
        1)
    $limitViolation = [PsxVitestJob]::GetLimitViolationReason($job)
    Write-Host "[memory guard] Peak process-tree commit: $peakMemoryMB MB."
    if ($exitCode -ne 0) {
        $reason = if ($limitViolation -in @("process_memory", "job_memory", "process_and_job_memory")) {
            "memory_limit"
        } else {
            "vitest_exit"
        }
        Write-GuardDiagnostic `
            -Reason $reason `
            -VitestExitCode $exitCode `
            -LimitViolation $limitViolation `
            -PeakMemoryMB $peakMemoryMB `
            -ProcessTree $lastProcessTree
        $progress = Read-VitestProgress
        $lastCompleted = if ($null -eq $progress -or [string]::IsNullOrWhiteSpace($progress.lastCompletedFile)) {
            "<none>"
        } else {
            $progress.lastCompletedFile
        }
        Write-Host (
            "[memory guard] Failure: reason=$reason, limit=$limitViolation, " +
            "last completed=$lastCompleted. Diagnostic: TestResults/web/guard-diagnostic.json"
        ) -ForegroundColor Yellow
    }
}
catch {
    $runnerError = $_
    if ($null -ne $process) {
        $snapshot = @(Get-VitestProcessTree -RootProcessId $process.Id)
        if ($snapshot.Count -gt 0) {
            $lastProcessTree = $snapshot
        }
    }
    if ($job -ne [IntPtr]::Zero) {
        try {
            $peakMemoryMB = [Math]::Round([PsxVitestJob]::GetPeakJobMemory($job) / 1MB, 1)
            $limitViolation = [PsxVitestJob]::GetLimitViolationReason($job)
        }
        catch { }
    }
    Write-GuardDiagnostic `
        -Reason "runner_error" `
        -VitestExitCode $exitCode `
        -LimitViolation $limitViolation `
        -PeakMemoryMB $peakMemoryMB `
        -ProcessTree $lastProcessTree `
        -ErrorType $_.Exception.GetType().Name
}
finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
    if ($job -ne [IntPtr]::Zero) {
        [void][PsxVitestJob]::CloseHandle($job)
    }
}

if ($null -ne $runnerError) {
    throw $runnerError
}

exit $exitCode
