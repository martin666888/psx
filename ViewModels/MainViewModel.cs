using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSX.Services;

namespace PSX.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceManager _workspaceManager;
    private bool _disposed;
    private string? _routineStatus;
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

    public void SetStatus(string? message)
    {
        if (_disposed) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // Routine status follows the active workspace's runtime. Persistent
            // startup warnings still take precedence in RefreshStatusMessage.
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

    [RelayCommand]
    private void NewTab()
    {
        _ = _workspaceManager.CreateTerminalAsync();
    }

    [RelayCommand]
    private void NewAgent(string providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
            _ = _workspaceManager.CreateAgentAsync(providerKey);
    }

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
                provider.DisplayName));
        }

        _workspaceManager.WorkspaceCreated += OnWorkspaceCreated;
        _workspaceManager.WorkspaceClosed += OnWorkspaceClosed;
        _workspaceManager.WorkspaceChanged += OnWorkspaceChanged;
        _workspaceManager.WorkspaceActivationRequested += OnWorkspaceActivationRequested;
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
    }
}

public sealed record AgentProviderChoiceViewModel(string Key, string DisplayName);
