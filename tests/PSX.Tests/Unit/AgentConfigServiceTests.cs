using System.IO;
using System.Text.Json;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentConfigServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeConfigSource(AgentProviderConfigReport report) : IAgentConfigSource
    {
        public int CollectCount { get; private set; }

        public AgentProviderConfigReport Collect(CancellationToken cancellationToken)
        {
            CollectCount++;
            return report;
        }
    }

    private sealed class StaleThenFreshConfigSource(
        AgentProviderConfigReport stale,
        AgentProviderConfigReport fresh,
        TaskCompletionSource entered,
        Task release) : IAgentConfigSource
    {
        private int _calls;

        public AgentProviderConfigReport Collect(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var report = call == 1 ? stale : fresh;
            if (call == 1)
            {
                entered.TrySetResult();
                release.GetAwaiter().GetResult();
            }

            return report;
        }
    }

    private static AgentProviderConfigReport EmptyReport(string note = "ok") =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            AgentProviderConfigReport.Available,
            [new AgentConfigFact("note", note)],
            [],
            [],
            [],
            []);

    private static TestProvider Provider(
        TestWorkspace workspace,
        string key,
        IAgentConfigSource? configSource) =>
        new(key, key, new CountingRuntime(workspace.Path), [], "agent", usageSource: null, configSource: configSource);

    private static AgentProviderRegistry RegistryWith(
        TestWorkspace workspace,
        params TestProvider[] providers) =>
        new(providers, new AgentProviderOptions { DefaultProviderKey = providers[0].Descriptor.Key });

    [TestMethod]
    public async Task Collect_StampsDescriptorIdentityOntoSourceReports()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_StampsDescriptorIdentityOntoSourceReports));
        var source = new FakeConfigSource(EmptyReport());
        var service = new AgentConfigService(
            RegistryWith(workspace, Provider(workspace, "acp-claude", source)),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        var result = await service.CollectAsync(force: true, CancellationToken.None);

        Assert.HasCount(1, result.Report.Providers);
        Assert.AreEqual("acp-claude", result.Report.Providers[0].ProviderKey);
        Assert.AreEqual("acp-claude", result.Report.Providers[0].DisplayName);
        Assert.AreEqual("agent", result.Report.Providers[0].IconKey);
    }

    [TestMethod]
    public async Task Collect_CachesWithinTtl_AndForceRescans()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CachesWithinTtl_AndForceRescans));
        var source = new FakeConfigSource(EmptyReport("first"));
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var service = new AgentConfigService(
            RegistryWith(workspace, Provider(workspace, "acp-claude", source)),
            clock);

        var first = await service.CollectAsync(force: false, CancellationToken.None);
        var second = await service.CollectAsync(force: false, CancellationToken.None);
        Assert.AreEqual(1, source.CollectCount);
        Assert.AreSame(first, second);

        var forced = await service.CollectAsync(force: true, CancellationToken.None);
        Assert.AreEqual(2, source.CollectCount);
        Assert.AreNotSame(first, forced);
    }

    [TestMethod]
    public async Task Collect_ForceRefresh_DoesNotLetOlderScanOverwriteCache()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ForceRefresh_DoesNotLetOlderScanOverwriteCache));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StaleThenFreshConfigSource(
            EmptyReport("stale"),
            EmptyReport("fresh"),
            entered,
            release.Task);
        var service = new AgentConfigService(
            RegistryWith(workspace, Provider(workspace, "acp-claude", source)));

        var staleTask = service.CollectAsync(force: false, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var freshTask = service.CollectAsync(force: true, CancellationToken.None);
        release.TrySetResult();

        var stale = await staleTask;
        var fresh = await freshTask;
        Assert.AreEqual("stale", stale.Report.Providers[0].Facts[0].Value);
        Assert.AreEqual("fresh", fresh.Report.Providers[0].Facts[0].Value);

        var cached = await service.CollectAsync(force: false, CancellationToken.None);
        Assert.AreEqual("fresh", cached.Report.Providers[0].Facts[0].Value);
    }

    [TestMethod]
    public async Task Collect_SourceException_FoldsToUnavailableWithFixedNote()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SourceException_FoldsToUnavailableWithFixedNote));
        var source = new ThrowingConfigSource();
        var service = new AgentConfigService(
            RegistryWith(workspace, Provider(workspace, "acp-claude", source)));

        var result = await service.CollectAsync(force: true, CancellationToken.None);
        Assert.HasCount(1, result.Report.Providers);
        Assert.AreEqual(AgentProviderConfigReport.Unavailable, result.Report.Providers[0].State);
        CollectionAssert.Contains(
            result.Report.Providers[0].Notes.ToArray(),
            AgentConfigNotes.ParseFailed);
    }

    [TestMethod]
    public async Task Collect_SerializesWithoutCanarySecrets()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SerializesWithoutCanarySecrets));
        const string canary = "CANARY_SERVICE_SECRET_abcdefghijklmnopqrstuvwxyz012345";
        var home = Path.Combine(workspace.Path, ".claude");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"), $$"""
            { "env": { "KEY": "{{canary}}" }, "model": "x" }
            """);
        var source = new ClaudeConfigSource(() => home, () => null);
        var service = new AgentConfigService(
            RegistryWith(workspace, Provider(workspace, "acp-claude", source)));

        var result = await service.CollectAsync(force: true, CancellationToken.None);
        var json = JsonSerializer.Serialize(result);
        Assert.IsFalse(json.Contains(canary, StringComparison.Ordinal));
    }

    private sealed class ThrowingConfigSource : IAgentConfigSource
    {
        public AgentProviderConfigReport Collect(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
