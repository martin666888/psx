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
$pathNode = "<not checked>"

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

$processMemoryLimitBytes = [uint64]$ProcessMemoryLimitMB * 1MB
$jobMemoryLimitBytes = [uint64]$JobMemoryLimitMB * 1MB
$job = [PsxVitestJob]::CreateLimitedJob(
    $processMemoryLimitBytes,
    $jobMemoryLimitBytes)
$process = $null
$exitCode = 1

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
    $process.WaitForExit()
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
    Write-Host "[memory guard] Peak process-tree commit: $peakMemoryMB MB."
}
finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
    if ($job -ne [IntPtr]::Zero) {
        [void][PsxVitestJob]::CloseHandle($job)
    }
}

exit $exitCode
