namespace MacroSuite.Core.Execution;

public enum ExecutionTraceLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public sealed record ExecutionTraceEvent(
    DateTimeOffset Timestamp,
    Guid RunId,
    Guid? ActionId,
    string Category,
    string Message,
    ExecutionTraceLevel Level = ExecutionTraceLevel.Information,
    object? Data = null,
    Exception? Exception = null);

public interface IExecutionTraceSink
{
    bool IsEnabled { get; }
    void Write(ExecutionTraceEvent traceEvent);
}

public sealed class DelegateExecutionTraceSink : IExecutionTraceSink
{
    private readonly Action<ExecutionTraceEvent> _writer;

    public bool IsEnabled { get; }

    public DelegateExecutionTraceSink(Action<ExecutionTraceEvent> writer, bool isEnabled = true)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        IsEnabled = isEnabled;
    }

    public void Write(ExecutionTraceEvent traceEvent) => _writer(traceEvent);
}

public static class MacroVariableNames
{
    public const string LastDetection = "Vision.LastDetection";
    public const string LastDetectedX = "Vision.LastDetectedX";
    public const string LastDetectedY = "Vision.LastDetectedY";
    public const string LastDetectedXAlias = "LastDetectedX";
    public const string LastDetectedYAlias = "LastDetectedY";
    public const string LastDetectedConfidence = "Vision.LastDetectedConfidence";
    public const string LastDetectedPercentage = "Vision.LastDetectedPercentage";
    public const string LastDetectedDistance = "Vision.LastDetectedDistance";
    public const string LastDetectedRegionX = "Vision.LastDetectedRegionX";
    public const string LastDetectedRegionY = "Vision.LastDetectedRegionY";
    public const string LastDetectedRegionWidth = "Vision.LastDetectedRegionWidth";
    public const string LastDetectedRegionHeight = "Vision.LastDetectedRegionHeight";
    public const string LastDetectionX = "LastDetection.X";
    public const string LastDetectionY = "LastDetection.Y";
    public const string LastDetectionConfidence = "LastDetection.Confidence";
    public const string LastDetectionPercentage = "LastDetection.Percentage";
    public const string LastDetectionDistance = "LastDetection.Distance";
    public const string LastDetectionRegionX = "LastDetection.Region.X";
    public const string LastDetectionRegionY = "LastDetection.Region.Y";
    public const string LastDetectionRegionWidth = "LastDetection.Region.Width";
    public const string LastDetectionRegionHeight = "LastDetection.Region.Height";
}
