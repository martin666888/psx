using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSX.Models;
using PSX.Services;

namespace PSX.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceManager _workspaceManager;
    private bool _disposed;
    private string? _persistentWarning;

    public ObservableCollection<TabItemViewModel> Tabs { get; } = new();
    public ObservableCollection<AgentProviderChoiceViewModel> AgentProviders { get; } = new();
    public ThemePickerViewModel ThemePicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusVisible;

    [ObservableProperty]
    private bool _hasPersistentWarning;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public void SetPersistentWarning(string? message)
    {
        if (_disposed) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _persistentWarning = message;
            RefreshStatusMessage();
        });
    }

    [RelayCommand]
    private void DismissPersistentWarning()
    {
        _persistentWarning = null;
        RefreshStatusMessage();
    }

    private void RefreshStatusMessage()
    {
        // The bottom bar now carries only persistent startup warnings (for
        // example a missing WebView2 runtime); routine runtime status moved
        // to the workspace toolbar. It stays collapsed until a warning fires.
        HasPersistentWarning = !string.IsNullOrWhiteSpace(_persistentWarning);
        StatusMessage = HasPersistentWarning ? _persistentWarning : null;
        IsStatusVisible = !string.IsNullOrWhiteSpace(StatusMessage);
    }

    [ObservableProperty]
    private TabItemViewModel? _activeTab;

    [RelayCommand]
    private void NewTab()
    {
        RecordPendingPlacementIfNeeded();
        _ = _workspaceManager.CreateTerminalAsync();
    }

    [RelayCommand]
    private void NewAgent(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return;
        RecordPendingPlacementIfNeeded();
        _ = _workspaceManager.CreateAgentAsync(providerKey);
    }

    /// <summary>Creation transaction: when the「+」popover's「新建到右侧列」
    /// toggle is on, the next created workspace lands in a fresh pane.</summary>
    private void RecordPendingPlacementIfNeeded()
    {
        if (!NewWorkspaceToNewPane) return;
        _workspaceManager.RecordPendingPlacement(WorkspaceLayoutService.NewPanePlacement);
        NewWorkspaceToNewPane = false;
    }

    [ObservableProperty]
    private bool _newWorkspaceToNewPane;

    [ObservableProperty]
    private bool _isSplit;

    [RelayCommand]
    private void MoveTabToNewPane(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;
        _workspaceManager.SplitWorkspaceToNewPane(sessionId);
    }

    [RelayCommand]
    private void SwapPanes() => _workspaceManager.SwapPanes();

    [RelayCommand]
    private void CollapseToSinglePane() => _workspaceManager.CollapseToSinglePane();

    [RelayCommand]
    private void FocusAdjacentPane(int delta) => _workspaceManager.FocusAdjacentPane(delta);

    [RelayCommand]
    private void CloseTab(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;

        _ = _workspaceManager.CloseAsync(sessionId);
    }

    [RelayCommand]
    private void SwitchTab(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;

        var tab = Tabs.FirstOrDefault(t => t.SessionId == sessionId);
        if (tab != null)
        {
            ActiveTab = tab;
            _ = _workspaceManager.ActivateAsync(sessionId);
        }
    }

    partial void OnActiveTabChanged(TabItemViewModel? oldValue, TabItemViewModel? newValue)
    {
        foreach (var tab in Tabs)
        {
            tab.IsActive = ReferenceEquals(tab, newValue);
        }
    }

    public MainViewModel(
        IWorkspaceManager workspaceManager,
        ThemePickerViewModel themePicker)
    {
        _workspaceManager = workspaceManager;
        ThemePicker = themePicker;

        foreach (var provider in workspaceManager.AgentProviders)
        {
            AgentProviders.Add(new AgentProviderChoiceViewModel(
                provider.Key,
                provider.DisplayName,
                provider.IconKey));
        }

        _workspaceManager.WorkspaceCreated += OnWorkspaceCreated;
        _workspaceManager.WorkspaceClosed += OnWorkspaceClosed;
        _workspaceManager.WorkspaceChanged += OnWorkspaceChanged;
        _workspaceManager.WorkspaceActivationRequested += OnWorkspaceActivationRequested;
        _workspaceManager.LayoutChanged += OnLayoutChanged;
    }

    private void OnLayoutChanged(object? sender, WorkspaceLayoutSnapshot snapshot)
    {
        if (_disposed) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            IsSplit = snapshot.Panes.Count > 1;
            // Tab three-state: focused pane's workspace = active, other visible
            // panes = muted underline, everything else unmarked.
            var visible = new Dictionary<Guid, bool>();
            foreach (var pane in snapshot.Panes)
            {
                if (!pane.WorkspaceId.HasValue) continue;
                visible[pane.WorkspaceId.Value] = pane.PaneId == snapshot.FocusedPaneId;
            }
            foreach (var tab in Tabs)
            {
                if (!visible.TryGetValue(tab.SessionId, out var focused))
                {
                    tab.IsActive = false;
                    tab.IsPaneVisible = false;
                    tab.IsSplit = IsSplit;
                    continue;
                }
                tab.IsActive = focused;
                tab.IsPaneVisible = !focused;
                tab.IsSplit = IsSplit;
                if (focused) ActiveTab = tab;
            }
        });
    }

    private void OnWorkspaceCreated(object? sender, WorkspaceEventArgs e)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            var tab = new TabItemViewModel(e.Workspace, CloseTabCommand);
            Tabs.Add(tab);
            ActiveTab = tab;
        });
    }

    private void OnWorkspaceClosed(object? sender, WorkspaceClosedEventArgs e)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            var tab = Tabs.FirstOrDefault(t => t.SessionId == e.WorkspaceId);
            if (tab != null)
            {
                Tabs.Remove(tab);
            }

            if (ActiveTab?.SessionId == e.WorkspaceId)
                ActiveTab = null;
        });
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceEventArgs e)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            var tab = Tabs.FirstOrDefault(t => t.SessionId == e.Workspace.WorkspaceId);
            if (tab != null)
            {
                tab.Update(e.Workspace);
            }
        });
    }

    private void OnWorkspaceActivationRequested(object? sender, Guid workspaceId)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            ActiveTab = Tabs.FirstOrDefault(tab => tab.SessionId == workspaceId) ?? ActiveTab;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _workspaceManager.WorkspaceCreated -= OnWorkspaceCreated;
        _workspaceManager.WorkspaceClosed -= OnWorkspaceClosed;
        _workspaceManager.WorkspaceChanged -= OnWorkspaceChanged;
        _workspaceManager.WorkspaceActivationRequested -= OnWorkspaceActivationRequested;
        _workspaceManager.LayoutChanged -= OnLayoutChanged;
    }
}

public sealed record AgentProviderChoiceViewModel(string Key, string DisplayName, string IconKey);
