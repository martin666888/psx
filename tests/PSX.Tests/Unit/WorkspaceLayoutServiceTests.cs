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
        Assert.AreEqual(WorkspaceLayoutService.FirstPaneId, pane.PaneId);
        Assert.IsNull(pane.WorkspaceId);
        Assert.IsNull(pane.Kind);
        Assert.AreEqual(1.0, pane.Ratio);
        Assert.AreEqual(WorkspaceLayoutService.FirstPaneId, snapshot.FocusedPaneId);
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
        Assert.AreEqual(WorkspaceLayoutService.FirstPaneId, broadcasts[1].FocusedPaneId);
        Assert.IsGreaterThan(broadcasts[0].LayoutRevision, broadcasts[1].LayoutRevision);
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class WorkspaceLayoutServiceSplitTests
{
    private static (WorkspaceLayoutService layout, List<WorkspaceLayoutSnapshot> broadcasts) Create()
    {
        var layout = new WorkspaceLayoutService();
        var broadcasts = new List<WorkspaceLayoutSnapshot>();
        layout.LayoutChanged += (_, snapshot) => broadcasts.Add(snapshot);
        return (layout, broadcasts);
    }

    [TestMethod]
    public void Split_CreatesSecondPane_FocusesIt_AndNormalizesRatios()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Panes);
        Assert.AreEqual(b, snapshot.Panes[1].WorkspaceId);
        Assert.AreEqual(snapshot.Panes[1].PaneId, snapshot.FocusedPaneId);
        Assert.AreEqual(0.5, snapshot.Panes[0].Ratio);
        Assert.AreEqual(0.5, snapshot.Panes[1].Ratio);
    }

    [TestMethod]
    public void Split_VisibleWorkspaceBelowCap_MovesItToAFreshRightPane()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Agent);

        layout.SplitWorkspaceToNewPane(a, WorkspaceKind.Agent);

        // Below the cap, splitting an already-visible workspace moves it to a
        // fresh right-hand pane; the vacated pane collapses.
        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Panes);
        Assert.AreEqual(b, snapshot.Panes[0].WorkspaceId);
        Assert.AreEqual(a, snapshot.Panes[1].WorkspaceId);
        Assert.AreEqual(a, layout.FocusedWorkspaceId);
    }

    [TestMethod]
    public void Assign_WorkspaceVisibleInOtherPane_OnlyMovesFocus()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);
        var revisionBefore = layout.Snapshot.LayoutRevision;

        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.IsGreaterThan(revisionBefore, snapshot.LayoutRevision);
        Assert.AreEqual(snapshot.Panes[0].PaneId, snapshot.FocusedPaneId);
        Assert.AreEqual(a, snapshot.Panes[0].WorkspaceId, "no duplication");
        Assert.AreEqual(b, snapshot.Panes[1].WorkspaceId);
    }

    [TestMethod]
    public void RemoveWorkspace_FromSecondPane_CollapsesPane_AndFocusFallsLeft()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);

        layout.RemoveWorkspace(b);

        var snapshot = layout.Snapshot;
        var pane = snapshot.Panes.Single();
        Assert.AreEqual(a, pane.WorkspaceId);
        Assert.AreEqual(1.0, pane.Ratio);
        Assert.AreEqual(pane.PaneId, snapshot.FocusedPaneId);
    }

    [TestMethod]
    public void RemoveWorkspace_FromLonePane_PullsMruBackgroundAtomically()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);
        layout.RemoveWorkspace(b);
        // B is closed; A visible. Re-split a new C, then close it so A stays
        // and B (closed) must never come back.
        var c = Guid.NewGuid();
        layout.SplitWorkspaceToNewPane(c, WorkspaceKind.Agent);

        layout.RemoveWorkspace(c);

        var snapshot = layout.Snapshot;
        Assert.AreEqual(a, snapshot.Panes.Single().WorkspaceId);
    }

    [TestMethod]
    public void CollapseToSinglePane_KeepsFocusedPaneContent()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);

        layout.CollapseToSinglePane();

        var snapshot = layout.Snapshot;
        var pane = snapshot.Panes.Single();
        Assert.AreEqual(b, pane.WorkspaceId);
        Assert.AreEqual(1.0, pane.Ratio);
    }

    [TestMethod]
    public void SetPaneRatio_ClampsAndRenormalizes()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);
        var first = layout.Snapshot.Panes[0].PaneId;

        layout.SetPaneRatio(first, 0.95);

        var snapshot = layout.Snapshot;
        Assert.AreEqual(0.8, snapshot.Panes[0].Ratio, "clamped to the 0.8 ceiling");
        Assert.AreEqual(0.2, snapshot.Panes[1].Ratio);
    }

    [TestMethod]
    public void PendingPlacement_NewPane_CreatedAtomicallyAtAssignment()
    {
        var (layout, broadcasts) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        layout.RecordPendingPlacement(WorkspaceLayoutService.NewPanePlacement);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);

        // No snapshot ever shows an empty second pane: creation + assignment
        // + focus land in one revision.
        foreach (var snapshot in broadcasts)
        {
            foreach (var pane in snapshot.Panes)
                Assert.IsTrue(pane.WorkspaceId.HasValue || snapshot.Panes.Count == 1);
        }
        var final = layout.Snapshot;
        Assert.HasCount(2, final.Panes);
        Assert.AreEqual(b, final.Panes[1].WorkspaceId);
        Assert.AreEqual(final.Panes[1].PaneId, final.FocusedPaneId);
    }

    [TestMethod]
    public void FocusPane_UnknownOrCurrent_IsNoOp()
    {
        var (layout, broadcasts) = Create();
        var a = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        broadcasts.Clear();

        layout.FocusPane("pane-999");
        layout.FocusPane(WorkspaceLayoutService.FirstPaneId);

        Assert.IsEmpty(broadcasts);
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class WorkspaceLayoutServiceMultiColumnTests
{
    private static (WorkspaceLayoutService layout, List<WorkspaceLayoutSnapshot> broadcasts) Create()
    {
        var layout = new WorkspaceLayoutService();
        var broadcasts = new List<WorkspaceLayoutSnapshot>();
        layout.LayoutChanged += (_, snapshot) => broadcasts.Add(snapshot);
        return (layout, broadcasts);
    }

    [TestMethod]
    public void Split_ToFourColumns_NormalizesRatios()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(ids[1], WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(ids[2], WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(ids[3], WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(4, snapshot.Panes);
        foreach (var pane in snapshot.Panes)
            Assert.AreEqual(0.25, pane.Ratio);
        Assert.AreEqual(snapshot.Panes[3].PaneId, snapshot.FocusedPaneId);
        Assert.AreEqual(ids[3], layout.FocusedWorkspaceId);
    }

    [TestMethod]
    public void Split_AtFourColumnCap_BackgroundWorkspaceReplacesFocusedPane()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < 4; i++)
            layout.SplitWorkspaceToNewPane(ids[i], WorkspaceKind.Agent);
        var fifth = Guid.NewGuid();

        layout.SplitWorkspaceToNewPane(fifth, WorkspaceKind.Terminal);

        var snapshot = layout.Snapshot;
        Assert.HasCount(4, snapshot.Panes);
        Assert.AreEqual(fifth, snapshot.Panes[3].WorkspaceId, "the cap degrades to replacing the focused pane");
        Assert.AreEqual(fifth, layout.FocusedWorkspaceId);
    }

    [TestMethod]
    public void RemoveWorkspace_MiddlePane_CollapsesAndFocusFallsLeft()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < 4; i++)
            layout.SplitWorkspaceToNewPane(ids[i], WorkspaceKind.Agent);
        layout.FocusPane(layout.Snapshot.Panes[1].PaneId);

        layout.RemoveWorkspace(ids[1]);

        var snapshot = layout.Snapshot;
        Assert.HasCount(3, snapshot.Panes);
        Assert.AreEqual(ids[0], layout.FocusedWorkspaceId, "focus falls to the left neighbor");
        foreach (var pane in snapshot.Panes)
            Assert.AreEqual(Math.Round(1.0 / 3, 4), pane.Ratio);
        Assert.IsFalse(snapshot.Panes.Any(p => p.WorkspaceId == ids[1]));
    }

    [TestMethod]
    public void FocusAdjacentPane_WrapsAroundAllColumns()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(ids[1], WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(ids[2], WorkspaceKind.Terminal);

        layout.FocusAdjacentPane(1);
        Assert.AreEqual(ids[0], layout.FocusedWorkspaceId, "wraps past the last column");
        layout.FocusAdjacentPane(-1);
        Assert.AreEqual(ids[2], layout.FocusedWorkspaceId, "wraps back");
    }
}
