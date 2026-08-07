using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PSX.ViewModels;

namespace PSX.Controls;

public partial class TabBar : UserControl
{
    // Drag threshold before a tab press becomes a move-to-pane drag (the
    // drop target lives in the WebView pane row and receives text/plain).
    private const double TabDragThreshold = 6;
    private Point? _tabDragStart;

    public TabBar()
    {
        InitializeComponent();
    }

    private void OnNewWorkspaceMenuItemClick(object sender, RoutedEventArgs e)
    {
        NewWorkspacePopup.IsOpen = false;
    }

    private void OnTabContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement tab)
            return;

        // Keep the selected tab as the popup's data source while its Tag
        // carries the TabBar/MainViewModel commands.  This mirrors the
        // custom + popup rather than invoking WPF's native ContextMenu.
        TabContextPopup.PlacementTarget = tab;
        TabContextPopup.IsOpen = true;
        e.Handled = true;
    }

    private void OnTabContextMenuActionClick(object sender, RoutedEventArgs e)
    {
        TabContextPopup.IsOpen = false;
    }

    private void OnTabSwitchPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            _tabDragStart = e.GetPosition(this);
    }

    private void OnTabSwitchPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragStart is not { } start)
            return;
        var position = e.GetPosition(this);
        if (System.Math.Abs(position.X - start.X) < TabDragThreshold
            && System.Math.Abs(position.Y - start.Y) < TabDragThreshold)
        {
            return;
        }

        _tabDragStart = null;
        if (sender is FrameworkElement element
            && element.DataContext is TabItemViewModel tab
            && tab.SessionId != System.Guid.Empty)
        {
            DragDrop.DoDragDrop(
                element,
                new DataObject(DataFormats.Text, tab.SessionId.ToString()),
                DragDropEffects.Move);
        }
    }
}
