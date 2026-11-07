using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

/// <summary>
/// Frozen provisional forecast in bytes for this one request, never measured consumption,
/// resource admission, authorization, provider capacity or an account/arrival assertion.
/// </summary>
public sealed record LaunchResourceBudget(ulong PhysicalBytes, ulong CommitBytes)
{
    public const ulong MaximumBytes = 1024UL * CapacityPolicy.GiB;
    [JsonIgnore] public bool IsValid => PhysicalBytes is > 0 and <= MaximumBytes && CommitBytes is > 0 and <= MaximumBytes;
    public LaunchResourceBudget Validated()
    {
        if (!IsValid) throw new ArgumentException("A launch forecast must contain positive physical and commit bytes, each no greater than 1,024 GiB.");
        return this;
    }
    public static LaunchResourceBudget FromSettings(CapacitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validated();
        return new LaunchResourceBudget((ulong)Math.Ceiling(settings.NewClientPhysicalGiB * CapacityPolicy.GiB),
            (ulong)Math.Ceiling(settings.NewClientCommitGiB * CapacityPolicy.GiB)).Validated();
    }
}
