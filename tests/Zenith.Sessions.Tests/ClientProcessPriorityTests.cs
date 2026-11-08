using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientProcessPriorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid) => new(pid, Now.AddMinutes(-1).UtcTicks, "Synthetic",
        0, "", false, false, 0, 0, null, Now);

    [Fact]
    public async Task AnExplicitWindowlessClientChangeAndRestoreUseTheOriginalClassAndReleaseEveryHandle()
    {
        await using var fixture = new Fixture(1);
        var applied = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        var row = Assert.Single(applied.Results);
        Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status);
        Assert.Equal(ClientProcessPriorityClass.Normal, row.Previous);
        Assert.Equal(ClientProcessPriorityClass.BelowNormal, row.Requested);
        Assert.Equal(ClientProcessPriorityClass.BelowNormal, row.Current);
        Assert.True(row.RestorePending);
        Assert.Equal(1, applied.PendingRestoreCount);
        Assert.True(applied.AllApplied);
        Assert.Equal(3, fixture.Native.Reads[1]); // Initial query, final comparison, accepted-set readback.
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));

        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
        Assert.Equal(0, restored.PendingRestoreCount);
        Assert.Equal(0x20u, fixture.Native.Clients[1].Priority);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Theory]
    [InlineData(0x20u, ClientProcessPriorityAction.Normal)]
    [InlineData(0x4000u, ClientProcessPriorityAction.BelowNormal)]
    public async Task AlreadyMatchingSettingsDoNotCreateRestorationOwnership(uint priority, ClientProcessPriorityAction action)
    {
        await using var fixture = new Fixture(1);
        fixture.Native.Clients[1].Priority = priority;
        var result = await fixture.Change(action, 1);
        Assert.Equal(ClientProcessPriorityStatus.Unchanged, Assert.Single(result.Results).Status);
        Assert.Equal(0, result.PendingRestoreCount);
        Assert.Empty(fixture.Native.Sets);
    }

    [Fact]
    public async Task ApplyingTheOriginalClassClearsOnlyThisOwnersPreviouslyConfirmedChange()
    {
        await using var fixture = new Fixture(1);
        fixture.Native.Clients[1].Priority = 0x4000;
        await fixture.Change(ClientProcessPriorityAction.Normal, 1);
        Assert.Equal(1, fixture.Control.PendingRestoreCount);
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(ClientProcessPriorityStatus.Applied, Assert.Single(result.Results).Status);
        Assert.Equal(0, result.PendingRestoreCount);
        Assert.Equal(0x4000u, fixture.Native.Clients[1].Priority);
    }

    [Theory]
    [InlineData(0x40u)]
    [InlineData(0x80u)]
    [InlineData(0x100u)]
    [InlineData(0x8000u)]
    [InlineData(0x1234u)]
    public async Task UnsupportedExistingClassesArePreserved(uint priority)
    {
        await using var fixture = new Fixture(1);
        fixture.Native.Clients[1].Priority = priority;
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        var row = Assert.Single(result.Results);
        Assert.Equal(ClientProcessPriorityStatus.UnsupportedPriority, row.Status);
        Assert.Equal(ClientProcessPriorityClass.Unsupported, row.Current);
        Assert.Equal(priority, fixture.Native.Clients[1].Priority);
        Assert.Empty(fixture.Native.Sets);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData("user", ClientProcessPriorityStatus.AccessDenied)]
    [InlineData("image", ClientProcessPriorityStatus.InvalidClient)]
    [InlineData("start", ClientProcessPriorityStatus.InvalidClient)]
    [InlineData("pid", ClientProcessPriorityStatus.InvalidClient)]
    [InlineData("exited", ClientProcessPriorityStatus.InvalidClient)]
    [InlineData("denied", ClientProcessPriorityStatus.AccessDenied)]
    public async Task NativeAdapterRejectsAnUnrelatedInaccessibleOrReusedProcess(string changed, ClientProcessPriorityStatus expected)
    {
        await using var fixture = new Fixture(1);
        var client = fixture.Native.Clients[1];
        switch (changed)
        {
            case "user": client.User = "S-1-5-21-other"; break;
            case "image": client.Image = @"C:\owned-fixture\Other.exe"; break;
            case "start": client.Start++; break;
            case "pid": client.Pid++; break;
            case "exited": client.Running = false; break;
            default: fixture.Native.OpenError = 5; break;
        }
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(expected, Assert.Single(result.Results).Status);
        Assert.Empty(fixture.Native.Sets);
        Assert.Equal(0, result.PendingRestoreCount);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("image")]
    [InlineData("start")]
    [InlineData("external-priority")]
    public async Task FinalNativeComparisonRefusesChangesThatAppearAfterTheFirstRead(string changed)
    {
        await using var fixture = new Fixture(1);
        fixture.Native.BeforeRead = (client, count) =>
        {
            if (count != 2) return;
            switch (changed)
            {
                case "user": client.User = "S-1-5-21-other"; break;
                case "image": client.Image = @"C:\owned-fixture\Other.exe"; break;
                case "start": client.Start++; break;
                default: client.Priority = 0x4000; break;
            }
        };
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(changed == "external-priority" ? ClientProcessPriorityStatus.Conflict
            : changed == "user" ? ClientProcessPriorityStatus.AccessDenied : ClientProcessPriorityStatus.InvalidClient,
            Assert.Single(result.Results).Status);
        Assert.Empty(fixture.Native.Sets);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationOrPanicDuringFinalValidationPreventsMutation(bool panic)
    {
        await using var fixture = new Fixture(1);
        using var stop = new CancellationTokenSource();
        fixture.Native.BeforeRead = (_, count) =>
        {
            if (count != 2) return;
            if (panic) fixture.Control.CancelOperations(); else stop.Cancel();
        };
        var result = await fixture.Control.ChangeAsync([Session(1)], ClientProcessPriorityAction.BelowNormal, stop.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(ClientProcessPriorityStatus.Cancelled, Assert.Single(result.Results).Status);
        Assert.Empty(fixture.Native.Sets);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAfterAcceptedSetStillReadsBackAndRetainsConfirmedOutcomeBeforeStoppingTheNextClient(bool panic)
    {
        await using var fixture = new Fixture(1, 2);
        using var stop = new CancellationTokenSource();
        fixture.Native.AfterSet = _ => { if (panic) fixture.Control.CancelOperations(); else stop.Cancel(); };
        var result = await fixture.Control.ChangeAsync([Session(1), Session(2)], ClientProcessPriorityAction.BelowNormal, stop.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(ClientProcessPriorityStatus.Applied, result.Results[0].Status);
        Assert.Equal(ClientProcessPriorityClass.BelowNormal, result.Results[0].Current);
        Assert.Equal(ClientProcessPriorityStatus.Cancelled, result.Results[1].Status);
        Assert.Equal(3, fixture.Native.Reads[1]);
        Assert.Equal((1, 0x4000u), Assert.Single(fixture.Native.Sets));
        Assert.Equal(1, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("throws")]
    [InlineData("mismatch")]
    public async Task AcceptedSetWithUnconfirmedReadbackKeepsItsOriginalAndLastAttemptedOwnership(string failure)
    {
        await using var fixture = new Fixture(1);
        fixture.Native.BeforeRead = (client, count) =>
        {
            if (count != 3) return;
            if (failure == "throws") throw new Win32Exception("SYNTHETIC_PRIVATE_ERROR");
            if (failure == "failed") fixture.Native.FailRead = true;
            else client.Priority = 0x20;
        };
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        var row = Assert.Single(result.Results);
        Assert.Equal(ClientProcessPriorityStatus.NotConfirmed, row.Status);
        Assert.True(row.RestorePending);
        Assert.Equal(1, result.PendingRestoreCount);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ERROR", row.ToString(), StringComparison.Ordinal);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task ASetThatMutatesThenThrowsCannotLoseItsRestorationReservation()
    {
        await using var fixture = new Fixture(1);
        fixture.Native.AfterSet = _ => throw new Win32Exception("SYNTHETIC_SECRET_ERROR");
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(ClientProcessPriorityStatus.NotConfirmed, Assert.Single(result.Results).Status);
        Assert.Equal(0x4000u, fixture.Native.Clients[1].Priority);
        Assert.Equal(1, fixture.Control.PendingRestoreCount);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ERROR", result.Results[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedNativeSetDoesNotCreateFalseRestorationOwnership()
    {
        await using var fixture = new Fixture(1);
        fixture.Native.DenySet = true;
        var result = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(ClientProcessPriorityStatus.AccessDenied, Assert.Single(result.Results).Status);
        Assert.Equal(0x20u, fixture.Native.Clients[1].Priority);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData(0x80u, ClientProcessPriorityStatus.UnsupportedPriority)]
    [InlineData(0x40u, ClientProcessPriorityStatus.UnsupportedPriority)]
    public async Task RestorePreservesObservedExternalChangesAndKeepsOwnership(uint external, ClientProcessPriorityStatus expected)
    {
        await using var fixture = new Fixture(1);
        await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        fixture.Native.Clients[1].Priority = external;
        int sets = fixture.Native.Sets.Count;
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(expected, Assert.Single(restored.Results).Status);
        Assert.Equal(external, fixture.Native.Clients[1].Priority);
        Assert.Equal(sets, fixture.Native.Sets.Count);
        Assert.Equal(1, restored.PendingRestoreCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreCanRetireOwnershipWhenTheOriginalClassIsAlreadyConfirmed(bool duringFinalComparison)
    {
        await using var fixture = new Fixture(1);
        await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        if (duringFinalComparison)
            fixture.Native.BeforeRead = (client, count) => { if (count == 5) client.Priority = 0x20; };
        else fixture.Native.Clients[1].Priority = 0x20;
        var restored = await fixture.Control.RestoreAllAsync(default);
        var row = Assert.Single(restored.Results);
        Assert.Equal(ClientProcessPriorityStatus.Unchanged, row.Status);
        Assert.Equal(ClientProcessPriorityClass.Normal, row.Current);
        Assert.False(row.RestorePending);
        Assert.Equal(0, restored.PendingRestoreCount);
        Assert.Single(fixture.Native.Sets);
        Assert.Equal(1, fixture.Native.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreNeverAppliesOldOwnershipToAReplacementProcessOrAConclusiveExit(bool reused)
    {
        await using var fixture = new Fixture(1);
        await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        if (reused) fixture.Native.Clients[1] = new Client(1) { Start = Session(1).ProcessStartUtcTicks + 1 };
        else fixture.Native.Clients[1].Running = false;
        var result = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.InvalidClient, Assert.Single(result.Results).Status);
        Assert.Single(fixture.Native.Sets);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData(87, ClientProcessPriorityStatus.InvalidClient, false)]
    [InlineData(5, ClientProcessPriorityStatus.AccessDenied, true)]
    [InlineData(6, ClientProcessPriorityStatus.NotConfirmed, true)]
    public async Task AnAbsentProcessObjectRetiresOwnershipButAccessFailuresKeepIt(int error,
        ClientProcessPriorityStatus expected, bool pending)
    {
        await using var fixture = new Fixture(1);
        await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        fixture.Native.OpenError = error;
        var restored = await fixture.Control.RestoreAllAsync(default);
        var row = Assert.Single(restored.Results);
        Assert.Equal(expected, row.Status);
        Assert.Equal(pending, row.RestorePending);
        Assert.Equal(pending ? 1 : 0, restored.PendingRestoreCount);
        Assert.Single(fixture.Native.Sets);
    }

    [Fact]
    public async Task RestoreAllCanRetireTheOldIdentityAndRestoreTheNewOwnedIdentityOfAReusedPid()
    {
        await using var fixture = new Fixture(1);
        await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        var replacement = Session(1) with { ProcessStartUtcTicks = Session(1).ProcessStartUtcTicks + 1 };
        fixture.Native.Clients[1] = new Client(1) { Start = replacement.ProcessStartUtcTicks };
        var applied = await fixture.Control.ChangeAsync([replacement], ClientProcessPriorityAction.BelowNormal, default);
        Assert.Equal(2, applied.PendingRestoreCount);
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Contains(restored.Results, row => row.Session.ProcessStartUtcTicks == Session(1).ProcessStartUtcTicks
            && row.Status == ClientProcessPriorityStatus.InvalidClient);
        Assert.Contains(restored.Results, row => row.Session.ProcessStartUtcTicks == replacement.ProcessStartUtcTicks
            && row.Status == ClientProcessPriorityStatus.Restored);
        Assert.Equal(0, restored.PendingRestoreCount);
        Assert.Equal(new[] { (1, 0x4000u), (1, 0x4000u), (1, 0x20u) }, fixture.Native.Sets);
    }

    [Fact]
    public async Task DuplicatePidWithSameOrConflictingStartsCannotBeAppliedTwice()
    {
        await using var fixture = new Fixture(1);
        var result = await fixture.Control.ChangeAsync([Session(1), Session(1) with { ProcessStartUtcTicks = Session(1).ProcessStartUtcTicks + 1 }],
            ClientProcessPriorityAction.BelowNormal, default);
        Assert.All(result.Results, row => Assert.Equal(ClientProcessPriorityStatus.InvalidClient, row.Status));
        Assert.Empty(fixture.Native.Handles);
        Assert.Empty(fixture.Native.Sets);
    }

    [Fact]
    public async Task UnresolvedOriginalsAreBoundedAndNeverEvictedToMakeRoom()
    {
        await using var fixture = new Fixture(Enumerable.Range(1, 129).ToArray());
        var filled = await fixture.Change(ClientProcessPriorityAction.BelowNormal, Enumerable.Range(1, 128).ToArray());
        Assert.Equal(128, filled.PendingRestoreCount);
        var refused = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 129);
        Assert.Equal(ClientProcessPriorityStatus.CapacityReached, Assert.Single(refused.Results).Status);
        Assert.Equal(128, fixture.Native.Sets.Count);
        Assert.Equal(0x20u, fixture.Native.Clients[129].Priority);
        Assert.Equal(128, fixture.Control.PendingRestoreCount);
        var unchanged = await fixture.Change(ClientProcessPriorityAction.Normal, 129);
        Assert.Equal(ClientProcessPriorityStatus.Unchanged, Assert.Single(unchanged.Results).Status);
        Assert.Equal(128, unchanged.PendingRestoreCount);
        Assert.Throws<ArgumentException>(() =>
        {
            _ = fixture.Control.ChangeAsync(Enumerable.Range(1, 129).Select(Session).ToArray(), ClientProcessPriorityAction.BelowNormal, default);
        });
    }

    [Fact]
    public async Task ActiveNativeWorkCannotBeQueuedOverOrAbandonedByDispose()
    {
        var fixture = new Fixture(1, 2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Native.BeforeSet = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic release was not signalled.");
        };
        Task<ClientProcessPriorityBatchResult> change = fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var busy = await fixture.Change(ClientProcessPriorityAction.Normal, 2);
            Assert.Equal(ClientProcessPriorityStatus.Busy, busy.Rejection);
            Assert.Equal(ClientProcessPriorityStatus.Busy, Assert.Single(busy.Results).Status);
            disposal = fixture.Control.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Contains(fixture.Native.Handles, handle => !handle.IsClosed);
        }
        finally
        {
            fixture.Native.BeforeSet = null;
            release.Set();
            await change.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.DisposeAsync();
        }
        Assert.Equal(0x20u, fixture.Native.Clients[1].Priority);
        Assert.Equal(0, fixture.Control.PendingRestoreCount);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
        var closed = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(ClientProcessPriorityStatus.Closed, closed.Rejection);
    }

    [Fact]
    public async Task DisposalUsesIndependentCleanupAfterCallerCancellationAndRestoresAllAcceptedChanges()
    {
        var fixture = new Fixture(1);
        using var stop = new CancellationTokenSource();
        fixture.Native.AfterSet = _ => stop.Cancel();
        var result = await fixture.Control.ChangeAsync([Session(1)], ClientProcessPriorityAction.BelowNormal, stop.Token);
        Assert.True(result.Cancelled);
        fixture.Native.AfterSet = null;
        await fixture.Control.DisposeAsync();
        Assert.Equal(new[] { (1, 0x4000u), (1, 0x20u) }, fixture.Native.Sets);
        Assert.Equal(0, fixture.Control.PendingRestoreCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task FailedDisposalRetainsOwnershipAndRepeatsTheSameFixedFailureWithoutNewMutations()
    {
        var fixture = new Fixture(1);
        await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        fixture.Native.DenySet = true;
        Task first = fixture.Control.DisposeAsync().AsTask();
        var error = await Assert.ThrowsAsync<ClientProcessPriorityCleanupException>(() => first);
        int attempts = fixture.Native.Attempts;
        Task repeated = fixture.Control.DisposeAsync().AsTask();
        Assert.Same(first, repeated);
        Assert.Same(error, await Assert.ThrowsAsync<ClientProcessPriorityCleanupException>(() => repeated));
        Assert.Null(error.InnerException);
        Assert.Equal(1, error.PendingRestoreCount);
        Assert.Equal(1, fixture.Control.PendingRestoreCount);
        Assert.Equal(attempts, fixture.Native.Attempts);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
        fixture.Native.Clients[1].Priority = 0x20; // Owned in-memory fixture cleanup, not another controller mutation.
    }

    [Fact]
    public async Task SelectionIsPinnedAndAStopWhileReadingTheSelectionCannotBecomeNewPermission()
    {
        await using var fixture = new Fixture(1, 2);
        var selection = new RevokingSelection([Session(1)], fixture.Control.CancelOperations);
        var result = await fixture.Control.ChangeAsync(selection, ClientProcessPriorityAction.BelowNormal, default);
        Assert.True(result.Cancelled);
        Assert.Equal(ClientProcessPriorityStatus.Cancelled, Assert.Single(result.Results).Status);
        Assert.Empty(fixture.Native.Handles);
    }

    [Fact]
    public void BatchResultsOwnTheirList()
    {
        var source = new List<ClientProcessPriorityResult>
        {
            new(Session(1), ClientProcessPriorityAction.Normal, ClientProcessPriorityStatus.Unchanged, null, null, null, false)
        };
        var result = new ClientProcessPriorityBatchResult(source, false, 0);
        source.Clear();
        Assert.Single(result.Results);
    }

    [Fact]
    public async Task EfficiencyModeIsConfirmedAndRestoreReturnsTheClientsOwnOriginalValues()
    {
        await using var fixture = new Fixture(1);
        var client = fixture.Native.Clients[1];
        // The client's own values are deliberately not Windows' defaults: restore must return these, not "normal".
        client.Throttling = new(0x4, 0x0);
        client.MemoryPriority = 3;
        var applied = await fixture.Change(ClientProcessPriorityAction.Efficiency, 1);
        var row = Assert.Single(applied.Results);
        Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status);
        Assert.Equal(ClientProcessPriorityClass.Normal, row.Previous);
        Assert.Equal(ClientProcessPriorityClass.BelowNormal, row.Requested);
        Assert.Equal(ClientProcessPriorityClass.BelowNormal, row.Current);
        Assert.True(row.RestorePending);
        Assert.Contains("efficiency mode", row.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0x4000u, client.Priority);
        Assert.Equal(new ClientPowerThrottling(0x5, 0x1), client.Throttling); // EcoQoS on; its other control bit kept.
        Assert.Equal(1u, client.MemoryPriority);
        Assert.Equal(0xFFul, client.Affinity); // Efficiency leaves the cores alone.
        Assert.Equal(3, fixture.Native.Reads[1]); // Initial query, final comparison, accepted-set readback.
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));

        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
        Assert.Equal(0, restored.PendingRestoreCount);
        Assert.Equal(new ClientProcessSettings(0x20, new(0x4, 0x0), 3, 0xFF), client.State);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task NormalAfterEfficiencyChangesOnlyThePriorityAndRestoreStillReturnsEveryOriginal()
    {
        await using var fixture = new Fixture(1);
        var client = fixture.Native.Clients[1];
        await fixture.Change(ClientProcessPriorityAction.Efficiency, 1);
        var normal = await fixture.Change(ClientProcessPriorityAction.Normal, 1);
        var row = Assert.Single(normal.Results);
        Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status);
        Assert.True(row.RestorePending);
        Assert.Equal(new ClientProcessSettings(0x20, new(0x1, 0x1), 1, 0xFF), client.State);

        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
        Assert.Equal(new ClientProcessSettings(0x20, new(0, 0), 5, 0xFF), client.State);
        Assert.Equal(0, restored.PendingRestoreCount);
    }

    [Theory]
    [InlineData("throttling")]
    [InlineData("memory")]
    public async Task EfficiencyIsRefusedWithoutAnyWriteWhenWindowsDoesNotReportAnOriginalValue(string unknown)
    {
        await using var fixture = new Fixture(1);
        if (unknown == "throttling") fixture.Native.Clients[1].Throttling = null;
        else fixture.Native.Clients[1].MemoryPriority = null;
        var result = await fixture.Change(ClientProcessPriorityAction.Efficiency, 1);
        Assert.Equal(ClientProcessPriorityStatus.SettingUnavailable, Assert.Single(result.Results).Status);
        Assert.Equal(0, fixture.Native.Attempts);
        Assert.Equal(0, result.PendingRestoreCount);
        // Normal and Below normal need only the priority class, so they still work for this client.
        var below = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(ClientProcessPriorityStatus.Applied, Assert.Single(below.Results).Status);
        Assert.Empty(fixture.Native.OtherSets);
    }

    [Fact]
    public async Task AnEfficiencyChangeRefusedPartwayKeepsOwnershipOfWhatWindowsAccepted()
    {
        await using var fixture = new Fixture(1);
        var client = fixture.Native.Clients[1];
        fixture.Native.DenyThrottling = true;
        var result = await fixture.Change(ClientProcessPriorityAction.Efficiency, 1);
        var row = Assert.Single(result.Results);
        Assert.Equal(ClientProcessPriorityStatus.NotConfirmed, row.Status);
        Assert.True(row.RestorePending);
        Assert.Equal(1, result.PendingRestoreCount);
        Assert.Equal(new ClientProcessSettings(0x20, new(0, 0), 1, 0xFF), client.State); // Memory accepted; nothing after the refusal.
        Assert.Equal(3, fixture.Native.Reads[1]); // The accepted part was still read back.
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
        // Owned in-memory fixture cleanup, not another controller mutation: back to the original so Exit retires ownership.
        client.MemoryPriority = 5;
        fixture.Native.LastSet.Remove(1);
    }

    [Fact]
    public async Task SpreadGivesEachSelectedClientItsOwnCoresAndRestoreReturnsEachOriginalAffinity()
    {
        await using var fixture = new Fixture(1, 2);
        fixture.Native.Clients[2].Affinity = 0x0F; // Already restricted before Zenith changed it.
        var spread = await fixture.Change(ClientProcessPriorityAction.SpreadCores, 1, 2);
        Assert.All(spread.Results, row => Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status));
        Assert.Contains("cores", spread.Results[0].Explanation, StringComparison.Ordinal);
        Assert.Equal(0x55ul, fixture.Native.Clients[1].Affinity);
        Assert.Equal(0xAAul, fixture.Native.Clients[2].Affinity);
        Assert.Empty(fixture.Native.Sets); // Spreading leaves the priority class alone.
        Assert.Equal(2, spread.PendingRestoreCount);

        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.All(restored.Results, row => Assert.Equal(ClientProcessPriorityStatus.Restored, row.Status));
        Assert.Equal(0xFFul, fixture.Native.Clients[1].Affinity);
        Assert.Equal(0x0Ful, fixture.Native.Clients[2].Affinity);
        Assert.Equal(0, restored.PendingRestoreCount);
    }

    [Theory]
    [InlineData(1, 0xFFul)] // One client has nothing to spread from.
    [InlineData(3, 0x1Ful)] // Five cores cannot give three clients two each.
    public async Task SpreadIsRefusedWithoutAnyWriteWhenEachClientCannotHaveTwoCores(int clients, ulong system)
    {
        int[] pids = Enumerable.Range(1, clients).ToArray();
        await using var fixture = new Fixture(pids);
        foreach (var client in fixture.Native.Clients.Values) client.Affinity = client.SystemAffinity = system;
        var result = await fixture.Change(ClientProcessPriorityAction.SpreadCores, pids);
        Assert.All(result.Results, row => Assert.Equal(ClientProcessPriorityStatus.NotEnoughCores, row.Status));
        Assert.Equal(0, fixture.Native.Attempts);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Fact]
    public async Task SpreadIsRefusedWithoutAnyWriteWhenWindowsDoesNotReportTheCores()
    {
        await using var fixture = new Fixture(1, 2);
        fixture.Native.Clients[1].Affinity = fixture.Native.Clients[1].SystemAffinity = null; // Threads in several processor groups.
        var result = await fixture.Change(ClientProcessPriorityAction.SpreadCores, 1, 2);
        Assert.Equal(ClientProcessPriorityStatus.SettingUnavailable, result.Results[0].Status);
        Assert.Equal(ClientProcessPriorityStatus.Applied, result.Results[1].Status);
        Assert.Equal(new[] { (2, "affinity aa") }, fixture.Native.OtherSets);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("throttling")]
    [InlineData("affinity")]
    public async Task RestorePreservesAnObservedExternalEfficiencyOrCoreChangeAndKeepsOwnership(string changed)
    {
        await using var fixture = new Fixture(1, 2);
        await fixture.Change(ClientProcessPriorityAction.Efficiency, 1, 2);
        await fixture.Change(ClientProcessPriorityAction.SpreadCores, 1, 2);
        var client = fixture.Native.Clients[1];
        switch (changed)
        {
            case "memory": client.MemoryPriority = 5; break;
            case "throttling": client.Throttling = new(0, 0); break;
            default: client.Affinity = 0xFF; break;
        }
        var external = client.State;
        int sets = fixture.Native.Sets.Count, others = fixture.Native.OtherSets.Count;
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Conflict, restored.Results.Single(row => row.Session.ProcessId == 1).Status);
        Assert.Equal(ClientProcessPriorityStatus.Restored, restored.Results.Single(row => row.Session.ProcessId == 2).Status);
        Assert.Equal(external, client.State);
        Assert.DoesNotContain(fixture.Native.Sets.Skip(sets), set => set.Pid == 1);
        Assert.DoesNotContain(fixture.Native.OtherSets.Skip(others), set => set.Pid == 1);
        Assert.Equal(1, restored.PendingRestoreCount);
    }

    [Fact]
    public async Task RestoreAllWithNoCurrentReservationsStillReportsBusyDuringActualPriorityWork()
    {
        await using var fixture = new Fixture(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Native.BeforeRead = (_, count) =>
        {
            if (count != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic release was not signalled.");
        };
        var operation = fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var busy = await fixture.Control.RestoreAllAsync(default);
            Assert.Empty(busy.Results);
            Assert.Equal(0, busy.PendingRestoreCount);
            Assert.Equal(ClientProcessPriorityStatus.Busy, busy.Rejection);
            Assert.False(busy.AllApplied);
        }
        finally { release.Set(); await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class RevokingSelection(IReadOnlyList<SessionObservation> sessions, Action revoke) : IReadOnlyList<SessionObservation>
    {
        public int Count => sessions.Count;
        public SessionObservation this[int index] { get { revoke(); return sessions[index]; } }
        public IEnumerator<SessionObservation> GetEnumerator() => sessions.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Client(int pid)
    {
        public int Pid = pid;
        public long Start = Session(pid).ProcessStartUtcTicks;
        public bool Running = true;
        public string Image = @"C:\owned-fixture\RobloxPlayerBeta.exe", User = "S-1-5-21-current";
        public uint Priority = 0x20;
        // Null: Windows did not report that value for this client.
        public ClientPowerThrottling? Throttling = new(0, 0);
        public uint? MemoryPriority = 5;
        public ulong? Affinity = 0xFF, SystemAffinity = 0xFF;
        public ClientProcessSettings State => new(Priority, Throttling, MemoryPriority, Affinity);
        public void Apply(ClientProcessSettings state)
            => (Priority, Throttling, MemoryPriority, Affinity) = (state.Priority, state.Throttling, state.MemoryPriority, state.Affinity);
    }

    private sealed class Native : IClientPriorityNativeApi
    {
        public string? CurrentUserSid => "S-1-5-21-current";
        public readonly Dictionary<int, Client> Clients = [];
        public readonly List<SafeProcessHandle> Handles = [];
        public readonly List<(int Pid, uint Priority)> Sets = [];
        public readonly List<(int Pid, string Value)> OtherSets = [];
        public readonly Dictionary<int, ClientProcessSettings> LastSet = [];
        public readonly Dictionary<int, int> Reads = [];
        private readonly Dictionary<SafeProcessHandle, Client> _handles = [];
        public Action<Client, int>? BeforeRead;
        public Action<Client>? BeforeSet, AfterSet;
        public bool DenySet, FailRead, DenyThrottling;
        public int OpenError;
        public int Attempts;
        public SafeProcessHandle Open(int pid, out int error)
        {
            error = OpenError;
            var handle = new SafeProcessHandle(OpenError != 0 ? 0 : (nint)(Handles.Count + 1000), ownsHandle: false);
            Handles.Add(handle);
            if (!handle.IsInvalid) _handles.Add(handle, Clients[pid]);
            return handle;
        }
        public bool Read(SafeProcessHandle handle, out ClientPriorityNativeSnapshot? snapshot)
        {
            var client = _handles[handle];
            int count = Reads.GetValueOrDefault(client.Pid) + 1;
            Reads[client.Pid] = count;
            BeforeRead?.Invoke(client, count);
            snapshot = FailRead ? null : new(client.Pid, client.Start, client.Running, client.Image, client.User, client.Priority,
                client.Throttling, client.MemoryPriority, client.Affinity, client.SystemAffinity);
            return !FailRead;
        }
        public bool Set(SafeProcessHandle handle, uint priority, out int error)
        {
            var client = _handles[handle];
            Attempts++;
            BeforeSet?.Invoke(client);
            error = DenySet ? 5 : 0;
            if (DenySet) return false;
            client.Priority = priority;
            Sets.Add((client.Pid, priority));
            LastSet[client.Pid] = client.State;
            AfterSet?.Invoke(client);
            return true;
        }
        public bool SetThrottling(SafeProcessHandle handle, ClientPowerThrottling throttling, out int error)
            => SetOther(handle, DenyThrottling, out error, client => client.Throttling = throttling, $"throttling {throttling.Control:x}/{throttling.State:x}");
        public bool SetMemoryPriority(SafeProcessHandle handle, uint memoryPriority, out int error)
            => SetOther(handle, false, out error, client => client.MemoryPriority = memoryPriority, $"memory {memoryPriority}");
        public bool SetAffinity(SafeProcessHandle handle, ulong affinity, out int error)
            => SetOther(handle, false, out error, client => client.Affinity = affinity, $"affinity {affinity:x}");
        private bool SetOther(SafeProcessHandle handle, bool deny, out int error, Action<Client> apply, string value)
        {
            var client = _handles[handle];
            Attempts++;
            error = DenySet || deny ? 5 : 0;
            if (error != 0) return false;
            apply(client);
            OtherSets.Add((client.Pid, value));
            LastSet[client.Pid] = client.State;
            return true;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Native Native { get; } = new();
        public WindowsClientProcessPriorityControl Control { get; }
        public Fixture(params int[] pids)
        {
            foreach (int pid in pids) Native.Clients.Add(pid, new(pid));
            Control = new(new NativeClientProcessPriorityAdapter(Native));
        }
        public Task<ClientProcessPriorityBatchResult> Change(ClientProcessPriorityAction action, params int[] pids)
            => Control.ChangeAsync(pids.Select(Session).ToArray(), action, default);
        public async ValueTask DisposeAsync()
        {
            Native.BeforeRead = null;
            Native.BeforeSet = null;
            Native.AfterSet = null;
            Native.OpenError = 0;
            Native.DenySet = Native.FailRead = Native.DenyThrottling = false;
            foreach (var (pid, state) in Native.LastSet)
                if (Native.Clients.TryGetValue(pid, out var client) && client.Start == Session(pid).ProcessStartUtcTicks)
                    client.Apply(state);
            await Control.DisposeAsync();
        }
    }
}
