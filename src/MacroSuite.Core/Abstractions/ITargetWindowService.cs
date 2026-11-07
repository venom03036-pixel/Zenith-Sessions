using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>Transient identity. Native handles and process instances are never serialized into macros.</summary>
public sealed record TargetWindowInfo(IntPtr Handle, int ProcessId, long ProcessInstance,
    string ApplicationId, string Title, Rectangle ClientBounds, long Generation, bool IsOwnWindow = false);

/// <summary>Immutable, cached observation. Reading it performs no native calls and is safe at recording ingress.</summary>
public sealed record TargetWindowSnapshot(TargetWindowInfo? Foreground, TargetWindowInfo? LastExternal,
    long Sequence, long ObservedAtTicks, bool IsReady, string? Problem = null)
{
    public static TargetWindowSnapshot Unavailable { get; } = new(null, null, 0, 0, false);
}

public sealed record ResolvedMacroTarget(MacroTargetRule Rule, TargetWindowInfo? Window);
public readonly record struct TargetValidation(bool IsValid, bool IsForeground, bool IsOwnWindow,
    Rectangle ClientBounds, long Generation, string? Problem = null)
{
    public bool AllowsInput => IsValid && IsForeground && !IsOwnWindow;
}

public interface ITargetWindowService : IDisposable
{
    bool SupportsWindowTargeting { get; }
    TargetWindowSnapshot Snapshot { get; }
    event EventHandler? Changed;
    Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default);
    ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null);
    TargetValidation Inspect(ResolvedMacroTarget target);
    void RegisterOwnWindow(IntPtr window);
    bool TryActivate(ResolvedMacroTarget target);
}
