using System.Text;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentBridgeMessageParserTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("9e894924-c93d-4f51-bccd-68b934d665ab");

    [TestMethod]
    public void TryParse_Submit_PreservesTextAndFiltersAttachmentIds()
    {
        var parsed = AgentBridgeMessageParser.TryParse(
            $$"""{"type":"agent_submit","workspaceId":"{{WorkspaceId}}","text":"hello","attachments":["a","",7,"b"]}""",
            out var message);

        Assert.IsTrue(parsed);
        Assert.AreEqual(AgentBridgeMessageKind.Submit, message!.Kind);
        Assert.AreEqual("hello", message.Submit!.Text);
        CollectionAssert.AreEqual(new[] { "a", "b" }, message.Submit.AttachmentIds);
        Assert.AreEqual(WorkspaceId, message.Submit.WorkspaceId);
    }

    [TestMethod]
    public void TryParse_SubmitWithOnlyWhitespaceAndNoAttachments_IsRejected()
    {
        Assert.IsFalse(AgentBridgeMessageParser.TryParse(
            $$"""{"type":"agent_submit","workspaceId":"{{WorkspaceId}}","text":"  ","attachments":[]}""",
            out _));
    }

    [TestMethod]
    public void TryParse_AttachmentUpload_UsesSafeDefaultsForWrongFieldTypes()
    {
        Assert.IsTrue(AgentBridgeMessageParser.TryParse(
            $$"""{"type":"agent_upload_attachment","workspaceId":"{{WorkspaceId}}","clientId":"client","fileName":"a.txt","mimeType":3,"size":"bad","dataBase64":"YWJj"}""",
            out var message));

        Assert.AreEqual(AgentBridgeMessageKind.AttachmentUpload, message!.Kind);
        Assert.AreEqual("client", message.AttachmentUpload!.ClientId);
        Assert.AreEqual("", message.AttachmentUpload.MimeType);
        Assert.AreEqual(0, message.AttachmentUpload.Size);
    }

    [TestMethod]
    [DataRow("agent_permission_response")]
    [DataRow("agent_question_response")]
    [DataRow("agent_elicitation_response")]
    public void TryParse_StructuredResponse_MapsTypeToCommand(string type)
    {
        Assert.IsTrue(AgentBridgeMessageParser.TryParse(
            $$"""{"type":"{{type}}","workspaceId":"{{WorkspaceId}}","requestId":"request-1","value":"choice"}""",
            out var message));

        Assert.AreEqual(type, message!.Command!.Command);
        Assert.AreEqual("request-1", message.Command.RequestId);
        Assert.AreEqual("choice", message.Command.Value);
    }

    [TestMethod]
    public void TryParse_Command_UsesExplicitCommandName()
    {
        Assert.IsTrue(AgentBridgeMessageParser.TryParse(
            $$"""{"type":"agent_command","workspaceId":"{{WorkspaceId}}","command":"new_thread","value":"x"}""",
            out var message));

        Assert.AreEqual("new_thread", message!.Command!.Command);
    }

    [TestMethod]
    public void TryParse_Command_PreservesBooleanConfigValue()
    {
        Assert.IsTrue(AgentBridgeMessageParser.TryParse(
            $$"""{"type":"agent_command","workspaceId":"{{WorkspaceId}}","command":"set_config_option","requestId":"fast_mode","value":true}""",
            out var message));

        Assert.AreEqual("set_config_option", message!.Command!.Command);
        Assert.IsNotNull(message.Command.BooleanValue);
        Assert.IsTrue(message.Command.BooleanValue.Value);
        Assert.AreEqual("", message.Command.Value);
    }

    [TestMethod]
    public void TryParse_GlobalCommand_DoesNotRequireWorkspaceId()
    {
        Assert.IsTrue(AgentBridgeMessageParser.TryParse(
            """{"type":"agent_global_command","command":"history","requestId":"history-1"}""",
            out var message));

        Assert.AreEqual(AgentBridgeMessageKind.Command, message!.Kind);
        Assert.AreEqual(Guid.Empty, message.Command!.WorkspaceId);
        Assert.AreEqual("history", message.Command.Command);
        Assert.AreEqual("history-1", message.Command.RequestId);
    }

    [TestMethod]
    public void TryParse_GlobalCommandWithoutCommand_IsRejected()
    {
        Assert.IsFalse(AgentBridgeMessageParser.TryParse(
            """{"type":"agent_global_command","requestId":"history-1"}""",
            out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-guid")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public void TryParse_AgentMessageWithoutValidWorkspaceId_IsRejected(string workspaceId)
    {
        Assert.IsFalse(AgentBridgeMessageParser.TryParse(
            $$"""{"type":"agent_command","workspaceId":"{{workspaceId}}","command":"state"}""",
            out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-json")]
    [DataRow("{\"type\":\"unknown\"}")]
    [DataRow("[]")]
    public void TryParse_MalformedOrUnknownMessage_IsRejected(string json)
    {
        Assert.IsFalse(AgentBridgeMessageParser.TryParse(json, out _));
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class TerminalBridgeMessageParserTests
{
    private static readonly Guid SessionId = Guid.Parse("7a5e9fba-61a6-442a-94e5-34e3f72a26f1");
    private static readonly Guid RequestId = Guid.Parse("8bc6bfac-6d4d-4fdb-a43b-dd7024bd4ccf");

    [TestMethod]
    public void TryParse_Input_DecodesBase64AfterValidatingSession()
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes("dir\r"));

        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"input","sessionId":"{{SessionId}}","data":"{{data}}"}""",
            out var message));

        Assert.AreEqual(TerminalBridgeMessageKind.Input, message!.Kind);
        Assert.AreEqual(SessionId, message.Input!.SessionId);
        Assert.AreEqual("dir\r", Encoding.UTF8.GetString(message.Input.Data));
    }

    [TestMethod]
    [DataRow("bad-guid", "YWJj")]
    [DataRow("7a5e9fba-61a6-442a-94e5-34e3f72a26f1", "not-base64")]
    public void TryParse_InputWithInvalidIdentityOrData_IsRejected(string sessionId, string data)
    {
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"input","sessionId":"{{sessionId}}","data":"{{data}}"}""",
            out _));
    }

    [TestMethod]
    public void TryParse_Resize_RequiresBothDimensions()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"resize","sessionId":"{{SessionId}}","cols":120,"rows":40}""",
            out var message));
        Assert.AreEqual(120, message!.Resize!.Cols);
        Assert.AreEqual(40, message.Resize.Rows);

        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"resize","sessionId":"{{SessionId}}","cols":120}""",
            out _));
    }

    [TestMethod]
    public void TryParse_TitleWithoutText_UsesTerminalFallback()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"title","sessionId":"{{SessionId}}"}""",
            out var message));

        Assert.AreEqual("Terminal", message!.Title!.Title);
    }

    [TestMethod]
    public void TryParse_PasteRequest_RequiresValidSessionAndRequestIds()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"paste_request","sessionId":"{{SessionId}}","requestId":"{{RequestId}}"}""",
            out var message));

        Assert.AreEqual(TerminalBridgeMessageKind.PasteRequest, message!.Kind);
        Assert.AreEqual(SessionId, message.PasteRequest!.SessionId);
        Assert.AreEqual(RequestId, message.PasteRequest.RequestId);
    }

    [TestMethod]
    [DataRow("bad-session", "8bc6bfac-6d4d-4fdb-a43b-dd7024bd4ccf")]
    [DataRow("7a5e9fba-61a6-442a-94e5-34e3f72a26f1", "bad-request")]
    [DataRow("7a5e9fba-61a6-442a-94e5-34e3f72a26f1", "")]
    public void TryParse_PasteRequestWithInvalidIdentity_IsRejected(string sessionId, string requestId)
    {
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"paste_request","sessionId":"{{sessionId}}","requestId":"{{requestId}}"}""",
            out _));
    }

    [TestMethod]
    public void TryParse_Ready_DoesNotRequireSessionFields()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse("""{"type":"ready"}""", out var message));
        Assert.AreEqual(TerminalBridgeMessageKind.Ready, message!.Kind);
    }

    [TestMethod]
    public void TryParse_PaneRatiosCommit_RequiresACompleteFiniteVectorShape()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            """{"type":"pane_ratios_commit","baseRevision":12,"panes":[{"paneId":"pane-1","ratio":0.42},{"paneId":"pane-2","ratio":0.58}]}""",
            out var message));
        Assert.AreEqual(TerminalBridgeMessageKind.PaneRatiosCommit, message!.Kind);
        Assert.AreEqual(12, message.PaneRatios!.BaseRevision);
        Assert.AreEqual(0.42, message.PaneRatios.Ratios["pane-1"]);

        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            """{"type":"pane_ratios_commit","baseRevision":12,"panes":[{"paneId":"pane-1","ratio":0.5},{"paneId":"pane-1","ratio":0.5}]}""",
            out _));
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            """{"type":"pane_ratios_commit","baseRevision":12,"panes":[{"paneId":"pane-1","ratio":-1},{"paneId":"pane-2","ratio":2}]}""",
            out _));
    }

    [TestMethod]
    public void TryParse_WorkspaceAndThemeIntents_ValidateRequiredFields()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            $$"""{"type":"workspace_layout_intent","action":"activate","workspaceId":"{{SessionId}}"}""",
            out var activate));
        Assert.AreEqual(TerminalBridgeMessageKind.WorkspaceLayoutIntent, activate!.Kind);
        Assert.AreEqual(SessionId, activate.WorkspaceIntent!.WorkspaceId);

        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            """{"type":"workspace_create","kind":"agent","providerKey":"acp-kimi","placement":"new_right"}""",
            out var create));
        Assert.AreEqual("acp-kimi", create!.WorkspaceCreate!.ProviderKey);

        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            """{"type":"theme_action","action":"preview","themeKey":"builtin:dark"}""",
            out var theme));
        Assert.AreEqual("preview", theme!.ThemeAction!.Action);

        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            """{"type":"workspace_create","kind":"agent","placement":"focused"}""",
            out _));
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(
            """{"type":"theme_action","action":"confirm"}""",
            out _));
    }

    [TestMethod]
    public void TryParse_LayoutIntent_CollapseSingleWithoutWorkspaceId_IsAccepted()
    {
        Assert.IsTrue(TerminalBridgeMessageParser.TryParse(
            """{"type":"workspace_layout_intent","action":"collapse_single"}""",
            out var message));

        Assert.AreEqual(TerminalBridgeMessageKind.WorkspaceLayoutIntent, message!.Kind);
        Assert.AreEqual("collapse_single", message.WorkspaceIntent!.Action);
        Assert.IsNull(message.WorkspaceIntent.WorkspaceId);
    }

    [TestMethod]
    [DataRow("""{"type":"workspace_layout_intent","action":"move_to_pane","workspaceId":"7a5e9fba-61a6-442a-94e5-34e3f72a26f1","paneId":"column-2"}""")]
    [DataRow("""{"type":"workspace_layout_intent","action":"swap"}""")]
    [DataRow("""{"type":"workspace_layout_intent","action":"move_to_pane"}""")]
    public void TryParse_LayoutIntent_RejectsRemovedActions(string json)
    {
        // move_to_pane and swap intents are gone: cross-column moves ride the
        // pane_move message (drag), collapse is the only id-less intent.
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(json, out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-json")]
    [DataRow("{\"type\":\"unknown\"}")]
    [DataRow("null")]
    public void TryParse_MalformedOrUnknownMessage_IsRejected(string json)
    {
        Assert.IsFalse(TerminalBridgeMessageParser.TryParse(json, out _));
    }
}
