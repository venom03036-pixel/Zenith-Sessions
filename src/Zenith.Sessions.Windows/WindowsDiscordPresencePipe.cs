using System.IO.Pipes;
using System.Security.Principal;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Discord's local IPC pipe on Windows: the Discord app listens on \\.\pipe\discord-ipc-0 (or the next free slot up to 9). The
/// named-pipe directory is listed first, which has no side effect on any server, so when Discord is not running no connection is
/// attempted at all and the answer is null at once; an open slot is connected with a bounded wait, lowest slot first, and a busy
/// or vanished slot gives way to the next. The client never lets the server impersonate it. The test seam moves only the name
/// prefix, so the production path is exercised over real named pipes against a test server.
/// </summary>
public sealed class WindowsDiscordPresencePipe : IDiscordPresencePipe
{
    public const string ProductionPrefix = "discord-ipc-";
    public const int SlotCount = 10;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private const string PipeDirectory = @"\\.\pipe\";
    private readonly string _prefix;

    public WindowsDiscordPresencePipe() : this(ProductionPrefix) { }
    internal WindowsDiscordPresencePipe(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length > 200 || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("A pipe name prefix is letters, digits, '-', '_' or '.'.", nameof(prefix));
        _prefix = prefix;
    }

    public async Task<Stream?> ConnectAsync(CancellationToken ct)
    {
        foreach (string name in OpenSlots())
        {
            ct.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
                return pipe;
            }
            // Busy (another client holds the slot) or gone since the listing: the next slot.
            catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException) { await pipe.DisposeAsync().ConfigureAwait(false); }
            catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        }
        return null;
    }

    /// <summary>The slots open right now ("prefix" + one digit), lowest first. Listing the pipe directory touches no server.</summary>
    internal IReadOnlyList<string> OpenSlots()
    {
        var slots = new List<(int Slot, string Name)>();
        foreach (string entry in Directory.EnumerateFiles(PipeDirectory))
        {
            string name = Path.GetFileName(entry);
            if (name.Length != _prefix.Length + 1 || !name.StartsWith(_prefix, StringComparison.Ordinal)) continue;
            char digit = name[^1];
            if (!char.IsAsciiDigit(digit)) continue;
            slots.Add((digit - '0', name));
        }
        slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return slots.Select(slot => slot.Name).ToArray();
    }
}
