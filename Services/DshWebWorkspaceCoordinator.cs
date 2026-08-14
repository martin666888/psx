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
    private readonly IAgentBridgeService _bridge;
    private readonly object _sync = new();
    private WorkspaceDescriptor? _workspace;
    private bool _shuttingDown;

    public DshWebWorkspaceCoordinator(IAgentBridgeService bridge) => _bridge = bridge;

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
            if (_shuttingDown)
                return null;
            if (_workspace != null)
                return _workspace.WorkspaceId;
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
        await PublishRuntimeStatusAsync("not_installed").ConfigureAwait(false);
        return created!.WorkspaceId;
    }

    public Task ActivateAsync(Guid workspaceId)
    {
        // Phase 1: no runtime state to apply; the descriptor is already open.
        return Task.CompletedTask;
    }

    public Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason)
    {
        lock (_sync)
        {
            if (_workspace?.WorkspaceId != workspaceId)
                return Task.CompletedTask;
            _workspace = null;
        }
        WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs { WorkspaceId = workspaceId });
        return Task.CompletedTask;
    }

    public async Task HandleCommandAsync(string name)
    {
        // Phase 1: no runtime — every command lands on the not_installed card.
        await PublishRuntimeStatusAsync("not_installed").ConfigureAwait(false);
    }

    public void BeginShutdown() { lock (_sync) _shuttingDown = true; }

    private Task PublishRuntimeStatusAsync(string state) =>
        _bridge.SendEventAsync(new
        {
            type = "dsh_runtime_status",
            state,
            readyUrl = (string?)null,
            errorClass = (string?)null
        });
}