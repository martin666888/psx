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
    public void Split_OnlyVisibleWorkspaceWithoutBackground_IsNoOp_AndKeepsTheSolePane()
    {
        var (layout, broadcasts) = Create();
        var workspaceId = Guid.NewGuid();
        layout.AssignActiveWorkspace(workspaceId, WorkspaceKind.Agent);
        broadcasts.Clear();

        layout.SplitWorkspaceToNewPane(workspaceId, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(1, snapshot.Panes);
        Assert.AreEqual(workspaceId, snapshot.Panes[0].WorkspaceId);
        Assert.AreEqual(snapshot.Panes[0].PaneId, snapshot.FocusedPaneId);
        Assert.IsEmpty(broadcasts, "a sole visible workspace without a replacement cannot create a second pane");
    }

    [TestMethod]
    public void Split_OnlyVisibleWorkspaceWithBackground_PullsBackgroundIntoSourcePane()
    {
        var (layout, _) = Create();
        var terminal = Guid.NewGuid();
        var agent = Guid.NewGuid();
        layout.AssignActiveWorkspace(terminal, WorkspaceKind.Terminal);
        layout.AssignActiveWorkspace(agent, WorkspaceKind.Agent);

        layout.SplitWorkspaceToNewPane(agent, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Panes);
        Assert.AreEqual(terminal, snapshot.Panes[0].WorkspaceId);
        Assert.AreEqual(agent, snapshot.Panes[1].WorkspaceId);
        Assert.AreEqual(snapshot.Panes[1].PaneId, snapshot.FocusedPaneId);
    }

    [TestMethod]
    public void Split_VisibleWorkspaceWithoutBackgroundReplacement_IsNoOp()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Agent);

        var before = layout.Snapshot;
        var accepted = layout.SplitWorkspaceToNewPane(a, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Panes);
        Assert.IsFalse(accepted);
        Assert.AreEqual(before.LayoutRevision, snapshot.LayoutRevision);
        CollectionAssert.AreEqual(before.Panes.Select(pane => pane.WorkspaceId).ToArray(), snapshot.Panes.Select(pane => pane.WorkspaceId).ToArray());
    }

    [TestMethod]
    public void Split_VisibleWorkspaceWithBackground_AddsRightPaneAndFillsSourceAtomically()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var background = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(background, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Agent);

        var accepted = layout.SplitWorkspaceToNewPane(background, WorkspaceKind.Terminal);

        var snapshot = layout.Snapshot;
        Assert.IsTrue(accepted);
        Assert.HasCount(3, snapshot.Panes);
        Assert.AreEqual(a, snapshot.Panes[0].WorkspaceId, "MRU background fills the source pane");
        Assert.AreEqual(background, snapshot.Panes[1].WorkspaceId, "target is inserted immediately to the right");
        Assert.AreEqual(b, snapshot.Panes[2].WorkspaceId);
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
    public void SetPaneRatios_ReplacesTheWholeVectorAtomically()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b, WorkspaceKind.Terminal);
        var before = layout.Snapshot;
        var first = before.Panes[0].PaneId;
        var second = before.Panes[1].PaneId;

        var accepted = layout.SetPaneRatios(before.LayoutRevision, new Dictionary<string, double>
        {
            [first] = 0.72,
            [second] = 0.28
        });

        var snapshot = layout.Snapshot;
        Assert.IsTrue(accepted);
        Assert.AreEqual(before.LayoutRevision + 1, snapshot.LayoutRevision);
        Assert.AreEqual(0.72, snapshot.Panes[0].Ratio);
        Assert.AreEqual(0.28, snapshot.Panes[1].Ratio);
    }

    [TestMethod]
    public void SetPaneRatios_RejectsStaleOrMalformedVectorsWithoutChangingLayout()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i], WorkspaceKind.Agent);
        var before = layout.Snapshot;
        var valid = before.Panes.ToDictionary(pane => pane.PaneId, pane => pane.Ratio);

        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision - 1, valid), "stale revision");
        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision, valid.Take(3).ToDictionary()), "missing pane");
        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision, valid.ToDictionary(pair => pair.Key, _ => 0.1)), "sum mismatch");
        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision, valid.ToDictionary(pair => pair.Key, pair => pair.Key == before.Panes[0].PaneId ? double.NaN : pair.Value)), "non-finite ratio");

        var snapshot = layout.Snapshot;
        Assert.AreEqual(before.LayoutRevision, snapshot.LayoutRevision);
        CollectionAssert.AreEqual(before.Panes.Select(pane => pane.Ratio).ToArray(), snapshot.Panes.Select(pane => pane.Ratio).ToArray());
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
    public void Split_AtFourColumnCap_IsRejectedWithoutChangingAssignments()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < 4; i++)
            layout.SplitWorkspaceToNewPane(ids[i], WorkspaceKind.Agent);
        var fifth = Guid.NewGuid();

        var before = layout.Snapshot;
        var accepted = layout.SplitWorkspaceToNewPane(fifth, WorkspaceKind.Terminal);

        var snapshot = layout.Snapshot;
        Assert.IsFalse(accepted);
        Assert.HasCount(4, snapshot.Panes);
        Assert.AreEqual(before.LayoutRevision, snapshot.LayoutRevision);
        CollectionAssert.AreEqual(before.Panes.Select(pane => pane.WorkspaceId).ToArray(), snapshot.Panes.Select(pane => pane.WorkspaceId).ToArray());
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
