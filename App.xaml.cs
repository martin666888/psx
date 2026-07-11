using System.IO;
using System.Windows;
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
            AppearanceService.ApplyWpf(AppearanceSettings.FromSettings(settings));

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
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppearanceService, AppearanceService>();
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
        services.AddTransient<ThemePickerViewModel>();

        // Views
        services.AddTransient<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
