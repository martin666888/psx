using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PSX.Models;

namespace PSX.Services;

public sealed class AcpJsonRpcTransport : IDisposable
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // After the process exits, give the stdout reader a brief, bounded window to
    // drain any session/update lines the agent wrote just before exiting so tail
    // output is not lost before we fault pending requests.
    private static readonly TimeSpan ProcessExitDrainGrace = TimeSpan.FromMilliseconds(500);

    private readonly AcpProcessSpec _processSpec;
    private readonly string _logPath;
    private readonly Func<JsonElement, Task<object?>> _requestHandler;
    private readonly Func<JsonElement, Task> _notificationHandler;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _lifecycleLock = new();
    // Single-consumer outbound queue. Caller threads (including the WPF UI thread)
    // only serialize + enqueue; the background writer pump owns all synchronous
    // StandardInput.WriteLine/Flush so a stalled child pipe can never block a
    // caller. FIFO ordering guarantees JSON-RPC message order without a write lock.
    private readonly Channel<WriteItem> _writeChannel =
        Channel.CreateUnbounded<WriteItem>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _writerTask;
    private Process? _process;
    private int _nextId;
    private volatile bool _started;
    private volatile bool _disconnected;
    private volatile bool _disposed;

    private sealed record WriteItem(string Json, TaskCompletionSource Completion);

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
                StandardInputEncoding = Utf8WithoutBom,
                StandardOutputEncoding = Utf8WithoutBom,
                StandardErrorEncoding = Utf8WithoutBom
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
            // Start the outbound writer pump: the only place that touches
            // StandardInput. Caller threads never block on a stalled pipe.
            _writerTask = Task.Run(() => WriterPumpAsync(process));
            // Start the stdout reader before wiring Process.Exited so the exit
            // handler can wait on it: on exit we let the reader drain buffered
            // stdout (its own EOF path signals the disconnect) and only force the
            // disconnect if that drain does not finish within a bounded grace.
            var stdoutTask = Task.Run(() => ReadStdoutAsync(process));
            process.Exited += (_, _) => OnProcessExited(process, stdoutTask);
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
        Log("queued >> " + json);

        var item = new WriteItem(
            json,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        // Enqueue only. The caller thread must never perform synchronous stdin
        // I/O, so a stuck child pipe cannot block it. The returned task completes
        // when the writer pump has actually flushed the message (or faults if the
        // transport is gone), letting callers apply their own write budget.
        if (!_writeChannel.Writer.TryWrite(item))
        {
            var ex = new InvalidOperationException("ACP adapter transport is disconnected.");
            SignalDisconnected(ex);
            item.Completion.TrySetException(ex);
        }

        return item.Completion.Task;
    }

    private async Task WriterPumpAsync(Process process)
    {
        try
        {
            while (await _writeChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_writeChannel.Reader.TryRead(out var item))
                {
                    try
                    {
                        process.StandardInput.WriteLine(item.Json);
                        process.StandardInput.Flush();
                        Log("sent >> " + item.Json);
                        item.Completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        Log("write_failed >> " + item.Json);
                        item.Completion.TrySetException(ex);
                        SignalDisconnected(ex);
                        FailRemainingWrites(ex);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SignalDisconnected(ex);
            FailRemainingWrites(ex);
        }
    }

    private void FailRemainingWrites(Exception exception)
    {
        _writeChannel.Writer.TryComplete(exception);
        while (_writeChannel.Reader.TryRead(out var item))
            item.Completion.TrySetException(exception);
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
                    // Preserve the JSON-RPC code (e.g. -32000 authRequired) so
                    // the session layer can branch on it instead of treating
                    // every failure as an opaque InvalidOperationException.
                    pending.TrySetException(AcpJsonRpcException.FromErrorElement(error));
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
        // Fault anything still queued for the writer so callers awaiting a write
        // (e.g. session/cancel with a short budget) never hang on a dead pipe.
        FailRemainingWrites(exception);
    }

    private void OnProcessExited(Process process, Task stdoutTask)
    {
        // The process is gone, but the agent may have written a final burst of
        // session/update lines just before exiting. Let the stdout reader drain
        // them first (it calls SignalDisconnected at EOF); only if that drain
        // stalls past the bounded grace do we fault pending requests here, so a
        // stuck pipe cannot hang the session forever. SignalDisconnected is
        // idempotent, so whichever path wins is authoritative.
        _ = Task.Run(async () =>
        {
            try
            {
                await stdoutTask.WaitAsync(ProcessExitDrainGrace).ConfigureAwait(false);
            }
            catch
            {
                // Drain timed out or faulted; fall through to force the disconnect.
            }

            SignalDisconnected(new EndOfStreamException(
                $"ACP adapter exited with code {TryGetExitCode(process)}."));
        });
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
        Process? process;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _disconnected = true;
            CompletePendingWithError(new ObjectDisposedException(nameof(AcpJsonRpcTransport)));
            // Fault queued writes and stop the pump so it cannot block on the pipe.
            FailRemainingWrites(new ObjectDisposedException(nameof(AcpJsonRpcTransport)));

            process = _process;
            _process = null;
        }

        // Kill, close stdin, and reap the child entirely off the calling thread
        // so a wedged process (one that stopped reading stdin, leaving a full pipe)
        // can never block the UI/shutdown thread. Dispose returns immediately.
        if (process != null)
        {
            _ = Task.Run(() =>
            {
                // Kill first: when the child stopped reading stdin the pipe buffer
                // is full, so a writer (or StandardInput.Close, which flushes) would
                // block on WriteFile. Killing the child tears down the pipe and
                // unblocks that write; only then is closing stdin safe. All of this
                // runs off the calling thread so Dispose itself never blocks even if
                // the child is completely wedged.
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                try { process.StandardInput?.Close(); } catch { }
                try { process.WaitForExit(milliseconds: 5000); }
                catch { }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            });
        }
    }
}
