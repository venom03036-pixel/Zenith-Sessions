using System.IO;
using System.Text.Json;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;
using Zenith.MacroHost.Execution;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Synthetic authenticated-document sources and in-memory input only; no file, pipe or native operations.
public sealed class SavedMacroPlanPreparerTests
{
    private const string PrivatePath = @"C:\synthetic-private-library\private-document.zmacro";
    private const string PrivateName = "synthetic-private-macro-name";
    private static readonly string Digest = new('A', 64);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static SavedMacroReference Reference() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static MacroModel Model(Guid id, params MacroStep[] steps) => new()
    {
        Id = id, Name = PrivateName, Description = PrivatePath,
        Steps = steps.Length == 0 ? [new KeyPressStep { KeyCode = 65, DelayAfterMs = 0 }] : steps,
        Settings = new() { MaxRuntime = TimeSpan.FromSeconds(10) }
    };
    private static SavedMacroDocument Document(MacroModel model) => new(model, new(model.Id, Digest, 128));
    private static SavedMacroDocumentPin Pin(SavedMacroReference reference, MacroModel? model = null, Func<bool>? guard = null)
        => SavedMacroDocumentPin.Create(reference, Document(model ?? Model(reference.MacroId)), guard ?? (() => true));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SavedMacroDocumentReadResult Ready(SavedMacroDocumentPin pin) => new(SavedMacroDocumentReadStatus.Ready, pin);

    [Fact]
    public async Task ExactReadyReadProducesOnlyAPinnedPlanAndBorrowsItsSource()
    {
        var reference = Reference();
        var source = new Source((_, _) => Task.FromResult(Ready(Pin(reference))));
        await using (var preparer = new SavedMacroPlanPreparer(source))
        {
            var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
            Assert.Equal(SavedMacroPreparationStatus.Ready, result.Status);
            var prepared = Assert.IsType<PreparedSavedMacroPlan>(result.Prepared);
            Assert.Equal(reference, prepared.Reference);
            Assert.Equal(reference.MacroId, prepared.Plan.MacroId);
            Assert.Equal(TargetedMacroScope.OwnedWindowDiagnostic, prepared.Plan.Scope);
            Assert.Equal(1, prepared.Plan.StepCount);
            Assert.Null(result.DocumentProblem);
            Assert.Equal(1, source.Calls);
        }
        Assert.Equal(0, source.DisposeCalls);
    }

    [Theory]
    [InlineData(SavedMacroDocumentReadStatus.Starting, SavedMacroPreparationStatus.Starting)]
    [InlineData(SavedMacroDocumentReadStatus.Busy, SavedMacroPreparationStatus.Busy)]
    [InlineData(SavedMacroDocumentReadStatus.NotFound, SavedMacroPreparationStatus.NotFound)]
    [InlineData(SavedMacroDocumentReadStatus.Changed, SavedMacroPreparationStatus.Changed)]
    [InlineData(SavedMacroDocumentReadStatus.Unavailable, SavedMacroPreparationStatus.Unavailable)]
    [InlineData(SavedMacroDocumentReadStatus.Cancelled, SavedMacroPreparationStatus.Cancelled)]
    [InlineData(SavedMacroDocumentReadStatus.Closed, SavedMacroPreparationStatus.Closed)]
    public async Task NonreadyReadPreservesItsTypedOutcomeWithoutAPlan(SavedMacroDocumentReadStatus read, SavedMacroPreparationStatus expected)
    {
        var source = new Source((_, _) => Task.FromResult(new SavedMacroDocumentReadResult(read)));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(Reference(), TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Prepared);
        Assert.Null(result.DocumentProblem);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IncompleteReferenceIsRefusedBeforeSourceWork(int missing)
    {
        var reference = Reference();
        reference = missing switch
        {
            0 => reference with { HostSessionId = Guid.Empty },
            1 => reference with { MacroId = Guid.Empty },
            _ => reference with { SavedRevision = Guid.Empty }
        };
        var source = new Source((_, _) => throw new InvalidOperationException("Must not read."));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(SavedMacroPreparationStatus.InvalidReference, result.Status);
        Assert.Null(result.Prepared);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task UnsupportedScopeIsRefusedBeforeSourceWork(int scope)
    {
        var source = new Source((_, _) => throw new InvalidOperationException("Must not read."));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(Reference(), (TargetedMacroScope)scope);
        Assert.Equal(SavedMacroPreparationStatus.UnsupportedDocument, result.Status);
        Assert.Equal(TargetedMacroPlanProblem.InvalidDocument, result.DocumentProblem);
        Assert.Null(result.Prepared);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task PrecancelledRequestDoesNotReadTheSource()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var source = new Source((_, _) => throw new InvalidOperationException("Must not read."));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(Reference(), TargetedMacroScope.OwnedWindowDiagnostic, stop.Token);
        Assert.Equal(SavedMacroPreparationStatus.Cancelled, result.Status);
        Assert.Null(result.Prepared);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("revision")]
    public async Task ForeignPinCannotSubstituteForTheRequestedReference(string changed)
    {
        var reference = Reference();
        var foreign = changed == "host" ? reference with { HostSessionId = Guid.NewGuid() }
            : reference with { SavedRevision = Guid.NewGuid() };
        int guards = 0;
        var source = new Source((_, _) => Task.FromResult(Ready(Pin(foreign, guard: () => { guards++; return true; }))));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(SavedMacroPreparationStatus.Changed, result.Status);
        Assert.Null(result.Prepared);
        Assert.Equal(0, guards);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("model")]
    [InlineData("receipt-id")]
    [InlineData("no-receipt")]
    [InlineData("no-model")]
    [InlineData("digest")]
    [InlineData("length")]
    public void PinRequiresAnExactValidAuthenticatedModelAndReceipt(string invalid)
    {
        var reference = Reference();
        var model = Model(invalid == "model" ? Guid.NewGuid() : reference.MacroId);
        SavedMacroDocumentReceipt? receipt = new(invalid == "receipt-id" ? Guid.NewGuid() : reference.MacroId, Digest, 128);
        if (invalid == "reference") reference = reference with { SavedRevision = Guid.Empty };
        if (invalid == "no-receipt") receipt = null;
        if (invalid == "digest") receipt = receipt! with { CiphertextSha256 = "invalid" };
        if (invalid == "length") receipt = receipt! with { CiphertextLength = 0 };
        var document = new SavedMacroDocument(invalid == "no-model" ? null! : model, receipt);
        var failure = Assert.Throws<ArgumentException>(() => SavedMacroDocumentPin.Create(reference, document, () => true));
        AssertPrivateTextAbsent(failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrThrowingOwnerGuardPreventsCompilation(bool throws)
    {
        var reference = Reference();
        // This document would be unsupported if compilation were reached.
        var invalid = Model(reference.MacroId, new ClickStep { IsClientRelative = false });
        var source = new Source((_, _) => Task.FromResult(Ready(Pin(reference, invalid,
            () => throws ? throw new IOException(PrivatePath + Digest) : false))));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(SavedMacroPreparationStatus.Changed, result.Status);
        Assert.Null(result.Prepared);
        Assert.Null(result.DocumentProblem);
        AssertPrivateTextAbsent(result.ToString());
    }

    [Fact]
    public async Task OwnerChangingAfterCompilationCannotPublishTheFrozenPlan()
    {
        var reference = Reference();
        int guards = 0;
        var source = new Source((_, _) => Task.FromResult(Ready(Pin(reference, guard: () => ++guards == 1))));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(2, guards);
        Assert.Equal(SavedMacroPreparationStatus.Changed, result.Status);
        Assert.Null(result.Prepared);
    }

    [Theory]
    [InlineData("target", TargetedMacroPlanProblem.UnsupportedTarget)]
    [InlineData("screen", TargetedMacroPlanProblem.UnsupportedInput)]
    [InlineData("indefinite", TargetedMacroPlanProblem.ResourceLimit)]
    public async Task WholeDocumentRefusalRemainsTypedAndCannotReturnAPartialPlan(string invalid, TargetedMacroPlanProblem problem)
    {
        var reference = Reference();
        var model = invalid == "screen" ? Model(reference.MacroId, new ClickStep()) : Model(reference.MacroId);
        if (invalid == "target") model.Settings.Target = new() { Mode = MacroTargetMode.Screen };
        if (invalid == "indefinite") model.Settings.RepeatUntilStopped = true;
        var source = new Source((_, _) => Task.FromResult(Ready(Pin(reference, model))));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(SavedMacroPreparationStatus.UnsupportedDocument, result.Status);
        Assert.Equal(problem, result.DocumentProblem);
        Assert.Null(result.Prepared);
        AssertPrivateTextAbsent(JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOrThrowingSourceProducesAFixedPrivateFailure(bool throws)
    {
        var source = new Source((_, _) => throws ? throw new IOException(PrivateName + PrivatePath + Digest)
            : Task.FromResult<SavedMacroDocumentReadResult>(null!));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(Reference(), TargetedMacroScope.OwnedWindowDiagnostic);
        Assert.Equal(SavedMacroPreparationStatus.Unavailable, result.Status);
        Assert.Null(result.Prepared);
        AssertPrivateTextAbsent(JsonSerializer.Serialize(result));
        AssertPrivateTextAbsent(result.ToString());
    }

    [Fact]
    public async Task PreparedPlanIgnoresLaterSourceMutationAndExposesNoBodyPathOrDigest()
    {
        var reference = Reference();
        var key = new KeyPressStep { KeyCode = 65, DelayAfterMs = 0 };
        MacroStep[] steps = [key];
        var model = Model(reference.MacroId, steps);
        var pin = Pin(reference, model);
        var source = new Source((_, _) => Task.FromResult(Ready(pin)));
        await using var preparer = new SavedMacroPlanPreparer(source);
        var result = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        var prepared = Assert.IsType<PreparedSavedMacroPlan>(result.Prepared);
        steps[0] = new KeyPressStep { KeyCode = 67 };
        model.Settings.RepeatCount = 1000;
        model.Settings.RepeatUntilStopped = true;
        model.Settings.Target = new() { Mode = MacroTargetMode.Screen };

        var target = new TargetedInputTarget(Guid.NewGuid(), 1, new DateTime(2026, 1, 1).Ticks, (nint)123, new(0, 0, 100, 100), 1);
        var transport = new Transport(target);
        await using var input = new TargetedInputCoordinator(transport);
        await using var runner = new TargetedMacroRunner(input, new([65], TargetedMacroSignals.None), (_, _) => true);
        var started = runner.TryStart(prepared.Plan, target);
        Assert.Equal(TargetedMacroStartStatus.Started, started.Status);
        var run = Assert.IsType<TargetedMacroRun>(started.Run);
        var completed = await run.Completion.WaitAsync(Deadline);
        Assert.Equal(TargetedMacroResultStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Snapshot.CompletedSteps);
        Assert.Equal(new[] { TargetedInputKind.KeyDown, TargetedInputKind.KeyUp }, transport.Commands.Select(item => item.Signal.Kind));
        Assert.All(transport.Commands, item => Assert.Equal(65, item.Signal.Key));
        Assert.False(completed.Snapshot.AccountVerified);
        Assert.False(completed.Snapshot.RobloxQualified);
        AssertPrivateTextAbsent(JsonSerializer.Serialize(result));
        AssertPrivateTextAbsent(JsonSerializer.Serialize(pin));
        AssertPrivateTextAbsent(prepared.ToString());
        using var pinJson = JsonDocument.Parse(JsonSerializer.Serialize(pin));
        Assert.Equal(new[] { "Reference" }, pinJson.RootElement.EnumerateObject().Select(item => item.Name));
    }

    [Fact]
    public async Task SourceAndOwnerGuardsRunOffTheCallingThreadAndSynchronizationContext()
    {
        var reference = Reference();
        int sourceThread = 0, guardThread = 0;
        SynchronizationContext? sourceContext = null, guardContext = null;
        var source = new Source((_, _) =>
        {
            sourceThread = Environment.CurrentManagedThreadId;
            sourceContext = SynchronizationContext.Current;
            return Task.FromResult(Ready(Pin(reference, guard: () =>
            {
                guardThread = Environment.CurrentManagedThreadId;
                guardContext = SynchronizationContext.Current;
                return true;
            })));
        });
        await using var preparer = new SavedMacroPlanPreparer(source);
        var finished = new TaskCompletionSource<(int Caller, SavedMacroPreparationResult Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                int caller = Environment.CurrentManagedThreadId;
                var result = preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic).WaitAsync(Deadline).GetAwaiter().GetResult();
                finished.TrySetResult((caller, result));
            }
            catch (Exception error) { finished.TrySetException(error); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            var observed = await finished.Task.WaitAsync(Deadline);
            Assert.Equal(SavedMacroPreparationStatus.Ready, observed.Result.Status);
            Assert.NotEqual(observed.Caller, sourceThread);
            Assert.NotEqual(observed.Caller, guardThread);
            Assert.Null(sourceContext);
            Assert.Null(guardContext);
        }
        finally { Assert.True(thread.Join(Deadline), "The owned calling thread must terminate."); }
    }

    [Fact]
    public async Task CancelledUncooperativeReadRetainsTheOnlySlotUntilItsActualCompletion()
    {
        var reference = Reference();
        var entered = Gate();
        var release = Gate();
        using var stop = new CancellationTokenSource();
        var source = new Source(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false); // Deliberately ignores cancellation.
            return Ready(Pin(reference));
        });
        await using var preparer = new SavedMacroPlanPreparer(source);
        var pending = preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic, stop.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            stop.Cancel();
            Assert.True(source.LastToken.IsCancellationRequested);
            Assert.False(pending.IsCompleted);
            var blocked = await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
            Assert.Equal(SavedMacroPreparationStatus.Busy, blocked.Status);
            Assert.Equal(1, source.Calls);
        }
        finally { release.TrySetResult(); await pending.WaitAsync(Deadline); }
        Assert.Equal(SavedMacroPreparationStatus.Cancelled, (await pending).Status);
        Assert.Equal(SavedMacroPreparationStatus.Ready, (await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic)).Status);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task DisposeClosesAdmissionCancelsAndJoinsTheUncooperativeReadWithoutDisposingItsBorrowedSource()
    {
        var reference = Reference();
        var entered = Gate();
        var cancelled = Gate();
        var release = Gate();
        var source = new Source(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return Ready(Pin(reference));
        });
        var preparer = new SavedMacroPlanPreparer(source);
        var pending = preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic);
        Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            disposing = preparer.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(Deadline);
            Assert.False(pending.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.Same(disposing, preparer.DisposeAsync().AsTask());
            Assert.Equal(SavedMacroPreparationStatus.Closed, (await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic)).Status);
            Assert.Equal(1, source.Calls);
            Assert.Equal(0, source.DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(Deadline);
            await (disposing ?? preparer.DisposeAsync().AsTask()).WaitAsync(Deadline);
        }
        Assert.Equal(SavedMacroPreparationStatus.Cancelled, (await pending).Status);
        Assert.Equal(0, source.DisposeCalls);
        Assert.Equal(SavedMacroPreparationStatus.Closed, (await preparer.PrepareAsync(reference, TargetedMacroScope.OwnedWindowDiagnostic)).Status);
    }

    [Fact]
    public async Task DisposedUnusedPreparerNeverContactsTheSource()
    {
        var source = new Source((_, _) => throw new InvalidOperationException("Must not read."));
        var preparer = new SavedMacroPlanPreparer(source);
        await preparer.DisposeAsync();
        Assert.Equal(SavedMacroPreparationStatus.Closed, (await preparer.PrepareAsync(Reference(), TargetedMacroScope.OwnedWindowDiagnostic)).Status);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, source.DisposeCalls);
    }

    private static void AssertPrivateTextAbsent(string value)
    {
        Assert.DoesNotContain(PrivateName, value, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivatePath, value, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-library", value, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest, value, StringComparison.Ordinal);
    }

    private sealed class Source(Func<SavedMacroReference, CancellationToken, Task<SavedMacroDocumentReadResult>> read)
        : ISavedMacroDocumentSource, IDisposable, IAsyncDisposable
    {
        public int Calls;
        public int DisposeCalls;
        public CancellationToken LastToken;
        public Task<SavedMacroDocumentReadResult> ReadAsync(SavedMacroReference reference, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LastToken = ct;
            return read(reference, ct);
        }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Transport(TargetedInputTarget target) : ITargetedInputTransport
    {
        public List<TargetedInputCommand> Commands { get; } = [];
        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget candidate, bool cleanup, CancellationToken ct)
            => ValueTask.FromResult(candidate == target ? TargetedInputValidation.Current : TargetedInputValidation.Unavailable);
        public ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!admissionOpen()) return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            Commands.Add(command);
            return ValueTask.FromResult(TargetedInputSubmission.Queued);
        }
    }
}
