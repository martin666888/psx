using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PSX.Services;

/// <summary>
/// Starts a redirected child process <c>CREATE_SUSPENDED</c>, assigns it to a
/// <c>KILL_ON_JOB_CLOSE</c> Job Object, and only then resumes it. The process
/// can never spawn a child outside the job, and a failed assignment kills the
/// still-suspended process instead of leaking it. Every failure path closes
/// all pipe/process handles and returns a diagnostic detail that must stay in
/// internal logs — it may contain absolute paths.
/// </summary>
internal static class SuspendedJobProcessLauncher
{
    internal sealed record LaunchResult(
        bool Succeeded,
        Process? Process,
        JobObjectHandle? Job,
        StreamReader? Output,
        StreamReader? Error,
        string? FailureDetail);

    public static LaunchResult TryStartInJob(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? log)
    {
        var commandLine = BuildCommandLine(fileName, arguments);
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        IntPtr stdinRead = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        var pinnedHandles = GCHandle.Alloc(Array.Empty<IntPtr>(), GCHandleType.Pinned);
        try
        {
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref securityAttributes, 0))
                return Failed($"CreatePipe(stdout) failed: {new Win32Exception().Message}");
            if (!SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0))
                return Failed($"SetHandleInformation(stdout) failed: {new Win32Exception().Message}");
            if (!CreatePipe(out stderrRead, out stderrWrite, ref securityAttributes, 0))
                return Failed($"CreatePipe(stderr) failed: {new Win32Exception().Message}");
            if (!SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0))
                return Failed($"SetHandleInformation(stderr) failed: {new Win32Exception().Message}");

            // STARTF_USESTDHANDLES requires all three standard handles to be
            // valid and inheritable. DSH web is non-interactive, so give it an
            // inheritable NUL input handle instead of passing NULL (which
            // CreateProcess copies unchecked and can make the child misbehave).
            stdinRead = CreateFileW(
                "NUL",
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                ref securityAttributes,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
            if (stdinRead == INVALID_HANDLE_VALUE)
            {
                stdinRead = IntPtr.Zero;
                return Failed($"CreateFile(NUL) failed: {new Win32Exception().Message}");
            }

            // Restrict handle inheritance to exactly NUL stdin and the two
            // pipe write ends:
            // without PROC_THREAD_ATTRIBUTE_HANDLE_LIST the child would inherit
            // every inheritable handle in the PSX process. The pinned array and
            // the attribute list must stay alive until CreateProcessW returns.
            var inheritableHandles = new[] { stdinRead, stdoutWrite, stderrWrite };
            pinnedHandles.Free();
            pinnedHandles = GCHandle.Alloc(inheritableHandles, GCHandleType.Pinned);
            var attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            if (attributeListSize == IntPtr.Zero)
                return Failed($"InitializeProcThreadAttributeList size query failed: {new Win32Exception().Message}");
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                return Failed($"InitializeProcThreadAttributeList failed: {new Win32Exception().Message}");
            attributeListInitialized = true;
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    IntPtr.Zero,
                    ProcThreadAttributeHandleList,
                    pinnedHandles.AddrOfPinnedObject(),
                    (IntPtr)(IntPtr.Size * inheritableHandles.Length),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                return Failed($"UpdateProcThreadAttribute failed: {new Win32Exception().Message}");
            }

            var startupInfo = new STARTUPINFOEXW();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startupInfo.StartupInfo.hStdInput = stdinRead;
            startupInfo.StartupInfo.hStdOutput = stdoutWrite;
            startupInfo.StartupInfo.hStdError = stderrWrite;
            startupInfo.lpAttributeList = attributeList;
            if (!CreateProcessW(
                    null,
                    new StringBuilder(commandLine),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                return Failed($"CreateProcess failed: {new Win32Exception().Message}");
            }

            // The child is suspended: drop our copies of the inherited write
            // ends, then join the job before its first instruction runs.
            CloseHandle(stdinRead);
            stdinRead = IntPtr.Zero;
            CloseHandle(stdoutWrite);
            stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite);
            stderrWrite = IntPtr.Zero;

            var job = JobObjectHandle.TryCreateWithKillOnClose(log);
            var assigned = false;
            try
            {
                assigned = job != null && job.AssignProcess(processInformation.hProcess, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Job Object assignment threw: {ex.Message}");
            }
            if (!assigned || job == null)
            {
                job?.Dispose();
                TerminateProcess(processInformation.hProcess, 1);
                CloseHandle(processInformation.hProcess);
                CloseHandle(processInformation.hThread);
                return Failed("Job Object assignment failed for the suspended process.");
            }

            if (ResumeThread(processInformation.hThread) == 0xFFFFFFFF)
            {
                job.Dispose();
                TerminateProcess(processInformation.hProcess, 1);
                CloseHandle(processInformation.hProcess);
                CloseHandle(processInformation.hThread);
                return Failed("ResumeThread failed for the suspended process.");
            }
            CloseHandle(processInformation.hProcess);
            CloseHandle(processInformation.hThread);

            Process process;
            try
            {
                process = Process.GetProcessById(processInformation.dwProcessId);
            }
            catch (Exception ex)
            {
                job.Dispose();
                return Failed($"Process.GetProcessById failed: {ex.Message}");
            }

            try
            {
                var output = CreateReader(stdoutRead);
                stdoutRead = IntPtr.Zero;
                var error = CreateReader(stderrRead);
                stderrRead = IntPtr.Zero;
                return new LaunchResult(true, process, job, output, error, null);
            }
            catch (Exception ex)
            {
                job.Dispose();
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                return Failed($"Stream setup failed: {ex.Message}");
            }
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                if (attributeListInitialized)
                    DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (pinnedHandles.IsAllocated)
                pinnedHandles.Free();
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
            if (stdoutRead != IntPtr.Zero) CloseHandle(stdoutRead);
            if (stderrRead != IntPtr.Zero) CloseHandle(stderrRead);
            if (stdinRead != IntPtr.Zero) CloseHandle(stdinRead);
        }
    }

    private static StreamReader CreateReader(IntPtr readHandle) =>
        new(new FileStream(
            new SafeFileHandle(readHandle, ownsHandle: true),
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: false),
            Encoding.UTF8);

    private static LaunchResult Failed(string detail) =>
        new(false, null, null, null, null, detail);

    /// <summary>
    /// Windows command-line quoting (CreateProcess parses one flat string):
    /// arguments without whitespace or quotes pass through verbatim; otherwise
    /// the argument is quoted and backslash runs before a quote (and the
    /// closing quote) are doubled per the CRT parsing rules.
    /// </summary>
    internal static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteArgument(fileName));
        foreach (var argument in arguments)
            builder.Append(' ').Append(QuoteArgument(argument));
        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');
        var pendingBackslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', pendingBackslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                if (pendingBackslashes > 0)
                    builder.Append('\\', pendingBackslashes);
                builder.Append(c);
            }

            pendingBackslashes = 0;
        }

        builder.Append('\\', pendingBackslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        IntPtr lpFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
