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
        ct.ThrowIfCancellationRequested();byte[] bytes=PerformanceSettingsTransfer.SerializeJson(settings,ct);
        await ExplicitReportFile.WriteAsync(path,bytes,ValidatePath,ct).ConfigureAwait(false);
    },CancellationToken.None);
    private string ValidatePath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a performance transfer file.");
        try {return DiagnosticExportPath.Validate(path,_dataRoot,_applicationRoot);}
        catch(ArgumentException)
        {throw new ArgumentException("Choose a normal .json file outside the app and its data folder, without filesystem links.");}
    }
}
