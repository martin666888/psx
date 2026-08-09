using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PSX.Services;

/// <summary>
/// Owns reverse ACP terminal processes and their bounded output buffers. Each
/// process is tied to the transport generation that created it so a transport
/// reset can deterministically tear down only its own terminals.
/// </summary>
internal sealed class AcpTerminalRequestHandler : IDisposable
{
    private sealed class TerminalProcess
    {
        public Process Process { get; init; } = null!;
        public StringBuilder Output { get; } = new();
        public int OutputByteCount { get; set; }
        public int OutputByteLimit { get; init; } = 200_000;
        public bool Truncated { get; set; }
        public long TransportGeneration { get; init; }
        public CancellationToken TransportToken { get; init; }
    }

    private readonly Func<string> _workingDirectory;
    private readonly Func<long> _transportGeneration;
    private readonly Func<CancellationToken> _transportToken;
    private readonly ConcurrentDictionary<string, TerminalProcess> _terminals = new();
    private bool _disposed;

    public AcpTerminalRequestHandler(
        Func<string> workingDirectory,
        Func<long> transportGeneration,
        Func<CancellationToken> transportToken)
    {
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _transportGeneration = transportGeneration ?? throw new ArgumentNullException(nameof(transportGeneration));
        _transportToken = transportToken ?? throw new ArgumentNullException(nameof(transportToken));
    }

    public async Task<object?> HandleAsync(string method, JsonElement parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return method switch
        {
            "terminal/create" => HandleCreate(parameters),
            "terminal/output" => HandleOutput(parameters),
            "terminal/wait_for_exit" => await HandleWaitForExitAsync(parameters).ConfigureAwait(false),
            "terminal/kill" => HandleKill(parameters),
            "terminal/release" => HandleRelease(parameters),
            _ => new { }
        };
    }

    public void CleanupGeneration(long generation)
    {
        foreach (var item in _terminals.ToArray())
        {
            if (item.Value.TransportGeneration != generation)
                continue;
            if (_terminals.TryRemove(item.Key, out var terminal))
                StopAndDispose(terminal);
        }
    }

    private object HandleCreate(JsonElement parameters)
    {
        var command = GetString(parameters, "command");
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("ACP terminal/create did not include a command.");

        var args = parameters.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
            ? argsElement.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
            : Array.Empty<string>();
        var cwd = GetString(parameters, "cwd");
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = _workingDirectory();
        cwd = EnsureAllowedDirectory(cwd);

        var terminalId = Guid.NewGuid().ToString();
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (parameters.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in envElement.EnumerateArray())
            {
                var name = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    startInfo.Environment[name] = GetString(item, "value");
            }
        }

        const int MaxOutputByteLimit = 2_000_000;
        var rawLimit = TryGetInt(parameters, "outputByteLimit") ?? 200_000;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start ACP terminal command: {command}");

        var terminal = new TerminalProcess
        {
            Process = process,
            OutputByteLimit = Math.Clamp(rawLimit, 0, MaxOutputByteLimit),
            TransportGeneration = _transportGeneration(),
            TransportToken = _transportToken()
        };
        _terminals[terminalId] = terminal;
        _ = Task.Run(() => ReadStreamAsync(terminalId, process.StandardOutput));
        _ = Task.Run(() => ReadStreamAsync(terminalId, process.StandardError));
        return new { terminalId };
    }

    private object HandleOutput(JsonElement parameters)
    {
        var terminal = GetTerminal(GetString(parameters, "terminalId"));
        lock (terminal.Output)
        {
            return new
            {
                output = terminal.Output.ToString(),
                truncated = terminal.Truncated,
                exitStatus = terminal.Process.HasExited
                    ? new { exitCode = terminal.Process.ExitCode, signal = (string?)null }
                    : null
            };
        }
    }

    private async Task<object> HandleWaitForExitAsync(JsonElement parameters)
    {
        var terminal = GetTerminal(GetString(parameters, "terminalId"));
        await terminal.Process.WaitForExitAsync(terminal.TransportToken).ConfigureAwait(false);
        return new { exitCode = terminal.Process.ExitCode, signal = (string?)null };
    }

    private object HandleKill(JsonElement parameters)
    {
        var terminal = GetTerminal(GetString(parameters, "terminalId"));
        try
        {
            if (!terminal.Process.HasExited)
                terminal.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        return new { };
    }

    private object HandleRelease(JsonElement parameters)
    {
        var terminalId = GetString(parameters, "terminalId");
        if (_terminals.TryRemove(terminalId, out var terminal))
            terminal.Process.Dispose();
        return new { };
    }

    private async Task ReadStreamAsync(string terminalId, StreamReader reader)
    {
        try
        {
            var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count <= 0 || !_terminals.TryGetValue(terminalId, out var terminal))
                    break;

                lock (terminal.Output)
                {
                    terminal.OutputByteCount = AppendBoundedUtf8(
                        terminal.Output,
                        terminal.OutputByteCount,
                        buffer.AsSpan(0, count),
                        terminal.OutputByteLimit,
                        out var truncated);
                    terminal.Truncated |= truncated;
                }
            }
        }
        catch (Exception) when (!_terminals.ContainsKey(terminalId) || _disposed)
        {
            // Reset/release/dispose owns the stream closure.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ACP terminal stream failed for {terminalId}: {ex}");
        }
    }

    internal static int AppendBoundedUtf8(
        StringBuilder output,
        int currentByteCount,
        ReadOnlySpan<char> text,
        int byteLimit,
        out bool truncated)
    {
        var joinsSplitSurrogate = output.Length > 0
            && text.Length > 0
            && char.IsHighSurrogate(output[^1])
            && char.IsLowSurrogate(text[0]);
        var appendedBytes = Encoding.UTF8.GetByteCount(text);
        if (joinsSplitSurrogate)
            appendedBytes -= 2;

        output.Append(text);
        var byteCount = currentByteCount + appendedBytes;
        var excess = byteCount - Math.Max(0, byteLimit);
        if (excess <= 0)
        {
            truncated = false;
            return byteCount;
        }

        var removeChars = 0;
        var removedBytes = 0;
        while (removedBytes < excess && removeChars < output.Length)
        {
            var current = output[removeChars];
            if (char.IsHighSurrogate(current)
                && removeChars + 1 < output.Length
                && char.IsLowSurrogate(output[removeChars + 1]))
            {
                removeChars += 2;
                removedBytes += 4;
            }
            else
            {
                removeChars++;
                removedBytes += current <= 0x7f ? 1 : current <= 0x7ff ? 2 : 3;
            }
        }

        if (removeChars > 0)
            output.Remove(0, removeChars);
        truncated = removeChars > 0;
        return byteCount - removedBytes;
    }

    private TerminalProcess GetTerminal(string terminalId)
    {
        if (!_terminals.TryGetValue(terminalId, out var terminal))
            throw new InvalidOperationException($"ACP terminal not found: {terminalId}");
        return terminal;
    }

    private string EnsureAllowedDirectory(string path)
    {
        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"Directory does not exist: {directory}");

        var root = Path.GetFullPath(_workingDirectory());
        if (!IsWithin(directory, root))
            throw new InvalidOperationException($"Path is outside the current workspace: {directory}");
        return directory;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? TryGetInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.TryGetInt32(out var result)
            ? result
            : null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var item in _terminals.ToArray())
        {
            if (_terminals.TryRemove(item.Key, out var terminal))
                StopAndDispose(terminal);
        }
    }

    private static void StopAndDispose(TerminalProcess terminal)
    {
        try
        {
            if (!terminal.Process.HasExited)
                terminal.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        terminal.Process.Dispose();
    }
}
