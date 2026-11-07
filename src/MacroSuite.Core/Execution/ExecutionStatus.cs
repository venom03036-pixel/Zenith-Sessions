using System.Threading.Channels;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Latest async execution/debug snapshot. Written from the engine; consumed by the UI overlay.
/// </summary>
public sealed record ExecutionStatusSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid RunId { get; init; }
    public Guid? ActionId { get; init; }
    public string Phase { get; init; } = string.Empty;
    public string? Message { get; init; }
    public ColorDetectionResult? Detection { get; init; }
    public VisualMatchDiagnostics? Visual { get; init; }
    public int Checks { get; init; }
    public int ConsecutiveTrue { get; init; }
    public int ConsecutiveFalse { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public interface IExecutionStatusSink
{
    bool TryWrite(ExecutionStatusSnapshot snapshot);
}

/// <summary>
/// Bounded, non-blocking status channel. Full channel drops the oldest snapshot (Rule 14).
/// </summary>
public sealed class BoundedExecutionStatusSink : IExecutionStatusSink
{
    public const int DefaultCapacity = 32;

    private readonly Channel<ExecutionStatusSnapshot> _channel;

    public BoundedExecutionStatusSink(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<ExecutionStatusSnapshot>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<ExecutionStatusSnapshot> Reader => _channel.Reader;

    public bool TryWrite(ExecutionStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return _channel.Writer.TryWrite(snapshot);
    }

    public void Complete() => _channel.Writer.TryComplete();
}
