using System.Text.Json;
using System.Threading.Channels;

namespace Zenith.Sessions.Windows;

public enum ManagerEventKind { Started,ProfileSaved,ProfilesRemoved,ProfilesImported,MetadataExported,SettingsSaved,AuthorizationLinked,
    AuthorizationDisconnected,OperationFailed,Panic,ControlsLocked,Shutdown,DroppedEvents,
    // Appended so earlier numeric values keep their meaning; the disk log and export store names.
    AuthorizationChecked,HandoffInspected,HandoffNotCompleted,ControlsUnlocked }

/// <summary>
/// The closed set of manager areas a failure can be attributed to, so a record says which subsystem failed
/// without any free text. Append new members only; the live journal and the report carry the name.
/// </summary>
public enum ManagerSubsystem { Library,Vault,Queue,SignIn,Directory,Telemetry,ClientWindows,Previews,MacroHost,Settings,Diagnostics,
    Launch,Installation,ClientPriority,Lifecycle,ClientMemory,RepeatLaunch,ClientSound,UploadLimit }

// No free-text payload: tokens, URLs, profile names, window titles and exception messages cannot enter this log.
public sealed class ManagerDiagnostics : IAsyncDisposable
{
    public const int JournalCapacity=512;
    private sealed record Entry(DateTimeOffset Utc,string Kind,string? ErrorType=null,long Count=0);
    private readonly Channel<ManagerDiagnosticEvent> _events;
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _writer;
    private readonly string _folder;
    private readonly int _fileLimit;
    private readonly Func<CancellationToken,Task>? _beforeWriter;
    private readonly object _gate=new();
    private readonly ManagerDiagnosticEvent?[] _journal=new ManagerDiagnosticEvent?[JournalCapacity];
    private int _nextJournal,_journalCount;
    private long _sequence,_infoCount,_warningCount,_errorCount;
    private long _dropped;
    private string? _failure;
    private bool _disposed;
    private Task? _dispose;
    public long DroppedEvents {get {lock(_gate) return _dropped;}}
    public string? Failure {get {lock(_gate) return _failure;}}
    public ManagerDiagnostics(string root,int capacity=256,int fileLimit=1024*1024) : this(root,capacity,fileLimit,null) { }
    // A test-only gate runs inside the single existing writer; production introduces no extra work.
    internal ManagerDiagnostics(string root,int capacity,int fileLimit,Func<CancellationToken,Task>? beforeWriter)
    {
        if(capacity is <1 or >4096 || fileLimit is <256 or >4*1024*1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        _folder=Path.Combine(root,"diagnostics"); _fileLimit=fileLimit;
        _beforeWriter=beforeWriter;
        _events=Channel.CreateBounded<ManagerDiagnosticEvent>(new BoundedChannelOptions(capacity)
            { SingleReader=true,FullMode=BoundedChannelFullMode.Wait,AllowSynchronousContinuations=false });
        _writer=Task.Run(()=>WriteAsync(_stop.Token));
    }
    /// <summary>Records without waiting for disk. Valid calls after disposal are ignored; invalid kinds are rejected.</summary>
    public void Record(ManagerEventKind kind,Type? errorType=null)=>TryRecord(kind,errorType);
    /// <summary>Same as <see cref="Record"/>, but reports whether this log took the event (false once disposal has begun).</summary>
    public bool TryRecord(ManagerEventKind kind,Type? errorType=null)
    {
        if(!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return Append(kind,errorType,null);
    }
    /// <summary>Records an <see cref="ManagerEventKind.OperationFailed"/> event that names the subsystem that failed.</summary>
    public void RecordFailure(ManagerSubsystem subsystem,Type? errorType=null)
    {
        if(!Enum.IsDefined(subsystem)) throw new ArgumentOutOfRangeException(nameof(subsystem));
        Append(ManagerEventKind.OperationFailed,errorType,subsystem);
    }
    private bool Append(ManagerEventKind kind,Type? errorType,ManagerSubsystem? subsystem)
    {
        string? safeError=SafeErrorType(errorType);
        lock(_gate)
        {
            if(_disposed || _sequence==long.MaxValue) return false;
            var entry=new ManagerDiagnosticEvent(++_sequence,DateTimeOffset.UtcNow,kind,safeError,Subsystem:subsystem);
            _journal[_nextJournal]=entry;_nextJournal=(_nextJournal+1)%JournalCapacity;
            if(_journalCount<JournalCapacity) _journalCount++;
            switch(entry.Level)
            {
                case DiagnosticLevel.Info:_infoCount++;break;
                case DiagnosticLevel.Warning:_warningCount++;break;
                case DiagnosticLevel.Error:_errorCount++;break;
            }
            if(!_events.Writer.TryWrite(entry)) _dropped++;
            return true;
        }
    }
    public ManagerDiagnosticSnapshot GetSnapshot()
    {
        lock(_gate)
        {
            var entries=new ManagerDiagnosticEvent[_journalCount];
            int first=(_nextJournal-_journalCount+JournalCapacity)%JournalCapacity;
            for(int index=0;index<entries.Length;index++) entries[index]=_journal[(first+index)%JournalCapacity]!;
            return new(_sequence,_sequence,_infoCount,_warningCount,_errorCount,_dropped,_failure,Array.AsReadOnly(entries));
        }
    }
    /// <summary>
    /// True when a snapshot taken now would differ from <paramref name="prior"/>, without copying the ring.
    /// One lock covers all three values: the writer changes Failure and DroppedEvents without a new sequence.
    /// </summary>
    public bool HasChangedSince(ManagerDiagnosticSnapshot? prior)
    {
        lock(_gate)
            return prior is null || prior.LatestSequence!=_sequence || prior.DroppedEvents!=_dropped
                || !string.Equals(prior.Failure,_failure,StringComparison.Ordinal);
    }
    // Fixed texts by cause (never the exception message). Only an access denial points at permissions:
    // a log held by a tailer, backup or scanner, or a full disk, would otherwise send the user to the wrong fix.
    private const string AccessDeniedText="Diagnostics could not be written. Check the data-folder permissions.";
    private const string LogInUseText="Diagnostics could not be written: another program is using the log file. Zenith retries on the next event.";
    private const string DiskFullText="Diagnostics could not be written: the disk is full. Zenith retries on the next event.";
    private const string WriteFailedText="Diagnostics could not be written to the data folder. Zenith retries on the next event.";
    internal static string WriteFailureText(Exception error)
    {
        if(error is UnauthorizedAccessException) return AccessDeniedText;
        // Win32 I/O errors surface as HRESULT_FROM_WIN32(code): facility 7 with the code in the low word.
        if((error.HResult & unchecked((int)0xFFFF0000))!=unchecked((int)0x80070000)) return WriteFailedText;
        return (error.HResult & 0xFFFF) switch
        {
            32 or 33 => LogInUseText,   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            112 or 39 => DiskFullText,  // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
            _ => WriteFailedText,
        };
    }
    private static bool IsWriteFailureText(string? text)=>text is AccessDeniedText or LogInUseText or DiskFullText or WriteFailedText;
    // One writer for the whole session: a disk error costs only the entry being written, never the writer.
    // A deleted folder, a log held open by another program or a full disk recovers on the next event.
    private async Task WriteAsync(CancellationToken ct)
    {
        ManagerDiagnosticEvent? inFlight=null;
        try
        {
            if(_beforeWriter is not null) await _beforeWriter(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await foreach(var entry in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                inFlight=entry;
                await TryWriteEntryAsync(new(entry.Utc,entry.Kind.ToString(),entry.ErrorType,entry.Count),countsAsDropped:true,ct).ConfigureAwait(false);
                inFlight=null;
            }
            long dropped=DroppedEvents;
            if(dropped>0) await TryWriteEntryAsync(new(DateTimeOffset.UtcNow,ManagerEventKind.DroppedEvents.ToString(),Count:dropped),countsAsDropped:false,ct).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {lock(_gate) _failure="Diagnostics shutdown timed out; some events were not confirmed written.";}
        finally
        {
            lock(_gate)
            {
                _events.Writer.TryComplete();
                if(inFlight is not null) _dropped++;
                while(_events.Reader.TryRead(out _)) _dropped++;
            }
        }
    }
    // An I/O failure is counted here exactly once (the caller then clears its in-flight entry); cancellation propagates.
    private async Task TryWriteEntryAsync(Entry entry,bool countsAsDropped,CancellationToken ct)
    {
        try
        {
            await WriteEntryAsync(entry,ct).ConfigureAwait(false);
            lock(_gate) if(IsWriteFailureText(_failure)) _failure=null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            lock(_gate)
            {
                _failure=WriteFailureText(ex);
                if(countsAsDropped) _dropped++;
            }
        }
    }
    private async Task WriteEntryAsync(Entry entry,CancellationToken ct)
    {
        // Explicitly preserve the established four-property disk format; journal UI fields (Sequence, Subsystem) stay in memory.
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(entry);
        // Recreated per entry so a folder deleted while the app runs (e.g. after "Open logs") is restored; no-op when present.
        Directory.CreateDirectory(_folder);
        string current=Path.Combine(_folder,"manager.jsonl");
        if(File.Exists(current) && new FileInfo(current).Length+bytes.Length+1>_fileLimit)
        {
            string older=Path.Combine(_folder,"manager.1.jsonl"),oldest=Path.Combine(_folder,"manager.2.jsonl");
            if(File.Exists(older)) File.Move(older,oldest,true);
            File.Move(current,older,true);
        }
        await using var file=new FileStream(current,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.Asynchronous);
        await file.WriteAsync(bytes,ct).ConfigureAwait(false); await file.WriteAsync("\n"u8.ToArray(),ct).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            if(_dispose is not null) return new(_dispose);
            _disposed=true;_events.Writer.TryComplete();
            _dispose=StopAsync();return new(_dispose);
        }
    }
    private async Task StopAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try {await _writer.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
        catch(TimeoutException) {_stop.Cancel();await _writer.ConfigureAwait(false);}
        finally {_stop.Dispose();}
    }
    private static string? SafeErrorType(Type? type)
    {
        if(type is null) return null;
        if(typeof(OperationCanceledException).IsAssignableFrom(type)) return "OperationCanceledException";
        if(typeof(UnauthorizedAccessException).IsAssignableFrom(type)) return "UnauthorizedAccessException";
        if(typeof(System.Security.Cryptography.CryptographicException).IsAssignableFrom(type)) return "CryptographicException";
        if(typeof(JsonException).IsAssignableFrom(type)) return "JsonException";
        if(typeof(InvalidDataException).IsAssignableFrom(type)) return "InvalidDataException";
        if(typeof(IOException).IsAssignableFrom(type)) return "IOException";
        if(typeof(ArgumentOutOfRangeException).IsAssignableFrom(type)) return "ArgumentOutOfRangeException";
        if(typeof(ArgumentNullException).IsAssignableFrom(type)) return "ArgumentNullException";
        if(typeof(ArgumentException).IsAssignableFrom(type)) return "ArgumentException";
        if(typeof(TimeoutException).IsAssignableFrom(type)) return "TimeoutException";
        if(typeof(ObjectDisposedException).IsAssignableFrom(type)) return "ObjectDisposedException";
        if(typeof(InvalidOperationException).IsAssignableFrom(type)) return "InvalidOperationException";
        if(typeof(NotSupportedException).IsAssignableFrom(type)) return "NotSupportedException";
        if(typeof(System.ComponentModel.Win32Exception).IsAssignableFrom(type)) return "Win32Exception";
        if(typeof(System.Data.Common.DbException).IsAssignableFrom(type)) return "DatabaseException";
        return typeof(Exception).IsAssignableFrom(type)?"Exception":"NonExceptionType";
    }
}
