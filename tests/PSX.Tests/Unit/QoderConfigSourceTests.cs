using System.Text.Json;
using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class QoderConfigSourceTests
{
    [TestMethod]
    public void Collect_ReturnsAvailableEmptyWithFixedNote()
    {
        var report = new QoderConfigSource().Collect(CancellationToken.None);
        Assert.AreEqual(AgentProviderConfigReport.Available, report.State);
        Assert.HasCount(0, report.Facts);
        Assert.HasCount(0, report.Models);
        Assert.HasCount(0, report.McpServers);
        Assert.HasCount(0, report.Skills);
        CollectionAssert.Contains(report.Notes.ToArray(), AgentConfigNotes.NoEditableConfig);

        // Wire JSON may escape non-ASCII; assert the note constant is present
        // after deserialization rather than as a raw substring.
        var roundTrip = JsonSerializer.Deserialize<AgentProviderConfigReport>(
            JsonSerializer.Serialize(report));
        Assert.IsNotNull(roundTrip);
        CollectionAssert.Contains(roundTrip!.Notes.ToArray(), AgentConfigNotes.NoEditableConfig);
    }
}
