using CommunityToolkit.Mvvm.ComponentModel;
using PSX.Services;

namespace PSX.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        var settings = _settingsService.GetSettings();

        _fontSize = settings.FontSize;
        _fontFamily = settings.FontFamily;
        _defaultShell = settings.DefaultShellProfileId;
    }

    [ObservableProperty]
    private int _fontSize = 14;

    [ObservableProperty]
    private string _fontFamily = "Cascadia Code, Consolas, monospace";

    [ObservableProperty]
    private string _defaultShell = "powershell";

    partial void OnFontSizeChanged(int value)
    {
        var settings = _settingsService.GetSettings();
        settings.FontSize = value;
        _settingsService.SaveSettings(settings);
    }

    partial void OnFontFamilyChanged(string value)
    {
        var settings = _settingsService.GetSettings();
        settings.FontFamily = value;
        _settingsService.SaveSettings(settings);
    }

    partial void OnDefaultShellChanged(string value)
    {
        var settings = _settingsService.GetSettings();
        settings.DefaultShellProfileId = value;
        _settingsService.SaveSettings(settings);
    }
}
