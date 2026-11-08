using System.ComponentModel;
using System.IO;
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

        // Zenith's own partial write is not "another change": Restore returns the value Windows accepted, and needs no throttling write.
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
        Assert.Equal(0, restored.PendingRestoreCount);
        Assert.Equal(new ClientProcessSettings(0x20, new(0, 0), 5, 0xFF), client.State);
    }

    [Fact]
    public async Task ExitRestoresAnEfficiencyChangeWindowsRefusedPartway()
    {
        var fixture = new Fixture(1);
        var client = fixture.Native.Clients[1];
        fixture.Native.DenyThrottling = true;
        Assert.Equal(ClientProcessPriorityStatus.NotConfirmed,
            Assert.Single((await fixture.Change(ClientProcessPriorityAction.Efficiency, 1)).Results).Status);
        await fixture.Control.DisposeAsync(); // The control itself, without the fixture's reset to what Zenith last set.
        Assert.Equal(0, fixture.Control.PendingRestoreCount);
        Assert.Equal(new ClientProcessSettings(0x20, new(0, 0), 5, 0xFF), client.State);
        await fixture.DisposeAsync();
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
        // The other client alone has nothing to spread from, so it keeps every core instead of half of them.
        Assert.Equal(ClientProcessPriorityStatus.NotEnoughCores, result.Results[1].Status);
        Assert.Contains("two selected clients that can be changed", result.Results[1].Explanation, StringComparison.Ordinal);
        Assert.Equal(0xFFul, fixture.Native.Clients[2].Affinity);
        Assert.Equal(0, fixture.Native.Attempts);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Theory]
    [InlineData("unreported", ClientProcessPriorityStatus.SettingUnavailable)]
    [InlineData("exited", ClientProcessPriorityStatus.InvalidClient)]
    [InlineData("high", ClientProcessPriorityStatus.UnsupportedPriority)]
    [InlineData("conflict", ClientProcessPriorityStatus.Conflict)]
    public async Task SpreadDividesTheCoresOnlyAmongTheClientsItCanChange(string refusal, ClientProcessPriorityStatus expected)
    {
        await using var fixture = new Fixture(1, 2, 3);
        var refused = fixture.Native.Clients[2];
        switch (refusal)
        {
            case "unreported": refused.Affinity = refused.SystemAffinity = null; break; // Threads in several processor groups.
            case "exited": refused.Running = false; break;
            case "high": refused.Priority = 0x80; break;
            default:
                await fixture.Change(ClientProcessPriorityAction.BelowNormal, 2);
                refused.Priority = 0x20; // Changed back elsewhere after Zenith's change.
                break;
        }
        var untouched = refused.State;
        var spread = await fixture.Change(ClientProcessPriorityAction.SpreadCores, 1, 2, 3);
        Assert.Equal(new[] { ClientProcessPriorityStatus.Applied, expected, ClientProcessPriorityStatus.Applied },
            spread.Results.Select(row => row.Status));
        // The refused client leaves no unused share: the two clients that changed split every core between them.
        Assert.Equal(0x55ul, fixture.Native.Clients[1].Affinity);
        Assert.Equal(0xAAul, fixture.Native.Clients[3].Affinity);
        Assert.Contains("No other client changed in the same operation", spread.Results[0].Explanation, StringComparison.Ordinal);
        Assert.Equal(untouched, refused.State);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task PanicWhileSpreadReadsItsClientsChangesNoneAndClosesEveryHandle()
    {
        await using var fixture = new Fixture(1, 2, 3);
        fixture.Native.BeforeRead = (client, _) => { if (client.Pid == 2) fixture.Control.CancelOperations(); };
        var spread = await fixture.Change(ClientProcessPriorityAction.SpreadCores, 1, 2, 3);
        Assert.True(spread.Cancelled);
        Assert.All(spread.Results, row => Assert.Equal(ClientProcessPriorityStatus.Cancelled, row.Status));
        Assert.Equal(0, fixture.Native.Attempts);
        Assert.Equal(0, spread.PendingRestoreCount);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
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

    [Fact]
    public async Task CapCpuPutsTheClientInAJobThisRunHoldsAndRestoreLiftsTheCapWithoutLeavingTheJob()
    {
        var fixture = new Fixture(1);
        long start = Session(1).ProcessStartUtcTicks;
        var applied = await fixture.Control.CapCpuAsync([Session(1)], 25, default);
        var row = Assert.Single(applied.Results);
        Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status);
        Assert.Equal(ClientProcessPriorityAction.CapCpu, row.Action);
        Assert.True(row.RestorePending);
        Assert.Contains("CPU", row.Explanation, StringComparison.Ordinal);
        var job = Assert.Single(fixture.Native.Jobs);
        Assert.Equal([1], job.Pids);
        Assert.Equal(ClientJobCpuRate.Cap(2500), job.Rate); // 25% of all CPU time, in Windows' hundredths of a percent.
        Assert.True(fixture.Ledger.Contains(1, start)); // Recorded first, so a restarted Zenith can name the client.
        Assert.Equal(0x20u, fixture.Native.Clients[1].Priority); // The cap changes nothing else about the client.
        Assert.Empty(fixture.Native.Sets);
        Assert.Equal(1, applied.PendingRestoreCount);
        Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
        // Only a handle can reach a job, so this run keeps the one it created for as long as the client lives.
        Assert.Single(fixture.Native.JobHandles, handle => !handle.IsClosed);

        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
        Assert.Equal(0, restored.PendingRestoreCount);
        // Windows never takes a process out of a job, so Restore clears the job's rate control and leaves the client in it.
        Assert.Equal(ClientJobCpuRate.None, job.Rate);
        Assert.Equal([1], job.Pids);
        Assert.False(fixture.Ledger.Contains(1, start)); // No cap is on the client, so the next start has nothing to name.
        Assert.Single(fixture.Native.JobHandles, handle => !handle.IsClosed);

        // A later cap, with another share, reuses that job through the held handle instead of calling it one this run did not create.
        var again = await fixture.Control.CapCpuAsync([Session(1)], 50, default);
        Assert.Equal(ClientProcessPriorityStatus.Applied, Assert.Single(again.Results).Status);
        Assert.Single(fixture.Native.Jobs);
        Assert.Equal(ClientJobCpuRate.Cap(5000), job.Rate);
        Assert.True(fixture.Ledger.Contains(1, start));
        // The same share again is no change and creates no new ownership.
        var same = await fixture.Control.CapCpuAsync([Session(1)], 50, default);
        Assert.Equal(ClientProcessPriorityStatus.Unchanged, Assert.Single(same.Results).Status);
        Assert.Equal(1, same.PendingRestoreCount);

        // Exit clears the cap, then closes the job handle; the client stays in its now inert job.
        await fixture.DisposeAsync();
        Assert.Equal(0, fixture.Control.PendingRestoreCount);
        Assert.Equal(ClientJobCpuRate.None, job.Rate);
        Assert.False(fixture.Ledger.Contains(1, start));
        Assert.All(fixture.Native.JobHandles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task CapCpuIsRefusedWithoutAnyChangeForAClientAlreadyInAJobThisRunDidNotCreate()
    {
        await using var fixture = new Fixture(1);
        fixture.Native.Clients[1].ForeignJob = true;
        var result = await fixture.Control.CapCpuAsync([Session(1)], 25, default);
        var row = Assert.Single(result.Results);
        Assert.Equal(ClientProcessPriorityStatus.AlreadyInJob, row.Status);
        Assert.Contains("job this run of Zenith did not create", row.Explanation, StringComparison.Ordinal);
        Assert.Empty(fixture.Native.Jobs);
        Assert.Equal(0, fixture.Native.Attempts);
        Assert.Equal(0, result.PendingRestoreCount);
        Assert.Empty(fixture.Ledger.Records);
        // The other controls need no job, so they still work for this client, and Restore never writes the unknown cap.
        var below = await fixture.Change(ClientProcessPriorityAction.BelowNormal, 1);
        Assert.Equal(ClientProcessPriorityStatus.Applied, Assert.Single(below.Results).Status);
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Restored, Assert.Single(restored.Results).Status);
        Assert.Empty(fixture.Native.Jobs);
        Assert.Equal(0x20u, fixture.Native.Clients[1].Priority);
    }

    [Fact]
    public async Task ARestartedControlNamesTheClientsAnEarlierRunLeftCappedCannotCapThemAgainAndDropsExitedClients()
    {
        var fixture = new Fixture(1, 2, 3);
        var capped = await fixture.Control.CapCpuAsync([Session(1), Session(2), Session(3)], 25, default);
        Assert.All(capped.Results, row => Assert.Equal(ClientProcessPriorityStatus.Applied, row.Status));
        Assert.Equal(3, fixture.Ledger.Records.Count);
        // Zenith crashes, so nothing is restored and its job handles are gone. Client 3 then exits.
        fixture.Native.Clients[3].Running = false;
        int writes = fixture.Native.Attempts;

        var restarted = new WindowsClientProcessPriorityControl(new NativeClientProcessPriorityAdapter(fixture.Native, fixture.Ledger));
        try
        {
            var leftovers = await restarted.CheckLeftoverCpuCapsAsync(default);
            Assert.Equal(new ClientCpuCapLeftovers(2, 0, false, false), leftovers);
            Assert.All(fixture.Native.Jobs, job => Assert.Equal(ClientJobCpuRate.Cap(2500), job.Rate)); // Nothing was changed: no run can reach those jobs.
            Assert.Equal(2, fixture.Ledger.Records.Count); // The exited client's record is dropped; the capped ones stay named.
            Assert.False(fixture.Ledger.Contains(3, Session(3).ProcessStartUtcTicks));
            Assert.Equal(writes, fixture.Native.Attempts); // The check writes nothing.
            Assert.All(fixture.Native.Handles, handle => Assert.True(handle.IsClosed));
            Assert.Equal(0, restarted.PendingRestoreCount);
            // The check changes nothing, so it finds the same clients again, and the restarted run cannot cap a client it cannot reach.
            Assert.Equal(new ClientCpuCapLeftovers(2, 0, false, false), await restarted.CheckLeftoverCpuCapsAsync(default));
            var again = await restarted.CapCpuAsync([Session(1)], 10, default);
            Assert.Equal(ClientProcessPriorityStatus.AlreadyInJob, Assert.Single(again.Results).Status);
            Assert.Equal(3, fixture.Native.Jobs.Count);
            Assert.Equal(ClientJobCpuRate.Cap(2500), fixture.Native.Jobs.Single(job => job.Pids.Contains(1)).Rate);
        }
        finally { await restarted.DisposeAsync(); }
        // Had the crashed run lived, its Exit would still reach its own jobs and clear every cap it set.
        await fixture.DisposeAsync();
        Assert.Equal(0, fixture.Control.PendingRestoreCount);
        Assert.All(fixture.Native.Jobs.Where(job => !job.Pids.Contains(3)), job => Assert.Equal(ClientJobCpuRate.None, job.Rate));
        Assert.Empty(fixture.Ledger.Records);
    }

    [Fact]
    public async Task AClientThatExitedReleasesItsJobHandleAndItsRecord()
    {
        await using var fixture = new Fixture(1);
        await fixture.Control.CapCpuAsync([Session(1)], 25, default);
        Assert.Single(fixture.Native.JobHandles, handle => !handle.IsClosed);
        fixture.Native.Clients[1].Running = false;
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.InvalidClient, Assert.Single(restored.Results).Status);
        Assert.Equal(0, restored.PendingRestoreCount);
        Assert.Empty(fixture.Ledger.Records);
        Assert.All(fixture.Native.JobHandles, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task ACapThatCannotBeRecordedForTheNextStartIsRefusedBeforeAnyJobIsCreated()
    {
        await using var fixture = new Fixture(new RefusingLedger(), 1);
        var result = await fixture.Control.CapCpuAsync([Session(1)], 25, default);
        var row = Assert.Single(result.Results);
        Assert.Equal(ClientProcessPriorityStatus.NotConfirmed, row.Status);
        Assert.False(row.RestorePending);
        Assert.Empty(fixture.Native.Jobs);
        Assert.Equal(0, fixture.Native.Attempts);
        Assert.Equal(0, result.PendingRestoreCount);
    }

    [Fact]
    public async Task ARefusedRateAfterTheJobWasCreatedLeavesAnInertJobThatALaterCapReuses()
    {
        await using var fixture = new Fixture(1);
        fixture.Native.DenyCap = true;
        var refused = await fixture.Control.CapCpuAsync([Session(1)], 25, default);
        Assert.Equal(ClientProcessPriorityStatus.AccessDenied, Assert.Single(refused.Results).Status);
        Assert.Equal(0, refused.PendingRestoreCount); // Nothing the user sees changed: no cap, so nothing to restore.
        var job = Assert.Single(fixture.Native.Jobs);
        Assert.Equal(ClientJobCpuRate.None, job.Rate);
        Assert.Equal([1], job.Pids); // Windows cannot undo the membership, so the job stays, with no limits, and its handle is kept.
        Assert.False(fixture.Ledger.Contains(1, Session(1).ProcessStartUtcTicks)); // No cap is on the client, so nothing is recorded.
        Assert.Single(fixture.Native.JobHandles, handle => !handle.IsClosed);

        fixture.Native.DenyCap = false;
        var applied = await fixture.Control.CapCpuAsync([Session(1)], 25, default);
        Assert.Equal(ClientProcessPriorityStatus.Applied, Assert.Single(applied.Results).Status);
        Assert.Single(fixture.Native.Jobs);
        Assert.Equal(ClientJobCpuRate.Cap(2500), job.Rate);
        Assert.True(fixture.Ledger.Contains(1, Session(1).ProcessStartUtcTicks));
    }

    [Fact]
    public async Task RestorePreservesACapChangedOutsideZenithAndReturnsTheCapAndEfficiencyTogether()
    {
        await using var fixture = new Fixture(1, 2);
        await fixture.Control.CapCpuAsync([Session(1), Session(2)], 25, default);
        await fixture.Change(ClientProcessPriorityAction.Efficiency, 1, 2);
        Assert.Equal(2, fixture.Control.PendingRestoreCount);
        var first = fixture.Native.Jobs.Single(job => job.Pids.Contains(1));
        first.Rate = ClientJobCpuRate.Cap(1000); // Changed elsewhere after Zenith's cap.
        var restored = await fixture.Control.RestoreAllAsync(default);
        Assert.Equal(ClientProcessPriorityStatus.Conflict, restored.Results.Single(row => row.Session.ProcessId == 1).Status);
        Assert.Equal(ClientProcessPriorityStatus.Restored, restored.Results.Single(row => row.Session.ProcessId == 2).Status);
        Assert.Equal(ClientJobCpuRate.Cap(1000), first.Rate);
        Assert.Equal(0x4000u, fixture.Native.Clients[1].Priority); // Everything about the conflicting client was preserved.
        Assert.Equal(ClientJobCpuRate.None, fixture.Native.Jobs.Single(job => job.Pids.Contains(2)).Rate);
        Assert.Equal(new ClientProcessSettings(0x20, new(0, 0), 5, 0xFF), fixture.Native.Clients[2].State);
        Assert.Equal(1, restored.PendingRestoreCount);
        first.Rate = ClientJobCpuRate.Cap(2500); // Owned in-memory fixture cleanup so the fixture's Exit can restore it.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(96)]
    [InlineData(100)]
    public async Task ACpuCapShareOutsideFiveToNinetyFivePercentIsRefusedBeforeAnyWork(int percent)
    {
        await using var fixture = new Fixture(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Control.CapCpuAsync([Session(1)], percent, default));
        // The share-less entry point cannot cap either.
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.ChangeAsync([Session(1)], ClientProcessPriorityAction.CapCpu, default));
        Assert.Empty(fixture.Native.Handles);
        Assert.Empty(fixture.Native.Jobs);
    }

    private sealed class RefusingLedger : IClientCpuCapLedger
    {
        public bool Unreadable => false;
        public IReadOnlyList<ClientCpuCapRecord> Records => [];
        public bool Contains(int processId, long startUtcTicks) => false;
        public void Remember(ClientCpuCapRecord record) => throw new IOException("SYNTHETIC_DISK_FULL");
        public void Forget(int processId, long startUtcTicks) { }
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
        // In a Windows job Zenith did not create (for example started from a terminal that uses one).
        public bool ForeignJob;
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
        public bool DenySet, FailRead, DenyThrottling, DenyCap;
        public int OpenError;
        public int Attempts;
        public sealed class Job
        {
            public readonly HashSet<int> Pids = [];
            public ClientJobCpuRate Rate = ClientJobCpuRate.None;
        }
        // A job lives on in the kernel while a process is in it, whoever holds a handle; the fixture keeps every job for the same reason.
        public readonly List<Job> Jobs = [];
        public readonly List<SafeJobObjectHandle> JobHandles = [];
        private readonly Dictionary<SafeJobObjectHandle, Job> _jobs = [];
        private SafeJobObjectHandle JobHandle(Job? job)
        {
            var handle = new SafeJobObjectHandle(job is null ? 0 : (nint)(JobHandles.Count + 5000), ownsHandle: false);
            JobHandles.Add(handle);
            if (job is not null) _jobs.Add(handle, job);
            return handle;
        }
        public bool IsInJob(SafeProcessHandle handle, SafeJobObjectHandle? job, out bool inJob)
        {
            var client = _handles[handle];
            inJob = job is null ? client.ForeignJob || Jobs.Any(candidate => candidate.Pids.Contains(client.Pid))
                : _jobs[job].Pids.Contains(client.Pid);
            return true;
        }
        public SafeJobObjectHandle CreateJob(out int error)
        {
            error = 0;
            var job = new Job();
            Jobs.Add(job);
            return JobHandle(job);
        }
        public bool AssignToJob(SafeProcessHandle handle, SafeJobObjectHandle job, out int error)
        {
            var client = _handles[handle];
            Attempts++;
            // Zenith refuses a client already in another job before this, so the fixture treats it as Windows 7 would.
            if (client.ForeignJob || Jobs.Any(candidate => candidate.Pids.Contains(client.Pid))) { error = 5; return false; }
            error = 0;
            _jobs[job].Pids.Add(client.Pid);
            return true;
        }
        public bool ReadCpuRate(SafeJobObjectHandle job, out ClientJobCpuRate rate)
        {
            rate = _jobs[job].Rate;
            return true;
        }
        public bool SetCpuRate(SafeJobObjectHandle job, ClientJobCpuRate rate, out int error)
        {
            Attempts++;
            error = DenySet || DenyCap ? 5 : 0;
            if (error != 0) return false;
            var target = _jobs[job];
            target.Rate = rate;
            foreach (int pid in target.Pids) OtherSets.Add((pid, $"cap {rate.ControlFlags:x}/{rate.CpuRate}"));
            return true;
        }
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
        public IClientCpuCapLedger Ledger { get; }
        public WindowsClientProcessPriorityControl Control { get; }
        public Fixture(params int[] pids) : this(new InMemoryClientCpuCapLedger(), pids) { }
        public Fixture(IClientCpuCapLedger ledger, params int[] pids)
        {
            Ledger = ledger;
            foreach (int pid in pids) Native.Clients.Add(pid, new(pid));
            Control = new(new NativeClientProcessPriorityAdapter(Native, ledger));
        }
        public Task<ClientProcessPriorityBatchResult> Change(ClientProcessPriorityAction action, params int[] pids)
            => Control.ChangeAsync(pids.Select(Session).ToArray(), action, default);
        public async ValueTask DisposeAsync()
        {
            Native.BeforeRead = null;
            Native.BeforeSet = null;
            Native.AfterSet = null;
            Native.OpenError = 0;
            Native.DenySet = Native.FailRead = Native.DenyThrottling = Native.DenyCap = false;
            foreach (var (pid, state) in Native.LastSet)
                if (Native.Clients.TryGetValue(pid, out var client) && client.Start == Session(pid).ProcessStartUtcTicks)
                    client.Apply(state);
            await Control.DisposeAsync();
        }
    }
}
