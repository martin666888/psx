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
        try
        {
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref securityAttributes, 0))
                return Failed($"CreatePipe(stdout) failed: {new Win32Exception().Message}");
            SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
            if (!CreatePipe(out stderrRead, out stderrWrite, ref securityAttributes, 0))
                return Failed($"CreatePipe(stderr) failed: {new Win32Exception().Message}");
            SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);

            var startupInfo = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                dwFlags = STARTF_USESTDHANDLES,
                hStdInput = IntPtr.Zero,
                hStdOutput = stdoutWrite,
                hStdError = stderrWrite
            };
            if (!CreateProcessW(
                    null,
                    new StringBuilder(commandLine),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    CREATE_SUSPENDED | CREATE_NO_WINDOW,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                return Failed($"CreateProcess failed: {new Win32Exception().Message}");
            }

            // The child is suspended: drop our copies of the inherited write
            // ends, then join the job before its first instruction runs.
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
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
            if (stdoutRead != IntPtr.Zero) CloseHandle(stdoutRead);
            if (stderrRead != IntPtr.Zero) CloseHandle(stderrRead);
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
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;

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
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
