using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PSX.Models;
using PSX.Services;

namespace PSX.ViewModels;

public partial class ThemeItemViewModel : ObservableObject
{
    public ThemeItemViewModel(ThemeDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

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

    /// <summary>Hidden WPF compatibility projection only; the WebView renders
    /// localized state through the catalog flags.</summary>
    public string StatusText => !IsAvailable
        ? PSX.Properties.Strings.ThemeStatusInvalid
        : IsUpdated
            ? PSX.Properties.Strings.ThemeStatusUpdated
            : IsCurrent
                ? PSX.Properties.Strings.ThemeStatusCurrent
                : "";
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

    /// <summary>Fixed theme message code (Models-level key resolved by the
    /// frontend locales); <see cref="Message"/> keeps only the raw technical
    /// detail line, never a PSX-composed sentence.</summary>
    [ObservableProperty]
    private string? _messageCode;

    /// <summary>Current-label state: 'named' (uses <see cref="CurrentThemeName"/>),
    /// 'custom' or 'missing' — the sentence lives in the locales.</summary>
    [ObservableProperty]
    private string _currentLabelState = "custom";

    [ObservableProperty]
    private string? _currentThemeName;

    public bool HasUserThemes => UserThemes.Count > 0;
    public IEnumerable<ThemeItemViewModel> AllThemes => BuiltInThemes.Concat(UserThemes);

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
            SetMessage(ThemeMessageCode.InvalidFile, isError: true, detail: loaded.Descriptor.DiagnosticSummary);
            RefreshThemeList();
            return;
        }

        foreach (var candidate in BuiltInThemes.Concat(UserThemes))
            candidate.IsPreview = ReferenceEquals(candidate, item);

        _previewFingerprint = loaded.Descriptor.Fingerprint;
        await _appearanceService.ApplyAsync(loaded.Descriptor.Appearance);
        SetMessage(ThemeMessageCode.Previewing, isError: false);
    }

    [RelayCommand]
    private async Task ConfirmThemeAsync(ThemeItemViewModel? item)
    {
        if (item?.IsPreview != true || _baseline == null)
            return;

        var loaded = _themeService.LoadTheme(item.Descriptor.FilePath, item.Descriptor.Source);
        if (!loaded.IsValid || loaded.Descriptor.Appearance == null)
        {
            SetMessage(ThemeMessageCode.InvalidFile, isError: true, detail: loaded.Descriptor.DiagnosticSummary);
            return;
        }

        if (!string.Equals(_previewFingerprint, loaded.Descriptor.Fingerprint, StringComparison.Ordinal))
        {
            _previewFingerprint = loaded.Descriptor.Fingerprint;
            await _appearanceService.ApplyAsync(loaded.Descriptor.Appearance);
            SetMessage(ThemeMessageCode.FileChanged, isError: false);
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
            SetMessage(ThemeMessageCode.ApplyFailed, isError: true, detail: ex.Message);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            SetMessage(ThemeMessageCode.FolderOpenFailed, isError: true, detail: ex.Message);
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
        IReadOnlyList<ThemeDescriptor> themes;
        try
        {
            themes = _themeService.ScanThemes();
        }
        catch (Exception ex)
        {
            // Keep the previously-rendered lists rather than clearing them on
            // a directory-level failure.
            SetMessage(ThemeMessageCode.ScanFailed, isError: true, detail: ex.Message);
            return;
        }

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
        if (current != null)
        {
            CurrentLabelState = "named";
            CurrentThemeName = current.Name;
        }
        else if (source != null)
        {
            CurrentLabelState = "named";
            CurrentThemeName = source.Name;
        }
        else
        {
            CurrentLabelState = string.IsNullOrWhiteSpace(currentSettings.ActiveThemeKey) ? "custom" : "missing";
            CurrentThemeName = null;
        }
        OnPropertyChanged(nameof(HasUserThemes));
    }

    private void SetMessage(string? code, bool isError, string? detail = null)
    {
        MessageCode = code;
        Message = detail;
        IsMessageError = isError;
    }

    /// <summary>Fixed theme message codes; sentences live in the frontend locales.</summary>
    public static class ThemeMessageCode
    {
        public const string Previewing = "theme.previewing";
        public const string FileChanged = "theme.file_changed";
        public const string ApplyFailed = "theme.apply_failed";
        public const string FolderOpenFailed = "theme.folder_open_failed";
        public const string ScanFailed = "theme.scan_failed";
        public const string InvalidFile = "theme.invalid_file";
    }

    public async Task HandleWebActionAsync(string action, string? themeKey)
    {
        if (action != "cancel" && !IsOpen)
            IsOpen = true;

        var item = string.IsNullOrWhiteSpace(themeKey)
            ? null
            : AllThemes.FirstOrDefault(candidate =>
                string.Equals(candidate.Descriptor.Key, themeKey, StringComparison.OrdinalIgnoreCase));

        switch (action)
        {
            case "preview":
                await PreviewThemeAsync(item);
                break;
            case "confirm":
                await ConfirmThemeAsync(item);
                break;
            case "cancel":
                IsOpen = false;
                break;
            case "refresh":
                await RefreshThemesAsync();
                break;
            case "open_folder":
                OpenThemeFolder();
                break;
        }
    }
}
