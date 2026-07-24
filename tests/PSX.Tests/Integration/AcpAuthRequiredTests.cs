using System.Text.Json;
using PSX.Models;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

/// <summary>
/// Standard ACP terminal-auth recovery (Phase 3). Drives the Fake ACP Agent
/// with deterministic <c>authRequired</c> (-32000) scenarios at the three
/// protocol points that can demand credentials — <c>session/new</c>,
/// <c>session/load</c>, <c>session/prompt</c> — and verifies the session
/// service recovers via a single silent <c>authenticate</c>, and that a
/// persistently failing login stays in the recoverable <c>auth_required</c>
/// state (never a permanent transcript_only degrade, never an infinite retry).
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AcpAuthRequiredTests
{
    [TestMethod]
    public async Task SessionNew_AuthRequired_RecoversAfterSilentAuthenticate()
    {
        using var fixture = new FakeAcpSessionFixture(
            nameof(SessionNew_AuthRequired_RecoversAfterSilentAuthenticate),
            scenario: "auth:new:recover");

        await fixture.Service.SubmitMessageAsync("first message");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.AreEqual(
            "Fake response completed.",
            thread.Messages.Single(message => message.Role == "assistant").Text);
        Assert.AreEqual("fake-session-new", thread.AcpSessionId);
    }

    [TestMethod]
    public async Task SessionPrompt_AuthRequired_RecoversAfterSilentAuthenticate()
    {
        using var fixture = new FakeAcpSessionFixture(
            nameof(SessionPrompt_AuthRequired_RecoversAfterSilentAuthenticate),
            scenario: "auth:prompt:recover");

        await fixture.Service.SubmitMessageAsync("first message");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.AreEqual(
            "Fake response completed.",
            thread.Messages.Single(message => message.Role == "assistant").Text);
    }

    [TestMethod]
    public async Task SessionLoad_AuthRequired_RecoversAndReplaysHistory()
    {
        using var fixture = new FakeAcpSessionFixture(
            nameof(SessionLoad_AuthRequired_RecoversAndReplaysHistory),
            scenario: "auth:load:recover");
        var historical = fixture.Store.CreateThread(fixture.Workspace.Path);
        historical.Provider = "fake-acp";
        historical.AcpSessionId = "fake-history-session";
        historical.Messages.Add(new AgentMessage { Role = "assistant", Text = "Local transcript" });
        fixture.Store.SaveThread(historical);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();

        var loaded = await fixture.Bridge.WaitForEventAsync(
            "agent_thread_loaded",
            message => message.GetProperty("threadId").GetString() == historical.ThreadId
                && message.GetProperty("messages").EnumerateArray().Any(item =>
                    item.GetProperty("role").GetString() == "user"
                    && item.GetProperty("text").GetString() == "Historical user"));

        Assert.IsTrue(loaded.GetProperty("messages").EnumerateArray().Any(item =>
            item.GetProperty("role").GetString() == "assistant"
            && item.GetProperty("text").GetString() == "Historical assistant before tool."));
    }

    [TestMethod]
    public async Task SessionNew_AuthPersistentlyFails_StaysAuthRequiredWithoutCommittingMessage()
    {
        using var fixture = new FakeAcpSessionFixture(
            nameof(SessionNew_AuthPersistentlyFails_StaysAuthRequiredWithoutCommittingMessage),
            scenario: "auth:new:blocked");

        await fixture.Service.SubmitMessageAsync("first message");

        var failure = await fixture.Bridge.WaitForEventAsync("run_failed");
        StringAssert.Contains(failure.GetProperty("text").GetString(), "登录");

        var state = await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "auth_required");
        Assert.AreEqual("auth_required", state.GetProperty("status").GetString());

        // The user message is only committed after the ACP session is established
        // and authenticated. A blocked login must not leave stale unsent history,
        // so the draft thread never gains a user message and stays an invisible
        // empty draft — and it must never degrade to transcript_only.
        Assert.IsEmpty(fixture.Store.ListThreads());
        Assert.AreNotEqual("transcript_only", state.GetProperty("status").GetString());
    }
}
