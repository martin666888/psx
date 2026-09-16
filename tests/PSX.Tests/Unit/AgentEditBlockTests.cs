using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentEditBlockTests
{
    [TestMethod]
    public void ReadEditBlocks_PreservesOrderAndExactContent_ResolvesWorkspacePaths()
    {
        using var input = JsonDocument.Parse("""
            {"content":[
              {"type":"text","text":"Keep this warning."},
              {"type":"diff","path":"src/a.c","oldText":" \r\n","newText":"```\n+text\n"},
              {"type":"content","content":{"type":"text","text":"Second file:"}},
              {"type":"diff","path":"../outside.c","oldText":null,"newText":""}
            ]}
            """);
        var blocks = AcpPermissionPolicy.ReadEditBlocks(input.RootElement, @"D:\project");
        Assert.IsNotNull(blocks);
        Assert.HasCount(4, blocks);
        Assert.AreEqual("Keep this warning.", blocks[0].Text);
        Assert.AreEqual("src/a.c", blocks[1].DisplayPath);
        Assert.AreEqual(@"D:\project\src\a.c", blocks[1].Path);
        Assert.IsFalse(blocks[1].External);
        Assert.AreEqual(" \r\n", blocks[1].OldText);
        Assert.AreEqual("```\n+text\n", blocks[1].NewText);
        Assert.AreEqual("Second file:", blocks[2].Text);
        Assert.AreEqual(@"D:\outside.c", blocks[3].DisplayPath);
        Assert.IsTrue(blocks[3].External);
        Assert.IsNull(blocks[3].OldText);
    }

    [TestMethod]
    [DataRow("{\"content\":[{\"type\":\"text\",\"text\":\"# Normal document\"}]}")]
    [DataRow("{\"content\":[{\"type\":\"diff\",\"path\":\"a.c\"}]}")]
    [DataRow("{\"content\":[{\"type\":\"diff\",\"path\":\"a.c\",\"oldText\":42,\"newText\":\"\"}]}")]
    [DataRow("{\"content\":[{\"type\":\"diff\",\"path\":\"a.c\",\"newText\":\"\"},{\"type\":\"image\"}]}")]
    public void ReadEditBlocks_NonEditOrIncompleteContent_KeepsDocumentFallback(string json)
    {
        using var input = JsonDocument.Parse(json);
        Assert.IsNull(AcpPermissionPolicy.ReadEditBlocks(input.RootElement, @"D:\project"));
    }

    [TestMethod]
    public void EditBlocks_SurvivePersistenceSnapshotMergeAndHistoryPayload()
    {
        var source = new AgentMessage
        {
            Role = "document_permission",
            DecisionSnapshotId = "edit-1",
            DecisionState = "pending",
            EditBlocks = [new("diff", Path: @"D:\project\a.c", DisplayPath: "a.c", OldText: "before", NewText: "after")]
        };
        var saved = JsonSerializer.Deserialize<AgentMessage>(JsonSerializer.Serialize(source))!;
        var messages = new List<AgentMessage>();
        DocumentDecisionSnapshotMerger.Merge(messages, [saved]);
        Assert.AreEqual("interrupted", messages[0].DecisionState);
        Assert.AreEqual(source.EditBlocks[0], messages[0].EditBlocks![0]);
        saved.EditBlocks!.Clear();
        Assert.HasCount(1, messages[0].EditBlocks!);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(AgentThreadBridgePayload.ThreadLoaded(
            new AgentThread { Messages = messages }, true, false)));
        var block = payload.RootElement.GetProperty("messages")[0].GetProperty("editBlocks")[0];
        Assert.AreEqual("a.c", block.GetProperty("displayPath").GetString());
        Assert.AreEqual("before", block.GetProperty("oldText").GetString());
        Assert.AreEqual("after", block.GetProperty("newText").GetString());
    }
}
