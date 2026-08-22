using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using Microsoft.Win32;
using PSX.Models;

namespace PSX.Services;

/// <summary>
/// Owns the single DeepSeek Harness (DSH) web workspace: one descriptor per
/// process, created on demand and never persisted. PSX owns the tab, column,
/// Web container, process lifecycle and export mediation; DSH itself owns
/// models, keys, projects and sessions. Closing the tab never stops the
/// runtime; the descriptor is removed and can be recreated as a fresh id.
/// </summary>
public interface IDshWebWorkspaceCoordinator
{
    Guid? OpenWorkspaceId { get; }
    Task<Guid?> CreateAsync();
    Task ActivateAsync(Guid workspaceId);
    Task CloseAsync(Guid workspaceId, WorkspaceCloseReason reason);
    /// <summary>Handle a dsh_command (install | retry | stop | check_update | update | cancel_update | recheck_with_registry | retry_install_with_registry).</summary>
    Task HandleCommandAsync(string name, string? version = null, string? registry = null);
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
    /// <summary>Hard cap for one mediated session export; a runaway or
    /// malicious export stream must not fill the user's disk.</summary>
    internal const long MaximumExportBytes = 1L << 30; // 1 GiB

    private readonly DshWebRuntimeSupervisor _supervisor;
    private readonly IAgentBridgeService _bridge;
    private readonly object _sync = new();
    private WorkspaceDescriptor? _workspace;
    private bool _shuttingDown;

    public DshWebWorkspaceCoordinator(DshWebRuntimeSupervisor supervisor, IAgentBridgeService bridge)
    {
        _supervisor = supervisor;
        _bridge = bridge;
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

    public async Task HandleCommandAsync(string name, string? version = null, string? registry = null)
    {
        lock (_sync)
        {
            if (_shuttingDown)
                return;
        }

        switch (name)
        {
            case "install":
                await _supervisor.InstallAndStartAsync().ConfigureAwait(false);
                break;
            case "retry":
                await _supervisor.RetryAsync().ConfigureAwait(false);
                break;
            case "stop":
                await _supervisor.StopAsync().ConfigureAwait(false);
                break;
            case "check_update":
                await _supervisor.CheckForUpdateAsync().ConfigureAwait(false);
                break;
            case "update":
                await _supervisor.UpdateAndRestartAsync(version).ConfigureAwait(false);
                break;
            case "cancel_update":
                await _supervisor.CancelUpdateAsync().ConfigureAwait(false);
                break;
            case "recheck_with_registry":
                if (!string.IsNullOrWhiteSpace(registry))
                    await _supervisor.RecheckWithRegistryAsync(registry).ConfigureAwait(false);
                break;
            case "retry_install_with_registry":
                if (!string.IsNullOrWhiteSpace(registry))
                    await _supervisor.RetryInstallWithRegistryAsync(registry).ConfigureAwait(false);
                break;
        }
    }

    public async Task HandleExportAsync(string url, string filename)
    {
        lock (_sync)
        {
            if (_shuttingDown)
                return;
        }

        // The host mediates the export: only the current DSH origin's
        // /api/session.export endpoint may be fetched, and the save runs through
        // a Windows SaveFileDialog rather than the (dead in WebView2) download
        // path. The URL and path are validated against the supervisor's ready URL
        // before any network call; the browser never saves bytes itself.
        // Requests that fail validation (no runtime, malformed or spoofed URL)
        // are dropped silently; from the first validated request on, every
        // failure surfaces as a fixed safe workspace_notice.
        try
        {
            var readyUrl = _supervisor.ReadyUrl;
            if (readyUrl == null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var exportUri)) return;
            if (!IsAllowedExportUri(exportUri, readyUrl)) return;

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(exportUri, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (ShouldRejectExportResponse(response, readyUrl))
            {
                await SendFailureNoticeAsync("导出失败：服务返回了无效响应。").ConfigureAwait(false);
                return;
            }
            if (!IsAllowedExportContentType(response.Content.Headers.ContentType))
            {
                await SendFailureNoticeAsync("导出失败：会话数据格式无效。").ConfigureAwait(false);
                return;
            }

            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var prefix = new byte[4];
            if (!await TryReadExactAsync(source, prefix).ConfigureAwait(false)
                || !LooksLikeZip(prefix))
            {
                await SendFailureNoticeAsync("导出失败：会话数据无效。").ConfigureAwait(false);
                return;
            }

            var path = await Application.Current.Dispatcher
                .InvokeAsync(() => PromptExportSavePath(SanitizeExportFilename(filename)))
                .Task.ConfigureAwait(false);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                await SaveExportAtomicallyAsync(source, prefix, path, MaximumExportBytes)
                    .ConfigureAwait(false);
            }
            catch (DshExportTooLargeException)
            {
                Debug.WriteLine("DSH export exceeded the size limit.");
                await SendFailureNoticeAsync("导出失败：文件过大，已取消。").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DSH export failed: " + ex.Message);
                await SendFailureNoticeAsync("导出失败：文件写入没有完成。").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("DSH export failed: " + ex.Message);
            await SendFailureNoticeAsync("导出失败：无法连接本地服务。").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write the export to a scratch file beside the target, then atomically
    /// replace the target once the stream is fully written. A pre-existing
    /// file survives any failure; the scratch file never outlives the call.
    /// Throws <see cref="DshExportTooLargeException"/> past
    /// <paramref name="maximumBytes"/>.
    /// </summary>
    internal static async Task SaveExportAtomicallyAsync(
        Stream source, byte[] prefix, string destinationPath, long maximumBytes)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.dsh-part");

        var written = 0L;
        try
        {
            await using (var destination = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.SequentialScan))
            {
                await destination.WriteAsync(prefix).ConfigureAwait(false);
                written = prefix.Length;
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > maximumBytes)
                        throw new DshExportTooLargeException();
                    await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>Raised when an export stream exceeds the size cap.</summary>
    internal sealed class DshExportTooLargeException : Exception
    {
        public DshExportTooLargeException()
            : base("DSH export exceeded the size limit.")
        {
        }
    }

    private Task SendFailureNoticeAsync(string message) =>
        _bridge.SendEventAsync(new { type = "workspace_notice", message });

    /// <summary>
    /// Fill <paramref name="buffer"/> even when the source yields partial
    /// reads. A short stream returns false instead of treating leftover
    /// bytes as a ZIP magic.
    /// </summary>
    internal static async Task<bool> TryReadExactAsync(Stream source, Memory<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[total..]).ConfigureAwait(false);
            if (read == 0)
                return false;
            total += read;
        }

        return true;
    }

    internal static bool IsAllowedExportUri(Uri exportUri, Uri readyUrl)
    {
        if (!string.Equals(
                exportUri.GetLeftPart(UriPartial.Authority),
                readyUrl.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(exportUri.AbsolutePath, "/api/session.export", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuse 3xx (auto-follow is disabled) and any final URI that is no
    /// longer the current DSH origin + export path.
    /// </summary>
    internal static bool ShouldRejectExportResponse(HttpResponseMessage response, Uri readyUrl)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            return true;
        if (!response.IsSuccessStatusCode)
            return true;
        return response.RequestMessage?.RequestUri is { } final
            && !IsAllowedExportUri(final, readyUrl);
    }

    internal static bool IsAllowedExportContentType(MediaTypeHeaderValue? contentType)
    {
        var media = contentType?.MediaType;
        if (string.IsNullOrWhiteSpace(media))
            return true;
        if (media.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static bool LooksLikeZip(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != (byte)'P' || header[1] != (byte)'K')
            return false;
        return (header[2], header[3]) switch
        {
            (0x03, 0x04) => true,
            (0x05, 0x06) => true,
            (0x07, 0x08) => true,
            _ => false
        };
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
