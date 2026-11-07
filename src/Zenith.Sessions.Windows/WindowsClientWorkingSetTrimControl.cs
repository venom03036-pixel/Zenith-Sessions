using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// One actual worker and no queued requests. No native operation runs on the caller's thread. Each
/// client's before reading, trim and after reading share one short-lived exact handle, so a recycled
/// PID can never receive another instance's trim or reading. Trims are spaced one sample tick apart.
/// </summary>
public sealed class WindowsClientWorkingSetTrimControl : IClientWorkingSetTrimControl
{
    private const string ClientImage = "RobloxPlayerBeta.exe";
    private readonly object _gate = new();
    private readonly IClientTrimNativeApi _native;
    private readonly string _image;
    private readonly TimeSpan _spacing, _afterDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Task<ClientWorkingSetTrimBatchResult>? _active;
    private CancellationTokenSource? _stop;
    private Task? _dispose;
    private long _generation;
    private bool _closed;

    public WindowsClientWorkingSetTrimControl() : this(new NativeClientWorkingSetTrimApi(), ClientImage,
        ClientWorkingSetTrimPolicy.TrimSpacing, ClientWorkingSetTrimPolicy.AfterReadingDelay) { }
    internal WindowsClientWorkingSetTrimControl(IClientTrimNativeApi native, string clientImage, TimeSpan spacing,
        TimeSpan afterDelay, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientImage);
        if (spacing < TimeSpan.Zero || afterDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(spacing));
        (_image, _spacing, _afterDelay) = (clientImage, spacing, afterDelay);
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    public void CancelOperations()
    {
        Interlocked.Increment(ref _generation);
        // The run awaits with ForceYielding, so cancelling under the gate never runs its native work here.
        lock (_gate) _stop?.Cancel();
    }

    public Task<ClientWorkingSetTrimBatchResult> TrimAsync(IReadOnlyList<SessionObservation> sessions, CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        ArgumentNullException.ThrowIfNull(sessions);
        int count = sessions.Count;
        if (count is < 0 or > ClientWorkingSetTrimPolicy.MaximumProcesses)
            throw new ArgumentException("Select at most 128 client processes in one trim.", nameof(sessions));
        var pinned = new SessionObservation[count];
        for (int index = 0; index < count; index++)
            pinned[index] = sessions[index] ?? throw new ArgumentException("Each selected client must be present.", nameof(sessions));
        lock (_gate)
        {
            if (_closed) return Task.FromResult(Refused(pinned, ClientWorkingSetTrimStatus.Closed));
            if (_active is { IsCompleted: false }) return Task.FromResult(Refused(pinned, ClientWorkingSetTrimStatus.Busy));
            bool stopped = ct.IsCancellationRequested || generation != Interlocked.Read(ref _generation);
            if (count == 0) return Task.FromResult(new ClientWorkingSetTrimBatchResult([], stopped));
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (stopped) stop.Cancel();
            _stop = stop;
            return _active = Task.Run(() => RunAsync(pinned, stop), CancellationToken.None);
        }
    }

    private async Task<ClientWorkingSetTrimBatchResult> RunAsync(SessionObservation[] pinned, CancellationTokenSource stop)
    {
        var token = stop.Token;
        var results = new ClientWorkingSetTrimResult[pinned.Length];
        var accepted = new List<(int Index, SafeProcessHandle Process, long TrimmedAt)>(pinned.Length);
        var duplicatePids = pinned.GroupBy(session => session.ProcessId).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        try
        {
            string? user;
            try { user = _native.CurrentUserSid; }
            catch (Exception) { user = null; }
            long? lastTrim = null;
            for (int index = 0; index < pinned.Length; index++)
            {
                var session = pinned[index];
                if (session.ProcessId <= 0 || session.ProcessStartUtcTicks <= 0 || session.Problem is not null
                    || duplicatePids.Contains(session.ProcessId))
                {
                    results[index] = new(session, ClientWorkingSetTrimStatus.InvalidClient);
                    continue;
                }
                if (lastTrim is { } previous) await WaitUntilAsync(previous, _spacing, token);
                if (token.IsCancellationRequested) { results[index] = new(session, ClientWorkingSetTrimStatus.Cancelled); continue; }
                if (string.IsNullOrEmpty(user)) { results[index] = new(session, ClientWorkingSetTrimStatus.AccessDenied); continue; }
                results[index] = TrimOne(session, user, token, out var process, out bool attempted);
                if (attempted) lastTrim = Stopwatch.GetTimestamp();
                if (process is not null) accepted.Add((index, process, lastTrim!.Value));
            }
            foreach (var (index, process, trimmedAt) in accepted)
            {
                await WaitUntilAsync(trimmedAt, _afterDelay, token);
                // A stopped run takes no after reading: an early one would understate what Windows put back.
                if (!token.IsCancellationRequested && Validate(process, pinned[index], user!, out var after) is null)
                    results[index] = results[index] with { After = after };
                process.Dispose();
            }
            return new(results, token.IsCancellationRequested);
        }
        finally
        {
            foreach (var (_, process, _) in accepted) process.Dispose();
            lock (_gate) if (ReferenceEquals(_stop, stop)) _stop = null;
            stop.Dispose();
        }
    }

    private ClientWorkingSetTrimResult TrimOne(SessionObservation session, string user, CancellationToken token,
        out SafeProcessHandle? retained, out bool attempted)
    {
        retained = null; attempted = false;
        SafeProcessHandle process;
        int error;
        try { process = _native.Open(session.ProcessId, out error); }
        catch (Exception) { return new(session, ClientWorkingSetTrimStatus.NotConfirmed); }
        try
        {
            // ERROR_INVALID_PARAMETER: no process object exists for this positive PID.
            if (process.IsInvalid) return new(session, error == 87 ? ClientWorkingSetTrimStatus.InvalidClient : Failure(error));
            if (Validate(process, session, user, out var before) is { } refused) return new(session, refused);
            if (token.IsCancellationRequested) return new(session, ClientWorkingSetTrimStatus.Cancelled, before);
            attempted = true;
            bool trimmed;
            try { trimmed = _native.Trim(process, out error); }
            catch (Exception) { return new(session, ClientWorkingSetTrimStatus.NotConfirmed, before); }
            if (!trimmed) return new(session, Failure(error), before);
            retained = process;
            return new(session, ClientWorkingSetTrimStatus.Trimmed, before);
        }
        finally { if (retained is null) process.Dispose(); }
    }

    private ClientWorkingSetTrimStatus? Validate(SafeProcessHandle process, SessionObservation expected, string user,
        out ClientMemoryReading? memory)
    {
        memory = null;
        ClientTrimNativeSnapshot? current;
        try { if (!_native.Read(process, out current) || current is null) return ClientWorkingSetTrimStatus.NotConfirmed; }
        catch (Exception) { return ClientWorkingSetTrimStatus.NotConfirmed; }
        if (!current.Running || current.ProcessId != expected.ProcessId || current.StartUtcTicks != expected.ProcessStartUtcTicks)
            return ClientWorkingSetTrimStatus.InvalidClient;
        if (!string.Equals(current.UserSid, user, StringComparison.Ordinal)) return ClientWorkingSetTrimStatus.AccessDenied;
        if (!string.Equals(Path.GetFileName(current.ImagePath), _image, StringComparison.OrdinalIgnoreCase))
            return ClientWorkingSetTrimStatus.InvalidClient;
        if (current.Memory is null) return ClientWorkingSetTrimStatus.NotConfirmed;
        memory = current.Memory;
        return null;
    }

    private async Task WaitUntilAsync(long since, TimeSpan interval, CancellationToken token)
    {
        var remaining = interval - Stopwatch.GetElapsedTime(since);
        if (remaining <= TimeSpan.Zero || token.IsCancellationRequested) return;
        // ForceYielding keeps a caller's Cancel from running the next native step on the caller's thread.
        await _delay(remaining, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
    }

    private static ClientWorkingSetTrimStatus Failure(int error) => error == 5
        ? ClientWorkingSetTrimStatus.AccessDenied : ClientWorkingSetTrimStatus.NotConfirmed;
    private static ClientWorkingSetTrimBatchResult Refused(SessionObservation[] pinned, ClientWorkingSetTrimStatus status)
        => new(pinned.Select(session => new ClientWorkingSetTrimResult(session, status)).ToArray(), false, status);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is null)
            {
                _closed = true;
                CancelOperations();
                _dispose = _active ?? Task.CompletedTask;
            }
            return new(_dispose);
        }
    }
}
