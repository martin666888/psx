using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Support;

internal sealed class FakeAcpSessionFixture : IDisposable
{
    public FakeAcpSessionFixture(string scope, string? scenario = null)
    {
        Workspace = TestWorkspace.Create(scope);
        Store = new AgentThreadStore(Path.Combine(Workspace.Path, "store"));
        Bridge = new RecordingAgentBridgeService();
        Runtime = new FakeAcpRuntime(Workspace, scenario: scenario);
        Provider = new FakeAcpProvider(Runtime);
        Registry = new FakeAgentProviderRegistry(Provider);
        RuntimeCoordinator = new AgentRuntimeCoordinator(Registry);
        var thread = Store.CreateThread(Workspace.Path);
        thread.Provider = Provider.Descriptor.Key;
        Store.SaveThread(thread);
        Service = new AcpAgentSessionService(
            Guid.NewGuid(),
            Bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            Store,
            new NullAgentDirectoryPicker(),
            Registry,
            Provider,
            thread,
            RuntimeCoordinator);
    }

    public TestWorkspace Workspace { get; }
    public AgentThreadStore Store { get; }
    public RecordingAgentBridgeService Bridge { get; }
    public FakeAcpRuntime Runtime { get; }
    public FakeAcpProvider Provider { get; }
    public FakeAgentProviderRegistry Registry { get; }
    public AgentRuntimeCoordinator RuntimeCoordinator { get; }
    public AcpAgentSessionService Service { get; private set; }

    public void BindToThread(AgentThread thread)
    {
        Service.Dispose();
        Service = new AcpAgentSessionService(
            Guid.NewGuid(),
            Bridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            Store,
            new NullAgentDirectoryPicker(),
            Registry,
            Provider,
            thread,
            RuntimeCoordinator);
    }

    public AgentThread LoadOnlyVisibleThread()
    {
        var summary = Store.ListThreads().Single();
        return Store.LoadThread(summary.ThreadId)
            ?? throw new AssertFailedException("The Agent thread was not persisted.");
    }

    public void Dispose()
    {
        Service.Dispose();
        Workspace.Dispose();
    }
}

internal sealed class RecordingAgentBridgeService : IAgentBridgeService
{
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly SemaphoreSlim _eventSignal = new(0);

    public event EventHandler<AgentSubmitEventArgs>? UserMessageSubmitted;
    public event EventHandler<AgentCommandEventArgs>? CommandReceived;
    public event EventHandler<AgentAttachmentUploadEventArgs>? AttachmentUploadReceived;

    public IReadOnlyList<JsonElement> Events => _events.ToArray();

    public Task InitializeAsync(WebView2 webView) => Task.CompletedTask;

    public Task SendEventAsync(object message)
    {
        _events.Enqueue(JsonSerializer.SerializeToElement(message));
        _eventSignal.Release();
        return Task.CompletedTask;
    }

    public void RaiseCommand(
        string command,
        string? requestId = null,
        string? value = null,
        Guid? workspaceId = null,
        bool? booleanValue = null)
    {
        CommandReceived?.Invoke(this, new AgentCommandEventArgs
        {
            WorkspaceId = workspaceId ?? Guid.Empty,
            Command = command,
            RequestId = requestId,
            Value = value,
            BooleanValue = booleanValue
        });
    }

    public async Task<JsonElement> WaitForEventAsync(
        string type,
        Func<JsonElement, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var match = _events.FirstOrDefault(candidate =>
                candidate.TryGetProperty("type", out var eventType)
                && eventType.GetString() == type
                && (predicate == null || predicate(candidate)));
            if (match.ValueKind != JsonValueKind.Undefined)
                return match.Clone();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                Assert.Fail($"Timed out waiting for bridge event '{type}'.");

            await _eventSignal.WaitAsync(remaining).ConfigureAwait(false);
        }
    }

    public void Submit(string text, Guid? workspaceId = null) =>
        UserMessageSubmitted?.Invoke(this, new AgentSubmitEventArgs
        {
            WorkspaceId = workspaceId ?? Guid.Empty,
            Text = text
        });

    public void Upload(AgentAttachmentUploadEventArgs args) =>
        AttachmentUploadReceived?.Invoke(this, args);
}

internal sealed class FakeAcpRuntime(TestWorkspace workspace, bool initiallyReady = true, string? scenario = null) : IAcpAgentRuntime
{
    private bool _ready = initiallyReady;

    public event Action<string>? StatusChanged;

    public string LogPath => Path.Combine(workspace.Path, "runtime.log");

    public bool SupportsSelfUpdate { get; set; } = true;

    /// <summary>Result kind reported by <see cref="RefreshAsync"/>.</summary>
    public AcpRuntimeOperationKind RefreshResultKind { get; set; } = AcpRuntimeOperationKind.AlreadyReady;

    /// <summary>Non-null: a refresh stages this version (HasPendingUpdate).</summary>
    public string? StagedVersion { get; set; }

    private bool _refreshed;

    public bool IsReady() => _ready;

    public RuntimeVersionSnapshot GetVersionSnapshot() =>
        new(
            CurrentVersion: "1.0.0-fake",
            PendingVersion: _refreshed ? StagedVersion : null,
            HasPendingUpdate: _refreshed && StagedVersion != null)
        {
            ProductName = "Fake Agent",
            TechnicalDetails = "ACP adapter 9.9.9-fake"
        };

    public string BuildStatusText(string? suffix = null) =>
        suffix ?? (_ready ? "Fake ACP runtime ready." : "Fake ACP runtime is not installed.");

    public Task<AcpRuntimeOperationResult> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alreadyReady = _ready;
        _ready = true;
        StatusChanged?.Invoke(BuildStatusText());
        return Task.FromResult(new AcpRuntimeOperationResult(
            alreadyReady ? AcpRuntimeOperationKind.AlreadyReady : AcpRuntimeOperationKind.Success,
            "Fake ACP runtime ready."));
    }

    public Task<AcpRuntimeOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        _refreshed = true;
        return Task.FromResult(new AcpRuntimeOperationResult(RefreshResultKind, "Fake ACP refresh finished."));
    }

    public Task PrepareForStartupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public AcpProcessSpec CreateProcessSpec(string workingDirectory) => workspace.CreateTestAgentSpec(scenario);

    public void Dispose() { }

    public void PublishStatus(string status) => StatusChanged?.Invoke(status);

    public void SetReady(bool ready, string? status = null)
    {
        _ready = ready;
        StatusChanged?.Invoke(status ?? BuildStatusText());
    }
}

internal sealed class FakeAcpProvider(
    IAcpAgentRuntime runtime,
    string key = "fake-acp",
    string displayName = "Fake ACP",
    string assistantName = "Fake") : IAcpAgentProvider
{
    public AgentDescriptor Descriptor { get; } = new(
        key,
        displayName,
        assistantName,
        string.Equals(key, "fake-acp", StringComparison.OrdinalIgnoreCase)
            ? ["fake-legacy"]
            : [key + "-legacy"]);

    public IAcpAgentRuntime Runtime { get; } = runtime;

    public AcpClientCapabilityProfile ClientCapabilities { get; set; } = new()
    {
        FileSystemReadText = true,
        FileSystemWriteText = true,
        Terminal = true,
        SessionBooleanConfig = true,
        ElicitationFormUrl = true,
        TerminalOutputMeta = true
    };

    public object CreateNewSessionParameters(string workingDirectory) => new { cwd = workingDirectory };

    public object CreateRestoreSessionParameters(string sessionId, string workingDirectory) =>
        new { sessionId, cwd = workingDirectory };

    public bool IsCommandVisible(string normalizedCommand) => true;

    public ShellProfile? CreateNativeTerminalProfile(string workingDirectory, string? sessionId) => null;
}

internal sealed class FakeAgentProviderRegistry(FakeAcpProvider provider) : IAgentProviderRegistry
{
    public IAcpAgentProvider DefaultProvider => provider;
    public IReadOnlyList<IAcpAgentProvider> Providers { get; } = [provider];

    public IAcpAgentProvider? Find(string? providerKey) =>
        string.Equals(providerKey, provider.Descriptor.Key, StringComparison.OrdinalIgnoreCase)
        || provider.Descriptor.LegacyKeys.Contains(providerKey ?? "", StringComparer.OrdinalIgnoreCase)
            ? provider
            : null;
}

internal sealed class NullAgentDirectoryPicker : IAgentDirectoryPicker
{
    public string? PickDirectory(string initialDirectory) => null;
}

internal sealed class NullTabManagementService : ITabManagementService
{
    public event EventHandler<TabCreatedEventArgs>? TabCreated { add { } remove { } }
    public event EventHandler<TabClosedEventArgs>? TabClosed { add { } remove { } }
    public event EventHandler<TabTitleChangedEventArgs>? TabTitleChanged { add { } remove { } }
    public event EventHandler<string>? PaneFocusRequested { add { } remove { } }
    public event EventHandler<PaneRatiosEventArgs>? PaneRatiosRequested { add { } remove { } }
    public event EventHandler<PaneMoveEventArgs>? PaneMoveRequested { add { } remove { } }
    public event EventHandler<WorkspaceLayoutIntentEventArgs>? WorkspaceLayoutIntentRequested { add { } remove { } }
    public event EventHandler<WorkspaceCreateEventArgs>? WorkspaceCreateRequested { add { } remove { } }
    public event EventHandler<DshCommandEventArgs>? DshCommandRequested { add { } remove { } }

    public Task<Guid> CreateTabAsync(ShellProfile? profile = null) => Task.FromResult(Guid.NewGuid());
    public Task CloseTabAsync(Guid sessionId) => Task.CompletedTask;
    public Task SwitchTabAsync(Guid sessionId) => Task.CompletedTask;
    public Task ResizeTabAsync(Guid sessionId, int cols, int rows) => Task.CompletedTask;
    public TerminalSession? GetSession(Guid sessionId) => null;
    public Task ShutdownAsync(TimeSpan? timeout = null) => Task.CompletedTask;
}

internal sealed class NullTerminalBridgeService : ITerminalBridgeService
{
    public event EventHandler<TerminalInputEventArgs>? InputReceived { add { } remove { } }
    public event EventHandler<TerminalResizeEventArgs>? ResizeRequested { add { } remove { } }
    public event EventHandler<TerminalTitleEventArgs>? TitleChanged { add { } remove { } }
    public event EventHandler<string>? ViewModeChanged { add { } remove { } }
    public event EventHandler? FrontendReady { add { } remove { } }
    public event EventHandler<string>? PaneFocusRequested { add { } remove { } }
    public event EventHandler<PaneRatiosEventArgs>? PaneRatiosRequested { add { } remove { } }
    public event EventHandler<PaneMoveEventArgs>? PaneMoveRequested { add { } remove { } }
    public event EventHandler<WorkspaceLayoutIntentEventArgs>? WorkspaceLayoutIntentRequested { add { } remove { } }
    public event EventHandler<WorkspaceCreateEventArgs>? WorkspaceCreateRequested { add { } remove { } }
    public event EventHandler<DshCommandEventArgs>? DshCommandRequested { add { } remove { } }
    public event EventHandler<ThemeActionEventArgs>? ThemeActionRequested { add { } remove { } }

    public Task InitializeAsync(WebView2 webView) => Task.CompletedTask;
    public Task CreateTerminalAsync(Guid sessionId) => Task.CompletedTask;
    public Task SendOutputAsync(Guid sessionId, string base64Data) => Task.CompletedTask;
    public Task SwitchTerminalAsync(Guid sessionId) => Task.CompletedTask;
    public Task CloseTerminalAsync(Guid sessionId) => Task.CompletedTask;
    public Task ResizeTerminalAsync(Guid sessionId, int cols, int rows) => Task.CompletedTask;
    public Task SetViewModeAsync(string mode) => Task.CompletedTask;
    public Task SendAppearanceAsync(AppearanceSettings appearance) => Task.CompletedTask;
}
