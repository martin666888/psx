using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Owns the single Kimi Code Web workspace: one descriptor per process,
/// created on demand and never persisted. PSX owns the tab, column, Web
/// container, process lifecycle and export mediation; Kimi owns its own
/// server state under the user's profile. Closing the tab never stops the
/// runtime; the descriptor is removed and can be recreated as a fresh id.
/// </summary>
public interface IKimiWebWorkspaceCoordinator
{
    Guid? OpenWorkspaceId { get; }
    Task<Guid?> CreateAsync();
    Task ActivateAsync(Guid workspaceId);
    Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason);
    /// <summary>Handle a kimi_web_command (stop | retry).</summary>
    Task HandleCommandAsync(string name);
    /// <summary>Mediate a Kimi Web session export: validate the URL against
    /// the current kimi origin + /api/v1/sessions/{id}/export path, fetch it
    /// host-side as a POST with the supervisor's in-memory token, and save
    /// via a Windows SaveFileDialog. The browser download path is never used;
    /// every failure surfaces as a workspace_notice (never silent).</summary>
    Task HandleExportAsync(string url, string? path, string? sessionId);
    void BeginShutdown();

    event EventHandler<WorkspaceEventArgs>? WorkspaceCreated;
    event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;
}

public sealed class KimiWebWorkspaceCoordinator : IKimiWebWorkspaceCoordinator
{
    /// <summary>Exact path format of the Kimi Web session export endpoint;
    /// the session id is not trusted beyond matching [^/]+.</summary>
    private static readonly Regex ExportPathPattern = new(
        @"^/api/v1/sessions/[^/]+/export$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly KimiWebRuntimeSupervisor _supervisor;
    private readonly IAgentBridgeService _bridge;
    private readonly object _sync = new();
    private WorkspaceDescriptor? _workspace;
    private bool _shuttingDown;

    public KimiWebWorkspaceCoordinator(KimiWebRuntimeSupervisor supervisor, IAgentBridgeService bridge)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

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
                Kind = WorkspaceKind.KimiWeb,
                Title = "Kimi Code Web",
                IconKey = "kimi"
            };
            created = _workspace;
        }
        WorkspaceCreated?.Invoke(this, new WorkspaceEventArgs { Workspace = created! });
        // Kick off the runtime server (launches `kimi web` when launchable,
        // else publishes the unavailable reason).
        await _supervisor.StartAsync().ConfigureAwait(false);
        return created!.WorkspaceId;
    }

    public async Task ActivateAsync(Guid workspaceId)
    {
        // Ensure the server is running when the user focuses the Kimi Web tab.
        await _supervisor.StartAsync().ConfigureAwait(false);
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
        // Shutdown gate: once BeginShutdown ran, no new Kimi Web operation may
        // start; MainWindow's shutdown path owns the supervisor teardown.
        lock (_sync)
        {
            if (_shuttingDown)
                return;
        }

        switch (name)
        {
            case "retry":
                await _supervisor.RetryAsync().ConfigureAwait(false);
                break;
            case "stop":
                await _supervisor.StopAsync().ConfigureAwait(false);
                break;
        }
    }

    public async Task HandleExportAsync(string url, string? path, string? sessionId)
    {
        lock (_sync)
        {
            if (_shuttingDown)
                return;
        }

        // The host mediates the export: only the current kimi origin's
        // /api/v1/sessions/{id}/export endpoint may be POSTed, the request
        // carries the Authorization header from the supervisor's in-memory
        // token (never from the frontend), and the save runs through a
        // Windows SaveFileDialog rather than the (dead in WebView2) download
        // path. The URL is validated against the supervisor's ready snapshot
        // before any network call; the browser never saves bytes itself.
        // Unlike DSH, every failure surfaces as a user-readable
        // workspace_notice — the kimi export contract never fails silently.
        try
        {
            var ready = _supervisor.GetReadyTokenSnapshot();
            if (ready == null)
            {
                await SendFailureNoticeAsync("导出失败：本地服务未就绪，请稍后重试。").ConfigureAwait(false);
                return;
            }
            var origin = KimiWebRuntimeSupervisor.ToFrameOrigin(ready.Value.ReadyUrl);
            if (origin == null)
            {
                await SendFailureNoticeAsync("导出失败：本地服务未就绪，请稍后重试。").ConfigureAwait(false);
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var exportUri) || !IsAllowedExportUri(exportUri, origin))
            {
                await SendFailureNoticeAsync("导出失败：导出请求无效。").ConfigureAwait(false);
                return;
            }

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, exportUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ready.Value.Token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (ShouldRejectExportResponse(response, origin))
            {
                await SendFailureNoticeAsync("导出失败：服务返回了无效响应。").ConfigureAwait(false);
                return;
            }
            if (!DshWebWorkspaceCoordinator.IsAllowedExportContentType(response.Content.Headers.ContentType))
            {
                await SendFailureNoticeAsync("导出失败：会话数据格式无效。").ConfigureAwait(false);
                return;
            }

            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var prefix = new byte[4];
            if (!await DshWebWorkspaceCoordinator.TryReadExactAsync(source, prefix).ConfigureAwait(false)
                || !DshWebWorkspaceCoordinator.LooksLikeZip(prefix))
            {
                await SendFailureNoticeAsync("导出失败：会话数据无效。").ConfigureAwait(false);
                return;
            }

            var filename = string.IsNullOrWhiteSpace(sessionId)
                ? "kimi-session-export"
                : $"kimi-session-{SanitizeExportFilename(sessionId)}";
            var targetPath = await Application.Current.Dispatcher
                .InvokeAsync(() => PromptExportSavePath(filename))
                .Task.ConfigureAwait(false);
            if (string.IsNullOrEmpty(targetPath)) return;

            try
            {
                await DshWebWorkspaceCoordinator.SaveExportAtomicallyAsync(
                        source, prefix, targetPath, DshWebWorkspaceCoordinator.MaximumExportBytes)
                    .ConfigureAwait(false);
            }
            catch (DshWebWorkspaceCoordinator.DshExportTooLargeException)
            {
                Debug.WriteLine("Kimi Web export exceeded the size limit.");
                await SendFailureNoticeAsync("导出失败：文件过大，已取消。").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Kimi Web export failed: " + ex.Message);
                await SendFailureNoticeAsync("导出失败：文件写入没有完成。").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Kimi Web export failed: " + ex.Message);
            await SendFailureNoticeAsync("导出失败：无法连接本地服务。").ConfigureAwait(false);
        }
    }

    private Task SendFailureNoticeAsync(string message) =>
        _bridge.SendEventAsync(new { type = "workspace_notice", message });

    /// <summary>True when the export URI is exactly the current kimi origin
    /// plus the /api/v1/sessions/{id}/export path; anything else (spoofed,
    /// stale or redirected target) is refused.</summary>
    internal static bool IsAllowedExportUri(Uri exportUri, string origin)
    {
        if (exportUri.Scheme != Uri.UriSchemeHttp)
            return false;
        if (!string.IsNullOrEmpty(exportUri.UserInfo))
            return false;
        if (!string.Equals(
                exportUri.GetLeftPart(UriPartial.Authority),
                origin,
                StringComparison.OrdinalIgnoreCase))
            return false;
        return ExportPathPattern.IsMatch(exportUri.AbsolutePath);
    }

    /// <summary>Refuse 3xx (auto-follow is disabled) and any final URI that is
    /// no longer the current kimi origin + export path.</summary>
    internal static bool ShouldRejectExportResponse(HttpResponseMessage response, string origin)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            return true;
        if (!response.IsSuccessStatusCode)
            return true;
        return response.RequestMessage?.RequestUri is { } final
            && !IsAllowedExportUri(final, origin);
    }

    private static string SanitizeExportFilename(string sessionId)
    {
        var name = string.Join('_', sessionId.Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrWhiteSpace(name) ? "export" : name;
    }

    private static string? PromptExportSavePath(string filename)
    {
        var name = filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? filename
            : filename + ".zip";
        var dialog = new SaveFileDialog
        {
            FileName = name,
            DefaultExt = ".zip",
            Filter = "ZIP 归档 (*.zip)|*.zip"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void BeginShutdown() { lock (_sync) _shuttingDown = true; }
}
