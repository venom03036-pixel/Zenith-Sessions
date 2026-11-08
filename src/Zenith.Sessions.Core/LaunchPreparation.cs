using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

/// <summary>One queue-created preparation owner. No credentials or capacity reservation.</summary>
public sealed class LaunchPreparationContext
{
    public LaunchIntent OriginalIntent { get; }
    public bool RequiresVerifiedExecutable { get; }
    internal LaunchPreparationContext(LaunchIntent intent, bool requiresVerifiedExecutable = false)
    { OriginalIntent = intent; RequiresVerifiedExecutable = requiresVerifiedExecutable; }
    public override string ToString() => "LaunchPreparationContext { Queue-owned preparation, no dispatch authority }";
}

/// <summary>Optional extension; preparation must never start a native process or send input.</summary>
public interface IPreparingLaunchProvider : ILaunchProvider
{
    Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct);
}

public interface IPreparedAccountLaunch : IAsyncDisposable
{
    LaunchPreparationContext Context { get; }
    LaunchIntent DispatchIntent { get; }
    bool IsExpired { get; }
    // Exactly one queue dispatch. Validation/current/admission still run at the actual native
    // boundary. The handle remains owned until the returned task and cleanup really finish.
    Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct);
}

public enum LaunchPreparationFailure { None, Rejected, Expired, CleanupFailed }
public sealed record LaunchPreparationResult(LaunchPreparationFailure Failure,
    [property: JsonIgnore] IPreparedAccountLaunch? Prepared = null)
{
    public override string ToString() => $"LaunchPreparationResult {{ Failure = {Failure}, Handle = [redacted] }}";
}

/// <summary>Current queue-owned emission fence, not account/arrival proof or persisted data.</summary>
public sealed class LaunchDispatchBoundary : IDisposable
{
    private readonly Func<LaunchIntent, CancellationToken, Task<bool>> _validate;
    private readonly Func<bool> _current;
    private readonly Func<LaunchAdmission> _admission;
    private int _closed;
    public LaunchIntent Intent { get; }
    internal LaunchDispatchBoundary(LaunchIntent intent, Func<LaunchIntent, CancellationToken, Task<bool>> validate,
        Func<bool> current, Func<LaunchAdmission> admission)
    { Intent = intent; _validate = validate; _current = current; _admission = admission; }

    public bool IsCurrent
    {
        get
        {
            if (Volatile.Read(ref _closed) != 0) return false;
            try { return _current() && Volatile.Read(ref _closed) == 0; }
            catch (Exception) { return false; }
        }
    }
    public async Task<bool> ValidateAsync(LaunchIntent intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (intent != Intent || !IsCurrent) return false;
        bool valid = await _validate(intent, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); return valid && IsCurrent;
    }
    public LaunchAdmission ReadAdmission()
    {
        if (!IsCurrent) return new(false, "This dispatch no longer owns a current launch reservation.");
        try
        {
            var result = _admission();
            return IsCurrent ? result : new(false, "This dispatch was stopped during its resource check.");
        }
        catch (Exception) { return new(false, "Fresh launch admission could not be confirmed."); }
    }
    public void Dispose() => Interlocked.Exchange(ref _closed, 1);
    public override string ToString() => "LaunchDispatchBoundary { Queue-owned emission fence }";
}
