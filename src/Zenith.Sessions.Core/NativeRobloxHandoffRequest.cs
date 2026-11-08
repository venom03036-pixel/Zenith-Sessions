using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace Zenith.Sessions.Core;

/// <summary>
/// Builds the roblox-player request for the Direct ticket mode (R-api-7): the same fields, order and encoding as the
/// request the official page emits, for the frozen destination only, with a fresh browser tracker (kept for log linking)
/// and a fresh join attempt. The ticket is consumed into the request and zeroed here; the result is a managed string that
/// the one-use candidate owns, and the caller must accept it through <see cref="NativeRobloxHandoff.AcceptBuilt"/> before
/// anything else sees it. No HTTP, browser or dispatch happens here.
/// </summary>
public static class NativeRobloxHandoffRequest
{
    public const string Locale = "en_us";
    private const string Prefix = "roblox-player:1+launchmode:play+gameinfo:", Launcher = "https://www.roblox.com/Game/PlaceLauncher.ashx?request=";

    /// <summary>A fresh positive tracker within the receipt bound. Roblox's own trackers are not reproduced; any value in the bound links a log line to its start.</summary>
    public static long NewBrowserTrackerId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        // The bound is 2^53 - 1, so masking to it keeps the value uniform; only zero is moved, to the smallest valid tracker.
        long value = BitConverter.ToInt64(bytes) & LaunchProcessReceipt.MaximumBrowserTrackerId;
        return value == 0 ? 1 : value;
    }

    public static string Build(NativeHandoffContext context, ProtectedLaunchTicket ticket, long browserTrackerId, Guid joinAttemptId, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(ticket); ArgumentNullException.ThrowIfNull(time);
        context.Validated();
        if (browserTrackerId is <= 0 or > LaunchProcessReceipt.MaximumBrowserTrackerId) throw new ArgumentOutOfRangeException(nameof(browserTrackerId));
        if (joinAttemptId == Guid.Empty) throw new ArgumentException("A launch request needs a fresh join attempt ID.", nameof(joinAttemptId));
        if (ticket.IsConsumed) throw new ObjectDisposedException(nameof(ProtectedLaunchTicket));
        var destination = context.Destination;
        string tracker = browserTrackerId.ToString(CultureInfo.InvariantCulture);
        string launcher = Launcher + (destination.JobId is null ? "RequestGame" : "RequestGameJob") + "&browserTrackerId=" + tracker
            + "&placeId=" + destination.PlaceId.ToString(CultureInfo.InvariantCulture) + (destination.JobId is null ? "" : "&gameId=" + destination.JobId)
            + "&isPlayTogetherGame=false&joinAttemptId=" + joinAttemptId.ToString("D") + "&joinAttemptOrigin=" + (destination.JobId is null ? "PlayButton" : "JoinInstance");
        long launchTime = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (launchTime <= 0) throw new InvalidOperationException("The clock is not usable for a launch request.");
        string tail = "+launchtime:" + launchTime.ToString(CultureInfo.InvariantCulture) + "+placelauncherurl:" + Encode(launcher)
            + "+browsertrackerid:" + tracker + "+robloxLocale:" + Locale + "+gameLocale:" + Locale + "+LaunchExp:InApp+channel:";
        // The ticket is written percent-encoded into one rented buffer between the prefix and the tail, then the buffer is zeroed.
        char[] buffer = ArrayPool<char>.Shared.Rent(Prefix.Length + 3 * ticket.Length + tail.Length);
        try
        {
            int count = 0;
            Prefix.AsSpan().CopyTo(buffer); count += Prefix.Length;
            ticket.Consume(span => count += Encode(span, buffer.AsSpan(count)));
            tail.AsSpan().CopyTo(buffer.AsSpan(count)); count += tail.Length;
            return new string(buffer, 0, count);
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    private static string Encode(string value)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(3 * value.Length);
        try { return new string(buffer, 0, Encode(value, buffer)); }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    // RFC 3986 unreserved characters stay; everything else (visible ASCII only: the ticket is validated and the launcher is
    // built here) becomes an upper-case percent escape, which the shared grammar decodes back exactly.
    private static int Encode(ReadOnlySpan<char> value, Span<char> target)
    {
        const string hex = "0123456789ABCDEF";
        int count = 0;
        foreach (char character in value)
        {
            if (character is < '!' or > '~') throw new ArgumentException("A launch request field must be visible ASCII.");
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~') target[count++] = character;
            else { target[count++] = '%'; target[count++] = hex[character >> 4]; target[count++] = hex[character & 0xF]; }
        }
        return count;
    }
}
