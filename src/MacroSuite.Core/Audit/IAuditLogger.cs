namespace MacroSuite.Core.Audit;

/// <summary>
/// Structured, machine-readable audit entry tracking an AI or automated execution event.
/// STRICT RULE 10: Never contains API keys, credentials, or sensitive secrets.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public Guid? RunId { get; init; }
    public Guid? MacroId { get; init; }
    public Guid? StepId { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string? ProposedAction { get; init; }
    public string? ValidationResult { get; init; }
    public string? ApprovalResult { get; init; }
    public string? ExecutedAction { get; init; }
    public string? ScreenshotPath { get; init; }
    public string? Error { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCostUsd { get; init; }
}

/// <summary>
/// Asynchronous, non-blocking audit logging pipeline (Rule 18).
/// Enqueueing never blocks macro execution or input simulation.
/// </summary>
public enum AuditLoggerHealth
{
    Running = 0,
    Degraded = 1,
    Faulted = 2,
    Stopping = 3,
    Stopped = 4
}

public interface IAuditLogger : IAsyncDisposable, IDisposable
{
    /// <summary>Durable-write health. Autonomous AI input must fail closed when this is Faulted.</summary>
    AuditLoggerHealth Health { get; }

    /// <summary>Enqueues an audit event without blocking.</summary>
    void Enqueue(AuditEntry entry);

    /// <summary>
    /// Flushes pending queued audit entries. Fails if any requested event was dropped or not written.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);
}
