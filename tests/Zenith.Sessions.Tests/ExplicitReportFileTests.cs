using System.IO;
using System.Text;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ExplicitReportFileTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-report-file-tests-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");
    private string App=>Path.Combine(_root,"app");
    private string Share=>Path.Combine(_root,"share");
    private static readonly byte[] NewReport=Encoding.UTF8.GetBytes("{\"Synthetic\":\"new report\"}");
    public ExplicitReportFileTests() {Directory.CreateDirectory(Data);Directory.CreateDirectory(App);Directory.CreateDirectory(Share);}

    [Fact]
    public async Task ReplaceRetriesWhileAPriorReportIsBrieflyHeld()
    {
        string report=Path.Combine(Share,"report.json");await File.WriteAllTextAsync(report,"old report");
        var held=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            Task write=ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,default);
            await Task.Delay(60);await held.DisposeAsync();await write;
        }
        finally {await held.DisposeAsync();}
        Assert.Equal(NewReport,await File.ReadAllBytesAsync(report));Assert.Empty(Directory.GetFiles(Share,"*.tmp"));
    }

    [Fact]
    public async Task AFailureInTheChosenFolderNamesThatFolderWithoutItsPathAndKeepsTheOldReport()
    {
        string report=Path.Combine(Share,"report.json");await File.WriteAllTextAsync(report,"old report");File.SetAttributes(report,FileAttributes.ReadOnly);
        ReportFileException error;
        try {error=await Assert.ThrowsAsync<ReportFileException>(()=>ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,default));}
        finally {File.SetAttributes(report,FileAttributes.Normal);}
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        Assert.Contains("chosen folder",error.Message);Assert.DoesNotContain(Share,error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old report",await File.ReadAllTextAsync(report));Assert.Equal(new[] {report},Directory.GetFiles(Share));
    }

    [Fact]
    public async Task AMissingChosenFolderIsNamedAsUnavailableAndIsNotCreated()
    {
        string folder=Path.Combine(Share,"removed");
        var error=await Assert.ThrowsAsync<ReportFileException>(()=>ExplicitReportFile.WriteReportAsync(Path.Combine(folder,"report.json"),NewReport,Data,App,default));
        Assert.IsType<DirectoryNotFoundException>(error.InnerException);
        Assert.Contains("chosen folder is no longer available",error.Message);Assert.DoesNotContain(folder,error.Message,StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task PathRulesAndCancellationAreNotRewordedAsFolderFailures()
    {
        string settings=Path.Combine(Data,"settings.json");await File.WriteAllTextAsync(settings,"synthetic settings");
        await Assert.ThrowsAsync<ArgumentException>(()=>ExplicitReportFile.WriteReportAsync(settings,NewReport,Data,App,default));
        Assert.Equal("synthetic settings",await File.ReadAllTextAsync(settings));
        string report=Path.Combine(Share,"report.json");await File.WriteAllTextAsync(report,"old report");
        using(var stopped=new CancellationTokenSource())
        {
            stopped.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,stopped.Token));
        }
        // Panic, Lock and shutdown cancel a write that is waiting out a held report; that must stay a cancellation.
        await using(var held=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(70));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ExplicitReportFile.WriteReportAsync(report,NewReport,Data,App,stop.Token));
        }
        Assert.Equal("old report",await File.ReadAllTextAsync(report));Assert.Equal(new[] {report},Directory.GetFiles(Share));
    }

    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-report-file-tests-",StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
