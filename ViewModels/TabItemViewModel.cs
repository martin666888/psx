using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSX.Models;

namespace PSX.ViewModels;

public partial class TabItemViewModel : ObservableObject
{
    public Guid SessionId { get; }
    public WorkspaceKind Kind { get; }
    public string IconKey { get; }

    [ObservableProperty]
    private string _title = "Terminal";

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Visible in a pane but unfocused (split panes); mutually
    /// exclusive with IsActive, hidden workspaces have neither.</summary>
    [ObservableProperty]
    private bool _isPaneVisible;

    /// <summary>Mirror of the window split state, used by the tab
    /// context menu to show pane actions.</summary>
    [ObservableProperty]
    private bool _isSplit;

    [ObservableProperty]
    private string _toolTip = "";

    [ObservableProperty]
    private AgentWorkspaceState? _agentState;

    public ICommand CloseCommand { get; }

    public TabItemViewModel(WorkspaceDescriptor workspace, ICommand closeCommand)
    {
        SessionId = workspace.WorkspaceId;
        Kind = workspace.Kind;
        IconKey = workspace.IconKey;
        _title = workspace.Title;
        _agentState = workspace.AgentState;
        _toolTip = BuildToolTip(workspace);
        CloseCommand = closeCommand;
    }

    public void Update(WorkspaceDescriptor workspace)
    {
        Title = workspace.Title;
        AgentState = workspace.AgentState;
        ToolTip = BuildToolTip(workspace);
    }

    private static string BuildToolTip(WorkspaceDescriptor workspace)
    {
        if (workspace.Kind == WorkspaceKind.Terminal)
            return workspace.Title;

        var state = workspace.AgentState?.ToString() ?? "Idle";
        return $"{workspace.ProviderName ?? workspace.ProviderKey ?? "Agent"}\n" +
               $"{workspace.Title}\n" +
               $"{workspace.WorkingDirectory}\n" +
               state;
    }
}
