using System.Text.Json;
using System.Diagnostics;
using PSX.Models;
using PSX.Services;
using PSX.Tests.Support;

namespace PSX.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AcpAgentSessionServiceTests
{
    [TestMethod]
    public async Task Prompt_PersistsProtocolUpdatesAndFinishesCleanly()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(Prompt_PersistsProtocolUpdatesAndFinishesCleanly));

        await fixture.Service.SubmitMessageAsync("exercise fake agent");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        CollectionAssert.Contains(thread.Messages.Select(message => message.Role).ToList(), "user");
        CollectionAssert.Contains(thread.Messages.Select(message => message.Role).ToList(), "thinking");
        CollectionAssert.Contains(thread.Messages.Select(message => message.Role).ToList(), "plan");
        CollectionAssert.Contains(thread.Messages.Select(message => message.Role).ToList(), "tool");
        Assert.AreEqual("Fake response completed.", thread.Messages.Single(message => message.Role == "assistant").Text);
        Assert.AreEqual("thinking", thread.Messages[1].Role);
        Assert.AreEqual(thread.Messages[0].RunId, thread.Messages[1].RunId);
        Assert.AreEqual(4321, thread.ContextUsedTokens);
        Assert.AreEqual(100_000, thread.ContextWindowTokens);
        Assert.AreEqual(0.23m, thread.ContextCostAmount);
        Assert.AreEqual("USD", thread.ContextCostCurrency);
        Assert.AreEqual("fake-session-new", thread.AcpSessionId);
        Assert.AreEqual("1.0-test", thread.AdapterVersion);

        var commandEvent = fixture.Bridge.Events.Last(message => EventType(message) == "agent_commands");
        Assert.AreEqual(1, commandEvent.GetProperty("commands").GetArrayLength(), "Duplicate ACP commands must be collapsed.");
    }

    [TestMethod]
    public async Task BooleanConfigOption_AdvertisesCapabilityAndSendsTypedBooleanValue()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(BooleanConfigOption_AdvertisesCapabilityAndSendsTypedBooleanValue));

        await fixture.Service.SubmitMessageAsync("exercise boolean config");
        await fixture.Bridge.WaitForEventAsync("run_finished");
        var initialOptions = await fixture.Bridge.WaitForEventAsync(
            "agent_config_options",
            message => message.GetProperty("options").EnumerateArray()
                .Any(option => option.GetProperty("id").GetString() == "fast_mode"));
        Assert.IsTrue(initialOptions.GetProperty("options").EnumerateArray()
            .Any(option => option.GetProperty("id").GetString() == "fast_mode"));

        fixture.Bridge.RaiseCommand("set_config_option", "fast_mode", booleanValue: true);
        var updatedOptions = await fixture.Bridge.WaitForEventAsync(
            "agent_config_options",
            message => message.GetProperty("options").EnumerateArray()
                .Any(option => option.GetProperty("id").GetString() == "fast_mode"
                    && option.GetProperty("currentValue").ValueKind == JsonValueKind.True));

        Assert.IsTrue(updatedOptions.GetProperty("options").EnumerateArray()
            .Any(option => option.GetProperty("id").GetString() == "fast_mode"
                && option.GetProperty("currentValue").GetBoolean()));
    }

    [TestMethod]
    public async Task ModeTransition_UsesAgentOptionsAndPersistsSelectedSnapshot()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(ModeTransition_UsesAgentOptionsAndPersistsSelectedSnapshot));

        await fixture.Service.SubmitMessageAsync("request permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");

        Assert.AreEqual("mode_transition", permission.GetProperty("presentation").GetString());
        Assert.AreEqual(
            $"# Fake plan{Environment.NewLine}{Environment.NewLine}Implement and verify.",
            permission.GetProperty("documentText").GetString());
        CollectionAssert.AreEqual(
            new[] { "approve", "reject" },
            permission.GetProperty("options").EnumerateArray()
                .Select(option => option.GetProperty("optionId").GetString()).ToArray());

        var requestId = permission.GetProperty("requestId").GetString();
        fixture.Bridge.RaiseCommand("agent_permission_response", requestId, "approve");
        await fixture.Bridge.WaitForEventAsync("permission_resolved");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        var snapshot = thread.Messages.Single(message => message.Role == "mode_transition");
        Assert.AreEqual("selected", snapshot.DecisionState);
        Assert.AreEqual("approve", snapshot.SelectedOptionId);
        Assert.AreEqual(2, snapshot.DecisionOptions?.Count);
        Assert.IsFalse(thread.Messages.Any(message => message.Role == "tool"
            && message.ToolCallId == "tool-mode-transition"));
    }

    [TestMethod]
    public async Task ModeTransition_RejectsAnOptionTheAgentDidNotOffer()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(ModeTransition_RejectsAnOptionTheAgentDidNotOffer));

        await fixture.Service.SubmitMessageAsync("request permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");
        fixture.Bridge.RaiseCommand(
            "agent_permission_response",
            permission.GetProperty("requestId").GetString(),
            "invented-option");

        var cancelled = await fixture.Bridge.WaitForEventAsync("permission_cancelled");
        StringAssert.Contains(cancelled.GetProperty("text").GetString(), "not offered");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var snapshot = fixture.LoadOnlyVisibleThread().Messages.Single(message => message.Role == "mode_transition");
        Assert.AreEqual("cancelled", snapshot.DecisionState);
        Assert.IsNull(snapshot.SelectedOptionId);
    }

    [TestMethod]
    public async Task OrdinaryPermission_WithMarkdownContentRemainsAnOrdinaryPermission()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(OrdinaryPermission_WithMarkdownContentRemainsAnOrdinaryPermission));

        await fixture.Service.SubmitMessageAsync("ordinary permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");

        Assert.AreEqual(JsonValueKind.Null, permission.GetProperty("presentation").ValueKind);
        Assert.AreEqual("execute", permission.GetProperty("toolKind").GetString());
        CollectionAssert.AreEqual(
            new[] { "run-once", "cancel" },
            permission.GetProperty("options").EnumerateArray()
                .Select(option => option.GetProperty("optionId").GetString()).ToArray());
        fixture.Bridge.RaiseCommand(
            "agent_permission_response",
            permission.GetProperty("requestId").GetString(),
            "run-once");
        await fixture.Bridge.WaitForEventAsync("permission_resolved");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.IsFalse(thread.Messages.Any(message => message.Role == "mode_transition"));
        Assert.IsTrue(thread.Messages.Any(message => message.Role == "assistant"
            && message.Text == "Ordinary permission result: run-once"));
    }

    [TestMethod]
    public async Task EmptyOrdinaryPermission_IsCancelledWithoutWaitingForSyntheticOptions()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(EmptyOrdinaryPermission_IsCancelledWithoutWaitingForSyntheticOptions));

        await fixture.Service.SubmitMessageAsync("empty permission");
        var request = await fixture.Bridge.WaitForEventAsync("permission_request");
        Assert.AreEqual(0, request.GetProperty("options").GetArrayLength());
        var cancelled = await fixture.Bridge.WaitForEventAsync("permission_cancelled");
        StringAssert.Contains(cancelled.GetProperty("text").GetString(), "did not provide");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.IsFalse(thread.Messages.Any(message => message.Role == "mode_transition"));
        Assert.IsTrue(thread.Messages.Any(message => message.Role == "assistant"
            && message.Text == "Empty permission result: cancelled"));
    }

    [TestMethod]
    public async Task Cancel_StopsAHangingPromptAndLeavesTransportReusable()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(Cancel_StopsAHangingPromptAndLeavesTransportReusable));

        await fixture.Service.SubmitMessageAsync("hang until cancelled");
        await fixture.Bridge.WaitForEventAsync("thinking_delta");
        await fixture.Service.CancelAsync();
        await fixture.Bridge.WaitForEventAsync("run_finished");

        await fixture.Service.SubmitMessageAsync("run after cancellation");
        await fixture.Bridge.WaitForEventAsync(
            "assistant_message_done",
            timeout: TimeSpan.FromSeconds(10));

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.IsTrue(thread.Messages.Any(message => message.Role == "assistant"
            && message.Text == "Fake response completed."));
    }

    [TestMethod]
    public async Task LoadThread_ReplaysHistoryAndInterruptsPendingModeTransition()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(LoadThread_ReplaysHistoryAndInterruptsPendingModeTransition));
        var historical = fixture.Store.CreateThread(fixture.Workspace.Path);
        historical.Provider = "fake-acp";
        historical.AcpSessionId = "fake-history-session";
        historical.Messages.Add(new AgentMessage
        {
            Role = "mode_transition",
            Name = "Old decision",
            Text = "# Stored proposal",
            RequestId = "stored-request",
            ToolCallId = "stored-tool",
            DecisionState = "pending",
            DecisionOptions = [new AgentDecisionOption { OptionId = "approve", Name = "Approve", Kind = "allow_once" }]
        });
        fixture.Store.SaveThread(historical);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();
        var loaded = await fixture.Bridge.WaitForEventAsync(
            "agent_thread_loaded",
            message => message.GetProperty("threadId").GetString() == historical.ThreadId
                && message.GetProperty("messages").EnumerateArray().Any(item =>
                    item.GetProperty("role").GetString() == "user"
                    && item.GetProperty("text").GetString() == "Historical user"));

        var messages = loaded.GetProperty("messages").EnumerateArray().ToArray();
        Assert.IsTrue(messages.Any(message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("text").GetString() == "Historical user"));
        var thinking = messages.Single(message => message.GetProperty("role").GetString() == "thinking");
        Assert.AreEqual("Historical thought one.Historical thought two.", thinking.GetProperty("text").GetString());
        Assert.AreEqual("thinking", messages[1].GetProperty("role").GetString());
        Assert.AreEqual(
            messages[0].GetProperty("runId").GetString(),
            thinking.GetProperty("runId").GetString());
        CollectionAssert.AreEqual(
            new[] { "Historical assistant before tool.", "Historical assistant after tool." },
            messages.Where(message => message.GetProperty("role").GetString() == "assistant")
                .Select(message => message.GetProperty("text").GetString()).ToArray());
        var snapshot = messages.Single(message => message.GetProperty("role").GetString() == "mode_transition");
        Assert.AreEqual("interrupted", snapshot.GetProperty("decisionState").GetString());
        Assert.IsFalse(messages.Any(message => message.GetProperty("role").GetString() == "tool"
            && message.GetProperty("toolCallId").GetString() == "stored-tool"));

        var persisted = fixture.Store.LoadThread(historical.ThreadId);
        Assert.AreEqual(
            "interrupted",
            persisted?.Messages.Single(message => message.Role == "mode_transition").DecisionState);
    }

    [TestMethod]
    public async Task UnknownProviderThread_RemainsTranscriptOnlyWithoutLaunchingAgent()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(UnknownProviderThread_RemainsTranscriptOnlyWithoutLaunchingAgent));
        var historical = fixture.Store.CreateThread(fixture.Workspace.Path);
        historical.Provider = "unknown-provider";
        historical.AcpSessionId = "must-not-load";
        historical.Messages.Add(new AgentMessage { Role = "assistant", Text = "Local transcript" });
        fixture.Store.SaveThread(historical);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();
        var state = await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("threadId").GetString() == historical.ThreadId
                && message.GetProperty("status").GetString() == "transcript_only");

        Assert.AreEqual("transcript_only", state.GetProperty("status").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Store.RootDirectory, "agent", "acp-logs")));
    }

    [TestMethod]
    public async Task UnknownSlashCommand_IsRejectedBeforeAnAcpSessionStarts()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(UnknownSlashCommand_IsRejectedBeforeAnAcpSessionStarts));

        await fixture.Service.SubmitMessageAsync("/not-a-command");
        var rejected = await fixture.Bridge.WaitForEventAsync("agent_command_rejected");

        Assert.AreEqual("commands_loading", rejected.GetProperty("reason").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Store.RootDirectory, "agent", "acp-logs")));
        Assert.IsEmpty(fixture.Store.ListThreads());
    }

    [TestMethod]
    public void PromptRequestTimeout_IsNullSoLongMultiAgentTurnsAreNeverKilled()
    {
        // The core P0-1 fix: session/prompt carries no wall-clock deadline. A
        // finite value here would kill long multi-agent turns mid-flight.
        Assert.IsNull(AcpAgentSessionService.PromptRequestTimeout);
    }

    [TestMethod]
    public async Task Disconnect_DuringPrompt_PersistsPartialOutputAndEntersRecoveryPending()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(Disconnect_DuringPrompt_PersistsPartialOutputAndEntersRecoveryPending));

        await fixture.Service.SubmitMessageAsync("crash after partial");

        // MF3 + P1-2: a real mid-turn disconnect drives the transport reset and
        // leaves the workspace in recovery_pending rather than a bare error.
        var state = await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "recovery_pending",
            timeout: TimeSpan.FromSeconds(15));

        // P1-1: sessionId falls back to the recovery id instead of blanking out.
        Assert.AreEqual("fake-session-new", state.GetProperty("sessionId").GetString());

        // P0-2: the partial assistant text streamed before the crash is persisted.
        var thread = fixture.LoadOnlyVisibleThread();
        Assert.IsTrue(
            thread.Messages.Any(message => message.Role == "assistant"
                && message.Text == "Partial answer before crash."),
            "Partial assistant output must survive a mid-turn disconnect.");
    }

    [TestMethod]
    public async Task Cancel_WhenAgentIgnoresCancel_ForceResetsWithinInjectedGrace()
    {
        using var fixture = new FakeAcpSessionFixture(
            nameof(Cancel_WhenAgentIgnoresCancel_ForceResetsWithinInjectedGrace),
            scenario: "ignorecancel");
        // Inject a tiny grace so the force-reset fallback fires quickly instead
        // of waiting the full production 5s.
        fixture.Service.CancelGracePeriod = TimeSpan.FromMilliseconds(50);

        await fixture.Service.SubmitMessageAsync("hang until cancelled");
        await fixture.Bridge.WaitForEventAsync("thinking_delta");

        var stopwatch = Stopwatch.StartNew();
        await fixture.Service.CancelAsync();
        await fixture.Bridge.WaitForEventAsync("run_finished");
        stopwatch.Stop();

        Assert.IsTrue(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Force reset must honor the injected grace; took {stopwatch.Elapsed}.");

        // The forced reset killed the transport, so the workspace must report a
        // pending reconnect rather than a misleading "ready" (regression guard for
        // the status overwrite in the cancel finally / tail).
        var recovery = await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "recovery_pending",
            timeout: TimeSpan.FromSeconds(5));
        Assert.AreEqual("recovery_pending", recovery.GetProperty("status").GetString());

        // The transport recovers so the next turn completes normally.
        await fixture.Service.SubmitMessageAsync("run after forced reset");
        await fixture.Bridge.WaitForEventAsync(
            "assistant_message_done",
            timeout: TimeSpan.FromSeconds(15));

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.IsTrue(thread.Messages.Any(message => message.Role == "assistant"
            && message.Text == "Fake response completed."));
    }

    [TestMethod]
    public async Task Cancel_PublishesStoppingStatusImmediatelyWhileTheRunIsStillBusy()
    {
        using var fixture = new FakeAcpSessionFixture(
            nameof(Cancel_PublishesStoppingStatusImmediatelyWhileTheRunIsStillBusy),
            scenario: "ignorecancel");

        await fixture.Service.SubmitMessageAsync("hang until cancelled");
        await fixture.Bridge.WaitForEventAsync("thinking_delta");

        await fixture.Service.CancelAsync();

        // P0-2: the UI must see "stopping" the instant Stop is pressed, before any
        // transport I/O and independent of whether the agent ever acknowledges
        // session/cancel. busy stays true so the live Stop affordance is kept.
        var stopping = await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "stopping",
            timeout: TimeSpan.FromSeconds(5));
        Assert.AreEqual("stopping", stopping.GetProperty("status").GetString());
        Assert.IsTrue(
            stopping.GetProperty("busy").GetBoolean(),
            "The run is still in flight while stopping, so busy must stay true.");
    }

    private static string? EventType(JsonElement message) =>
        message.TryGetProperty("type", out var type) ? type.GetString() : null;
}
