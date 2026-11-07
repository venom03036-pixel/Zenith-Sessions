namespace Zenith.Sessions.Core;

/// <summary>Stable observed process-instance/window identity, independent of changing resource samples.</summary>
public sealed record ClientPreviewTarget(SessionProcessKey Process, nint Window);

public static class ClientPreviewLimits
{
    public const int MaximumTargets = 4, MaximumWidth = 320, MaximumHeight = 180;
    public const int MaximumStride = MaximumWidth * 4, MaximumFrameBytes = MaximumStride * MaximumHeight;
    public const int MaximumOverviewTargets = 128;
    public const int MaximumOverviewPixelBytes = MaximumOverviewTargets * MaximumFrameBytes;
    public const int MaximumSourceDimension = 8192, MaximumSourcePixels = 8_294_400;
    public static readonly TimeSpan MinimumBatchInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaximumOverviewFrameAge = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Owned, immutable, unpooled BGRA32 pixels. Width/height preserve the source aspect ratio within
/// integer-pixel rounding, never upscale, and are at most 320x180. Stride is exactly Width*4.
/// No native surface, graphics handle or disposable frame escapes the provider. A provider must
/// not mutate/reuse Pixels after returning; the coordinator additionally detaches accepted bytes.
/// CapturedUtc dates the acquired surface, never a later repeat of the same surface timestamp.
/// SurfaceTimestampTicks is the monotonic compositor timestamp in 100-nanosecond TimeSpan ticks;
/// measured refresh intervals use that clock rather than differences in wall-clock CapturedUtc.
/// </summary>
public sealed record ClientPreviewFrame(ClientPreviewTarget Target, long Generation, int Width, int Height,
    int Stride, ReadOnlyMemory<byte> Pixels, DateTimeOffset CapturedUtc, int SourceWidth, int SourceHeight,
    long SurfaceTimestampTicks);

public enum ClientPreviewStatus { Ready, Unsupported, InvalidClient, Minimized, Hidden, Deferred, NoFrame, Cancelled, Failed, Stale }
public enum ClientPreviewMode { Selected, Overview }

/// <summary>Provider explanations must be fixed text; the coordinator replaces them with its own status text.</summary>
public sealed record ClientPreviewResult(ClientPreviewTarget Target, ClientPreviewStatus Status,
    ClientPreviewFrame? Frame, string Explanation);

/// <summary>
/// The coordinator owns the injected provider and serializes CaptureAsync/ReleaseAsync/DisposeAsync.
/// ReleaseAsync must finish actual capture cleanup before completing, including after cancellation.
/// Frames are ephemeral observations, never account/authorization/game/arrival evidence or persisted data.
/// </summary>
public interface IClientPreviewProvider : IAsyncDisposable
{
    Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets,
        long generation, CancellationToken ct);
    Task ReleaseAsync(CancellationToken ct);
}

/// <summary>
/// Optional progress reporting on the same owned provider. Results are exactly the ordered attempted
/// prefix of the request, with Count equal to AttemptedCount. A time/budget break omits unvisited
/// targets; an individually unavailable target is an attempted result. No native work is abandoned.
/// </summary>
public interface IClientPreviewBatchProvider : IClientPreviewProvider
{
    Task<ClientPreviewBatchResult> CaptureBatchAsync(IReadOnlyList<ClientPreviewTarget> targets,
        long generation, CancellationToken ct);
}

public sealed record ClientPreviewBatchResult(int AttemptedCount, IReadOnlyList<ClientPreviewResult> Results);

/// <summary>
/// One immutable latest publication. Consumers should release older snapshots rather than retain a history.
/// Stopping means publication was cleared but actual provider cleanup has not completed successfully.
/// Overview may retain an accepted frame's original generation/time across exact-identity roster updates;
/// the snapshot generation identifies the current publication context, not a new capture of those pixels.
/// </summary>
public sealed record ClientPreviewSnapshot(long Generation, bool Running, IReadOnlyList<ClientPreviewResult> Results,
    string Explanation, long Revision = 0, bool Stopping = false, ClientPreviewMode Mode = ClientPreviewMode.Selected);

public sealed class ClientPreviewException : Exception
{
    public ClientPreviewException() : base("Client preview resources could not be released.") { }
}

public static class ClientPreviewMessages
{
    public static string For(ClientPreviewStatus status) => status switch
    {
        ClientPreviewStatus.Ready => "Optional client preview. This does not verify an account or game session.",
        ClientPreviewStatus.Unsupported => "Client preview capture is unavailable on this system.",
        ClientPreviewStatus.InvalidClient => "The selected process or window is no longer a valid client.",
        ClientPreviewStatus.Minimized => "This client is minimized. No current preview is available.",
        ClientPreviewStatus.Hidden => "This client is hidden. No current preview is available.",
        ClientPreviewStatus.Deferred => "Client preview capture is deferred.",
        ClientPreviewStatus.NoFrame => "No fresh client preview is available.",
        ClientPreviewStatus.Cancelled => "Client preview capture was cancelled.",
        ClientPreviewStatus.Stale => "Last-known client preview. It is stale and does not verify an account or game session.",
        _ => "Client preview capture could not finish."
    };
}
