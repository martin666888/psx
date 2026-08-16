using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Support;

/// <summary>
/// Explicit fake for the mandatory <see cref="IDshWebWorkspaceCoordinator"/>
/// injection. It never constructs the real DSH runtime/supervisor, so tests
/// cannot create temp-rooted installs or undisposed lifecycle objects.
/// </summary>
internal sealed class FakeDshWorkspaceCoordinator : IDshWebWorkspaceCoordinator
{
    private readonly object _sync = new();
    private Guid? _openId;
    private bool _shuttingDown;

    public List<string> Commands { get; } = new();
    public List<(string Url, string Filename)> Exports { get; } = new();
    public int CreateCalls { get; private set; }
    public bool FailNextCreate { get; set; }

    public Guid? OpenWorkspaceId
    {
        get { lock (_sync) return _openId; }
    }

    public event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    public event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;

    public Task<Guid?> CreateAsync()
    {
        CreateCalls++;
        if (FailNextCreate)
        {
            FailNextCreate = false;
            return Task.FromResult<Guid?>(null);
        }

        WorkspaceDescriptor created;
        lock (_sync)
        {
            if (_shuttingDown) return Task.FromResult<Guid?>(null);
            if (_openId.HasValue) return Task.FromResult<Guid?>(_openId);
            created = new WorkspaceDescriptor
            {
                WorkspaceId = Guid.NewGuid(),
                Kind = WorkspaceKind.DshWeb,
                Title = "DeepSeek Harness",
                IconKey = "dsh"
            };
            _openId = created.WorkspaceId;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = created });
        return Task.FromResult<Guid?>(created.WorkspaceId);
    }

    public Task ActivateAsync(Guid workspaceId) => Task.CompletedTask;

    public Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason)
    {
        lock (_sync)
        {
            if (_openId != workspaceId) return Task.CompletedTask;
            _openId = null;
        }
        WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs { WorkspaceId = workspaceId });
        return Task.CompletedTask;
    }

    public Task HandleCommandAsync(string name)
    {
        lock (_sync)
            Commands.Add(name);
        return Task.CompletedTask;
    }

    public Task HandleExportAsync(string url, string filename)
    {
        lock (_sync)
            Exports.Add((url, filename));
        return Task.CompletedTask;
    }

    public void BeginShutdown()
    {
        lock (_sync)
            _shuttingDown = true;
    }
}
