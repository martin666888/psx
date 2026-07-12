using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSX.Services;

namespace PSX.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ITabManagementService _tabManagementService;
    private readonly ITerminalBridgeService _terminalBridgeService;
    private bool _isCreatingReplacementTab;
    private bool _disposed;
    private string? _routineStatus;
    private string? _persistentWarning;

    public ObservableCollection<TabItemViewModel> Tabs { get; } = new();
    public ThemePickerViewModel ThemePicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusVisible;

    [ObservableProperty]
    private bool _hasPersistentWarning;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public void SetStatus(string? message)
    {
        if (_disposed) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _routineStatus = message;
            RefreshStatusMessage();
        });
    }

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
        HasPersistentWarning = !string.IsNullOrWhiteSpace(_persistentWarning);
        StatusMessage = HasPersistentWarning ? _persistentWarning : _routineStatus;
        IsStatusVisible = !string.IsNullOrWhiteSpace(StatusMessage);
    }

    [ObservableProperty]
    private TabItemViewModel? _activeTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTerminalView))]
    [NotifyPropertyChangedFor(nameof(IsAgentView))]
    private string _activeViewMode = "terminal";

    public bool IsTerminalView => string.Equals(ActiveViewMode, "terminal", StringComparison.OrdinalIgnoreCase);
    public bool IsAgentView => string.Equals(ActiveViewMode, "agent", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void NewTab()
    {
        _ = _tabManagementService.CreateTabAsync();
    }

    [RelayCommand]
    private void CloseTab(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;

        _ = _tabManagementService.CloseTabAsync(sessionId);
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
            _ = _tabManagementService.SwitchTabAsync(sessionId);
        }
    }

    [RelayCommand]
    private void ShowTerminal()
    {
        ActiveViewMode = "terminal";
        _ = _terminalBridgeService.SetViewModeAsync("terminal");
    }

    [RelayCommand]
    private void ShowAgent()
    {
        ActiveViewMode = "agent";
        _ = _terminalBridgeService.SetViewModeAsync("agent");
    }

    partial void OnActiveTabChanged(TabItemViewModel? oldValue, TabItemViewModel? newValue)
    {
        foreach (var tab in Tabs)
        {
            tab.IsActive = ReferenceEquals(tab, newValue);
        }
    }

    public MainViewModel(
        ITabManagementService tabManagementService,
        ITerminalBridgeService terminalBridgeService,
        ThemePickerViewModel themePicker)
    {
        _tabManagementService = tabManagementService;
        _terminalBridgeService = terminalBridgeService;
        ThemePicker = themePicker;

        _tabManagementService.TabCreated += OnTabCreated;
        _tabManagementService.TabClosed += OnTabClosed;
        _tabManagementService.TabTitleChanged += OnTabTitleChanged;
        _terminalBridgeService.ViewModeChanged += OnViewModeChanged;
    }

    private void OnTabCreated(object? sender, TabCreatedEventArgs e)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            _isCreatingReplacementTab = false;

            var tab = new TabItemViewModel(e.SessionId, e.Title, CloseTabCommand);
            Tabs.Add(tab);
            ActiveTab = tab;
        });
    }

    private void OnTabClosed(object? sender, TabClosedEventArgs e)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            var tab = Tabs.FirstOrDefault(t => t.SessionId == e.SessionId);
            var wasActive = ActiveTab?.SessionId == e.SessionId;
            var removedIndex = tab != null ? Tabs.IndexOf(tab) : -1;

            if (tab != null)
            {
                Tabs.Remove(tab);
            }

            // If all tabs are closed, create a new default one
            if (Tabs.Count == 0 && !_isCreatingReplacementTab)
            {
                _isCreatingReplacementTab = true;
                _ = _tabManagementService.CreateTabAsync();
            }
            else if (wasActive || ActiveTab == null)
            {
                var nextIndex = removedIndex >= 0
                    ? Math.Min(removedIndex, Tabs.Count - 1)
                    : Tabs.Count - 1;

                ActiveTab = nextIndex >= 0 ? Tabs[nextIndex] : null;
                if (ActiveTab != null)
                {
                    _ = _tabManagementService.SwitchTabAsync(ActiveTab.SessionId);
                }
            }
        });
    }

    private void OnTabTitleChanged(object? sender, TabTitleChangedEventArgs e)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;

            var tab = Tabs.FirstOrDefault(t => t.SessionId == e.SessionId);
            if (tab != null)
            {
                tab.Title = e.Title;
            }
        });
    }

    private void OnViewModeChanged(object? sender, string mode)
    {
        if (_disposed) return;

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            ActiveViewMode = mode;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tabManagementService.TabCreated -= OnTabCreated;
        _tabManagementService.TabClosed -= OnTabClosed;
        _tabManagementService.TabTitleChanged -= OnTabTitleChanged;
        _terminalBridgeService.ViewModeChanged -= OnViewModeChanged;
    }
}
