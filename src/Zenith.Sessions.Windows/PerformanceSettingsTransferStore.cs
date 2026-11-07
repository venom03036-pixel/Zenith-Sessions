using System.IO;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Explicit file transfers only. Every returned task owns its temporary streams; no settings apply, worker or persistent resource is retained.</summary>
public sealed class PerformanceSettingsTransferStore : IPerformanceSettingsTransferStore
{
    private readonly string _dataRoot,_applicationRoot;
    public PerformanceSettingsTransferStore(string dataRoot,string? applicationRoot=null)
    {
        if(string.IsNullOrWhiteSpace(dataRoot) || applicationRoot is not null && string.IsNullOrWhiteSpace(applicationRoot))
            throw new ArgumentException("The runtime data and application directories are required.");
        _dataRoot=Path.GetFullPath(dataRoot);_applicationRoot=Path.GetFullPath(applicationRoot??AppContext.BaseDirectory);
    }
    public Task<PortablePerformanceSettings> ImportAsync(string path,CancellationToken ct)=>Task.Run(async ()=>
    {
        ct.ThrowIfCancellationRequested();string source=ValidatePath(path);ct.ThrowIfCancellationRequested();
        await using var file=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(file.Length>PerformanceSettingsTransfer.MaximumJsonBytes) throw new PerformanceSettingsTransferException(PerformanceSettingsTransferProblem.TooLarge);
        return await PerformanceSettingsTransfer.ReadJsonAsync(file,ct).ConfigureAwait(false);
    },CancellationToken.None);

    public Task ExportAsync(string path,PortablePerformanceSettings settings,CancellationToken ct)=>Task.Run(async ()=>
    {
        ct.ThrowIfCancellationRequested();byte[] bytes=PerformanceSettingsTransfer.SerializeJson(settings,ct);string destination=ValidatePath(path);
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes,ct).ConfigureAwait(false);await file.FlushAsync(ct).ConfigureAwait(false);file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();ValidatePath(destination);
            if(!File.Exists(destination)) {ct.ThrowIfCancellationRequested();File.Move(temporary,destination);return;}
            for(int attempt=0;;attempt++)
            {
                ct.ThrowIfCancellationRequested();ValidatePath(destination);
                try {File.Replace(temporary,destination,null);break;}
                catch(IOException ex) when(((ex.HResult&0xffff) is 32 or 33 or 1175) && attempt<9 && File.Exists(temporary) && File.Exists(destination))
                {await Task.Delay(TimeSpan.FromMilliseconds(25),ct).ConfigureAwait(false);}
            }
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    },CancellationToken.None);
    private string ValidatePath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a performance transfer file.");
        try {return DiagnosticExportPath.Validate(path,_dataRoot,_applicationRoot);}
        catch(ArgumentException)
        {throw new ArgumentException("Choose a normal .json file outside the app and its data folder, without filesystem links.");}
    }
}
