using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;

namespace Zenith.MacroHost.Execution;

/// <summary>Host-local saved reference. It carries no document, path, credential or input grant.</summary>
public readonly record struct SavedMacroReference(Guid HostSessionId, Guid MacroId, Guid SavedRevision)
{
    public bool IsValid => HostSessionId != Guid.Empty && MacroId != Guid.Empty && SavedRevision != Guid.Empty;
}

public enum SavedMacroDocumentReadStatus { Ready, Starting, Busy, NotFound, Changed, Unavailable, Cancelled, Closed }

/// <summary>
/// One detached authenticated read with a current-owner guard. The model and ciphertext
/// receipt remain private to this assembly; this object must never be sent through IPC.
/// </summary>
public sealed class SavedMacroDocumentPin
{
    private readonly MacroModel _model;
    private readonly Func<bool> _current;
    public SavedMacroReference Reference { get; }

    private SavedMacroDocumentPin(SavedMacroReference reference, MacroModel model, Func<bool> current)
    {
        Reference = reference;
        _model = model;
        _current = current;
    }

    public static SavedMacroDocumentPin Create(SavedMacroReference reference, SavedMacroDocument document, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!reference.IsValid || document.SavedModel is null || document.Receipt is null
            || document.Receipt.Validated().MacroId != reference.MacroId || document.SavedModel.Id != reference.MacroId)
            throw new ArgumentException("An exact authenticated saved document and reference are required.");
        return new(reference, document.SavedModel, stillCurrent);
    }

    public bool IsCurrent() => IsCurrent(out _);

    /// <summary>A throwing owner guard still fails closed; only its error type is handed back for diagnostics.</summary>
    internal bool IsCurrent(out Type? fault)
    {
        fault = null;
        try { return _current(); }
        catch (Exception error)
        {
            fault = error.GetType();
            return false;
        }
    }

    internal TargetedMacroPlan Compile(TargetedMacroScope scope) => TargetedMacroPlan.Compile(_model, scope);
    public override string ToString() => "Host-local authenticated saved-document pin";
}

public sealed class SavedMacroDocumentReadResult
{
    public SavedMacroDocumentReadStatus Status { get; }
    public SavedMacroDocumentPin? Pin { get; }
    public SavedMacroDocumentReadResult(SavedMacroDocumentReadStatus status, SavedMacroDocumentPin? pin = null)
    {
        if (!Enum.IsDefined(status) || (status == SavedMacroDocumentReadStatus.Ready) != (pin is not null))
            throw new ArgumentException("The saved-document read result is inconsistent.");
        Status = status;
        Pin = pin;
    }

    /// <summary>
    /// Type of the unexpected error behind an Unavailable read, for the preparer's fault callback
    /// only. Internal so it is never serialized, shown or sent through IPC.
    /// </summary>
    internal Type? Fault { get; private init; }

    /// <summary>
    /// An Unavailable read caused by an unexpected error. Only the error's type is kept, so a
    /// developer can tell a bug from a damaged library; its message, path and stack are dropped.
    /// </summary>
    public static SavedMacroDocumentReadResult Faulted(Type errorType)
    {
        ArgumentNullException.ThrowIfNull(errorType);
        if (!typeof(Exception).IsAssignableFrom(errorType))
            throw new ArgumentException("An exception type is required.", nameof(errorType));
        return new(SavedMacroDocumentReadStatus.Unavailable) { Fault = errorType };
    }

    public override string ToString() => $"Saved-document read: {Status}";
}

/// <summary>Borrowed host-local source; its document owner controls shutdown and actual file reads.</summary>
public interface ISavedMacroDocumentSource
{
    Task<SavedMacroDocumentReadResult> ReadAsync(SavedMacroReference reference, CancellationToken ct = default);
}

public enum SavedMacroPreparationStatus
{
    Ready, InvalidReference, Starting, Busy, NotFound, Changed, Unavailable, Cancelled, Closed, UnsupportedDocument
}

/// <summary>An immutable compatible plan pinned for one requested operation, not permission to send input.</summary>
public sealed class PreparedSavedMacroPlan
{
    public SavedMacroReference Reference { get; }
    public TargetedMacroPlan Plan { get; }
    internal PreparedSavedMacroPlan(SavedMacroReference reference, TargetedMacroPlan plan)
    { Reference = reference; Plan = plan; }
    public override string ToString() => "Pinned saved-macro plan; input eligibility is separate";
}

public sealed class SavedMacroPreparationResult
{
    public SavedMacroPreparationStatus Status { get; }
    public PreparedSavedMacroPlan? Prepared { get; }
    public TargetedMacroPlanProblem? DocumentProblem { get; }
    public string Explanation => Status switch
    {
        SavedMacroPreparationStatus.Ready => "The exact saved revision is pinned. Target and input eligibility must still be established.",
        SavedMacroPreparationStatus.InvalidReference => "Choose a current host and saved macro revision.",
        SavedMacroPreparationStatus.Starting => "The saved library is still loading.",
        SavedMacroPreparationStatus.Busy => "Saved-document work is already in progress. Retry after it finishes.",
        SavedMacroPreparationStatus.NotFound => "The saved macro is no longer available.",
        SavedMacroPreparationStatus.Changed => "The saved macro or host changed. Refresh and choose its current revision.",
        SavedMacroPreparationStatus.Cancelled => "Saved-macro preparation was cancelled.",
        SavedMacroPreparationStatus.Closed => "The saved-macro connection is closing.",
        SavedMacroPreparationStatus.UnsupportedDocument => "This execution scope does not support the complete saved document.",
        _ => "The saved document could not be authenticated. Its editor draft was not used instead."
    };
    internal SavedMacroPreparationResult(SavedMacroPreparationStatus status, PreparedSavedMacroPlan? prepared = null,
        TargetedMacroPlanProblem? problem = null)
    { Status = status; Prepared = prepared; DocumentProblem = problem; }
    public override string ToString() => Explanation;
}

/// <summary>
/// One bounded preparation owner. Reads/decrypts through the existing store, freezes the
/// real interpreter plan off UI, and rechecks the source generation after compilation.
/// It borrows the source and never performs input, capture or credential operations.
/// </summary>
public sealed class SavedMacroPlanPreparer : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ISavedMacroDocumentSource _source;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<Type> _fault;
    private Task<SavedMacroPreparationResult>? _worker;
    private Task? _dispose;
    private bool _closed;

    /// <summary>
    /// <paramref name="fault"/> receives the type of each unexpected error that a fixed result
    /// hides: a throwing source or owner guard, an error the source caught itself, or a compiler
    /// error turned into InvalidDocument. It is required so that a host wiring this preparer must
    /// pass those types to its fixed-payload diagnostics (as ManagerDiagnostics.Record(kind,
    /// errorType) does) instead of losing them. Only the type is passed, never a message, path or
    /// stack. The callback runs on the preparation worker, must return quickly, and an error it
    /// throws is ignored. No call happens after DisposeAsync completes.
    /// </summary>
    public SavedMacroPlanPreparer(ISavedMacroDocumentSource source, Action<Type> fault)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _fault = fault ?? throw new ArgumentNullException(nameof(fault));
    }

    public Task<SavedMacroPreparationResult> PrepareAsync(SavedMacroReference reference, TargetedMacroScope scope,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_closed) return Task.FromResult(new SavedMacroPreparationResult(SavedMacroPreparationStatus.Closed));
            if (!reference.IsValid) return Task.FromResult(new SavedMacroPreparationResult(SavedMacroPreparationStatus.InvalidReference));
            if (scope != TargetedMacroScope.OwnedWindowDiagnostic)
                return Task.FromResult(new SavedMacroPreparationResult(SavedMacroPreparationStatus.UnsupportedDocument,
                    problem: TargetedMacroPlanProblem.InvalidDocument));
            if (ct.IsCancellationRequested) return Task.FromResult(new SavedMacroPreparationResult(SavedMacroPreparationStatus.Cancelled));
            if (_worker is { IsCompleted: false }) return Task.FromResult(new SavedMacroPreparationResult(SavedMacroPreparationStatus.Busy));
            // Publish the tracked work under the gate. No source callback or synchronous
            // document/plan processing is permitted on the calling dispatcher.
            return _worker = Task.Run(() => PrepareCoreAsync(reference, scope, ct), CancellationToken.None);
        }
    }

    private async Task<SavedMacroPreparationResult> PrepareCoreAsync(SavedMacroReference reference, TargetedMacroScope scope,
        CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var read = await _source.ReadAsync(reference, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (read is null) return new(SavedMacroPreparationStatus.Unavailable);
            if (read.Status != SavedMacroDocumentReadStatus.Ready) return Result(Map(read.Status), read.Fault);
            Type? fault = null;
            if (read.Pin is not { } pin || pin.Reference != reference || !pin.IsCurrent(out fault))
                return Result(SavedMacroPreparationStatus.Changed, fault);
            var plan = pin.Compile(scope);
            linked.Token.ThrowIfCancellationRequested();
            if (plan.MacroId != reference.MacroId || !pin.IsCurrent(out fault)) return Result(SavedMacroPreparationStatus.Changed, fault);
            lock (_sync)
            {
                if (_closed) return new(SavedMacroPreparationStatus.Closed);
                linked.Token.ThrowIfCancellationRequested();
                // Only frozen scalar plan data escapes. Do not retain the pin/model in
                // a callback closure or share mutable step instances between runs.
                return new(SavedMacroPreparationStatus.Ready, new(reference, plan));
            }
        }
        catch (OperationCanceledException) { return new(SavedMacroPreparationStatus.Cancelled); }
        catch (TargetedMacroPlanException error) { return Result(SavedMacroPreparationStatus.UnsupportedDocument, error.Fault, error.Problem); }
        catch (Exception error) { return Result(SavedMacroPreparationStatus.Unavailable, error.GetType()); }
    }

    // The fixed status keeps macro content out of the result; the fault callback gets only the
    // error type so a bug can be told apart from a damaged library.
    private SavedMacroPreparationResult Result(SavedMacroPreparationStatus status, Type? fault,
        TargetedMacroPlanProblem? problem = null)
    {
        if (fault is not null)
        {
            try { _fault(fault); }
            // A failing diagnostic sink has nowhere further to report, and it must never change the fixed outcome.
            catch (Exception) { }
        }
        return new(status, problem: problem);
    }

    private static SavedMacroPreparationStatus Map(SavedMacroDocumentReadStatus status) => status switch
    {
        SavedMacroDocumentReadStatus.Starting => SavedMacroPreparationStatus.Starting,
        SavedMacroDocumentReadStatus.Busy => SavedMacroPreparationStatus.Busy,
        SavedMacroDocumentReadStatus.NotFound => SavedMacroPreparationStatus.NotFound,
        SavedMacroDocumentReadStatus.Changed => SavedMacroPreparationStatus.Changed,
        SavedMacroDocumentReadStatus.Cancelled => SavedMacroPreparationStatus.Cancelled,
        SavedMacroDocumentReadStatus.Closed => SavedMacroPreparationStatus.Closed,
        _ => SavedMacroPreparationStatus.Unavailable
    };

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose is not null) return new(_dispose);
            _closed = true;
            var worker = _worker;
            return new(_dispose = Task.Run(() => DisposeCoreAsync(worker), CancellationToken.None));
        }
    }

    private async Task DisposeCoreAsync(Task<SavedMacroPreparationResult>? worker)
    {
        bool failed = false;
        try { _lifetime.Cancel(); }
        catch (Exception) { failed = true; }
        try { if (worker is not null) await worker.ConfigureAwait(false); }
        catch (Exception) { failed = true; }
        finally { _lifetime.Dispose(); }
        if (failed) throw new InvalidOperationException("Saved-macro preparation cleanup needs attention.");
    }
}
