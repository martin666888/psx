using System.Collections.Concurrent;
using System.Text.Json;
using PSX.Models;

namespace PSX.Services;

public sealed class AgentThreadPersistenceCoordinator : IDisposable
{
    internal static readonly TimeSpan CheckpointDelay = TimeSpan.FromMilliseconds(250);

    private sealed class ThreadState
    {
        public object Sync { get; } = new();
        public object IoSync { get; } = new();
        public long Generation { get; set; }
        public bool Deleted { get; set; }
        public AgentThread? Pending { get; set; }
        public Task? Worker { get; set; }
    }

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAgentThreadStore _store;
    private readonly ConcurrentDictionary<string, ThreadState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AgentThreadPersistenceCoordinator(IAgentThreadStore store)
    {
        _store = store;
    }

    public void SaveCritical(AgentThread thread)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = Clone(thread);
        var state = StateFor(snapshot.ThreadId);
        long generation;
        lock (state.Sync)
        {
            if (state.Deleted)
                return;
            generation = ++state.Generation;
            state.Pending = null;
        }

        lock (state.IoSync)
        {
            lock (state.Sync)
            {
                if (state.Deleted || state.Generation != generation)
                    return;
            }
            _store.SaveThread(snapshot);
        }
    }

    public void QueueCheckpoint(AgentThread thread)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = Clone(thread);
        var state = StateFor(snapshot.ThreadId);
        lock (state.Sync)
        {
            if (state.Deleted)
                return;
            state.Pending = snapshot;
            if (state.Worker is { IsCompleted: false })
                return;
            state.Worker = RunCheckpointAsync(state, _lifetime.Token);
        }
    }

    public Task FlushThreadAsync(string threadId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_states.TryGetValue(threadId, out var state))
            Flush(state);
        return Task.CompletedTask;
    }

    public void DeleteThread(string threadId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = StateFor(threadId);
        lock (state.Sync)
        {
            state.Generation++;
            state.Deleted = true;
            state.Pending = null;
        }
        lock (state.IoSync)
            _store.DeleteThread(threadId);
    }

    public AgentAttachment SaveAttachment(
        string threadId,
        string fileName,
        string mimeType,
        byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = StateFor(threadId);
        lock (state.IoSync)
        {
            lock (state.Sync)
            {
                if (state.Deleted)
                    throw new InvalidOperationException("The Agent thread has already been deleted.");
            }
            return _store.SaveAttachment(threadId, fileName, mimeType, data);
        }
    }

    private async Task RunCheckpointAsync(ThreadState state, CancellationToken token)
    {
        try
        {
            await Task.Delay(CheckpointDelay, token).ConfigureAwait(false);
            Flush(state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void Flush(ThreadState state)
    {
        AgentThread? snapshot;
        long generation;
        lock (state.Sync)
        {
            snapshot = state.Pending;
            state.Pending = null;
            state.Worker = null;
            generation = state.Generation;
            if (state.Deleted || snapshot == null)
                return;
        }

        lock (state.IoSync)
        {
            lock (state.Sync)
            {
                if (state.Deleted || state.Generation != generation)
                    return;
            }
            _store.SaveThread(snapshot);
        }
    }

    private ThreadState StateFor(string threadId) =>
        _states.GetOrAdd(threadId, static _ => new ThreadState());

    private static AgentThread Clone(AgentThread thread)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(thread, SnapshotOptions);
        return JsonSerializer.Deserialize<AgentThread>(bytes, SnapshotOptions)
            ?? throw new InvalidOperationException("Unable to snapshot Agent thread state.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var state in _states.Values)
            Flush(state);
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
