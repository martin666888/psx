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
        services.AddSingleton<IAgentHistoryCatalog, AgentHistoryCatalog>();
        // Local user profile (History dock footer / Usage panel). Plain user
        // data beside the thread store — never part of psx.ini.
        services.AddSingleton(sp => new AgentProfileStore(
            Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "profile")));
        // Global Usage aggregation (thread activity + provider exact-usage
        // sources). Singleton so its short-TTL cache is shared across requests.
        services.AddSingleton(sp => new AgentUsageService(
            sp.GetRequiredService<IAgentThreadStore>(),
            sp.GetRequiredService<IAgentProviderRegistry>()));
        services.AddSingleton<IAgentDirectoryPicker, WpfAgentDirectoryPicker>();

        // Runtime / ACP install pipeline. RuntimeLocator remains shared by
        // Terminal/WebView2 and the concrete Claude runtime implementation.
        // (so logs land in the same %USERPROFILE%/.psx/ tree as thread data).
        services.AddSingleton<RuntimeLocator>();
        services.AddSingleton<AcpRuntimeManager>(sp =>
            new AcpRuntimeManager(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "agent", "acp-logs")));
        services.AddSingleton<ClaudeAcpAgentProvider>();
        services.AddSingleton<IAcpAgentProvider>(sp => sp.GetRequiredService<ClaudeAcpAgentProvider>());

        // Kimi Code is MIT-licensed and pre-installed into tools/kimi/ at build
        // time, so it uses its own bundled runtime (no npm ci / promote). Its
        // logs land next to the ACP logs. Registering the provider is enough
        // for it to appear in the New Agent menu / workspace creation / history
        // filter / provider catalog. Default provider stays Claude.
        services.AddSingleton<KimiCodeAcpRuntime>(sp =>
            new KimiCodeAcpRuntime(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "agent", "acp-logs")));
        services.AddSingleton<KimiCodeAcpAgentProvider>();
        services.AddSingleton<IAcpAgentProvider>(sp => sp.GetRequiredService<KimiCodeAcpAgentProvider>());

        // Qwen Code is Apache-2.0 and pre-installed into tools/qwen/ at build
        // time, so it uses its own bundled runtime (no npm ci / promote). Its
        // logs land next to the ACP logs. Registering the provider is enough
        // for it to appear in the New Agent menu / workspace creation / history
        // filter / provider catalog. Default provider stays Claude.
        services.AddSingleton<QwenCodeAcpRuntime>(sp =>
            new QwenCodeAcpRuntime(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "agent", "acp-logs")));
        services.AddSingleton<QwenCodeAcpAgentProvider>();
        services.AddSingleton<IAcpAgentProvider>(sp => sp.GetRequiredService<QwenCodeAcpAgentProvider>());

        // Qoder CLI is Claude-style managed: the release ships tools/qoder-seed
        // only; user confirmation installs into runtime/qoder-current. Logs land
        // next to the ACP logs.
        services.AddSingleton<QoderCliAcpRuntime>(sp =>
            new QoderCliAcpRuntime(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "agent", "acp-logs")));
        services.AddSingleton<QoderCliAcpAgentProvider>();
        services.AddSingleton<IAcpAgentProvider>(sp => sp.GetRequiredService<QoderCliAcpAgentProvider>());

        // OpenCode is MIT-licensed and pre-installed into tools/opencode/ at
        // build time (a native Bun-compiled binary from the platform package,
        // not the opencode-ai wrapper), so it uses its own bundled runtime
        // with the same current/next self-update model as Kimi/Qwen. Machines
        // without AVX2 install the baseline variant into
        // runtime/opencode-current after user confirmation instead.
        services.AddSingleton<OpencodeAcpRuntime>(sp =>
            new OpencodeAcpRuntime(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "agent", "acp-logs")));
        services.AddSingleton<OpencodeAcpAgentProvider>();
        services.AddSingleton<IAcpAgentProvider>(sp => sp.GetRequiredService<OpencodeAcpAgentProvider>());
        services.AddSingleton(new AgentProviderOptions { DefaultProviderKey = "acp-claude" });
        services.AddSingleton<IAgentProviderRegistry, AgentProviderRegistry>();
        services.AddSingleton<IAgentRuntimeCoordinator, AgentRuntimeCoordinator>();
        services.AddSingleton<RuntimePreflightService>();

        services.AddSingleton<IAgentWorkspaceFactory, AgentWorkspaceFactory>();
        services.AddSingleton<IAgentWorkspaceCoordinator, AgentWorkspaceCoordinator>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();

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
