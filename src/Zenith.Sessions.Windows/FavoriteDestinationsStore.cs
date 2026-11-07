using System.IO;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Bounded metadata-only storage. The caller supplies the runtime data directory, outside the application package.</summary>
public sealed class FavoriteDestinationsStore : IFavoriteDestinationStore,IDisposable
{
    public const int MaximumFileBytes=512*1024;
    private readonly SemaphoreSlim _writer=new(1,1);
    private readonly string _root;
    private int _disposed;
    public string Path {get;}
    private sealed record Document(int Version,long Revision,StoredFavorite[] Items);
    private sealed record StoredFavorite(Guid Id,string Name,long PlaceId,string? JobId,long Revision,Guid ChangeId);
    private sealed record Loaded(Document Document,bool MainValid,bool MainPresent,string? Notice);

    public FavoriteDestinationsStore(string root)
    {
        if(string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The favorites data directory is required.",nameof(root));
        _root=System.IO.Path.GetFullPath(root);Path=System.IO.Path.Combine(_root,"favorite-destinations.json");
    }
    public Task<FavoriteDestinationSnapshot> LoadAsync(CancellationToken ct)=>SerializedAsync(async token=>
    {
        var loaded=await LoadDocumentAsync(token).ConfigureAwait(false);
        return new FavoriteDestinationSnapshot(loaded.Document.Revision,
            Array.AsReadOnly(loaded.Document.Items.Select(ToFavorite).ToArray()),loaded.Notice);
    },ct);

    public Task<FavoriteDestination> SaveAsync(FavoriteDestination destination,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);destination=destination.Validated();
        return SerializedAsync(async token=>
        {
            var loaded=await LoadDocumentAsync(token).ConfigureAwait(false);var document=loaded.Document;
            int index=Array.FindIndex(document.Items,item=>item.Id==destination.Id);
            if(index<0 && destination.Revision!=0 || index>=0 && (document.Items[index].Revision!=destination.Revision || document.Items[index].ChangeId!=destination.ChangeId))
                throw new FavoriteDestinationConflictException();
            // A rename meets the whole shared name rule; an unchanged saved name stays as it was saved.
            if(index>=0 && !string.Equals(document.Items[index].Name,destination.Name,StringComparison.Ordinal)) FavoriteDestination.ValidateName(destination.Name);
            if(index<0 && document.Items.Length>=FavoriteDestination.MaximumCount) throw new FavoriteDestinationCapacityException();
            if(destination.Revision==long.MaxValue || document.Revision==long.MaxValue)
                throw Damaged("Favorites revisions cannot advance. Existing files were preserved; review the favorites storage.");
            var saved=destination with {Revision=destination.Revision+1,ChangeId=Guid.NewGuid()};
            var items=new StoredFavorite[document.Items.Length+(index<0?1:0)];document.Items.CopyTo(items,0);
            items[index<0?items.Length-1:index]=ToStored(saved);
            await WriteDocumentAsync(new(1,document.Revision+1,items),loaded,token).ConfigureAwait(false);
            return saved;
        },ct);
    }
    public async Task DeleteAsync(Guid id,long expectedRevision,Guid expectedChangeId,CancellationToken ct)
    {
        if(id==Guid.Empty || expectedRevision<1 || expectedChangeId==Guid.Empty) throw new ArgumentException("A saved favorite identity and revision are required.");
        await SerializedAsync(async token=>
        {
            var loaded=await LoadDocumentAsync(token).ConfigureAwait(false);var document=loaded.Document;
            int index=Array.FindIndex(document.Items,item=>item.Id==id);
            if(index<0 || document.Items[index].Revision!=expectedRevision || document.Items[index].ChangeId!=expectedChangeId) throw new FavoriteDestinationConflictException();
            if(document.Revision==long.MaxValue) throw Damaged("Favorites revisions cannot advance. Existing files were preserved; review the favorites storage.");
            var items=new StoredFavorite[document.Items.Length-1];
            Array.Copy(document.Items,0,items,0,index);Array.Copy(document.Items,index+1,items,index,items.Length-index);
            await WriteDocumentAsync(new(1,document.Revision+1,items),loaded,token).ConfigureAwait(false);
            return true;
        },ct).ConfigureAwait(false);
    }

    private async Task<T> SerializedAsync<T>(Func<CancellationToken,Task<T>> operation,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Directory/replace/durable flush calls are blocking platform operations, bounded to this one owned request.
            return await Task.Run(async ()=>
            {
                ct.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
                Directory.CreateDirectory(_root);
                await using var ownership=await AcquireFileLockAsync(ct).ConfigureAwait(false);
                return await operation(ct).ConfigureAwait(false);
            },CancellationToken.None).ConfigureAwait(false);
        }
        finally {_writer.Release();}
    }
    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        for(int attempt=0;;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try {return new FileStream(Path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.Asynchronous);}
            catch(IOException ex) when(((ex.HResult&0xffff) is 32 or 33) && attempt<99)
            {await Task.Delay(TimeSpan.FromMilliseconds(20),ct).ConfigureAwait(false);}
        }
    }
    private async Task<Loaded> LoadDocumentAsync(CancellationToken ct)
    {
        bool mainPresent=Exists(Path);
        if(mainPresent)
        {
            try
            {
                var document=await ReadDocumentAsync(Path,ct).ConfigureAwait(false);
                return new(document,true,true,Exists(Path+".tmp")?"An unfinished favorites write was ignored. The committed favorites were loaded; no temporary data was applied.":null);
            }
            catch(FavoriteDestinationStorageException ex) when(ex.Problem==FavoriteDestinationStorageProblem.Damaged)
            {
                if(!Exists(Path+".bak")) throw Damaged("Favorites are damaged and no valid backup is available. Existing files were preserved; review the favorites storage.");
            }
        }
        if(Exists(Path+".bak"))
        {
            var backup=await ReadDocumentAsync(Path+".bak",ct).ConfigureAwait(false);
            return new(backup,false,mainPresent,"Loaded the last valid favorites copy. The original files were preserved; save a deliberate change to repair the main file.");
        }
        if(Exists(Path+".damaged") || Exists(Path+".tmp"))
            throw Damaged("Favorites have an interrupted or damaged storage file. Existing files were preserved; review recovery before creating new favorites.");
        var initial=new Document(1,1,[]);
        await WriteDocumentAsync(initial,new(initial,false,false,null),ct).ConfigureAwait(false);
        return new(initial,true,true,null);
    }
    private static async Task<Document> ReadDocumentAsync(string path,CancellationToken ct)
    {
        byte[] bytes=await ReadBytesAsync(path,ct).ConfigureAwait(false);
        PreflightVersion(bytes,ct);
        try
        {
            using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=8});var root=json.RootElement;
            int version=root.GetProperty("Version").GetInt32();
            if(version!=1) throw new FavoriteDestinationStorageException(FavoriteDestinationStorageProblem.UnsupportedVersion,
                "The favorites file has an unsupported version. Existing files were preserved; use a compatible Zenith Sessions version.");
            RequireProperties(root,["Version","Revision","Items"]);
            long revision=root.GetProperty("Revision").GetInt64();var data=root.GetProperty("Items");
            if(revision<1 || data.ValueKind!=JsonValueKind.Array || data.GetArrayLength()>FavoriteDestination.MaximumCount) throw new InvalidDataException();
            var items=new StoredFavorite[data.GetArrayLength()];var ids=new HashSet<Guid>();int index=0;
            foreach(var entry in data.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();RequireProperties(entry,["Id","Name","PlaceId","JobId","Revision","ChangeId"]);
                var favorite=new FavoriteDestination(entry.GetProperty("Id").GetGuid(),entry.GetProperty("Name").GetString()!,
                    entry.GetProperty("PlaceId").GetInt64(),entry.GetProperty("JobId").GetString(),entry.GetProperty("Revision").GetInt64(),entry.GetProperty("ChangeId").GetGuid()).Validated();
                if(favorite.Revision<1 || favorite.Revision>revision || !ids.Add(favorite.Id)) throw new InvalidDataException();
                items[index++]=ToStored(favorite);
            }
            return new(1,revision,items);
        }
        catch(Exception ex) when(ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        {throw Damaged("Favorites contain an invalid or unreadable record. Existing files were preserved; review the favorites storage.");}
    }
    private static bool PreflightVersion(ReadOnlySpan<byte> bytes,CancellationToken ct,bool allowCurrentCorruption=false)
    {
        // Inspect every exact root Version before ordinary corruption can choose a backup.
        // Stop on an observed incompatible/ambiguous version even if later JSON is damaged.
        // Unknown formats may put their version after deeper data. The iterative scan's depth
        // stays bounded by the already bounded file; current-format decoding still uses eight.
        var reader=new Utf8JsonReader(bytes,new JsonReaderOptions {MaxDepth=MaximumFileBytes});int versions=0;bool current=false;
        try
        {
            while(reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if(reader.TokenType!=JsonTokenType.PropertyName || reader.CurrentDepth!=1 || !reader.ValueTextEquals("Version"u8)) continue;
                if(++versions>1) throw Unsupported();
                if(!reader.Read()) break;
                if(reader.TokenType==JsonTokenType.Number)
                {
                    if(!reader.TryGetInt32(out int version) || version!=1) throw Unsupported();
                    current=true;
                }
            }
        }
        catch(JsonException)
        {
            if(allowCurrentCorruption && current) return true;
            throw Damaged("Favorites contain an invalid or unreadable record. Existing files were preserved; review the favorites storage.");
        }
        return current;
        static FavoriteDestinationStorageException Unsupported()=>new(FavoriteDestinationStorageProblem.UnsupportedVersion,
            "The favorites file has an unsupported or ambiguous version. Existing files were preserved; use a compatible Zenith Sessions version.");
    }
    private static void RequireProperties(JsonElement element,string[] expected)
    {
        if(element.ValueKind!=JsonValueKind.Object) throw new InvalidDataException();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in element.EnumerateObject())
            if(!expected.Contains(property.Name,StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException();
        if(seen.Count!=expected.Length) throw new InvalidDataException();
    }
    private static async Task<byte[]> ReadBytesAsync(string path,CancellationToken ct)
    {
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,8192,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(file.Length>MaximumFileBytes) throw Damaged("Favorites exceed the bounded storage size. Existing files were preserved; review the favorites storage.");
        var bytes=new byte[(int)file.Length];
        try {await file.ReadExactlyAsync(bytes,ct).ConfigureAwait(false);}
        catch(EndOfStreamException) {throw Damaged("Favorites were truncated while reading. Existing files were preserved; retry recovery.");}
        return bytes;
    }
    private async Task WriteDocumentAsync(Document document,Loaded previous,CancellationToken ct)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(document);
        if(bytes.Length>MaximumFileBytes) throw Damaged("Favorites exceed the bounded storage size. Existing files were preserved.");
        // A valid primary is the load authority, but replacing its backup must not destroy
        // an unsupported generation that Load never needed to inspect.
        if(Exists(Path+".bak"))
        {
            byte[] backup=await ReadBytesAsync(Path+".bak",ct).ConfigureAwait(false);
            if(!PreflightVersion(backup,ct,allowCurrentCorruption:previous.MainValid))
                throw Damaged("The favorites backup's storage version could not be confirmed. Existing files were preserved; review the backup before saving.");
        }
        if(previous.MainPresent && !previous.MainValid) await PreserveDamagedAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();string temporary=Path+".tmp";
        // One fixed staging file bounds interrupted-write storage. It is never used as committed data.
        await using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
        {
            await file.WriteAsync(bytes,ct).ConfigureAwait(false);await file.FlushAsync(ct).ConfigureAwait(false);file.Flush(true);
        }
        ct.ThrowIfCancellationRequested();
        if(previous.MainPresent)
        {
            for(int attempt=0;;attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try {File.Replace(temporary,Path,previous.MainValid?Path+".bak":null);break;}
                catch(IOException ex) when(((ex.HResult&0xffff) is 32 or 33 or 1175) && attempt<9 && Exists(temporary) && Exists(Path))
                {await Task.Delay(TimeSpan.FromMilliseconds(25),ct).ConfigureAwait(false);}
            }
        }
        else File.Move(temporary,Path);
    }
    private async Task PreserveDamagedAsync(CancellationToken ct)
    {
        byte[] original;
        try {original=await ReadBytesAsync(Path,ct).ConfigureAwait(false);}
        catch(FavoriteDestinationStorageException)
        {throw new FavoriteDestinationStorageException(FavoriteDestinationStorageProblem.RecoveryBlocked,"The damaged favorites file exceeds the recovery-copy bound. Preserve it outside the data folder before repairing favorites.");}
        string damaged=Path+".damaged";
        if(Exists(damaged))
        {
            byte[] previous;
            try {previous=await ReadBytesAsync(damaged,ct).ConfigureAwait(false);}
            catch(FavoriteDestinationStorageException)
            {throw new FavoriteDestinationStorageException(FavoriteDestinationStorageProblem.RecoveryBlocked,"The damaged-original recovery slot is occupied. Preserve that file outside the data folder before another repair.");}
            if(!original.AsSpan().SequenceEqual(previous))
                throw new FavoriteDestinationStorageException(FavoriteDestinationStorageProblem.RecoveryBlocked,"The damaged-original recovery slot is occupied. Preserve that file outside the data folder before another repair.");
            return;
        }
        await using var copy=new FileStream(damaged,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough);
        await copy.WriteAsync(original,ct).ConfigureAwait(false);await copy.FlushAsync(ct).ConfigureAwait(false);copy.Flush(true);
    }
    private static bool Exists(string path)
    {
        try {File.GetAttributes(path);return true;}
        catch(FileNotFoundException) {return false;}
        catch(DirectoryNotFoundException) {return false;}
    }
    private static FavoriteDestination ToFavorite(StoredFavorite item)=>new(item.Id,item.Name,item.PlaceId,item.JobId,item.Revision,item.ChangeId);
    private static StoredFavorite ToStored(FavoriteDestination item)=>new(item.Id,item.Name,item.PlaceId,item.JobId,item.Revision,item.ChangeId);
    private static FavoriteDestinationStorageException Damaged(string message)=>new(FavoriteDestinationStorageProblem.Damaged,message);
    /// <summary>Dispose after the caller has awaited all operations; this store owns no persistent worker.</summary>
    public void Dispose() {if(Interlocked.Exchange(ref _disposed,1)==0) _writer.Dispose();}
}
