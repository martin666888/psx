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

    /// <summary>The workspace needs the user (waiting for input,
    /// erred, or finished) while its pane is unfocused.</summary>
    [ObservableProperty]
    private bool _needsAttention;

    /// <summary>More columns can still be created (below the cap);
    /// drives the 移到新列 menu item.</summary>
    [ObservableProperty]
    private bool _canSplitFurther;

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

        if (workspace.Kind == WorkspaceKind.DshWeb)
            return workspace.Title + "\nDeepSeek Harness 工作区";

        if (workspace.Kind == WorkspaceKind.KimiWeb)
            return workspace.Title + "\nKimi Code Web 工作区";

        var state = workspace.AgentState?.ToString() ?? "Idle";
        return $"{workspace.ProviderName ?? workspace.ProviderKey ?? "Agent"}\n" +
               $"{workspace.Title}\n" +
               $"{workspace.WorkingDirectory}\n" +
               state;
    }
}
