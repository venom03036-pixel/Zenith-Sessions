using System.Text.Json;
using System.Threading.Channels;

namespace Zenith.Sessions.Windows;

public enum ManagerEventKind { Started,ProfileSaved,ProfilesRemoved,ProfilesImported,MetadataExported,SettingsSaved,AuthorizationLinked,
    AuthorizationDisconnected,OperationFailed,Panic,ControlsLocked,Shutdown,DroppedEvents }

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
    public void Record(ManagerEventKind kind,Type? errorType=null)
    {
        if(!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        string? safeError=SafeErrorType(errorType);
        lock(_gate)
        {
            if(_disposed || _sequence==long.MaxValue) return;
            var entry=new ManagerDiagnosticEvent(++_sequence,DateTimeOffset.UtcNow,kind,safeError);
            _journal[_nextJournal]=entry;_nextJournal=(_nextJournal+1)%JournalCapacity;
            if(_journalCount<JournalCapacity) _journalCount++;
            switch(entry.Level)
            {
                case DiagnosticLevel.Info:_infoCount++;break;
                case DiagnosticLevel.Warning:_warningCount++;break;
                case DiagnosticLevel.Error:_errorCount++;break;
            }
            if(!_events.Writer.TryWrite(entry)) _dropped++;
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
    private async Task WriteAsync(CancellationToken ct)
    {
        ManagerDiagnosticEvent? inFlight=null;
        try
        {
            if(_beforeWriter is not null) await _beforeWriter(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_folder);
            await foreach(var entry in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                inFlight=entry;
                await WriteEntryAsync(new(entry.Utc,entry.Kind.ToString(),entry.ErrorType,entry.Count),ct).ConfigureAwait(false);
                inFlight=null;
            }
            long dropped=DroppedEvents;
            if(dropped>0) await WriteEntryAsync(new(DateTimeOffset.UtcNow,ManagerEventKind.DroppedEvents.ToString(),Count:dropped),ct).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {lock(_gate) _failure="Diagnostics shutdown timed out; some events were not confirmed written.";}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {lock(_gate) _failure="Diagnostics could not be written. Check the data-folder permissions.";}
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
    private async Task WriteEntryAsync(Entry entry,CancellationToken ct)
    {
        // Explicitly preserve the established four-property disk format; journal UI fields stay in memory.
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(entry);
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
