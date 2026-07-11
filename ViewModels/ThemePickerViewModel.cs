using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSX.Models;
using PSX.Services;

namespace PSX.ViewModels;

public partial class ThemeItemViewModel : ObservableObject
{
    public ThemeDescriptor Descriptor { get; }
    public string Name => Descriptor.Name;
    public string Author => Descriptor.Author;
    public string DiagnosticSummary => Descriptor.DiagnosticSummary;
    public bool IsAvailable => Descriptor.Availability == ThemeAvailability.Available;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isUpdated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConfirm))]
    private bool _isPreview;

    public bool ShowConfirm => IsPreview && IsAvailable;
    public string StatusText => !IsAvailable ? "Invalid" : IsUpdated ? "Updated" : IsCurrent ? "Current" : "";

    public ThemeItemViewModel(ThemeDescriptor descriptor)
    {
        Descriptor = descriptor;
    }
}

public partial class ThemePickerViewModel : ObservableObject
{
    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;
    private readonly IAppearanceService _appearanceService;
    private AppearanceSettings? _baseline;
    private string? _previewFingerprint;
    private bool _committedClose;

    public ObservableCollection<ThemeItemViewModel> BuiltInThemes { get; } = [];
    public ObservableCollection<ThemeItemViewModel> UserThemes { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isMessageError;

    [ObservableProperty]
    private string _currentLabel = "Current: Custom";

    public bool HasUserThemes => UserThemes.Count > 0;

    public ThemePickerViewModel(
        IThemeService themeService,
        ISettingsService settingsService,
        IAppearanceService appearanceService)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _appearanceService = appearanceService;
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            _baseline = AppearanceSettings.FromSettings(_settingsService.GetSettings());
            _committedClose = false;
            RefreshThemeList();
        }
        else if (_committedClose)
        {
            _committedClose = false;
        }
        else
        {
            _ = CancelPreviewAsync();
        }
    }

    [RelayCommand]
    private async Task PreviewThemeAsync(ThemeItemViewModel? item)
    {
        if (item?.IsAvailable != true || _baseline == null)
            return;

        var loaded = _themeService.LoadTheme(item.Descriptor.FilePath, item.Descriptor.Source);
        if (!loaded.IsValid || loaded.Descriptor.Appearance == null)
        {
            SetMessage(loaded.Descriptor.DiagnosticSummary, isError: true);
            RefreshThemeList();
            return;
        }

        foreach (var candidate in BuiltInThemes.Concat(UserThemes))
            candidate.IsPreview = ReferenceEquals(candidate, item);

        _previewFingerprint = loaded.Descriptor.Fingerprint;
        await _appearanceService.ApplyAsync(loaded.Descriptor.Appearance);
        SetMessage("Previewing — confirm to keep this theme.", isError: false);
    }

    [RelayCommand]
    private async Task ConfirmThemeAsync(ThemeItemViewModel? item)
    {
        if (item?.IsPreview != true || _baseline == null)
            return;

        var loaded = _themeService.LoadTheme(item.Descriptor.FilePath, item.Descriptor.Source);
        if (!loaded.IsValid || loaded.Descriptor.Appearance == null)
        {
            SetMessage(loaded.Descriptor.DiagnosticSummary, isError: true);
            return;
        }

        if (!string.Equals(_previewFingerprint, loaded.Descriptor.Fingerprint, StringComparison.Ordinal))
        {
            _previewFingerprint = loaded.Descriptor.Fingerprint;
            await _appearanceService.ApplyAsync(loaded.Descriptor.Appearance);
            SetMessage("Theme file changed. Review the new preview, then confirm again.", isError: false);
            return;
        }

        try
        {
            var latest = _settingsService.ReloadSettings();
            loaded.Descriptor.Appearance.ApplyTo(latest);
            _settingsService.SaveThemeSettings(latest, loaded.Descriptor.Key, loaded.Descriptor.Fingerprint);
            _baseline = loaded.Descriptor.Appearance.Clone();
            _committedClose = true;
            RefreshThemeList();
            IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetMessage($"Unable to apply theme: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task RefreshThemesAsync()
    {
        await CancelPreviewAsync();
        RefreshThemeList();
    }

    [RelayCommand]
    private void OpenThemeFolder()
    {
        try
        {
            _themeService.OpenUserThemeDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(ex.Message, isError: true);
        }
    }

    private async Task CancelPreviewAsync()
    {
        if (_baseline != null && BuiltInThemes.Concat(UserThemes).Any(t => t.IsPreview))
            await _appearanceService.ApplyAsync(_baseline);

        _previewFingerprint = null;
        foreach (var item in BuiltInThemes.Concat(UserThemes))
            item.IsPreview = false;
        SetMessage(null, isError: false);
    }

    private void RefreshThemeList()
    {
        var currentSettings = _settingsService.GetSettings();
        var currentFingerprint = _themeService.ComputeFingerprint(AppearanceSettings.FromSettings(currentSettings));
        var themes = _themeService.ScanThemes();

        BuiltInThemes.Clear();
        UserThemes.Clear();
        foreach (var descriptor in themes)
        {
            var item = new ThemeItemViewModel(descriptor)
            {
                IsCurrent = descriptor.Availability == ThemeAvailability.Available &&
                            string.Equals(descriptor.Fingerprint, currentFingerprint, StringComparison.Ordinal),
                IsUpdated = descriptor.Availability == ThemeAvailability.Available &&
                            string.Equals(descriptor.Key, currentSettings.ActiveThemeKey, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(descriptor.Fingerprint, currentFingerprint, StringComparison.Ordinal)
            };
            (descriptor.Source == ThemeSource.BuiltIn ? BuiltInThemes : UserThemes).Add(item);
        }
        var current = BuiltInThemes.Concat(UserThemes).FirstOrDefault(t => t.IsCurrent);
        var source = BuiltInThemes.Concat(UserThemes).FirstOrDefault(t =>
            string.Equals(t.Descriptor.Key, currentSettings.ActiveThemeKey, StringComparison.OrdinalIgnoreCase));
        CurrentLabel = current != null
            ? $"Current: {current.Name}"
            : source != null
                ? $"Current: {source.Name}"
                : string.IsNullOrWhiteSpace(currentSettings.ActiveThemeKey)
                    ? "Current: Custom"
                    : "Theme source missing";
        OnPropertyChanged(nameof(HasUserThemes));
    }

    private void SetMessage(string? text, bool isError)
    {
        Message = text;
        IsMessageError = isError;
    }
}
