using System.IO;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentUsageServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class FakeUsageSource(IReadOnlyList<AgentUsageRecord> records) : IAgentUsageSource
    {
        public int CollectCount { get; private set; }

        public AgentUsageContribution Collect(
            IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken)
        {
            CollectCount++;
            return new AgentUsageContribution(
                records,
                new AgentUsageSourceStatus(
                    "acp-claude", AgentUsageSourceStatus.Available,
                    records.Count, 0, 0, sessionIds.Count, sessionIds.Count, "1",
                    DateTimeOffset.UnixEpoch, null));
        }
    }

    private static AgentThreadStore NewStore(TestWorkspace workspace) =>
        new(Path.Combine(workspace.Path, "store"));

    private static AgentThread AddThread(
        AgentThreadStore store, string providerKey, string? claudeSessionId,
        long? contextUsed, params DateTimeOffset[] userMessageStamps)
    {
        var thread = store.CreateThread("C:/work");
        thread.Provider = providerKey;
        thread.ClaudeSessionId = claudeSessionId;
        thread.ContextUsedTokens = contextUsed;
        foreach (var stamp in userMessageStamps)
            thread.Messages.Add(new AgentMessage { Role = "user", Text = "hi", CreatedAt = stamp });
        store.SaveThread(thread);
        return thread;
    }

    private static AgentProviderRegistry RegistryWith(
        TestWorkspace workspace, IAgentUsageSource? usageSource, string key = "acp-claude")
    {
        var provider = new TestProvider(key, "Test", new CountingRuntime(workspace.Path), [], "claude", usageSource);
        return new AgentProviderRegistry([provider], new AgentProviderOptions { DefaultProviderKey = key });
    }

    [TestMethod]
    public async Task Collect_ReadsAllThreadFilesBeyondIndexCap()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ReadsAllThreadFilesBeyondIndexCap));
        var store = NewStore(workspace);
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 105; i++)
            AddThread(store, "acp-claude", null, null, now.AddHours(-i % 5));

        var service = new AgentUsageService(store, RegistryWith(workspace, null),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));
        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(105, result.Report.Last30Days.ActiveThreads);
    }

    [TestMethod]
    public async Task Collect_CorruptThreadFile_MarksPsxThreadsPartial()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptThreadFile_MarksPsxThreadsPartial));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", null, null, DateTimeOffset.UtcNow);
        var threadsDir = Path.Combine(store.RootDirectory, "agent", "threads");
        File.WriteAllText(Path.Combine(threadsDir, "corrupt.json"), "{ not json");

        var service = new AgentUsageService(store, RegistryWith(workspace, null));
        var result = await service.CollectAsync(force: true, CancellationToken.None);

        var psx = result.Sources.Single(s => s.Key == AgentUsageService.PsxThreadsSourceKey);
        Assert.AreEqual(AgentUsageSourceStatus.Partial, psx.Status);
        Assert.IsGreaterThanOrEqualTo(1, psx.SkippedFiles);
    }

    [TestMethod]
    public async Task Collect_WindowActiveThreads_DeduplicatesAcrossDays()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_WindowActiveThreads_DeduplicatesAcrossDays));
        var store = NewStore(workspace);
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        // One thread active on two consecutive days within the 7-day window.
        AddThread(store, "acp-claude", null, null, now.AddDays(-1), now);

        var service = new AgentUsageService(store, RegistryWith(workspace, null),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));
        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(1, result.Report.Last7Days.ActiveThreads);
        Assert.AreEqual(2, result.Report.Last7Days.Turns);
    }

    [TestMethod]
    public async Task Collect_ModelRows_ChangePerWindow()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ModelRows_ChangePerWindow));
        var store = NewStore(workspace);
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        AddThread(store, "acp-claude", "psx-session", null, now);

        var records = new List<AgentUsageRecord>
        {
            new(now, "model-today", 100, 10, 0, 0),
            new(now.AddDays(-20), "model-old", 200, 20, 0, 0)
        };
        var service = new AgentUsageService(store, RegistryWith(workspace, new FakeUsageSource(records)),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));
        var result = await service.CollectAsync(force: false, CancellationToken.None);

        var todayRows = result.Report.Today.ProviderSections.Single().ExactUsage!.ModelRows;
        Assert.HasCount(1, todayRows);
        Assert.AreEqual("model-today", todayRows[0].Model);

        var monthRows = result.Report.Last30Days.ProviderSections.Single().ExactUsage!.ModelRows;
        Assert.HasCount(2, monthRows);
        Assert.AreEqual(300, result.Report.Last30Days.Tokens.Input);
    }

    [TestMethod]
    public async Task Collect_ContextSnapshotsNeverEnterTotals()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ContextSnapshotsNeverEnterTotals));
        var store = NewStore(workspace);
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        // Provider WITHOUT a usage source (Kimi-like), thread carries a context
        // snapshot only.
        AddThread(store, "acp-kimi", null, contextUsed: 4321, now);

        var registry = new AgentProviderRegistry(
            [new TestProvider("acp-kimi", "Kimi", new CountingRuntime(workspace.Path), [], "kimi")],
            new AgentProviderOptions { DefaultProviderKey = "acp-kimi" });
        var service = new AgentUsageService(store, registry, new FixedTimeProvider(now, TimeZoneInfo.Utc));
        var result = await service.CollectAsync(force: false, CancellationToken.None);

        var section = result.Report.Today.ProviderSections.Single();
        Assert.IsNull(section.ExactUsage);
        Assert.IsNotNull(section.ContextSnapshots);
        Assert.AreEqual(4321, section.ContextSnapshots!.Single().UsedTokens);
        Assert.AreEqual(AgentUsageTokens.Zero, result.Report.Today.Tokens);
        Assert.IsNull(result.Report.Today.CacheHitRate);
    }

    [TestMethod]
    public async Task Collect_UsesLocalTimeZoneForWindowBucketing()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UsesLocalTimeZoneForWindowBucketing));
        var store = NewStore(workspace);
        // now (UTC) = 2026-07-20T01:00Z; local +13 => 2026-07-20 14:00 local.
        var utcNow = new DateTimeOffset(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus13", TimeSpan.FromHours(13), "plus13", "plus13");
        // A message at 2026-07-19T23:00Z is 2026-07-20 12:00 local => "today".
        AddThread(store, "acp-claude", null, null,
            new DateTimeOffset(2026, 7, 19, 23, 0, 0, TimeSpan.Zero));

        var service = new AgentUsageService(store, RegistryWith(workspace, null), new FixedTimeProvider(utcNow, zone));
        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(1, result.Report.Today.ActiveThreads);
        Assert.AreEqual(zone.Id, result.Timezone);
    }

    [TestMethod]
    public async Task Collect_CachesWithinTtl_ForcesRescan()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CachesWithinTtl_ForcesRescan));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "psx-session", null, DateTimeOffset.UtcNow);
        var source = new FakeUsageSource([]);
        var service = new AgentUsageService(store, RegistryWith(workspace, source));

        await service.CollectAsync(force: false, CancellationToken.None);
        await service.CollectAsync(force: false, CancellationToken.None);
        Assert.AreEqual(1, source.CollectCount, "cached result must not re-scan");

        await service.CollectAsync(force: true, CancellationToken.None);
        Assert.AreEqual(2, source.CollectCount, "force must re-scan");
    }

    [TestMethod]
    public async Task Collect_HeatmapSpansConfiguredWindow()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_HeatmapSpansConfiguredWindow));
        var store = NewStore(workspace);
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        AddThread(store, "acp-claude", null, null, now, now.AddDays(-1));

        var service = new AgentUsageService(store, RegistryWith(workspace, null),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));
        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.HasCount(AgentUsageService.HeatmapDays, result.Report.Heatmap);
        Assert.AreEqual(2, result.Report.Heatmap.Count(count => count > 0));
    }
}
