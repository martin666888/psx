using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using PSX.Models;
using PSX.Services;
using PSX.ViewModels;

namespace PSX;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var settingsService = new SettingsService();
            var settings = settingsService.GetSettings();
            ApplyThemeResources(settings);

            var services = new ServiceCollection();
            ConfigureServices(services, settingsService);
            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}", "PSX 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services, ISettingsService settingsService)
    {
        // Services
        services.AddSingleton<ISettingsService>(settingsService);
        services.AddSingleton<ConPtyService>();
        services.AddSingleton<ITerminalBridgeService, TerminalBridgeService>();
        services.AddSingleton<ITabManagementService, TabManagementService>();
        services.AddSingleton<IAgentBridgeService, AgentBridgeService>();
        services.AddSingleton<IAgentThreadStore, AgentThreadStore>();
        services.AddSingleton<IAgentDirectoryPicker, WpfAgentDirectoryPicker>();

        // Runtime / ACP install pipeline. RuntimeLocator is pure and cheap;
        // AcpRuntimeManager depends on AgentThreadStore for its log directory
        // (so logs land in the same %USERPROFILE%/.psx/ tree as thread data).
        services.AddSingleton<RuntimeLocator>();
        services.AddSingleton<AcpRuntimeManager>(sp =>
            new AcpRuntimeManager(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "agent", "acp-logs")));
        services.AddSingleton<RuntimePreflightService>();

        services.AddSingleton<IAgentSessionService, AcpAgentSessionService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Views
        services.AddTransient<MainWindow>();
    }

    private static void ApplyThemeResources(AppSettings settings)
    {
        var t = settings.ThemeColors;
        var res = Current.Resources;

        SetBrush(res, "WindowBackgroundBrush", t.Background);
        SetBrush(res, "SurfaceBrush", t.Surface);
        SetBrush(res, "SurfaceRaisedBrush", t.SurfaceRaised);
        SetBrush(res, "SurfaceMutedBrush", t.SurfaceMuted);
        SetBrush(res, "HoverBrush", t.Hover);
        SetBrush(res, "BorderBrush", t.Border);
        SetBrush(res, "BorderStrongBrush", t.BorderStrong);
        SetBrush(res, "TextPrimaryBrush", t.Text);
        SetBrush(res, "TextSecondaryBrush", t.TextMuted);
        SetBrush(res, "TextDimBrush", t.TextDim);
        SetBrush(res, "AccentBrush", t.Accent);
        SetBrush(res, "AccentHoverBrush", t.AccentHover);
        SetBrush(res, "ErrorBrush", t.Error);
        SetBrush(res, "ErrorBgBrush", t.ErrorBg);
        SetBrush(res, "WarningBrush", t.Warning);
        SetBrush(res, "WarningBgBrush", t.WarningBg);
        SetBrush(res, "ScrollbarBrush", t.Scrollbar);
        SetBrush(res, "ScrollbarHoverBrush", t.ScrollbarHover);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string colorHex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            resources[key] = new SolidColorBrush(color);
        }
        catch
        {
            // 非法颜色值，跳过，保留 XAML 默认值
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
