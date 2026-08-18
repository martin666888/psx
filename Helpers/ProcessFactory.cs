using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PSX.Models;
using static PSX.Helpers.NativeMethods;

namespace PSX.Helpers;

/// <summary>
/// Creates ConPTY processes attached to shell executables.
/// </summary>
internal static class ProcessFactory
{
    /// <summary>
    /// Creates pipes, pseudo console, and starts the shell process.
    /// Returns the session with all handles populated.
    /// </summary>
    public static TerminalSession Create(ShellProfile profile, TerminalSize size)
    {
        // Create input pipe (terminal → shell)
        var inputSa = SECURITY_ATTRIBUTES.Create(true);
        CreatePipe(out var inputPipeRead, out var inputPipeWrite, ref inputSa, 0).ThrowIfFailed("Failed to create input pipe");
        // Ensure the write end (our side) is NOT inheritable
        SetHandleInformation(inputPipeWrite, HANDLE_FLAG_INHERIT, 0).ThrowIfFailed("Failed to set input pipe handle info");

        // Create output pipe (shell → terminal)
        var outputSa = SECURITY_ATTRIBUTES.Create(true);
        CreatePipe(out var outputPipeRead, out var outputPipeWrite, ref outputSa, 0).ThrowIfFailed("Failed to create output pipe");
        // Ensure the read end (our side) is NOT inheritable
        SetHandleInformation(outputPipeRead, HANDLE_FLAG_INHERIT, 0).ThrowIfFailed("Failed to set output pipe handle info");

        // Create pseudo console
        var coord = new COORD((short)size.Columns, (short)size.Rows);
        var conPty = PseudoConsole.Create(coord, inputPipeRead, outputPipeWrite);

        // Create process
        var startupInfo = BuildStartupInfo(conPty.Handle);
        var commandLine = string.IsNullOrEmpty(profile.Arguments)
            ? profile.Command
            : $"\"{profile.Command}\" {profile.Arguments}";
        var environmentBlock = BuildEnvironmentBlock(profile);

        bool success;
        PROCESS_INFORMATION processInfo;
        try
        {
            success = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false, // ConPTY already owns duplicated pipe handles; do not inherit the host's stdio.
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                environmentBlock,
                profile.StartingDirectory,
                ref startupInfo,
                out processInfo);
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }

        if (!success)
        {
            var error = Marshal.GetLastWin32Error();
            conPty.Dispose();
            CloseHandle(inputPipeRead);
            CloseHandle(inputPipeWrite);
            CloseHandle(outputPipeRead);
            CloseHandle(outputPipeWrite);
            throw new Win32Exception(error, "Failed to create process");
        }

        // Close handles we don't need: Process.GetProcessById opens its own handle
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);

        // Build session
        var session = new TerminalSession(profile, size)
        {
            ConPtyHandle = conPty.Handle,
            InputPipeWrite = new SafeFileHandle(inputPipeWrite, ownsHandle: true),
            OutputPipeRead = new SafeFileHandle(outputPipeRead, ownsHandle: true),
            Process = Process.GetProcessById((int)processInfo.dwProcessId)
        };

        // Close the ConPTY-side pipe handles — ConPTY has duplicated them
        CloseHandle(inputPipeRead);
        CloseHandle(outputPipeWrite);

        // Free the attribute list memory
        if (startupInfo.lpAttributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
        }

        return session;
    }

    private static IntPtr BuildEnvironmentBlock(ShellProfile profile)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                variables[key] = value;
            }
        }

        if (profile.Id.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            variables["COLORTERM"] = "truecolor";
            variables["TERM_PROGRAM"] = "PSX";
            variables["TERM_PROGRAM_VERSION"] =
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
        }

        variables["TERM"] = "xterm-256color";

        var builder = new StringBuilder();
        foreach (var variable in variables)
        {
            builder.Append(variable.Key);
            builder.Append('=');
            builder.Append(variable.Value);
            builder.Append('\0');
        }
        builder.Append('\0');

        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static STARTUPINFOEXW BuildStartupInfo(IntPtr conPtyHandle)
    {
        // Determine the size needed for the attribute list
        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(
            IntPtr.Zero, 1, 0, ref attributeListSize);

        // Allocate the attribute list
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        if (attributeList == IntPtr.Zero)
            throw new OutOfMemoryException("Failed to allocate attribute list");

        // Initialize the attribute list
        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize attribute list");

        // Set the pseudo console attribute
        var conPtyHandleAsIntPtr = conPtyHandle;
        if (!UpdateProcThreadAttribute(
            attributeList,
            0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            conPtyHandleAsIntPtr,
            IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update proc thread attribute");
        }

        var startupInfo = new STARTUPINFOEXW
        {
            StartupInfo = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOEXW>()
            },
            lpAttributeList = attributeList
        };

        return startupInfo;
    }

    private static void ThrowIfFailed(this bool result, string message)
    {
        if (!result)
            throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }
}
