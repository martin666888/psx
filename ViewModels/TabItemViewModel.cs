using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PSX.ViewModels;

public partial class TabItemViewModel : ObservableObject
{
    public Guid SessionId { get; }

    [ObservableProperty]
    private string _title = "Terminal";

    [ObservableProperty]
    private bool _isActive;

    public ICommand CloseCommand { get; }

    public TabItemViewModel(Guid sessionId, string title, ICommand closeCommand)
    {
        SessionId = sessionId;
        _title = title;
        CloseCommand = closeCommand;
    }
}
