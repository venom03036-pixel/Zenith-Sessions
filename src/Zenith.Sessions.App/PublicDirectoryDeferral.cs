using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// Tells a Roblox pause from a refusal made on this PC. The public directory sets RetryAfter on every provider pause
/// (a 429, or a pause that is still running). A Deferred without one was turned down locally: both shared request slots
/// busy, a timed-out request, a stale picture descriptor or the same picture already downloading. That is not a
/// cooldown, so a later user action or update may ask again. Nothing is waited for, queued or retried here.
/// </summary>
internal static class PublicDirectoryDeferral
{
    public static bool IsLocal<T>(PublicDirectoryResult<T> result) where T:class
        =>result.State==PublicDirectoryState.Deferred && result.RetryAfter is null;
}
