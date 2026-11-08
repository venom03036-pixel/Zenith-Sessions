using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed record ClientWindowLayoutSettings(ClientWindowTileOptions? Tile = null, string? PreferredDisplayId = null,
    ClientArrivalWindowAction ArrivalAction = ClientArrivalWindowAction.None)
{
    public ClientWindowLayoutSettings Validated()
    {
        if (PreferredDisplayId is not null && (PreferredDisplayId.Length is < 1 or > 100 || PreferredDisplayId.Any(char.IsControl)))
            throw new InvalidDataException("The preferred display identifier is invalid.");
        // Optional within schema 1: files without it keep windows alone when clients connect, and older builds ignore it.
        if (!Enum.IsDefined(ArrivalAction)) throw new InvalidDataException("The automatic window layout choice is invalid.");
        return this with { Tile = (Tile ?? new()).Validated() };
    }
}

public sealed record SessionsSettings(int Version = 1, string Theme = "graphite-mint", bool DenseAccounts = false,
    CapacitySettings? Capacity = null, string OAuthClientId = "", int OAuthCallbackPort = 47635,
    ManagerPerformanceSettings? Performance = null, ClientWindowLayoutSettings? WindowLayout = null,
    ManagerWindowPlacement? ManagerWindow = null, bool CloseButtonExits = false, int IdleLockMinutes = 0, string PanicShortcut = "F12",
    bool MuteArrivingClients = false, bool KeepSignInBrowserData = false, bool CheckRobloxUpdates = false)
{
    // ManagerWindow, CloseButtonExits, IdleLockMinutes, PanicShortcut, MuteArrivingClients and CheckRobloxUpdates are optional within
    // schema 1: files without them load unchanged, and older builds ignore them. Without CloseButtonExits the window's close button
    // keeps hiding the manager to the tray; without IdleLockMinutes (or with 0) the manager never locks itself after idle time; without
    // PanicShortcut the global panic shortcut is F12; without MuteArrivingClients no client is muted when it appears; without
    // CheckRobloxUpdates Roblox's current release is read only on an explicit Refresh installation or native trial start. A
    // PanicShortcut that earlier builds saved but that is no longer allowed (such as Ctrl+Alt+Q, which is AltGr+Q) is read as F12
    // with a notice by SettingsStore; any other refused value makes the settings invalid.
    public SessionsSettings Validated()
    {
        if (Version != 1 || Theme is null || Theme.Length > 80 || OAuthClientId is null || OAuthClientId.Length > 100 || OAuthClientId.Any(c => !char.IsAsciiDigit(c))
            || OAuthCallbackPort is < 1024 or > 65535 || !IdleLockPolicy.IsValidMinutes(IdleLockMinutes)
            || !global::Zenith.Sessions.Core.PanicShortcut.TryParse(PanicShortcut, out _, out _)) throw new InvalidDataException("Settings have an unsupported version or invalid value.");
        (Capacity ?? new()).Validated();
        (Performance ?? new()).Validated();
        ManagerWindow?.Validated();
        return this with { Capacity = Capacity ?? new(), Performance = Performance ?? new(), WindowLayout = (WindowLayout ?? new()).Validated() };
    }
}

public sealed class SettingsStore(string root) : IDisposable
{
    private const int MaximumBytes = 65536;
    private sealed class UnsupportedSettingsFormatException : Exception { }
    // RetiredPanicShortcut is why the saved panic shortcut was replaced by F12, or null when it was kept.
    private sealed record SettingsRead(SessionsSettings Settings, bool ImplicitVersion, string? RetiredPanicShortcut);
    private readonly SemaphoreSlim _writer = new(1);
    public string Path { get; } = System.IO.Path.Combine(root, "settings.json");
    public async Task<(SessionsSettings Settings, string? Notice)> LoadAsync(CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ct.ThrowIfCancellationRequested();
            bool primary = Exists(Path), backup = Exists(Path + ".bak"), temporary = Exists(Path + ".tmp"), damaged = Exists(Path + ".damaged");
            if (!primary)
            {
                if (backup)
                {
                    var recovered = await ReadRecoveryAsync(Path + ".bak", ct).ConfigureAwait(false);
                    return (recovered.Settings, Notice(recovered, "The main settings file is missing. Loaded the last valid settings copy without changing files. Review and save to repair the main file."));
                }
                if (temporary || damaged) throw new InvalidDataException("Settings recovery evidence exists without a valid main file or backup. Existing files were preserved; review them before creating settings.");
                var defaults = new SessionsSettings().Validated(); await SaveOwnedAsync(defaults, ct).ConfigureAwait(false); return (defaults, null);
            }
            try
            {
                var current = await ReadAsync(Path, ct).ConfigureAwait(false);
                return (current.Settings, Notice(current, temporary ? "Loaded current settings. An interrupted temporary settings copy was preserved; review it before saving." : null));
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                if (backup)
                {
                    var recovered = await ReadRecoveryAsync(Path + ".bak", ct).ConfigureAwait(false);
                    return (recovered.Settings, Notice(recovered, "Loaded the last valid settings copy. Review and save to repair the main file."));
                }
                throw new InvalidDataException("Settings are damaged and no valid backup is available. Preserve the files and open the settings folder for recovery.", ex);
            }
        }
        finally { _writer.Release(); }
    }
    /// <summary>
    /// UI thread, before the manager window is first shown: the window placement in the main settings file, or null when there
    /// is none or the file cannot be read right now. Read-only and best effort, so the window opens at its default place instead;
    /// it never creates, repairs or recovers files, and any real settings problem is still reported by <see cref="LoadAsync"/>.
    /// </summary>
    public ManagerWindowPlacement? ReadManagerWindow()
    {
        if (!_writer.Wait(0)) return null;
        try { return ReadMainBeforeShow(Path)?.ManagerWindow; }
        finally { _writer.Release(); }
    }
    /// <summary>
    /// UI thread, at startup before the workspace and its store exist (so nothing in this process writes settings yet): the theme
    /// id saved in the main settings file under <paramref name="root"/>, or null, so the first frame uses the saved palette.
    /// Same best-effort rules as <see cref="ReadManagerWindow"/>: one bounded read of the main file only (never the backup or
    /// temporary copy), null on any problem, and never creates, repairs or recovers files. <see cref="LoadAsync"/> still decides.
    /// </summary>
    public static string? ReadTheme(string root) => ReadMainBeforeShow(System.IO.Path.Combine(root, "settings.json"))?.Theme;
    private static SessionsSettings? ReadMainBeforeShow(string path)
    {
        try
        {
            if (!Exists(path)) return null;
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaximumBytes) return null;
                bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            }
            return Decode(bytes, CancellationToken.None).Settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException) { return null; }
    }
    private static string? Notice(SettingsRead read, string? notice)
    {
        if (read.RetiredPanicShortcut is { } reason)
            notice = Join(notice, "The saved panic shortcut is no longer allowed, so F12 is used until you choose another in Settings → Panic shortcut and save. " + reason);
        if (read.ImplicitVersion) notice = Join(notice, "Legacy settings without a version were read as schema 1. Review and save to add an explicit version.");
        return notice;
        static string Join(string? first, string second) => first is null ? second : first + " " + second;
    }
    private static bool Exists(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A settings entry is not a regular local file. Existing recovery evidence was preserved.");
        return true;
    }
    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Settings exceed the size limit.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); return bytes;
    }
    private static async Task<SettingsRead> ReadAsync(string path, CancellationToken ct)
        => Decode(await ReadBytesAsync(path, ct).ConfigureAwait(false), ct);
    private static async Task<SettingsRead> ReadRecoveryAsync(string path, CancellationToken ct)
    {
        try { return await ReadAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (Recoverable(ex))
        { throw new InvalidDataException("The settings recovery copy is damaged. Existing files were preserved; review them before saving.", ex); }
    }
    private static SettingsRead Decode(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> json = bytes;
        // Preserve the stream reader's existing UTF-8 BOM compatibility without
        // normalizing or rewriting the stored original during recovery.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = bytes.AsMemory(3);
        bool implicitVersion = PreflightVersion(json.Span, ct);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Settings are invalid.");
        ct.ThrowIfCancellationRequested();
        var settings = document.RootElement.Deserialize<SessionsSettings>() ?? throw new InvalidDataException("Settings are empty.");
        // Earlier builds accepted some panic shortcuts that type or edit in other programs. Such a choice is replaced by F12 with
        // a notice, so the other settings and the backup stay usable; the next save writes F12 or the user's new choice.
        string? retired = null;
        if (PanicShortcut.IsNoLongerAllowed(settings.PanicShortcut))
        {
            PanicShortcut.TryParse(settings.PanicShortcut, out _, out string reason);
            retired = reason; settings = settings with { PanicShortcut = PanicShortcut.Default.ToString() };
        }
        settings = settings.Validated();
        ct.ThrowIfCancellationRequested(); return new(settings, implicitVersion, retired);
    }
    private static bool PreflightVersion(ReadOnlySpan<byte> json, CancellationToken ct)
    {
        // This scan has the same byte bound as the file, but can traverse an unfamiliar
        // deep prefix before deciding its format. Normal document/typed depth limits
        // still apply afterward. Reject a future/duplicate version immediately, even
        // when later JSON is malformed, so ordinary corruption cannot select a backup.
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaximumBytes });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return true;
        int versions = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("Version")) continue;
            versions++;
            if (versions > 1) throw UnsupportedFormat();
            if (!reader.Read()) throw new JsonException("Settings have an incomplete version.");
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value) || value != 1) throw UnsupportedFormat();
        }
        return versions == 0;
    }
    private static InvalidDataException UnsupportedFormat() => new("Settings have an unsupported format. Existing files were preserved without fallback or overwrite.", new UnsupportedSettingsFormatException());
    public async Task SaveAsync(SessionsSettings settings, CancellationToken ct)
    {
        settings = settings.Validated(); await _writer.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await SaveOwnedAsync(settings, ct).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }
    private async Task SaveOwnedAsync(SessionsSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool primary = Exists(Path), backup = Exists(Path + ".bak"), temporary = Exists(Path + ".tmp"), damaged = Exists(Path + ".damaged");
        if (temporary) throw new InvalidDataException("An interrupted temporary settings copy already exists. It was preserved; review it before saving.");
        if (!primary && !backup && damaged) throw new InvalidDataException("Settings recovery evidence exists without a valid main file or backup. It was preserved; review it before saving.");
        byte[]? original = null; bool validOld = false;
        if (primary)
        {
            original = await ReadBytesAsync(Path, ct).ConfigureAwait(false);
            try { Decode(original, ct); validOld = true; }
            catch (Exception ex) when (Recoverable(ex)) { /* Preserve this bounded malformed original below; unsupported versions are never caught. */ }
        }
        if (backup)
        {
            byte[] bytes = await ReadBytesAsync(Path + ".bak", ct).ConfigureAwait(false);
            try { Decode(bytes, ct); }
            catch (Exception ex) when (Recoverable(ex))
            {
                if (!primary) throw new InvalidDataException("The orphan settings backup is damaged. Existing files were preserved; review them before saving.", ex);
                // A known malformed backup may be replaced by the validated primary; future formats propagate.
            }
        }
        if (original is not null && !validOld && damaged)
        {
            byte[] preserved = await ReadBytesAsync(Path + ".damaged", ct).ConfigureAwait(false);
            if (!original.AsSpan().SequenceEqual(preserved))
                throw new InvalidDataException("A different damaged settings original is already preserved. Both originals were retained; review recovery files before saving.");
        }
        Directory.CreateDirectory(root);
        string temporaryPath = Path + ".tmp"; bool ownsTemporary = false;
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                await JsonSerializer.SerializeAsync(file, settings, cancellationToken: ct).ConfigureAwait(false);
                if (file.Length > MaximumBytes) throw new InvalidDataException("Settings exceed the size limit.");
                await file.FlushAsync(ct).ConfigureAwait(false); file.Flush(true);
            }
            if (original is not null && !validOld && !damaged)
            {
                await using var copy = new FileStream(Path + ".damaged", FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await copy.WriteAsync(original, ct).ConfigureAwait(false); await copy.FlushAsync(ct).ConfigureAwait(false); copy.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (primary) File.Replace(temporaryPath, Path, validOld ? Path + ".bak" : null);
            else File.Move(temporaryPath, Path);
        }
        finally { if (ownsTemporary) File.Delete(temporaryPath); }
    }
    private static bool Recoverable(Exception error) => error is JsonException or ArgumentException
        || error is InvalidDataException { InnerException: not UnsupportedSettingsFormatException };
    public void Dispose()=>_writer.Dispose();
}
