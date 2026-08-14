using System.Windows.Threading;
using PSX.Models;

namespace PSX.Services;

public sealed class TabManagementService : ITabManagementService, IDisposable
{
    private readonly ConPtyService _conPtyService;
    private readonly ITerminalBridgeService _bridgeService;
    private readonly ISettingsService _settingsService;
    private readonly object _sessionStateLock = new();
    private readonly HashSet<Guid> _closingSessions = new();
    private readonly HashSet<Guid> _closedSessions = new();
    // Per-session last applied size: dedupes identical resize round-trips and
    // keeps each ConPTY session on its own dimensions (split-pane foundation).
    private readonly Dictionary<Guid, (int Cols, int Rows)> _sessionSizes = new();
    // Creation-time fallback for a new tab (single-pane semantics: the last
    // resize always came from the one visible pane). Phase 1 replaces this
    // inheritance with the target pane's measured size.
    private int? _lastTerminalColumns;
    private int? _lastTerminalRows;
    private bool _disposed;

    public event EventHandler<TabCreatedEventArgs>? TabCreated;
    public event EventHandler<TabClosedEventArgs>? TabClosed;
    public event EventHandler<TabTitleChangedEventArgs>? TabTitleChanged;
    public event EventHandler<string>? PaneFocusRequested;
    public event EventHandler<PaneRatiosEventArgs>? PaneRatiosRequested;
    public event EventHandler<PaneMoveEventArgs>? PaneMoveRequested;
    public event EventHandler<WorkspaceLayoutIntentEventArgs>? WorkspaceLayoutIntentRequested;
    public event EventHandler<WorkspaceCreateEventArgs>? WorkspaceCreateRequested;
    public event EventHandler<DshCommandEventArgs>? DshCommandRequested;

    public TabManagementService(
        ConPtyService conPtyService,
        ITerminalBridgeService bridgeService,
        ISettingsService settingsService)
    {
        _conPtyService = conPtyService;
        _bridgeService = bridgeService;
        _settingsService = settingsService;

        // Wire up event forwarding
        _conPtyService.OutputReceived += OnConPtyOutput;
        _conPtyService.SessionExited += OnConPtySessionExited;
        _bridgeService.InputReceived += OnBridgeInput;
        _bridgeService.ResizeRequested += OnBridgeResize;
        _bridgeService.TitleChanged += OnBridgeTitleChanged;
        _bridgeService.FrontendReady += OnFrontendReady;
        _bridgeService.PaneFocusRequested += (_, paneId) => PaneFocusRequested?.Invoke(this, paneId);
        _bridgeService.PaneRatiosRequested += (_, args) => PaneRatiosRequested?.Invoke(this, args);
        _bridgeService.PaneMoveRequested += (_, args) => PaneMoveRequested?.Invoke(this, args);
        _bridgeService.WorkspaceLayoutIntentRequested += (_, args) => WorkspaceLayoutIntentRequested?.Invoke(this, args);
        _bridgeService.WorkspaceCreateRequested += (_, args) => WorkspaceCreateRequested?.Invoke(this, args);
        _bridgeService.DshCommandRequested += (_, args) => DshCommandRequested?.Invoke(this, args);
    }

    public Task<Guid> CreateTabAsync(ShellProfile? profile = null)
    {
        if (_disposed)
            return Task.FromResult(Guid.Empty);

        profile ??= _settingsService.GetDefaultProfile();

        int columns;
        int rows;
        lock (_sessionStateLock)
        {
            columns = _lastTerminalColumns ?? 120;
            rows = _lastTerminalRows ?? 30;
        }

        var size = new TerminalSize { Columns = columns, Rows = rows };
        var session = _conPtyService.CreateSession(profile, size);

        _ = _bridgeService.CreateTerminalAsync(session.SessionId);
        _ = _bridgeService.SwitchTerminalAsync(session.SessionId);

        TabCreated?.Invoke(this, new TabCreatedEventArgs
        {
            SessionId = session.SessionId,
            Title = profile.Name
        });

        return Task.FromResult(session.SessionId);
    }

    public Task CloseTabAsync(Guid sessionId)
    {
        if (_disposed)
            return Task.CompletedTask;

        lock (_sessionStateLock)
        {
            if (!_closedSessions.Add(sessionId))
                return Task.CompletedTask;

            _closingSessions.Add(sessionId);
            _sessionSizes.Remove(sessionId);
        }

        _ = _bridgeService.CloseTerminalAsync(sessionId);
        TabClosed?.Invoke(this, new TabClosedEventArgs { SessionId = sessionId });

        _ = _conPtyService.CloseSessionAsync(sessionId);
        return Task.CompletedTask;
    }

    public Task SwitchTabAsync(Guid sessionId)
    {
        if (_disposed)
            return Task.CompletedTask;

        _ = _bridgeService.SwitchTerminalAsync(sessionId);
        return Task.CompletedTask;
    }

    public Task ResizeTabAsync(Guid sessionId, int cols, int rows)
    {
        if (_disposed)
            return Task.CompletedTask;

        _conPtyService.Resize(sessionId, cols, rows);
        _ = _bridgeService.ResizeTerminalAsync(sessionId, cols, rows);
        return Task.CompletedTask;
    }

    public TerminalSession? GetSession(Guid sessionId)
    {
        return _conPtyService.GetSession(sessionId);
    }

    private void OnConPtyOutput(object? sender, TerminalOutputEventArgs e)
    {
        if (_disposed) return;

        try
        {
            _ = _bridgeService.SendOutputAsync(e.SessionId, e.Data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"发送输出失败: {ex.Message}");
        }
    }

    private void OnConPtySessionExited(object? sender, SessionExitedEventArgs e)
    {
        if (_disposed) return;

        lock (_sessionStateLock)
        {
            if (_closingSessions.Contains(e.SessionId) || !_closedSessions.Add(e.SessionId))
                return;

            _sessionSizes.Remove(e.SessionId);
        }

        _ = _bridgeService.CloseTerminalAsync(e.SessionId);
        TabClosed?.Invoke(this, new TabClosedEventArgs { SessionId = e.SessionId });
    }

    private void OnBridgeInput(object? sender, TerminalInputEventArgs e)
    {
        if (_disposed) return;

        _conPtyService.WriteInput(e.SessionId, e.Data);
    }

    private void OnBridgeResize(object? sender, TerminalResizeEventArgs e)
    {
        if (_disposed) return;

        if (e.Cols is < 2 or > short.MaxValue || e.Rows is < 1 or > short.MaxValue)
            return;

        lock (_sessionStateLock)
        {
            _lastTerminalColumns = e.Cols;
            _lastTerminalRows = e.Rows;

            // Identical (cols, rows) round-trips must not reach ConPTY again.
            if (_sessionSizes.TryGetValue(e.SessionId, out var applied)
                && applied.Cols == e.Cols && applied.Rows == e.Rows)
            {
                return;
            }

            _sessionSizes[e.SessionId] = (e.Cols, e.Rows);
        }

        _conPtyService.Resize(e.SessionId, e.Cols, e.Rows);
    }

    private void OnBridgeTitleChanged(object? sender, TerminalTitleEventArgs e)
    {
        if (_disposed) return;

        TabTitleChanged?.Invoke(this, new TabTitleChangedEventArgs
        {
            SessionId = e.SessionId,
            Title = e.Title
        });
    }

    private void OnFrontendReady(object? sender, EventArgs e)
    {
        if (_disposed) return;

        // Use BeginInvoke to avoid blocking the WebView2 message handler
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            if (_disposed) return;

            try
            {
                await CreateTabAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建初始 Tab 失败: {ex}");
            }
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sessionStateLock)
        {
            _closingSessions.Clear();
            _closedSessions.Clear();
        }

        _conPtyService.OutputReceived -= OnConPtyOutput;
        _conPtyService.SessionExited -= OnConPtySessionExited;
        _bridgeService.InputReceived -= OnBridgeInput;
        _bridgeService.ResizeRequested -= OnBridgeResize;
        _bridgeService.TitleChanged -= OnBridgeTitleChanged;
        _bridgeService.FrontendReady -= OnFrontendReady;

        // ConPtyService.Dispose handles session cleanup
        _conPtyService.Dispose();
    }

    public Task ShutdownAsync(TimeSpan? timeout = null)
    {
        return _conPtyService.ShutdownAsync(timeout);
    }
}
