using System.Windows;
using System.Windows.Controls;

namespace PSX.Controls;

public partial class TabBar : UserControl
{
    public TabBar()
    {
        InitializeComponent();
    }

    private void OnNewWorkspaceMenuItemClick(object sender, RoutedEventArgs e)
    {
        NewWorkspacePopup.IsOpen = false;
    }
}
