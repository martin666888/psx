using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PSX.Helpers;
using PSX.Models;
using static PSX.Helpers.NativeMethods;

namespace PSX.Services;

public sealed class ConPtyService : IDisposable
{
    private readonly Dictionary<Guid, TerminalSession> _sessions = new();
    private readonly Dictionary<Guid, Task> _readTasks = new();
    private readonly HashSet<Guid> _closingSessions = new();
    private readonly object _sessionsLock = new();
    private readonly object _writeLock = new();
    private bool _disposed;

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
    public event EventHandler<SessionExitedEventArgs>? SessionExited;

    /// <summary>
    /// Creates a new terminal session with the given shell profile and initial size.
    /// </summary>
    public TerminalSession CreateSession(ShellProfile profile, TerminalSize size)
    {
        var session = ProcessFactory.Create(profile, size);
        Task readTask;

        lock (_sessionsLock)
        {
            _sessions[session.SessionId] = session;
            readTask = ReadOutputLoopAsync(session);
            _readTasks[session.SessionId] = readTask;
        }

        return session;
    }

    /// <summary>
    /// Writes raw bytes (user keystrokes) to the input pipe of the specified session.
    /// </summary>
    public void WriteInput(Guid sessionId, byte[] data)
    {
        TerminalSession? session;
        lock (_sessionsLock)
        {
            _sessions.TryGetValue(sessionId, out session);
        }

        if (session == null)
            return;

        if (session.InputPipeWrite?.IsClosed ?? true)
            return;

        lock (_writeLock)
        {
            WriteFile(
                session.InputPipeWrite.DangerousGetHandle(),
                data,
                (uint)data.Length,
                out _,
                IntPtr.Zero);
        }
    }

    /// <summary>
    /// Resizes the ConPTY for the specified session.
    /// </summary>
    public void Resize(Guid sessionId, int columns, int rows)
    {
        if (columns is < 2 or > short.MaxValue || rows is < 1 or > short.MaxValue)
        {
            Debug.WriteLine($"Ignored invalid ConPTY resize for {sessionId}: {columns}x{rows}");
            return;
        }

        TerminalSession? session;
        lock (_sessionsLock)
        {
            _sessions.TryGetValue(sessionId, out session);
            if (session == null)
                return;

            if (session.Size.Columns == columns && session.Size.Rows == rows)
            {
                Debug.WriteLine($"Ignored duplicate ConPTY resize for {sessionId}: {columns}x{rows}");
                return;
            }

            Debug.WriteLine(
                $"ConPTY resize {sessionId}: {session.Size.Columns}x{session.Size.Rows} -> {columns}x{rows}");
            session.Size.Columns = columns;
            session.Size.Rows = rows;
        }

        var coord = new COORD((short)columns, (short)rows);
        try
        {
            var hr = ResizePseudoConsole(session.ConPtyHandle, coord);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);
        }
        catch
        {
            // Resize failure is non-critical, ignore
        }
    }

    /// <summary>
    /// Closes and disposes the specified terminal session.
    /// </summary>
    public void CloseSession(Guid sessionId)
    {
        _ = CloseSessionAsync(sessionId);
    }

    public Task CloseSessionAsync(Guid sessionId)
    {
        TerminalSession? session;
        Task? readTask;

        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionId, out session))
                return Task.CompletedTask;

            if (!_closingSessions.Add(sessionId))
                return Task.CompletedTask;

            _sessions.Remove(sessionId);
            _readTasks.TryGetValue(sessionId, out readTask);
        }

        return Task.Run(() => CloseSessionCoreAsync(session, readTask));
    }

    public TerminalSession? GetSession(Guid sessionId)
    {
        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }
    }

    private async Task ReadOutputLoopAsync(TerminalSession session)
    {
        const int bufferSize = 4096;
        const int maxBatchSize = 64 * 1024;
        var flushInterval = TimeSpan.FromMilliseconds(8);
        var buffer = new byte[bufferSize];
        var batchBuffer = new ArrayBufferWriter<byte>(maxBatchSize);
        var batchTimer = Stopwatch.StartNew();
        var outputHandle = session.OutputPipeRead;
        var addRefSucceeded = false;

        if (outputHandle == null || outputHandle.IsInvalid)
            return;

        void AppendToBatch(uint bytesRead)
        {
            var count = (int)bytesRead;
            buffer.AsSpan(0, count).CopyTo(batchBuffer.GetSpan(count));
            batchBuffer.Advance(count);
        }

        void FlushBatch()
        {
            if (batchBuffer.WrittenCount == 0)
                return;

            var base64 = Convert.ToBase64String(batchBuffer.WrittenSpan);
            OutputReceived?.Invoke(this, new TerminalOutputEventArgs
            {
                SessionId = session.SessionId,
                Data = base64
            });

            batchBuffer.Clear();
            batchTimer.Restart();
        }

        try
        {
            outputHandle.DangerousAddRef(ref addRefSucceeded);
            var handle = outputHandle.DangerousGetHandle();
            if (handle == IntPtr.Zero)
                return;

            await Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    try
                    {
                        if (session.CancellationTokenSource.IsCancellationRequested)
                            break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    bool success;
                    uint bytesRead;
                    try
                    {
                        success = ReadFile(handle, buffer, bufferSize, out bytesRead, IntPtr.Zero);
                    }
                    catch
                    {
                        break;
                    }

                    if (!success || bytesRead == 0)
                        break;

                    AppendToBatch(bytesRead);

                    // Check if pipe has more data waiting
                    bool hasMoreData = PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out uint bytesAvail, IntPtr.Zero)
                                       && bytesAvail > 0;

                    if (!hasMoreData || batchBuffer.WrittenCount >= maxBatchSize || batchTimer.Elapsed >= flushInterval)
                    {
                        FlushBatch();
                    }
                }

                FlushBatch();
            }, session.CancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on session close
        }
        catch
        {
            // Read loop exited due to error
        }
        finally
        {
            FlushBatch();

            if (addRefSucceeded)
            {
                try { outputHandle.DangerousRelease(); }
                catch { }
            }

            bool notifyExit;
            lock (_sessionsLock)
            {
                notifyExit = !_closingSessions.Contains(session.SessionId);
                if (notifyExit)
                {
                    _sessions.Remove(session.SessionId);
                    _readTasks.Remove(session.SessionId);
                }
            }

            if (notifyExit)
            {
                SessionExited?.Invoke(this, new SessionExitedEventArgs
                {
                    SessionId = session.SessionId
                });

                _ = Task.Run(() => DisposeSessionResources(session, killProcess: false, disposeOutputPipe: true));
            }
        }
    }

    private async Task CloseSessionCoreAsync(TerminalSession session, Task? readTask)
    {
        try
        {
            try { session.CancellationTokenSource.Cancel(); }
            catch { }

            DisposeSessionResources(session, killProcess: true, disposeOutputPipe: false);

            if (readTask != null)
            {
                try { await readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { }
            }

            DisposeSessionResources(session, killProcess: false, disposeOutputPipe: true);
        }
        finally
        {
            try { session.CancellationTokenSource.Dispose(); }
            catch { }

            lock (_sessionsLock)
            {
                _readTasks.Remove(session.SessionId);
                _closingSessions.Remove(session.SessionId);
            }
        }
    }

    private static void DisposeSessionResources(
        TerminalSession session,
        bool killProcess,
        bool disposeOutputPipe)
    {
        try { session.InputPipeWrite?.Dispose(); }
        catch { }

        if (killProcess && session.Process is not null)
        {
            try
            {
                if (!session.Process.HasExited)
                    session.Process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        if (session.ConPtyHandle != IntPtr.Zero)
        {
            try { Helpers.NativeMethods.ClosePseudoConsole(session.ConPtyHandle); }
            catch { }
            session.ConPtyHandle = IntPtr.Zero;
        }

        if (disposeOutputPipe)
        {
            try { session.OutputPipeRead?.Dispose(); }
            catch { }

            try { session.Process?.Dispose(); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Guid[] sessionIds;
        lock (_sessionsLock)
        {
            sessionIds = _sessions.Keys.ToArray();
        }

        foreach (var sessionId in sessionIds)
        {
            _ = CloseSessionAsync(sessionId);
        }
    }
}

public sealed class TerminalOutputEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
    public string Data { get; set; } = "";
}

public sealed class SessionExitedEventArgs : EventArgs
{
    public Guid SessionId { get; set; }
}
