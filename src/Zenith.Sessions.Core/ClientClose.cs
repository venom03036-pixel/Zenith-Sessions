namespace Zenith.Sessions.Core;

/// <summary>
/// How one escalating close request for a client ended. A close asks the client's identity-checked window to close
/// normally (WM_CLOSE), asks again after a short gap so Roblox's own leave prompt is answered, and then waits a bounded
/// time for the exact process instance to exit. It never ends the process itself.
/// </summary>
public enum ClientCloseStatus
{
    /// <summary>The exact process instance exited within the bounded wait.</summary>
    Exited,
    /// <summary>Close was requested, the bounded wait passed and the process is still alive. Force close may now be offered.</summary>
    StillOpen,
    /// <summary>The observed instance had already exited, its PID was reused or its window changed before anything was sent.</summary>
    NotCurrent,
    /// <summary>The process is alive but Windows refused the close request for its window; nothing was sent.</summary>
    NotSent,
    /// <summary>Stopped by Panic, Lock, exit or cancellation. A request already sent cannot be recalled.</summary>
    Cancelled
}

/// <param name="CloseRequests">How many close requests reached the client's window (0, 1 or 2).</param>
public sealed record ClientCloseResult(ClientCloseStatus Status, int CloseRequests, string Explanation)
{
    /// <summary>True only when the client was asked to close and is still alive after the bounded wait. Force close is never offered otherwise.</summary>
    public bool OffersForceClose => Status == ClientCloseStatus.StillOpen && CloseRequests > 0;
}

/// <summary>How one explicitly confirmed force close ended.</summary>
public enum ClientForceCloseStatus
{
    /// <summary>Windows accepted the termination and the process exited.</summary>
    Ended,
    /// <summary>Windows accepted the termination but had not reported the exit within the short confirmation wait.</summary>
    ExitUnconfirmed,
    /// <summary>The exact process instance was already gone (exited or PID reused); nothing was ended.</summary>
    NotCurrent,
    /// <summary>Windows refused the termination (for example another Windows user's client); nothing was ended.</summary>
    Denied,
    /// <summary>Stopped before the termination was sent; nothing was ended.</summary>
    Cancelled
}

/// <param name="EndedUtc">When Windows accepted the termination; null when nothing was ended.</param>
public sealed record ClientForceCloseResult(ClientForceCloseStatus Status, DateTimeOffset? EndedUtc, string Explanation)
{
    public bool Accepted => Status is ClientForceCloseStatus.Ended or ClientForceCloseStatus.ExitUnconfirmed;
}

/// <summary>
/// Closing one observed client. <see cref="CloseAsync"/> is the normal, repeated close request with a bounded wait;
/// <see cref="ForceCloseAsync"/> is TerminateProcess on a handle whose PID and start time were checked against the
/// observation, and only ever runs after the user confirmed it for that named client. Nothing here runs on its own.
/// </summary>
public interface IClientCloseControl
{
    Task<ClientCloseResult> CloseAsync(SessionObservation session, CancellationToken ct);
    Task<ClientForceCloseResult> ForceCloseAsync(SessionObservation session, CancellationToken ct);
    /// <summary>Panic, Lock and exit: every waiting close reports Cancelled; requests already sent cannot be recalled.</summary>
    void CancelCloseOperations();
}
