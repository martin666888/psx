using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
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
    /// <summary>Mediate a DSH session-log export: validate the URL against the
    /// current ready origin + /api/session.export path, fetch it host-side, and
    /// save via a Windows SaveFileDialog. The browser download path is never
    /// used (WebView2 DownloadStarting does not fire for the DSH frame).</summary>
    Task HandleExportAsync(string url, string filename);
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

    public async Task HandleExportAsync(string url, string filename)
    {
        // The host mediates the export: only the current DSH origin's
        // /api/session.export endpoint may be fetched, and the save runs through
        // a Windows SaveFileDialog rather than the (dead in WebView2) download
        // path. The URL and path are validated against the supervisor's ready URL
        // before any network call; the browser never saves bytes itself.
        var readyUrl = _supervisor.ReadyUrl;
        if (readyUrl == null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var exportUri)) return;
        if (!string.Equals(
                exportUri.GetLeftPart(UriPartial.Authority),
                readyUrl.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(exportUri.AbsolutePath, "/api/session.export", StringComparison.OrdinalIgnoreCase)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await http.GetAsync(exportUri, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return;

        var path = await Application.Current.Dispatcher
            .InvokeAsync(() => PromptExportSavePath(SanitizeExportFilename(filename)))
            .Task.ConfigureAwait(false);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var destination = File.Create(path);
            await source.CopyToAsync(destination).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }

    private static string SanitizeExportFilename(string filename)
    {
        var name = string.Join('_', filename.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "session";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
        return name;
    }

    private static string? PromptExportSavePath(string filename)
    {
        var dialog = new SaveFileDialog
        {
            FileName = filename,
            DefaultExt = ".zip",
            Filter = "ZIP 归档 (*.zip)|*.zip"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void BeginShutdown() { lock (_sync) _shuttingDown = true; }
}