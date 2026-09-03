using System.Globalization;
using System.IO;
using System.Threading;
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
            // Must precede every service resolution: the DSH/Kimi supervisors
            // create ~/.psx subdirectories in their constructors, and the
            // locale migration needs to know whether the install predates
            // this first run.
            PsxInstallState.Record();
            // Apply the PERSISTED preference (not just the install default)
            // before any WPF resource or early MessageBox resolves strings.
            var environmentStore = new PsxEnvironmentSettingsStore(
                PsxInstallState.RootDirectory,
                PsxInstallState.PreExistingInstall
                    ? LocaleDescriptor.UpgradeDefaultMode
                    : LocaleDescriptor.FreshInstallDefaultMode);
            ApplyUiCulture(environmentStore.GetSnapshot().ResolvedLocale);

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
            MessageBox.Show(
                string.Format(
                    CultureInfo.CurrentUICulture,
                    PSX.Properties.Strings.StartupFailedFormat,
                    ex.Message,
                    ex.StackTrace),
                PSX.Properties.Strings.ErrorDialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Set the WPF UI culture so .resx reads (MessageBox, dialogs)
    /// pick the resolved display language. Later switches update this through
    /// the coordinator's LocaleChanged event.</summary>
    private static void ApplyUiCulture(string resolvedLocale)
    {
        var culture = new CultureInfo(resolvedLocale);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private static void ConfigureServices(IServiceCollection services, ISettingsService settingsService)
    {
        // Services
        services.AddSingleton<ISettingsService>(settingsService);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppearanceService, AppearanceService>();
        services.AddSingleton<ConPtyService>();
        services.AddSingleton<ITerminalBridgeService>(sp => new TerminalBridgeService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<RuntimeLocator>(),
            () => sp.GetRequiredService<PsxEnvironmentSettingsCoordinator>().GetSnapshot().ResolvedLocale));
        services.AddSingleton<ITabManagementService, TabManagementService>();
        services.AddSingleton<IAgentBridgeService, AgentBridgeService>();
        services.AddSingleton<IAgentThreadStore, AgentThreadStore>();
        services.AddSingleton<AgentThreadPersistenceCoordinator>();
        services.AddSingleton<IAgentHistoryCatalog, AgentHistoryCatalog>();
        // Local user profile (History dock footer / Usage panel). Plain user
        // data beside the thread store — never part of psx.ini.
        services.AddSingleton(sp => new AgentProfileStore(
            Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "profile")));
        services.AddSingleton(sp => new PsxEnvironmentSettingsStore(
            sp.GetRequiredService<IAgentThreadStore>().RootDirectory,
            // Upgraded installs keep today's Chinese UI; fresh installs follow
            // Windows (Models/LocaleDescriptor.cs).
            PsxInstallState.PreExistingInstall
                ? LocaleDescriptor.UpgradeDefaultMode
                : LocaleDescriptor.FreshInstallDefaultMode));
        services.AddSingleton<PsxEnvironmentSettingsCoordinator>();
        // Local-all usage contributors (every on-machine session, not just
        // PSX-owned ones). The disk cache is disposable, keyed by path hashes,
        // and rooted beside the thread store.
        services.AddSingleton<IAgentLocalUsageContributor>(sp => new KimiLocalUsageContributor(
            cacheStore: new FileAgentUsageCacheStore(
                Path.Combine(
                    sp.GetRequiredService<IAgentThreadStore>().RootDirectory,
                    "usage-cache",
                    "v1"))));
        // DSH sessions are compressed, so extraction goes through the bundled
        // Node script; the locator resolves it lazily (dev builds have no
        // portable Node, and the contributor degrades accordingly).
        services.AddSingleton<IAgentLocalUsageContributor>(sp => new DshLocalUsageContributor(
            extractor: new NodeDshSessionExtractor(
                () => sp.GetRequiredService<RuntimeLocator>().Locate().PortableNodePath,
                () => Path.Combine(
                    sp.GetRequiredService<RuntimeLocator>().Locate().InstallDirectory,
                    "tools",
                    "usage-extractor",
                    "dsh-session-extract.mjs")),
            cacheStore: new FileAgentUsageCacheStore(
                Path.Combine(
                    sp.GetRequiredService<IAgentThreadStore>().RootDirectory,
                    "usage-cache",
                    "v1"))));
        // Global Usage aggregation (thread activity + provider exact-usage
        // sources + local-machine usage contributors). Singleton so its
        // short-TTL cache is shared across requests.
        services.AddSingleton(sp => new AgentUsageService(
            sp.GetRequiredService<IAgentThreadStore>(),
            sp.GetRequiredService<IAgentProviderRegistry>(),
            localContributors: sp.GetServices<IAgentLocalUsageContributor>()));
        // Global Config aggregation for the Usage panel「配置」tab. Singleton
        // so its short-TTL cache is shared across requests. Distinct from live
        // ACP agent_config_options (session Composer).
        services.AddSingleton(sp => new AgentConfigService(
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
        services.AddSingleton<IDshWebWorkspaceCoordinator, DshWebWorkspaceCoordinator>();
        services.AddSingleton<DshWebRuntime>(sp =>
            new DshWebRuntime(
                sp.GetRequiredService<RuntimeLocator>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "dsh")));
        services.AddSingleton<DshWebRuntimeSupervisor>(sp =>
            new DshWebRuntimeSupervisor(
                sp.GetRequiredService<DshWebRuntime>(),
                sp.GetRequiredService<IAgentBridgeService>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "dsh"),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "dsh-workspace"),
                sp.GetRequiredService<PsxEnvironmentSettingsCoordinator>()));
        services.AddSingleton<IKimiWebWorkspaceCoordinator, KimiWebWorkspaceCoordinator>();
        services.AddSingleton<KimiWebRuntimeSupervisor>(sp =>
            new KimiWebRuntimeSupervisor(
                sp.GetRequiredService<KimiCodeAcpRuntime>(),
                sp.GetRequiredService<IAgentBridgeService>(),
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "kimi-web"),
                // Neutral working directory for `kimi web` sessions (the
                // server falls back to process.cwd()); never an internal PSX
                // tree or a user's project directories.
                Path.Combine(sp.GetRequiredService<IAgentThreadStore>().RootDirectory, "kimi-web-workspace")));
        services.AddSingleton<WorkspaceLayoutService>();
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
