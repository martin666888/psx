using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

public sealed class AcpJsonRpcTransport : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AcpProcessSpec _processSpec;
    private readonly string _logPath;
    private readonly Func<JsonElement, Task<object?>> _requestHandler;
    private readonly Func<JsonElement, Task> _notificationHandler;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _lifecycleLock = new();
    private readonly object _writeLock = new();
    private Process? _process;
    private int _nextId;
    private volatile bool _started;
    private volatile bool _disconnected;
    private volatile bool _disposed;

    public AcpJsonRpcTransport(
        AcpProcessSpec processSpec,
        string logPath,
        Func<JsonElement, Task<object?>> requestHandler,
        Func<JsonElement, Task> notificationHandler)
    {
        _processSpec = processSpec;
        _logPath = logPath;
        _requestHandler = requestHandler;
        _notificationHandler = notificationHandler;
    }

    public bool IsRunning
    {
        get
        {
            if (_disposed || _disconnected)
                return false;

            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (IsRunning)
                return;
            if (_started || _disconnected)
                throw new InvalidOperationException("ACP adapter transport is disconnected and cannot be restarted.");

            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

            var startInfo = new ProcessStartInfo
            {
                FileName = _processSpec.FileName,
                WorkingDirectory = _processSpec.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in _processSpec.Arguments)
                startInfo.ArgumentList.Add(argument);
            foreach (var variable in _processSpec.Environment)
            {
                if (variable.Value == null)
                    startInfo.Environment.Remove(variable.Key);
                else
                    startInfo.Environment[variable.Key] = variable.Value;
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Failed to start ACP adapter.");
            }

            _process = process;
            _started = true;
            process.Exited += (_, _) => SignalDisconnected(
                new EndOfStreamException($"ACP adapter exited with code {TryGetExitCode(process)}."));
            _ = Task.Run(() => ReadStdoutAsync(process));
            _ = Task.Run(() => ReadStderrAsync(process));
        }
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (!IsRunning)
            Start();

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await SendMessageAsync(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            }).ConfigureAwait(false);

            return timeout.HasValue
                ? await tcs.Task.WaitAsync(timeout.Value, cancellationToken).ConfigureAwait(false)
                : await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters)
    {
        ThrowIfUnavailable();
        return SendMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
    }

    private Task SendMessageAsync(object message)
    {
        ThrowIfUnavailable();
        var json = JsonSerializer.Serialize(message, JsonOptions);
        Log(">> " + json);

        lock (_writeLock)
        {
            ThrowIfUnavailable();
            var process = _process;
            if (process == null || !IsRunning)
                throw new InvalidOperationException("ACP adapter is not running.");

            try
            {
                process.StandardInput.WriteLine(json);
                process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                SignalDisconnected(ex);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Log("<< " + line);
                await HandleIncomingLineAsync(line).ConfigureAwait(false);
            }

            SignalDisconnected(new EndOfStreamException("ACP adapter stdout closed."));
        }
        catch (Exception ex)
        {
            SignalDisconnected(ex);
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Log("!! " + line);
            }
        }
        catch { }
    }

    private async Task HandleIncomingLineAsync(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement.Clone();

        if (root.TryGetProperty("id", out var idElement)
            && !root.TryGetProperty("method", out _))
        {
            var id = idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt32()
                : int.TryParse(idElement.GetString(), out var parsed) ? parsed : -1;

            if (id >= 0 && _pending.TryRemove(id, out var pending))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    pending.TrySetException(new InvalidOperationException(FormatError(error)));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    pending.TrySetResult(result.Clone());
                }
                else
                {
                    pending.TrySetResult(default);
                }
            }

            return;
        }

        if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out _))
        {
            // Agent requests may intentionally wait for user input or a
            // background terminal. Do not block the stdout reader while one
            // request is pending; JSON-RPC responses are correlated by id.
            _ = HandleAgentRequestAsync(root);
            return;
        }

        if (root.TryGetProperty("method", out _))
        {
            await _notificationHandler(root).ConfigureAwait(false);
        }
    }

    private async Task HandleAgentRequestAsync(JsonElement request)
    {
        var id = request.GetProperty("id").Clone();
        try
        {
            var result = await _requestHandler(request).ConfigureAwait(false);
            await SendResponseAsync(id, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await SendErrorAsync(id, ex.Message).ConfigureAwait(false);
            }
            catch (Exception sendException)
            {
                SignalDisconnected(sendException);
            }
        }
    }

    private Task SendResponseAsync(JsonElement id, object? result)
    {
        return SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            result = result ?? new { }
        });
    }

    private Task SendErrorAsync(JsonElement id, string message)
    {
        return SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code = -32603,
                message
            }
        });
    }

    private static string FormatError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? error.GetRawText();
        }

        return error.GetRawText();
    }

    private void CompletePendingWithError(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
                pending.TrySetException(exception);
        }
    }

    private void SignalDisconnected(Exception exception)
    {
        if (_disposed || _disconnected)
            return;

        _disconnected = true;
        Log("!! transport disconnected: " + exception.Message);
        CompletePendingWithError(exception);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (_disconnected)
            throw new InvalidOperationException("ACP adapter transport is disconnected.");
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private void Log(string line)
    {
        try
        {
            File.AppendAllText(_logPath, DateTimeOffset.Now.ToString("u") + " " + line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _disconnected = true;
            CompletePendingWithError(new ObjectDisposedException(nameof(AcpJsonRpcTransport)));
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch { }

            _process?.Dispose();
            _process = null;
        }
    }
}
