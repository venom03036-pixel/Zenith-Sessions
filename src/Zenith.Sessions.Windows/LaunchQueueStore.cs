using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Which fail-closed state stopped the last queue load, save or set-aside.</summary>
public enum LaunchQueueBlockerKind { InterruptedWrite,OrphanEvidence,ConflictingDamagedCopy,UnreadableQueue,UnreadableBackup,UnsupportedFormat }
/// <summary>A fail-closed queue state and the kept file (a fixed name in the data folder) that causes it.</summary>
public sealed record LaunchQueueBlocker(LaunchQueueBlockerKind Kind,string FileName);

public sealed class LaunchQueueStore(string root) : ILaunchQueueStore,IDisposable
{
    private const string FileName="launch-queue.json";
    private readonly SemaphoreSlim _writer=new(1,1);
    private readonly string _path=Path.Combine(root,FileName);
    private volatile LaunchQueueBlocker? _blocker;
    public string? RecoveryNotice {get;private set;}
    /// <summary>Names the kept file behind the last failed load or save; null after a success or an unclassified failure.</summary>
    public LaunchQueueBlocker? RecoveryBlocker=>_blocker;
    private sealed record Envelope(int Version,LaunchQueueSnapshot Queue);
    private sealed class UnsupportedQueueFormatException : Exception { }
    private static readonly JsonSerializerOptions Json=new() { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    public async Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecoveryNotice=null;_blocker=null;
        bool primaryPresent=Exists(_path);
        if(primaryPresent)
        {
            try {return await ReadGenerationAsync("",ct).ConfigureAwait(false);}
            catch(Exception ex) when(Recoverable(ex))
            {
                if(!Exists(_path+".bak")) throw Blocked(LaunchQueueBlockerKind.UnreadableQueue,"",ex);
            }
        }
        if(Exists(_path+".bak"))
        {
            try
            {
                var recovered=await ReadGenerationAsync(".bak",ct).ConfigureAwait(false);
                // Backup intent is historical. Loading never promotes it or resumes activity. Its
                // waiting rows become review rows here (BackupRowForReview), so the coordinator's
                // never-started recovery does not apply to them. The coordinator then keeps recovered
                // preparation cancellable and a receipted, budgeted started client as saved, and turns
                // every other active row into review. Nothing is replayed.
                RecoveryNotice="Loaded the verified launch-queue backup. Recovered requests are paused for review; no client was restarted. Original recovery files were preserved.";
                return recovered with{Paused=true,Operations=recovered.Operations.Select(BackupRowForReview).ToArray()};
            }
            catch(Exception ex) when(Recoverable(ex)){throw Blocked(LaunchQueueBlockerKind.UnreadableBackup,".bak",ex);}
        }
        if(primaryPresent) throw Blocked(LaunchQueueBlockerKind.UnreadableQueue,"");
        if(Exists(_path+".tmp")) throw Blocked(LaunchQueueBlockerKind.OrphanEvidence,".tmp");
        if(Exists(_path+".damaged")) throw Blocked(LaunchQueueBlockerKind.OrphanEvidence,".damaged");
        return LaunchQueueSnapshot.Empty;
    }
    private InvalidDataException Blocked(LaunchQueueBlockerKind kind,string suffix,Exception? error=null)
    { _blocker=new(kind,FileName+suffix);return RecoveryRequired(error); }
    // Unsupported generations keep their own exception; record which kept file needs a compatible version.
    private async Task<LaunchQueueSnapshot> ReadGenerationAsync(string suffix,CancellationToken ct)
    {
        try {return await ReadAsync(_path+suffix,ct).ConfigureAwait(false);}
        catch(InvalidDataException ex) when(ex.InnerException is UnsupportedQueueFormatException)
        { _blocker=new(LaunchQueueBlockerKind.UnsupportedFormat,FileName+suffix);throw; }
    }
    // The backup is one generation behind. The lost primary may already have moved a queued or
    // blocked request into native startup, so the backup's copy cannot prove that nothing started.
    private static LaunchOperation BackupRowForReview(LaunchOperation operation)=>
        operation.State==LaunchState.Queued || operation.State==LaunchState.Blocked&&operation.Problem!=LaunchProblem.PreparationInterrupted
            ?operation with { State=LaunchState.ReviewRequired,Problem=LaunchProblem.Interrupted }:operation;
    private static bool Exists(string path)
    {
        try {File.GetAttributes(path);return true;}
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    private static InvalidDataException RecoveryRequired(Exception? error=null)=>new(
        "The launch queue needs recovery. Existing files were preserved; restore a verified backup or review the interrupted files before launching.",error);
    private async Task PreserveDamagedAsync(string path,CancellationToken ct)
    {
        byte[] original;
        // An oversized original stays in place instead of being copied beyond the decoded bound.
        try {original=await ReadBoundedBytesAsync(path,ct).ConfigureAwait(false);}
        catch(InvalidDataException) {_blocker=new(LaunchQueueBlockerKind.UnreadableQueue,FileName);throw;}
        if(Exists(path+".damaged"))
        {
            byte[] existing;
            try {existing=await ReadBoundedBytesAsync(path+".damaged",ct).ConfigureAwait(false);}
            catch(InvalidDataException) {_blocker=new(LaunchQueueBlockerKind.ConflictingDamagedCopy,FileName+".damaged");throw;}
            if(!original.AsSpan().SequenceEqual(existing)) throw Blocked(LaunchQueueBlockerKind.ConflictingDamagedCopy,".damaged");
            return;
        }
        ct.ThrowIfCancellationRequested();
        await using var saved=new FileStream(path+".damaged",FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough);
        await saved.WriteAsync(original,ct).ConfigureAwait(false);await saved.FlushAsync(ct).ConfigureAwait(false);saved.Flush(true);
    }
    private static async Task<byte[]> ReadBoundedBytesAsync(string path,CancellationToken ct)
    {
        await using var input=File.OpenRead(path);
        if(input.Length>1048576) throw RecoveryRequired();
        var bytes=new byte[checked((int)input.Length)];await input.ReadExactlyAsync(bytes,ct).ConfigureAwait(false);return bytes;
    }
    private static async Task<LaunchQueueSnapshot> ReadAsync(string path,CancellationToken ct)
    {
        byte[] bytes=await ReadBoundedBytesAsync(path,ct).ConfigureAwait(false);
        return Decode(bytes,ct);
    }
    private static LaunchQueueSnapshot Decode(byte[] bytes,CancellationToken ct)
    {
        // The prior stream decoder accepted an optional UTF-8 BOM. Keep that compatibility
        // while the version preflight operates on actual JSON tokens, not an encoding marker.
        ReadOnlyMemory<byte> content=bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf?bytes.AsMemory(3):bytes;
        InspectVersionBeforeDecoding(content.Span,ct,out _);
        using var document=JsonDocument.Parse(content);
        // Inspect the bounded envelope version before decoding a future queue body. Otherwise
        // unknown future fields could produce JsonException and silently select an older backup.
        int versions=0;JsonElement version=default;
        if(document.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("The saved launch queue is invalid.");
        foreach(var property in document.RootElement.EnumerateObject())
            if(property.NameEquals("Version")) {versions++;version=property.Value;}
        if(versions>1 || versions==1 && version.ValueKind==JsonValueKind.Number
            && (!version.TryGetInt32(out int found) || found is not (1 or 2 or 3 or 4 or 5 or 6 or 7)))
            throw new InvalidDataException("The launch queue has an unsupported format; it was preserved without replay or fallback.",new UnsupportedQueueFormatException());
        // Preparation begins at v5, explicit closure acknowledgment at v6 and pinned resource budgets at v7. Inspect bounded properties
        // before Deserialize: unknown fields or duplicate properties must not make a newer
        // preparation marker look like ordinary corruption and silently select an old backup.
        int format=versions==1 && version.ValueKind==JsonValueKind.Number && version.TryGetInt32(out int currentVersion)?currentVersion:0;
        if(HasUnsupportedSemanticMarkers(document.RootElement,format))
            throw new InvalidDataException("The launch queue contains state data unsupported by its format; it was preserved without replay or fallback.",new UnsupportedQueueFormatException());
        RejectDuplicateProperties(document.RootElement,ct);
        ct.ThrowIfCancellationRequested();
        var envelope=document.RootElement.Deserialize<Envelope>(Json);
        ct.ThrowIfCancellationRequested();
        if(envelope is not { Version:1 or 2 or 3 or 4 or 5 or 6 or 7 }) throw new InvalidDataException("The saved launch queue is invalid.");
        if(envelope.Queue is null) throw new InvalidDataException("The saved launch queue is invalid.");
        LaunchCoordinator.ValidateSnapshot(envelope.Queue);
        if(envelope.Version<4)
        {
            if(envelope.Queue.Operations.Any(o=>o.ProcessReceipt is not null || o.State==LaunchState.ClientStartedUnverified || o.Problem==LaunchProblem.ClientStartedUnverified))
                throw new InvalidDataException("A legacy launch queue contains unsupported process receipt data.");
            return new(true,envelope.Queue.Operations.Select(o=>LaunchDescriptions.Terminal(o.State)?o:
                o with { State=LaunchState.ReviewRequired,Problem=LaunchProblem.Interrupted }).ToArray());
        }
        return envelope.Queue;
    }
    private static void InspectVersionBeforeDecoding(ReadOnlySpan<byte> bytes,CancellationToken ct,out bool supportedVersion)
    {
        // The iterative preflight can inspect an unfamiliar deep prefix within the total
        // 1-MiB byte bound. Normal queue decoding still keeps its existing JSON depth bound.
        // A known future/ambiguous version cannot be hidden by later parse failures.
        supportedVersion=false;
        var reader=new Utf8JsonReader(bytes,new JsonReaderOptions{MaxDepth=1048576});int versions=0;
        while(reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if(reader.TokenType!=JsonTokenType.PropertyName||reader.CurrentDepth!=1||!reader.ValueTextEquals("Version"u8))continue;
            if(++versions>1)throw UnsupportedVersion();
            if(!reader.Read())return;
            if(reader.TokenType==JsonTokenType.Number&&(!reader.TryGetInt32(out int version)||version is not (1 or 2 or 3 or 4 or 5 or 6 or 7)))
                throw UnsupportedVersion();
            supportedVersion=reader.TokenType==JsonTokenType.Number;
        }
        static InvalidDataException UnsupportedVersion()=>new("The launch queue has an unsupported or ambiguous format; existing generations were preserved.",new UnsupportedQueueFormatException());
    }
    private static bool HasUnsupportedSemanticMarkers(JsonElement root,int format)
    {
        if(format>=7)return false;
        foreach(var queue in root.EnumerateObject())
        {
            if(!queue.NameEquals("Queue") || queue.Value.ValueKind!=JsonValueKind.Object) continue;
            foreach(var operations in queue.Value.EnumerateObject())
            {
                if(!operations.NameEquals("Operations") || operations.Value.ValueKind!=JsonValueKind.Array) continue;
                foreach(var operation in operations.Value.EnumerateArray())
                {
                    if(operation.ValueKind!=JsonValueKind.Object) continue;
                    foreach(var field in operation.EnumerateObject())
                    {
                        if(format<7 && field.NameEquals("ResourceBudget") && field.Value.ValueKind!=JsonValueKind.Null)return true;
                        if(format<5 && field.NameEquals("State") && Marker(field.Value,(int)LaunchState.Preparing,nameof(LaunchState.Preparing))) return true;
                        if(format<5 && field.NameEquals("Problem") && (Marker(field.Value,(int)LaunchProblem.PreparationRejected,nameof(LaunchProblem.PreparationRejected))
                            || Marker(field.Value,(int)LaunchProblem.PreparationTimedOut,nameof(LaunchProblem.PreparationTimedOut))
                            || Marker(field.Value,(int)LaunchProblem.PreparationExpired,nameof(LaunchProblem.PreparationExpired))
                            || Marker(field.Value,(int)LaunchProblem.PreparationCleanupFailed,nameof(LaunchProblem.PreparationCleanupFailed))
                            || Marker(field.Value,(int)LaunchProblem.PreparationInterrupted,nameof(LaunchProblem.PreparationInterrupted)))) return true;
                        if(format<6 && field.NameEquals("Problem") && Marker(field.Value,(int)LaunchProblem.ClientClosureAcknowledged,nameof(LaunchProblem.ClientClosureAcknowledged)))return true;
                    }
                }
            }
        }
        return false;
        static bool Marker(JsonElement value,int number,string name) => value.ValueKind==JsonValueKind.Number
            ? value.TryGetDecimal(out decimal numeric) && numeric==number
            : value.ValueKind==JsonValueKind.String && (string.Equals(value.GetString()?.Trim(),name,StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.GetString()?.Trim(),number.ToString(System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal));
    }
    private static void RejectDuplicateProperties(JsonElement element,CancellationToken ct)
    {
        // The document is already bounded to 1 MiB and JsonDocument's depth limit.
        // Check every occurrence only after future semantic preflight, so a duplicate
        // cannot hide unsupported reservation costs behind a last-value-wins decode.
        ct.ThrowIfCancellationRequested();
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in element.EnumerateObject())
            {
                if(!names.Add(field.Name))throw new InvalidDataException("The saved launch queue contains duplicate fields.");
                RejectDuplicateProperties(field.Value,ct);
            }
        }
        else if(element.ValueKind==JsonValueKind.Array)
            foreach(var value in element.EnumerateArray())RejectDuplicateProperties(value,ct);
    }
    // Caller owns _writer. Classifies the existing generations exactly as a save needs them and
    // throws, naming the blocker, for any state that must fail closed.
    private async Task<(bool Valid,bool Damaged)> InspectGenerationsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();bool oldValid=false,oldDamaged=false;
        bool primary=Exists(_path),hasBackup=Exists(_path+".bak");
        if(!primary&&!hasBackup&&Exists(_path+".damaged"))throw Blocked(LaunchQueueBlockerKind.OrphanEvidence,".damaged");
        if(primary)
        {
            try {await ReadGenerationAsync("",ct).ConfigureAwait(false);oldValid=true;}
            catch(Exception ex) when(Recoverable(ex)){oldDamaged=true;}
        }
        if(hasBackup)
        {
            try
            {
                // An unreadable/oversized backup cannot be classified as replaceable current
                // corruption. Read its bounded bytes outside the recoverable decode filter.
                byte[] backup=await ReadBoundedBytesAsync(_path+".bak",ct).ConfigureAwait(false);
                bool supportedBackup=false;
                try
                {
                    ReadOnlySpan<byte> content=backup;
                    if(backup.Length>=3&&backup[0]==0xef&&backup[1]==0xbb&&backup[2]==0xbf)content=content[3..];
                    InspectVersionBeforeDecoding(content,ct,out supportedBackup);
                    Decode(backup,ct);
                }
                catch(Exception ex) when(Recoverable(ex))
                {
                    // Known malformed backup data is not a valid generation. Normal
                    // primary validation below still controls replacement; unsupported
                    // generations escape this filter and preserve both original files.
                    if(!oldValid||!supportedBackup)throw RecoveryRequired(ex);
                }
            }
            catch(InvalidDataException ex)
            {
                _blocker=new(ex.InnerException is UnsupportedQueueFormatException?LaunchQueueBlockerKind.UnsupportedFormat:LaunchQueueBlockerKind.UnreadableBackup,FileName+".bak");
                throw;
            }
        }
        return (oldValid,oldDamaged);
    }
    public async Task SaveAsync(LaunchQueueSnapshot snapshot,CancellationToken ct)
    {
        LaunchCoordinator.ValidateSnapshot(snapshot);await _writer.WaitAsync(ct).ConfigureAwait(false);
        string temporary=_path+".tmp";bool ownsTemporary=false;
        try
        {
            _blocker=null;
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            var (oldValid,oldDamaged)=await InspectGenerationsAsync(ct).ConfigureAwait(false);
            if(oldDamaged)await PreserveDamagedAsync(_path,ct).ConfigureAwait(false);
            // CreateNew and owned cleanup preserve a preexisting interrupted write. A failed
            // preflight never truncates it, including when the main file is a future format.
            // Behind a readable primary it is set aside only by an explicit user choice.
            if(Exists(temporary))
            {
                _blocker=new(oldValid?LaunchQueueBlockerKind.InterruptedWrite:LaunchQueueBlockerKind.OrphanEvidence,FileName+".tmp");
                throw new IOException("An earlier interrupted launch-queue save (launch-queue.json.tmp) was kept. The queue was not saved.");
            }
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {
                ownsTemporary=true;
                await JsonSerializer.SerializeAsync(file,new Envelope(7,snapshot),Json,ct).ConfigureAwait(false);
                if(file.Length>1048576) throw new InvalidDataException("The launch queue exceeds its storage limit.");
                await file.FlushAsync(ct).ConfigureAwait(false);file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if(Exists(_path))File.Replace(temporary,_path,oldValid?_path+".bak":null);
            else File.Move(temporary,_path);
        }
        finally
        {
            try {if(ownsTemporary&&File.Exists(temporary))File.Delete(temporary);}
            finally {_writer.Release();}
        }
    }
    /// <summary>
    /// Renames a kept interrupted save (<c>launch-queue.json.tmp</c>) to a timestamped
    /// <c>launch-queue.json.interrupted-...</c> file so the committed queue can be saved again.
    /// Nothing is deleted, overwritten or decoded. The interrupted save can be newer than the
    /// primary: a crash while saving a provider's result leaves a started client's PID/start
    /// receipt only there. Setting it aside stays fail-closed because the coordinator commits
    /// Launching before any provider call, and when the queue reopens a row in Launching or any
    /// later unresolved state comes back as a reserving row that needs review. A lost newer save can
    /// therefore only leave more work held, never release a reservation or allow a second start.
    /// Such a row has lost its receipt, so exact-observation reconciliation cannot clear it; the
    /// user reviews it instead.
    /// Refuses unless the primary is a readable supported generation and no other blocker exists.
    /// Returns the kept file name, or null when there was no interrupted save to set aside.
    /// </summary>
    public async Task<string?> SetAsideInterruptedWriteAsync(CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _blocker=null;
            var (valid,_)=await InspectGenerationsAsync(ct).ConfigureAwait(false);
            string temporary=_path+".tmp";
            if(!Exists(temporary)) return null;
            if(!valid) throw Blocked(LaunchQueueBlockerKind.OrphanEvidence,".tmp");
            ct.ThrowIfCancellationRequested();
            string kept=FileName+".interrupted-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'",System.Globalization.CultureInfo.InvariantCulture);
            File.Move(temporary,Path.Combine(root,kept),overwrite:false);
            return kept;
        }
        finally {_writer.Release();}
    }
    private static bool Recoverable(Exception error)=>error is JsonException || error is InvalidDataException {InnerException:not UnsupportedQueueFormatException};
    public void Dispose()=>_writer.Dispose();
}
