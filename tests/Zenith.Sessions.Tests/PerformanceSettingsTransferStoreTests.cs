using System.IO;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class PerformanceSettingsTransferStoreTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-performance-transfer-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");
    private string App=>Path.Combine(_root,"app");
    private string Share=>Path.Combine(_root,"share");
    private PerformanceSettingsTransferStore Store()
    {Directory.CreateDirectory(Data);Directory.CreateDirectory(App);Directory.CreateDirectory(Share);return new(Data,App);}
    [Fact]
    public async Task ExplicitFileRoundTripAndReplacementOnlyTouchTheSelectedExport()
    {
        var store=Store();string destination=Path.Combine(Share,"performance.json"),runtime=Path.Combine(Data,"settings.json");
        const string originalRuntime="synthetic local-only settings";await File.WriteAllTextAsync(runtime,originalRuntime);
        var original=new PortablePerformanceSettings(ManagerPerformanceProfile.MainAccountPriority,2000,750,9000,40000);
        await store.ExportAsync(destination,original,default);Assert.Equal(original,await store.ImportAsync(destination,default));
        var replacement=new PortablePerformanceSettings(ManagerPerformanceProfile.Custom,500,250,1000,20000);
        await store.ExportAsync(destination,replacement,default);Assert.Equal(replacement,await store.ImportAsync(destination,default));
        Assert.Equal(originalRuntime,await File.ReadAllTextAsync(runtime));Assert.Equal(new[] {destination},Directory.GetFiles(Share));
        Assert.Empty(Directory.GetFiles(App));Assert.Equal(new[] {runtime},Directory.GetFiles(Data));
    }
    [Fact]
    public async Task ImportAndExportCannotReadOrOverwriteRuntimeOrApplicationStorage()
    {
        var store=Store();
        foreach(string protectedFile in new[] {Path.Combine(Data,"settings.json"),Path.Combine(App,"performance.json")})
        {
            const string original="synthetic credential-marker must remain local";await File.WriteAllTextAsync(protectedFile,original);
            await Assert.ThrowsAsync<ArgumentException>(()=>store.ExportAsync(protectedFile,new(),default));
            await Assert.ThrowsAsync<ArgumentException>(()=>store.ImportAsync(protectedFile,default));
            await Assert.ThrowsAsync<ArgumentException>(()=>store.ExportAsync("\\\\?\\"+protectedFile,new(),default));
            Assert.Equal(original,await File.ReadAllTextAsync(protectedFile));
        }
        foreach(string bad in new[] {"relative.json",Path.Combine(Share,"profile.txt"),Path.Combine(Share,"profile.json:extra"),Path.Combine(Share,"profile:extra.json")})
        {
            await Assert.ThrowsAsync<ArgumentException>(()=>store.ExportAsync(bad,new(),default));
            await Assert.ThrowsAsync<ArgumentException>(()=>store.ImportAsync(bad,default));
        }
        Assert.Empty(Directory.GetFiles(Share));
    }
    [Fact]
    public async Task InvalidAndOversizedImportsPreserveBothSourceAndCurrentSettings()
    {
        var store=Store();string source=Path.Combine(Share,"incoming.json"),runtime=Path.Combine(Data,"settings.json");
        const string current="synthetic saved settings and main-account marker";await File.WriteAllTextAsync(runtime,current);
        var local=new ManagerPerformanceSettings(MainAccountProfileId:Guid.NewGuid());var before=local;
        foreach(byte[] payload in new[] {Encoding.UTF8.GetBytes("{\"credential\":\"secret-marker\"}"),new byte[] {0xff,0xfe},
            new byte[PerformanceSettingsTransfer.MaximumJsonBytes+1],Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(PerformanceSettingsTransfer.SerializeJson(new())).Replace("\"version\":1","\"version\":2",StringComparison.Ordinal))})
        {
            await File.WriteAllBytesAsync(source,payload);
            var error=await Assert.ThrowsAsync<PerformanceSettingsTransferException>(()=>store.ImportAsync(source,default));Assert.DoesNotContain("secret-marker",error.ToString());
            Assert.Equal(payload,await File.ReadAllBytesAsync(source));Assert.Equal(current,await File.ReadAllTextAsync(runtime));Assert.Same(before,local);
        }
        Assert.Equal(new[] {source},Directory.GetFiles(Share));
    }
    [Fact]
    public async Task InvalidExportValuesDoNotTruncateAnExistingSelectedFile()
    {
        var store=Store();string destination=Path.Combine(Share,"existing.json");await store.ExportAsync(destination,new(),default);byte[] original=await File.ReadAllBytesAsync(destination);
        await Assert.ThrowsAsync<PerformanceSettingsTransferException>(()=>store.ExportAsync(destination,new(CustomProcessSampleMs:1),default));
        Assert.Equal(original,await File.ReadAllBytesAsync(destination));Assert.Equal(new[] {destination},Directory.GetFiles(Share));
    }
    [Fact]
    public async Task PreCancelledTransfersPerformNoFileChangesOrDefaultImport()
    {
        var store=Store();string file=Path.Combine(Share,"performance.json");await store.ExportAsync(file,new(),default);byte[] original=await File.ReadAllBytesAsync(file);
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ExportAsync(file,new(ManagerPerformanceProfile.HighPerformance),stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ImportAsync(file,stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ExportAsync(Path.Combine(Share,"must-not-exist.json"),new(),stop.Token));
        Assert.Equal(original,await File.ReadAllBytesAsync(file));Assert.Equal(new[] {file},Directory.GetFiles(Share));
    }
    [Fact]
    public async Task CancellationDuringABlockedReplacePreservesThePreviousExportAndCleansStaging()
    {
        var store=Store();string destination=Path.Combine(Share,"performance.json");await store.ExportAsync(destination,new(),default);byte[] original=await File.ReadAllBytesAsync(destination);
        await using(var held=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(70));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ExportAsync(destination,new(ManagerPerformanceProfile.HighPerformance),stop.Token));
        }
        Assert.Equal(original,await File.ReadAllBytesAsync(destination));Assert.Equal(new[] {destination},Directory.GetFiles(Share));
        await store.ExportAsync(destination,new(ManagerPerformanceProfile.HighPerformance),default);
        Assert.Equal(ManagerPerformanceProfile.HighPerformance,(await store.ImportAsync(destination,default)).Profile);
    }
    [Fact]
    public async Task ATransferDoesNotCreateMissingDirectoriesOrRepairMissingFilesWithDefaults()
    {
        var store=Store();string directory=Path.Combine(Share,"missing"),file=Path.Combine(directory,"performance.json");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(()=>store.ImportAsync(file,default));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(()=>store.ExportAsync(file,new(),default));
        Assert.False(Directory.Exists(directory));Assert.Empty(Directory.GetFiles(Share));
        await Assert.ThrowsAsync<FileNotFoundException>(()=>store.ImportAsync(Path.Combine(Share,"missing.json"),default));
    }
    public void Dispose()
    {
        string resolved=Path.GetFullPath(_root),temporary=Path.GetFullPath(Path.GetTempPath());
        if(Path.GetDirectoryName(resolved)!=temporary.TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(resolved).StartsWith("zenith-performance-transfer-",StringComparison.Ordinal))
            throw new InvalidOperationException("The owned transfer fixture path is invalid.");
        if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
