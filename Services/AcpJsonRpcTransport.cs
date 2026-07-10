using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PSX.Services;

public sealed class AcpJsonRpcTransport : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _nodePath;
    private readonly string _adapterPath;
    private readonly string _workingDirectory;
    private readonly string _logPath;
    private readonly Func<JsonElement, Task<object?>> _requestHandler;
    private readonly Func<JsonElement, Task> _notificationHandler;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _writeLock = new();
    private Process? _process;
    private int _nextId;
    private bool _disposed;

    public AcpJsonRpcTransport(
        string nodePath,
        string adapterPath,
        string workingDirectory,
        string logPath,
        Func<JsonElement, Task<object?>> requestHandler,
        Func<JsonElement, Task> notificationHandler)
    {
        _nodePath = nodePath;
        _adapterPath = adapterPath;
        _workingDirectory = workingDirectory;
        _logPath = logPath;
        _requestHandler = requestHandler;
        _notificationHandler = notificationHandler;
    }

    public bool IsRunning => _process is { HasExited: false };

    public void Start()
    {
        if (IsRunning)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = _nodePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(_adapterPath);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
            throw new InvalidOperationException("Failed to start ACP adapter.");

        _ = Task.Run(ReadStdoutAsync);
        _ = Task.Run(ReadStderrAsync);
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan? timeout = null)
    {
        if (!IsRunning)
            Start();

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await SendMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        }).ConfigureAwait(false);

        var wait = timeout.HasValue
            ? tcs.Task.WaitAsync(timeout.Value)
            : tcs.Task;

        return await wait.ConfigureAwait(false);
    }

    public Task SendNotificationAsync(string method, object? parameters)
    {
        return SendMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
    }

    private Task SendMessageAsync(object message)
    {
        var process = _process ?? throw new InvalidOperationException("ACP adapter is not running.");
        var json = JsonSerializer.Serialize(message, JsonOptions);
        Log(">> " + json);

        lock (_writeLock)
        {
            process.StandardInput.WriteLine(json);
            process.StandardInput.Flush();
        }

        return Task.CompletedTask;
    }

    private async Task ReadStdoutAsync()
    {
        var process = _process;
        if (process == null)
            return;

        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Log("<< " + line);
                await HandleIncomingLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            CompletePendingWithError(ex);
        }
    }

    private async Task ReadStderrAsync()
    {
        var process = _process;
        if (process == null)
            return;

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
            await HandleAgentRequestAsync(root).ConfigureAwait(false);
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
            await SendErrorAsync(id, ex.Message).ConfigureAwait(false);
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
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        _process?.Dispose();
        CompletePendingWithError(new ObjectDisposedException(nameof(AcpJsonRpcTransport)));
    }
}
