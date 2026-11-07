using System.Collections.ObjectModel;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class SessionRowReconcilerTests
{
    private const long StartTicks = 638_000_000_000_000_000;

    private static SessionObservation Observation(int processId, long startTicks, long workingSet = 1) =>
        new(processId, startTicks, "version-0", processId, "Client", false, true, workingSet, 1, 1, DateTimeOffset.UnixEpoch);

    private static (SessionRowReconciler Reconciler, List<SessionRow> Removed) Create()
    {
        var removed = new List<SessionRow>();
        return (new SessionRowReconciler(removed.Add, observation => new SessionRow(observation)), removed);
    }

    [Fact]
    public void ReconcileOf128UnchangedClientsKeepsRowIdentityAndDoesNotAllocatePerComparison()
    {
        var (reconciler, removed) = Create();
        var first = new SessionObservation[128];
        for (int i = 0; i < first.Length; i++) first[i] = Observation(1000 + i, StartTicks + i * 7L, 100);
        var rows = new ObservableCollection<SessionRow>();
        Assert.True(reconciler.Reconcile(rows, first));
        Assert.False(reconciler.Reconcile(rows, first)); // Warm the matching path before measuring it.
        var original = rows.ToArray();
        // The same processes with new measurements, reported in the opposite order.
        var second = new SessionObservation[first.Length];
        for (int i = 0; i < second.Length; i++) second[i] = first[first.Length - 1 - i] with { WorkingSetBytes = 200 + i };

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = reconciler.Reconcile(rows, second);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(changed);
        Assert.True(allocated < 8 * 1024,
            $"Matching 128 unchanged clients allocated {allocated} managed bytes; row matching should compare process keys, not build key strings per comparison.");
        Assert.Empty(removed);
        Assert.Equal(original.Length, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Same(original[i], rows[i]);
            Assert.Same(second[second.Length - 1 - i], rows[i].Observation);
        }
    }

    [Fact]
    public void ReconcileRemovesAndReleasesARowWhoseProcessIsGone()
    {
        var (reconciler, removed) = Create();
        var rows = new ObservableCollection<SessionRow>();
        var a = Observation(10, StartTicks); var b = Observation(20, StartTicks); var c = Observation(30, StartTicks);
        reconciler.Reconcile(rows, [a, b, c]);
        var (rowA, rowB, rowC) = (rows[0], rows[1], rows[2]);

        Assert.True(reconciler.Reconcile(rows, [c, a]));

        Assert.Equal(new[] { rowB }, removed);
        Assert.Equal(new[] { rowA, rowC }, rows);
    }

    [Fact]
    public void ReconcileReplacesARowWhenAProcessIdIsReusedWithANewStartTime()
    {
        var (reconciler, removed) = Create();
        var rows = new ObservableCollection<SessionRow>();
        var stable = Observation(5, StartTicks); var original = Observation(10, StartTicks + 100);
        reconciler.Reconcile(rows, [stable, original]);
        var (stableRow, originalRow) = (rows[0], rows[1]);
        var reused = Observation(10, StartTicks + 200);

        Assert.True(reconciler.Reconcile(rows, [reused, stable]));

        Assert.Equal(new[] { originalRow }, removed);
        Assert.Equal(2, rows.Count);
        Assert.Same(stableRow, rows[0]);
        Assert.NotSame(originalRow, rows[1]);
        Assert.Same(reused, rows[1].Observation);
    }

    [Fact]
    public void ReconcileKeepsOneRowForADuplicateProcessInOneSnapshot()
    {
        var (reconciler, removed) = Create();
        var rows = new ObservableCollection<SessionRow>();
        var earlier = Observation(10, StartTicks, 100); var later = Observation(10, StartTicks, 200);

        Assert.True(reconciler.Reconcile(rows, [earlier, later]));
        var row = Assert.Single(rows);
        Assert.Same(later, row.Observation);

        Assert.False(reconciler.Reconcile(rows, [earlier]));
        Assert.Same(row, Assert.Single(rows));
        Assert.Same(earlier, row.Observation);
        Assert.Empty(removed);
    }
}
