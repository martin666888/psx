using PSX.Models;
using PSX.Services;

namespace PSX.Tests.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class WorkspaceLayoutServiceTests
{
    [TestMethod]
    public void Snapshot_Initial_SingleEmptyFocusedColumn()
    {
        var layout = new WorkspaceLayoutService();
        var snapshot = layout.Snapshot;

        var column = snapshot.Columns.Single();
        Assert.AreEqual(WorkspaceLayoutService.FirstColumnId, column.ColumnId);
        Assert.IsEmpty(column.Tabs);
        Assert.IsNull(column.ActiveTabId);
        Assert.AreEqual(1.0, column.Ratio);
        Assert.AreEqual(WorkspaceLayoutService.FirstColumnId, snapshot.FocusedColumnId);
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
        var column = snapshot.Columns.Single();
        Assert.AreEqual(workspaceId, column.ActiveTabId);
        Assert.AreEqual(workspaceId, column.Tabs.Single().WorkspaceId);
        Assert.AreEqual(WorkspaceKind.Agent, column.Tabs.Single().Kind);
        Assert.AreEqual(workspaceId, layout.FocusedWorkspaceId);
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
    public void RemoveWorkspace_Assigned_EmptiesSoleColumnWithMonotonicRevision()
    {
        var layout = new WorkspaceLayoutService();
        var broadcasts = new List<WorkspaceLayoutSnapshot>();
        layout.LayoutChanged += (_, snapshot) => broadcasts.Add(snapshot);
        var assigned = Guid.NewGuid();
        layout.AssignActiveWorkspace(assigned, WorkspaceKind.Terminal);

        // Removing a workspace that is not open is a no-op.
        layout.RemoveWorkspace(Guid.NewGuid());
        Assert.HasCount(1, broadcasts);

        layout.RemoveWorkspace(assigned);

        Assert.HasCount(2, broadcasts);
        var column = broadcasts[1].Columns.Single();
        Assert.IsEmpty(column.Tabs);
        Assert.IsNull(column.ActiveTabId);
        Assert.AreEqual(WorkspaceLayoutService.FirstColumnId, broadcasts[1].FocusedColumnId);
        Assert.IsNull(layout.FocusedWorkspaceId);
        // Focus stays on the column itself, never on a removed workspace.
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
    public void Split_MovesTabToNewRightColumn_FocusesIt_AndNormalizesRatios()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);

        layout.SplitWorkspaceToNewPane(b);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Columns);
        Assert.AreEqual(b, snapshot.Columns[1].ActiveTabId);
        Assert.AreEqual(b, snapshot.Columns[1].Tabs.Single().WorkspaceId);
        Assert.AreEqual(snapshot.Columns[1].ColumnId, snapshot.FocusedColumnId);
        Assert.AreEqual(0.5, snapshot.Columns[0].Ratio);
        Assert.AreEqual(0.5, snapshot.Columns[1].Ratio);
    }

    [TestMethod]
    public void Split_MovesTabOut_SourceColumnWithTabsKeepsActiveTab()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Agent);

        layout.SplitWorkspaceToNewPane(b);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Columns);
        Assert.AreEqual(a, snapshot.Columns[0].ActiveTabId, "active falls to the left neighbor");
        Assert.AreEqual(a, snapshot.Columns[0].Tabs.Single().WorkspaceId);
        Assert.AreEqual(b, snapshot.Columns[1].ActiveTabId);
        Assert.AreEqual(b, snapshot.Columns[1].Tabs.Single().WorkspaceId);
    }

    [TestMethod]
    public void Split_MovesOnlyTab_SourceColumnCollapses()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);
        layout.AssignActiveWorkspace(c, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b);
        layout.SplitWorkspaceToNewPane(c);

        layout.SplitWorkspaceToNewPane(b);

        var snapshot = layout.Snapshot;
        Assert.HasCount(3, snapshot.Columns, "the collapsed slot is reused, never an empty column");
        var target = snapshot.Columns.Single(column => column.Tabs.Any(tab => tab.WorkspaceId == b));
        Assert.AreEqual(b, target.ActiveTabId);
        Assert.AreEqual(b, target.Tabs.Single().WorkspaceId);
        Assert.IsFalse(snapshot.Columns.Except(new[] { target }).Any(column => column.Tabs.Any(tab => tab.WorkspaceId == b)));
    }

    [TestMethod]
    public void Assign_OpenWorkspace_ActivatesTabAndFocusesItsColumn()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(b);
        var revisionBefore = layout.Snapshot.LayoutRevision;

        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.IsGreaterThan(revisionBefore, snapshot.LayoutRevision);
        Assert.AreEqual(snapshot.Columns[0].ColumnId, snapshot.FocusedColumnId);
        Assert.AreEqual(a, snapshot.Columns[0].ActiveTabId, "no duplication");
        Assert.AreEqual(a, snapshot.Columns[0].Tabs.Single().WorkspaceId);
        Assert.AreEqual(b, snapshot.Columns[1].Tabs.Single().WorkspaceId);
    }

    [TestMethod]
    public void Assign_UnopenedWorkspace_DefaultPlacement_BecomesActiveTabInCurrentColumn()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();

        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(1, snapshot.Columns);
        var column = snapshot.Columns.Single();
        Assert.AreEqual(a, column.ActiveTabId);
        Assert.AreEqual(a, column.Tabs.Single().WorkspaceId);
        Assert.AreEqual(column.ColumnId, snapshot.FocusedColumnId);
    }

    [TestMethod]
    public void Assign_UnopenedWorkspace_NewRightAtCap_FallsBackToCurrentColumnTab()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, WorkspaceLayoutService.MaxColumns).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);
        var extra = Guid.NewGuid();

        layout.RecordPendingPlacement(WorkspaceLayoutService.NewPanePlacement);
        layout.AssignActiveWorkspace(extra, WorkspaceKind.Terminal);

        var snapshot = layout.Snapshot;
        Assert.HasCount(WorkspaceLayoutService.MaxColumns, snapshot.Columns, "the column cap never grows");
        Assert.AreEqual(ids.Length + 1, snapshot.Columns.Sum(c => c.Tabs.Count));
        var focused = snapshot.Columns.Single(c => c.ColumnId == snapshot.FocusedColumnId);
        Assert.AreEqual(extra, focused.ActiveTabId, "degrades into a tab in the focused column");
        Assert.IsTrue(focused.Tabs.Any(tab => tab.WorkspaceId == extra));
    }

    [TestMethod]
    public void DefaultPlacement_NeverCreatesSecondColumn_OnlyExplicitNewRightDoes()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
            layout.AssignActiveWorkspace(id, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(1, snapshot.Columns, "default placement always lands in the focused column");
        Assert.HasCount(ids.Length, snapshot.Columns.Single().Tabs);
        Assert.AreEqual(ids[^1], snapshot.Columns.Single().ActiveTabId);
    }

    [TestMethod]
    public void History_ClickOpenInactiveTab_OnlyActivates()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(c, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(c);
        var before = layout.Snapshot;

        // History click on an open-but-inactive tab in another column.
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Columns, "a pure jump creates no column and no tab");
        Assert.AreEqual(snapshot.Columns[0].ColumnId, snapshot.FocusedColumnId, "the tab's column is focused");
        Assert.AreEqual(a, snapshot.Columns[0].ActiveTabId);
        Assert.HasCount(2, snapshot.Columns[0].Tabs);
        Assert.AreEqual(before.LayoutRevision + 1, snapshot.LayoutRevision);
    }

    [TestMethod]
    public void History_ClickClosedThread_OpensTabInFocusedColumn()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var closed = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(b);
        var before = layout.Snapshot;

        layout.AssignActiveWorkspace(closed, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Columns, "History stays in the current column, never creates one");
        var focused = snapshot.Columns.Single(c => c.ColumnId == snapshot.FocusedColumnId);
        Assert.AreEqual(closed, focused.ActiveTabId);
        Assert.IsTrue(focused.Tabs.Any(tab => tab.WorkspaceId == closed));
        Assert.AreEqual(before.Columns.Sum(c => c.Tabs.Count) + 1, snapshot.Columns.Sum(c => c.Tabs.Count));
    }

    [TestMethod]
    public void History_ClickNeverDuplicatesOpenThread()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(b);
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        var before = layout.Snapshot;

        // The tab is already the active tab of the focused column: no-op.
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        var snapshot = layout.Snapshot;
        Assert.AreEqual(before.LayoutRevision, snapshot.LayoutRevision, "repeated clicks stay a pure jump");
        Assert.AreEqual(2, snapshot.Columns.Sum(c => c.Tabs.Count));
    }

    [TestMethod]
    public void RemoveWorkspace_NotLastTab_ActivatesAdjacentTab()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(c, WorkspaceKind.Agent);

        layout.RemoveWorkspace(c);

        var column = layout.Snapshot.Columns.Single();
        Assert.HasCount(2, column.Tabs);
        Assert.AreEqual(b, column.ActiveTabId, "active prefers the left neighbor");

        layout.RemoveWorkspace(a);

        var columnAfter = layout.Snapshot.Columns.Single();
        Assert.HasCount(1, columnAfter.Tabs);
        Assert.AreEqual(b, columnAfter.ActiveTabId, "no left neighbor -> the right tab");
    }

    [TestMethod]
    public void RemoveWorkspace_LastTab_CollapsesColumnAndNeighborsAbsorbRatio()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(b);

        layout.RemoveWorkspace(b);

        var snapshot = layout.Snapshot;
        var column = snapshot.Columns.Single();
        Assert.AreEqual(a, column.ActiveTabId);
        Assert.AreEqual(1.0, column.Ratio);
        Assert.AreEqual(column.ColumnId, snapshot.FocusedColumnId);
        Assert.AreEqual(a, layout.FocusedWorkspaceId);
    }

    [TestMethod]
    public void RemoveWorkspace_LoneColumnLastTab_ResetsToFreshEmptyColumn_NotMruResurrection()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b);
        layout.RemoveWorkspace(b);
        // A is the only open tab; B was closed. Closing A must reset to a
        // fresh empty column — the caller creates a replacement terminal tab,
        // and B must never resurrect.
        layout.RemoveWorkspace(a);

        var snapshot = layout.Snapshot;
        var column = snapshot.Columns.Single();
        Assert.IsEmpty(column.Tabs);
        Assert.IsNull(column.ActiveTabId);
        Assert.AreEqual(WorkspaceLayoutService.FirstColumnId, column.ColumnId);
        Assert.AreEqual(WorkspaceLayoutService.FirstColumnId, snapshot.FocusedColumnId);
        Assert.IsNull(layout.FocusedWorkspaceId);
    }

    [TestMethod]
    public void Collapse_MergesOtherColumnsTabsIntoFocusedColumnInOrder()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(c, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(d, WorkspaceKind.Agent);
        layout.SplitWorkspaceToNewPane(b);
        layout.SplitWorkspaceToNewPane(c);
        // Columns now: [a, d] | [c] | [b] (each new column lands right of its
        // source, so b's column is last). Focus the column holding b.
        var bColumnId = layout.Snapshot.Columns.Single(column => column.Tabs.Any(tab => tab.WorkspaceId == b)).ColumnId;
        layout.FocusPane(bColumnId);

        layout.CollapseToSinglePane();

        var snapshot = layout.Snapshot;
        var column = snapshot.Columns.Single();
        Assert.AreEqual(1.0, column.Ratio);
        Assert.AreEqual(layout.Snapshot.FocusedColumnId, column.ColumnId);
        Assert.AreEqual(b, column.ActiveTabId, "the focused column keeps its own active tab");
        CollectionAssert.AreEqual(
            new[] { b, a, d, c },
            column.Tabs.Select(tab => tab.WorkspaceId).ToArray(),
            "other columns' tabs join the stack tail in original column order");
    }

    [TestMethod]
    public void SetPaneRatios_ReplacesTheWholeVectorAtomically()
    {
        var (layout, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);
        layout.SplitWorkspaceToNewPane(b);
        var before = layout.Snapshot;
        var first = before.Columns[0].ColumnId;
        var second = before.Columns[1].ColumnId;

        var accepted = layout.SetPaneRatios(before.LayoutRevision, new Dictionary<string, double>
        {
            [first] = 0.72,
            [second] = 0.28
        });

        var snapshot = layout.Snapshot;
        Assert.IsTrue(accepted);
        Assert.AreEqual(before.LayoutRevision + 1, snapshot.LayoutRevision);
        Assert.AreEqual(0.72, snapshot.Columns[0].Ratio);
        Assert.AreEqual(0.28, snapshot.Columns[1].Ratio);
    }

    [TestMethod]
    public void SetPaneRatios_RejectsStaleOrMalformedVectorsWithoutChangingLayout()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);
        var before = layout.Snapshot;
        var valid = before.Columns.ToDictionary(column => column.ColumnId, column => column.Ratio);

        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision - 1, valid), "stale revision");
        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision, valid.Take(2).ToDictionary()), "missing column");
        Assert.IsFalse(layout.SetPaneRatios(before.LayoutRevision, valid.ToDictionary(pair => pair.Key, _ => 0.1)), "sum mismatch");
        Assert.IsFalse(layout.SetPaneRatios(
            before.LayoutRevision,
            valid.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == before.Columns[0].ColumnId ? double.NaN : pair.Value)),
            "non-finite ratio");

        var snapshot = layout.Snapshot;
        Assert.AreEqual(before.LayoutRevision, snapshot.LayoutRevision);
        CollectionAssert.AreEqual(
            before.Columns.Select(column => column.Ratio).ToArray(),
            snapshot.Columns.Select(column => column.Ratio).ToArray());
    }

    [TestMethod]
    public void PendingPlacement_NewRight_CreatedAtomicallyAtAssignment()
    {
        var (layout, broadcasts) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);

        layout.RecordPendingPlacement(WorkspaceLayoutService.NewPanePlacement);
        layout.AssignActiveWorkspace(b, WorkspaceKind.Terminal);

        // No snapshot ever shows an empty second column: creation + assignment
        // + focus land in one revision.
        foreach (var snapshot in broadcasts)
        {
            foreach (var column in snapshot.Columns)
                Assert.IsTrue(column.Tabs.Count > 0 || snapshot.Columns.Count == 1);
        }
        var final = layout.Snapshot;
        Assert.HasCount(2, final.Columns);
        Assert.AreEqual(b, final.Columns[1].ActiveTabId);
        Assert.AreEqual(b, final.Columns[1].Tabs.Single().WorkspaceId);
        Assert.AreEqual(final.Columns[1].ColumnId, final.FocusedColumnId);
    }

    [TestMethod]
    public void FocusPane_UnknownOrCurrent_IsNoOp()
    {
        var (layout, broadcasts) = Create();
        var a = Guid.NewGuid();
        layout.AssignActiveWorkspace(a, WorkspaceKind.Agent);
        broadcasts.Clear();

        layout.FocusPane("column-999");
        layout.FocusPane(WorkspaceLayoutService.FirstColumnId);

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
    public void Split_ToThreeColumns_NormalizesRatios()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, WorkspaceLayoutService.MaxColumns).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);

        var snapshot = layout.Snapshot;
        Assert.HasCount(3, snapshot.Columns);
        foreach (var column in snapshot.Columns)
            Assert.AreEqual(Math.Round(1.0 / 3, 4), column.Ratio);
        Assert.AreEqual(snapshot.Columns[2].ColumnId, snapshot.FocusedColumnId);
        Assert.AreEqual(ids[2], layout.FocusedWorkspaceId);
    }

    [TestMethod]
    public void Split_AtThreeColumnCap_IsRejectedWithoutChangingAssignments()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, WorkspaceLayoutService.MaxColumns).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);
        var extra = Guid.NewGuid();

        var before = layout.Snapshot;
        var accepted = layout.SplitWorkspaceToNewPane(extra);

        var snapshot = layout.Snapshot;
        Assert.IsFalse(accepted);
        Assert.HasCount(WorkspaceLayoutService.MaxColumns, snapshot.Columns);
        Assert.AreEqual(before.LayoutRevision, snapshot.LayoutRevision);
    }

    [TestMethod]
    public void RemoveWorkspace_MiddleColumn_CollapsesAndFocusFallsLeft()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, WorkspaceLayoutService.MaxColumns).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);
        layout.FocusPane(layout.Snapshot.Columns[1].ColumnId);

        layout.RemoveWorkspace(ids[1]);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Columns);
        Assert.AreEqual(ids[0], layout.FocusedWorkspaceId, "focus falls to the left neighbor column");
        foreach (var column in snapshot.Columns)
            Assert.AreEqual(Math.Round(1.0 / 2, 4), column.Ratio);
        Assert.IsFalse(snapshot.Columns.Any(c => c.Tabs.Any(tab => tab.WorkspaceId == ids[1])));
    }

    [TestMethod]
    public void RemoveWorkspace_DestroyedColumn_RatioAbsorbedProportionallyNotEqualized()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, WorkspaceLayoutService.MaxColumns).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);
        var before = layout.Snapshot;
        var middleId = before.Columns.Single(column => column.Tabs.Any(tab => tab.WorkspaceId == ids[1])).ColumnId;
        var edgeIds = before.Columns.Where(column => column.ColumnId != middleId).Select(column => column.ColumnId).ToArray();
        Assert.HasCount(2, edgeIds);
        // A user drag left the columns at 0.5 / 0.3 / 0.2.
        Assert.IsTrue(layout.SetPaneRatios(before.LayoutRevision, new Dictionary<string, double>
        {
            [edgeIds[0]] = 0.5,
            [middleId] = 0.3,
            [edgeIds[1]] = 0.2
        }));

        layout.RemoveWorkspace(ids[1]);

        var snapshot = layout.Snapshot;
        Assert.HasCount(2, snapshot.Columns);
        var ratios = snapshot.Columns.ToDictionary(column => column.ColumnId, column => column.Ratio);
        Assert.AreEqual(0.5 / 0.7, ratios[edgeIds[0]], 0.001, "0.5 keeps its relative weight of the remainder");
        Assert.AreEqual(0.2 / 0.7, ratios[edgeIds[1]], 0.001, "0.2 keeps its relative weight of the remainder");
        Assert.AreEqual(1.0, snapshot.Columns.Sum(column => column.Ratio), 0.001);
        Assert.IsFalse(snapshot.Columns.Any(c => c.Tabs.Any(tab => tab.WorkspaceId == ids[1])));
    }

    [TestMethod]
    public void FocusAdjacentPane_WrapsAroundAllColumns()
    {
        var (layout, _) = Create();
        var ids = Enumerable.Range(0, WorkspaceLayoutService.MaxColumns).Select(_ => Guid.NewGuid()).ToArray();
        layout.AssignActiveWorkspace(ids[0], WorkspaceKind.Agent);
        for (var i = 1; i < ids.Length; i++)
            layout.SplitWorkspaceToNewPane(ids[i]);

        layout.FocusAdjacentPane(1);
        Assert.AreEqual(ids[0], layout.FocusedWorkspaceId, "wraps past the last column");
        layout.FocusAdjacentPane(-1);
        Assert.AreEqual(ids[2], layout.FocusedWorkspaceId, "wraps back");
    }
}
