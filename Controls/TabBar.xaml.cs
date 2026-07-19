using System.Windows.Controls;
using PSX.ViewModels;

namespace PSX.Controls;

public partial class TabBar : UserControl
{
    public TabBar()
    {
        InitializeComponent();
    }

    private void OnNewWorkspaceClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not Button button)
            return;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = "New Terminal",
            Command = viewModel.NewTabCommand
        });

        var agents = new MenuItem { Header = "New Agent" };
        foreach (var provider in viewModel.AgentProviders)
        {
            agents.Items.Add(new MenuItem
            {
                Header = provider.DisplayName,
                Command = viewModel.NewAgentCommand,
                CommandParameter = provider.Key
            });
        }
        agents.IsEnabled = agents.Items.Count > 0;
        menu.Items.Add(agents);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
