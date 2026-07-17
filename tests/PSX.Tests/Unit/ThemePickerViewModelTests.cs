using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;
using PSX.ViewModels;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ThemePickerViewModelTests
{
    [TestMethod]
    public async Task PreviewTheme_AvailableTheme_AppliesPreviewAndMarksOnlySelection()
    {
        var first = Descriptor("first", "fingerprint-1", "#101010");
        var second = Descriptor("second", "fingerprint-2", "#202020");
        var fixture = CreateFixture(first, second);
        fixture.ViewModel.IsOpen = true;

        await fixture.ViewModel.PreviewThemeCommand.ExecuteAsync(fixture.ViewModel.BuiltInThemes[1]);

        Assert.HasCount(1, fixture.Appearance.Applied);
        Assert.AreEqual("#202020", fixture.Appearance.Applied[0].ThemeColors.Background);
        Assert.IsFalse(fixture.ViewModel.BuiltInThemes[0].IsPreview);
        Assert.IsTrue(fixture.ViewModel.BuiltInThemes[1].IsPreview);
        Assert.IsFalse(fixture.ViewModel.IsMessageError);
    }

    [TestMethod]
    public async Task Close_AfterPreview_RestoresBaselineAndClearsPreview()
    {
        var fixture = CreateFixture(Descriptor("preview", "fingerprint", "#101010"));
        fixture.Settings.Current.ThemeColors.Background = "#abcdef";
        fixture.ViewModel.IsOpen = true;
        await fixture.ViewModel.PreviewThemeCommand.ExecuteAsync(fixture.ViewModel.BuiltInThemes[0]);

        fixture.ViewModel.IsOpen = false;
        await TestWorkspace.WaitUntilAsync(
            () => fixture.Appearance.Applied.Count == 2,
            TimeSpan.FromSeconds(2),
            "Closing the picker did not restore the baseline appearance.");

        Assert.AreEqual("#abcdef", fixture.Appearance.Applied[1].ThemeColors.Background);
        Assert.IsFalse(fixture.ViewModel.BuiltInThemes[0].IsPreview);
        Assert.IsNull(fixture.ViewModel.Message);
    }

    [TestMethod]
    public async Task ConfirmTheme_UnchangedPreview_SavesLatestSettingsAndClosesWithoutRollback()
    {
        var descriptor = Descriptor("confirmed", "fingerprint", "#123456");
        var fixture = CreateFixture(descriptor);
        fixture.ViewModel.IsOpen = true;
        var item = fixture.ViewModel.BuiltInThemes[0];
        await fixture.ViewModel.PreviewThemeCommand.ExecuteAsync(item);

        await fixture.ViewModel.ConfirmThemeCommand.ExecuteAsync(item);

        Assert.IsFalse(fixture.ViewModel.IsOpen);
        Assert.AreEqual(1, fixture.Settings.SaveCount);
        Assert.AreEqual("builtin:confirmed", fixture.Settings.SavedThemeKey);
        Assert.AreEqual("fingerprint", fixture.Settings.SavedFingerprint);
        Assert.AreEqual("#123456", fixture.Settings.Current.ThemeColors.Background);
        Assert.HasCount(1, fixture.Appearance.Applied);
    }

    [TestMethod]
    public async Task ConfirmTheme_FileChanged_RepreviewsAndRequiresSecondConfirmation()
    {
        var original = Descriptor("changing", "old", "#111111");
        var changed = Descriptor("changing", "new", "#222222");
        var fixture = CreateFixture(original);
        fixture.ViewModel.IsOpen = true;
        var item = fixture.ViewModel.BuiltInThemes[0];
        await fixture.ViewModel.PreviewThemeCommand.ExecuteAsync(item);
        fixture.Themes.LoadResult = new ThemeValidationResult { Descriptor = changed };

        await fixture.ViewModel.ConfirmThemeCommand.ExecuteAsync(item);

        Assert.AreEqual(0, fixture.Settings.SaveCount);
        Assert.IsTrue(fixture.ViewModel.IsOpen);
        Assert.HasCount(2, fixture.Appearance.Applied);
        Assert.AreEqual("#222222", fixture.Appearance.Applied[1].ThemeColors.Background);
        StringAssert.Contains(fixture.ViewModel.Message, "changed");
    }

    [TestMethod]
    public async Task PreviewTheme_InvalidReload_ShowsDiagnosticAndDoesNotApply()
    {
        var descriptor = Descriptor("invalidated", "old", "#111111");
        var fixture = CreateFixture(descriptor);
        fixture.ViewModel.IsOpen = true;
        var invalid = new ThemeDescriptor
        {
            Name = "Invalid",
            FilePath = descriptor.FilePath,
            Source = descriptor.Source,
            Availability = ThemeAvailability.Invalid
        };
        invalid.Diagnostics.Add(new ThemeDiagnostic { IsError = true, Message = "Invalid theme data" });
        fixture.Themes.LoadResult = new ThemeValidationResult { Descriptor = invalid };

        await fixture.ViewModel.PreviewThemeCommand.ExecuteAsync(fixture.ViewModel.BuiltInThemes[0]);

        Assert.HasCount(0, fixture.Appearance.Applied);
        Assert.IsTrue(fixture.ViewModel.IsMessageError);
        StringAssert.Contains(fixture.ViewModel.Message, "Invalid theme data");
    }

    [TestMethod]
    public async Task ConfirmTheme_SaveFailure_LeavesPreviewOpenAndReportsError()
    {
        var fixture = CreateFixture(Descriptor("failure", "fingerprint", "#111111"));
        fixture.Settings.SaveException = new IOException("disk full");
        fixture.ViewModel.IsOpen = true;
        var item = fixture.ViewModel.BuiltInThemes[0];
        await fixture.ViewModel.PreviewThemeCommand.ExecuteAsync(item);

        await fixture.ViewModel.ConfirmThemeCommand.ExecuteAsync(item);

        Assert.IsTrue(fixture.ViewModel.IsOpen);
        Assert.IsTrue(item.IsPreview);
        Assert.IsTrue(fixture.ViewModel.IsMessageError);
        StringAssert.Contains(fixture.ViewModel.Message, "disk full");
    }

    [TestMethod]
    public void Open_ScanFailure_ReportsErrorWithoutClearingExistingItems()
    {
        var fixture = CreateFixture(Descriptor("existing", "fingerprint", "#111111"));
        fixture.ViewModel.IsOpen = true;
        fixture.ViewModel.IsOpen = false;
        fixture.Themes.ScanException = new IOException("themes unavailable");

        fixture.ViewModel.IsOpen = true;

        Assert.HasCount(1, fixture.ViewModel.BuiltInThemes);
        Assert.IsTrue(fixture.ViewModel.IsMessageError);
        StringAssert.Contains(fixture.ViewModel.Message, "themes unavailable");
    }

    private static Fixture CreateFixture(params ThemeDescriptor[] descriptors)
    {
        var themes = new FakeThemeService(descriptors);
        var settings = new FakeSettingsService();
        var appearance = new RecordingAppearanceService();
        return new Fixture(new ThemePickerViewModel(themes, settings, appearance), themes, settings, appearance);
    }

    private static ThemeDescriptor Descriptor(string id, string fingerprint, string background)
    {
        var appearance = AppearanceSettings.FromSettings(new AppSettings());
        appearance.ThemeColors.Background = background;
        return new ThemeDescriptor
        {
            Key = $"builtin:{id}",
            Id = id,
            Name = id,
            FilePath = $"{id}.ini",
            Source = ThemeSource.BuiltIn,
            Availability = ThemeAvailability.Available,
            Fingerprint = fingerprint,
            Appearance = appearance
        };
    }

    private sealed record Fixture(
        ThemePickerViewModel ViewModel,
        FakeThemeService Themes,
        FakeSettingsService Settings,
        RecordingAppearanceService Appearance);

    private sealed class FakeThemeService(IEnumerable<ThemeDescriptor> descriptors) : IThemeService
    {
        private readonly IReadOnlyList<ThemeDescriptor> _descriptors = descriptors.ToArray();
        public string UserThemeDirectory => "user-themes";
        public Exception? ScanException { get; set; }
        public ThemeValidationResult? LoadResult { get; set; }

        public IReadOnlyList<ThemeDescriptor> ScanThemes()
        {
            if (ScanException != null)
                throw ScanException;
            return _descriptors;
        }

        public ThemeValidationResult LoadTheme(string filePath, ThemeSource source) =>
            LoadResult ?? new ThemeValidationResult
            {
                Descriptor = _descriptors.Single(descriptor => descriptor.FilePath == filePath && descriptor.Source == source)
            };

        public string ComputeFingerprint(AppearanceSettings appearance) => appearance.ThemeColors.Background;
        public void OpenUserThemeDirectory() { }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string ConfigPath => "test-psx.ini";
        public string? StartupWarning => null;
        public int SaveCount { get; private set; }
        public string? SavedThemeKey { get; private set; }
        public string? SavedFingerprint { get; private set; }
        public Exception? SaveException { get; set; }

        public AppSettings GetSettings() => Current;
        public AppSettings ReloadSettings() => Current;
        public void SaveSettings(AppSettings settings) => throw new NotSupportedException();

        public void SaveThemeSettings(AppSettings settings, string activeThemeKey, string themeFingerprint)
        {
            if (SaveException != null)
                throw SaveException;
            SaveCount++;
            SavedThemeKey = activeThemeKey;
            SavedFingerprint = themeFingerprint;
            settings.ActiveThemeKey = activeThemeKey;
            settings.ThemeFingerprint = themeFingerprint;
        }

        public List<ShellProfile> GetProfiles() => [ShellProfile.PowerShell];
        public ShellProfile GetDefaultProfile() => ShellProfile.PowerShell;
    }

    private sealed class RecordingAppearanceService : IAppearanceService
    {
        public List<AppearanceSettings> Applied { get; } = [];

        public Task ApplyAsync(AppearanceSettings appearance)
        {
            Applied.Add(appearance.Clone());
            return Task.CompletedTask;
        }
    }
}
