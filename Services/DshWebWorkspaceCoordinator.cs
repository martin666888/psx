using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Owns the single DeepSeek Harness (DSH) web workspace: one descriptor per
/// process, created on demand and never persisted. Phase 1 keeps the runtime
/// uninstalled — the iframe stays empty and the host shows a placeholder card;
/// the runtime/supervisor arrive in Phase 2. Closing the tab never disposes a
/// runtime (there is none yet); the descriptor is simply removed and can be
/// recreated.
/// </summary>
public interface IDshWebWorkspaceCoordinator
{
    Guid? OpenWorkspaceId { get; }
    Task<Guid?> CreateAsync();
    Task ActivateAsync(Guid workspaceId);
    Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason);
    /// <summary>Handle a dsh_command (install | retry | stop). Phase 1 has no
    /// runtime, so every command reports not_installed.</summary>
    Task HandleCommandAsync(string name);
    void BeginShutdown();

    event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;
}

public sealed class DshWebWorkspaceCoordinator : IDshWebWorkspaceCoordinator
{
    private readonly DshWebRuntimeSupervisor _supervisor;
    private readonly object _sync = new();
    private WorkspaceDescriptor? _workspace;
    private bool _shuttingDown;

    public DshWebWorkspaceCoordinator(DshWebRuntimeSupervisor supervisor) => _supervisor = supervisor;

    public Guid? OpenWorkspaceId
    {
        get { lock (_sync) return _workspace?.WorkspaceId; }
    }

    public event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    public event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;

    public async Task<Guid?> CreateAsync()
    {
        WorkspaceDescriptor? created;
        lock (_sync)
        {
            if (_shuttingDown) return null;
            if (_workspace != null) return _workspace.WorkspaceId;
            _workspace = new WorkspaceDescriptor
            {
                WorkspaceId = Guid.NewGuid(),
                Kind = WorkspaceKind.DshWeb,
                Title = "DeepSeek Harness",
                IconKey = "dsh"
            };
            created = _workspace;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = created! });
        // Kick off the runtime (starts the server if installed, else stays not_installed).
        await _supervisor.EnsureRunningAsync().ConfigureAwait(false);
        return created!.WorkspaceId;
    }

    public async Task ActivateAsync(Guid workspaceId)
    {
        // Ensure the server is running when the user focuses the DSH tab.
        await _supervisor.EnsureRunningAsync().ConfigureAwait(false);
    }

    public Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason)
    {
        lock (_sync)
        {
            if (_workspace?.WorkspaceId != workspaceId) return Task.CompletedTask;
            _workspace = null;
        }
        WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs { WorkspaceId = workspaceId });
        // Closing the tab does NOT stop the runtime (background work survives).
        return Task.CompletedTask;
    }

    public async Task HandleCommandAsync(string name)
    {
        switch (name)
        {
            case "install":
                await _supervisor.InstallAsync().ConfigureAwait(false);
                await _supervisor.EnsureRunningAsync().ConfigureAwait(false);
                break;
            case "retry":
                await _supervisor.EnsureRunningAsync().ConfigureAwait(false);
                break;
            case "stop":
                _supervisor.Stop();
                break;
        }
    }

    public void BeginShutdown() { lock (_sync) _shuttingDown = true; }
}