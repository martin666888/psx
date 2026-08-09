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
    public async Task CheckRuntimeUpdate_NoNewerVersion_PublishesCheckingThenUpToDate()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(CheckRuntimeUpdate_NoNewerVersion_PublishesCheckingThenUpToDate));

        fixture.Bridge.RaiseCommand("check_runtime_update");

        var final = await fixture.Bridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "up_to_date");
        Assert.AreEqual("1.0.0-fake", final.GetProperty("currentVersion").GetString());
        // The bridge carries the toolbar-facing product label and the
        // tooltip-only technical detail alongside the raw version pair.
        Assert.AreEqual("Fake Agent v1.0.0-fake", final.GetProperty("versionLabel").GetString());
        Assert.AreEqual("ACP adapter 9.9.9-fake", final.GetProperty("versionDetail").GetString());
        var states = fixture.Bridge.Events
            .Where(message => EventType(message) == "runtime_update_status")
            .Select(message => message.GetProperty("state").GetString())
            .ToList();
        CollectionAssert.Contains(states, "checking");
        Assert.AreEqual("up_to_date", states.Last());
    }

    [TestMethod]
    public async Task CheckRuntimeUpdate_BroadcastsToEveryWorkspaceSharingTheRuntime()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(CheckRuntimeUpdate_BroadcastsToEveryWorkspaceSharingTheRuntime));
        var passiveBridge = new RecordingAgentBridgeService();
        var passiveThread = fixture.Store.CreateThread(fixture.Workspace.Path);
        passiveThread.Provider = fixture.Provider.Descriptor.Key;
        fixture.Store.SaveThread(passiveThread);
        using var passiveService = new AcpAgentSessionService(
            Guid.NewGuid(),
            passiveBridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            fixture.Store,
            new NullAgentDirectoryPicker(),
            fixture.Registry,
            fixture.Provider,
            passiveThread,
            fixture.RuntimeCoordinator);

        fixture.Bridge.RaiseCommand("check_runtime_update");

        await fixture.Bridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "up_to_date");
        // The workspace that never clicked Update mirrors the same lifecycle
        // through the coordinator broadcast.
        await passiveBridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "checking");
        await passiveBridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "up_to_date");
    }

    [TestMethod]
    public async Task RuntimeUpdateFailure_SnapshotReachesWorkspacesCreatedLater()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(RuntimeUpdateFailure_SnapshotReachesWorkspacesCreatedLater));
        fixture.Runtime.RefreshResultKind = AcpRuntimeOperationKind.Failed;

        fixture.Bridge.RaiseCommand("check_runtime_update");
        await fixture.Bridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "failed");

        // A workspace created after the failed run must read the coordinator
        // snapshot (state and message) instead of defaulting back to idle.
        var lateBridge = new RecordingAgentBridgeService();
        var lateThread = fixture.Store.CreateThread(fixture.Workspace.Path);
        lateThread.Provider = fixture.Provider.Descriptor.Key;
        fixture.Store.SaveThread(lateThread);
        using var lateService = new AcpAgentSessionService(
            Guid.NewGuid(),
            lateBridge,
            new NullTabManagementService(),
            new NullTerminalBridgeService(),
            fixture.Store,
            new NullAgentDirectoryPicker(),
            fixture.Registry,
            fixture.Provider,
            lateThread,
            fixture.RuntimeCoordinator);

        lateBridge.RaiseCommand("state");
        var snapshot = await lateBridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "failed");
        Assert.AreEqual("Fake ACP refresh finished.", snapshot.GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task CheckRuntimeUpdate_StagedUpdate_PublishesRestartRequired()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(CheckRuntimeUpdate_StagedUpdate_PublishesRestartRequired));
        fixture.Runtime.RefreshResultKind = AcpRuntimeOperationKind.Success;
        fixture.Runtime.StagedVersion = "2.0.0-fake";

        fixture.Bridge.RaiseCommand("check_runtime_update");

        var staged = await fixture.Bridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "staged_restart_required");
        Assert.AreEqual("2.0.0-fake", staged.GetProperty("pendingVersion").GetString());
    }

    [TestMethod]
    public async Task CheckRuntimeUpdate_RuntimeNotInstalled_PublishesInstallRequiredWithoutRefreshing()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(CheckRuntimeUpdate_RuntimeNotInstalled_PublishesInstallRequiredWithoutRefreshing));
        fixture.Runtime.SetReady(false);

        fixture.Bridge.RaiseCommand("check_runtime_update");

        await fixture.Bridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "install_required");
        Assert.IsFalse(fixture.Bridge.Events.Any(message =>
            EventType(message) == "runtime_update_status"
            && message.GetProperty("state").GetString() is "checking" or "up_to_date"));
    }

    [TestMethod]
    public async Task CheckRuntimeUpdate_BundledRuntime_PublishesUnsupportedWithoutRefreshing()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(CheckRuntimeUpdate_BundledRuntime_PublishesUnsupportedWithoutRefreshing));
        fixture.Runtime.SupportsSelfUpdate = false;

        fixture.Bridge.RaiseCommand("check_runtime_update");

        await fixture.Bridge.WaitForEventAsync(
            "runtime_update_status",
            message => message.GetProperty("state").GetString() == "unsupported");
        Assert.IsFalse(fixture.Bridge.Events.Any(message =>
            EventType(message) == "runtime_update_status"
            && message.GetProperty("state").GetString() == "checking"));
    }

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
        var toolEventCountBeforeResolution = fixture.Bridge.Events.Count(message =>
            IsToolLifecycleEvent(message, "tool-mode-transition"));

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
        Assert.AreEqual(toolEventCountBeforeResolution, fixture.Bridge.Events.Count(message =>
            IsToolLifecycleEvent(message, "tool-mode-transition")),
            "Mode-transition updates after the permission request must not re-enter the ordinary tool lifecycle.");
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
    public async Task OrdinaryPermission_WithSingleLineContentUsesOrdinaryDescriptionWithoutDocumentSnapshot()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(OrdinaryPermission_WithSingleLineContentUsesOrdinaryDescriptionWithoutDocumentSnapshot));

        await fixture.Service.SubmitMessageAsync("ordinary permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");

        Assert.AreEqual(JsonValueKind.Null, permission.GetProperty("presentation").ValueKind);
        Assert.AreEqual("execute", permission.GetProperty("toolKind").GetString());
        Assert.AreEqual("# Markdown command details", permission.GetProperty("description").GetString());
        Assert.AreEqual(JsonValueKind.Null, permission.GetProperty("documentText").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, permission.GetProperty("text").ValueKind);
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
        Assert.IsFalse(thread.Messages.Any(message => message.Role == "document_permission"));
        Assert.IsTrue(thread.Messages.Any(message => message.Role == "assistant"
            && message.Text == "Ordinary permission result: run-once"));
    }

    [TestMethod]
    public async Task DocumentPermission_WithoutKindRendersMarkdownAndPersistsSelectedSnapshot()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(DocumentPermission_WithoutKindRendersMarkdownAndPersistsSelectedSnapshot));

        await fixture.Service.SubmitMessageAsync("document permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");

        Assert.AreEqual("document", permission.GetProperty("presentation").GetString());
        Assert.AreEqual("ExitPlanMode", permission.GetProperty("title").GetString());
        Assert.AreEqual(
            "# Kimi plan\n\n1. Review the request\n2. Implement the change\n\nPlan saved for approval.",
            (permission.GetProperty("documentText").GetString() ?? "").Replace("\r\n", "\n"));
        Assert.AreEqual(JsonValueKind.Null, permission.GetProperty("text").ValueKind);
        CollectionAssert.AreEqual(
            new[] { "approve", "revise", "reject" },
            permission.GetProperty("options").EnumerateArray()
                .Select(option => option.GetProperty("optionId").GetString()).ToArray());
        var toolEventCountBeforeResolution = fixture.Bridge.Events.Count(message =>
            IsToolLifecycleEvent(message, "tool-document-permission"));

        fixture.Bridge.RaiseCommand(
            "agent_permission_response",
            permission.GetProperty("requestId").GetString(),
            "approve");
        await fixture.Bridge.WaitForEventAsync("permission_resolved");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        var snapshot = thread.Messages.Single(message => message.Role == "document_permission");
        Assert.AreEqual("selected", snapshot.DecisionState);
        Assert.AreEqual("approve", snapshot.SelectedOptionId);
        Assert.AreEqual(3, snapshot.DecisionOptions?.Count);
        Assert.IsFalse(thread.Messages.Any(message => message.Role == "tool"
            && message.ToolCallId == "tool-document-permission"));
        Assert.AreEqual(toolEventCountBeforeResolution, fixture.Bridge.Events.Count(message =>
            IsToolLifecycleEvent(message, "tool-document-permission")),
            "Document-decision updates after the permission request must not re-enter the ordinary tool lifecycle.");
    }

    [TestMethod]
    public async Task DocumentPermission_IsCancelledAndPersistedWhenTheRunStops()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(DocumentPermission_IsCancelledAndPersistedWhenTheRunStops));

        await fixture.Service.SubmitMessageAsync("document permission");
        await fixture.Bridge.WaitForEventAsync("permission_request");
        await fixture.Service.CancelAsync();
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var snapshot = fixture.LoadOnlyVisibleThread().Messages
            .Single(message => message.Role == "document_permission");
        Assert.AreEqual("cancelled", snapshot.DecisionState);
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
    public async Task ToolMerger_PrefersContentOverDifferentlyFormattedRawOutput()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(ToolMerger_PrefersContentOverDifferentlyFormattedRawOutput));

        await fixture.Service.SubmitMessageAsync("merge content rawoutput");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var tool = fixture.LoadOnlyVisibleThread().Messages.Single(message =>
            message.Role == "tool" && message.ToolCallId == "tool-merge-content");
        Assert.AreEqual("```ts\n1| const x = 1;\n```", (tool.ToolOutput ?? "").Replace("\r\n", "\n"));
        Assert.IsFalse(
            (tool.ToolOutput ?? "").Contains("const x = 1;\nconst x = 1;", StringComparison.Ordinal)
            || (tool.ToolOutput ?? "").Contains("```\nconst x = 1;", StringComparison.Ordinal),
            "rawOutput must not be concatenated when content is present");
        StringAssert.Contains(tool.ToolInput ?? "", "src/a.ts");
    }

    [TestMethod]
    public async Task ToolMerger_HoldsPendingJsonUntilRawInputThenPersistsFinalInput()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(ToolMerger_HoldsPendingJsonUntilRawInputThenPersistsFinalInput));

        await fixture.Service.SubmitMessageAsync("merge pending params");
        await fixture.Bridge.WaitForEventAsync(
            "tool_updated",
            message => message.TryGetProperty("input", out var input)
                       && (input.GetString() ?? "").Contains("a.ts", StringComparison.Ordinal)
                       && (input.GetString() ?? "").Contains("final", StringComparison.Ordinal));
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var tool = fixture.LoadOnlyVisibleThread().Messages.Single(message =>
            message.Role == "tool" && message.ToolCallId == "tool-merge-pending");
        StringAssert.Contains(tool.ToolInput ?? "", "a.ts");
        StringAssert.Contains(tool.ToolInput ?? "", "final");
        Assert.AreEqual("Wrote a.ts", tool.ToolOutput);
        Assert.IsFalse(
            (tool.ToolOutput ?? "").Contains("partial", StringComparison.Ordinal),
            "pending JSON parameter snapshots must not become the final output");
    }

    [TestMethod]
    public async Task ToolMerger_FinalContentSnapshotOverwritesTerminalDelta()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(ToolMerger_FinalContentSnapshotOverwritesTerminalDelta));

        await fixture.Service.SubmitMessageAsync("merge terminal snapshot");
        await fixture.Bridge.WaitForEventAsync(
            "tool_delta",
            message => message.GetProperty("toolCallId").GetString() == "tool-merge-terminal");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var tool = fixture.LoadOnlyVisibleThread().Messages.Single(message =>
            message.Role == "tool" && message.ToolCallId == "tool-merge-terminal");
        Assert.AreEqual("final snapshot", tool.ToolOutput);
        Assert.IsFalse(
            (tool.ToolOutput ?? "").Contains("partial line", StringComparison.Ordinal),
            "terminal deltas must be replaced by a later content snapshot");
        StringAssert.Contains(tool.ToolInput ?? "", "echo hi");
    }

    [TestMethod]
    public async Task ToolMerger_TitleOnlyUpdateReplacesNameAndEmitsToolUpdated()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(ToolMerger_TitleOnlyUpdateReplacesNameAndEmitsToolUpdated));

        await fixture.Service.SubmitMessageAsync("merge title update");
        var updated = await fixture.Bridge.WaitForEventAsync(
            "tool_updated",
            message => message.GetProperty("toolCallId").GetString() == "tool-merge-title"
                       && message.GetProperty("name").GetString() == "Updated Title");
        Assert.AreEqual("Updated Title", updated.GetProperty("summary").GetString());
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var tool = fixture.LoadOnlyVisibleThread().Messages.Single(message =>
            message.Role == "tool" && message.ToolCallId == "tool-merge-title");
        Assert.AreEqual("Updated Title", tool.Name);
        Assert.AreEqual("Updated Title", tool.Summary);
    }

    [TestMethod]
    public async Task DiffOnlyPermission_RendersDocumentCardWithReadableDiffBody()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(DiffOnlyPermission_RendersDocumentCardWithReadableDiffBody));

        await fixture.Service.SubmitMessageAsync("diff permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");

        Assert.AreEqual("document", permission.GetProperty("presentation").GetString());
        var documentText = (permission.GetProperty("documentText").GetString() ?? "").Replace("\r\n", "\n");
        Assert.IsFalse(string.IsNullOrWhiteSpace(documentText));
        StringAssert.Contains(documentText, "### src/App.cs");
        StringAssert.Contains(documentText, "-old line");
        StringAssert.Contains(documentText, "+new line");

        fixture.Bridge.RaiseCommand(
            "agent_permission_response",
            permission.GetProperty("requestId").GetString(),
            "approve");
        await fixture.Bridge.WaitForEventAsync("permission_resolved");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        var snapshot = thread.Messages.Single(message => message.Role == "document_permission");
        Assert.AreEqual("selected", snapshot.DecisionState);
        Assert.AreEqual("approve", snapshot.SelectedOptionId);
        Assert.IsFalse(thread.Messages.Any(message => message.Role == "tool"
            && message.ToolCallId == "tool-diff-permission"));
    }

    [TestMethod]
    public async Task AskUserPermission_LiftsQuestionsIntoFormAndReturnsAnswers()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(AskUserPermission_LiftsQuestionsIntoFormAndReturnsAnswers));

        await fixture.Service.SubmitMessageAsync("ask user permission");
        var permission = await fixture.Bridge.WaitForEventAsync("permission_request");

        Assert.AreEqual("form", permission.GetProperty("presentation").GetString());
        Assert.AreEqual("proceed_once", permission.GetProperty("formSubmitOptionId").GetString());
        Assert.AreEqual(JsonValueKind.Null, permission.GetProperty("text").ValueKind);
        Assert.IsTrue(permission.TryGetProperty("schema", out var schema));
        Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("q0", out _));
        Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("q0_other", out _));
        Assert.AreEqual(0, schema.GetProperty("required").GetArrayLength());

        var requestId = permission.GetProperty("requestId").GetString();
        fixture.Bridge.RaiseCommand(
            "agent_permission_response",
            requestId,
            """{"optionId":"proceed_once","content":{"q0":"Scenic"}}""");
        await fixture.Bridge.WaitForEventAsync("permission_resolved");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        var thread = fixture.LoadOnlyVisibleThread();
        Assert.IsTrue(thread.Messages.Any(message => message.Role == "assistant"
            && message.Text == "Ask user result: proceed_once; answers=0=Scenic"));
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
    public async Task LoadThread_KeepsLocalTranscriptAndInterruptsPendingModeTransition()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(LoadThread_KeepsLocalTranscriptAndInterruptsPendingModeTransition));
        var historical = fixture.Store.CreateThread(fixture.Workspace.Path);
        historical.Provider = "fake-acp";
        historical.AcpSessionId = "fake-history-session";
        historical.Messages.Add(new AgentMessage { Role = "user", Text = "Local user", RunId = "run-1" });
        historical.Messages.Add(new AgentMessage { Role = "thinking", Text = "Local thought", RunId = "run-1" });
        historical.Messages.Add(new AgentMessage
        {
            Role = "tool",
            Name = "Bash",
            RunId = "run-1",
            ToolCallId = "local-tool",
            ToolOutput = "out",
            ToolStatus = "completed",
            Summary = "run ls"
        });
        historical.Messages.Add(new AgentMessage { Role = "assistant", Text = "Local answer", RunId = "run-1" });
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
        historical.Messages.Add(new AgentMessage
        {
            Role = "document_permission",
            Name = "ExitPlanMode",
            Text = "# Stored Kimi plan",
            RequestId = "stored-document-request",
            ToolCallId = "stored-document-tool",
            DecisionState = "pending",
            DecisionOptions = [new AgentDecisionOption { OptionId = "approve", Name = "Approve", Kind = "allow_once" }]
        });
        fixture.Store.SaveThread(historical);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();

        // The locally saved transcript is authoritative: the ACP replay only
        // restores the agent-side session, and the pending decision flips to
        // interrupted because it can no longer be answered.
        var loaded = await fixture.Bridge.WaitForEventAsync(
            "agent_thread_loaded",
                message => message.GetProperty("threadId").GetString() == historical.ThreadId
                && message.GetProperty("messages").EnumerateArray().Any(item =>
                    item.GetProperty("role").GetString() == "document_permission"
                    && item.GetProperty("decisionState").GetString() == "interrupted"));

        var messages = loaded.GetProperty("messages").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[] { "user", "thinking", "tool", "assistant", "mode_transition", "document_permission" },
            messages.Select(message => message.GetProperty("role").GetString()).ToArray(),
            "the aggregated local transcript must survive the ACP replay untouched");
        Assert.AreEqual("Local answer", messages.Single(message => message.GetProperty("role").GetString() == "assistant").GetProperty("text").GetString());
        Assert.IsFalse(messages.Any(message => (message.GetProperty("text").GetString() ?? "").Contains("Historical")),
            "replayed chunks must never overwrite the local archive");

        await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "restored");
        Assert.IsFalse(fixture.Bridge.Events.Any(message =>
            EventType(message) == "command_result"
            && (message.GetProperty("text").GetString() ?? "").Contains("restored")),
            "restore status must not be written into the conversation timeline");

        var persisted = fixture.Store.LoadThread(historical.ThreadId);
        Assert.AreEqual(
            "interrupted",
            persisted?.Messages.Single(message => message.Role == "mode_transition").DecisionState);
        Assert.AreEqual(
            "interrupted",
            persisted?.Messages.Single(message => message.Role == "document_permission").DecisionState);
        Assert.AreEqual(6, persisted?.Messages.Count, "no replayed messages may be appended to the archive");
    }

    [TestMethod]
    public async Task Restore_LoadWithLocalTranscript_KeepsControlUpdatesWhileDroppingReplay()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(Restore_LoadWithLocalTranscript_KeepsControlUpdatesWhileDroppingReplay));
        var historical = CreateRestorableThread(fixture);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();

        await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "restored");

        // Control updates interleaved with the replay must survive even though
        // the content chunks are dropped: commands, then usage.
        await fixture.Bridge.WaitForEventAsync(
            "agent_commands",
            message => message.GetProperty("commands").EnumerateArray()
                .Any(command => command.GetProperty("name").GetString() == "/replayed"));
        await fixture.Bridge.WaitForEventAsync(
            "agent_usage_update",
            message => message.GetProperty("contextUsedTokens").GetInt64() == 777);

        // The archive is untouched and the control state was persisted by the
        // single unified save when the restore request completed.
        var persisted = fixture.Store.LoadThread(historical.ThreadId);
        Assert.AreEqual(2, persisted?.Messages.Count);
        Assert.IsFalse(persisted!.Messages.Any(message => (message.Text ?? "").Contains("Historical")));
        Assert.AreEqual(777L, persisted.ContextUsedTokens!.Value);
    }

    [TestMethod]
    public async Task Restore_WithResumeCapability_UsesSessionResumeAndKeepsControlUpdates()
    {
        // The "resume" scenario answers session/load with an error, so a
        // successful restore proves the client picked session/resume.
        using var fixture = new FakeAcpSessionFixture(
            nameof(Restore_WithResumeCapability_UsesSessionResumeAndKeepsControlUpdates),
            scenario: "resume");
        var historical = CreateRestorableThread(fixture);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();

        await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "restored");
        await fixture.Bridge.WaitForEventAsync(
            "agent_commands",
            message => message.GetProperty("commands").EnumerateArray()
                .Any(command => command.GetProperty("name").GetString() == "/resumed"));
        await fixture.Bridge.WaitForEventAsync(
            "agent_usage_update",
            message => message.GetProperty("contextUsedTokens").GetInt64() == 888);

        var persisted = fixture.Store.LoadThread(historical.ThreadId);
        Assert.AreEqual(2, persisted?.Messages.Count, "resume must not replay or append any history");
        Assert.AreEqual(888L, persisted!.ContextUsedTokens!.Value);
    }

    [TestMethod]
    public async Task Restore_ResumeAdvertisedButNotImplemented_FallsBackToSessionLoadOnce()
    {
        // The agent advertises sessionCapabilities.resume but answers
        // session/resume with -32601: only this exact error may fall back to
        // session/load, which then completes the restore in ControlOnly mode.
        using var fixture = new FakeAcpSessionFixture(
            nameof(Restore_ResumeAdvertisedButNotImplemented_FallsBackToSessionLoadOnce),
            scenario: "resume:notfound");
        var historical = CreateRestorableThread(fixture);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();

        await fixture.Bridge.WaitForEventAsync(
            "agent_state",
            message => message.GetProperty("status").GetString() == "restored");
        await fixture.Bridge.WaitForEventAsync(
            "agent_commands",
            message => message.GetProperty("commands").EnumerateArray()
                .Any(command => command.GetProperty("name").GetString() == "/replayed"));

        var persisted = fixture.Store.LoadThread(historical.ThreadId);
        Assert.AreEqual(2, persisted?.Messages.Count);
        Assert.IsFalse(persisted!.Messages.Any(message => (message.Text ?? "").Contains("Historical")));
    }

    private static AgentThread CreateRestorableThread(FakeAcpSessionFixture fixture)
    {
        var historical = fixture.Store.CreateThread(fixture.Workspace.Path);
        historical.Provider = "fake-acp";
        historical.AcpSessionId = "fake-history-session";
        historical.Messages.Add(new AgentMessage { Role = "user", Text = "Local user", RunId = "run-1" });
        historical.Messages.Add(new AgentMessage { Role = "assistant", Text = "Local answer", RunId = "run-1" });
        fixture.Store.SaveThread(historical);
        return historical;
    }

    [TestMethod]
    public async Task LoadThread_EmptyLocalTranscript_RebuildsAggregatedHistory()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(LoadThread_EmptyLocalTranscript_RebuildsAggregatedHistory));
        var historical = fixture.Store.CreateThread(fixture.Workspace.Path);
        historical.Provider = "fake-acp";
        historical.AcpSessionId = "fake-history-session";
        fixture.Store.SaveThread(historical);

        fixture.BindToThread(historical);
        await fixture.Service.RestoreAsync();

        var loaded = await fixture.Bridge.WaitForEventAsync(
            "agent_thread_loaded",
            message => message.GetProperty("threadId").GetString() == historical.ThreadId
                && message.GetProperty("messages").EnumerateArray().Any(item =>
                    item.GetProperty("role").GetString() == "user"
                    && item.GetProperty("text").GetString() == "Historical user"));

        // The rebuilt transcript must match the live persistence shape:
        // user → thinking (combined) → tools → assistant (single message per
        // turn), never assistant fragments interleaved with tool rows.
        var messages = loaded.GetProperty("messages").EnumerateArray().ToArray();
        var roles = messages.Select(message => message.GetProperty("role").GetString()).ToArray();
        Assert.AreEqual("user", roles[0]);
        Assert.AreEqual("thinking", roles[1]);
        var thinking = messages[1];
        Assert.AreEqual("Historical thought one.Historical thought two.", thinking.GetProperty("text").GetString());
        Assert.AreEqual(
            messages[0].GetProperty("runId").GetString(),
            thinking.GetProperty("runId").GetString());

        var assistants = messages
            .Where(message => message.GetProperty("role").GetString() == "assistant")
            .Select(message => message.GetProperty("text").GetString())
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "Historical assistant before tool.Historical assistant after tool." },
            assistants,
            "the turn's assistant text folds into one message even around tool calls");

        var toolIndex = Array.IndexOf(roles, "tool");
        var assistantIndex = Array.FindIndex(roles, role => role == "assistant");
        Assert.IsTrue(toolIndex >= 0 && toolIndex < assistantIndex,
            "tools sit between thinking and the assistant text inside the turn");

        var persisted = fixture.Store.LoadThread(historical.ThreadId);
        Assert.AreEqual(messages.Length, persisted?.Messages.Count);
    }

    [TestMethod]
    public async Task PlanUpdate_WithoutEntries_ReachesFrontendAsDocument()
    {
        using var fixture = new FakeAcpSessionFixture(nameof(PlanUpdate_WithoutEntries_ReachesFrontendAsDocument));

        await fixture.Service.SubmitMessageAsync("send the plan document please");
        await fixture.Bridge.WaitForEventAsync("run_finished");

        // Entry-less plan updates used to be dropped wholesale; they must now
        // surface as a document-mode plan with readable text (never raw JSON).
        var plan = await fixture.Bridge.WaitForEventAsync(
            "plan_update",
            message => (message.GetProperty("text").GetString() ?? "").Contains("Refactor first"));
        var planText = plan.GetProperty("text").GetString() ?? "";
        Assert.AreEqual(0, plan.GetProperty("entries").GetArrayLength());
        StringAssert.Contains(planText, "## Approach");
        Assert.DoesNotContain("sessionUpdate", planText,
            "document text must be extracted from content blocks, not raw JSON");

        var thread = fixture.LoadOnlyVisibleThread();
        var planMessage = thread.Messages.Single(message => message.Role == "plan");
        StringAssert.Contains(planMessage.Text, "Refactor first, then test.");
        Assert.AreEqual(0, planMessage.PlanEntries?.Count ?? 0);
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

    private static bool IsToolLifecycleEvent(JsonElement message, string expectedToolCallId) =>
        (EventType(message) is "tool_started" or "tool_updated" or "tool_finished" or "tool_delta")
        && message.TryGetProperty("toolCallId", out var toolCallId)
        && toolCallId.GetString() == expectedToolCallId;
}
