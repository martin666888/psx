using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class WorkspaceLayoutServiceTests
{
    [TestMethod]
    public void Snapshot_Initial_SingleEmptyFocusedPane()
    {
        var layout = new WorkspaceLayoutService();
        var snapshot = layout.Snapshot;

        var pane = snapshot.Panes.Single();
        Assert.AreEqual(WorkspaceLayoutService.SinglePaneId, pane.PaneId);
        Assert.IsNull(pane.WorkspaceId);
        Assert.IsNull(pane.Kind);
        Assert.AreEqual(1.0, pane.Ratio);
        Assert.AreEqual(WorkspaceLayoutService.SinglePaneId, snapshot.FocusedPaneId);
        Assert.AreEqual(0, snapshot.LayoutRevision);
    }

    [TestMethod]
    public void AssignActiveWorkspace_BumpsRevisionAndBroadcasts()
    {
        var layout = new WorkspaceLayoutService();
        var broadcasts = new List<WorkspaceLayoutSnapshot>();
        layout.LayoutChanged += (_, snapshot) => broadcasts.Add(snapshot);
        var workspaceId = Guid.NewGuid();

        layout.AssignActiveWorkspace(workspaceId, WorkspaceKind.Agent);

        var snapshot = broadcasts.Single();
        Assert.AreEqual(1, snapshot.LayoutRevision);
        Assert.AreEqual(workspaceId, snapshot.Panes.Single().WorkspaceId);
        Assert.AreEqual(WorkspaceKind.Agent, snapshot.Panes.Single().Kind);
    }

    [TestMethod]
    public void AssignActiveWorkspace_SameAssignment_DoesNotBroadcast()
    {
        var layout = new WorkspaceLayoutService();
        var workspaceId = Guid.NewGuid();
        layout.AssignActiveWorkspace(workspaceId, WorkspaceKind.Terminal);
        var broadcasts = 0;
        layout.LayoutChanged += (_, _) => broadcasts++;

        layout.AssignActiveWorkspace(workspaceId, WorkspaceKind.Terminal);

        Assert.AreEqual(0, broadcasts);
        Assert.AreEqual(1, layout.Snapshot.LayoutRevision);
    }

    [TestMethod]
    public void RemoveWorkspace_Assigned_EmptiesPaneWithMonotonicRevision()
    {
        var layout = new WorkspaceLayoutService();
        var broadcasts = new List<WorkspaceLayoutSnapshot>();
        layout.LayoutChanged += (_, snapshot) => broadcasts.Add(snapshot);
        var assigned = Guid.NewGuid();
        layout.AssignActiveWorkspace(assigned, WorkspaceKind.Terminal);

        // Removing an unassigned workspace is a no-op.
        layout.RemoveWorkspace(Guid.NewGuid());
        Assert.HasCount(1, broadcasts);

        layout.RemoveWorkspace(assigned);

        Assert.HasCount(2, broadcasts);
        Assert.IsNull(broadcasts[1].Panes.Single().WorkspaceId);
        Assert.IsNull(broadcasts[1].Panes.Single().Kind);
        // Focus stays on the pane itself, never on a removed workspace.
        Assert.AreEqual(WorkspaceLayoutService.SinglePaneId, broadcasts[1].FocusedPaneId);
        Assert.IsTrue(broadcasts[1].LayoutRevision > broadcasts[0].LayoutRevision);
    }
}
