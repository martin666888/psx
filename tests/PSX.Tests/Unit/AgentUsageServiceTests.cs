using System.IO;
using System.Text.Json;
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

    private sealed class FakeUsageSource(
        IReadOnlyList<AgentUsageRecord> records,
        Func<IReadOnlyCollection<string>, AgentUsageSourceStatus>? statusFactory = null) : IAgentUsageSource
    {
        public int CollectCount { get; private set; }

        public AgentUsageSourceStatus Collect(
            IReadOnlyCollection<string> sessionIds,
            IAgentUsageRecordSink sink,
            CancellationToken cancellationToken)
        {
            CollectCount++;
            foreach (var record in records)
                sink.Add(record);
            return statusFactory?.Invoke(sessionIds) ?? Status(
                AgentUsageSourceStatus.Available,
                expectedSessions: sessionIds.Count,
                matchedSessions: sessionIds.Count);
        }
    }

    private static AgentUsageSourceStatus Status(
        string status,
        int skippedFiles = 0,
        int badLines = 0,
        int? expectedSessions = 0,
        int? matchedSessions = 0) =>
        new(
            "internal-test-source",
            status,
            1,
            skippedFiles,
            badLines,
            expectedSessions,
            matchedSessions,
            "test",
            DateTimeOffset.UnixEpoch,
            null);

    private static AgentThreadStore NewStore(TestWorkspace workspace) =>
        new(Path.Combine(workspace.Path, "store"));

    private static AgentThread AddThread(
        AgentThreadStore store,
        string providerKey,
        string? sessionId)
    {
        var thread = store.CreateThread("C:/work");
        thread.Provider = providerKey;
        thread.ClaudeSessionId = sessionId;
        store.SaveThread(thread);
        return thread;
    }

    private static TestProvider Provider(
        TestWorkspace workspace,
        string key,
        IAgentUsageSource? usageSource) =>
        new(key, key, new CountingRuntime(workspace.Path), [], "agent", usageSource);

    private static AgentProviderRegistry RegistryWith(
        TestWorkspace workspace,
        IAgentUsageSource? usageSource,
        string key = "acp-claude") =>
        RegistryWithProviders(workspace, Provider(workspace, key, usageSource));

    private static AgentProviderRegistry RegistryWithProviders(
        TestWorkspace workspace,
        params TestProvider[] providers) =>
        new(providers, new AgentProviderOptions { DefaultProviderKey = providers[0].Descriptor.Key });

    [TestMethod]
    public async Task Collect_ReadsAllThreadFilesBeyondIndexCap()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ReadsAllThreadFilesBeyondIndexCap));
        var store = NewStore(workspace);
        for (var index = 0; index < 105; index++)
            AddThread(store, "acp-claude", null);

        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, null),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Unavailable, result.Completeness.Status);
        Assert.AreEqual(105, result.Completeness.UntrackedThreads);
    }

    [TestMethod]
    public async Task Collect_CorruptThreadFileWithoutKnownThreads_IsPartial()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CorruptThreadFileWithoutKnownThreads_IsPartial));
        var store = NewStore(workspace);
        var threadsDirectory = Path.Combine(store.RootDirectory, "agent", "threads");
        Directory.CreateDirectory(threadsDirectory);
        File.WriteAllText(Path.Combine(threadsDirectory, "corrupt.json"), "{ not json");

        var service = new AgentUsageService(store, RegistryWith(workspace, new FakeUsageSource([])));
        var result = await service.CollectAsync(force: true, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Partial, result.Completeness.Status);
        Assert.IsGreaterThanOrEqualTo(1, result.Completeness.SkippedFiles);
        Assert.AreEqual(0, result.Report.Today.TotalTokens);
    }

    [TestMethod]
    public async Task Collect_DailyAndWindowTotalsUseTheSameTokenFormula()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_DailyAndWindowTotalsUseTheSameTokenFormula));
        var store = NewStore(workspace);
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        AddThread(store, "acp-claude", "psx-session");
        var records = new List<AgentUsageRecord>
        {
            new(now, "internal-model", 1, 2, 3, 4),
            new(now.AddDays(-1), "internal-model", 10, 20, 30, 40),
            new(now.AddDays(-6), "internal-model", 2, 3, 4, 5),
            new(now.AddDays(-20), "internal-model", 5, 6, 7, 8),
            new(now.AddDays(-40), "internal-model", 9, 9, 9, 9)
        };
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, new FakeUsageSource(records)),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.HasCount(AgentUsageService.HeatmapDays, result.Report.DailyTokens);
        Assert.AreEqual(10, result.Report.DailyTokens[^1]);
        Assert.AreEqual(100, result.Report.DailyTokens[^2]);
        Assert.AreEqual(14, result.Report.DailyTokens[^7]);
        Assert.AreEqual(26, result.Report.DailyTokens[^21]);
        Assert.AreEqual(10, result.Report.Today.TotalTokens);
        Assert.AreEqual(124, result.Report.Last7Days.TotalTokens);
        Assert.AreEqual(150, result.Report.Last30Days.TotalTokens);
        Assert.AreEqual(AgentUsageCompleteness.Available, result.Completeness.Status);
        Assert.HasCount(1, result.Report.Providers);
        Assert.AreEqual("acp-claude", result.Report.Providers[0].ProviderKey);
        Assert.AreEqual(124, result.Report.Providers[0].Last7Days.TotalTokens);
    }

    [TestMethod]
    public async Task Collect_UsesReportTimeZoneForDailyAndWindowBucketing()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_UsesReportTimeZoneForDailyAndWindowBucketing));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "psx-session");
        var utcNow = new DateTimeOffset(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus13", TimeSpan.FromHours(13), "plus13", "plus13");
        var records = new[]
        {
            new AgentUsageRecord(
                new DateTimeOffset(2026, 7, 19, 23, 0, 0, TimeSpan.Zero),
                "internal-model",
                7,
                3,
                0,
                0)
        };
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, new FakeUsageSource(records)),
            new FixedTimeProvider(utcNow, zone));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(10, result.Report.Today.TotalTokens);
        Assert.AreEqual(10, result.Report.DailyTokens[^1]);
        Assert.AreEqual(zone.Id, result.Timezone);
    }

    [TestMethod]
    public async Task Collect_NoThreadsAndNoDamage_IsAvailableZero()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_NoThreadsAndNoDamage_IsAvailableZero));
        var store = NewStore(workspace);
        var service = new AgentUsageService(store, RegistryWith(workspace, new FakeUsageSource([])));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Available, result.Completeness.Status);
        Assert.AreEqual(0, result.Report.Today.TotalTokens);
        Assert.IsTrue(result.Report.DailyTokens.All(value => value == 0));
    }

    [TestMethod]
    public async Task Collect_ProviderWithoutExactSource_IsUnavailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ProviderWithoutExactSource_IsUnavailable));
        var store = NewStore(workspace);
        AddThread(store, "acp-kimi", "kimi-session");
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, null, "acp-kimi"));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Unavailable, result.Completeness.Status);
        Assert.AreEqual(1, result.Completeness.UntrackedThreads);
        Assert.AreEqual(0, result.Report.Today.TotalTokens);
    }

    [TestMethod]
    public async Task Collect_OnlyKimiWithExactFixture_IsAvailable()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_OnlyKimiWithExactFixture_IsAvailable));
        var store = NewStore(workspace);
        var thread = store.CreateThread("C:/work");
        thread.Provider = "acp-kimi";
        thread.AcpSessionId = "psx-kimi-fixture";
        store.SaveThread(thread);
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "KimiUsage",
            "0.29.1");
        var source = new KimiCodeSessionUsageSource(() => fixtureRoot);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1784522400400)
            .AddHours(1);
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, source, "acp-kimi"),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Available, result.Completeness.Status);
        Assert.AreEqual(110, result.Report.Today.TotalTokens);
        Assert.HasCount(1, result.Report.Providers);
        Assert.AreEqual("acp-kimi", result.Report.Providers[0].ProviderKey);
        Assert.AreEqual(
            AgentUsageCompleteness.Available,
            result.Report.Providers[0].Completeness.Status);
    }

    [TestMethod]
    public async Task Collect_ExactAndUntrackedProviders_IsPartial()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_ExactAndUntrackedProviders_IsPartial));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "claude-session");
        AddThread(store, "acp-kimi", "kimi-session");
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var claudeSource = new FakeUsageSource(
            [new AgentUsageRecord(now, "internal-model", 4, 6, 0, 0)]);
        var registry = RegistryWithProviders(
            workspace,
            Provider(workspace, "acp-claude", claudeSource),
            Provider(workspace, "acp-kimi", null));
        var service = new AgentUsageService(
            store,
            registry,
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Partial, result.Completeness.Status);
        Assert.AreEqual(1, result.Completeness.UntrackedThreads);
        Assert.AreEqual(10, result.Report.Today.TotalTokens);
        Assert.HasCount(2, result.Report.Providers);
        Assert.AreEqual(
            AgentUsageCompleteness.Available,
            result.Report.Providers[0].Completeness.Status);
        Assert.AreEqual(
            AgentUsageCompleteness.Unavailable,
            result.Report.Providers[1].Completeness.Status);
    }

    [TestMethod]
    public async Task Collect_MissingSessionCounts_NullsBothFoldedCounts()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_MissingSessionCounts_NullsBothFoldedCounts));
        var store = NewStore(workspace);
        AddThread(store, "provider-one", "one");
        AddThread(store, "provider-two", "two");
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var counted = new FakeUsageSource(
            [new AgentUsageRecord(now, "internal-model", 1, 0, 0, 0)]);
        var uncounted = new FakeUsageSource(
            [],
            _ => Status(
                AgentUsageSourceStatus.Available,
                expectedSessions: null,
                matchedSessions: null));
        var registry = RegistryWithProviders(
            workspace,
            Provider(workspace, "provider-one", counted),
            Provider(workspace, "provider-two", uncounted));
        var service = new AgentUsageService(
            store,
            registry,
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.IsNull(result.Completeness.ExpectedSessions);
        Assert.IsNull(result.Completeness.MatchedSessions);
        Assert.AreEqual(AgentUsageCompleteness.Available, result.Completeness.Status);
    }

    [TestMethod]
    public async Task Collect_SourceGapAndSessionMismatch_ArePartial()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_SourceGapAndSessionMismatch_ArePartial));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "one");
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var source = new FakeUsageSource(
            [new AgentUsageRecord(now, "internal-model", 1, 0, 0, 0)],
            _ => Status(
                AgentUsageSourceStatus.Partial,
                skippedFiles: 2,
                badLines: 3,
                expectedSessions: 70,
                matchedSessions: 35));
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, source),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        Assert.AreEqual(AgentUsageCompleteness.Partial, result.Completeness.Status);
        Assert.AreEqual(70, result.Completeness.ExpectedSessions);
        Assert.AreEqual(35, result.Completeness.MatchedSessions);
        Assert.AreEqual(2, result.Completeness.SkippedFiles);
        Assert.AreEqual(3, result.Completeness.BadLines);
    }

    [TestMethod]
    public async Task Collect_PartialProvider_IncludesEverySuccessfullyParsedExactRow()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_PartialProvider_IncludesEverySuccessfullyParsedExactRow));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "complete-session");
        AddThread(store, "acp-claude", "damaged-session");
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var source = new FakeUsageSource(
            [
                new AgentUsageRecord(now, "internal-model", 10, 0, 0, 0),
                new AgentUsageRecord(now, "internal-model", 20, 0, 0, 0)
            ],
            _ => Status(
                AgentUsageSourceStatus.Partial,
                badLines: 1,
                expectedSessions: 2,
                matchedSessions: 1));
        var service = new AgentUsageService(
            store,
            RegistryWith(workspace, source),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);

        var provider = result.Report.Providers.Single();
        Assert.AreEqual(AgentUsageCompleteness.Partial, provider.Completeness.Status);
        Assert.AreEqual(30, provider.Today.TotalTokens);
        CollectionAssert.Contains(
            provider.Completeness.Reasons.ToArray(),
            AgentUsageGapReason.UnmatchedSessions);
        Assert.AreEqual(
            AgentUsageCompleteness.Partial,
            result.Completeness.Status);
        Assert.AreEqual(30, result.Report.Today.TotalTokens);
    }

    [TestMethod]
    public async Task Collect_CachesWithinTtl_AndForceRescans()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_CachesWithinTtl_AndForceRescans));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "psx-session");
        var source = new FakeUsageSource([]);
        var service = new AgentUsageService(store, RegistryWith(workspace, source));

        await service.CollectAsync(force: false, CancellationToken.None);
        await service.CollectAsync(force: false, CancellationToken.None);
        Assert.AreEqual(1, source.CollectCount, "cached result must not re-scan");

        await service.CollectAsync(force: true, CancellationToken.None);
        Assert.AreEqual(2, source.CollectCount, "force must re-scan");
    }

    [TestMethod]
    public async Task Collect_PublicJsonOmitsInternalUsageDimensions()
    {
        using var workspace = TestWorkspace.Create(nameof(Collect_PublicJsonOmitsInternalUsageDimensions));
        var store = NewStore(workspace);
        AddThread(store, "acp-claude", "psx-session");
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var service = new AgentUsageService(
            store,
            RegistryWith(
                workspace,
                new FakeUsageSource(
                    [new AgentUsageRecord(now, "secret-internal-model", 1, 2, 3, 4)])),
            new FixedTimeProvider(now, TimeZoneInfo.Utc));

        var result = await service.CollectAsync(force: false, CancellationToken.None);
        var json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        foreach (var forbidden in new[]
                 {
                     "title",
                     "model",
                     "contextSnapshots",
                     "cacheHitRate",
                     "activeThreads",
                     "sources",
                     "parserVersion",
                     "internal-test-source"
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
        StringAssert.Contains(json, "\"providerKey\":\"acp-claude\"");
        StringAssert.Contains(json, "\"iconKey\":\"agent\"");
    }
}
