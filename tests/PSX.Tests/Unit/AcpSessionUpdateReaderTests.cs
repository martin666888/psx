using System.Text.Json;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AcpSessionUpdateReaderTests
{
    [TestMethod]
    [DataRow("user_message_chunk", (int)AcpSessionUpdateKind.UserMessageChunk)]
    [DataRow("agent_message_chunk", (int)AcpSessionUpdateKind.AgentMessageChunk)]
    [DataRow("agent_thought_chunk", (int)AcpSessionUpdateKind.AgentThoughtChunk)]
    [DataRow("tool_call", (int)AcpSessionUpdateKind.ToolCall)]
    [DataRow("tool_call_update", (int)AcpSessionUpdateKind.ToolCallUpdate)]
    [DataRow("plan", (int)AcpSessionUpdateKind.Plan)]
    [DataRow("available_commands_update", (int)AcpSessionUpdateKind.AvailableCommands)]
    [DataRow("usage_update", (int)AcpSessionUpdateKind.Usage)]
    [DataRow("config_option_update", (int)AcpSessionUpdateKind.ConfigOption)]
    [DataRow("current_mode_update", (int)AcpSessionUpdateKind.CurrentMode)]
    [DataRow("session_info_update", (int)AcpSessionUpdateKind.SessionInfo)]
    [DataRow("future_update", (int)AcpSessionUpdateKind.Unknown)]
    public void ReadKind_MapsWireValues(string wireValue, int expected)
    {
        using var document = JsonDocument.Parse($$"""{"sessionUpdate":"{{wireValue}}"}""");

        Assert.AreEqual((AcpSessionUpdateKind)expected, AcpSessionUpdateReader.ReadKind(document.RootElement));
    }

    [TestMethod]
    public void ReadContentText_TextBlock_ReturnsText()
    {
        using var document = JsonDocument.Parse("""{"content":{"type":"text","text":"hello"}}""");

        Assert.AreEqual("hello", AcpSessionUpdateReader.ReadContentText(document.RootElement));
    }

    [TestMethod]
    public void ReadContentText_NonTextContent_PreservesJson()
    {
        using var document = JsonDocument.Parse("""{"content":{"type":"image","data":"abc"}}""");

        Assert.AreEqual("""{"type":"image","data":"abc"}""", AcpSessionUpdateReader.ReadContentText(document.RootElement));
    }
}
