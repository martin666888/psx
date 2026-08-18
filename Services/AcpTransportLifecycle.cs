namespace PSX.Services;

/// <summary>
/// Owns the mutable lifetime of one ACP transport: gate, generation, linked
/// cancellation source, and the session id required after a forced reset.
/// Protocol initialization and session restore remain orchestration concerns.
/// </summary>
internal sealed class AcpTransportLifecycle : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _lifetimeCts;
    private bool _disposed;

    public AcpJsonRpcTransport? Current { get; private set; }
    public long Generation { get; private set; }
    public string? RecoverySessionId { get; private set; }
    public bool RecoveryRequired { get; private set; }
    public CancellationToken? LifetimeToken => _lifetimeCts?.Token;

    public Task EnterAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);

    public bool TryEnter(TimeSpan timeout) => _gate.Wait(timeout);

    public async Task<bool> TryEnterAsync(TimeSpan timeout)
        => await _gate.WaitAsync(timeout).ConfigureAwait(false);

    public void Exit() => _gate.Release();

    public long ReserveGeneration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ++Generation;
    }

    public void Attach(
        long generation,
        AcpJsonRpcTransport transport,
        CancellationTokenSource lifetimeCts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(lifetimeCts);
        if (generation != Generation)
            throw new InvalidOperationException("Cannot attach an ACP transport to a stale generation.");
        if (Current != null || _lifetimeCts != null)
            throw new InvalidOperationException("An ACP transport is already attached.");

        Current = transport;
        _lifetimeCts = lifetimeCts;
    }

    public bool Reset(
        AcpJsonRpcTransport expectedTransport,
        long expectedGeneration,
        string? activeSessionId,
        string? persistedSessionId)
    {
        if (!ReferenceEquals(Current, expectedTransport) || Generation != expectedGeneration)
            return false;

        RecoverySessionId ??= activeSessionId ?? persistedSessionId;
        RecoveryRequired = !string.IsNullOrWhiteSpace(RecoverySessionId);
        try { _lifetimeCts?.Cancel(); } catch (ObjectDisposedException) { }
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        Current = null;
        expectedTransport.Dispose();
        return true;
    }

    public void ClearRecovery()
    {
        RecoverySessionId = null;
        RecoveryRequired = false;
    }

    public void DisableRecovery() => RecoveryRequired = false;

    public void ForceDispose(AcpJsonRpcTransport transport) => transport.Dispose();

    public bool IsCurrent(AcpJsonRpcTransport transport, long generation)
        => ReferenceEquals(Current, transport) && Generation == generation;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _lifetimeCts?.Cancel(); } catch (ObjectDisposedException) { }
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        Current?.Dispose();
        Current = null;
        _gate.Dispose();
    }
}
