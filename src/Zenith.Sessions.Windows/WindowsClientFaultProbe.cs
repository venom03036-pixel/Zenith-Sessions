using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Read-only crash records for one ended client. Windows' Application Error record (event 1000) must name this exact
/// process by pid and creation time; only its time and exception code are kept, never its paths, modules or report ids.
/// Roblox crash dumps are counted by creation time in the top level of its crash report folders and are never opened.
/// No process handle (the client is gone), no spawned tool, no file writes. Per call: at most 128 events and 512 entries.
/// Uses the in-box wevtapi.dll directly instead of adding the System.Diagnostics.EventLog package (Rule 19).
/// </summary>
public sealed class WindowsClientFaultProbe : IClientFaultProbe
{
    public const int MaximumEvents = 128, MaximumDumpEntries = 512;
    /// <summary>Roblox's crash handler can start writing a dump while the failing client is still listed.</summary>
    public static readonly TimeSpan DumpLead = TimeSpan.FromMinutes(1);
    private const int MaximumEventBytes = 64 * 1024;
    private static readonly XmlReaderSettings XmlSettings = new()
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumEventBytes, IgnoreWhitespace = true };
    private readonly string _channel;
    private readonly string[] _dumpFolders;

    public WindowsClientFaultProbe() : this("Application", DefaultDumpFolders(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))) { }

    internal WindowsClientFaultProbe(string channel, IReadOnlyList<string> dumpFolders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel); ArgumentNullException.ThrowIfNull(dumpFolders);
        _channel = channel; _dumpFolders = dumpFolders.ToArray();
    }

    internal static string[] DefaultDumpFolders(string local) => string.IsNullOrEmpty(local) ? [] :
        Array.ConvertAll(RobloxClientFolders.LogRoots(local), root => Path.Combine(root.Path, "crashes", "reports"));

    public ClientFaultCheck Check(RecentClientHistoryEntry ended, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ended);
        if (ended.NoLongerObservedUtc is not { } gone || now < gone || !ended.Process.IsValid)
            throw new ArgumentException("Only an ended client can be checked for crash records.", nameof(ended));
        ct.ThrowIfCancellationRequested();
        var start = new DateTimeOffset(ended.Process.ProcessStartUtcTicks, TimeSpan.Zero);
        bool eventsRead = TryFindFault(ended.Process, start, now, ct, out var fault, out var code);
        var dumpsFrom = ended.LastObservedUtc - DumpLead < start ? start : ended.LastObservedUtc - DumpLead;
        bool dumpsRead = TryCountDumps(dumpsFrom, now, ct, out int dumps);
        return new(now, eventsRead && dumpsRead, fault, code, dumps);
    }

    private bool TryFindFault(SessionProcessKey process, DateTimeOffset from, DateTimeOffset until, CancellationToken ct,
        out DateTimeOffset? fault, out uint? code)
    {
        fault = null; code = null;
        // The window is exactly [process start, check time]: a later record would be inconsistent with the check.
        string query = "*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[@SystemTime>='"
            + XPathTime(from) + "' and @SystemTime<='" + XPathTime(until) + "']]]";
        using var results = Native.EvtQuery(0, _channel, query, Native.QueryChannelPath | Native.QueryReverseDirection);
        if (results.IsInvalid) return false;
        var events = new nint[16]; var buffer = new byte[8192];
        int seen = 0; bool skipped = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!Native.EvtNext(results, events.Length, events, 2000, 0, out int returned))
                return Marshal.GetLastWin32Error() == Native.ErrorNoMoreItems && !skipped;
            try
            {
                for (int index = 0; index < returned && fault is null; index++)
                {
                    if (Render(events[index], ref buffer) is not { } xml) { skipped = true; continue; }
                    try
                    {
                        if (!Matches(xml, process, out var time, out var exception)) continue;
                        // The query's bounds are whole milliseconds, so after a clock step a record for this process can
                        // still fall just outside the window. It cannot be placed, so the check is incomplete.
                        if (time < from || time > until) skipped = true;
                        else { fault = time; code = exception; }
                    }
                    catch (XmlException) { skipped = true; }
                }
            }
            finally { for (int index = 0; index < returned; index++) Native.EvtClose(events[index]); }
            seen += returned;
            if (fault is not null) return true;
            if (seen >= MaximumEvents) return false;
        }
    }

    private static string? Render(nint record, ref byte[] buffer)
    {
        if (!Native.EvtRender(0, record, Native.RenderEventXml, buffer.Length, buffer, out int used, out _))
        {
            if (Marshal.GetLastWin32Error() != Native.ErrorInsufficientBuffer || used <= buffer.Length || used > MaximumEventBytes) return null;
            buffer = new byte[used];
            if (!Native.EvtRender(0, record, Native.RenderEventXml, buffer.Length, buffer, out used, out _)) return null;
        }
        // The used byte count includes the terminating UTF-16 null.
        int length = Math.Clamp(used, 0, buffer.Length) & ~1;
        if (length >= 2 && buffer[length - 2] == 0 && buffer[length - 1] == 0) length -= 2;
        return Encoding.Unicode.GetString(buffer, 0, length);
    }

    /// <summary>True only for an event naming this pid and this exact creation FILETIME.</summary>
    internal static bool Matches(string xml, SessionProcessKey process, out DateTimeOffset time, out uint? code)
    {
        time = default; code = null;
        string? pid = null, created = null, exception = null, systemTime = null;
        using (var reader = XmlReader.Create(new StringReader(xml), XmlSettings))
        {
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "TimeCreated") systemTime = reader.GetAttribute("SystemTime");
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Data")
                {
                    string? name = reader.GetAttribute("Name");
                    string value = reader.ReadElementContentAsString();
                    if (name == "ProcessId") pid = value;
                    else if (name == "ProcessCreationTime") created = value;
                    else if (name == "ExceptionCode") exception = value;
                    continue;
                }
                reader.Read();
            }
        }
        if (!TryHex(pid, out ulong id) || id != (ulong)process.ProcessId || !TryHex(created, out ulong fileTime)
            || fileTime > (ulong)DateTime.MaxValue.ToFileTimeUtc() || DateTime.FromFileTimeUtc((long)fileTime).Ticks != process.ProcessStartUtcTicks
            || !DateTimeOffset.TryParse(systemTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
            return false;
        code = TryHex(exception, out ulong status) && status <= uint.MaxValue ? (uint)status : null;
        return true;
    }

    private static bool TryHex(string? text, out ulong value)
    {
        value = 0;
        var digits = text.AsSpan().Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) digits = digits[2..];
        return digits.Length is > 0 and <= 16 && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static string XPathTime(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private bool TryCountDumps(DateTimeOffset from, DateTimeOffset until, CancellationToken ct, out int dumps)
    {
        dumps = 0; bool complete = true; int inspected = 0;
        foreach (string folder in _dumpFolders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var directory = new DirectoryInfo(folder);
                if (!directory.Exists) continue;
                // A redirected folder is not Roblox's own crash folder.
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { complete = false; continue; }
                // Names, attributes and times come from the directory listing; no dump is opened.
                foreach (var file in directory.EnumerateFiles("*.dmp", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++inspected > MaximumDumpEntries) return false;
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || !file.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var created = new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);
                    if (created >= from && created <= until && dumps < ClientFaultCheck.MaximumCrashDumps) dumps++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { complete = false; }
        }
        return complete;
    }

    private sealed class EventLogHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EventLogHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.EvtClose(handle);
    }

    private static class Native
    {
        internal const int QueryChannelPath = 0x1, QueryReverseDirection = 0x200, RenderEventXml = 1;
        internal const int ErrorInsufficientBuffer = 122, ErrorNoMoreItems = 259;
        [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern EventLogHandle EvtQuery(nint session, string path, string query, int flags);
        [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EvtNext(EventLogHandle results, int eventsSize, [Out] nint[] events, int timeout, int flags, out int returned);
        [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EvtRender(nint context, nint fragment, int flags, int bufferSize, [Out] byte[] buffer, out int bufferUsed, out int propertyCount);
        [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EvtClose(nint handle);
    }
}
